using Mike.Common;
using Mike.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mike.Service.Tests;

public sealed class ActionGuardTests
{
    [Fact]
    public async Task ApprovalIsBoundToArgumentsAndConsumedOnce()
    {
        var approvals = new ApprovalService(new ApprovalRegistry(), NullLogger.Instance);
        var broker = new ToolBroker(approvals);
        var calls = 0;
        broker.RegisterTool(new ToolDefinition { Id = "write", RequiredLevel = PermissionLevel.R2, Action = _ => { calls++; return Task.FromResult("ok"); } });

        var requested = await broker.ExecuteToolAsync("write", "a", PermissionLevel.R0);
        var id = System.Text.Json.JsonDocument.Parse(requested).RootElement.GetProperty("approvalId").GetString()!;
        approvals.MarkApproved(id);

        var wrongArgs = await broker.ExecuteToolAsync("write", "b", PermissionLevel.R0, id);
        Assert.Contains("approval_required", wrongArgs);
        var accepted = await broker.ExecuteToolAsync("write", "a", PermissionLevel.R0, id);
        Assert.Contains("success", accepted);
        var replay = await broker.ExecuteToolAsync("write", "a", PermissionLevel.R0, id);
        Assert.Contains("approval_required", replay);
        Assert.Equal(1, calls);
    }
}
