using Xunit;

namespace Mike.Tests;

public sealed class InstallerScriptTests
{
    [Fact]
    public void PrerequisitesStartsCompatibilityBackendDetachedForRequestedProfile()
    {
        string script = File.ReadAllText(FindRepositoryFile("installer", "MikePrerequisites.ps1"));

        Assert.Contains("$backendRoot = Join-Path $UserLocalAppData 'MINIKE-Local-AI'", script);
        Assert.Contains("$backendArgumentLine", script);
        Assert.Contains("' -File \"'", script);
        Assert.Contains("' -InstallRoot \"'", script);
        Assert.Contains("$backendProcess = Start-Process -FilePath $WindowsPowerShell", script);
        Assert.Contains("-PassThru", script);
        Assert.Contains("(Join-Path $backendRoot 'backend.pid')", script);
        Assert.DoesNotContain("& $WindowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $backendCore", script);
        Assert.Contains("$codePath = if ($code -is [System.Management.Automation.CommandInfo])", script);
        Assert.Contains("& $codePath --install-extension", script);
        Assert.DoesNotContain("& $code.Source --install-extension", script);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Repository file not found: {Path.Combine(parts)}");
    }
}
