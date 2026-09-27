using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mike.Common;

namespace Mike.Service
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private Process? _hermesProcess;
        private readonly IpcServer _ipcServer;
        private readonly ElevatedIpcServer _elevatedServer;
        private readonly ToolBroker _toolBroker;
        private readonly IMikeInferenceProvider _inferenceProvider;
        private readonly ModelStore _modelStore;
        private readonly ApprovalRegistry _approvalRegistry;
        private readonly IApprovalService _approvalService;
        private readonly MeshManager _meshManager;
        private readonly NatsMessagingService _natsService;
        private readonly LanDiscoveryService? _lanDiscovery;
        private readonly UpdateManager _updateManager;
        private readonly AutonomousEvolution _evolution;
        private readonly string _hermesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin", "hermes", "hermes.exe");
        private bool _hermesUnavailableLogged;
        private readonly string _safeDataRoot = GetWritableDataRoot();
        private string? _activeAutoHealCandidateId;
        private int _consecutiveBackendFailures;

        private static string GetWritableDataRoot()
        {
            string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MikeLocal");
            try
            {
                Directory.CreateDirectory(common);
                string probe = Path.Combine(common, ".write-test");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return common;
            }
            catch
            {
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MikeLocal");
                Directory.CreateDirectory(local);
                return local;
            }
        }

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
            Directory.CreateDirectory(_safeDataRoot);

            // Initialize Model Store
            _modelStore = new ModelStore(Path.Combine(_safeDataRoot, "models"));
            EnsureLocalModels();

            // Initialize Approval System
            _approvalRegistry = new ApprovalRegistry();
            _approvalService = new ApprovalService(_approvalRegistry, _logger);

            // Initialize Tool Broker
            _toolBroker = new ToolBroker(_approvalService);
            RegisterDefaultTools();

            // Initialize Inference with Fallback
            var localFallback = new InferenceRouter(
                new OllamaProvider(), new LlamaCppProvider(), _modelStore);
            _inferenceProvider = new InferenceRouter(
                new HermesProvider(), localFallback, _modelStore);

            // Initialize Mesh and NATS
            _meshManager = new MeshManager();
            _lanDiscovery = LanDiscoveryService.TryCreateFromEnvironment(_meshManager);
            _natsService = new NatsMessagingService(logger, _toolBroker, _meshManager);
            _updateManager = new UpdateManager(logger);
            _evolution = new AutonomousEvolution(Path.Combine(_safeDataRoot, "evolution"));

            // Initialize IPC Servers
            _ipcServer = new IpcServer(logger, _toolBroker, _inferenceProvider, _modelStore, _approvalService);
            _elevatedServer = new ElevatedIpcServer(logger);
        }

        private void RegisterDefaultTools()
        {
            _toolBroker.RegisterTool(new ToolDefinition {
                Id = "sys_info",
                Description = "Returns basic system information",
                RequiredLevel = PermissionLevel.R0,
                Action = async (args) => $"OS: {Environment.OSVersion}, Machine: {Environment.MachineName}"
            });

            // Hermes commonly calls this read-only capability by its standard
            // name. Keep the native alias so the request is executed instead
            // of being shown as raw JSON to the user.
            _toolBroker.RegisterTool(new ToolDefinition {
                Id = "system_info",
                Description = "Returns basic system information",
                RequiredLevel = PermissionLevel.R0,
                Action = async (args) => $"OS: {Environment.OSVersion}, Machine: {Environment.MachineName}"
            });

            _toolBroker.RegisterTool(new ToolDefinition {
                Id = "list_files",
                Description = "Lists files in a directory",
                RequiredLevel = PermissionLevel.R1,
                Action = async (args) => {
                    if (string.IsNullOrEmpty(args)) return "Please provide a path.";
                    var path = SafePath(args);
                    return string.Join("\n", Directory.GetFiles(path));
                }
            });
            _toolBroker.RegisterTool(new ToolDefinition {
                Id = "create_folder",
                Description = "Creates a directory at the specified path",
                RequiredLevel = PermissionLevel.R2,
                Action = async (args) => {
                    if (string.IsNullOrEmpty(args)) return "Please provide a path.";
                    var path = SafePath(args);
                    Directory.CreateDirectory(path);
                    return $"Folder created at {path}";
                }
            });

            _toolBroker.RegisterTool(new ToolDefinition {
                Id = "launch_app",
                Description = "Launches one allow-listed Windows application: calculator, notepad, explorer, or paint",
                RequiredLevel = PermissionLevel.R1,
                Action = async (args) => {
                    string key = (args ?? string.Empty).Trim().ToLowerInvariant();
                    var applications = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                        ["calculator"] = "calc.exe", ["calculadora"] = "calc.exe",
                        ["notepad"] = "notepad.exe", ["bloco de notas"] = "notepad.exe",
                        ["explorer"] = "explorer.exe", ["explorador"] = "explorer.exe",
                        ["paint"] = "mspaint.exe"
                    };
                    if (!applications.TryGetValue(key, out string? executable))
                        return "Aplicativo recusado. Permitidos: calculator, notepad, explorer, paint.";
                    Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                    return $"Aplicativo {key} aberto.";
                }
            });

        }

        private void EnsureLocalModels()
        {
            if (_modelStore.GetDefaultModel("Ollama") is null)
            {
                _modelStore.RegisterModel(new ModelMetadata
                {
                    Id = "ollama-default",
                    Name = "qwen2.5:1.5b",
                    Provider = "Ollama",
                    Config = new Dictionary<string, string>
                    {
                        ["endpoint"] = "http://127.0.0.1:11434/api/generate"
                    }
                });
            }
            if (_modelStore.GetDefaultModel("LlamaCpp") is null)
            {
                _modelStore.RegisterModel(new ModelMetadata
                {
                    Id = "llamacpp-default",
                    Name = "local",
                    Provider = "LlamaCpp",
                    Config = new Dictionary<string, string>
                    {
                        ["endpoint"] = "http://127.0.0.1:8080/completion"
                    }
                });
            }
        }

        private string SafePath(string requested)
        {
            var root = Path.GetFullPath(_safeDataRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(_safeDataRoot, requested));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Path is outside MikeLocal data directory.");
            return candidate;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Mike Service started. Managing Hermes sidecar, IPC, and Tool Broker...");

            _ipcServer.Start();
            _elevatedServer.Start();
            await _natsService.StartAsync();
            string installRoot = AutoHealTaskRecovery.ResolveInstallRoot(AppDomain.CurrentDomain.BaseDirectory);
            await AutoHealTaskRecovery.EnsureAsync(installRoot, _logger, stoppingToken);
            // The desktop/launcher may still be downloading the first model and
            // starting the compatibility API. Triggering AutoHeal here raced that
            // startup, reran prerequisites and repeatedly recycled a healthy agent.
            // Give the normal startup path a grace period; the periodic check below
            // still repairs genuinely headless or failed installations.
            bool compatibilityBackendHealthy = false;
            DateTimeOffset autoHealRecheck = DateTimeOffset.UtcNow.AddMinutes(2);
            if (_lanDiscovery is null)
                _logger.LogInformation("LAN mesh disabled: MIKE_MESH_SHARED_SECRET is not configured.");
            else
            {
                _lanDiscovery.Start();
                _logger.LogInformation("LAN discovery enabled for signed presence only; coordinator={Coordinator}", _meshManager.GetCoordinator().NodeId);
            }
            await _updateManager.CheckForUpdatesAsync(stoppingToken);
            var nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(6);
            var nextEvolutionCheck = DateTimeOffset.UtcNow.AddMinutes(5);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow >= autoHealRecheck)
                {
                    await AutoHealTaskRecovery.EnsureAsync(installRoot, _logger, stoppingToken);
                    compatibilityBackendHealthy = await AutoHealTaskRecovery.IsCompatibilityBackendHealthyAsync(stoppingToken);
                    if (!compatibilityBackendHealthy)
                    {
                        _consecutiveBackendFailures++;
                        if (AutoHealTaskRecovery.TriggerTask())
                            _logger.LogWarning("Compatibility backend remains unavailable; AutoHeal was retriggered.");
                        if (_consecutiveBackendFailures >= 2 && _activeAutoHealCandidateId is null)
                            await DiagnoseAutoHealFailureAsync(stoppingToken);
                        autoHealRecheck = DateTimeOffset.UtcNow.AddMinutes(5);
                    }
                    else
                    {
                        if (_activeAutoHealCandidateId is not null)
                        {
                            _evolution.Evaluate(_activeAutoHealCandidateId,
                                new EvolutionEvaluation(0.85, 0.90, 1.0, 0.80, DateTimeOffset.UtcNow));
                            _logger.LogInformation("AutoHeal recovery recorded as evolution evidence: {CandidateId}.", _activeAutoHealCandidateId);
                            _activeAutoHealCandidateId = null;
                        }
                        _consecutiveBackendFailures = 0;
                        autoHealRecheck = DateTimeOffset.UtcNow.AddHours(6);
                    }
                }
                if (DateTimeOffset.UtcNow >= nextUpdateCheck)
                {
                    await _updateManager.CheckForUpdatesAsync(stoppingToken);
                    nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(6);
                }
                if (DateTimeOffset.UtcNow >= nextEvolutionCheck)
                {
                    _logger.LogInformation(
                        "Automatic evolution audit: {AgentCount} agents, {CandidateCount} candidates.",
                        AutonomousEvolution.Agents.Count, _evolution.ReadCandidates().Count);
                    nextEvolutionCheck = DateTimeOffset.UtcNow.AddMinutes(30);
                }
                if (_hermesProcess == null || _hermesProcess.HasExited)
                {
                    if (!_hermesUnavailableLogged)
                    {
                        _logger.LogWarning("Hermes sidecar is disabled until a verified Hermes binary and protocol are configured.");
                        _hermesUnavailableLogged = true;
                    }
                }

                await Task.Delay(5000, stoppingToken);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _ipcServer.Stop();
            _elevatedServer.Stop();
            _lanDiscovery?.Dispose();
            _natsService.Dispose();
            if (_hermesProcess != null && !_hermesProcess.HasExited)
            {
                _logger.LogInformation("Stopping Hermes sidecar...");
                _hermesProcess.Kill();
            }
            await base.StopAsync(cancellationToken);
        }

        private async Task DiagnoseAutoHealFailureAsync(CancellationToken token)
        {
            const string symptom = "Compatibility backend on loopback port 47885 failed two consecutive health checks after the signed AutoHeal task was triggered.";
            string diagnosis;
            try
            {
                diagnosis = await _inferenceProvider.GenerateAsync(
                    "Analise uma falha no meu computador para a autocura do Mike. " +
                    "Nao execute comandos, nao solicite senhas e nao sugira downloads fora dos fornecedores permitidos. " +
                    "Retorne uma hipotese curta e um teste reversivel. Sintoma: " + symptom,
                    null, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnosis = "Inference unavailable; retain the known signed AutoHeal recovery path. " + ex.GetType().Name;
            }
            EvolutionCandidate candidate = _evolution.Propose(
                "evolution-manager", "auto-heal", symptom + " Diagnosis: " + SanitizeDiagnosis(diagnosis),
                EvolutionRisk.Reversible, 0.70);
            _activeAutoHealCandidateId = candidate.Id;
            _logger.LogWarning("AutoHeal opened quarantined evolution candidate {CandidateId}.", candidate.Id);
        }

        private static string SanitizeDiagnosis(string value)
        {
            string compact = string.Join(' ', (value ?? string.Empty).Split(
                new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return compact.Length <= 1000 ? compact : compact[..1000];
        }

        private void StartHermes()
        {
            _logger.LogWarning("Hermes start refused: binary/protocol verification is pending.");
            return;
            #pragma warning disable CS0162
            try
            {
                if (!File.Exists(_hermesPath))
                {
                    _logger.LogError($"Hermes executable not found at: {_hermesPath}");
                    return;
                }

                // Manage Hermes Token via DPAPI
                string token = SecureStorage.GetSecret("hermes_token");
                if (string.IsNullOrEmpty(token))
                {
                    token = Guid.NewGuid().ToString("N");
                    SecureStorage.SaveSecret("hermes_token", token);
                    _logger.LogInformation("Generated new Hermes token.");
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = _hermesPath,
                    Arguments = $"--port {MikeConstants.HermesPort} --token {token} --auth-mode dpapi",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _hermesProcess = Process.Start(psi);
                _logger.LogInformation($"Hermes sidecar started (PID: {_hermesProcess?.Id})");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to start Hermes: {ex.Message}");
            }
            #pragma warning restore CS0162
        }

    }
}
