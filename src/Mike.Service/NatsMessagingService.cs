using Microsoft.Extensions.Logging;
using Mike.Common;

namespace Mike.Service;

/// Mesh remains fail-closed until verified TLS/mTLS and JetStream are configured.
public sealed class NatsMessagingService : IDisposable
{
    private readonly ILogger _logger;
    public NatsMessagingService(ILogger logger, ToolBroker toolBroker, MeshManager meshManager) => _logger = logger;
    public Task StartAsync()
    {
        _logger.LogWarning("Mike Mesh is disabled: secure NATS/mTLS configuration is unavailable.");
        return Task.CompletedTask;
    }
    public void Stop() { }
    public void Dispose() => Stop();
}
