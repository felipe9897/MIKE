using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Mike.Service;

public static class AutoHealTaskRecovery
{
    public const string TaskName = "MikeLocal-AutoHeal";

    public static string ResolveInstallRoot(string serviceBaseDirectory) =>
        Path.GetFullPath(Path.Combine(serviceBaseDirectory, ".."));

    public static ProcessStartInfo CreateRegistrationStartInfo(string installRoot)
    {
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        string script = Path.Combine(installRoot, "MikeAutoHeal.ps1");
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = installRoot
        };
        foreach (string argument in new[] {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script, "-InstallRoot", installRoot, "-InstallTask"
        }) info.ArgumentList.Add(argument);
        return info;
    }

    public static bool TaskExists()
    {
        try
        {
            using var query = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                $"/Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            query?.WaitForExit(10_000);
            return query is not null && query.HasExited && query.ExitCode == 0;
        }
        catch { return false; }
    }

    public static async Task<bool> IsCompatibilityBackendHealthyAsync(CancellationToken token)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await client.GetAsync("http://127.0.0.1:47885/ui/status", token);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public static bool TriggerTask()
    {
        try
        {
            using var run = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                $"/Run /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            run?.WaitForExit(10_000);
            return run is not null && run.HasExited && run.ExitCode == 0;
        }
        catch { return false; }
    }

    public static async Task EnsureAsync(string installRoot, ILogger logger, CancellationToken token)
    {
        try
        {
            if (TaskExists()) return;
            string script = Path.Combine(installRoot, "MikeAutoHeal.ps1");
            if (!File.Exists(script))
            {
                logger.LogWarning("AutoHeal recovery skipped: installer script is missing.");
                return;
            }

            using var process = Process.Start(CreateRegistrationStartInfo(installRoot));
            if (process is null)
            {
                logger.LogWarning("AutoHeal recovery could not start PowerShell.");
                return;
            }
            await process.WaitForExitAsync(token);
            string error = await process.StandardError.ReadToEndAsync(token);
            if (process.ExitCode == 0 && TaskExists())
                logger.LogInformation("AutoHeal scheduled task was restored by Mike Service.");
            else
                logger.LogWarning("AutoHeal task recovery did not complete. Exit={ExitCode}; Error={Error}", process.ExitCode, error.Trim());
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("AutoHeal task recovery was canceled because the service is stopping.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AutoHeal task recovery failed; the service remains available.");
        }
    }
}
