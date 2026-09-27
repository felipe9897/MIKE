using System.Diagnostics;
using System.Text.Json;

namespace Mike.Common;

/// <summary>
/// Safe bridge to the official NousResearch Hermes CLI. Hermes is an optional
/// user-installed dependency; absence or an untrusted path fails closed.
/// This bridge never invokes a shell and passes all arguments separately.
/// </summary>
public sealed class HermesAgentClient
{
    private static readonly string[] AllowedRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hermes")
    ];

    public string? FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "bin", "hermes.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hermes", "hermes-agent", "venv", "Scripts", "hermes.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hermes", "hermes.exe")
        };

        return candidates.FirstOrDefault(IsAllowedExecutable);
    }

    public bool IsAvailable(out string reason)
    {
        var executable = FindExecutable();
        if (executable is null)
        {
            reason = "Hermes oficial não está instalado para este usuário.";
            return false;
        }

        reason = executable;
        return true;
    }

    public async Task<string> SendAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("O prompt não pode ser vazio.", nameof(prompt));
        if (prompt.Length > MikeIpcProtocol.MaxPayloadSize)
            throw new ArgumentException("O prompt excede o limite permitido.", nameof(prompt));

        var executable = FindExecutable()
            ?? throw new InvalidOperationException("Hermes oficial não está instalado; integração recusada.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            }
        };
        // Hermes treats local Ollama as an OpenAI-compatible custom endpoint.
        // CUSTOM_BASE_URL is the supported per-process override for a custom
        // provider; do not overwrite the user's cloud config or leak any key.
        process.StartInfo.Environment["CUSTOM_BASE_URL"] = "http://127.0.0.1:11434/v1";
        process.StartInfo.Environment["CUSTOM_API_KEY"] = "ollama-local";
        process.StartInfo.ArgumentList.Add("chat");
        process.StartInfo.ArgumentList.Add("--oneshot");
        process.StartInfo.ArgumentList.Add("--quiet");
        process.StartInfo.ArgumentList.Add("-q");
        process.StartInfo.ArgumentList.Add(prompt);
        process.StartInfo.ArgumentList.Add("--provider");
        process.StartInfo.ArgumentList.Add("custom");
        process.StartInfo.ArgumentList.Add("--model");
        // Hermes requires a 64K context window for its tool loop. The small
        // qwen2.5:1.5b Ollama model is still available to Mike's direct
        // InferenceRouter, but its advertised 32K window cannot initialize
        // Hermes. Allow operators to select another local model explicitly.
        process.StartInfo.ArgumentList.Add(await SelectHermesModelAsync(cancellationToken));
        // Compact local models such as llama3.2 do not expose the optional
        // OpenAI `thinking` parameter. Hermes must disable it for Ollama.
        process.StartInfo.ArgumentList.Add("--reasoning");
        process.StartInfo.ArgumentList.Add("none");
        process.StartInfo.ArgumentList.Add("--toolsets");
        process.StartInfo.ArgumentList.Add("terminal,file,browser,code_execution,vision,skills,todo,memory,delegation,computer_use");
        process.StartInfo.ArgumentList.Add("--checkpoints");
        process.StartInfo.ArgumentList.Add("--max-turns");
        process.StartInfo.ArgumentList.Add("30");
        process.StartInfo.ArgumentList.Add("--run-budget");
        process.StartInfo.ArgumentList.Add("300");
        process.StartInfo.ArgumentList.Add("--source");
        process.StartInfo.ArgumentList.Add("mike-local");

        if (!process.Start())
            throw new InvalidOperationException("Não foi possível iniciar Hermes.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : timeout);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Hermes terminou com código {process.ExitCode}: {TrimError(error)}");
            return output.Trim();
        }
        catch
        {
            TryTerminate(process);
            throw;
        }
    }

    private static bool IsAllowedExecutable(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var full = Path.GetFullPath(path);
        return AllowedRoots.Any(root => full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> SelectHermesModelAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("MIKE_HERMES_MODEL")?.Trim() is { Length: > 0 } configured)
            return configured;

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync("http://127.0.0.1:11434/api/tags", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var installed = document.RootElement.GetProperty("models").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString() ?? string.Empty)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Prefer the responsive model on modest PCs. Operators may opt in
            // to Hermes 3 with MIKE_HERMES_MODEL on machines where its latency
            // is acceptable; Mike still rejects unexecuted/fabricated actions.
            foreach (string preferred in new[] { "llama3.2:3b", "hermes3:8b", "hermes3:latest" })
                if (installed.Contains(preferred)) return preferred;
        }
        catch { /* Hermes will report a useful Ollama error when invoked. */ }

        return "hermes3:8b";
    }

    private static string TrimError(string error) => string.IsNullOrWhiteSpace(error) ? "sem detalhes" : error.Trim()[..Math.Min(error.Trim().Length, 1000)];

    private static void TryTerminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }
}
