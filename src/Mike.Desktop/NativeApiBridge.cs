using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Mike.Common;

namespace Mike.Desktop;

public sealed class NativeApiBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "data");
    private readonly string auditFile;
    private readonly object conversationGate = new();
    private readonly MeshManager mesh = new();
    private readonly LanDiscoveryService? discovery;
    private readonly AutonomousEvolution evolution;
    private readonly SelfCodeEvolutionExecutor selfCode;
    private string? legacyToken;

    public NativeApiBridge()
    {
        Directory.CreateDirectory(dataRoot);
        auditFile = Path.Combine(dataRoot, "audit.jsonl");
        evolution = new AutonomousEvolution(Path.Combine(dataRoot, "evolution"));
        selfCode = new SelfCodeEvolutionExecutor(Path.Combine(dataRoot, "evolution", "self-code"));
        try { discovery = LanDiscoveryService.TryCreateFromEnvironment(mesh); discovery?.Start(); } catch { }
    }

    public async Task<object> HandleAsync(string pathAndQuery, string method, string? body)
    {
        var uri = new Uri("http://mike.local" + pathAndQuery);
        string path = uri.AbsolutePath;
        var query = ParseQuery(uri.Query);
        return path switch
        {
            "/ui/status" => new { ok = true, online = true, native_app = true, version = typeof(NativeApiBridge).Assembly.GetName().Version?.ToString(3) ?? "1.0.42", model = "Mike Local Â· roteamento automÃ¡tico", provider = "Hermes/Ollama", backend = "named-pipe" },
            "/conversations" => ListConversations(query.GetValueOrDefault("q", "")),
            "/conversations/get" => GetConversation(query.GetValueOrDefault("id", "")),
            "/conversations/rename" when method == "POST" => RenameConversation(body),
            "/conversations/delete" when method == "POST" => DeleteConversation(body),
            "/models" => await ModelsAsync(),
            "/memory" => new { ok = true, notes = ReadList<MemoryNote>("memory.json") },
            "/memory/search" => SearchMemory(query.GetValueOrDefault("q", "")),
            "/memory/get" => GetMemory(query.GetValueOrDefault("id", "")),
            "/memory/save" when method == "POST" => SaveMemory(body),
            "/memory/delete" when method == "POST" => DeleteMemory(body),
            "/memory/graph" => new { ok = true, nodes = ReadList<MemoryNote>("memory.json"), edges = Array.Empty<object>() },
            "/memory/preferences" when method == "POST" => SavePreferences(body),
            "/memory/preferences" => ReadObject("memory-preferences.json", new { preferences = new { } }),
            "/memory/vector/status" => new { ok = true, active = false, backend = "lexical", note_count = ReadList<MemoryNote>("memory.json").Count },
            "/memory/vector/rebuild" => new { ok = true, backend = "lexical", message = "Ãndice lexical atualizado." },
            "/files" => ListFiles(query.GetValueOrDefault("path", "")),
            "/files/preview" => FileMetadata(query.GetValueOrDefault("path", "")),
            "/files/action" when method == "POST" => FileAction(body),
            "/agents" => AgentCatalog(),
            "/learning/proposals" => new { ok = true, proposals = ReadList<LearningProposal>("learning-proposals.json") },
            "/learning/history" => LearningHistory(),
            "/learning/propose" when method == "POST" => ProposeLearning(body),
            "/learning/approve" when method == "POST" => ApproveLearning(body),
            "/learning/rollback" when method == "POST" => RollbackLearning(body),
            "/learning/research" when method == "POST" => await ResearchAndLearnAsync(body),
            "/evolution/automatic" => EvolutionStatus(),
            "/evolution/automatic/propose" when method == "POST" => EvolutionPropose(body),
            "/evolution/automatic/evaluate" when method == "POST" => EvolutionEvaluate(body),
            "/evolution/automatic/rollback" when method == "POST" => EvolutionRollback(body),
            "/evolution/automatic/outcome" when method == "POST" => EvolutionOutcome(body),
            "/evolution/self-code/stage" when method == "POST" => await SelfCodeStageAsync(body),
            "/evolution/self-code/promote" when method == "POST" => await SelfCodePromoteAsync(body),
            "/evolution/self-code/rollback" when method == "POST" => SelfCodeRollback(body),
            "/admin/diagnostic" when method == "POST" => await AdminDiagnosticAsync(body),
            "/owner-control/status" => OwnerControlStatus(),
            "/owner-control/enable" when method == "POST" => EnableOwnerControl(body),
            "/owner-control/revoke" when method == "POST" => RevokeOwnerControl(),
            "/computer/observe" => WindowsComputerOperator.Observe(),
            "/computer/action" when method == "POST" => ComputerAction(body),
            "/peers" => PeerStatus(),
            "/compute-cluster" => ClusterStatus(),
            "/manga/status" => MangaStatus(),
            "/manga/search" when method == "POST" => await SearchMangaAsync(body),
            "/manga/start" when method == "POST" => await StartMangaAsync(body),
            "/translation/start" when method == "POST" => StartTranslation(body),
            "/translation/status" when method == "POST" => TranslationStatus(body),
            "/system/theme" when method == "POST" => ApplyWindowsTheme(body),
            "/projects" => new { ok = true, projects = Array.Empty<object>() },
            _ => await ProxyLegacyAsync(pathAndQuery, method, body)
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    private object ApplyWindowsTheme(string? body)
    {
        var request = Deserialize<ThemeRequest>(body) ?? new();
        string snapshot = Path.Combine(dataRoot, "windows-theme-backup.json");
        string wallpaper = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "assets", "mike-wallpaper-minike-20260719.png"));
        using var personalize = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", true);
        using var dwm = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\DWM", true);
        using var desktop = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop", true);
        if (!File.Exists(snapshot))
            File.WriteAllText(snapshot, JsonSerializer.Serialize(new ThemeSnapshot {
                AppsUseLightTheme = RegistryInt(personalize, "AppsUseLightTheme"), SystemUsesLightTheme = RegistryInt(personalize, "SystemUsesLightTheme"),
                ColorizationColor = RegistryInt(dwm, "ColorizationColor"), AccentColor = RegistryInt(dwm, "AccentColor"), ColorPrevalence = RegistryInt(dwm, "ColorPrevalence"),
                Wallpaper = desktop?.GetValue("Wallpaper")?.ToString()
            }, JsonOptions));
        if (request.Action == "status") return new { ok = true, mode = Convert.ToInt32(personalize?.GetValue("AppsUseLightTheme", 0)) == 1 ? "light" : "dark", wallpaperAvailable = File.Exists(wallpaper), userSession = true };
        if (request.Action == "mode") { int value = request.Mode == "light" ? 1 : 0; personalize?.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord); personalize?.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord); }
        else if (request.Action == "accent") { dwm?.SetValue("ColorizationColor", unchecked((int)0xFF46D6FF), RegistryValueKind.DWord); dwm?.SetValue("AccentColor", unchecked((int)0xFFFFD646), RegistryValueKind.DWord); dwm?.SetValue("ColorPrevalence", 1, RegistryValueKind.DWord); }
        else if (request.Action == "wallpaper-apply") { if (!File.Exists(wallpaper)) return new { ok = false, error = "Wallpaper Minike ausente no instalador." }; if (!SystemParametersInfo(20, 0, wallpaper, 3)) return new { ok = false, error = "O Windows recusou a troca do papel de parede." }; }
        else if (request.Action is "restore" or "wallpaper-restore") {
            if (!File.Exists(snapshot)) return new { ok = false, error = "Backup do tema anterior nÃ£o foi encontrado." };
            var saved = JsonSerializer.Deserialize<ThemeSnapshot>(File.ReadAllText(snapshot), JsonOptions) ?? new();
            if (request.Action == "restore") { RestoreRegistry(personalize, "AppsUseLightTheme", saved.AppsUseLightTheme); RestoreRegistry(personalize, "SystemUsesLightTheme", saved.SystemUsesLightTheme); RestoreRegistry(dwm, "ColorizationColor", saved.ColorizationColor); RestoreRegistry(dwm, "AccentColor", saved.AccentColor); RestoreRegistry(dwm, "ColorPrevalence", saved.ColorPrevalence); }
            if (!string.IsNullOrWhiteSpace(saved.Wallpaper) && File.Exists(saved.Wallpaper)) SystemParametersInfo(20, 0, saved.Wallpaper, 3);
            if (request.Action == "restore") File.Delete(snapshot);
        } else return new { ok = false, error = "AÃ§Ã£o de tema desconhecida." };
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "ImmersiveColorSet", 2, 2000, out _);
        Audit("windows.theme", request.Action);
        return new { ok = true, action = request.Action, userSession = true, reversible = true, note = "Tema aplicado ao usuÃ¡rio conectado no Windows 11." };
    }

    private static void RestoreRegistry(RegistryKey? key, string name, object? value) { if (key is null) return; if (value is null) key.DeleteValue(name, false); else key.SetValue(name, value, RegistryValueKind.DWord); }
    private static int? RegistryInt(RegistryKey? key, string name) { object? value = key?.GetValue(name); return value is null ? null : Convert.ToInt32(value); }

    private async Task<object> ProxyLegacyAsync(string pathAndQuery, string method, string? body)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            legacyToken ??= await DiscoverLegacyTokenAsync(http);
            using var request = new HttpRequestMessage(new HttpMethod(method), "http://127.0.0.1:47885" + pathAndQuery);
            if (!string.IsNullOrWhiteSpace(legacyToken)) request.Headers.TryAddWithoutValidation("X-Mike-Token", legacyToken);
            if (method is "POST" or "PUT" or "PATCH") request.Content = new StringContent(body ?? "{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            string payload = await response.Content.ReadAsStringAsync();
            if (payload.Length > 8 * 1024 * 1024) return new { ok = false, error = "Resposta grande demais para a ponte nativa.", path = pathAndQuery };
            try { using var document = JsonDocument.Parse(payload); return document.RootElement.Clone(); }
            catch { return new { ok = response.IsSuccessStatusCode, status = (int)response.StatusCode, content = payload }; }
        }
        catch (Exception ex)
        {
            return new { ok = false, unavailable = true, error = "MÃ³dulo ainda estÃ¡ sendo preparado: " + ex.Message, path = pathAndQuery };
        }
    }

    private static async Task<string> DiscoverLegacyTokenAsync(HttpClient http)
    {
        string html = await http.GetStringAsync("http://127.0.0.1:47885/ui");
        var match = System.Text.RegularExpressions.Regex.Match(html, "var localApiToken='([0-9a-fA-F]+)'");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private object OwnerControlStatus()
    {
        bool elevated = IsAdministrator();
        long untilUnix = ReadOwnerControlUntil();
        bool active = elevated && untilUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new { ok = true, active, elevated, level = active ? "R3" : "R1", expires_at = active ? DateTimeOffset.FromUnixTimeSeconds(untilUnix) : (DateTimeOffset?)null, destructive_requires_confirmation = true };
    }

    private object EnableOwnerControl(string? body)
    {
        var request = Deserialize<OwnerControlRequest>(body) ?? new OwnerControlRequest();
        if (request.Confirm != true) return new { ok = false, confirmation_required = true, error = "Confirme que vocÃª Ã© o dono deste computador." };
        int minutes = Math.Clamp(request.Minutes, 5, 240);
        long until = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();
        if (IsAdministrator())
        {
            File.WriteAllText(Path.Combine(dataRoot, "owner-control-session.json"), JsonSerializer.Serialize(new { until }));
            Audit("owner_control.enable", $"R3 until {until}");
            return new { ok = true, active = true, level = "R3", expires_at = DateTimeOffset.FromUnixTimeSeconds(until) };
        }
        try
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("ExecutÃ¡vel nÃ£o localizado.");
            Process.Start(new ProcessStartInfo(executable, $"--owner-control-until={until}") { UseShellExecute = true, Verb = "runas" });
            Audit("owner_control.uac", "requested");
            _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
            });
            return new { ok = true, restarting = true, message = "Confirme o UAC. A Mike reiniciarÃ¡ com Controle Total do ProprietÃ¡rio." };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Audit("owner_control.uac", "cancelled");
            return new { ok = false, cancelled = true, error = "O UAC foi cancelado; nenhuma permissÃ£o foi liberada." };
        }
    }

    private object RevokeOwnerControl()
    {
        string file = Path.Combine(dataRoot, "owner-control-session.json");
        if (File.Exists(file)) File.Delete(file);
        Audit("owner_control.revoke", "requested");
        return new { ok = true, restart_required = IsAdministrator(), message = "Controle Total revogado. Reinicie a Mike para voltar ao modo normal." };
    }

    private object ComputerAction(string? body)
    {
        long until = ReadOwnerControlUntil();
        if (!IsAdministrator() || until <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return new { ok = false, approval_required = true, error = "Ative temporariamente o Controle do Proprietario." };
        var request = Deserialize<ComputerActionRequest>(body) ?? new();
        if (!WindowsComputerOperator.Validate(request, out string error))
            return new { ok = false, error };
        object result = WindowsComputerOperator.Execute(request);
        Audit("computer." + request.Action, request.Action == "type" ? $"text-length:{request.Text.Length}" : $"x:{request.X};y:{request.Y};window:{request.WindowHandle}");
        return result;
    }

    private long ReadOwnerControlUntil()
    {
        string file = Path.Combine(dataRoot, "owner-control-session.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return document.RootElement.GetProperty("until").GetInt64();
        }
        catch { return 0; }
    }

    private bool HasOwnerControl() => IsAdministrator() && ReadOwnerControlUntil() > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private async Task<object> SelfCodeStageAsync(string? body)
    {
        if (!HasOwnerControl()) return new { ok = false, approval_required = true, error = "Ative temporariamente o Controle do Proprietario para validar codigo proposto." };
        var request = Deserialize<SelfCodeRequest>(body);
        if (request?.Plan is null) return new { ok = false, error = "Plano de alteracao ausente." };
        SelfCodeStageResult result = await selfCode.StageAndValidateAsync(request.Plan);
        Audit("evolution.self_code.stage", $"candidate:{request.Plan.CandidateId};status:{result.Status};files:{result.Files.Count}");
        return result;
    }

    private async Task<object> SelfCodePromoteAsync(string? body)
    {
        if (!HasOwnerControl()) return new { ok = false, approval_required = true, error = "Ative temporariamente o Controle do Proprietario." };
        var request = Deserialize<SelfCodeRequest>(body);
        if (request?.Plan is null || !request.Confirm) return new { ok = false, confirmation_required = true, error = "Confirme explicitamente a promocao do codigo validado." };
        string snapshot = await selfCode.PromoteAsync(request.Plan, ownerConfirmed: true);
        Audit("evolution.self_code.promote", $"candidate:{request.Plan.CandidateId};files:{request.Plan.Changes.Count}");
        return new { ok = true, candidate_id = request.Plan.CandidateId, snapshot };
    }

    private object SelfCodeRollback(string? body)
    {
        if (!HasOwnerControl()) return new { ok = false, approval_required = true, error = "Ative temporariamente o Controle do Proprietario." };
        var request = Deserialize<SelfCodeRequest>(body);
        if (request?.Plan is null || !request.Confirm) return new { ok = false, confirmation_required = true, error = "Confirme explicitamente o rollback." };
        selfCode.Rollback(request.Plan);
        Audit("evolution.self_code.rollback", $"candidate:{request.Plan.CandidateId};files:{request.Plan.Changes.Count}");
        return new { ok = true, candidate_id = request.Plan.CandidateId };
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public PreparedChat PrepareChat(string? body)
    {
        var request = Deserialize<ChatRequest>(body) ?? new ChatRequest();
        string message = request.Message.Trim();
        if (message.Length == 0) throw new ArgumentException("A mensagem nÃ£o pode ser vazia.");
        lock (conversationGate)
        {
            var conversations = ReadList<Conversation>("conversations.json");
            var conversation = conversations.FirstOrDefault(item => item.Id == request.ConversationId);
            if (conversation is null)
            {
                conversation = new Conversation
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = message.Length > 54 ? message[..54] + "â€¦" : message,
                    CreatedAt = DateTimeOffset.Now
                };
                conversations.Insert(0, conversation);
            }
            string context = BuildConversationContext(conversation.Messages);
            conversation.Messages.Add(new ChatMessage { Role = "user", Content = message, At = DateTimeOffset.Now });
            conversation.UpdatedAt = DateTimeOffset.Now;
            Write("conversations.json", conversations.OrderByDescending(item => item.UpdatedAt).ToList());
            string prompt = context.Length == 0 ? message :
                "Use o histÃ³rico abaixo como contexto factual da mesma conversa. NÃ£o repita a apresentaÃ§Ã£o e nÃ£o trate esta mensagem como primeiro contato.\n\n" +
                context + "\n\n[CURRENT_USER_MESSAGE]\n" + message;
            return new PreparedChat(conversation.Id, conversation.Title, prompt, conversation.CatalogId, message);
        }
    }

    public object? TryComputerChat(PreparedChat prepared)
    {
        string message = prepared.CurrentMessage.Trim();
        var click = System.Text.RegularExpressions.Regex.Match(message,
            @"\b(?:clique|clicar)\s+(?:em\s+)?(?:x\s*)?(\d{1,5})\s*[,;x ]\s*(?:y\s*)?(\d{1,5})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        ComputerActionRequest? action = null;
        if (click.Success)
            action = new ComputerActionRequest { Action = "click", X = int.Parse(click.Groups[1].Value), Y = int.Parse(click.Groups[2].Value) };
        else
        {
            var type = System.Text.RegularExpressions.Regex.Match(message, @"^(?:digite|escreva)\s*:\s*(.+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (type.Success) action = new ComputerActionRequest { Action = "type", Text = type.Groups[1].Value.Trim() };
            else
            {
                var key = System.Text.RegularExpressions.Regex.Match(message,
                    @"^(?:aperte|pressione)\s+(enter|escape|tab|backspace|cima|baixo|esquerda|direita|home|end|pageup|pagedown)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (key.Success)
                {
                    var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                        ["cima"]="up", ["baixo"]="down", ["esquerda"]="left", ["direita"]="right"
                    };
                    string value = key.Groups[1].Value;
                    action = new ComputerActionRequest { Action = "key", Text = names.GetValueOrDefault(value, value).ToLowerInvariant() };
                }
            }
        }
        bool observe = System.Text.RegularExpressions.Regex.IsMatch(message,
            @"\b(?:liste|listar|mostre|mostrar|veja|ver)\s+(?:as\s+)?janelas\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (action is null && !observe) return null;

        object raw = observe ? WindowsComputerOperator.Observe() : ComputerAction(JsonSerializer.Serialize(action));
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(raw));
        JsonElement root = document.RootElement;
        bool ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
        string answer;
        if (ok && observe)
        {
            int count = root.TryGetProperty("windows", out var windows) ? windows.GetArrayLength() : 0;
            answer = $"Encontrei {count} janela(s) visivel(is) neste computador.";
        }
        else if (ok) answer = "Acao executada e registrada na auditoria.";
        else if (root.TryGetProperty("approval_required", out var approval) && approval.GetBoolean())
            answer = "Para fazer isso, ative temporariamente o Controle do Proprietario e repita o pedido.";
        else answer = root.TryGetProperty("error", out var error) ? error.GetString() ?? "A acao falhou." : "A acao falhou.";
        CompleteChat(prepared, answer);
        return new { ok, answer, computer_action = true, result = root.Clone() };
    }

    public static bool CanHandleComputerChat(string message) =>
        System.Text.RegularExpressions.Regex.IsMatch(message,
            @"\b(?:clique|clicar)\s+(?:em\s+)?(?:x\s*)?\d{1,5}\s*[,;x ]\s*(?:y\s*)?\d{1,5}\b|^(?:digite|escreva)\s*:|^(?:aperte|pressione)\s+(?:enter|escape|tab|backspace|cima|baixo|esquerda|direita|home|end|pageup|pagedown)$|\b(?:liste|listar|mostre|mostrar|veja|ver)\s+(?:as\s+)?janelas\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public async Task<object?> TryCatalogChatAsync(PreparedChat prepared, string? body)
    {
        Dictionary<string, object?> request;
        try { request = JsonSerializer.Deserialize<Dictionary<string, object?>>(body ?? "{}", JsonOptions) ?? new(); }
        catch { request = new(); }
        request["conversation_id"] = prepared.CatalogId ?? string.Empty;
        string originalMessage = request.TryGetValue("message", out var messageValue) ? Convert.ToString(messageValue) ?? string.Empty : string.Empty;
        bool requiresVerifiedSources = System.Text.RegularExpressions.Regex.IsMatch(originalMessage,
            "\\b(imposto|tribut|fiscal|ncm|icms|ipi|pis|cofins|lucro presumido|legisla[cÃ§][aÃ£]o)\\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var relevantSkills = ReadList<ApprovedSkill>("approved-skills.json")
            .Where(skill => (skill.Sources?.Length ?? 0) >= 2)
            .Where(skill => skill.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(word => word.Length >= 4 && originalMessage.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(skill => skill.ApprovedAt).Take(2).ToList();
        if (relevantSkills.Count > 0)
        {
            string learned = string.Join("\n\n", relevantSkills.Select(skill => $"Skill {skill.Title}:\n{skill.Instructions}"));
            if (learned.Length > 12000) learned = learned[..12000];
            request["message"] = originalMessage + "\n\n[CONHECIMENTO_APRENDIDO_DA_MIKE]\n" + learned;
            request["agent_mode"] = true;
        }
        if (requiresVerifiedSources)
        {
            request["message"] = "Consulte a internet e priorize fontes oficiais atuais. NÃ£o invente URL, NCM, alÃ­quota ou regra fiscal. Explique incertezas e cite links verificÃ¡veis. Pergunta do usuÃ¡rio: " + originalMessage;
            request["response_mode"] = "deep";
            request["agent_mode"] = true;
        }
        object proxied = await ProxyLegacyAsync("/ui/chat", "POST", JsonSerializer.Serialize(request));
        if (proxied is not JsonElement element || element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty("unavailable", out var unavailable) && unavailable.ValueKind == JsonValueKind.True) return null;
        if (!element.TryGetProperty("answer", out var answerElement) || answerElement.ValueKind != JsonValueKind.String) return proxied;

        string answer = answerElement.GetString() ?? string.Empty;
        if (requiresVerifiedSources && (await ExtractVerifiedSourcesAsync(answer)).Length == 0)
            answer = "NÃ£o consegui confirmar essa resposta em uma fonte oficial acessÃ­vel agora. NÃ£o vou inventar NCM, alÃ­quota ou regra fiscal. Informe a descriÃ§Ã£o completa do produto (material, finalidade, faixa etÃ¡ria, composiÃ§Ã£o e uso) e consulte tambÃ©m a Receita Federal/Portal Ãšnico ou seu contador antes de emitir a nota.";
        if (element.TryGetProperty("conversation", out var catalogConversation) &&
            catalogConversation.ValueKind == JsonValueKind.Object &&
            catalogConversation.TryGetProperty("id", out var catalogIdElement))
            SaveCatalogConversationId(prepared.Id, catalogIdElement.GetString());
        CompleteChat(prepared, answer);
        var result = JsonSerializer.Deserialize<Dictionary<string, object?>>(element.GetRawText(), JsonOptions) ?? new();
        result["conversation"] = new { id = prepared.Id, title = prepared.Title };
        result["context_persisted"] = true;
        result["catalog_router"] = true;
        return result;
    }

    public async Task<object?> TryMangaChatAsync(PreparedChat prepared, string? body)
    {
        var request = Deserialize<ChatRequest>(body) ?? new ChatRequest();
        string message = request.Message.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(message, "\\b(traduz|traduzir|traduza|traducao|manga|mangÃ¡|manhwa|manhua|webtoon|quadrinho)\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;
        var urlMatch = System.Text.RegularExpressions.Regex.Match(message, "https?://[^\\s<>\\\"]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!urlMatch.Success)
        {
            string title = ExtractMangaSearchTitle(message);
            if (title.Length < 2) return null;
            object searched = await SearchMangaAsync(JsonSerializer.Serialize(new MangaSearchRequest { Query = title }));
            using var searchDocument = JsonDocument.Parse(JsonSerializer.Serialize(searched));
            JsonElement searchRoot = searchDocument.RootElement;
            bool searchOk = searchRoot.TryGetProperty("ok", out var searchOkElement) && searchOkElement.ValueKind == JsonValueKind.True;
            string searchAnswerText = searchOk && searchRoot.TryGetProperty("answer", out var searchAnswer)
                ? searchAnswer.GetString() ?? "NÃ£o encontrei esse tÃ­tulo nas fontes oficiais configuradas."
                : "NÃ£o consegui pesquisar esse tÃ­tulo agora.";
            CompleteChat(prepared, searchAnswerText);
            return new { ok = searchOk, answer = searchAnswerText, manga_search = searched, conversation = new { id = prepared.Id, title = prepared.Title }, context_persisted = true };
        }
        string url = urlMatch.Value.TrimEnd('.', ',', ';', ')', ']');
        object started = await StartMangaAsync(JsonSerializer.Serialize(new MangaRequest { Url = url }));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(started));
        var root = document.RootElement;
        bool ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
        string answer;
        if (ok)
        {
            string output = root.TryGetProperty("output", out var outputElement) ? outputElement.GetString() ?? "" : "";
            answer = "Comecei a capturar e traduzir o mangÃ¡ pÃ¡gina por pÃ¡gina. O andamento fica salvo e pode ser retomado. " +
                     (string.IsNullOrWhiteSpace(output) ? "A pasta de resultado serÃ¡ aberta automaticamente." : "SaÃ­da: " + output);
        }
        else
        {
            string error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() ?? "fonte nÃ£o acessÃ­vel" : "fonte nÃ£o acessÃ­vel";
            answer = "NÃ£o iniciei a traduÃ§Ã£o: " + error + ". Sites com login, CAPTCHA, DRM ou que proÃ­bem download precisam ser abertos no navegador e importados como imagens/CBZ/PDF.";
        }
        CompleteChat(prepared, answer);
        return new { ok, answer, manga_job = started, conversation = new { id = prepared.Id, title = prepared.Title }, context_persisted = true };
    }

    private void SaveCatalogConversationId(string nativeId, string? catalogId)
    {
        if (string.IsNullOrWhiteSpace(catalogId)) return;
        lock (conversationGate)
        {
            var conversations = ReadList<Conversation>("conversations.json");
            var conversation = conversations.FirstOrDefault(item => item.Id == nativeId);
            if (conversation is null) return;
            conversation.CatalogId = catalogId;
            Write("conversations.json", conversations);
        }
    }

    public object CompleteChat(PreparedChat prepared, string answer)
    {
        lock (conversationGate)
        {
            var conversations = ReadList<Conversation>("conversations.json");
            var conversation = conversations.FirstOrDefault(item => item.Id == prepared.Id);
            if (conversation is not null)
            {
                conversation.Messages.Add(new ChatMessage { Role = "assistant", Content = answer, At = DateTimeOffset.Now });
                conversation.UpdatedAt = DateTimeOffset.Now;
                Write("conversations.json", conversations.OrderByDescending(item => item.UpdatedAt).ToList());
            }
        }
        return new { answer, model = "local:ollama", conversation = new { id = prepared.Id, title = prepared.Title } };
    }

    private object ListConversations(string search)
    {
        var items = ReadList<Conversation>("conversations.json");
        if (!string.IsNullOrWhiteSpace(search))
            items = items.Where(item => item.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                item.Messages.Any(message => message.Content.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        return new { ok = true, conversations = items.OrderByDescending(item => item.UpdatedAt).Select(item => new { item.Id, item.Title, item.CreatedAt, item.UpdatedAt, message_count = item.Messages.Count }) };
    }

    private object GetConversation(string id)
    {
        var item = ReadList<Conversation>("conversations.json").FirstOrDefault(value => value.Id == id);
        return item is null ? new { ok = false, error = "Conversa nÃ£o encontrada." } : new { ok = true, conversation = item };
    }

    private object RenameConversation(string? body)
    {
        var request = Deserialize<ConversationMutation>(body);
        if (request is null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Title)) return new { ok = false, error = "ID e tÃ­tulo sÃ£o obrigatÃ³rios." };
        lock (conversationGate)
        {
            var items = ReadList<Conversation>("conversations.json"); var item = items.FirstOrDefault(value => value.Id == request.Id);
            if (item is null) return new { ok = false, error = "Conversa nÃ£o encontrada." };
            item.Title = request.Title.Trim()[..Math.Min(request.Title.Trim().Length, 100)]; item.UpdatedAt = DateTimeOffset.Now; Write("conversations.json", items);
            return new { ok = true, conversation = item };
        }
    }

    private object DeleteConversation(string? body)
    {
        var request = Deserialize<ConversationMutation>(body); if (request is null) return new { ok = false, error = "ID obrigatÃ³rio." };
        lock (conversationGate) { var items = ReadList<Conversation>("conversations.json"); int removed = items.RemoveAll(item => item.Id == request.Id); Write("conversations.json", items); Audit("conversation.delete", request.Id); return new { ok = removed > 0 }; }
    }

    private static string BuildConversationContext(List<ChatMessage> messages)
    {
        const int maxChars = 14000;
        var selected = new List<string>(); int chars = 0;
        for (int index = messages.Count - 1; index >= 0 && selected.Count < 24; index--)
        {
            string line = (messages[index].Role == "assistant" ? "Mike: " : "UsuÃ¡rio: ") + messages[index].Content;
            if (chars + line.Length > maxChars) break; selected.Add(line); chars += line.Length;
        }
        selected.Reverse(); return string.Join("\n", selected);
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length > 1 ? Uri.UnescapeDataString(x[1].Replace('+', ' ')) : "", StringComparer.OrdinalIgnoreCase);

    private async Task<object> ModelsAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var json = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:11434/api/tags"));
            var models = json.RootElement.GetProperty("models").EnumerateArray().Select(m => new
            {
                model = m.GetProperty("name").GetString(), name = m.GetProperty("name").GetString(), provider = "Ollama",
                installed = true, compatible = true, cloud = false, purpose = "IA local"
            }).ToArray();
            return new { ok = true, active_model = models.FirstOrDefault()?.model, models };
        }
        catch { return new { ok = false, active_model = (string?)null, models = Array.Empty<object>(), error = "Ollama nÃ£o respondeu." }; }
    }

    private object SearchMemory(string q) => new { ok = true, notes = ReadList<MemoryNote>("memory.json").Where(n => (n.Title + " " + n.Content + " " + string.Join(' ', n.Tags)).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList() };
    private object GetMemory(string id) => new { ok = true, note = ReadList<MemoryNote>("memory.json").FirstOrDefault(n => n.Id == id) };
    private object SaveMemory(string? body)
    {
        var input = Deserialize<MemoryNote>(body) ?? new(); var notes = ReadList<MemoryNote>("memory.json");
        input.Id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString("N") : input.Id;
        input.UpdatedAt = DateTimeOffset.Now; int at = notes.FindIndex(n => n.Id == input.Id); if (at >= 0) notes[at] = input; else notes.Add(input);
        Write("memory.json", notes); Audit("memory.save", input.Id); return new { ok = true, note = input };
    }
    private object DeleteMemory(string? body)
    {
        string id = Deserialize<IdRequest>(body)?.Id ?? ""; var notes = ReadList<MemoryNote>("memory.json"); int removed = notes.RemoveAll(n => n.Id == id);
        Write("memory.json", notes); Audit("memory.delete", id); return new { ok = removed > 0 };
    }
    private object SavePreferences(string? body) { WriteRaw("memory-preferences.json", body ?? "{}"); Audit("memory.preferences", "updated"); return new { ok = true }; }

    private object ListFiles(string path)
    {
        string resolved = string.IsNullOrWhiteSpace(path) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetFullPath(path);
        if (!Directory.Exists(resolved)) return new { ok = false, error = "Pasta nÃ£o encontrada.", path = resolved };
        var info = new DirectoryInfo(resolved);
        var items = info.EnumerateFileSystemInfos().Take(500).Select(x => new { name = x.Name, path = x.FullName, type = x is DirectoryInfo ? "directory" : "file", size = x is FileInfo f ? f.Length : 0, modified = x.LastWriteTimeUtc }).ToArray();
        return new { ok = true, path = resolved, parent = info.Parent?.FullName, items };
    }
    private object FileMetadata(string path)
    {
        string resolved = Path.GetFullPath(path); var f = new FileInfo(resolved); if (!f.Exists) return new { ok = false, error = "Arquivo nÃ£o encontrado." };
        string ext = f.Extension.ToLowerInvariant(); string kind = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(ext) ? "image" : ext == ".pdf" ? "pdf" : new[] { ".txt", ".md", ".json", ".log", ".csv" }.Contains(ext) ? "text" : "unsupported";
        string? text = kind == "text" ? File.ReadAllText(f.FullName) : null;
        string? content = text?[..Math.Min(200_000, text.Length)];
        return new { ok = true, name = f.Name, path = f.FullName, size = f.Length, kind, content, message = kind == "unsupported" ? "Abra no aplicativo associado." : null };
    }
    private object FileAction(string? body)
    {
        var req = Deserialize<FileActionRequest>(body); if (req is null || !File.Exists(req.Path) && !Directory.Exists(req.Path)) return new { ok = false, error = "Caminho invÃ¡lido." };
        string target = req.Action == "open-folder" && File.Exists(req.Path) ? Path.GetDirectoryName(req.Path)! : req.Path;
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); Audit("file.open", target); return new { ok = true };
    }

    private object AgentCatalog()
    {
        var agents = new List<object> {
        new { id="windows-ui-operator", name="Operador visual Windows", role="Mouse, teclado e visÃ£o com aÃ§Ãµes reversÃ­veis", tools=new[]{"computer-use"} },
        new { id="computer-use-repair", name="Reparo Computer Use", role="SaÃºde e reparo do driver", tools=new[]{"doctor","health_report"} },
        new { id="uac-policy-guardian", name="GuardiÃ£o UAC", role="ElevaÃ§Ã£o explÃ­cita e auditada", tools=new[]{"approval","uac"} },
        new { id="native-api-compat", name="Compatibilidade da interface", role="Rotas da UI para backend nativo", tools=new[]{"status","models","memory","files"} },
        new { id="controlled-learning", name="Aprendizado controlado", role="Propostas, aprovaÃ§Ã£o, histÃ³rico e rollback", tools=new[]{"memory","skills","rollback"} }
        ,new { id="capability-scout", name="Mike Explorador de Capacidades", role="Audita recursos ausentes, pesquisa alternativas e propÃµe novas funÃ§Ãµes para aprovaÃ§Ã£o do proprietÃ¡rio", tools=new[]{"research","capabilities","skills","planning"} }
        ,new { id="business-concierge", name="Mike Gestor de Expediente", role="Conduz briefing matinal, mÃ©tricas sociais e atendimento sequencial por voz usando apenas conectores autorizados", tools=new[]{"weather","social","attendance","transcription","catalog","voice"} }
        };
        agents.AddRange(ReadList<LearnedAgent>("learned-agents.json"));
        return new { ok = true, agents, runs = Array.Empty<object>() };
    }

    private object PeerStatus()
    {
        var peers = new[] { mesh.LocalNode }.Concat(mesh.GetPeers().Where(p => DateTime.UtcNow - p.LastSeen < TimeSpan.FromMinutes(2)))
            .Select(p => new { id = p.NodeId, name = p.Hostname, ip = p.IpAddress, self = p.NodeId == mesh.LocalNode.NodeId, status = "online", ram_gb = p.RamGb, logical_processors = p.ProcessorCount, installed_models = Array.Empty<string>() }).ToArray();
        return new { ok = true, automatic_discovery = true, peers };
    }
    private object ClusterStatus()
    {
        var nodes = new[] { mesh.LocalNode }.Concat(mesh.GetPeers()).Select(p => new { id = p.NodeId, name = p.Hostname, ip = p.IpAddress, status = "online", role = p.NodeId == mesh.GetCoordinator().NodeId ? "commander" : "worker", ram_gb = p.RamGb, logical_processors = p.ProcessorCount, strength_score = p.ProcessorCount }).ToArray();
        return new { ok = true, commander = nodes.FirstOrDefault(n => n.role == "commander"), hierarchy = nodes, config = new { enabled = true, paused = false, max_cpu_percent = 50, max_ram_percent = 50, allowed_tasks = new[] { "chat", "code_review", "image_generate" } }, lan_relay = new { message = "descoberta LAN automÃ¡tica ativa; tarefas exigem autorizaÃ§Ã£o" } };
    }

    private object MangaStatus()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "manga-translate");
        bool module = File.Exists(Path.Combine(root, "scripts", "mike-manga.ps1"));
        bool koharu = File.Exists(Path.Combine(root, "vendor", "koharu", "koharu.exe"));
        bool tesseract = File.Exists(Path.Combine(root, "vendor", "tesseract", "bin", "tesseract.exe"));
        return new { ok = module, installed = module, koharu, tesseract, supports = new[] { "public-page", "javascript-page", "direct-image", "local-images", "zip", "cbz", "mangadex", "naver" }, module_root = root };
    }

    private object StartTranslation(string? body)
    {
        var request = Deserialize<TranslationRequest>(body) ?? new();
        if (string.IsNullOrWhiteSpace(request.Input) || !File.Exists(request.Input))
            return new { ok = false, error = "Arquivo de entrada nÃ£o encontrado." };
        string extension = Path.GetExtension(request.Input).ToLowerInvariant();
        string[] documents = { ".txt", ".md", ".srt", ".vtt", ".docx", ".epub" };
        bool media = request.Kind.Equals("media", StringComparison.OrdinalIgnoreCase) ||
                     new[] { ".mp3", ".wav", ".m4a", ".mp4", ".mkv", ".mov", ".webm" }.Contains(extension);
        if (!media && !documents.Contains(extension))
            return new { ok = false, error = "Formato ainda nÃ£o aceito pelo tradutor geral." };
        string engineRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "manga-translate", "translation-engine");
        string id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        string outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "translations", id);
        Directory.CreateDirectory(outputFolder);
        string output;
        if (media)
        {
            string script = Path.Combine(engineRoot, "translate-media.ps1");
            if (!File.Exists(script)) return new { ok = false, error = "MÃ³dulo de mÃ­dia ainda nÃ£o foi instalado; use Reparar." };
            output = outputFolder;
        }
        else
        {
            string script = Path.Combine(engineRoot, "mike_translate.py");
            if (!File.Exists(script)) return new { ok = false, error = "Motor adaptativo ainda nÃ£o foi instalado; use Reparar." };
            string suffix = extension is ".srt" or ".vtt" ? ".pt-BR" + extension : ".pt-BR" + extension;
            output = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(request.Input) + suffix);
        }
        ProcessStartInfo start = BuildTranslationStartInfo(request, outputFolder, output, media, engineRoot);
        Process? process;
        try { process = Process.Start(start); }
        catch (Exception ex) { return new { ok = false, error = "NÃ£o foi possÃ­vel iniciar o tradutor: " + ex.Message }; }
        if (process is null) return new { ok = false, error = "O processo de traduÃ§Ã£o nÃ£o iniciou." };
        File.WriteAllText(Path.Combine(outputFolder, "job.json"), JsonSerializer.Serialize(new { id, status = "running", pid = process.Id, attempts = 1, input = request.Input, output, kind = media ? "media" : "document", project = request.Project, source = request.Source, target = request.Target, dubbing = request.Dubbing, started_at = DateTimeOffset.Now }, JsonOptions));
        Audit("translation.started", id);
        return new { ok = true, id, pid = process.Id, output, folder = outputFolder, persistent = true };
    }

    private object TranslationStatus(string? body)
    {
        var request = Deserialize<TranslationStatusRequest>(body) ?? new();
        if (string.IsNullOrWhiteSpace(request.Id)) return new { ok = false, error = "Identificador da traducao ausente." };
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "translations", request.Id);
        string jobPath = Path.Combine(folder, "job.json");
        if (!File.Exists(jobPath)) return new { ok = false, error = "Trabalho de traducao nao encontrado." };
        using JsonDocument job = JsonDocument.Parse(File.ReadAllText(jobPath));
        var root = job.RootElement;
        int pid = root.TryGetProperty("pid", out var pidNode) && pidNode.TryGetInt32(out var parsedPid) ? parsedPid : 0;
        bool alive = false;
        if (pid > 0) { try { alive = !Process.GetProcessById(pid).HasExited; } catch { alive = false; } }
        string status = alive ? "running" : "finished";
        string? error = null;
        string? output = root.TryGetProperty("output", out var outputNode) ? outputNode.GetString() : null;
        int attempts = root.TryGetProperty("attempts", out var attemptNode) && attemptNode.TryGetInt32(out var parsedAttempts) ? parsedAttempts : 1;
        if (root.TryGetProperty("kind", out var kindNode) && kindNode.GetString() == "document" && output is not null)
        {
            string manifest = output + ".translation.json";
            if (!File.Exists(output) && !alive) { status = "failed"; error = "O arquivo de saida nao foi criado."; }
            else if (File.Exists(manifest)) { status = "completed"; }
        }
        else if (root.TryGetProperty("kind", out var mediaKind) && mediaKind.GetString() == "media")
        {
            bool hasSubtitle = Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.srt").Any();
            bool hasVideo = Directory.Exists(folder) && Directory.EnumerateFiles(folder, "video-*.mp4").Any();
            if (!alive && (hasSubtitle || hasVideo)) status = "completed";
            else if (!alive) { status = "failed"; error = "O trabalho de midia terminou sem gerar legenda ou video."; }
        }
        if (!alive && status == "failed" && attempts < 3 && output is not null)
        {
            var resume = new TranslationRequest {
                Input = root.TryGetProperty("input", out var inputValue) ? inputValue.GetString() ?? "" : "",
                Kind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() ?? "document" : "document",
                Project = root.TryGetProperty("project", out var projectValue) ? projectValue.GetString() ?? "default" : "default",
                Source = root.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString() ?? "auto" : "auto",
                Target = root.TryGetProperty("target", out var targetValue) ? targetValue.GetString() ?? "pt-BR" : "pt-BR",
                Dubbing = root.TryGetProperty("dubbing", out var dubbingValue) && dubbingValue.ValueKind == JsonValueKind.True
            };
            if (File.Exists(resume.Input))
            {
                string engineRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "manga-translate", "translation-engine");
                try
                {
                    Process? resumed = Process.Start(BuildTranslationStartInfo(resume, folder, output, resume.Kind == "media", engineRoot));
                    if (resumed is not null)
                    {
                        File.WriteAllText(jobPath, JsonSerializer.Serialize(new { id = request.Id, status = "running", pid = resumed.Id, attempts = attempts + 1, input = resume.Input, output, kind = resume.Kind, project = resume.Project, source = resume.Source, target = resume.Target, dubbing = resume.Dubbing, resumed_at = DateTimeOffset.Now }, JsonOptions));
                        return new { ok = true, id = request.Id, status = "running", alive = true, pid = resumed.Id, input = resume.Input, output, folder, error = (string?)null, resumed = true };
                    }
                }
                catch (Exception ex) { error = "Falha ao retomar: " + ex.Message; }
            }
        }
        if (!alive && status == "finished")
        {
            string report = Path.Combine(folder, "report.json");
            if (File.Exists(report)) { try { using var reportDoc = JsonDocument.Parse(File.ReadAllText(report)); if (reportDoc.RootElement.TryGetProperty("ok", out var ok) && !ok.GetBoolean()) { status = "failed"; error = reportDoc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "Falha no trabalho."; } } catch { } }
        }
        if (!alive && status is "completed" or "failed")
        {
            string evidence = Path.Combine(folder, ".evolution-recorded");
            if (!File.Exists(evidence))
            {
                bool success = status == "completed";
                evolution.RecordOutcome("translation-specialist",
                    root.TryGetProperty("kind", out var outcomeKind) ? "translation-" + outcomeKind.GetString() : "translation",
                    success ? "Translation job completed and produced the expected artifact." : "Translation job failed after its recovery attempts.",
                    success, success ? 0.85 : 0.20, success ? 0.90 : 0.20, 1.0, success ? 0.80 : 0.40);
                File.WriteAllText(evidence, DateTimeOffset.UtcNow.ToString("O"));
                Audit("evolution.translation_outcome", request.Id + ":" + status);
            }
        }
        return new { ok = true, id = request.Id, status, alive, pid, input = root.TryGetProperty("input", out var inputNode) ? inputNode.GetString() : null, output, folder, error };
    }

    private static ProcessStartInfo BuildTranslationStartInfo(TranslationRequest request, string outputFolder, string output, bool media, string engineRoot)
    {
        if (media)
        {
            string script = Path.Combine(engineRoot, "translate-media.ps1");
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string value in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-InputPath", request.Input, "-OutputDir", outputFolder, "-Project", request.Project, "-SourceLanguage", request.Source, "-TargetLanguage", request.Target }) start.ArgumentList.Add(value);
            if (request.Dubbing) start.ArgumentList.Add("-DubAudio");
            return start;
        }
        string python = Path.Combine(engineRoot, "mike_translate.py");
        var document = new ProcessStartInfo("python.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string value in new[] { python, request.Input, "--output", output, "--project", request.Project, "--source", request.Source, "--target", request.Target, "--memory-root", Path.Combine(engineRoot, "memory"), "--checkpoint", output + ".checkpoint.json" }) document.ArgumentList.Add(value);
        return document;
    }

    private async Task<object> SearchMangaAsync(string? body)
    {
        var request = Deserialize<MangaSearchRequest>(body) ?? new();
        string query = request.Query.Trim();
        if (query.Length < 2) return new { ok = false, error = "Informe o nome do manga, manhwa ou webtoon." };
        if (query.Length > 160) query = query[..160];

        string? configPath = MangaSourcesPaths().FirstOrDefault(File.Exists);
        if (configPath is null) return new { ok = false, error = "Lista oficial de fontes nÃ£o encontrada. Execute Reparar na Mike." };
        using JsonDocument sourceDocument = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        var sources = sourceDocument.RootElement.GetProperty("sources").EnumerateArray()
            .Where(source => source.TryGetProperty("search", out var searchable) && searchable.ValueKind == JsonValueKind.True)
            .Where(source => source.TryGetProperty("base_url", out var baseUrl) && baseUrl.ValueKind == JsonValueKind.String)
            .Select(source => new MangaSource
            {
                Id = source.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                Name = source.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                BaseUrl = source.GetProperty("base_url").GetString() ?? "",
                Languages = source.TryGetProperty("languages", out var languages) && languages.ValueKind == JsonValueKind.Array
                    ? languages.EnumerateArray().Select(language => language.GetString() ?? "").Where(language => language.Length > 0).ToArray()
                    : Array.Empty<string>()
            }).Where(source => Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out _)).ToList();

        var results = new List<MangaSearchResult>();
        MangaSource? mangaDex = sources.FirstOrDefault(source => source.Id == "mangadex_api");
        if (mangaDex is not null)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MikeLocal/1.0.42 manga-search");
                string api = "https://api.mangadex.org/manga?limit=6&title=" + Uri.EscapeDataString(query) + "&order[relevance]=desc";
                using JsonDocument catalog = JsonDocument.Parse(await http.GetStringAsync(api));
                foreach (JsonElement manga in catalog.RootElement.GetProperty("data").EnumerateArray())
                {
                    string id = manga.GetProperty("id").GetString() ?? "";
                    JsonElement attributes = manga.GetProperty("attributes");
                    string title = ReadLocalizedTitle(attributes.GetProperty("title"));
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id)) continue;
                    bool ptBr = false;
                    try
                    {
                        string chapterApi = "https://api.mangadex.org/chapter?limit=1&manga=" + Uri.EscapeDataString(id) + "&translatedLanguage[]=pt-br";
                        using JsonDocument chapters = JsonDocument.Parse(await http.GetStringAsync(chapterApi));
                        ptBr = chapters.RootElement.TryGetProperty("total", out var total) && total.GetInt32() > 0;
                    }
                    catch { }
                    results.Add(new MangaSearchResult { Title = title, Url = "https://mangadex.org/title/" + id, Source = mangaDex.Name, Language = ptBr ? "pt-BR" : "multi", PtBr = ptBr, Kind = "title" });
                }
            }
            catch { }
        }

        foreach (MangaSource source in sources.Where(source => source.Id != "mangadex_api"))
        {
            bool ptBr = source.Languages.Any(language => language.Equals("pt-BR", StringComparison.OrdinalIgnoreCase) || language.Equals("pt", StringComparison.OrdinalIgnoreCase));
            results.Add(new MangaSearchResult { Title = "Pesquisar â€œ" + query + "â€", Url = BuildOfficialSearchUrl(source, query), Source = source.Name, Language = ptBr ? "pt-BR" : string.Join(", ", source.Languages), PtBr = ptBr, Kind = "official-search" });
        }
        results = results.OrderByDescending(item => item.PtBr).ThenBy(item => item.Kind == "title" ? 0 : 1).ThenBy(item => item.Source).Take(18).ToList();
        int ptCount = results.Count(item => item.PtBr);
        string answer = results.Count == 0
            ? "NÃ£o encontrei resultados nas fontes oficiais/pÃºblicas configuradas."
            : ptCount > 0
                ? $"Encontrei {results.Count} opÃ§Ãµes oficiais para â€œ{query}â€; {ptCount} priorizadas em portuguÃªs do Brasil. Abra um resultado abaixo."
                : $"Encontrei {results.Count} opÃ§Ãµes oficiais para â€œ{query}â€, mas nenhuma confirmou pt-BR. Abra uma ediÃ§Ã£o pÃºblica e cole a URL do episÃ³dio para eu oferecer a traduÃ§Ã£o, sem contornar login, compra, paywall ou DRM.";
        Audit("manga.search", query);
        return new { ok = true, query, preferred_language = "pt-BR", answer, has_pt_br = ptCount > 0, results, policy = "Somente fontes oficiais/pÃºblicas habilitadas em sources.json; sem bypass de login, paywall ou DRM." };
    }

    private static IEnumerable<string> MangaSourcesPaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "manga-translate", "config", "sources.json");
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "manga-translate", "config", "sources.json"));
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "manga-translate", "config", "sources.json"));
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "local-ai", "manga-translate", "config", "sources.json"));
    }

    private static string ReadLocalizedTitle(JsonElement titles)
    {
        foreach (string language in new[] { "pt-br", "en", "ja-ro", "ko-ro", "ja", "ko", "zh" })
            if (titles.TryGetProperty(language, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        return titles.EnumerateObject().FirstOrDefault().Value.ValueKind == JsonValueKind.String
            ? titles.EnumerateObject().First().Value.GetString() ?? "" : "";
    }

    private static string BuildOfficialSearchUrl(MangaSource source, string query)
    {
        string encoded = Uri.EscapeDataString(query);
        return source.Id switch
        {
            "webtoon_assisted" or "webtoon_global" => "https://www.webtoons.com/en/search?keyword=" + encoded,
            "naver_webtoon" => "https://comic.naver.com/search?keyword=" + encoded,
            "amazon_br_manga" => "https://www.amazon.com.br/s?k=" + encoded + "+manga&i=digital-text",
            "google_play_books_br" => "https://play.google.com/store/search?q=" + encoded + "&c=books",
            "viz" => "https://www.viz.com/search?search=" + encoded,
            "bookwalker" => "https://global.bookwalker.jp/search/?word=" + encoded,
            "internet_archive_reviewed" => "https://archive.org/search?query=" + encoded,
            "wikimedia_commons" => "https://commons.wikimedia.org/w/index.php?search=" + encoded,
            _ => source.BaseUrl
        };
    }

    private static string ExtractMangaSearchTitle(string message)
    {
        string title = System.Text.RegularExpressions.Regex.Replace(message,
            "(?i)\\b(procure|pesquise|busque|buscar|encontre|achar|ache|quero ler|onde (?:ler|encontrar)|mang[aÃ¡]|manhwa|manhua|webtoon|quadrinho|pelo nome|chamado|chamada)\\b", " ");
        title = System.Text.RegularExpressions.Regex.Replace(title, "\\s+", " ").Trim(' ', ':', '-', '?', '!', '.', '"', '\'');
        return title;
    }

    private async Task<object> StartMangaAsync(string? body)
    {
        var request = Deserialize<MangaRequest>(body) ?? new();
        if (string.IsNullOrWhiteSpace(request.Url) && string.IsNullOrWhiteSpace(request.Input))
            return new { ok = false, error = "Informe uma URL pÃºblica ou um arquivo/pasta local." };
        string script = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "manga-translate", "scripts", "mike-manga.ps1");
        if (!File.Exists(script)) return new { ok = false, error = "MÃ³dulo de mangÃ¡ nÃ£o instalado. Execute Reparar na Mike." };
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Command", "translate", "-Json" }) start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(request.Url)) { start.ArgumentList.Add("-Url"); start.ArgumentList.Add(request.Url); }
        else { start.ArgumentList.Add("-Input"); start.ArgumentList.Add(Path.GetFullPath(request.Input)); }
        if (request.AllowPaidApi) start.ArgumentList.Add("-AllowPaidApi");
        using var process = Process.Start(start);
        if (process is null) return new { ok = false, error = "NÃ£o foi possÃ­vel iniciar o tradutor." };
        string output = await process.StandardOutput.ReadToEndAsync(); string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); Audit("manga.start", request.Url ?? request.Input);
        return new { ok = process.ExitCode is 0 or 2, exit_code = process.ExitCode, output, error };
    }

    private object ProposeLearning(string? body)
    {
        var req = Deserialize<LearningProposal>(body) ?? new(); req.Id = Guid.NewGuid().ToString("N"); req.Status = "pending"; req.CreatedAt = DateTimeOffset.Now;
        var all = ReadList<LearningProposal>("learning-proposals.json"); all.Add(req); Write("learning-proposals.json", all); Audit("learning.propose", req.Id); return new { ok = true, proposal = req };
    }
    private async Task<object> ResearchAndLearnAsync(string? body)
    {
        var request = Deserialize<LearningResearchRequest>(body) ?? new();
        string topic = request.Topic.Trim();
        if (topic.Length < 3) return new { ok = false, error = "Informe o assunto que a Mike deve aprender." };
        if (topic.Length > 500) topic = topic[..500];
        string prompt = "Pesquise na internet e em fontes confiÃ¡veis sobre o tema abaixo. Compare diversas fontes, registre URLs e datas, consolide um manual prÃ¡tico em portuguÃªs e nÃ£o invente fatos. Tema: " + topic;
        object response = await ProxyLegacyAsync("/ui/chat", "POST", JsonSerializer.Serialize(new { message = prompt, conversation_id = "", response_mode = "deep", agent_mode = true }));
        if (response is not JsonElement element || !element.TryGetProperty("answer", out var answerElement))
            return new { ok = false, error = "A pesquisa nÃ£o pÃ´de ser concluÃ­da agora.", detail = response };
        string knowledge = answerElement.GetString() ?? string.Empty;
        if (knowledge.Length < 80) return new { ok = false, error = "A pesquisa retornou conteÃºdo insuficiente para criar uma skill." };

        string[] sources = await ExtractVerifiedSourcesAsync(knowledge);
        if (sources.Length < 2)
            return new { ok = false, error = "A pesquisa nÃ£o encontrou pelo menos duas fontes distintas e acessÃ­veis. Nada foi salvo para evitar aprendizado incorreto.", sources };
        string id = "learned-" + Guid.NewGuid().ToString("N");
        var skills = ReadList<ApprovedSkill>("approved-skills.json");
        skills.Add(new ApprovedSkill { ProposalId = id, Title = topic, Instructions = knowledge, Sources = sources, Version = 1, ApprovedAt = DateTimeOffset.Now });
        Write("approved-skills.json", skills);
        bool broad = topic.Length > 90 || System.Text.RegularExpressions.Regex.IsMatch(topic, "\\b(sistema|especialista|completo|empresa|mercado|programa[cÃ§][aÃ£]o|marketing|atendimento|redes sociais)\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        LearnedAgent? agent = null;
        if (broad)
        {
            var agents = ReadList<LearnedAgent>("learned-agents.json");
            string agentTopic = topic.Length > 72 ? topic[..72] + "â€¦" : topic;
            agent = new LearnedAgent { Id = "specialist-" + Guid.NewGuid().ToString("N"), Name = "Mike Especialista em " + agentTopic, Role = "Consulta a skill consolidada e auxilia em " + topic, SkillId = id, CreatedAt = DateTimeOffset.Now };
            agents.Add(agent); Write("learned-agents.json", agents);
        }
        Audit("learning.research", id + (agent is null ? " skill" : " agent"));
        return new { ok = true, skill = new { id, title = topic, version = 1 }, agent, sources, sources_preserved = true, message = agent is null ? "Skill criada e pronta para uso." : "Skill e agente especialista criados e prontos para uso." };
    }

    private static async Task<string[]> ExtractVerifiedSourcesAsync(string text)
    {
        string[] candidates = System.Text.RegularExpressions.Regex.Matches(text, "https?://[^\\s)\\]}>]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => match.Value.TrimEnd('.', ',', ';', ':')).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(8) };
        async Task<string?> VerifyAsync(string candidate)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                return (int)response.StatusCode is >= 200 and < 400 ? response.RequestMessage?.RequestUri?.ToString() ?? candidate : null;
            }
            catch { return null; }
        }
        string?[] checkedSources = await Task.WhenAll(candidates.Select(VerifyAsync));
        return checkedSources.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
    }
    private object ApproveLearning(string? body)
    {
        string id = Deserialize<IdRequest>(body)?.Id ?? ""; var all = ReadList<LearningProposal>("learning-proposals.json"); var item = all.FirstOrDefault(x => x.Id == id);
        if (item is null || item.Status != "pending") return new { ok = false, error = "Proposta pendente nÃ£o encontrada." };
        if (item.Kind.Equals("skill", StringComparison.OrdinalIgnoreCase))
        {
            var skills = ReadList<ApprovedSkill>("approved-skills.json");
            skills.Add(new ApprovedSkill { ProposalId = item.Id, Title = item.Title, Instructions = item.Content, Version = 1, ApprovedAt = DateTimeOffset.Now });
            Write("approved-skills.json", skills);
        }
        else
        {
            var notes = ReadList<MemoryNote>("memory.json");
            notes.Add(new MemoryNote { Id = "learning-" + item.Id, Title = item.Title, Content = item.Content, Tags = new[] { "aprendizado-aprovado" }, Source = "controlled-learning", UpdatedAt = DateTimeOffset.Now });
            Write("memory.json", notes);
        }
        item.Status = "approved"; item.DecidedAt = DateTimeOffset.Now; Write("learning-proposals.json", all); Audit("learning.approved", id); return new { ok = true, proposal = item };
    }
    private object RollbackLearning(string? body)
    {
        string id = Deserialize<IdRequest>(body)?.Id ?? ""; var all = ReadList<LearningProposal>("learning-proposals.json"); var item = all.FirstOrDefault(x => x.Id == id);
        if (item is null || item.Status != "approved") return new { ok = false, error = "Proposta aprovada nÃ£o encontrada." };
        if (item.Kind.Equals("skill", StringComparison.OrdinalIgnoreCase)) { var skills = ReadList<ApprovedSkill>("approved-skills.json"); skills.RemoveAll(x => x.ProposalId == id); Write("approved-skills.json", skills); }
        else { var notes = ReadList<MemoryNote>("memory.json"); notes.RemoveAll(x => x.Id == "learning-" + id); Write("memory.json", notes); }
        item.Status = "rolled_back"; item.DecidedAt = DateTimeOffset.Now; Write("learning-proposals.json", all); Audit("learning.rolled_back", id); return new { ok = true, proposal = item };
    }
    private object LearningHistory() => new { ok = true, proposals = ReadList<LearningProposal>("learning-proposals.json"), approved_skills = ReadList<ApprovedSkill>("approved-skills.json") };

    private object EvolutionStatus() => new {
        ok = true, automatic = true, agents = AutonomousEvolution.Agents,
        candidates = evolution.ReadCandidates()
    };

    private object EvolutionPropose(string? body)
    {
        var request = Deserialize<EvolutionProposalRequest>(body) ?? new();
        var candidate = evolution.Propose(request.AgentId, request.Domain,
            request.Description, request.Risk, request.BaselineScore);
        Audit("evolution.propose", candidate.Id);
        return new { ok = true, candidate };
    }

    private object EvolutionEvaluate(string? body)
    {
        var request = Deserialize<EvolutionEvaluationRequest>(body) ?? new();
        var candidate = evolution.Evaluate(request.Id, new EvolutionEvaluation(
            request.Quality, request.Reliability, request.Safety, request.Efficiency,
            DateTimeOffset.UtcNow));
        Audit("evolution.evaluate", candidate.Id + ":" + candidate.Status);
        return new { ok = true, candidate };
    }

    private object EvolutionRollback(string? body)
    {
        var request = Deserialize<EvolutionRollbackRequest>(body) ?? new();
        var candidate = evolution.Rollback(request.Id, request.Reason);
        Audit("evolution.rollback", candidate.Id);
        return new { ok = true, candidate };
    }

    private object EvolutionOutcome(string? body)
    {
        var request = Deserialize<EvolutionOutcomeRequest>(body) ?? new();
        var candidate = evolution.RecordOutcome(request.AgentId, request.Domain,
            request.Description, request.Success, request.Quality, request.Reliability,
            request.Safety, request.Efficiency);
        Audit("evolution.outcome", candidate.Id + ":" + candidate.Status);
        return new { ok = true, candidate };
    }

    private async Task<object> AdminDiagnosticAsync(string? body)
    {
        var req = Deserialize<AdminRequest>(body); if (req?.Confirm != true) return new { ok = false, confirmation_required = true, message = "O Windows mostrarÃ¡ o UAC. Confirme para executar somente o diagnÃ³stico administrativo." };
        string marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MikeLocal", "uac-diagnostic.txt");
        string escaped = marker.Replace("'", "''");
        Process? p;
        try { p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName('{escaped}')) | Out-Null; Set-Content -LiteralPath '{escaped}' -Value ([DateTimeOffset]::Now.ToString('O'))\"") { UseShellExecute = true, Verb = "runas" }); }
        catch (System.ComponentModel.Win32Exception) { Audit("admin.diagnostic", "cancelled"); return new { ok = false, cancelled = true, error = "ElevaÃ§Ã£o cancelada sem alteraÃ§Ãµes." }; }
        if (p is null) return new { ok = false, error = "ElevaÃ§Ã£o cancelada ou nÃ£o iniciada." }; await p.WaitForExitAsync(); bool ok = p.ExitCode == 0 && File.Exists(marker); Audit("admin.diagnostic", ok ? "passed" : "failed"); return new { ok, marker, exit_code = p.ExitCode };
    }

    private List<T> ReadList<T>(string name) { try { return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(Path.Combine(dataRoot, name)), JsonOptions) ?? new(); } catch { return new(); } }
    private object ReadObject(string name, object fallback) { try { return JsonSerializer.Deserialize<object>(File.ReadAllText(Path.Combine(dataRoot, name)), JsonOptions) ?? fallback; } catch { return fallback; } }
    private T? Deserialize<T>(string? body) { try { return JsonSerializer.Deserialize<T>(body ?? "{}", JsonOptions); } catch { return default; } }
    private void Write<T>(string name, T value) => File.WriteAllText(Path.Combine(dataRoot, name), JsonSerializer.Serialize(value, JsonOptions));
    private void WriteRaw(string name, string json) { using var _ = JsonDocument.Parse(json); File.WriteAllText(Path.Combine(dataRoot, name), json); }
    private void Audit(string action, string target) => File.AppendAllText(auditFile, JsonSerializer.Serialize(new { at = DateTimeOffset.Now, action, target }) + Environment.NewLine);

    private sealed class MemoryNote { public string Id { get; set; } = ""; public string Title { get; set; } = "Nota"; public string Content { get; set; } = ""; public string[] Tags { get; set; } = Array.Empty<string>(); public string Source { get; set; } = "native"; public DateTimeOffset UpdatedAt { get; set; } }
    public sealed record PreparedChat(string Id, string Title, string Prompt, string? CatalogId, string CurrentMessage);
    private sealed class ChatRequest { public string Message { get; set; } = ""; [JsonPropertyName("conversation_id")] public string? ConversationId { get; set; } }
    private sealed class ConversationMutation { public string Id { get; set; } = ""; public string Title { get; set; } = ""; }
    private sealed class Conversation
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "Nova conversa";
        public string? CatalogId { get; set; }
        public List<ChatMessage> Messages { get; set; } = new();
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
    private sealed class ChatMessage { public string Role { get; set; } = "user"; public string Content { get; set; } = ""; public DateTimeOffset At { get; set; } }
    private sealed class LearningProposal { public string Id { get; set; } = ""; public string Kind { get; set; } = "memory"; public string Title { get; set; } = ""; public string Content { get; set; } = ""; public string Status { get; set; } = "pending"; public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset? DecidedAt { get; set; } }
    private sealed class ApprovedSkill { public string ProposalId { get; set; } = ""; public string Title { get; set; } = ""; public string Instructions { get; set; } = ""; public string[] Sources { get; set; } = Array.Empty<string>(); public int Version { get; set; } public DateTimeOffset ApprovedAt { get; set; } }
    private sealed class LearnedAgent { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Role { get; set; } = ""; public string SkillId { get; set; } = ""; public string[] Tools { get; set; } = new[] { "research", "memory", "reasoning" }; public DateTimeOffset CreatedAt { get; set; } }
    private sealed class LearningResearchRequest { public string Topic { get; set; } = ""; }
    private sealed class EvolutionProposalRequest { public string AgentId { get; set; } = ""; public string Domain { get; set; } = ""; public string Description { get; set; } = ""; public EvolutionRisk Risk { get; set; } = EvolutionRisk.Reversible; public double BaselineScore { get; set; } }
    private sealed class EvolutionEvaluationRequest { public string Id { get; set; } = ""; public double Quality { get; set; } public double Reliability { get; set; } public double Safety { get; set; } public double Efficiency { get; set; } }
    private sealed class EvolutionRollbackRequest { public string Id { get; set; } = ""; public string Reason { get; set; } = "owner request"; }
    private sealed class EvolutionOutcomeRequest { public string AgentId { get; set; } = "programming-specialist"; public string Domain { get; set; } = "programming"; public string Description { get; set; } = "Measured task outcome"; public bool Success { get; set; } public double Quality { get; set; } public double Reliability { get; set; } public double Safety { get; set; } = 1; public double Efficiency { get; set; } }
    private sealed class SelfCodeRequest { public SelfCodeChangePlan? Plan { get; set; } public bool Confirm { get; set; } }
    private sealed class IdRequest { public string Id { get; set; } = ""; }
    private sealed class FileActionRequest { public string Action { get; set; } = ""; public string Path { get; set; } = ""; }
    private sealed class AdminRequest { public bool Confirm { get; set; } }
    private sealed class OwnerControlRequest { public bool Confirm { get; set; } public int Minutes { get; set; } = 30; }
    private sealed class MangaRequest { public string Url { get; set; } = ""; public string Input { get; set; } = ""; public bool AllowPaidApi { get; set; } }
    private sealed class TranslationRequest { public string Input { get; set; } = ""; public string Kind { get; set; } = "document"; public string Project { get; set; } = "default"; public string Source { get; set; } = "auto"; public string Target { get; set; } = "pt-BR"; public bool Dubbing { get; set; } }
    private sealed class TranslationStatusRequest { public string Id { get; set; } = ""; }
    private sealed class MangaSearchRequest { public string Query { get; set; } = ""; }
    private sealed class MangaSource { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string BaseUrl { get; set; } = ""; public string[] Languages { get; set; } = Array.Empty<string>(); }
    private sealed class MangaSearchResult { public string Title { get; set; } = ""; public string Url { get; set; } = ""; public string Source { get; set; } = ""; public string Language { get; set; } = ""; public bool PtBr { get; set; } public string Kind { get; set; } = ""; }
    private sealed class ThemeRequest { public string Action { get; set; } = "status"; public string Mode { get; set; } = "dark"; }
    private sealed class ThemeSnapshot { public int? AppsUseLightTheme { get; set; } public int? SystemUsesLightTheme { get; set; } public int? ColorizationColor { get; set; } public int? AccentColor { get; set; } public int? ColorPrevalence { get; set; } public string? Wallpaper { get; set; } }
    public void Dispose() => discovery?.Dispose();
}

