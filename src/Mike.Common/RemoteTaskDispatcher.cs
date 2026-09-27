using System.Text.Json;

namespace Mike.Common;

public sealed class RemoteTaskDispatcher
{
    private readonly RemoteTaskAuthenticator authenticator;
    private readonly ToolBroker broker;
    private readonly IReadOnlyDictionary<string, string> taskTools;

    public RemoteTaskDispatcher(RemoteTaskAuthenticator authenticator, ToolBroker broker,
        IReadOnlyDictionary<string, string> taskTools)
    {
        this.authenticator = authenticator;
        this.broker = broker;
        this.taskTools = taskTools;
    }

    public async Task<string> DispatchAsync(RemoteTaskEnvelope envelope, string? approvalId = null)
    {
        if (!authenticator.TryAuthorize(envelope, out string error))
            return JsonSerializer.Serialize(new { type = "error", code = error });
        if (!taskTools.TryGetValue(envelope.TaskType, out string? toolId))
            return JsonSerializer.Serialize(new { type = "error", code = "task_not_allowed" });

        // A signed pairing authorizes the origin, target and payload. It grants
        // read-only execution; user/admin writes still pass through a one-shot
        // approval bound by ToolBroker to the exact tool and arguments.
        PermissionLevel effectiveLevel = envelope.RequiredLevel <= PermissionLevel.R1
            ? envelope.RequiredLevel : PermissionLevel.R1;
        return await broker.ExecuteToolAsync(toolId, envelope.Payload, effectiveLevel, approvalId);
    }
}
