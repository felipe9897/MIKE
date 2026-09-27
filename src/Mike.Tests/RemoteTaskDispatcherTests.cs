using System.Security.Cryptography;
using System.Text.Json;
using Mike.Common;
using Xunit;

namespace Mike.Service.Tests;

public sealed class RemoteTaskDispatcherTests
{
    [Fact]
    public async Task SignedReadRunsButRemoteWriteRequiresBoundOneShotApproval()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        var signer = new RemoteTaskAuthenticator("phone", secret);
        var receiver = new RemoteTaskAuthenticator("pc", secret);
        var approvals = new ApprovalService(new ApprovalRegistry(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var broker = new ToolBroker(approvals);
        int writes = 0;
        broker.RegisterTool(new ToolDefinition { Id = "observe", RequiredLevel = PermissionLevel.R1, Action = _ => Task.FromResult("screen") });
        broker.RegisterTool(new ToolDefinition { Id = "write", RequiredLevel = PermissionLevel.R2, Action = _ => { writes++; return Task.FromResult("done"); } });
        var dispatcher = new RemoteTaskDispatcher(receiver, broker, new Dictionary<string, string> {
            ["computer.observe"] = "observe", ["computer.write"] = "write"
        });

        var read = new RemoteTaskEnvelope { SourceNodeId = "phone", TargetNodeId = "pc", TaskType = "computer.observe", RequiredLevel = PermissionLevel.R1 };
        signer.Sign(read);
        Assert.Equal("success", JsonDocument.Parse(await dispatcher.DispatchAsync(read)).RootElement.GetProperty("type").GetString());

        var write = new RemoteTaskEnvelope { SourceNodeId = "phone", TargetNodeId = "pc", TaskType = "computer.write", Payload = "typed text", RequiredLevel = PermissionLevel.R2 };
        signer.Sign(write);
        using JsonDocument requested = JsonDocument.Parse(await dispatcher.DispatchAsync(write));
        Assert.Equal("approval_required", requested.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, writes);
        string approvalId = requested.RootElement.GetProperty("approvalId").GetString()!;
        approvals.MarkApproved(approvalId);

        // An approval retry is a new signed request with a new nonce, but is
        // still bound to the same tool and exact payload by ApprovalService.
        write.Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        signer.Sign(write);
        Assert.Equal("success", JsonDocument.Parse(await dispatcher.DispatchAsync(write, approvalId)).RootElement.GetProperty("type").GetString());
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task UnknownTaskNeverReachesBroker()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        var signer = new RemoteTaskAuthenticator("phone", secret);
        var receiver = new RemoteTaskAuthenticator("pc", secret);
        var dispatcher = new RemoteTaskDispatcher(receiver, new ToolBroker(), new Dictionary<string, string>());
        var task = new RemoteTaskEnvelope { SourceNodeId = "phone", TargetNodeId = "pc", TaskType = "shell.raw" };
        signer.Sign(task);
        using JsonDocument result = JsonDocument.Parse(await dispatcher.DispatchAsync(task));
        Assert.Equal("task_not_allowed", result.RootElement.GetProperty("code").GetString());
    }
}
