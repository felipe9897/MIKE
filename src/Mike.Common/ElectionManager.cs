using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Mike.Common
{
    public class ElectionManager
    {
        private readonly MeshManager _meshManager;
        private readonly ILogger _logger;
        private bool _isLeader = false;
        private DateTime _lastElection = DateTime.MinValue;
        private readonly TimeSpan _electionInterval = TimeSpan.FromSeconds(5);

        public bool IsLeader => _isLeader;

        public ElectionManager(MeshManager meshManager, ILogger logger)
        {
            _meshManager = meshManager;
            _logger = logger;
        }

        public void RunElection()
        {
            if (DateTime.UtcNow - _lastElection < _electionInterval) return;
            _lastElection = DateTime.UtcNow;

            var peers = _meshManager.GetPeers();
            var allNodes = peers.Select(p => p.NodeId).ToList();
            allNodes.Add(_meshManager.LocalNode.NodeId);

            allNodes.Sort();

            bool nowLeader = allNodes.First() == _meshManager.LocalNode.NodeId;

            if (nowLeader != _isLeader)
            {
                _isLeader = nowLeader;
                _logger.LogInformation("Election result: Node {NodeId} is now the leader. Local status: {Status}",
                    allNodes.First(), _isLeader ? "LEADER" : "FOLLOWER");
            }
        }

        public async Task StartElectionLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                RunElection();
                await Task.Delay(2000, ct);
            }
        }
    }
}
