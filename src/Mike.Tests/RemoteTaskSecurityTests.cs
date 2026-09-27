using System.Security.Cryptography;
using Mike.Common;
using Xunit;

namespace Mike.Service.Tests;

public sealed class RemoteTaskSecurityTests
{
    [Fact]
    public void SignedTaskIsAcceptedOnceOnlyByItsTarget()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        var sender = new RemoteTaskAuthenticator("source", secret);
        var receiver = new RemoteTaskAuthenticator("target", secret);
        var task = new RemoteTaskEnvelope {
            SourceNodeId = "source", TargetNodeId = "target", TaskType = "computer.observe",
            RequiredLevel = PermissionLevel.R1
        };
        sender.Sign(task);
        Assert.True(receiver.TryAuthorize(task, out _));
        Assert.False(receiver.TryAuthorize(task, out string replay));
        Assert.Equal("replayed_nonce", replay);
    }

    [Fact]
    public void TamperedExpiredWrongTargetAndDestructiveTasksAreRejected()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        var signer = new RemoteTaskAuthenticator("source", secret);
        var receiver = new RemoteTaskAuthenticator("target", secret);
        var task = new RemoteTaskEnvelope { SourceNodeId = "source", TargetNodeId = "target", TaskType = "computer.action", Payload = "{\"action\":\"click\"}" };
        signer.Sign(task);
        task.Payload = "{\"action\":\"type\"}";
        Assert.False(receiver.TryAuthorize(task, out string tampered));
        Assert.Equal("invalid_signature", tampered);

        var wrong = new RemoteTaskEnvelope { SourceNodeId = "source", TargetNodeId = "other", TaskType = "computer.observe" };
        signer.Sign(wrong);
        Assert.False(receiver.TryAuthorize(wrong, out string target));
        Assert.Equal("wrong_target", target);

        Assert.Throws<ArgumentException>(() => signer.Sign(new RemoteTaskEnvelope {
            SourceNodeId = "source", TargetNodeId = "target", TaskType = "delete.everything", RequiredLevel = PermissionLevel.R4
        }));
    }
}
