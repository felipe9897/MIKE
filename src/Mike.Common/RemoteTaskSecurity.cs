using System.Security.Cryptography;
using System.Text;

namespace Mike.Common;

public sealed class RemoteTaskEnvelope
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceNodeId { get; set; } = "";
    public string TargetNodeId { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public PermissionLevel RequiredLevel { get; set; } = PermissionLevel.R1;
    public long IssuedAtUnix { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long ExpiresAtUnix { get; set; } = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds();
    public string Nonce { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    public string Signature { get; set; } = "";
}

public sealed class RemoteTaskAuthenticator
{
    private readonly byte[] secret;
    private readonly string localNodeId;
    private readonly Dictionary<string, long> consumedNonces = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public RemoteTaskAuthenticator(string localNodeId, byte[] secret)
    {
        if (string.IsNullOrWhiteSpace(localNodeId)) throw new ArgumentException("Local node is required.", nameof(localNodeId));
        if (secret is null || secret.Length < 32) throw new ArgumentException("Remote task secret must contain at least 32 bytes.", nameof(secret));
        this.localNodeId = localNodeId;
        this.secret = secret.ToArray();
    }

    public void Sign(RemoteTaskEnvelope envelope)
    {
        ValidateShape(envelope);
        envelope.Signature = Convert.ToBase64String(HMACSHA256.HashData(secret, Canonical(envelope)));
    }

    public bool TryAuthorize(RemoteTaskEnvelope envelope, out string error, long? nowUnix = null)
    {
        error = "";
        try { ValidateShape(envelope); }
        catch (ArgumentException ex) { error = ex.Message; return false; }
        long now = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(envelope.TargetNodeId), Encoding.UTF8.GetBytes(localNodeId)))
        { error = "wrong_target"; return false; }
        if (envelope.IssuedAtUnix > now + 30 || envelope.ExpiresAtUnix < now || envelope.ExpiresAtUnix - envelope.IssuedAtUnix > 300)
        { error = "expired_or_invalid_window"; return false; }
        byte[] supplied;
        try { supplied = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException) { error = "invalid_signature"; return false; }
        byte[] expected = HMACSHA256.HashData(secret, Canonical(envelope));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
        { error = "invalid_signature"; return false; }
        lock (gate)
        {
            foreach (string expired in consumedNonces.Where(item => item.Value < now).Select(item => item.Key).ToArray()) consumedNonces.Remove(expired);
            if (consumedNonces.ContainsKey(envelope.Nonce)) { error = "replayed_nonce"; return false; }
            consumedNonces[envelope.Nonce] = envelope.ExpiresAtUnix;
        }
        return true;
    }

    private static void ValidateShape(RemoteTaskEnvelope envelope)
    {
        if (envelope is null) throw new ArgumentException("Envelope is required.");
        if (string.IsNullOrWhiteSpace(envelope.Id) || string.IsNullOrWhiteSpace(envelope.SourceNodeId)
            || string.IsNullOrWhiteSpace(envelope.TargetNodeId) || string.IsNullOrWhiteSpace(envelope.TaskType)
            || string.IsNullOrWhiteSpace(envelope.Nonce)) throw new ArgumentException("Remote task identity is incomplete.");
        if (envelope.Payload.Length > 256 * 1024) throw new ArgumentException("Remote task payload is too large.");
        if (!Enum.IsDefined(envelope.RequiredLevel) || envelope.RequiredLevel == PermissionLevel.R4)
            throw new ArgumentException("Remote destructive tasks are not accepted.");
        if (envelope.TaskType.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')))
            throw new ArgumentException("Remote task type is invalid.");
    }

    private static byte[] Canonical(RemoteTaskEnvelope value) => Encoding.UTF8.GetBytes(string.Join("\n",
        value.Id, value.SourceNodeId, value.TargetNodeId, value.TaskType, value.Payload,
        (int)value.RequiredLevel, value.IssuedAtUnix, value.ExpiresAtUnix, value.Nonce));
}
