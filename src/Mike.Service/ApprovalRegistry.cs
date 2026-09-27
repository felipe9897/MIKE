using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Mike.Service
{
    public class ApprovalRequest
    {
        public string Id { get; set; } = string.Empty;
        public string ToolId { get; set; } = string.Empty;
        // Authorization uses a fixed-length fingerprint; raw or secret arguments are
        // deliberately not retained in the approval state machine.
        public byte[] ArgsFingerprint { get; set; } = Array.Empty<byte>();
        public DateTime Expiry { get; set; }
        private int _state;

        public bool IsApproved => Volatile.Read(ref _state) == 1;

        public bool TryApprove(DateTime now)
        {
            if (now >= Expiry) return false;
            return Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        }

        public bool TryConsume(DateTime now, string toolId, string args)
        {
            if (now >= Expiry || !string.Equals(ToolId, toolId, StringComparison.Ordinal)) return false;
            byte[] fingerprint = ComputeFingerprint(toolId, args);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(ArgsFingerprint, fingerprint)) return false;
                return Interlocked.CompareExchange(ref _state, 2, 1) == 1;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fingerprint);
            }
        }

        private static byte[] ComputeFingerprint(string toolId, string args)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes(toolId));
            hash.AppendData(new byte[] { 0 });
            hash.AppendData(Encoding.UTF8.GetBytes(args));
            return hash.GetHashAndReset();
        }

        public static byte[] Fingerprint(string toolId, string args) => ComputeFingerprint(toolId, args);
    }

    public class ApprovalRegistry
    {
        private readonly ConcurrentDictionary<string, ApprovalRequest> _pending = new ConcurrentDictionary<string, ApprovalRequest>();

        public string CreateRequest(string toolId, string args)
        {
            string id = Guid.NewGuid().ToString("N");
            var request = new ApprovalRequest
            {
                Id = id,
                ToolId = toolId,
                ArgsFingerprint = ApprovalRequest.Fingerprint(toolId, args),
                Expiry = DateTime.UtcNow.AddMinutes(5)
            };
            _pending[id] = request;
            return id;
        }

        public bool TryApprove(string id)
        {
            if (_pending.TryGetValue(id, out var request))
            {
                if (request.TryApprove(DateTime.UtcNow)) return true;
                _pending.TryRemove(id, out _);
            }
            return false;
        }

        public ApprovalRequest? GetRequest(string id)
        {
            return _pending.TryGetValue(id, out var request) ? request : null;
        }

        public bool TryConsume(string id, string toolId, string args)
        {
            if (!_pending.TryGetValue(id, out var request)) return false;
            if (!request.TryConsume(DateTime.UtcNow, toolId, args))
            {
                if (DateTime.UtcNow >= request.Expiry) _pending.TryRemove(id, out _);
                return false;
            }
            return _pending.TryRemove(id, out _);
        }

        public void Cleanup()
        {
            var expired = _pending.Where(kvp => DateTime.UtcNow >= kvp.Value.Expiry).Select(kvp => kvp.Key).ToList();
            foreach (var id in expired)
            {
                _pending.TryRemove(id, out _);
            }
        }
    }
}
