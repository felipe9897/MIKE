using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Mike.Tests;

public sealed class MobileRelaySecurityTests
{
    [Fact]
    public async Task RelayRequiresClientKeyForPhoneTraffic()
    {
        string php = FindExecutable("php.exe") ?? throw new InvalidOperationException("PHP CLI is required for the relay security test.");
        string source = FindRepositoryFile("local-ai", "relay.php");
        string root = Path.Combine(Path.GetTempPath(), "mike-relay-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.Copy(source, Path.Combine(root, "relay.php"));
        int port = ReservePort();
        using var server = Process.Start(new ProcessStartInfo(php)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-S", $"127.0.0.1:{port}", "-t", root }
        }) ?? throw new InvalidOperationException("Could not start the PHP test server.");

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            await WaitUntilReadyAsync(client);
            const string code = "ABCD2345";
            const string serverCredential = "relay-test-value-0123456789abcdef";
            const string key = "client-key-0123456789abcdef0123456789";

            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, new { action="register", code, secret=serverCredential, client_key=key })).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, new { action="status", code })).Status);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, new { action="status", code, client_key=key })).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, new { action="send", code, text="denied" })).Status);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, new { action="send", code, client_key=key, text="secure message" })).Status);

            var polled = await PostAsync(client, new { action="poll", code, secret=serverCredential });
            Assert.Equal("secure message", polled.Json.GetProperty("messages")[0].GetProperty("text").GetString());
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, new { action="reply", code, secret=serverCredential, text="secure reply" })).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, new { action="recv", code, since=0 })).Status);
            var received = await PostAsync(client, new { action="recv", code, client_key=key, since=0 });
            Assert.Equal("secure reply", received.Json.GetProperty("messages")[0].GetProperty("text").GetString());
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, new { action="unregister", code, secret=serverCredential })).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, new { action="send", code, client_key=key, text="after revoke" })).Status);
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitUntilReadyAsync(HttpClient client)
    {
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try { await PostAsync(client, new { action="status", code="ABCD2345" }); return; }
            catch (HttpRequestException) { await Task.Delay(100); }
        }
        throw new TimeoutException("PHP relay test server did not start.");
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> PostAsync(HttpClient client, object body)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("relay.php", content);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, json.RootElement.Clone());
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? FindExecutable(string name)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(directory.Trim(), name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
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
