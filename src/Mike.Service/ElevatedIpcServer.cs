using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mike.Common;

namespace Mike.Service
{
    public class ElevatedIpcServer
    {
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public ElevatedIpcServer(ILogger logger)
        {
            _logger = logger;
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
                    NamedPipeServerStream? server = NamedPipeSecurity.CreateServer(ElevatedBroker.PipeName, administrativeOnly: true);
                    try
                    {
                        await server.WaitForConnectionAsync(token);
                        if (!NamedPipeSecurity.IsAdministrator(server))
                        {
                            _logger.LogWarning("Rejected elevated IPC client that is not an administrator.");
                            server.Disconnect();
                            continue;
                        }
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
                    _logger.LogError($"Elevated IPC Server error: {ex.Message}");
                    await Task.Delay(1000, token);
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken token)
        {
            using (server)
            {
                // No elevated command is executable until a reviewed allow-list,
                // nonce/expiry checks and an explicit UAC flow are implemented.
                // Reading a raw command here would turn this pipe into an admin
                // shell, so fail closed and never echo the request into logs.
                _ = await MikeIpcProtocol.ReadMessageAsync(server, token);
                await MikeIpcProtocol.WriteMessageAsync(server,
                    "{\"type\":\"error\",\"code\":\"elevated_actions_disabled\",\"message\":\"Elevated actions are disabled until a signed policy is configured.\"}");
            }
        }

        public void Stop()
        {
            _cts.Cancel();
        }
    }
}
