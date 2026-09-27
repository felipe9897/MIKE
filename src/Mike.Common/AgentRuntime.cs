using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mike.Common
{
    public class AgentRequest
    {
        public string Prompt { get; set; } = string.Empty;
        public List<string> ContextIds { get; set; } = new();
        public Dictionary<string, string> Parameters { get; set; } = new();
    }


    public class AgentResponse
    {
        public string Content { get; set; } = string.Empty;
        public List<ToolCall> ToolCalls { get; set; } = new();
        public bool StopReasonIsTool { get; set; }
    }


    public class ToolCall
    {
        public string ToolId { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string CallId { get; set; } = string.Empty;
    }

    public class MikeAgentRuntime
    {
        private readonly IMikeInferenceProvider _inference;
        private readonly ToolBroker _toolBroker;
        private readonly ModelStore _modelStore;

        public MikeAgentRuntime(IMikeInferenceProvider inference, ToolBroker toolBroker, ModelStore modelStore)
        {
            _inference = inference;
            _toolBroker = toolBroker;
            _modelStore = modelStore;
        }

        public async Task<string> ExecuteLoopAsync(string userPrompt, PermissionLevel userLevel, int maxIterations = 5, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
                return JsonSerializer.Serialize(new { type = "error", message = "Prompt cannot be empty." });
            if (userPrompt.Length > MikeIpcProtocol.MaxPayloadSize)
                return JsonSerializer.Serialize(new { type = "error", message = "Prompt exceeds the maximum size." });
            string normalized = CurrentUserMessage(userPrompt).ToLowerInvariant();
            if ((normalized.Contains("nome deste computador") || normalized.Contains("nome do computador") || normalized.Contains("hostname")))
            {
                return await _toolBroker.ExecuteToolAsync("sys_info", "", userLevel);
            }
            foreach (var app in new[] { (Signal: "calculadora", Argument: "calculator"), (Signal: "bloco de notas", Argument: "notepad"), (Signal: "explorador", Argument: "explorer"), (Signal: "paint", Argument: "paint") })
            {
                if ((normalized.Contains("abra") || normalized.Contains("abrir")) && normalized.Contains(app.Signal))
                    return await _toolBroker.ExecuteToolAsync("launch_app", app.Argument, userLevel);
            }
            if (!LooksLikeComputerAction(userPrompt))
            {
                var direct = await _inference.GenerateAsync(
                    "Você é Mike Local. Responda diretamente em português brasileiro, sem JSON, sem chamar ferramentas e sem inventar comandos.\n\nUsuário: " + userPrompt,
                    null, token);
                if (!TryExtractToolCall(direct, out var unexpectedCall)) return direct;
                // A local model may still emit a valid structured tool request
                // despite the direct-answer prompt. Execute only registered
                // tools through the broker, preserving permission checks.
                string result = await _toolBroker.ExecuteToolAsync(unexpectedCall.ToolId, unexpectedCall.Arguments, userLevel);
                string followup = userPrompt + $"\nTool result for {unexpectedCall.ToolId}: {result}";
                return await _inference.GenerateAsync(BuildPrompt(followup), _modelStore.GetDefaultModel(_inference.ProviderName), token);
            }
            maxIterations = Math.Clamp(maxIterations, 1, 20);
            string currentContext = userPrompt;
            for (int i = 0; i < maxIterations; i++)
            {
                token.ThrowIfCancellationRequested();
                var model = _modelStore.GetDefaultModel(_inference.ProviderName);
                var response = await _inference.GenerateAsync(BuildPrompt(currentContext), model, token);

                if (TryExtractToolCall(response, out var call))
                {
                    string result = await _toolBroker.ExecuteToolAsync(call.ToolId, call.Arguments, userLevel);
                    if (call.ToolId is "sys_info" or "system_info" && TryReadSuccessfulResult(result, out var directResult))
                        return directResult;
                    currentContext += $"\nTool result for {call.ToolId}: {result}";
                }
                else
                {
                    if (!currentContext.Contains("Tool result for ", StringComparison.Ordinal))
                        return "Não executei nenhuma ação porque o modelo não selecionou uma ferramenta válida. Reformule o pedido ou use uma ação disponível; nada foi alterado no computador.";
                    return response;
                }
            }
            return "Maximum agent iterations reached without final answer.";
        }

        private static bool TryReadSuccessfulResult(string json, out string result)
        {
            result = string.Empty;
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "success" &&
                    root.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    result = value.GetString() ?? string.Empty;
                    return result.Length > 0;
                }
            }
            catch (JsonException) { }
            return false;
        }

        private static bool LooksLikeComputerAction(string prompt)
        {
            string value = CurrentUserMessage(prompt).ToLowerInvariant();
            string[] signals =
            {
                "no meu pc", "no meu computador", "no computador", "arquivo", "pasta", "instale", "instalar",
                "execute", "executar", "abra o", "abrir o", "terminal", "powershell", "cmd",
                "apague", "remova", "mova", "copie", "renomeie", "crie um projeto", "corrija o projeto"
            };
            return signals.Any(value.Contains);
        }

        private static string CurrentUserMessage(string prompt)
        {
            const string marker = "[CURRENT_USER_MESSAGE]";
            int index = prompt.LastIndexOf(marker, StringComparison.Ordinal);
            return index >= 0 ? prompt[(index + marker.Length)..].Trim() : prompt;
        }

        private static string BuildPrompt(string context)
        {
            return "You are Mike Local. Answer the user directly. Available tools are EXACTLY: " +
                "sys_info(arguments empty), list_files(arguments path), create_folder(arguments path), " +
                "launch_app(arguments one of calculator, notepad, explorer, paint). " +
                "If a local tool is needed, output ONLY JSON with fields tool (string) and arguments (string). " +
                "Never describe an action as completed before receiving its Tool result. Never invent tool IDs. " +
                "Otherwise output the final answer as plain text.\n\nConversation:\n" + context;
        }

        private static bool TryExtractToolCall(string response, out ToolCall call)
        {
            call = new ToolCall();
            if (string.IsNullOrWhiteSpace(response)) return false;

            // Preferred format: a JSON object. Fenced JSON is accepted because local models commonly use it.
            var candidate = response.Trim();
            // Some local agent models prefix an otherwise valid object with
            // "JSON" (for example: JSON{"tool":...}). Accept only the first
            // complete-looking object; ToolBroker still enforces the registry
            // and permission policy before anything can run.
            if (!candidate.StartsWith('{'))
            {
                var objectStart = candidate.IndexOf('{');
                var objectEnd = candidate.LastIndexOf('}');
                if (objectStart >= 0 && objectEnd > objectStart)
                    candidate = candidate[objectStart..(objectEnd + 1)].Trim();
            }
            if (candidate.StartsWith("```") && candidate.EndsWith("```", StringComparison.Ordinal))
            {
                var firstNewLine = candidate.IndexOf('\n');
                candidate = firstNewLine >= 0 ? candidate[(firstNewLine + 1)..^3].Trim() : string.Empty;
            }
            try
            {
                using var document = JsonDocument.Parse(candidate);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String)
                {
                    call.ToolId = tool.GetString() ?? string.Empty;
                    if (root.TryGetProperty("arguments", out var arguments))
                        call.Arguments = arguments.ValueKind == JsonValueKind.String
                            ? arguments.GetString() ?? string.Empty
                            : arguments.GetRawText();
                    return !string.IsNullOrWhiteSpace(call.ToolId);
                }
            }
            catch (JsonException)
            {
                // Fall through to the legacy marker only for compatibility with existing local prompts.
            }

            const string marker = "[TOOL_CALL:";
            var start = response.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return false;
            var end = response.IndexOf(']', start + marker.Length);
            if (end < 0) return false;
            var parts = response[(start + marker.Length)..end].Split('|', 2);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) return false;
            call.ToolId = parts[0].Trim();
            call.Arguments = parts.Length == 2 ? parts[1] : string.Empty;
            return true;
        }
    }
}
