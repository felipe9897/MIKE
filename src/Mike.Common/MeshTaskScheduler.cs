namespace Mike.Common;

public sealed record MeshTaskRequest(string TaskType, int EstimatedCost = 1, bool RequiresGpu = false, bool AllowRemote = true);

public sealed record MeshTaskAssignment(string NodeId, bool IsLocal, int Attempt, TimeSpan Timeout);

/// <summary>Plans work only. It never executes a remote command; the authenticated
/// transport remains responsible for sending an approved, scoped task.</summary>
public sealed class MeshTaskScheduler
{
    private readonly MeshManager _mesh;
    public MeshTaskScheduler(MeshManager mesh) => _mesh = mesh;

    public IReadOnlyList<MeshTaskAssignment> Plan(MeshTaskRequest request, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(request.TaskType)) throw new ArgumentException("Tipo de tarefa obrigatorio.", nameof(request));
        if (request.EstimatedCost < 1 || request.EstimatedCost > 100) throw new ArgumentOutOfRangeException(nameof(request.EstimatedCost));
        var nodes = new[] { _mesh.LocalNode }.Concat(_mesh.GetPeers())
            .Where(n => n.LastSeen > DateTime.UtcNow.AddMinutes(-2))
            .Where(n => !request.RequiresGpu || n.HardwareTier is "high" or "mid" || n.SupportedTasks.Contains(request.TaskType, StringComparer.OrdinalIgnoreCase))
            .Where(n => n.SupportedTasks.Length == 0 || n.SupportedTasks.Contains(request.TaskType, StringComparer.OrdinalIgnoreCase))
            .OrderBy(n => n.CurrentLoad)
            .ThenByDescending(n => TierScore(n.HardwareTier))
            .ThenByDescending(n => n.RamGb)
            .ThenBy(n => n.NodeId, StringComparer.Ordinal)
            .ToList();
        if (!request.AllowRemote) nodes = nodes.Where(n => n.NodeId == _mesh.LocalNode.NodeId).ToList();
        var selected = nodes.Take(Math.Min(nodes.Count, 3)).ToList();
        var t = timeout is { } value && value > TimeSpan.Zero ? value : TimeSpan.FromMinutes(5);
        return selected.Select((n, i) => new MeshTaskAssignment(n.NodeId, n.NodeId == _mesh.LocalNode.NodeId, i + 1, t)).ToArray();
    }

    private static int TierScore(string? tier) => tier?.ToLowerInvariant() switch { "high" => 3, "mid" or "directml" => 2, "lite" => 1, _ => 0 };
}
