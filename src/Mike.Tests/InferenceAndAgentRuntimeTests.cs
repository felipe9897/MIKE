using System.Net;
using System.Net.Http;
using System.Text;
using Mike.Common;
using Xunit;

namespace Mike.Service.Tests;

public sealed class InferenceAndAgentRuntimeTests
{
    [Fact]
    public async Task OllamaProviderPostsNonStreamingRequestAndReadsResponse()
    {
        string? request = null;
        using var client = new HttpClient(new DelegateHandler(message =>
        {
            if (message.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"models\":[{\"name\":\"mike-test\"}]}", Encoding.UTF8, "application/json")
                };
            request = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"response\":\"resposta local\",\"done\":true}", Encoding.UTF8, "application/json")
            };
        }));
        var provider = new OllamaProvider(client);

        var response = await provider.GenerateAsync("olá", new ModelMetadata
        {
            Name = "mike-test",
            Config = new Dictionary<string, string> { ["endpoint"] = "http://127.0.0.1:11434/api/generate" }
        }, CancellationToken.None);

        Assert.Equal("resposta local", response);
        Assert.Contains("\"stream\":false", request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mike-test", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentRuntimeExecutesStructuredToolCallThenReturnsFinalAnswer()
    {
        var responses = new Queue<string>(new[]
        {
            "{\"tool\":\"ping\",\"arguments\":\"ok\"}",
            "Tudo certo."
        });
        var provider = new QueueProvider(responses);
        var broker = new ToolBroker();
        var executed = 0;
        broker.RegisterTool(new ToolDefinition
        {
            Id = "ping",
            RequiredLevel = PermissionLevel.R0,
            Action = args => { executed++; return Task.FromResult("pong:" + args); }
        });
        var store = new ModelStore(Path.Combine(Path.GetTempPath(), "mike-test-" + Guid.NewGuid().ToString("N")));
        var runtime = new MikeAgentRuntime(provider, broker, store);

        var answer = await runtime.ExecuteLoopAsync("teste", PermissionLevel.R0, 3);

        Assert.Equal("Tudo certo.", answer);
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task AgentRuntimeExecutesHermesJsonPrefixedToolCall()
    {
        var responses = new Queue<string>(new[]
        {
            "JSON{\"tool\":\"system_info\",\"arguments\":\"{'showall': false}\",\"name\":\"system_info\"}"
        });
        var provider = new QueueProvider(responses);
        var broker = new ToolBroker();
        var executed = 0;
        broker.RegisterTool(new ToolDefinition
        {
            Id = "system_info",
            RequiredLevel = PermissionLevel.R0,
            Action = args => { executed++; return Task.FromResult("Machine: TEST-PC"); }
        });
        var store = new ModelStore(Path.Combine(Path.GetTempPath(), "mike-test-" + Guid.NewGuid().ToString("N")));
        var runtime = new MikeAgentRuntime(provider, broker, store);

        var answer = await runtime.ExecuteLoopAsync("informe o nome no meu computador", PermissionLevel.R0, 3);

        Assert.Equal("Machine: TEST-PC", answer);
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task AgentRuntimeConstrainsComputerWorkToRegisteredTools()
    {
        var provider = new CapturingProvider("Hermes", "Tarefa concluída e testada.");
        var runtime = new MikeAgentRuntime(provider, new ToolBroker(),
            new ModelStore(Path.Combine(Path.GetTempPath(), "mike-test-" + Guid.NewGuid().ToString("N"))));

        var answer = await runtime.ExecuteLoopAsync("crie um projeto no meu computador", PermissionLevel.R1);

        Assert.Contains("não selecio", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Available tools are EXACTLY", provider.LastPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never invent tool IDs", provider.LastPrompt, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class QueueProvider : IMikeInferenceProvider
    {
        private readonly Queue<string> _responses;
        public QueueProvider(Queue<string> responses) => _responses = responses;
        public string ProviderName => "Test";
        public Task<string> GenerateAsync(string prompt, ModelMetadata? model, CancellationToken token)
            => Task.FromResult(_responses.Dequeue());
    }

    private sealed class CapturingProvider : IMikeInferenceProvider
    {
        private readonly string response;
        public CapturingProvider(string name, string response) { ProviderName = name; this.response = response; }
        public string ProviderName { get; }
        public string LastPrompt { get; private set; } = "";
        public Task<string> GenerateAsync(string prompt, ModelMetadata? model, CancellationToken token)
        { LastPrompt = prompt; return Task.FromResult(response); }
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
