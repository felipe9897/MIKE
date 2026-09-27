using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Mike.Common;

namespace Mike.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly NativeApiBridge nativeApi = new();
        private readonly DesktopControlIpcServer desktopControlIpc;
        public MainWindow()
        {
            InitializeComponent();
            desktopControlIpc = new DesktopControlIpcServer(nativeApi);
            desktopControlIpc.Start();
            InitializeAsync();
        }

        protected override void OnClosed(EventArgs e)
        {
            desktopControlIpc.Dispose();
            nativeApi.Dispose();
            base.OnClosed(e);
        }

        private async void InitializeAsync()
        {
            try
            {
                await ActivateOwnerControlSessionAsync();
                await EnsureLegacyMigrationAsync();
                SetNativeStartup("Preparando a interface...", "Verificando o Microsoft WebView2.");
                await EnsureWebView2RuntimeAsync();
                // Initialize WebView2 environment
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MikeLocal", "WebView2");
                Directory.CreateDirectory(dataDir);
                var environment = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await webView.EnsureCoreWebView2Async(environment);
                webView.Visibility = Visibility.Visible;
                startupPanel.Visibility = Visibility.Collapsed;
                webView.CoreWebView2.NavigateToString(
                    "<html><body style='margin:0;background:#10131a;color:#f5f7ff;font:16px Segoe UI;display:grid;place-items:center;height:100vh'><main style='width:min(680px,82vw)'><h1>Preparando o Mike</h1><p id='stage'>Iniciando verificação...</p><div style='height:14px;background:#252b38;border-radius:9px;overflow:hidden'><div id='bar' style='height:100%;width:4%;background:linear-gradient(90deg,#26d9ff,#7668ff);transition:width .4s'></div></div><div style='display:flex;justify-content:space-between;margin-top:9px;color:#aab3c5'><span id='pct'>4%</span><span id='time'>Tempo decorrido: 0s</span></div><pre id='details' style='height:150px;overflow:auto;white-space:pre-wrap;background:#0a0d13;border:1px solid #2a3242;border-radius:10px;padding:12px;color:#aab3c5;font:13px Consolas;margin-top:18px'>A preparação é automática. Você pode acompanhar cada etapa aqui.</pre></main><script>let s=Date.now();setInterval(()=>{let n=Math.floor((Date.now()-s)/1000),m=Math.floor(n/60);document.getElementById('time').textContent='Tempo decorrido: '+(m?m+'m ':'')+(n%60)+'s';},1000);window.mikeProgress=(p,t,d)=>{bar.style.width=p+'%';pct.textContent=p+'%';stage.textContent=t;if(d){details.textContent=(details.textContent+'\\n'+d).split('\\n').slice(-9).join('\\n');details.scrollTop=details.scrollHeight;}};</script></body></html>");

                await EnsureBackendAsync();
                await ReportProgressAsync(7, "Verificando atualizações...", "Consultando o canal estável da Mike.");
                if (await CheckAndLaunchUpdateAsync())
                {
                    Application.Current.Shutdown();
                    return;
                }
                await ReportProgressAsync(10, "Iniciando o núcleo local...", "Serviço principal verificado; aguardando a API local.");
                await EnsureLegacyToolsBackendAsync();
                await ReportProgressAsync(38, "Núcleo local pronto", "A API local respondeu. Verificando os componentes selecionados.");
                await RunPrerequisitesInBackgroundAsync();
                _ = EnsureTranslatorDependenciesAsync();
                await ReportProgressAsync(94, "Validando o conjunto instalado...", "Confirmando novamente a API local depois dos instaladores e testes.");
                if (!await IsLegacyToolsBackendReadyAsync())
                    await EnsureLegacyToolsBackendAsync(fastRecovery: true);
                if (!await IsLegacyToolsBackendReadyAsync())
                    throw new InvalidOperationException("A preparação terminou, mas o núcleo local não respondeu na porta 47885.");
                await ReportProgressAsync(96, "Componentes verificados", "Preparando a interface principal.");

                // Load the UI from the local directory
                string[] candidates =
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "mike-ui.html"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "ui", "mike-ui.html"),
                    Path.Combine(Environment.CurrentDirectory, "local-ai", "mike-ui.html")
                };
                string? uiPath = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
                if (uiPath is null)
                    throw new FileNotFoundException("A interface mike-ui.html não foi encontrada na instalação.");

                string installRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(uiPath)!, ".."));
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "mike.local", installRoot, CoreWebView2HostResourceAccessKind.Allow);
                webView.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    if (!args.IsSuccess)
                        ShowStartupError($"Falha ao carregar a interface ({args.WebErrorStatus}).");
                };
                bool openManga = Environment.GetCommandLineArgs().Any(arg => arg.Equals("--view=manga", StringComparison.OrdinalIgnoreCase));
                webView.CoreWebView2.Navigate("https://mike.local/ui/mike-ui.html" + (openManga ? "?view=manga" : ""));

                // Listen for messages from the frontend
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            }
            catch (Exception ex)
            {
                SetNativeStartup("Não foi possível abrir a interface", ex.Message, failed: true);
                ShowStartupError($"Erro ao iniciar a Mike: {ex.Message}");
            }
        }

        private void SetNativeStartup(string stage, string detail, bool failed = false)
        {
            startupStage.Text = stage;
            startupDetail.Text = detail;
            startupProgress.IsIndeterminate = !failed;
            startupProgress.Visibility = failed ? Visibility.Collapsed : Visibility.Visible;
            startupPanel.Visibility = Visibility.Visible;
        }

        private async Task<bool> CheckAndLaunchUpdateAsync()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MikeLocal", "updates");
            Directory.CreateDirectory(root);
            string statusPath = Path.Combine(root, "status.json");
            using var updateMutex = new Mutex(false, "Global\\MikeLocal-AutoUpdate");
            bool acquired;
            try { acquired = updateMutex.WaitOne(0); } catch { acquired = false; }
            if (!acquired) return false;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                string manifestJson = await client.GetStringAsync("https://minike.com.br/local-ai/mike-native-release.json");
                using JsonDocument manifest = JsonDocument.Parse(manifestJson);
                JsonElement value = manifest.RootElement;
                string versionText = value.GetProperty("version").GetString() ?? "";
                string url = value.GetProperty("url").GetString() ?? "";
                string expectedHash = value.GetProperty("sha256").GetString() ?? "";
                Version current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
                if (!Version.TryParse(versionText, out Version? available) || available <= current) return false;
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    !(uri.Host.Equals("minike.com.br", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".minike.com.br", StringComparison.OrdinalIgnoreCase)) ||
                    expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)) return false;
                string package = Path.Combine(root, $"MikeLocal-{available}.exe");
                bool validExisting = File.Exists(package) && FileHashMatches(package, expectedHash);
                if (!validExisting)
                {
                    string partial = package + ".part";
                    await ReportProgressAsync(8, $"Baixando atualização {available}...", "O download é oculto e será validado por SHA-256 antes de executar.");
                    long existingBytes = File.Exists(partial) ? new FileInfo(partial).Length : 0;
                    using HttpRequestMessage request = new(HttpMethod.Get, uri);
                    if (existingBytes > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
                    using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    bool resumed = existingBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                    if (!resumed) existingBytes = 0;
                    await using Stream input = await response.Content.ReadAsStreamAsync();
                    await using FileStream output = new(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                    byte[] buffer = new byte[1024 * 1024];
                    long downloaded = existingBytes;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                        downloaded += read;
                        if (downloaded % (16L * 1024 * 1024) < read)
                            await ReportProgressAsync(8, $"Baixando atualização {available}...", $"{downloaded / 1048576:N0} MB recebidos; o download pode ser retomado.");
                    }
                    await output.FlushAsync();
                    if (!FileHashMatches(partial, expectedHash))
                        throw new InvalidDataException("SHA-256 da atualização não confere.");
                    File.Move(partial, package, true);
                }
                Process? process = Process.Start(new ProcessStartInfo(package, "/passive /norestart") { UseShellExecute = true });
                if (process is null) return false;
                File.WriteAllText(statusPath, JsonSerializer.Serialize(new { status = "launching", package, pid = process.Id, launched_at = DateTimeOffset.Now }));
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { return false; /* UAC cancelado: tenta novamente na próxima abertura. */ }
            catch (Exception ex)
            {
                File.WriteAllText(statusPath, JsonSerializer.Serialize(new { status = "failed", error = ex.Message, checked_at = DateTimeOffset.Now }));
                return false;
            }
            finally { try { updateMutex.ReleaseMutex(); } catch { } }
        }

        private static bool FileHashMatches(string path, string expectedHash)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task EnsureWebView2RuntimeAsync()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(CoreWebView2Environment.GetAvailableBrowserVersionString())) return;
            }
            catch { }

            string installer = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebView2RuntimeInstallerX64.exe");
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            using (var response = await client.GetAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703", HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(installer);
                await response.Content.CopyToAsync(output);
            }
            using var process = Process.Start(new ProcessStartInfo(installer)
            {
                Arguments = "/silent /install",
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Não foi possível iniciar o instalador do WebView2.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode is not (0 or 3010))
                throw new InvalidOperationException($"WebView2 retornou código {process.ExitCode}.");
        }

        private async Task RunPrerequisitesInBackgroundAsync()
        {
            string stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal");
            Directory.CreateDirectory(stateRoot);
            string stateFile = Path.Combine(stateRoot, "setup-status.json");
            string setupVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            try
            {
                if (File.Exists(stateFile))
                {
                    using JsonDocument previous = JsonDocument.Parse(await File.ReadAllTextAsync(stateFile));
                    JsonElement root = previous.RootElement;
                    bool ready = root.TryGetProperty("status", out var status) && status.GetString() is "ready" or "verified";
                    bool sameVersion = root.TryGetProperty("version", out var version) && version.GetString() == setupVersion;
                    if (ready && sameVersion)
                    {
                        await ReportProgressAsync(92, "Componentes já preparados", $"Preparação {setupVersion} validada; nenhuma reinstalação necessária.");
                        return;
                    }
                }
                await File.WriteAllTextAsync(stateFile, JsonSerializer.Serialize(new { status = "running", version = setupVersion, started_at = DateTimeOffset.Now }));
                await EnsurePrerequisitesAsync();
                bool backendReady = await IsLegacyToolsBackendReadyAsync();
                await File.WriteAllTextAsync(stateFile, JsonSerializer.Serialize(new { status = backendReady ? "verified" : "core_restart_required", version = setupVersion, completed_at = DateTimeOffset.Now, backend_ready = backendReady }));
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(stateFile, JsonSerializer.Serialize(new { status = "partial", version = setupVersion, error = ex.Message, checked_at = DateTimeOffset.Now }));
            }
        }

        private async Task EnsurePrerequisitesAsync()
        {
            string script = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "MikePrerequisites.ps1"));
            if (!File.Exists(script))
                throw new FileNotFoundException("Preparador de Ollama/WebView2 não foi instalado.", script);

            string windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            string setupLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "prerequisites-live.log");
            var process = Process.Start(new ProcessStartInfo(windowsPowerShell)
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -InstallMissing -PostInstall -InstallRoot \"{Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."))}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Não foi possível iniciar a preparação da IA local.");
            int progress = 8;
            DateTimeOffset lastHeartbeat = DateTimeOffset.MinValue;
            async Task PumpAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is string line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    progress = Math.Min(82, progress + 3);
                    await File.AppendAllTextAsync(setupLog, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");
                    await Dispatcher.InvokeAsync(() => ReportProgressAsync(progress, FriendlyStage(line), line));
                }
            }
            var outputTask = PumpAsync(process.StandardOutput);
            var errorTask = PumpAsync(process.StandardError);
            Task exitTask = process.WaitForExitAsync();
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(exitTask, Task.Delay(2000));
                if (exitTask.IsCompleted) break;
                if (DateTimeOffset.Now - lastHeartbeat >= TimeSpan.FromSeconds(15))
                {
                    lastHeartbeat = DateTimeOffset.Now;
                    process.Refresh();
                    string activity = $"Processo ativo: PID {process.Id}; CPU {process.TotalProcessorTime.TotalSeconds:F0}s; memória {process.WorkingSet64 / 1048576:F0} MB. Nenhum componente lento será encerrado.";
                    await File.AppendAllTextAsync(setupLog, $"{DateTimeOffset.Now:O} HEARTBEAT {activity}{Environment.NewLine}");
                    await ReportProgressAsync(progress, "Preparação em andamento...", activity);
                }
            }
            await exitTask;
            await Task.WhenAll(outputTask, errorTask);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"A preparação da IA local falhou (código {process.ExitCode}).");
        }

        private static async Task EnsureTranslatorDependenciesAsync()
        {
            string localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal");
            string whisperRoot = Path.Combine(localRoot, "secondary-engines", "whisper_cpp");
            if (File.Exists(Path.Combine(whisperRoot, "bin", "whisper-cli.exe")) &&
                Directory.Exists(Path.Combine(whisperRoot, "models")) &&
                Directory.EnumerateFiles(Path.Combine(whisperRoot, "models"), "ggml-*.bin").Any()) return;

            string marker = Path.Combine(localRoot, "translator-dependencies.json");
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromMinutes(30)) return;
            Directory.CreateDirectory(localRoot);
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { status = "installing", started_at = DateTimeOffset.Now }));
            string script = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "installer-tools", "install-mike-secondary-engines.ps1"));
            if (!File.Exists(script)) return;
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo(powershell)
                {
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -InstallRoot \"{localRoot}\" -Modules whisper_cpp -SkipModelDownloads",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (process is null) throw new InvalidOperationException("O preparador do Tradutor nao iniciou.");
                await process.WaitForExitAsync();
                bool ready = process.ExitCode == 0 && File.Exists(Path.Combine(whisperRoot, "bin", "whisper-cli.exe"));
                await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { status = ready ? "ready" : "failed", exit_code = process.ExitCode, completed_at = DateTimeOffset.Now }));
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { status = "failed", error = ex.Message, completed_at = DateTimeOffset.Now }));
            }
        }

        private static string FriendlyStage(string line)
        {
            string value = line.ToLowerInvariant();
            if (value.Contains("ollama") || value.Contains("modelo")) return "Verificando a inteligência local...";
            if (value.Contains("webview")) return "Verificando o motor da interface...";
            if (value.Contains("hermes") || value.Contains("agent")) return "Preparando agentes e Hermes...";
            if (value.Contains("computer") || value.Contains("cua")) return "Verificando automação do Windows...";
            if (value.Contains("download") || value.Contains("baix")) return "Baixando componente necessário...";
            return "Configurando componentes locais...";
        }

        private async Task ReportProgressAsync(int percent, string stage, string detail)
        {
            if (webView.CoreWebView2 is null) return;
            string args = JsonSerializer.Serialize(new object[] { percent, stage, detail });
            await webView.CoreWebView2.ExecuteScriptAsync($"window.mikeProgress&&window.mikeProgress(...{args})");
        }

        private static async Task EnsureLegacyMigrationAsync()
        {
            string marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "migration-1.0.24.done");
            if (File.Exists(marker)) return;
            string script = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "MikeLegacyMigration.ps1"));
            if (!File.Exists(script)) return;
            var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null) return;
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                await File.WriteAllTextAsync(marker, DateTimeOffset.Now.ToString("O"));
            }
        }

        private static async Task EnsureBackendAsync()
        {
            try
            {
                using var probe = new NamedPipeClientStream(".", MikeConstants.PipeName, PipeDirection.InOut);
                await probe.ConnectAsync(250);
                return;
            }
            catch { }

            string serviceExe = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "service", "Mike.Service.exe"));
            if (!File.Exists(serviceExe)) return;
            if (Process.GetProcessesByName("Mike.Service").Length == 0)
            {
                Process.Start(new ProcessStartInfo(serviceExe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                await Task.Delay(1000);
            }
        }

        private static async Task ActivateOwnerControlSessionAsync()
        {
            string? value = Environment.GetCommandLineArgs()
                .FirstOrDefault(arg => arg.StartsWith("--owner-control-until=", StringComparison.OrdinalIgnoreCase));
            if (value is null || !long.TryParse(value.Split('=', 2)[1], out long until) ||
                until <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return;

            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal", "data");
            Directory.CreateDirectory(data);
            await File.WriteAllTextAsync(Path.Combine(data, "owner-control-session.json"), JsonSerializer.Serialize(new { until }));

            foreach (Process process in Process.GetProcessesByName("Mike.Service"))
            {
                try { process.Kill(true); await process.WaitForExitAsync(); } catch { }
                finally { process.Dispose(); }
            }
        }

        private async Task EnsureLegacyToolsBackendAsync(bool fastRecovery = false)
        {
            if (await IsLegacyToolsBackendReadyAsync()) return;

            string script = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "tools", "mike-local-ai-backend.ps1"));
            if (!File.Exists(script))
                throw new FileNotFoundException("O núcleo local não foi instalado.", script);

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MINIKE-Local-AI");
            string windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            string startupArgs = " -SecondaryEnginesPreset disabled -SkipSecondaryModelDownloads -SkipSlowModeSetup -SkipDependencies";
            Process? process = Process.Start(new ProcessStartInfo(windowsPowerShell)
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -BackendOnly -InstallRoot \"{root}\"{startupArgs}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            // Não existe timeout destrutivo: enquanto o núcleo inicia ou a autocura
            // trabalha, a tela de preparação permanece visível. Se o processo terminar
            // sem abrir a porta, uma nova tentativa é feita sem matar tarefas em curso.
            int waitPulse = 10;
            while (true)
            {
                await Task.Delay(500);
                if (await IsLegacyToolsBackendReadyAsync()) return;
                if (++waitPulse % 4 == 0)
                    await ReportProgressAsync(Math.Min(36, 10 + waitPulse / 4),
                        "Iniciando o núcleo local...",
                        "Atividade detectada; aguardando a porta local 47885 responder.");
                if (process is not null && !process.HasExited) continue;

                await Task.Delay(2500);
                process?.Dispose();
                process = Process.Start(new ProcessStartInfo(windowsPowerShell)
                {
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -BackendOnly -InstallRoot \"{root}\"{startupArgs}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
        }

        private static async Task<bool> IsLegacyToolsBackendReadyAsync()
        {
            try
            {
                // The PowerShell compatibility backend is intentionally single-threaded.
                // Startup/background maintenance can make a healthy status probe take a
                // few seconds, especially on HDDs and 4-core PCs. A 700 ms timeout made
                // the desktop launch another installer and recycle the healthy agent.
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await http.GetAsync("http://127.0.0.1:47885/ui/status");
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private void ShowStartupError(string message)
        {
            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "desktop.log"),
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
                webView.CoreWebView2?.NavigateToString(
                    $"<html><body style='font:16px Segoe UI;padding:32px'><h2>Mike não conseguiu abrir</h2><p>{System.Net.WebUtility.HtmlEncode(message)}</p></body></html>");
            }
            catch { }
        }

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string messageJson = e.TryGetWebMessageAsString();

            try
            {
                if (messageJson.StartsWith('{'))
                {
                    using var envelope = JsonDocument.Parse(messageJson);
                    var root = envelope.RootElement;
                    if (root.TryGetProperty("type", out var type) && type.GetString() == "ipc.request")
                    {
                        var id = root.GetProperty("id").GetString() ?? string.Empty;
                        var path = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() ?? string.Empty : string.Empty;
                        var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() ?? "GET" : "GET";
                        var body = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
                        if (path != "/ui/chat")
                        {
                            var data = await nativeApi.HandleAsync(path, method, body);
                            webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "bridge.response", id, data }));
                            return;
                        }
                        var prepared = nativeApi.PrepareChat(body);
                        var computerResponse = nativeApi.TryComputerChat(prepared);
                        if (computerResponse is not null)
                        {
                            webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "bridge.response", id, data = computerResponse }));
                            return;
                        }
                        var mangaResponse = await nativeApi.TryMangaChatAsync(prepared, body);
                        if (mangaResponse is not null)
                        {
                            webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "bridge.response", id, data = mangaResponse }));
                            return;
                        }
                        var catalogResponse = await nativeApi.TryCatalogChatAsync(prepared, body);
                        if (catalogResponse is not null)
                        {
                            webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(new { type = "bridge.response", id, data = catalogResponse }));
                            return;
                        }
                        var answer = await MikeIpc.SendRequestAsync(prepared.Prompt);
                        webView.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(new
                        {
                            type = "bridge.response",
                            id,
                            data = nativeApi.CompleteChat(prepared, answer)
                        }));
                        return;
                    }
                }
                // Route the message to the Mike Service via Named Pipes
                string response = await MikeIpc.SendRequestAsync(messageJson);

                // Send the response back to the frontend
                webView.CoreWebView2.PostWebMessageAsString(response);
            }
            catch (Exception ex)
            {
                webView.CoreWebView2.PostWebMessageAsString($"IPC Error: {ex.Message}");
            }
        }
    }
}
