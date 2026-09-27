using System.IO.Pipes;
using System.Text.Json;
using Mike.Common;

namespace Mike.Desktop;

/// <summary>
/// Runs inside the interactive Windows session so authenticated local relays can
/// request visible desktop actions without trying to send input from session 0.
/// NativeApiBridge remains the policy boundary and requires Owner Control.
/// </summary>
public sealed class DesktopControlIpcServer : IDisposable
{
    public const string PipeName = "MikeLocalDesktopControl";
    private readonly NativeApiBridge bridge;
    private readonly CancellationTokenSource cancellation = new();
    private Task? listener;

    public DesktopControlIpcServer(NativeApiBridge bridge) => this.bridge = bridge;

    public void Start() => listener ??= Task.Run(() => ListenAsync(cancellation.Token));

    private async Task ListenAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeSecurity.CreateServer(PipeName, currentUserOnly: true);
                await server.WaitForConnectionAsync(token);
                _ = HandleAsync(server, token);
                server = null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch
            {
                server?.Dispose();
                try { await Task.Delay(500, token); } catch (OperationCanceledException) { }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken token)
    {
        using (server)
        {
            try
            {
                string requestJson = await MikeIpcProtocol.ReadMessageAsync(server, token);
                var request = JsonSerializer.Deserialize<DesktopControlRequest>(requestJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (request is null || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 4096)
                {
                    await MikeIpcProtocol.WriteMessageAsync(server, JsonSerializer.Serialize(new { ok = false, handled = false, error = "invalid_request" }));
                    return;
                }

                if (!NativeApiBridge.CanHandleComputerChat(request.Message))
                {
                    await MikeIpcProtocol.WriteMessageAsync(server, JsonSerializer.Serialize(new { ok = true, handled = false }));
                    return;
                }
                var prepared = bridge.PrepareChat(JsonSerializer.Serialize(new
                {
                    message = request.Message,
                    conversation_id = request.ConversationId
                }));
                object? result = bridge.TryComputerChat(prepared);
                await MikeIpcProtocol.WriteMessageAsync(server, JsonSerializer.Serialize(
                    result ?? new { ok = true, handled = false }));
            }
            catch
            {
                try { await MikeIpcProtocol.WriteMessageAsync(server, JsonSerializer.Serialize(new { ok = false, handled = false, error = "desktop_ipc_failed" })); }
                catch { }
            }
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private sealed class DesktopControlRequest
    {
        public string Message { get; set; } = "";
        public string? ConversationId { get; set; }
    }
}
