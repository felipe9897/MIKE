using Mike.Common;
using Xunit;

namespace Mike.Service.Tests;

public sealed class MeshManagerTests
{
    [Fact]
    public void CoordinatorUsesHardwareTierThenStableNodeId()
    {
        var mesh = new MeshManager();
        mesh.LocalNode.HardwareTier = "lite";
        mesh.LocalNode.RamGb = 8;
        mesh.LocalNode.ProcessorCount = 4;
        mesh.UpdatePeer(new NodeIdentity
        {
            NodeId = "high-node",
            Hostname = "GPU-PC",
            HardwareTier = "high",
            RamGb = 32,
            ProcessorCount = 16,
            LastSeen = DateTime.UtcNow
        });

        Assert.Equal("high-node", mesh.GetCoordinator().NodeId);
    }

    [Fact]
    public void LocalNodeIsNeverAddedAsPeer()
    {
        var mesh = new MeshManager();
        mesh.UpdatePeer(mesh.LocalNode);
        Assert.Empty(mesh.GetPeers());
    }

    [Fact]
    public void SchedulerRanksLowLoadBeforeStrongButUsesStableFallbacks()
    {
        var mesh = new MeshManager();
        mesh.LocalNode.HardwareTier = "high";
        mesh.LocalNode.CurrentLoad = 0.9;
        mesh.LocalNode.SupportedTasks = new[] { "image" };
        mesh.UpdatePeer(new NodeIdentity { NodeId="worker-b", HardwareTier="mid", CurrentLoad=0.1, SupportedTasks=new[] { "image" } });
        mesh.UpdatePeer(new NodeIdentity { NodeId="worker-a", HardwareTier="high", CurrentLoad=0.1, SupportedTasks=new[] { "image" } });

        var plan = new MeshTaskScheduler(mesh).Plan(new MeshTaskRequest("image", 50, true));
        Assert.Equal(new[] { "worker-a", "worker-b", mesh.LocalNode.NodeId }, plan.Select(x => x.NodeId));
        Assert.Equal(new[] { 1, 2, 3 }, plan.Select(x => x.Attempt));
    }

    [Fact]
    public void SchedulerCanForceLocalOnlyForLightWork()
    {
        var mesh = new MeshManager();
        var plan = new MeshTaskScheduler(mesh).Plan(new MeshTaskRequest("chat", 1, false, false));
        var assignment = Assert.Single(plan);
        Assert.True(assignment.IsLocal);
    }
}
