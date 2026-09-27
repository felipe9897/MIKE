using System.Text.Json;
using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mike.Common;
using Mike.Desktop;
using Xunit;

namespace Mike.Service.Tests;

public sealed class NativeApiBridgeTests
{
    [Fact]
    public async Task MainNativeRoutesReturnExpectedContracts()
    {
        using var bridge = new NativeApiBridge();
        Assert.True(Property(await bridge.HandleAsync("/ui/status", "GET", null), "ok"));
        Assert.NotNull(PropertyElement(await bridge.HandleAsync("/models", "GET", null), "models"));
        Assert.NotNull(PropertyElement(await bridge.HandleAsync("/memory", "GET", null), "notes"));
        Assert.True(Property(await bridge.HandleAsync("/files", "GET", null), "ok"));
        Assert.NotNull(PropertyElement(await bridge.HandleAsync("/agents", "GET", null), "agents"));
        Assert.NotNull(PropertyElement(await bridge.HandleAsync("/peers", "GET", null), "peers"));
        Assert.Equal(Mike.Common.MikeConstants.Version, PropertyElement(await bridge.HandleAsync("/ui/status", "GET", null), "version")?.GetString());
    }

    [Fact]
    public async Task LearningRequiresApprovalAndSupportsRollback()
    {
        using var bridge = new NativeApiBridge();
        var proposed = Json(await bridge.HandleAsync("/learning/propose", "POST", "{\"kind\":\"skill\",\"title\":\"Teste controlado\",\"content\":\"responder de forma auditÃ¡vel\"}"));
        string id = proposed.GetProperty("proposal").GetProperty("id").GetString()!;
        Assert.Equal("pending", proposed.GetProperty("proposal").GetProperty("status").GetString());
        var approved = Json(await bridge.HandleAsync("/learning/approve", "POST", $"{{\"id\":\"{id}\"}}"));
        Assert.True(approved.GetProperty("ok").GetBoolean());
        var history = Json(await bridge.HandleAsync("/learning/history", "GET", null));
        Assert.Contains(history.GetProperty("approved_skills").EnumerateArray(), x => x.GetProperty("proposalId").GetString() == id);
        var rolledBack = Json(await bridge.HandleAsync("/learning/rollback", "POST", $"{{\"id\":\"{id}\"}}"));
        Assert.True(rolledBack.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ConversationsPersistAndProvideMultiTurnContext()
    {
        using var bridge = new NativeApiBridge();
        string marker = "contexto-" + Guid.NewGuid().ToString("N");
        var first = bridge.PrepareChat($"{{\"message\":\"Meu cÃ³digo Ã© {marker}\"}}");
        bridge.CompleteChat(first, "Vou lembrar nesta conversa.");
        var second = bridge.PrepareChat($"{{\"message\":\"qual era o cÃ³digo?\",\"conversation_id\":\"{first.Id}\"}}");
        Assert.Contains(marker, second.Prompt);
        Assert.Contains("Vou lembrar", second.Prompt);
        var loaded = Json(await bridge.HandleAsync($"/conversations/get?id={first.Id}", "GET", null));
        Assert.True(loaded.GetProperty("ok").GetBoolean());
        Assert.Equal(3, loaded.GetProperty("conversation").GetProperty("messages").GetArrayLength());
        var deleted = Json(await bridge.HandleAsync("/conversations/delete", "POST", $"{{\"id\":\"{first.Id}\"}}"));
        Assert.True(deleted.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task LegacyCompatibilityProxyServesAdvancedRoutes()
    {
        if (Environment.GetEnvironmentVariable("MIKE_TEST_LEGACY_BACKEND") != "1") return;
        using var bridge = new NativeApiBridge();
        foreach (string route in new[] { "/tools", "/capabilities", "/projects", "/terminal/history", "/connectors", "/pc-health/status" })
        {
            var result = Json(await bridge.HandleAsync(route, "GET", null));
            Assert.False(result.TryGetProperty("unavailable", out var unavailable) && unavailable.GetBoolean(), route);
        }
    }

    [Fact]
    public async Task ChatUsesFullCatalogRouterAndKeepsNativeConversation()
    {
        if (Environment.GetEnvironmentVariable("MIKE_TEST_LEGACY_BACKEND") != "1") return;
        using var bridge = new NativeApiBridge();
        var prepared = bridge.PrepareChat("{\"message\":\"quanto Ã© 2 + 2?\"}");
        var routed = Json((await bridge.TryCatalogChatAsync(prepared, "{\"message\":\"quanto Ã© 2 + 2?\"}"))!);
        Assert.True(routed.GetProperty("catalog_router").GetBoolean());
        Assert.True(routed.GetProperty("context_persisted").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(routed.GetProperty("answer").GetString()));
        await bridge.HandleAsync("/conversations/delete", "POST", $"{{\"id\":\"{prepared.Id}\"}}");
    }

    [Fact]
    public async Task LearningRejectsUnreachableInventedSources()
    {
        var method = typeof(NativeApiBridge).GetMethod("ExtractVerifiedSourcesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var task = (Task<string[]>)method!.Invoke(null, new object[] { "Fonte falsa: https://fonte-inexistente.invalid/ncm-icms" })!;
        Assert.Empty(await task);
    }

    [Fact]
    public async Task AdministrativeDiagnosticRunsOnlyInExplicitInteractiveTest()
    {
        if (Environment.GetEnvironmentVariable("MIKE_TEST_UAC") != "1") return;
        using var bridge = new NativeApiBridge();
        var refused = Json(await bridge.HandleAsync("/admin/diagnostic", "POST", "{\"confirm\":false}"));
        Assert.True(refused.GetProperty("confirmation_required").GetBoolean());
        var result = Json(await bridge.HandleAsync("/admin/diagnostic", "POST", "{\"confirm\":true}"));
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(File.Exists(result.GetProperty("marker").GetString()));
    }

    [Fact]
    public async Task AutomaticEvolutionApiPublishesAgentsAndKeepsAdministrativeWorkQuarantined()
    {
        using var bridge = new NativeApiBridge();
        var status = Json(await bridge.HandleAsync("/evolution/automatic", "GET", null));
        Assert.True(status.GetProperty("automatic").GetBoolean());
        Assert.True(status.GetProperty("agents").GetArrayLength() >= 6);
        var proposed = Json(await bridge.HandleAsync("/evolution/automatic/propose", "POST",
            "{\"agentId\":\"fleet-administrator\",\"domain\":\"fleet\",\"description\":\"remote printer\",\"risk\":2,\"baselineScore\":0.5}"));
        Assert.Equal("quarantine", proposed.GetProperty("candidate").GetProperty("status").GetString());
    }

    [Fact]
    public void ComputerOperatorRejectsArbitraryKeysAndUnsafeText()
    {
        Assert.False(WindowsComputerOperator.Validate(new ComputerActionRequest { Action = "key", Text = "win+r" }, out _));
        Assert.False(WindowsComputerOperator.Validate(new ComputerActionRequest { Action = "type", Text = "line1\nline2" }, out _));
        Assert.True(WindowsComputerOperator.Validate(new ComputerActionRequest { Action = "key", Text = "enter" }, out _));
        Assert.True(WindowsComputerOperator.Validate(new ComputerActionRequest { Action = "type", Text = "texto autorizado" }, out _));
    }

    [Fact]
    public void ComputerChatRecognizesPhysicalRequestsAndRequiresOwnerControl()
    {
        using var bridge = new NativeApiBridge();
        var prepared = bridge.PrepareChat("{\"message\":\"digite: teste controlado\"}");
        var result = Json(bridge.TryComputerChat(prepared)!);
        Assert.True(result.GetProperty("computer_action").GetBoolean());
        Assert.Contains("Controle do Proprietario", result.GetProperty("answer").GetString());
        Assert.Null(bridge.TryComputerChat(bridge.PrepareChat("{\"message\":\"qual e a capital do Brasil?\"}")));
    }

    [Fact]
    public async Task DesktopControlPipeIgnoresConversationAndRoutesPhysicalCommands()
    {
        using var bridge = new NativeApiBridge();
        using var server = new DesktopControlIpcServer(bridge);
        server.Start();

        JsonElement normal = await SendDesktopControlAsync("converse comigo sobre musica");
        Assert.False(normal.GetProperty("handled").GetBoolean());

        JsonElement physical = await SendDesktopControlAsync("digite: teste pelo celular");
        Assert.True(physical.GetProperty("computer_action").GetBoolean());
        Assert.Contains("Controle do Proprietario", physical.GetProperty("answer").GetString());
    }

    private static async Task<JsonElement> SendDesktopControlAsync(string message)
    {
        using var client = new NamedPipeClientStream(".", DesktopControlIpcServer.PipeName, PipeDirection.InOut);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(timeout.Token);
        await MikeIpcProtocol.WriteMessageAsync(client, JsonSerializer.Serialize(new { message }));
        string response = await MikeIpcProtocol.ReadMessageAsync(client, timeout.Token);
        return JsonDocument.Parse(response).RootElement.Clone();
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    private static JsonElement? PropertyElement(object value, string name) { var j = Json(value); return j.TryGetProperty(name, out var p) ? p : null; }
    private static bool Property(object value, string name) => PropertyElement(value, name)?.GetBoolean() == true;
}

public sealed class SelfCodeEvolutionExecutorTests
{
    [Fact]
    public async Task StagesTestsPromotesAndRollsBackWithOwnerConfirmation()
    {
        string root = Path.Combine(Path.GetTempPath(), "mike-self-code-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "repo");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(Path.Combine(workspace, "src", "Mike.Tests"));
        string readme = Path.Combine(workspace, "README.md");
        await File.WriteAllTextAsync(readme, "before");
        await File.WriteAllTextAsync(Path.Combine(workspace, "src", "Mike.Tests", "Mike.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Run("git", "init", workspace);
        Run("git", "config user.email mike@test.local", workspace);
        Run("git", "config user.name Mike", workspace);
        Run("git", "add -f .", workspace);
        Run("git", "commit -m baseline", workspace);

        var plan = new SelfCodeChangePlan("candidate123", workspace, [new SelfCodeFileChange(
            "README.md", Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(readme))),
            Convert.ToBase64String(Encoding.UTF8.GetBytes("after"))) ]);
        var executor = new SelfCodeEvolutionExecutor(state);
        try
        {
            SelfCodeStageResult staged = await executor.StageAndValidateAsync(plan);
            Assert.True(staged.Ok, staged.Log);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.PromoteAsync(plan, false));
            await executor.PromoteAsync(plan, true);
            Assert.Equal("after", await File.ReadAllTextAsync(readme));
            executor.Rollback(plan);
            Assert.Equal("before", await File.ReadAllTextAsync(readme));
        }
        finally
        {
            try { await executor.DiscardStageAsync(plan); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Run(string file, string arguments, string cwd)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}

public sealed class UpdateManagerTests
{
    [Fact]
    public void ManifestMustUseMinikeHttpsAndValidHash()
    {
        Assert.True(UpdateManager.ValidateManifest(new UpdateManifest("1.0.11", "https://minike.com.br/local-ai/setup.exe", new string('A', 64)), out var version));
        Assert.Equal(new Version(1, 0, 11), version);
        Assert.False(UpdateManager.ValidateManifest(new UpdateManifest("1.0.12", "http://example.com/setup.exe", new string('A', 64)), out _));
        Assert.False(UpdateManager.ValidateManifest(new UpdateManifest("bad", "https://minike.com.br/setup.exe", "xyz"), out _));
    }

    [Fact]
    public async Task VerifiedPackageIsStagedAndBadHashIsRejected()
    {
        byte[] package = "verified-mike-installer"u8.ToArray();
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package));
        string root = Path.Combine(Path.GetTempPath(), "mike-updater-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            HttpClient Client(string advertisedHash) => new(new FakeUpdateHandler(package, advertisedHash));
            var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            var updater = new UpdateManager(logger, stateRoot: root, currentVersion: new Version(1, 0, 37),
                clientFactory: () => Client(hash), launchUpdates: false);
            await updater.CheckForUpdatesAsync();
            using (JsonDocument status = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "status.json"))))
                Assert.Equal("ready", status.RootElement.GetProperty("status").GetString());
            Assert.True(UpdateManager.HashMatches(Path.Combine(root, "MikeLocal-1.0.41.exe"), hash));

            var rejected = new UpdateManager(logger, stateRoot: root, currentVersion: new Version(1, 0, 37),
                clientFactory: () => Client(new string('A', 64)), launchUpdates: false);
            await rejected.CheckForUpdatesAsync();
            using JsonDocument failed = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "status.json")));
            Assert.Equal("failed", failed.RootElement.GetProperty("status").GetString());
            Assert.False(File.Exists(Path.Combine(root, "MikeLocal-1.0.41.exe.part")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeUpdateHandler(byte[] package, string hash) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                string manifest = JsonSerializer.Serialize(new UpdateManifest("1.0.41", "https://minike.com.br/local-ai/MikeLocalSetup.exe", hash));
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(manifest) });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        }
    }
}

public sealed class AutonomousEvolutionTests
{
    [Fact]
    public void ReversibleImprovementPromotesAfterThreeSafeBetterEvaluations()
    {
        string root = Path.Combine(Path.GetTempPath(), "mike-evolution-" + Guid.NewGuid().ToString("N"));
        try
        {
            var evolution = new AutonomousEvolution(root);
            EvolutionCandidate candidate = evolution.Propose(
                "translation-specialist", "translation", "Improve OCR selection",
                EvolutionRisk.Reversible, 0.70);
            for (int index = 0; index < 3; index++)
                candidate = evolution.Evaluate(candidate.Id,
                    new EvolutionEvaluation(0.90, 0.90, 1.0, 0.80, DateTimeOffset.UtcNow));
            Assert.Equal("promoted", candidate.Status);
            Assert.NotNull(candidate.PromotedAt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AdministrativeImprovementNeverPromotesAutomaticallyAndCanRollback()
    {
        string root = Path.Combine(Path.GetTempPath(), "mike-evolution-" + Guid.NewGuid().ToString("N"));
        try
        {
            var evolution = new AutonomousEvolution(root);
            EvolutionCandidate candidate = evolution.Propose(
                "fleet-administrator", "fleet", "Install a remote printer",
                EvolutionRisk.Administrative, 0.50);
            for (int index = 0; index < 4; index++)
                candidate = evolution.Evaluate(candidate.Id,
                    new EvolutionEvaluation(1, 1, 1, 1, DateTimeOffset.UtcNow));
            Assert.Equal("quarantine", candidate.Status);
            Assert.Equal("rolled_back", evolution.Rollback(candidate.Id, "owner request").Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void RepeatedMeasuredOutcomesFeedAndPromoteDomainCandidate()
    {
        string root = Path.Combine(Path.GetTempPath(), "mike-evolution-" + Guid.NewGuid().ToString("N"));
        try
        {
            var evolution = new AutonomousEvolution(root);
            EvolutionCandidate candidate = null!;
            for (int index = 0; index < 3; index++)
                candidate = evolution.RecordOutcome("programming-specialist", "programming-tests",
                    "Patch compiled and tests passed", true, 0.90, 0.90, 1.0, 0.80);
            Assert.Equal("promoted", candidate.Status);
            Assert.Equal(3, candidate.Evaluations.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

