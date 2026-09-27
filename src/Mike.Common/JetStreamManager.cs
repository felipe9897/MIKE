using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Mike.Common
{
    public class JetStreamManager
    {
        private readonly ILogger _logger;
        private readonly NatsConnection _connection;
        private readonly NatsJSContext _js;
        private const string StreamName = "MIKE_MESH_TOOLS";

        public JetStreamManager(NatsConnection connection, ILogger logger)
        {
            _connection = connection;
            _logger = logger;
            _js = new NatsJSContext(_connection);
        }

        public async Task InitializeStreamAsync()
        {
            try
            {
                var streamConfig = new StreamConfig(StreamName, new[] { "mike.mesh.tool.*" })
                {
                    Storage = StreamConfigStorage.File,
                    Retention = StreamConfigRetention.Limits,
                    MaxAge = TimeSpan.FromDays(1),
                    MaxMsgs = 10000
                };

                await _js.CreateStreamAsync(streamConfig);
                _logger.LogInformation("JetStream {StreamName} initialized successfully.", StreamName);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to initialize JetStream: {Message}", ex.Message);
            }
        }

        public async Task PublishAsync<T>(string subject, T message)
        {
            try
            {
                var data = JsonSerializer.SerializeToUtf8Bytes(message);
                await _js.PublishAsync(subject, data);
            }
            catch (Exception ex)
            {
                _logger.LogError("JetStream Publish failed: {Message}", ex.Message);
                throw;
            }
        }

        public async Task SubscribeAsync(string subject, string durableName, Func<NatsJSMsg<byte[]>, Task<bool>> handler)
        {
            try
            {
                var consumerConfig = new ConsumerConfig(durableName)
                {
                    MaxDeliver = 5
                };

                var consumer = await _js.CreateOrUpdateConsumerAsync(StreamName, consumerConfig);

                _ = Task.Run(async () =>
                {
                    await foreach (var msg in consumer.ConsumeAsync<byte[]>())
                    {
                        bool success = await handler(msg);
                        if (success)
                        {
                            await msg.AckAsync();
                        }
                        else
                        {
                            await msg.NakAsync();
                            _logger.LogWarning("Message NACKed. Retrying delivery for {Subject}", subject);
                        }
                    }
                });

                _logger.LogInformation("JetStream durable consumer {DurableName} subscribed to {Subject}", durableName, subject);
            }
            catch (Exception ex)
            {
                _logger.LogError("JetStream Subscribe failed: {Message}", ex.Message);
            }
        }

        public async Task MoveToDlqAsync(NatsJSMsg<byte[]> msg, Exception ex)
        {
            try
            {
                var dlqSubject = $"mike.mesh.dlq.{msg.Subject}";
                _logger.LogError("Moving message to DLQ {Subject} due to: {Message}", dlqSubject, ex.Message);

                var dlqPacket = new {
                    OriginalMsg = msg.Data,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow
                };

                await _js.PublishAsync(dlqSubject, JsonSerializer.SerializeToUtf8Bytes(dlqPacket));
                await msg.AckAsync();
            }
            catch (Exception dlqEx)
            {
                _logger.LogCritical("Failed to move message to DLQ: {Message}", dlqEx.Message);
            }
        }
    }
}
