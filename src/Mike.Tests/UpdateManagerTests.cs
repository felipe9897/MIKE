using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Mike.Service;
using Xunit;

namespace Mike.Tests;

public sealed class UpdateManagerTests
{
    [Fact]
    public void ManifestRequiresHttpsAndOwnedHost()
    {
        const string hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        Assert.True(UpdateManager.ValidateManifest(new("1.0.46", "https://minike.com.br/update.exe", hash), out _));
        Assert.False(UpdateManager.ValidateManifest(new("1.0.46", "http://minike.com.br/update.exe", hash), out _));
        Assert.False(UpdateManager.ValidateManifest(new("1.0.46", "https://minike.com.br.attacker.test/update.exe", hash), out _));
    }

    [Fact]
    public async Task CurrentOrOlderVersionDoesNotDownload()
    {
        string root = TempRoot();
        int packageRequests = 0;
        try
        {
            var manager = Create(root, new Version(1, 0, 46), request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("manifest.json"))
                    return Json(new { version = "1.0.45", url = "https://minike.com.br/update.exe", sha256 = new string('A', 64) });
                packageRequests++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            });
            await manager.CheckForUpdatesAsync();
            Assert.Equal(0, packageRequests);
            Assert.Equal("current", ReadStatus(root).GetProperty("status").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task VerifiedPackageIsPromotedAtomically()
    {
        byte[] payload = Encoding.UTF8.GetBytes("signed installer fixture");
        string hash = Convert.ToHexString(SHA256.HashData(payload));
        string root = TempRoot();
        try
        {
            var manager = Create(root, new Version(1, 0, 45), request =>
                request.RequestUri!.AbsolutePath.EndsWith("manifest.json")
                    ? Json(new { version = "1.0.46", url = "https://minike.com.br/update.exe", sha256 = hash })
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            await manager.CheckForUpdatesAsync();
            Assert.True(File.Exists(Path.Combine(root, "MikeLocal-1.0.46.exe")));
            Assert.False(File.Exists(Path.Combine(root, "MikeLocal-1.0.46.exe.part")));
            Assert.Equal("ready", ReadStatus(root).GetProperty("status").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HashMismatchNeverPromotesPackage()
    {
        string root = TempRoot();
        try
        {
            var manager = Create(root, new Version(1, 0, 45), request =>
                request.RequestUri!.AbsolutePath.EndsWith("manifest.json")
                    ? Json(new { version = "1.0.46", url = "https://minike.com.br/update.exe", sha256 = new string('A', 64) })
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
            await manager.CheckForUpdatesAsync();
            Assert.False(File.Exists(Path.Combine(root, "MikeLocal-1.0.46.exe")));
            Assert.False(File.Exists(Path.Combine(root, "MikeLocal-1.0.46.exe.part")));
            Assert.Equal("failed", ReadStatus(root).GetProperty("status").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    private static UpdateManager Create(string root, Version current, Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new Mock<ILogger>().Object, "https://minike.com.br/manifest.json", root, current,
            () => new HttpClient(new StubHandler(response)), launchUpdates: false);

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private static JsonElement ReadStatus(string root) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "status.json"))).RootElement.Clone();

    private static string TempRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "mike-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
