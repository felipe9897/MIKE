using Mike.Common;
using Xunit;

namespace Mike.Service.Tests;

public sealed class SecureStorageTests
{
    [Fact]
    public void DpapiRoundTripPreservesSecret()
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = "test-" + Guid.NewGuid().ToString("N");
        var fixtureValue = "mike-test-value-" + Guid.NewGuid().ToString("N");
        SecureStorage.SaveSecret(key, fixtureValue);
        Assert.Equal(fixtureValue, SecureStorage.GetSecret(key));
    }
}
