using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mike.Common;

public sealed record SelfCodeFileChange(string RelativePath, string ExpectedSha256, string ContentBase64);
public sealed record SelfCodeChangePlan(string CandidateId, string WorkspaceRoot, IReadOnlyList<SelfCodeFileChange> Changes);
public sealed record SelfCodeStageResult(bool Ok, string Status, string StageRoot, string Log, IReadOnlyList<string> Files);

/// <summary>
/// Stages self-code changes in a detached git worktree. It never promotes by
/// itself: promotion requires an explicit owner confirmation and preserves an
/// atomic rollback snapshot outside the workspace.
/// </summary>
public sealed class SelfCodeEvolutionExecutor
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".ps1", ".cmd", ".json", ".html", ".css", ".js", ".md", ".wxs" };
    private readonly string stateRoot;

    public SelfCodeEvolutionExecutor(string stateRoot)
    {
        this.stateRoot = Path.GetFullPath(stateRoot);
        Directory.CreateDirectory(this.stateRoot);
    }

    public async Task<SelfCodeStageResult> StageAndValidateAsync(SelfCodeChangePlan plan, CancellationToken token = default)
    {
        ValidatePlan(plan);
        string workspace = Path.GetFullPath(plan.WorkspaceRoot);
        string stage = Path.Combine(stateRoot, "staging", plan.CandidateId);
        if (Directory.Exists(stage)) await RemoveWorktreeAsync(workspace, stage);
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);

        var log = new StringBuilder();
        var worktree = await RunAsync("git", $"worktree add --detach \"{stage}\" HEAD", workspace, token);
        log.AppendLine(worktree.Output);
        if (worktree.ExitCode != 0) return new(false, "worktree_failed", stage, log.ToString(), []);

        try
        {
            foreach (SelfCodeFileChange change in plan.Changes)
            {
                string source = ResolveInside(workspace, change.RelativePath);
                if (!HashMatches(source, change.ExpectedSha256))
                    return new(false, "source_changed", stage, log.ToString(), []);
                byte[] content = Convert.FromBase64String(change.ContentBase64);
                if (content.Length > 2 * 1024 * 1024) throw new InvalidOperationException("A self-code file exceeds 2 MB.");
                string target = ResolveInside(stage, change.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, content, token);
            }

            string tests = Path.Combine(stage, "src", "Mike.Tests", "Mike.Tests.csproj");
            if (!File.Exists(tests)) return new(false, "test_project_missing", stage, log.ToString(), []);
            var validation = await RunAsync("dotnet", $"test \"{tests}\" -c Release", stage, token, 20 * 60 * 1000);
            log.AppendLine(validation.Output);
            string status = validation.ExitCode == 0 ? "validated" : "tests_failed";
            await WriteManifestAsync(plan, stage, status, log.ToString(), token);
            return new(validation.ExitCode == 0, status, stage, log.ToString(), plan.Changes.Select(x => x.RelativePath).ToArray());
        }
        catch
        {
            await RemoveWorktreeAsync(workspace, stage);
            throw;
        }
    }

    public async Task<string> PromoteAsync(SelfCodeChangePlan plan, bool ownerConfirmed, CancellationToken token = default)
    {
        if (!ownerConfirmed) throw new UnauthorizedAccessException("Owner confirmation is required.");
        ValidatePlan(plan);
        string workspace = Path.GetFullPath(plan.WorkspaceRoot);
        string stage = Path.Combine(stateRoot, "staging", plan.CandidateId);
        string manifest = Path.Combine(stage, ".mike-evolution.json");
        if (!File.Exists(manifest) || !File.ReadAllText(manifest).Contains("\"validated\"", StringComparison.Ordinal))
            throw new InvalidOperationException("The candidate has not passed validation.");
        string snapshot = Path.Combine(stateRoot, "snapshots", plan.CandidateId);
        foreach (SelfCodeFileChange change in plan.Changes)
        {
            string current = ResolveInside(workspace, change.RelativePath);
            if (!HashMatches(current, change.ExpectedSha256)) throw new InvalidOperationException("Source changed after staging.");
            string backup = ResolveInside(snapshot, change.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(current, backup, true);
            string staged = ResolveInside(stage, change.RelativePath);
            string temporary = current + ".mike-new";
            File.Copy(staged, temporary, true);
            File.Move(temporary, current, true);
        }
        await File.WriteAllTextAsync(Path.Combine(snapshot, ".promoted"), DateTimeOffset.UtcNow.ToString("O"), token);
        return snapshot;
    }

    public void Rollback(SelfCodeChangePlan plan)
    {
        ValidatePlan(plan);
        string workspace = Path.GetFullPath(plan.WorkspaceRoot);
        string snapshot = Path.Combine(stateRoot, "snapshots", plan.CandidateId);
        if (!File.Exists(Path.Combine(snapshot, ".promoted"))) throw new InvalidOperationException("Rollback snapshot not found.");
        foreach (SelfCodeFileChange change in plan.Changes)
            File.Copy(ResolveInside(snapshot, change.RelativePath), ResolveInside(workspace, change.RelativePath), true);
    }

    public async Task DiscardStageAsync(SelfCodeChangePlan plan)
    {
        ValidatePlan(plan);
        await RemoveWorktreeAsync(Path.GetFullPath(plan.WorkspaceRoot), Path.Combine(stateRoot, "staging", plan.CandidateId));
    }

    private static void ValidatePlan(SelfCodeChangePlan plan)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(plan.CandidateId ?? "", "^[a-zA-Z0-9_-]{8,80}$"))
            throw new ArgumentException("Invalid candidate ID.");
        string workspace = Path.GetFullPath(plan.WorkspaceRoot ?? "");
        if (!Directory.Exists(Path.Combine(workspace, ".git")) && !File.Exists(Path.Combine(workspace, ".git")))
            throw new ArgumentException("Workspace must be a git repository.");
        if (plan.Changes.Count is < 1 or > 20) throw new ArgumentException("A candidate must change 1 to 20 files.");
        foreach (SelfCodeFileChange change in plan.Changes)
        {
            string extension = Path.GetExtension(change.RelativePath);
            if (!AllowedExtensions.Contains(extension) || change.ExpectedSha256.Length != 64 || !change.ExpectedSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("Candidate contains a disallowed file or hash.");
            _ = Convert.FromBase64String(change.ContentBase64);
            _ = ResolveInside(workspace, change.RelativePath);
        }
    }

    private static string ResolveInside(string root, string relative)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Path escapes workspace.");
        return full;
    }

    private static bool HashMatches(string file, string expected) => File.Exists(file) &&
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static async Task WriteManifestAsync(SelfCodeChangePlan plan, string stage, string status, string log, CancellationToken token)
    {
        string json = JsonSerializer.Serialize(new { plan.CandidateId, status, files = plan.Changes.Select(x => x.RelativePath), validatedAt = DateTimeOffset.UtcNow, logSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(log))) });
        await File.WriteAllTextAsync(Path.Combine(stage, ".mike-evolution.json"), json, token);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string arguments, string cwd, CancellationToken token, int timeoutMs = 120000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(timeoutMs);
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })
            ?? throw new InvalidOperationException("Could not start validation process.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    private static async Task RemoveWorktreeAsync(string workspace, string stage)
    {
        try { await RunAsync("git", $"worktree remove --force \"{stage}\"", workspace, CancellationToken.None); } catch { }
    }
}
