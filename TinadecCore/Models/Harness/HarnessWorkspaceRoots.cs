using System.Collections.Concurrent;
using TinadecCore.Persistence;

namespace TinadecCore.Models.Harness;

/// <summary>
/// The governed working directory one harness provider runs in — the same directory whichever channel
/// it is reached over.
/// <para>
/// This is the boundary that matters for a local agent: a harness reads and writes its own
/// <c>cwd</c> with its own tools no matter what capability bits were declared in a handshake, so
/// choosing the directory is choosing the blast radius. It lives here rather than on the ACP session
/// host because the one-shot headless channel needs exactly the same decision; two owners would mean
/// the same provider editing files in two different places depending on which channel a run picked.
/// </para>
/// </summary>
internal interface IHarnessWorkspaceRoots
{
    string ForProvider(Guid providerInstanceId);

    /// <summary>
    /// Governed working directory scoped to a specific Swarm teammate within a run. Falls back to
    /// <see cref="ForProvider"/> when neither <paramref name="runId"/> nor
    /// <paramref name="teammateHandle"/> is supplied so single-agent behavior stays identical.
    /// </summary>
    string ForTeammate(Guid providerInstanceId, string? runId, string? teammateHandle) =>
        ForProvider(providerInstanceId);
}

internal sealed class HarnessWorkspaceRoots(StoragePaths paths) : IHarnessWorkspaceRoots
{
    private readonly ConcurrentDictionary<Guid, string> _directories = new();
    private readonly ConcurrentDictionary<(Guid ProviderInstanceId, string RunScope, string TeammateScope), string> _teammateDirectories = new();

    public string ForProvider(Guid providerInstanceId) => _directories.GetOrAdd(providerInstanceId, id =>
    {
        // The second GUID is minted here rather than derived from the provider, so two providers that
        // ever share an identity token still cannot collide on a directory.
        var path = paths.HarnessWorkspace(id, Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    });

    public string ForTeammate(Guid providerInstanceId, string? runId, string? teammateHandle)
    {
        var runScope = NormalizeScope(runId);
        var teammateScope = NormalizeScope(teammateHandle);
        if (runScope.Length == 0 && teammateScope.Length == 0)
        {
            return ForProvider(providerInstanceId);
        }

        return _teammateDirectories.GetOrAdd((providerInstanceId, runScope, teammateScope), key =>
        {
            // Core-minted GUID workspace keeps teammate scratch directories strictly contained under
            // StoragePaths.Locations.Temp and immune to path traversal from run/handle strings.
            var path = paths.HarnessWorkspace(key.ProviderInstanceId, Guid.NewGuid());
            Directory.CreateDirectory(path);
            return path;
        });
    }

    private static string NormalizeScope(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
