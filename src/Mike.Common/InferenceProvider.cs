using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mike.Common
{
    public interface IMikeInferenceProvider
    {
        Task<string> GenerateAsync(string prompt, ModelMetadata? model, CancellationToken token);
        string ProviderName { get; }
    }

    public sealed class HermesProvider : IMikeInferenceProvider
    {
        private readonly HermesAgentClient _client = new();
        public string ProviderName => "Hermes";

        public async Task<string> GenerateAsync(string prompt, ModelMetadata? model, CancellationToken token)
        {
            if (!_client.IsAvailable(out var reason))
                throw new InvalidOperationException(reason);
            if (!LooksAgentic(prompt))
                throw new InvalidOperationException("Hermes reserved for computer actions and multi-step agent work.");
            return await _client.SendAsync(prompt, TimeSpan.FromMinutes(5), token);
        }

        private static bool LooksAgentic(string prompt)
        {
            string value = prompt.ToLowerInvariant();
            string[] signals = { "no meu pc", "computador", "arquivo", "pasta", "instal", "execut", "terminal", "powershell", "cmd", "abrir", "criar projeto", "corrigir projeto", "automat", "pesquis", "planej" };
            return signals.Any(value.Contains) || value.Length > 900;
        }
    }

    public class InferenceRouter : IMikeInferenceProvider
    {
        private readonly IMikeInferenceProvider _primary;
        private readonly IMikeInferenceProvider _fallback;
        private readonly ModelStore _modelStore;

        public InferenceRouter(IMikeInferenceProvider primary, IMikeInferenceProvider fallback, ModelStore modelStore)
        {
            _primary = primary;
            _fallback = fallback;
            _modelStore = modelStore;
        }

        public string ProviderName => $"Router({_primary.ProviderName} -> {_fallback.ProviderName})";

        public async Task<string> GenerateAsync(string prompt, ModelMetadata? model, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Prompt cannot be empty.", nameof(prompt));
            try
            {
                // Use the provided model if it matches primary, else get primary's default
                var primaryModel = (model?.Provider == _primary.ProviderName) ? model : _modelStore.GetDefaultModel(_primary.ProviderName);
                return await _primary.GenerateAsync(prompt, primaryModel, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            {
                Console.WriteLine($"Primary provider {_primary.ProviderName} failed: {ex.Message}. Falling back to {_fallback.ProviderName}...");
                var fallbackModel = (model?.Provider == _fallback.ProviderName) ? model : _modelStore.GetDefaultModel(_fallback.ProviderName);
                return await _fallback.GenerateAsync(prompt, fallbackModel, token);
            }
        }
    }

    public class OllamaProvider : IMikeInferenceProvider
    {
        public string ProviderName => "Ollama";
        private readonly HttpClient _httpClient;

        public OllamaProvider(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public async Task<string> GenerateAsync(string prompt, ModelMetadata? model, CancellationToken token)
        {
            string modelName = model?.Name ?? "qwen2.5:1.5b";
            string endpoint = model?.Config.GetValueOrDefault("endpoint", "http://127.0.0.1:11434/api/generate")
                ?? "http://127.0.0.1:11434/api/generate";
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Ollama endpoint is invalid.");

            modelName = await SelectInstalledModelAsync(prompt, modelName, endpointUri, token);


            var payload = new { model = modelName, prompt, stream = false };
            var content = JsonSerializer.Serialize(payload);
            using var response = await _httpClient.PostAsync(endpointUri,
                new StringContent(content, Encoding.UTF8, "application/json"), token);

            response.EnsureSuccessStatusCode();
            var resultJson = await response.Content.ReadAsStringAsync(token);

            using var doc = JsonDocument.Parse(resultJson);
            if (!doc.RootElement.TryGetProperty("response", out var result) || result.ValueKind != JsonValueKind.String)
                throw new JsonException("Ollama response did not contain response text.");
            return result.GetString() ?? string.Empty;
        }

        private async Task<string> SelectInstalledModelAsync(string prompt, string fallback, Uri generateUri, CancellationToken token)
        {
            try
            {
                var tagsUri = new Uri(generateUri, "/api/tags");
                using var response = await _httpClient.GetAsync(tagsUri, token);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var installed = doc.RootElement.GetProperty("models").EnumerateArray()
                    .Select(item => item.GetProperty("name").GetString())
                    .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray();
                if (installed.Length == 0) return fallback;

                string text = prompt.ToLowerInvariant();
                bool coding = new[] { "código", "codigo", "program", "bug", "script", "sql", "html", "javascript", "python", "powershell" }.Any(text.Contains);
                bool reasoning = new[] { "racioc", "analise", "análise", "compare", "estratég", "estrateg", "calcule", "planej" }.Any(text.Contains);
                string[] preferences = coding
                    ? new[] { "coder", "qwen2.5", "llama3.2" }
                    : reasoning ? new[] { "deepseek-r1", "qwen", "llama3.2" }
                    : new[] { "llama3.2", "qwen2.5", "qwen" };
                foreach (string preference in preferences)
                {
                    string? match = installed.FirstOrDefault(name => name.Contains(preference, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) return match;
                }
                return installed.FirstOrDefault(name => name.Equals(fallback, StringComparison.OrdinalIgnoreCase)) ?? installed[0];
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                return fallback;
            }
        }
    }

    public class LlamaCppProvider : IMikeInferenceProvider
    {
        public string ProviderName => "LlamaCpp";
        private readonly HttpClient _httpClient;

        public LlamaCppProvider(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        public async Task<string> GenerateAsync(string prompt, ModelMetadata? model, CancellationToken token)
        {
            string endpoint = model?.Config.GetValueOrDefault("endpoint", "http://127.0.0.1:8080/completion")
                ?? "http://127.0.0.1:8080/completion";
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) || endpointUri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("llama.cpp endpoint is invalid.");


            var payload = new {
                prompt = prompt,
                n_predict = 512,
                stream = false
            };
            var content = JsonSerializer.Serialize(payload);

            using var response = await _httpClient.PostAsync(endpointUri,
                new StringContent(content, Encoding.UTF8, "application/json"), token);

            response.EnsureSuccessStatusCode();
            var resultJson = await response.Content.ReadAsStringAsync(token);

            using var doc = JsonDocument.Parse(resultJson);
            if (!doc.RootElement.TryGetProperty("content", out var result) || result.ValueKind != JsonValueKind.String)
                throw new JsonException("llama.cpp response did not contain content text.");
            return result.GetString() ?? string.Empty;
        }
    }
}
