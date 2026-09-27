using Mike.Service;
using Xunit;

namespace Mike.Service.Tests;

public sealed class AutoHealTaskRecoveryTests
{
    [Fact]
    public void RegistrationUsesSystem64PowerShellAndArgumentList()
    {
        string root = Path.Combine("C:\\Program Files", "MikeLocal");
        var info = AutoHealTaskRecovery.CreateRegistrationStartInfo(root);

        Assert.Contains("System32", info.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("powershell.exe", info.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-InstallTask", info.ArgumentList);
        Assert.Contains(root, info.ArgumentList);
        Assert.Contains(Path.Combine(root, "MikeAutoHeal.ps1"), info.ArgumentList);
        Assert.DoesNotContain(info.ArgumentList, value => value.EndsWith("\\\"", StringComparison.Ordinal));
    }

    [Fact]
    public void InstallRootIsParentOfServiceDirectory()
    {
        string service = Path.Combine("C:\\Program Files", "MikeLocal", "service") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.Combine("C:\\Program Files", "MikeLocal"), AutoHealTaskRecovery.ResolveInstallRoot(service));
    }

    [Fact]
    public void TaskNameRemainsStableForInstallerAndServiceRecovery()
    {
        Assert.Equal("MikeLocal-AutoHeal", AutoHealTaskRecovery.TaskName);
    }
}
