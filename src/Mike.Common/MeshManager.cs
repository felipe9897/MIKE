using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Mike.Common
{
    public class NodeIdentity
    {
        public string NodeId { get; set; }
        public string Hostname { get; set; } = Environment.MachineName;
        public string IpAddress { get; set; } = "0.0.0.0";
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public string HardwareTier { get; set; } = "unknown";
        public int ProcessorCount { get; set; }
        public double RamGb { get; set; }
        public double CurrentLoad { get; set; }
        public string[] SupportedTasks { get; set; } = Array.Empty<string>();

        public NodeIdentity()
        {
            NodeId = SecureStorage.GetSecret("node_identity") ?? GenerateAndSaveId();
        }

        private string GenerateAndSaveId()
        {
            var id = Guid.NewGuid().ToString("N");
            SecureStorage.SaveSecret("node_identity", id);
            return id;
        }
    }

    public class MeshManager
    {
        public NodeIdentity LocalNode { get; }
        private readonly Dictionary<string, NodeIdentity> _peers = new();

        public MeshManager()
        {
            LocalNode = new NodeIdentity();
            LocalNode.IpAddress = GetLocalIpAddress();
            LocalNode.ProcessorCount = Environment.ProcessorCount;
        }

        private string GetLocalIpAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !System.Net.IPAddress.IsLoopback(ip))
                    {
                        return ip.ToString();
                    }
                }
            }
            catch (Exception) { }
            return "0.0.0.0";
        }

        public void UpdatePeer(NodeIdentity peer)
        {
            if (peer.NodeId == LocalNode.NodeId) return;
            _peers[peer.NodeId] = peer;
        }

        public List<NodeIdentity> GetPeers() => _peers.Values.ToList();

        public NodeIdentity GetCoordinator()
        {
            return new[] { LocalNode }.Concat(GetPeers())
                .OrderByDescending(n => TierScore(n.HardwareTier))
                .ThenByDescending(n => n.RamGb)
                .ThenByDescending(n => n.ProcessorCount)
                .ThenBy(n => n.NodeId, StringComparer.Ordinal)
                .First();
        }

        private static int TierScore(string? tier) => tier?.ToLowerInvariant() switch
        {
            "high" => 3,
            "mid" or "directml" => 2,
            "lite" => 1,
            _ => 0
        };

        public void RemovePeer(string nodeId) => _peers.Remove(nodeId);
    }
}
