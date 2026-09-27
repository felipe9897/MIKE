using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Mike.Common
{
    public class OutboxMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Subject { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int Retries { get; set; } = 0;
        public bool Processed { get; set; } = false;
    }

    public class OutboxManager
    {
        private readonly string _storagePath;
        private readonly ILogger _logger;
        private List<OutboxMessage> _messages = new();
        private readonly object _lock = new();

        public OutboxManager(ILogger logger)
        {
            _logger = logger;
            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MikeLocal", "outbox.json");
            LoadOutbox();
        }

        public void Enqueue(string subject, byte[] data)
        {
            lock (_lock)
            {
                _messages.Add(new OutboxMessage { Subject = subject, Data = data });
                SaveOutbox();
            }
        }

        public async Task ProcessOutboxAsync(Func<OutboxMessage, Task<bool>> processor)
        {
            List<OutboxMessage> toProcess;
            lock (_lock)
            {
                toProcess = _messages.Where(m => !m.Processed).ToList();
            }

            foreach (var msg in toProcess)
            {
                try
                {
                    bool success = await processor(msg);
                    if (success)
                    {
                        lock (_lock)
                        {
                            msg.Processed = true;
                            SaveOutbox();
                        }
                    }
                    else
                    {
                        lock (_lock)
                        {
                            msg.Retries++;
                            SaveOutbox();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Outbox processing error for {Id}: {Message}", msg.Id, ex.Message);
                }
            }
        }

        private void LoadOutbox()
        {
            if (!File.Exists(_storagePath)) return;
            try
            {
                var json = File.ReadAllText(_storagePath);
                _messages = JsonSerializer.Deserialize<List<OutboxMessage>>(json) ?? new List<OutboxMessage>();
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to load outbox: {Message}", ex.Message);
            }
        }

        private void SaveOutbox()
        {
            try
            {
                var json = JsonSerializer.Serialize(_messages);
                File.WriteAllText(_storagePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to save outbox: {Message}", ex.Message);
            }
        }
    }
}
