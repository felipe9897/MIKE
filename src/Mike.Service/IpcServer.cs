using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mike.Common;

namespace Mike.Service
{
    public class IpcServer
    {
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ToolBroker _toolBroker;
        private readonly IMikeInferenceProvider _inference;
        private readonly MikeAgentRuntime _agentRuntime;
        private readonly IApprovalService _approvalService;

        public IpcServer(ILogger logger, ToolBroker toolBroker, IMikeInferenceProvider inference, ModelStore modelStore, IApprovalService approvalService)
        {
            _logger = logger;
            _toolBroker = toolBroker;
            _inference = inference;
            _approvalService = approvalService;
            _agentRuntime = new MikeAgentRuntime(_inference, _toolBroker, modelStore);
        }

        public void Start()
        {
            Task.Run(() => ListenLoop(_cts.Token));
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    NamedPipeServerStream? server = NamedPipeSecurity.CreateServer("MikeLocalIPC");
                    try
                    {
                        _logger.LogInformation("IPC Server waiting for connection...");
                        await server.WaitForConnectionAsync(token);
                        _logger.LogInformation("IPC Client connected.");
                        _ = HandleClientAsync(server, token);
                        server = null;
                    }
                    finally
                    {
                        server?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"IPC Server error: {MikeLogger.Redact(ex.Message)}");
                    await Task.Delay(1000, token);
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken token)
        {
            using (server)
            {
                try
                {
                    PermissionLevel sessionLevel = GetSessionPermissionLevel();
                    string request = await MikeIpcProtocol.ReadMessageAsync(server, token);
                    _logger.LogInformation($"Received IPC request: {MikeLogger.Redact(request)}");

                    string response;
                    if (request.StartsWith("approve:"))
                    {
                        string approvalId = request.Substring(8);
                        _approvalService.MarkApproved(approvalId);
                        response = System.Text.Json.JsonSerializer.Serialize(new { type = "success", message = "Approval granted." });
                    }
                    else if (request.StartsWith("tool:execute_approved|"))
                    {
                        var parts = request.Substring(22).Split('|');
                        if (parts.Length < 2)
                        {
                            response = $"{{\"error\": \"Invalid approved tool request format.\"}}";
                        }
                        else
                        {
                            string approvalId = parts[0];
                            string toolId = parts[1];
                            string args = parts.Length > 2 ? string.Join("|", parts[2..]) : "";
                            response = await _toolBroker.ExecuteToolAsync(toolId, args, sessionLevel, approvalId);
                        }
                    }
                    else if (request.StartsWith("tool:"))
                    {
                        var parts = request.Substring(5).Split('|');
                        string toolId = parts[0];
                        string args = parts.Length > 1 ? string.Join("|", parts[1..]) : "";
                        response = await _toolBroker.ExecuteToolAsync(toolId, args, sessionLevel);
                    }
                    else
                    {
                        response = await _agentRuntime.ExecuteLoopAsync(request, sessionLevel);
                    }

                    await MikeIpcProtocol.WriteMessageAsync(server, response);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"IPC Client handler error: {MikeLogger.Redact(ex.Message)}");
                    try
                    {
                        string errorResponse = System.Text.Json.JsonSerializer.Serialize(new {
                            type = "error",
                            message = "Internal service error: " + ex.Message
                        });
                        await MikeIpcProtocol.WriteMessageAsync(server, errorResponse);
                    }
                    catch { /* Ignore errors while reporting errors */ }
                }
            }
        }

        private static PermissionLevel GetSessionPermissionLevel()
        {
            if (!OperatingSystem.IsWindows()) return PermissionLevel.R1;
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            bool elevated = new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            if (!elevated) return PermissionLevel.R1;
            try
            {
                string state = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MikeLocal", "data", "owner-control-session.json");
                using var document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(state));
                long until = document.RootElement.GetProperty("until").GetInt64();
                return until > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? PermissionLevel.R3 : PermissionLevel.R1;
            }
            catch { return PermissionLevel.R1; }
        }

        public void Stop()
        {
            _cts.Cancel();
        }
    }
}
