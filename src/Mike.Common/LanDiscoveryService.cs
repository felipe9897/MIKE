using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mike.Common;

/// <summary>Small, opt-in LAN presence layer. It only exchanges signed metadata;
/// it is deliberately not a remote-command transport.</summary>
public sealed class LanDiscoveryService : IDisposable
{
    public const int Port = 47977;
    public const string MulticastAddress = "239.255.77.77";
    private readonly MeshManager _mesh;
    private readonly byte[] _secret;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public LanDiscoveryService(MeshManager mesh, string sharedSecret)
    {
        _mesh = mesh;
        if (string.IsNullOrWhiteSpace(sharedSecret) || sharedSecret.Length < 16)
            throw new ArgumentException("A chave compartilhada deve ter pelo menos 16 caracteres.", nameof(sharedSecret));
        _secret = Encoding.UTF8.GetBytes(sharedSecret);
        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = false };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        try { _udp.JoinMulticastGroup(IPAddress.Parse(MulticastAddress)); } catch { /* LAN sem multicast continua recebendo unicast */ }
    }

    public static LanDiscoveryService? TryCreateFromEnvironment(MeshManager mesh)
    {
        var secret = Environment.GetEnvironmentVariable("MIKE_MESH_SHARED_SECRET");
        // The product discovery key authenticates Mike presence packets only;
        // it never authorizes commands or file access. A private shared secret,
        // when configured, replaces it for managed/private clusters.
        secret = string.IsNullOrWhiteSpace(secret) ? "MikeLocal-LAN-Presence-v1-2026" : secret;
        return new LanDiscoveryService(mesh, secret);
    }

    public void Start()
    {
        _loop ??= Task.Run(ReceiveLoopAsync);
        _ = Task.Run(AnnounceLoopAsync);
    }

    private async Task AnnounceLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        await AnnounceAsync();
        while (await timer.WaitForNextTickAsync(_stop.Token)) await AnnounceAsync();
    }

    private async Task AnnounceAsync()
    {
        var payload = new Advertisement
        {
            NodeId = _mesh.LocalNode.NodeId, Hostname = _mesh.LocalNode.Hostname,
            IpAddress = _mesh.LocalNode.IpAddress, HardwareTier = _mesh.LocalNode.HardwareTier,
            ProcessorCount = _mesh.LocalNode.ProcessorCount, RamGb = _mesh.LocalNode.RamGb,
            CurrentLoad = _mesh.LocalNode.CurrentLoad, SupportedTasks = _mesh.LocalNode.SupportedTasks,
            SentAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        payload.Signature = Sign(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(MulticastAddress), Port));
    }

    private async Task ReceiveLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var packet = await _udp.ReceiveAsync(_stop.Token);
                if (packet.Buffer.Length > 16 * 1024) continue;
                var item = JsonSerializer.Deserialize<Advertisement>(packet.Buffer);
                if (item is null || item.NodeId == _mesh.LocalNode.NodeId || !IsFresh(item.SentAtUnix) || !Verify(item)) continue;
                _mesh.UpdatePeer(new NodeIdentity { NodeId=item.NodeId, Hostname=item.Hostname,
                    IpAddress=item.IpAddress, HardwareTier=item.HardwareTier,
                    ProcessorCount=item.ProcessorCount, RamGb=item.RamGb,
                    CurrentLoad=item.CurrentLoad, SupportedTasks=item.SupportedTasks,
                    LastSeen=DateTime.UtcNow });
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (_stop.IsCancellationRequested) { break; }
            catch (JsonException) { }
        }
    }

    private string Sign(Advertisement value) => Convert.ToBase64String(HMACSHA256.HashData(_secret, Canonical(value)));
    private bool Verify(Advertisement value)
    {
        var supplied = value.Signature;
        value.Signature = "";
        try {
            var expected = Convert.FromBase64String(Sign(value));
            var actual = Convert.FromBase64String(supplied ?? "");
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        } catch (FormatException) { return false; }
        finally { value.Signature = supplied; }
    }
    private static byte[] Canonical(Advertisement v) => Encoding.UTF8.GetBytes(string.Join("|", v.NodeId,v.Hostname,v.IpAddress,v.HardwareTier,v.ProcessorCount,v.RamGb.ToString("R",System.Globalization.CultureInfo.InvariantCulture),v.SentAtUnix));
    private static bool IsFresh(long timestamp) => Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) <= 120;

    public void Dispose()
    {
        _stop.Cancel();
        try { _udp.DropMulticastGroup(IPAddress.Parse(MulticastAddress)); } catch { }
        _udp.Dispose(); _stop.Dispose();
    }
    private sealed class Advertisement
    {
        public string NodeId { get; set; } = ""; public string Hostname { get; set; } = "";
        public string IpAddress { get; set; } = ""; public string HardwareTier { get; set; } = "unknown";
        public int ProcessorCount { get; set; } public double RamGb { get; set; }
        public double CurrentLoad { get; set; } public string[] SupportedTasks { get; set; } = Array.Empty<string>();
        public long SentAtUnix { get; set; } public string Signature { get; set; } = "";
    }
}
