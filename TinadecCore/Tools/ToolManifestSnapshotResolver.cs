using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Tools;

/// <summary>
/// Obtains a v2 manifest from the Core-owned project workspace and materializes
/// only the tools authorized by the admitted runtime profile.  The process
/// manifest hash is verified locally before any entry is exposed to a worker.
/// </summary>
public sealed class ToolManifestSnapshotResolver : IToolManifestSnapshotResolver
{
    private readonly ISessionLocator _sessions;
    private readonly IToolProcessManager _processes;

    public ToolManifestSnapshotResolver(ISessionLocator sessions, IToolProcessManager processes)
    {
        _sessions = sessions;
        _processes = processes;
    }

    public async Task<ToolManifestSnapshot> ResolveAsync(
        ToolManifestSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SessionId == Guid.Empty)
            throw new ToolManifestSnapshotException("TOOL_MANIFEST_SESSION_REQUIRED", "A session is required to resolve the tool manifest.");

        var session = await _sessions.FindAsync(request.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");
        var project = await _sessions.FindProjectAsync(session.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Project was not found.");
        if (project.TenantId != session.TenantId || project.WorkspaceId != session.WorkspaceId)
            throw new ToolManifestSnapshotException("TOOL_MANIFEST_SCOPE_MISMATCH", "The project does not belong to the session workspace.");

        string root;
        try
        {
            root = Path.GetFullPath(project.RootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolManifestSnapshotException("TOOL_MANIFEST_WORKSPACE_INVALID", "The project workspace root is invalid.");
        }
        if (!Directory.Exists(root))
            throw new ToolManifestSnapshotException("TOOL_MANIFEST_WORKSPACE_UNAVAILABLE", "The project workspace root no longer exists.");

        ToolManifestDto manifest;
        try
        {
            manifest = await _processes.GetManifestAsync(root, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolManifestSnapshotException("TOOL_MANIFEST_UNAVAILABLE", "The TinadecTools manifest handshake timed out.");
        }
        catch (ToolManifestSnapshotException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ToolManifestSnapshotException("TOOL_MANIFEST_UNAVAILABLE", "The TinadecTools manifest could not be resolved.");
        }

        ValidateManifest(manifest);
        var computedHash = ToolManifestHasher.Compute(manifest.Tools);
        if (!IsSha256(manifest.ManifestHash)
            || !string.Equals(manifest.ManifestHash, computedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolManifestSnapshotException("TOOL_MANIFEST_HASH_MISMATCH", "The TinadecTools manifest hash is invalid.");
        }

        var requested = (request.AllowedToolIds ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "*", StringComparison.Ordinal))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var byId = manifest.Tools.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var unknown = requested.FirstOrDefault(value => !byId.ContainsKey(value));
        if (unknown is not null)
        {
            throw new ToolManifestSnapshotException(
                "TOOL_MANIFEST_TOOL_NOT_FOUND",
                $"The runtime profile authorizes unknown tool '{unknown}'.");
        }

        var authorized = request.AllowAllTools
            ? manifest.Tools
            : manifest.Tools.Where(item => requested.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        var frozen = authorized.Select(ToFrozen).ToArray();
        return new ToolManifestSnapshot(manifest.ProtocolVersion, computedHash, frozen);
    }

    private static void ValidateManifest(ToolManifestDto manifest)
    {
        if (manifest.ProtocolVersion != 2)
        {
            throw new ToolManifestSnapshotException(
                "TOOL_MANIFEST_PROTOCOL_UNSUPPORTED",
                "TinadecTools manifest v2 is required for autonomous runs.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in manifest.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Id) || !seen.Add(tool.Id.Trim()))
                throw new ToolManifestSnapshotException("TOOL_MANIFEST_INVALID", "The TinadecTools manifest contains duplicate or empty tool ids.");
            if (tool.InputSchema.ValueKind is not JsonValueKind.Object)
                throw new ToolManifestSnapshotException("TOOL_MANIFEST_INVALID", $"Tool '{tool.Id}' has an invalid input schema.");
            if (string.IsNullOrWhiteSpace(tool.Risk) || string.IsNullOrWhiteSpace(tool.RetrySafety))
                throw new ToolManifestSnapshotException("TOOL_MANIFEST_INVALID", $"Tool '{tool.Id}' has incomplete execution policy metadata.");
        }
    }

    private static FrozenToolManifestEntry ToFrozen(ToolManifestEntryDto tool) => new(
        tool.Id.Trim(),
        tool.Description,
        tool.InputSchema.Clone(),
        tool.Risk,
        tool.MutatesWorkspace,
        tool.RequiresApproval,
        tool.RetrySafety,
        tool.ConfirmationFields.ToArray());

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length == 64
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
