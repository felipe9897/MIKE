using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mike.Service;

public sealed record UpdateManifest(string Version, string Url, string Sha256, string? ReleasedAt = null);

/// <summary>HTTPS + SHA-256 updater. MSI MajorUpgrade performs transactional
/// replacement/rollback; this stages a verified installer and starts its
/// visible, UAC-controlled flow.</summary>
public sealed class UpdateManager
{
    public const string DefaultManifestUrl = "https://minike.com.br/local-ai/mike-native-release.json";
    private readonly ILogger logger;
    private readonly string manifestUrl;
    private readonly string stateRoot;
    private readonly Version currentVersion;
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private readonly Func<HttpClient> clientFactory;
    private readonly bool launchUpdates;

    public UpdateManager(ILogger logger, string? manifestUrl = null, string? stateRoot = null,
        Version? currentVersion = null, Func<HttpClient>? clientFactory = null, bool launchUpdates = true)
    {
        this.logger = logger;
        this.manifestUrl = manifestUrl ?? DefaultManifestUrl;
        this.stateRoot = stateRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MikeLocal", "updates");
        this.currentVersion = currentVersion ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
        this.clientFactory = clientFactory ?? (() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        this.launchUpdates = launchUpdates;
        Directory.CreateDirectory(this.stateRoot);
    }

    public async Task CheckForUpdatesAsync(CancellationToken token = default)
    {
        if (Environment.GetEnvironmentVariable("MIKE_DISABLE_AUTO_UPDATE") == "1") return;
        if (!await checkGate.WaitAsync(0, token)) return;
        try
        {
            using var client = clientFactory();
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(await client.GetStringAsync(manifestUrl, token), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (!ValidateManifest(manifest, out var available) || available <= currentVersion)
            {
                logger.LogInformation("Mike Local {Version} is up to date.", currentVersion);
                WriteStatus(new { status = "current", current_version = currentVersion.ToString(), checked_at = DateTimeOffset.Now });
                return;
            }

            string package = Path.Combine(stateRoot, $"MikeLocal-{available}.exe");
            string partial = package + ".part";
            logger.LogInformation("Downloading Mike Local {Version} update.", available);
            await using (var input = await client.GetStreamAsync(manifest!.Url, token))
            await using (var output = File.Create(partial)) await input.CopyToAsync(output, token);
            if (!HashMatches(partial, manifest.Sha256)) { File.Delete(partial); throw new InvalidDataException("Update SHA-256 mismatch."); }
            File.Move(partial, package, true);
            WriteStatus(new { status = "ready", current_version = currentVersion.ToString(), available_version = available.ToString(), package, sha256 = manifest.Sha256, checked_at = DateTimeOffset.Now });

            // Services run in Session 0 and cannot safely display UAC. The
            // service stages the verified package; Mike Desktop performs the
            // visible handoff in the interactive user session.
            if (launchUpdates && Environment.UserInteractive)
            {
                Process.Start(new ProcessStartInfo(package, "/passive /norestart") { UseShellExecute = true });
                logger.LogInformation("Verified update {Version} started interactively.", available);
            }
            else logger.LogInformation("Verified update {Version} staged for Mike Desktop.", available);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning("Automatic update check failed safely: {Message}", ex.Message);
            WriteStatus(new { status = "failed", current_version = currentVersion.ToString(), error = ex.Message, checked_at = DateTimeOffset.Now });
        }
        finally { checkGate.Release(); }
    }

    public static bool ValidateManifest(UpdateManifest? manifest, out Version version)
    {
        version = new Version(0, 0);
        return manifest is not null && Version.TryParse(manifest.Version, out version!) &&
            Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            (uri.Host.Equals("minike.com.br", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".minike.com.br", StringComparison.OrdinalIgnoreCase)) &&
            manifest.Sha256.Length == 64 && manifest.Sha256.All(Uri.IsHexDigit);
    }

    public static bool HashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private void WriteStatus(object value)
    {
        string path = Path.Combine(stateRoot, "status.json"), temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, true);
    }
}
