using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Skills;

/// <summary>
/// Installs a market entry by writing one file, and exactly one file: the Tool Provider's server
/// config for an MCP server, or <c>skills/&lt;name&gt;/SKILL.md</c> for a skill.
///
/// The surface is built so that no call in it can touch a filesystem or start a process:
/// <list type="bullet">
///   <item>a preview computes the proposal and stores it under a digest and a deadline;</item>
///   <item>an apply hands those stored bytes to <see cref="IUserToolActionService"/>, the same
///     governed path every other user-initiated write takes — frozen manifest binding, pre-write
///     workspace snapshot, permission envelope, and a one-time human approval;</item>
///   <item>the write itself is <c>write_file</c> inside the project's own tool workspace, so the
///     tool layer's workspace boundary, its <c>file_hash</c> precondition, and the approval
///     evidence ("which file, how big, what digest") apply here without being re-implemented.</item>
/// </list>
///
/// The target path is not guessed. Core asks the provider where its config lives
/// (<c>mcp_list</c>'s <c>config_path</c>) and refuses if the answer is not inside the project
/// being installed into — writing somewhere plausible instead would produce a config entry no
/// server ever reads, which is the failure this surface most easily hides.
///
/// Failing safe is also why an unreadable config yields "no precondition" rather than an
/// overwrite: <c>write_file</c> creates only when the file is absent and refuses without a
/// matching hash when it exists. A file Core could not read therefore cannot be clobbered by a
/// proposal built on a guess about it.
/// </summary>
public sealed class MarketInstallService : IMarketInstallService
{
    /// <summary>The only tool this service can ever ask the governed path to run.</summary>
    internal const string WriteFileToolId = "write_file";

    private const string ReadFileToolId = "read_file";
    private const string McpListToolId = "mcp_list";
    private const int MaxServerIdChars = 64;
    private const string UnknownSource = "unknown";

    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly IDbContextFactory<IntegrationDbContext> _dbFactory;
    private readonly ITenantContextAccessor _tenantContext;
    private readonly IToolProvider _provider;
    private readonly ISessionLocator _sessions;
    private readonly IUserToolActionService _actions;

    public MarketInstallService(
        IDbContextFactory<IntegrationDbContext> dbFactory,
        ITenantContextAccessor tenantContext,
        IToolProvider provider,
        ISessionLocator sessions,
        IUserToolActionService actions)
    {
        _dbFactory = dbFactory;
        _tenantContext = tenantContext;
        _provider = provider;
        _sessions = sessions;
        _actions = actions;
    }

    public Task<MarketInstallProposalDto> PreviewInstallAsync(
        Guid catalogId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        if (catalogId == Guid.Empty)
            throw new MarketCatalogException(MarketErrorCodes.EntryNotFound, "catalog_id is required.");

        return PreviewAsync(projectId, MarketInstallActions.Install, catalogId, cancellationToken);
    }

    public Task<MarketInstallProposalDto> PreviewUninstallAsync(
        Guid installationId,
        CancellationToken cancellationToken = default)
    {
        if (installationId == Guid.Empty)
        {
            throw new MarketCatalogException(
                MarketErrorCodes.InstallationNotFound, "installation_id is required.");
        }

        return PreviewAsync(default, MarketInstallActions.Uninstall, installationId, cancellationToken);
    }

    /// <summary>
    /// <paramref name="subjectId"/> is a catalog id for an install and an installation id for a
    /// removal — the two actions are the same shape (freeze the bytes, then write them) and differ
    /// only in which row the truth starts from.
    /// </summary>
    private async Task<MarketInstallProposalDto> PreviewAsync(
        Guid projectId,
        string action,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var subject = action == MarketInstallActions.Install
            ? await ReadCatalogSubjectAsync(db, scope.TenantId, subjectId, cancellationToken).ConfigureAwait(false)
            : await ReadInstallationSubjectAsync(db, scope, subjectId, cancellationToken).ConfigureAwait(false);

        // A preview installs into the project the caller named; a removal goes to the project the
        // installation record already belongs to, which is the only one whose config holds it.
        var project = await ResolveProjectAsync(
            action == MarketInstallActions.Install ? projectId : subject.ProjectId,
            scope,
            cancellationToken).ConfigureAwait(false);
        // An entry that cannot become an action is refused before anything is read: "this row is a
        // remote endpoint, not a server we can start" and "this skill is already installed, and Core
        // has no delete" are facts about the entry, not about the project's config, and the two get
        // different codes because a client branches on them differently — one is "pick another
        // row", the other is "fix the workspace".
        AssertExpressible(action, subject);

        var plan = string.Equals(subject.Kind, MarketEntryKinds.Skill, StringComparison.OrdinalIgnoreCase)
            ? await PlanSkillInstallAsync(project, subject, cancellationToken).ConfigureAwait(false)
            : await PlanConfigWriteAsync(project, subject, action, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var record = new MarketInstallProposalRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            ProjectId = project.ProjectId,
            PrincipalId = scope.PrincipalId,
            Action = action,
            CatalogId = action == MarketInstallActions.Install ? subjectId : subject.CatalogId,
            SourceId = subject.SourceId,
            InstallationId = action == MarketInstallActions.Uninstall ? subjectId : null,
            ExtensionId = subject.ExtensionId,
            Version = subject.Version,
            Kind = subject.Kind,
            ServerId = subject.ServerId,
            Command = subject.Command,
            ArgsJson = JsonSerializer.Serialize(subject.Args),
            EnvironmentJson = JsonSerializer.Serialize(subject.Environment),
            ReplacesCommand = plan.ReplacesCommand,
            TargetPath = plan.TargetPath,
            Content = plan.Content,
            // Null when the file could not be read as well as when it is absent: either way the
            // write is conditioned on creating, never on overwriting.
            ExpectedFileHash = plan.ExpectedFileHash,
            ManifestHash = subject.ManifestHash,
            Status = "pending",
            CreatedAt = now,
            ExpiresAt = now.Add(MarketInstallPolicy.ProposalTtl),
        };
        record.Digest = ComputeDigest(record);

        db.InstallProposals.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToProposalDto(record, subject, plan);
    }

    /// <summary>
    /// The proposal for one server entry: the provider's own config path, and that path's content
    /// with the entry added or taken back out. Read through the tool layer, so the bytes Core
    /// freezes are the bytes the write will be conditioned on.
    /// </summary>
    private async Task<Plan> PlanConfigWriteAsync(
        ProjectReference project,
        ProposalSubject subject,
        string action,
        CancellationToken cancellationToken)
    {
        var configPath = await ConfigPathAsync(project.RootPath, cancellationToken).ConfigureAwait(false);
        if (!IsInsideWorkspace(configPath, project.RootPath))
        {
            throw new MarketCatalogException(
                MarketErrorCodes.TargetUnresolved,
                $"The tool provider reads its server config from '{configPath}', which is not inside "
                + $"the project root '{project.RootPath}'. Core will not write a file nothing reads.");
        }

        var (current, currentHash, unreadable) = await CurrentConfigAsync(
            project.RootPath, configPath, cancellationToken).ConfigureAwait(false);

        var merge = action == MarketInstallActions.Install
            ? MergeInstall(current, subject)
            : MergeUninstall(current, subject.ServerId);

        if (merge.Content is null)
        {
            throw new MarketCatalogException(
                action == MarketInstallActions.Install
                    ? MarketErrorCodes.TargetUnresolved
                    : MarketErrorCodes.NotExpressible,
                merge.Error ?? "Nothing was proposed.");
        }

        if (merge.Content.Length > MarketInstallPolicy.MaxConfigChars)
        {
            throw new MarketCatalogException(
                MarketErrorCodes.TargetUnresolved,
                $"The resulting server config is larger than the {MarketInstallPolicy.MaxConfigChars} "
                + "characters Core will freeze into a proposal.");
        }

        return new Plan(configPath, merge.Content, unreadable ? null : currentHash, merge.ReplacesCommand, null);
    }

    /// <summary>
    /// The proposal for one skill: the document itself, fetched here and only here, written to the
    /// path <see cref="WorkspaceSkillPolicy"/> reads skills from.
    ///
    /// Three things follow from that being a file rather than a config entry. The fetch happens at
    /// preview and never again, so the bytes a person approves are the bytes that land — a skill has
    /// no version string to be betrayed by. The target is composed by Core, not reported by the
    /// provider, because the workspace's own layout decides where a skill is. And an existing file
    /// is conditioned on its current hash rather than skipped: replacing a hand-written skill is a
    /// decision a person is entitled to make with the old bytes and the new ones on one screen.
    /// </summary>
    private async Task<Plan> PlanSkillInstallAsync(
        ProjectReference project,
        ProposalSubject subject,
        CancellationToken cancellationToken)
    {
        var (body, error) = await SkillRepositorySource.ReadDocumentAsync(
            _provider, project.RootPath, subject.SourceLocation, subject.ExtensionId, cancellationToken)
            .ConfigureAwait(false);

        if (body is null)
        {
            throw new MarketCatalogException(
                MarketErrorCodes.NotExpressible,
                error ?? "The skill document could not be read, so there is nothing to review.");
        }

        // The document is checked against the rule the workspace will apply to it later. A skill
        // whose frontmatter names a different directory, or carries its own off switch, is refused
        // here rather than written into the project to be silently dropped by the loader — an
        // install that "succeeds" and changes nothing is the failure this surface can least afford.
        var relative = WorkspaceSkillPolicy.RelativePathFor(subject.ExtensionId);
        if (!WorkspaceSkillPolicy.TryRead(body, subject.ExtensionId, relative, out _, out var reason))
        {
            throw new MarketCatalogException(
                MarketErrorCodes.NotExpressible,
                $"The document at {relative} is not an installable skill: {reason}.");
        }

        var target = WorkspaceSkillPolicy.AbsolutePathFor(project.RootPath, subject.ExtensionId);
        if (!IsInsideWorkspace(target, project.RootPath))
        {
            // Unreachable for a name that passed the format rule, and kept because this is the
            // last gate before a path reaches a write.
            throw new MarketCatalogException(
                MarketErrorCodes.TargetUnresolved,
                $"'{target}' is not inside the project root '{project.RootPath}'.");
        }

        var (existing, existingHash, unreadable) = await CurrentConfigAsync(
            project.RootPath, target, cancellationToken).ConfigureAwait(false);

        var warning = existing is null
            ? null
            : $"'{relative}' already exists in this project"
              + (unreadable ? " and Core could not read it" : $" ({existing.Length} characters)")
              + ". Approving this replaces it; the bytes being overwritten are recoverable from the "
              + "write's own snapshot.";

        return new Plan(target, body, existingHash, null, warning);
    }

    /// <summary>
    /// The two kinds this build can put on disk, and the one thing each needs in order to be a
    /// proposal at all. Nothing here decides where a file goes — that is the plan's job — this only
    /// refuses the entries that have no shape Core knows how to write.
    /// </summary>
    private static void AssertExpressible(string action, ProposalSubject subject)
    {
        var isSkill = string.Equals(subject.Kind, MarketEntryKinds.Skill, StringComparison.OrdinalIgnoreCase);

        if (action == MarketInstallActions.Install)
        {
            if (isSkill)
                return;

            if (string.IsNullOrWhiteSpace(subject.Command) || subject.Args.Count == 0)
            {
                throw new MarketCatalogException(
                    MarketErrorCodes.NotExpressible,
                    subject.Blocker ?? "This catalog entry carries no pinned command.");
            }

            return;
        }

        if (!isSkill)
            return;

        // A skill is one file, and taking it back means removing that file. The tool layer writes,
        // reads, and lists — it has no delete and no rename — so a "removal" proposal here would
        // have to be some other action wearing its name. The switch that does exist is inside the
        // document, and saying so is the useful answer.
        throw new MarketCatalogException(
            MarketErrorCodes.NotExpressible,
            $"This build cannot remove a skill: '{subject.ExtensionId}' is the file "
            + $"{WorkspaceSkillPolicy.RelativePathFor(subject.ExtensionId)}, and the tool layer offers "
            + "no delete. Turn it off by adding "
            + $"'{WorkspaceSkillPolicy.DisabledKey}: true' to its own frontmatter, or remove the file "
            + "yourself; either way it stops being advertised to the model without being uninstalled.");
    }

    public async Task<MarketInstallationDto> ApplyAsync(
        Guid proposalId,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var proposal = await db.InstallProposals.FirstOrDefaultAsync(
            x => x.Id == proposalId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new MarketCatalogException(
                MarketErrorCodes.ProposalNotFound, "No install proposal has that id.");

        if (!string.Equals(ComputeDigest(proposal), proposal.Digest, StringComparison.Ordinal))
        {
            // Checked before the replay branch below, which answers from the same stored bytes: a
            // row whose content no longer hashes to the digest the reviewer was shown is not the
            // proposal anyone read, and must not be queued — or replayed — as if it were.
            proposal.Status = "stale";
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw new MarketCatalogException(
                MarketErrorCodes.ProposalStale,
                "The stored proposal no longer matches its own digest, so what would be written is "
                + "not what was reviewed.");
        }

        if (proposal.Status == "consumed" && proposal.UserToolActionId is { } queued)
        {
            // A retried apply answers with the action it already queued rather than a fresh
            // refusal. The alternative — "this proposal is spent" to a client whose first response
            // was lost — leaves a person looking at an error while an approval sits waiting for
            // them, which is how a duplicate gets minted by hand.
            var existing = await db.Installations.AsNoTracking().FirstOrDefaultAsync(
                x => x.TenantId == scope.TenantId
                    && x.ProjectId == proposal.ProjectId
                    && x.ServerId == proposal.ServerId,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var alreadyName = await SourceNameAsync(db, scope.TenantId, existing.SourceId, cancellationToken)
                    .ConfigureAwait(false);
                return ToInstallationDto(existing, alreadyName,
                    (await _actions.GetAsync(queued, cancellationToken).ConfigureAwait(false))?.Status);
            }
        }

        if (proposal.Status != "pending")
            throw new MarketCatalogException(
                MarketErrorCodes.ProposalStale, $"This proposal was already {proposal.Status}.");

        if (proposal.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            // The expiry is written down rather than only reported: a row that outlived its
            // deadline must not become applyable again for a client that retries after the fact.
            proposal.Status = "expired";
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw new MarketCatalogException(
                MarketErrorCodes.ProposalStale,
                $"This proposal expired at {proposal.ExpiresAt:O}.");
        }

        await AssertStillTrueAsync(db, scope.TenantId, proposal, cancellationToken).ConfigureAwait(false);

        var parameters = new Dictionary<string, object?>
        {
            ["filepath"] = proposal.TargetPath,
            ["content"] = proposal.Content,
        };
        if (proposal.ExpectedFileHash is { } hash)
        {
            // Present or absent, never empty: the tool distinguishes "create" from "overwrite
            // exactly these bytes" by whether the field is there at all.
            parameters["file_hash"] = hash;
        }

        UserToolActionResult action;
        try
        {
            action = await _actions.CreateAsync(new UserToolActionRequest(
                proposal.ProjectId,
                WriteFileToolId,
                JsonSerializer.Serialize(parameters),
                // One proposal, one action: an apply button clicked twice queues the same governed
                // write rather than a second approval for identical bytes.
                IdempotencyKey: $"market-install:{proposal.Id:N}"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            // Each of these means the write could not be prepared: an unknown project, a provider
            // whose manifest no longer advertises write_file, a snapshot that could not be taken.
            // Named as a target problem instead of surfacing as a 500.
            throw new MarketCatalogException(
                MarketErrorCodes.TargetUnresolved,
                $"Core could not queue the governed write for this proposal: {ex.Message}");
        }

        proposal.Status = "consumed";
        proposal.UserToolActionId = action.Id;
        proposal.ConsumedAt = DateTimeOffset.UtcNow;

        var installation = await RecordApplyAsync(db, scope, proposal, action.Id, cancellationToken)
            .ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var sourceName = await SourceNameAsync(db, scope.TenantId, installation.SourceId, cancellationToken)
            .ConfigureAwait(false);

        return ToInstallationDto(installation, sourceName, action.Status);
    }

    public async Task<IReadOnlyList<MarketInstallationDto>> ListInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // SQLite stores DateTimeOffset as text and cannot translate ordering over that CLR type,
        // so the tenant-scoped rows are fetched unordered and sorted here.
        var rows = (await db.Installations.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.CreatedAt)
            .ToList();

        if (rows.Count == 0)
            return [];

        var names = (await db.Sources.AsNoTracking()
                .Where(x => x.TenantId == scope.TenantId)
                .Select(x => new { x.Id, x.Name })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(x => x.Id, x => x.Name);

        var result = new List<MarketInstallationDto>(rows.Count);
        foreach (var row in rows)
        {
            var status = await CurrentActionStatusAsync(row, cancellationToken).ConfigureAwait(false);

            // A removal that finished is not an installation. Dropping it here is what lets the
            // ledger hold one row per (project, server) instead of a tombstone per cycle.
            if (row.State == MarketInstallationStates.Removing && status == "completed")
                continue;

            result.Add(ToInstallationDto(row, names.GetValueOrDefault(row.SourceId, UnknownSource), status));
        }

        return result;
    }

    /// <summary>
    /// Re-checks what the preview could not have known. The catalog row is the load-bearing one:
    /// its stored hash is what an approval was about, so a refresh that republished this entry
    /// under a different body invalidates the proposal rather than quietly installing the new one.
    /// </summary>
    private static async Task AssertStillTrueAsync(
        IntegrationDbContext db,
        Guid tenant,
        MarketInstallProposalRecord proposal,
        CancellationToken cancellationToken)
    {
        if (proposal.Action != MarketInstallActions.Install || proposal.CatalogId is not { } catalogId)
            return;

        var hash = await db.CatalogEntries.AsNoTracking()
            .Where(x => x.TenantId == tenant && x.Id == catalogId)
            .Select(x => x.ManifestHash)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (hash is not null && string.Equals(hash, proposal.ManifestHash, StringComparison.Ordinal))
            return;

        proposal.Status = "stale";
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        throw new MarketCatalogException(
            MarketErrorCodes.ProposalStale,
            hash is null
                ? "The catalog entry this proposal was built from is no longer listed."
                : "The source has since published a different version of this entry, so what was reviewed "
                  + "is no longer what would be written.");
    }

    /// <summary>
    /// Creates or refreshes the ledger row for one consumed proposal. An upgrade over the same
    /// server id repoints the row at the newer action: an approval minted from an older proposal
    /// can still be granted, and the one that lands second then fails the tool's hash check rather
    /// than silently winning.
    /// </summary>
    private static async Task<MarketInstallationRecord> RecordApplyAsync(
        IntegrationDbContext db,
        TenantContext scope,
        MarketInstallProposalRecord proposal,
        Guid actionId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var installation = await db.Installations.FirstOrDefaultAsync(
            x => x.TenantId == scope.TenantId
                && x.ProjectId == proposal.ProjectId
                && x.ServerId == proposal.ServerId,
            cancellationToken).ConfigureAwait(false);

        if (installation is null)
        {
            installation = new MarketInstallationRecord
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                ProjectId = proposal.ProjectId,
                CatalogId = proposal.CatalogId ?? Guid.Empty,
                SourceId = proposal.SourceId ?? Guid.Empty,
                ExtensionId = proposal.ExtensionId,
                Version = proposal.Version,
                Kind = proposal.Kind,
                ServerId = proposal.ServerId,
                ConfigPath = proposal.TargetPath,
                ManifestHash = proposal.ManifestHash ?? string.Empty,
                InstallActionId = actionId,
                State = MarketInstallationStates.Installing,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Installations.Add(installation);
            return installation;
        }

        if (proposal.Action == MarketInstallActions.Uninstall)
        {
            installation.UninstallActionId = actionId;
            installation.State = MarketInstallationStates.Removing;
        }
        else
        {
            installation.CatalogId = proposal.CatalogId ?? installation.CatalogId;
            installation.SourceId = proposal.SourceId ?? installation.SourceId;
            installation.Version = proposal.Version;
            installation.Kind = proposal.Kind;
            installation.ConfigPath = proposal.TargetPath;
            installation.ManifestHash = proposal.ManifestHash ?? installation.ManifestHash;
            installation.InstallActionId = actionId;
            installation.UninstallActionId = null;
            installation.State = MarketInstallationStates.Installing;
        }

        installation.UpdatedAt = now;
        return installation;
    }

    private async Task<string?> CurrentActionStatusAsync(
        MarketInstallationRecord row,
        CancellationToken cancellationToken)
    {
        var actionId = row.State == MarketInstallationStates.Removing ? row.UninstallActionId : row.InstallActionId;
        if (actionId is not { } id)
            return null;

        return (await _actions.GetAsync(id, cancellationToken).ConfigureAwait(false))?.Status;
    }

    /// <summary>Where the provider says its server config lives. Nothing here resolves a path.</summary>
    private async Task<string> ConfigPathAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        // The inventory is only a means of learning the path; the tool list of every configured
        // server would be a far larger answer to nobody's question.
        var listed = await CallAsync(
            workspaceRoot, McpListToolId, new Dictionary<string, object?> { ["include_schema"] = false },
            cancellationToken).ConfigureAwait(false);

        if (listed is null)
        {
            throw new MarketCatalogException(
                MarketErrorCodes.TargetUnresolved,
                "The tool provider could not be read, so Core cannot say which config file an install "
                + "would change. Nothing was proposed.");
        }

        var path = Text(listed.Value, "config_path");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new MarketCatalogException(
                MarketErrorCodes.TargetUnresolved,
                "The tool provider reported no server config path, so there is no target to propose.");
        }

        return path.Trim();
    }

    private async Task<(string? Content, string? Hash, bool Unreadable)> CurrentConfigAsync(
        string workspaceRoot,
        string configPath,
        CancellationToken cancellationToken)
    {
        var read = await CallAsync(
            workspaceRoot, ReadFileToolId, new Dictionary<string, object?> { ["filepath"] = configPath },
            cancellationToken).ConfigureAwait(false);

        if (read is not { } payload
            || !payload.TryGetProperty("success", out var flag)
            || flag.ValueKind != JsonValueKind.True)
        {
            // Either the file is not there or the provider could not read it, and the proposal
            // does not need to tell those apart: both mean "create, do not overwrite".
            return (null, null, true);
        }

        return (JoinLines(payload), Text(payload, "file_hash"), false);
    }

    private async Task<JsonElement?> CallAsync(
        string workspaceRoot,
        string toolId,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _provider.CallAsync(
                workspaceRoot,
                new ToolWireRequestDto
                {
                    ToolId = toolId,
                    // No run owns a market install read; the provider only echoes this on wire events.
                    SessionId = "market-install",
                    Approved = false,
                    Params = JsonSerializer.SerializeToElement(parameters),
                },
                ProviderTimeout,
                cancellationToken).ConfigureAwait(false);

            return response.IsSuccess && response.Result is { ValueKind: JsonValueKind.Object } result
                ? result
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException
            or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds or replaces this server's entry, leaving every other member of the document exactly as
    /// it was found. Unknown keys survive because this is a node edit rather than a typed
    /// round-trip: the config's schema belongs to the tool layer, and Core only writes the four
    /// fields an install is authorized to state.
    /// </summary>
    private static (string? Content, string? Error, string? ReplacesCommand) MergeInstall(
        string? current,
        ProposalSubject subject)
    {
        var (root, error) = ReadConfig(current);
        if (root is null)
            return (null, error, null);

        if (string.IsNullOrWhiteSpace(subject.Command) || subject.Args.Count == 0)
        {
            return (null, "This entry carries no command, so there is nothing to write.", null);
        }

        var servers = (JsonArray)root["servers"]!;
        var entry = new JsonObject
        {
            ["id"] = subject.ServerId,
            ["name"] = subject.DisplayName,
            ["command"] = subject.Command,
            ["args"] = new JsonArray(subject.Args.Select(arg => (JsonNode?)JsonValue.Create(arg)).ToArray()),
        };

        for (var index = 0; index < servers.Count; index++)
        {
            if (servers[index] is not JsonObject candidate || !SameServer(candidate, subject.ServerId))
                continue;

            // The command being overwritten is reported rather than hidden: "adds the pinned
            // server" and "changes what is already there" are different things to approve.
            var replaced = CommandOf(candidate);
            servers[index] = entry;
            return (root.ToJsonString(Pretty), null, replaced);
        }

        servers.Add(entry);
        return (root.ToJsonString(Pretty), null, null);
    }

    private static (string? Content, string? Error, string? ReplacesCommand) MergeUninstall(
        string? current,
        string serverId)
    {
        var (root, error) = ReadConfig(current);
        if (root is null)
            return (null, error, null);

        var servers = (JsonArray)root["servers"]!;
        for (var index = 0; index < servers.Count; index++)
        {
            if (servers[index] is JsonObject candidate && SameServer(candidate, serverId))
            {
                servers.RemoveAt(index);
                return (root.ToJsonString(Pretty), null, null);
            }
        }

        return (null, $"'{serverId}' is not in the config any more, so there is nothing to remove.", null);
    }

    private static (JsonObject? Root, string? Error) ReadConfig(string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
            return (new JsonObject { ["servers"] = new JsonArray() }, null);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(current);
        }
        catch (JsonException ex)
        {
            // Refusing beats rewriting: a file Core cannot parse is a file Core would flatten to
            // just its own entry, and the operator's other servers would be gone by the write.
            return (null, $"The current config is not JSON Core can edit: {ex.Message}");
        }

        if (node is not JsonObject root)
            return (null, "The current config is not a JSON object.");

        if (!root.TryGetPropertyValue("servers", out var servers) || servers is null)
        {
            root["servers"] = new JsonArray();
            return (root, null);
        }

        return servers is not JsonArray list
            ? (null, "The current config's servers field is not a list.")
            : (root, null);
    }

    /// <summary>The entry's command line as it stands, for the proposal that would replace it.</summary>
    private static string? CommandOf(JsonObject entry)
    {
        if (entry["command"]?.GetValueKind() != JsonValueKind.String)
            return null;

        var command = entry["command"]!.GetValue<string>();
        if (entry["args"]?.GetValueKind() != JsonValueKind.Array)
            return command;

        var args = string.Join(' ', ((JsonArray)entry["args"]!)
            .Where(node => node is not null)
            .Select(node => node!.ToJsonString().Trim('"')));

        return args.Length == 0 ? command : command + " " + args;
    }

    private static bool SameServer(JsonObject entry, string serverId) =>
        entry["id"]?.GetValueKind() == JsonValueKind.String
        && string.Equals(entry["id"]!.GetValue<string>(), serverId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The line texts joined back with newlines. <c>read_file</c> returns lines rather than bytes,
    /// and that is good enough here because the only consumer is a JSON parser: a lost trailing
    /// newline or a CRLF folded to LF changes no JSON value. The bytes that get written are Core's
    /// own rendering, not a copy of what was read.
    /// </summary>
    private static string? JoinLines(JsonElement response)
    {
        if (!response.TryGetProperty("all_contents", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return null;

        var text = new StringBuilder();
        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind is JsonValueKind.Object or JsonValueKind.String)
                text.Append(LineText(line)).Append('\n');
        }

        return text.Length == 0 ? null : text.ToString();
    }

    /// <summary>
    /// <c>LineContent</c> is a positional record carrying no JSON naming, so its member casing on
    /// the wire is the runtime's business rather than a contract. Rather than pin one spelling,
    /// take the one string member the object has — a line of text is the only string in there.
    /// </summary>
    private static string LineText(JsonElement line)
    {
        var content = line.TryGetProperty("content", out var inner) ? inner : line;
        if (content.ValueKind != JsonValueKind.Object)
            return content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : string.Empty;

        foreach (var property in content.EnumerateObject())
        {
            if (string.Equals(property.Name, "content", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString() ?? string.Empty;
            }
        }

        foreach (var property in content.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// True only for a path the tool layer will accept for this project: the provider resolves
    /// relative paths against the workspace root and rejects anything outside it, so a config
    /// elsewhere is a target Core cannot write and must not pretend to.
    /// </summary>
    private static bool IsInsideWorkspace(string path, string root)
    {
        try
        {
            var relative = Path.GetRelativePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                Path.GetFullPath(path));

            return relative.Length > 0
                && !Path.IsPathRooted(relative)
                && !relative.StartsWith("..", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// A config id from a source's identifier, lowercased and reduced to name-shaped characters.
    /// Two sources collapsing onto one id is not silent: it surfaces as the entry the proposal says
    /// it replaces.
    /// </summary>
    internal static string ServerIdFor(string extensionId)
    {
        var builder = new StringBuilder(extensionId.Length);
        var pendingSeparator = false;
        foreach (var character in extensionId)
        {
            if (!char.IsLetterOrDigit(character))
            {
                pendingSeparator = builder.Length > 0;
                continue;
            }

            if (builder.Length > 0 && pendingSeparator)
                builder.Append('-');

            builder.Append(char.ToLowerInvariant(character));
            pendingSeparator = false;

            if (builder.Length >= MaxServerIdChars)
                break;
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "market-server" : slug;
    }

    /// <summary>
    /// Binds the reviewable facts to the bytes. The digest is over what a reviewer is shown, so a
    /// stored proposal whose content no longer matches its own digest cannot be the one that was
    /// approved — which is the only guarantee Core can offer before the tool layer's own hash check.
    /// </summary>
    internal static string ComputeDigest(MarketInstallProposalRecord proposal)
    {
        var canonical = string.Join('\n',
            proposal.Action,
            proposal.ProjectId.ToString("N"),
            proposal.ServerId,
            proposal.Version,
            proposal.Command ?? string.Empty,
            proposal.ArgsJson,
            proposal.TargetPath,
            proposal.ExpectedFileHash ?? string.Empty,
            proposal.ManifestHash ?? string.Empty,
            proposal.Content);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private async Task<ProjectReference> ResolveProjectAsync(
        Guid projectId,
        TenantContext scope,
        CancellationToken cancellationToken)
    {
        if (projectId == Guid.Empty)
            throw new MarketCatalogException(
                MarketErrorCodes.ProjectNotFound,
                "A market install is always for one project, and its tool workspace.");

        var project = await _sessions.FindProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null || project.TenantId != scope.TenantId || project.WorkspaceId != scope.WorkspaceId)
            throw new MarketCatalogException(
                MarketErrorCodes.ProjectNotFound,
                "No project in this workspace has that id, so there is no tool workspace to install into.");

        return project;
    }

    private static async Task<ProposalSubject> ReadInstallationSubjectAsync(
        IntegrationDbContext db,
        TenantContext scope,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        var installation = await db.Installations.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == installationId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new MarketCatalogException(
                MarketErrorCodes.InstallationNotFound, "No market installation has that id.");

        if (installation.State == MarketInstallationStates.Removing)
        {
            throw new MarketCatalogException(
                MarketErrorCodes.NotExpressible,
                "A removal is already pending for this installation; approve or refuse it first.");
        }

        var sourceName = await db.Sources.AsNoTracking()
            .Where(x => x.Id == installation.SourceId && x.TenantId == scope.TenantId)
            .Select(x => x.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return new ProposalSubject(
            installation.ProjectId,
            installation.SourceId,
            installation.CatalogId,
            sourceName ?? UnknownSource,
            installation.ExtensionId,
            installation.Kind,
            installation.Version,
            installation.ServerId,
            installation.ExtensionId,
            installation.ManifestHash,
            null,
            [],
            [],
            "This installation is not in the catalog any more.");
    }

    private static async Task<ProposalSubject> ReadCatalogSubjectAsync(
        IntegrationDbContext db,
        Guid tenant,
        Guid catalogId,
        CancellationToken cancellationToken)
    {
        var row = await db.CatalogEntries.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == catalogId && x.TenantId == tenant, cancellationToken).ConfigureAwait(false)
            ?? throw new MarketCatalogException(
                MarketErrorCodes.EntryNotFound, "No catalog entry has that id.");

        var source = await db.Sources.AsNoTracking().FirstOrDefaultAsync(
            x => x.Id == row.SourceId && x.TenantId == tenant, cancellationToken).ConfigureAwait(false)
            ?? throw new MarketCatalogException(
                MarketErrorCodes.SourceNotFound, "The source that listed this entry no longer exists.");

        // Two kinds have a meaning here. A row of any other kind is refused by name: an ACP adapter
        // or a tool pack would need a different file, a different target, and a different review, and
        // guessing at one from the shape of another is how a config entry nobody reads gets written.
        var isSkill = string.Equals(row.Kind, MarketEntryKinds.Skill, StringComparison.OrdinalIgnoreCase);
        if (!isSkill && !string.Equals(row.Kind, MarketEntryKinds.McpServer, StringComparison.OrdinalIgnoreCase))
        {
            throw new MarketCatalogException(
                MarketErrorCodes.NotExpressible,
                $"This build installs '{MarketEntryKinds.McpServer}' and '{MarketEntryKinds.Skill}' entries; "
                + $"this entry is '{row.Kind}'.");
        }

        string? command = null;
        IReadOnlyList<string> args = [];
        IReadOnlyList<MarketEnvironmentRequestDto> environment = [];
        string? blocker = null;
        if (!isSkill)
            (command, args, environment, blocker) = ReadInstall(row);

        return new ProposalSubject(
            default,
            row.SourceId,
            row.Id,
            source.Name,
            row.ExtensionId,
            row.Kind,
            row.Version,
            ServerIdFor(row.ExtensionId),
            row.DisplayName,
            row.ManifestHash,
            command,
            args,
            environment,
            blocker ?? (source.Enabled
                ? null
                : $"'{source.Name}' is disabled, so a refresh could not confirm this entry is still published."),
            source.Location);
    }

    /// <summary>
    /// Reads back what the adapter stored, defensively. A row whose detail does not parse or whose
    /// pin went missing is not installable and says so — it is never an excuse to rebuild a command
    /// from whatever fields remain. Returns the command, its pinned arguments, the variable names it
    /// asks for, or why not.
    /// </summary>
    private static (string? Command, IReadOnlyList<string> Args,
        IReadOnlyList<MarketEnvironmentRequestDto> Environment, string? Blocker) ReadInstall(
        ExtensionCatalogRecord row)
    {
        if (string.IsNullOrWhiteSpace(row.DetailJson))
            return (null, [], [], "This entry stores no install description.");

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(row.DetailJson!).RootElement;
        }
        catch (JsonException)
        {
            return (null, [], [], "This entry's stored detail could not be read back.");
        }

        var blocker = root.TryGetProperty("install_blocker", out var blocked)
            && blocked.ValueKind == JsonValueKind.String
            ? blocked.GetString()
            : null;

        if (!root.TryGetProperty("install", out var install) || install.ValueKind != JsonValueKind.Object)
            return (null, [], [], blocker ?? "This entry cannot be installed by this build.");

        var command = install.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
        var version = install.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(version)
            || !install.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array)
        {
            return (null, [], [], "This entry's stored install description is no longer usable.");
        }

        var list = args.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.String)
            .Select(a => a.GetString()!)
            .ToList();

        // Whitespace cannot appear in a stored argument because the adapter refused it, and the
        // check stays anyway: this is the last point where a hand-edited or corrupted row can be
        // stopped before its text reaches a command line.
        if (list.Count == 0 || list.Any(a => a.Length == 0 || a.Any(ch => char.IsWhiteSpace(ch))))
            return (null, [], [], "This entry's stored install description is no longer usable.");

        var environment = new List<MarketEnvironmentRequestDto>();
        if (install.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in env.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(name.GetString()))
                {
                    continue;
                }

                environment.Add(new MarketEnvironmentRequestDto
                {
                    Name = name.GetString()!,
                    Required = item.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True,
                    Secret = item.TryGetProperty("secret", out var s) && s.ValueKind == JsonValueKind.True,
                    Description = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()
                        : null,
                });
            }
        }

        return (command, list, environment, null);
    }

    private static async Task<string> SourceNameAsync(
        IntegrationDbContext db,
        Guid tenant,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        var name = await db.Sources.AsNoTracking()
            .Where(x => x.Id == sourceId && x.TenantId == tenant)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return name ?? UnknownSource;
    }

    private static MarketInstallProposalDto ToProposalDto(
        MarketInstallProposalRecord proposal,
        ProposalSubject subject,
        Plan plan)
    {
        var isSkill = string.Equals(proposal.Kind, MarketEntryKinds.Skill, StringComparison.OrdinalIgnoreCase);

        // One note per proposal, chosen by what the pin actually is. Printing the package warning on
        // a skill would tell a reviewer their approved bytes might change, when the bytes they are
        // looking at are exactly the bytes that will be written.
        var warnings = new List<string>
        {
            isSkill ? MarketInstallPolicy.ContentPinNote : MarketInstallPolicy.VersionPinNote,
        };

        if (subject.Environment.Count > 0)
        {
            warnings.Add("This server asks for environment variables ("
                + string.Join(", ", subject.Environment.Select(x => x.Name))
                + "). No values are written by this proposal — a server that needs them may fail to "
                + "start until they are configured.");
        }

        if (plan.Warning is { } replacing)
            warnings.Add(replacing);

        if (proposal.ExpectedFileHash is null)
        {
            warnings.Add("Core found no readable file at the target, so this is a create: if something "
                + "is there after all, the write will be refused rather than overwrite it.");
        }

        return new MarketInstallProposalDto
        {
            Id = proposal.Id.ToString("N"),
            Action = proposal.Action,
            ProjectId = proposal.ProjectId.ToString("N"),
            CatalogId = proposal.CatalogId?.ToString("N"),
            InstallationId = proposal.InstallationId?.ToString("N"),
            SourceName = subject.SourceName,
            ExtensionId = proposal.ExtensionId,
            Kind = proposal.Kind,
            Version = proposal.Version,
            ServerId = proposal.ServerId,
            ReplacesCommand = proposal.ReplacesCommand,
            Command = proposal.Command,
            Args = subject.Args,
            Environment = subject.Environment,
            TargetPath = proposal.TargetPath,
            Content = proposal.Content,
            ExpectedFileHash = proposal.ExpectedFileHash,
            Digest = proposal.Digest,
            ExpiresAt = proposal.ExpiresAt,
            Warnings = warnings,
        };
    }

    private static MarketInstallationDto ToInstallationDto(
        MarketInstallationRecord row,
        string sourceName,
        string? actionStatus) => new()
        {
            Id = row.Id.ToString("N"),
            ProjectId = row.ProjectId.ToString("N"),
            CatalogId = row.CatalogId.ToString("N"),
            SourceName = sourceName,
            ExtensionId = row.ExtensionId,
            Kind = row.Kind,
            Version = row.Version,
            ServerId = row.ServerId,
            ConfigPath = row.ConfigPath,
            State = row.State,
            InstallActionId = row.InstallActionId.ToString("N"),
            UninstallActionId = row.UninstallActionId?.ToString("N"),
            ActionStatus = actionStatus,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };

    /// <summary>
    /// Everything one preview is about, from whichever row it started. The two subjects of a
    /// proposal — a catalog entry and an installation — carry the same fields because a removal is
    /// the same write with one entry taken back out.
    /// </summary>
    private sealed record ProposalSubject(
        Guid ProjectId,
        Guid SourceId,
        Guid CatalogId,
        string SourceName,
        string ExtensionId,
        string Kind,
        string Version,
        string ServerId,
        string DisplayName,
        string ManifestHash,
        string? Command,
        IReadOnlyList<string> Args,
        IReadOnlyList<MarketEnvironmentRequestDto> Environment,
        string? Blocker,
        // Where the source that listed this entry is read from. Only a skill install uses it, and
        // only to compose the document address; a removal carries an empty string because it writes
        // a file Core has already been told about and never goes back to the market.
        string SourceLocation = "");

    /// <summary>
    /// One file a proposal would write: where, with what bytes, conditioned on which current hash,
    /// and the two sentences a reviewer needs that the record itself cannot carry. Computed by
    /// whichever plan fits the entry's kind, and the only shape a preview hands to the store.
    /// </summary>
    private sealed record Plan(
        string TargetPath,
        string Content,
        string? ExpectedFileHash,
        string? ReplacesCommand,
        string? Warning);
}
