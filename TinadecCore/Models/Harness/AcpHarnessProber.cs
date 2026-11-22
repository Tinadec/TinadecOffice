using TinadecCore.Abstractions.Ports;
using TinadecCore.Models.Harness.Acp;

namespace TinadecCore.Models.Harness;

/// <summary>
/// Proves a harness can actually speak ACP, the way an ACP session can be proved: spawn it, complete
/// the <c>initialize</c> handshake, open a session, and let it go.
/// <para>
/// This is the public face of the ACP layer for callers outside DmaEA. The session host, its request
/// record, and the frame types stay internal, because exposing them would hand every module a way to
/// open harness processes and choose their working directory — which is precisely the decision the
/// governed scratch root exists to keep in one place.
/// </para>
/// </summary>
public interface IAcpHarnessProber
{
    /// <summary>
    /// Throws when the harness cannot complete a handshake. <paramref name="providerInstanceId"/> is
    /// the identity the session's scratch directory is derived from; a provider that is not stored yet
    /// passes <c>null</c> and is probed under a throwaway identity.
    /// </summary>
    Task<AcpHarnessProbeResult> ProbeAsync(Guid? providerInstanceId, string harnessId, string binaryPath, string? homePath, CancellationToken cancellationToken = default);

    Task<AcpHarnessProbeResult> ProbeAsync(Guid? providerInstanceId, string harnessId, string binaryPath, string? homePath, string? launchArgs, CancellationToken cancellationToken = default) =>
        ProbeAsync(providerInstanceId, harnessId, binaryPath, homePath, cancellationToken);
}

public sealed record AcpHarnessProbeResult(bool LoadSession, bool ImageInput, bool SessionFork);

internal sealed class AcpHarnessProber(IAcpSessionHost sessions) : IAcpHarnessProber
{
    public Task<AcpHarnessProbeResult> ProbeAsync(Guid? providerInstanceId, string harnessId, string binaryPath, string? homePath, CancellationToken cancellationToken = default) =>
        ProbeAsync(providerInstanceId, harnessId, binaryPath, homePath, launchArgs: null, cancellationToken);

    public async Task<AcpHarnessProbeResult> ProbeAsync(Guid? providerInstanceId, string harnessId, string binaryPath, string? homePath, string? launchArgs, CancellationToken cancellationToken = default)
    {
        // Resolved before anything is spawned: custom-acp or explicit launch_args honors custom flags,
        // otherwise a known driver resolves strictly from the catalog.
        var argv = HarnessCatalog.ResolveAcpArgv(harnessId, launchArgs);
        var probeId = providerInstanceId ?? Guid.NewGuid();
        try
        {
            // Reaching a session is the whole test: `initialize` answered and `session/new` succeeded.
            var session = await sessions.AcquireAsync(new AcpSessionRequest(
                probeId,
                harnessId,
                binaryPath,
                argv,
                sessions.ScratchDirectoryFor(probeId),
                string.IsNullOrWhiteSpace(homePath) ? null : new Dictionary<string, string?> { ["HOME"] = homePath }), cancellationToken).ConfigureAwait(false);
            var capabilities = session.Capabilities;
            return new(capabilities.LoadSession, capabilities.ImageInput, capabilities.SessionFork);
        }
        finally
        {
            // A probe proves the harness starts; it does not promise to keep it running. Leaving a warm
            // process behind would outlive a provider the user goes on to edit or delete.
            await sessions.DropAsync(probeId, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
