using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Persistence;

namespace TinadecCore.Peers;

public sealed class PeerIdentityService : IPeerIdentityService, IDisposable
{
    private readonly IDbContextFactory<PeerDbContext> _dbFactory;
    private readonly ISecretStore _secretStore;
    private readonly IPeerPolicyService _policyService;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _identityGate = new(1, 1);

    public PeerIdentityService(
        IDbContextFactory<PeerDbContext> dbFactory,
        ISecretStore secretStore,
        IPeerPolicyService policyService,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _secretStore = secretStore;
        _policyService = policyService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        PeerSecretKeys.ValidateAll();
    }

    public async Task<PeerLocalIdentitySnapshot> EnsureLocalIdentityAsync(CancellationToken cancellationToken = default)
    {
        var settings = await PeerMigrations.EnsureInboundSettingsDefaultAsync(_dbFactory, _timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var material = await EnsureCertificateMaterialAsync(settings.LocalDeviceId, cancellationToken)
            .ConfigureAwait(false);
        return new PeerLocalIdentitySnapshot(
            settings.LocalDeviceId,
            settings.DisplayName,
            settings.Enabled != 0,
            settings.ListenPort,
            material.Fingerprint);
    }

    public async Task<PeerLocalTlsMaterialDto> GetLocalTlsMaterialAsync(CancellationToken cancellationToken = default)
    {
        var settings = await PeerMigrations.EnsureInboundSettingsDefaultAsync(_dbFactory, _timeProvider, cancellationToken)
            .ConfigureAwait(false);
        var material = await EnsureCertificateMaterialAsync(settings.LocalDeviceId, cancellationToken)
            .ConfigureAwait(false);
        return new PeerLocalTlsMaterialDto
        {
            InboundEnabled = settings.Enabled != 0,
            LocalDeviceId = settings.LocalDeviceId,
            DisplayName = settings.DisplayName,
            ListenPort = settings.ListenPort,
            LocalFingerprint = material.Fingerprint,
            CertPem = material.CertPem,
            PrivateKeyPem = material.PrivateKeyPem
        };
    }

    public async Task<PeerInboundSettingsDto> GetInboundSettingsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await EnsureLocalIdentityAsync(cancellationToken).ConfigureAwait(false);
        return BuildInboundDto(snapshot);
    }

    public async Task<PeerInboundSettingsDto> UpdateInboundSettingsAsync(
        PeerUpdateInboundRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ListenPort is { } port && (port < 1024 || port > 65535))
        {
            throw new ArgumentException("listen_port must be between 1024 and 65535.", nameof(request));
        }

        if (request.DisplayName is not null && string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ArgumentException("display_name cannot be empty.", nameof(request));
        }

        await PeerMigrations.EnsureInboundSettingsDefaultAsync(_dbFactory, _timeProvider, cancellationToken)
            .ConfigureAwait(false);

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = await SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var row = await db.InboundSettings.SingleAsync(x => x.Id == 1, ct).ConfigureAwait(false);
            if (request.Enabled is { } enabled)
            {
                row.Enabled = enabled ? 1 : 0;
            }
            if (request.DisplayName is { } rawName)
            {
                var trimmed = rawName.Trim();
                row.DisplayName = trimmed.Length <= 128 ? trimmed : trimmed[..128];
            }
            if (request.ListenPort is { } nextPort)
            {
                row.ListenPort = nextPort;
            }
            row.UpdatedAt = nowMs;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return row;
        }, cancellationToken).ConfigureAwait(false);

        var material = await EnsureCertificateMaterialAsync(updated.LocalDeviceId, cancellationToken)
            .ConfigureAwait(false);
        var snapshot = new PeerLocalIdentitySnapshot(
            updated.LocalDeviceId,
            updated.DisplayName,
            updated.Enabled != 0,
            updated.ListenPort,
            material.Fingerprint);

        _policyService.PublishPolicyEvent(new PeerPolicyEventDto
        {
            Type = "inbound_updated",
            InboundEnabled = snapshot.InboundEnabled,
            ListenPort = snapshot.ListenPort,
            TimestampMs = nowMs
        });

        return BuildInboundDto(snapshot);
    }

    private static PeerInboundSettingsDto BuildInboundDto(PeerLocalIdentitySnapshot snapshot)
    {
        var addresses = DiscoverReachableAddresses(snapshot.ListenPort);
        var firewallHint = snapshot.InboundEnabled
            ? $"Ensure inbound TCP port {snapshot.ListenPort} is allowed on Private / Tailscale / WireGuard networks in your OS firewall."
            : null;
        return new PeerInboundSettingsDto
        {
            Enabled = snapshot.InboundEnabled,
            Listening = snapshot.InboundEnabled,
            DisplayName = snapshot.DisplayName,
            ListenPort = snapshot.ListenPort,
            LocalDeviceId = snapshot.LocalDeviceId,
            LocalFingerprint = snapshot.LocalFingerprint,
            ReachableAddresses = addresses,
            FirewallHint = firewallHint
        };
    }

    internal static IReadOnlyList<string> DiscoverReachableAddresses(int port)
    {
        var lanEndpoints = new List<string>();
        var virtualEndpoints = new List<string>();
        var otherEndpoints = new List<string>();
        var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                IPInterfaceProperties ipProps;
                try
                {
                    ipProps = nic.GetIPProperties();
                }
                catch
                {
                    continue;
                }

                var isVirtualOverlay = IsOverlayAdapter(nic.Name, nic.Description);
                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    var addr = unicast.Address;
                    if (addr.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(addr))
                        continue;

                    var bytes = addr.GetAddressBytes();
                    // Skip APIPA link-local 169.254.0.0/16 and 0.0.0.0
                    if (bytes[0] == 0 || (bytes[0] == 169 && bytes[1] == 254))
                        continue;

                    var ipText = addr.ToString();
                    if (!seenIps.Add(ipText))
                        continue;

                    var endpoint = $"{ipText}:{port}";
                    // CGNAT 100.64.0.0/10 is standard for Tailscale / WireGuard overlays
                    var isCgnat = bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
                    if (isVirtualOverlay || isCgnat)
                    {
                        virtualEndpoints.Add(endpoint);
                    }
                    else if (IsRfc1918Private(bytes))
                    {
                        lanEndpoints.Add(endpoint);
                    }
                    else
                    {
                        otherEndpoints.Add(endpoint);
                    }
                }
            }
        }
        catch
        {
            // Best-effort NIC enumeration; never fail inbound status reads due to OS adapter quirks.
        }

        lanEndpoints.Sort(StringComparer.Ordinal);
        virtualEndpoints.Sort(StringComparer.Ordinal);
        otherEndpoints.Sort(StringComparer.Ordinal);

        var combined = new List<string>(lanEndpoints.Count + virtualEndpoints.Count + otherEndpoints.Count);
        combined.AddRange(lanEndpoints);
        combined.AddRange(virtualEndpoints);
        combined.AddRange(otherEndpoints);
        return combined;
    }

    private static bool IsRfc1918Private(byte[] ipv4) =>
        ipv4[0] == 10
        || (ipv4[0] == 172 && ipv4[1] >= 16 && ipv4[1] <= 31)
        || (ipv4[0] == 192 && ipv4[1] == 168);

    private static bool IsOverlayAdapter(string? name, string? description)
    {
        ReadOnlySpan<string> markers = ["tailscale", "wireguard", "zerotier", "wintun", "utun", "wg"];
        foreach (var marker in markers)
        {
            if ((!string.IsNullOrEmpty(name) && name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(description) && description.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private async Task<(string CertPem, string PrivateKeyPem, string Fingerprint)> EnsureCertificateMaterialAsync(
        string localDeviceId,
        CancellationToken cancellationToken)
    {
        var existing = await TryReadValidMaterialAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Value;
        }

        await _identityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = await TryReadValidMaterialAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing.Value;
            }

            var generated = GenerateSelfSignedP256Identity(localDeviceId, _timeProvider.GetUtcNow());
            try
            {
                await _secretStore.PutAsync(PeerSecretKeys.LocalCertPem, generated.CertPem, cancellationToken).ConfigureAwait(false);
                await _secretStore.PutAsync(PeerSecretKeys.LocalPrivateKeyPem, generated.PrivateKeyPem, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException or IOException)
            {
                throw new PeerException(
                    503,
                    PeerErrorCodes.EncryptionUnavailable,
                    "Platform secret store is unavailable or read-only; peer TLS private key cannot be persisted safely.",
                    ex);
            }

            return generated;
        }
        finally
        {
            _identityGate.Release();
        }
    }

    private async Task<(string CertPem, string PrivateKeyPem, string Fingerprint)?> TryReadValidMaterialAsync(
        CancellationToken cancellationToken)
    {
        string? certPem;
        string? keyPem;
        try
        {
            certPem = await _secretStore.GetAsync(PeerSecretKeys.LocalCertPem, cancellationToken).ConfigureAwait(false);
            keyPem = await _secretStore.GetAsync(PeerSecretKeys.LocalPrivateKeyPem, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new PeerException(
                503,
                PeerErrorCodes.EncryptionUnavailable,
                "Failed to decrypt peer TLS identity material from platform secret store.",
                ex);
        }

        if (string.IsNullOrWhiteSpace(certPem) || string.IsNullOrWhiteSpace(keyPem))
        {
            return null;
        }

        try
        {
            using var cert = X509Certificate2.CreateFromPem(certPem);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(keyPem);
            var fingerprint = ComputeDerSha256Hex(cert.RawData);
            return (certPem, keyPem, fingerprint);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    internal static (string CertPem, string PrivateKeyPem, string Fingerprint) GenerateSelfSignedP256Identity(
        string localDeviceId,
        DateTimeOffset now)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var safeCn = string.IsNullOrWhiteSpace(localDeviceId) ? "local" : localDeviceId.Trim();
        var subject = new X500DistinguishedName($"CN=tinadec-peer-{safeCn}");
        var request = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
        var certPem = cert.ExportCertificatePem();
        var privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem();
        var fingerprint = ComputeDerSha256Hex(cert.RawData);
        return (certPem, privateKeyPem, fingerprint);
    }

    public static string ComputeDerSha256Hex(ReadOnlySpan<byte> derBytes) =>
        Convert.ToHexString(SHA256.HashData(derBytes)).ToLowerInvariant();

    public void Dispose() => _identityGate.Dispose();
}
