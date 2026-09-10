using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IToolProvider _provider;
    private readonly IServiceProvider? _services;

    public ToolManifestSnapshotResolver(ISessionLocator sessions, IToolProvider provider, IServiceProvider? services = null)
    {
        _sessions = sessions;
        _provider = provider;
        _services = services;
    }

    public async Task<ToolManifestSnapshot> ResolveAsync(
        ToolManifestSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SessionId == Guid.Empty)
            throw new ToolManifestSnapshotException("TOOL_MANIFEST_SESSION_REQUIRED", "A session is required to resolve the tool manifest.");

        var session = await _sessions.FindAsync(request.SessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Session was not found.");

        if (session.ProjectId is null)
        {
            // Free-conversation sessions have no TinadecTools child process, so no
            // live manifest exists to freeze. The frozen manifest carries only the
            // Core-owned create_workspace virtual tool: a worker may propose binding
            // a real workspace (approval-gated, executed by Core itself), and the
            // next interaction freezes a fresh manifest from the new project root.
            //
            // This synthetic entry is the run's tool *ceiling*, not a per-agent
            // grant: unlike the project path it is not intersected with each agent's
            // declared tool_scope, because the projectless ceiling must stay
            // resolvable on installs whose published mode predates create_workspace.
            // The per-agent boundary is still enforced downstream — every invocation
            // passes ToolInvocationScopeResolver.IsToolAllowed(instance grant) and the
            // model only ever sees IFrozenToolManifestCatalog (instance grant ∩ this
            // frozen manifest), so an agent that does not declare the tool cannot
            // reach it.
            var virtualEntry = CoreWorkspaceTool.ManifestEntry();
            var virtualHash = ToolManifestHasher.Compute(new[] { virtualEntry });
            return new ToolManifestSnapshot(2, virtualHash, [ToFrozen(virtualEntry)]);
        }

        var project = await _sessions.FindProjectAsync(session.ProjectId.Value, cancellationToken).ConfigureAwait(false)
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
            manifest = await _provider.GetManifestAsync(root, cancellationToken).ConfigureAwait(false);
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

        // The formal agent ∩ mode effective-tool set is authoritative over the raw roster
        // union when the session has a mode_version: a specialist's tool that the manifest
        // does not declare (or the mode does not grant) must not enter the freeze and must
        // not fail admission for a sibling agent that never needed it.
        HashSet<string>? formalEffective = null;
        try
        {
            var formal = _services?.GetService(typeof(IFormalModeResolver)) as IFormalModeResolver;
            if (formal is not null)
                formalEffective = await formal.GetEffectiveToolsForSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        }
        catch { }

        var byId = manifest.Tools.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var available = manifest.Tools.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Effective set to validate/freeze. When a formal mode governs the session, the agent ∩ mode
        // grant is authoritative and is trimmed to the tools the actual manifest offers — a
        // specialist's tool the manifest does not declare (e.g. browser.fetch on a code-only
        // manifest) is dropped, never an admission failure for a sibling that did not need it.
        string[] validated;
        if (formalEffective is null)
        {
            // Legacy TOML path: a genuinely-requested but missing tool is a config error.
            validated = requested;
        }
        else if (formalEffective.Contains("*") || formalEffective.Count == 0)
        {
            // Wildcard/empty grant: the manifest's own offering is authoritative; drop anything absent.
            validated = requested.Where(available.Contains).ToArray();
        }
        else
        {
            validated = formalEffective.Where(available.Contains).ToArray();
        }

        var unknown = validated.FirstOrDefault(value => !byId.ContainsKey(value));
        if (unknown is not null)
        {
            throw new ToolManifestSnapshotException(
                "TOOL_MANIFEST_TOOL_NOT_FOUND",
                $"The runtime profile authorizes unknown tool '{unknown}'.");
        }

        var baseAuthorized = request.AllowAllTools
            ? manifest.Tools
            : manifest.Tools.Where(item => validated.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToList();

        IReadOnlyList<ToolManifestEntryDto> authorized;
        if (formalEffective is null) authorized = baseAuthorized;
        else if (formalEffective.Contains("*")) authorized = baseAuthorized;
        else if (formalEffective.Count == 0) authorized = [];
        else authorized = baseAuthorized.Where(item => formalEffective.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToList();

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
