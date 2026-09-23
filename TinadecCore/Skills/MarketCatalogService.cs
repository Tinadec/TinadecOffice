using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Skills;

/// <summary>
/// The durable market surface, living with <see cref="IntegrationDbContext"/> because that is
/// where <c>extension_sources</c> and <c>extension_catalog_entries</c> are already mapped. A
/// separate Market module would have had to own the same tables twice, and
/// <c>ModulesDoNotReferenceEachOtherDirectly</c> forbids the alternative of reaching into here.
///
/// Two things this deliberately is not:
/// <list type="bullet">
///   <item>It is not a proxy. A refresh stores what a source said at a moment; reading the
///     catalog never touches the network, so a market outage cannot take the browser page
///     down with it.</item>
///   <item>It is not an installer. Nothing here writes a file, a config entry, or a model-facing
///     tool. A catalog row is an unverified claim from an external party, kept so that a human
///     can weigh it later under approval — the listings are data, and stay data.</item>
/// </list>
///
/// Scope is the tenant, not the workspace: a market source describes where software comes from,
/// which is an organisation-level fact, and <c>extension_sources</c> carries no workspace column.
/// </summary>
public sealed class MarketCatalogService : IMarketCatalogService
{
    /// <summary>Source kinds with an adapter in this build. Creating any other is refused.</summary>
    public static readonly IReadOnlyList<string> AdapterKinds = [McpRegistryKind];

    private const string McpRegistryKind = "mcp_registry";
    private const int MaxNameChars = 128;
    private const int MaxLocationChars = 2048;

    private readonly IDbContextFactory<IntegrationDbContext> _dbFactory;
    private readonly ITenantContextAccessor _tenantContext;
    private readonly IToolProvider _provider;

    public MarketCatalogService(
        IDbContextFactory<IntegrationDbContext> dbFactory,
        ITenantContextAccessor tenantContext,
        IToolProvider provider)
    {
        _dbFactory = dbFactory;
        _tenantContext = tenantContext;
        _provider = provider;
    }

    public IReadOnlyList<string> SupportedKinds => AdapterKinds;

    public async Task<IReadOnlyList<MarketSourceDto>> ListSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        var tenant = _tenantContext.Current.TenantId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var sources = await db.Sources.AsNoTracking()
            .Where(x => x.TenantId == tenant && x.DeletedAt == null)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var counts = await db.CatalogEntries.AsNoTracking()
            .Where(x => x.TenantId == tenant)
            .GroupBy(x => x.SourceId)
            .Select(g => new { SourceId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var stored = counts.ToDictionary(x => x.SourceId, x => x.Count);
        return sources.Select(x => Project(x, stored.GetValueOrDefault(x.Id))).ToList();
    }

    public async Task<MarketSourceDto> CreateSourceAsync(
        CreateMarketSourceRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = _tenantContext.Current;

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxNameChars)
            throw Invalid("name", $"A source name between 1 and {MaxNameChars} characters is required.");

        var kind = (request.Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (kind.Length == 0)
            throw Invalid("kind", "A source kind is required.");
        if (!AdapterKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
            throw new MarketCatalogException(
                MarketErrorCodes.UnsupportedKind,
                $"No market adapter reads kind '{kind}'. This build supports: {string.Join(", ", AdapterKinds)}.");

        var location = (request.Location ?? string.Empty).Trim();
        ValidateLocation(location);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var clone = await db.Sources.FirstOrDefaultAsync(
            x => x.TenantId == scope.TenantId && x.Name == name && x.DeletedAt == null,
            cancellationToken).ConfigureAwait(false);
        if (clone is not null)
            throw new MarketCatalogException(
                MarketErrorCodes.DuplicateSource,
                $"A source named '{name}' already exists.");

        var now = DateTimeOffset.UtcNow;
        var record = new ExtensionSourceRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            Name = name,
            Kind = kind,
            Location = location,
            Enabled = request.Enabled ?? true,
            Revision = 1,
            CreatedByPrincipalId = scope.PrincipalId,
            UpdatedByPrincipalId = scope.PrincipalId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Sources.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Project(record, 0);
    }

    public async Task<MarketSourceDto> SetSourceEnabledAsync(
        Guid sourceId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var tenant = _tenantContext.Current.TenantId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = await db.Sources.FirstOrDefaultAsync(
            x => x.Id == sourceId && x.TenantId == tenant && x.DeletedAt == null,
            cancellationToken).ConfigureAwait(false);
        if (source is null)
            throw new MarketCatalogException(MarketErrorCodes.SourceNotFound, "No market source has that id.");

        if (source.Enabled != enabled)
        {
            source.Enabled = enabled;
            source.Revision += 1;
            source.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var count = await db.CatalogEntries.AsNoTracking()
            .CountAsync(x => x.TenantId == tenant && x.SourceId == sourceId, cancellationToken)
            .ConfigureAwait(false);
        return Project(source, count);
    }

    public async Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken cancellationToken = default)
    {
        var tenant = _tenantContext.Current.TenantId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = await db.Sources.FirstOrDefaultAsync(
            x => x.Id == sourceId && x.TenantId == tenant, cancellationToken).ConfigureAwait(false);
        if (source is null)
            return false;

        // An installed entry is traceable to this source's row, and the row is what says which
        // version a human approved. Hard-forgetting it would leave a running server with no
        // recorded provenance, so the removal is refused rather than completed halfway.
        var inUse = await db.Installations.CountAsync(
            x => x.TenantId == tenant && x.SourceId == sourceId, cancellationToken).ConfigureAwait(false);
        if (inUse > 0)
        {
            throw new MarketCatalogException(
                MarketErrorCodes.SourceInUse,
                $"'{source.Name}' still backs {inUse} installation{(inUse == 1 ? "" : "s")}. "
                + "Remove them first.");
        }

        // Both sides go together. Rows kept behind a deleted source would keep answering the
        // catalog with an entry whose source no longer exists and can never be refreshed again.
        await db.CatalogEntries
            .Where(x => x.TenantId == tenant && x.SourceId == sourceId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Sources.Remove(source);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<MarketRefreshDto> RefreshSourceAsync(
        Guid sourceId,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenantContext.Current;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = await db.Sources.FirstOrDefaultAsync(
            x => x.Id == sourceId && x.TenantId == scope.TenantId && x.DeletedAt == null,
            cancellationToken).ConfigureAwait(false);
        if (source is null)
            throw new MarketCatalogException(MarketErrorCodes.SourceNotFound, "No market source has that id.");
        if (!source.Enabled)
            throw new MarketCatalogException(
                MarketErrorCodes.SourceDisabled,
                $"'{source.Name}' is disabled, so refreshing it would change nothing. Enable it first.");

        if (!string.Equals(source.Kind, McpRegistryKind, StringComparison.OrdinalIgnoreCase))
        {
            // Reachable only for a row created before an adapter was removed. Reported rather
            // than thrown: the caller asked what happened to this source, and "nothing, because"
            // is the answer.
            return Unavailable(source, $"This build has no adapter for kind '{source.Kind}'.");
        }

        var result = await McpRegistrySource.FetchAsync(_provider, workspaceRoot, source.Location, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Completed)
        {
            // The previous catalog stands. The reason is stored on the source row so the next
            // reader of /sources sees it without having to have been present for the failure.
            source.LastError = result.Reason;
            source.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new MarketRefreshDto
            {
                SourceId = source.Id.ToString("N"),
                Outcome = result.Blocked ? MarketRefreshOutcome.Blocked : MarketRefreshOutcome.Unavailable,
                Reason = result.Reason,
                PagesFetched = result.PagesFetched,
            };
        }

        var stored = await ReplaceEntriesAsync(
            db, scope.TenantId, source.Id, result.Entries, result.TruncatedPages, cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        source.LastRefreshedAt = now;
        source.LastError = null;
        source.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new MarketRefreshDto
        {
            SourceId = source.Id.ToString("N"),
            Outcome = MarketRefreshOutcome.Fetched,
            FetchedRows = stored.Stored,
            RefusedRows = result.RefusedRows,
            // Only a full pass is entitled to call a vanished row a deletion. When the page
            // ceiling cut the listing short, everything past it is still expected to exist, so
            // nothing was removed even though the listing did not mention it.
            RemovedRows = result.TruncatedPages ? 0 : stored.Removed,
            // Listed rows that a live installation still references. Without this the arithmetic
            // of a refresh would not close: rows the source stopped listing neither vanished nor
            // stayed silently.
            RetainedRows = result.TruncatedPages ? 0 : stored.Retained,
            PagesFetched = result.PagesFetched,
            TruncatedPages = result.TruncatedPages,
            RefreshedAt = now,
        };
    }

    /// <summary>
    /// Makes this source's rows match one completed listing. Surviving keys keep their row id, so
    /// a later reference to an entry outlives a refresh; only rows the source genuinely stopped
    /// listing disappear — with one exception: a row an installation still references is kept and
    /// aged out instead, because erasing it would delete the record of what was approved.
    /// <paramref name="partial"/> — the page ceiling cut the listing short — suspends deletion
    /// entirely: everything past the prefix is still expected to exist, and an incomplete read
    /// must not be allowed to look like a market that shrank.
    /// </summary>
    private static async Task<(int Stored, int Removed, int Retained)> ReplaceEntriesAsync(
        IntegrationDbContext db,
        Guid tenant,
        Guid sourceId,
        IReadOnlyList<McpRegistrySource.Entry> incoming,
        bool partial,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var current = await db.CatalogEntries
            .Where(x => x.TenantId == tenant && x.SourceId == sourceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Ordinal, and keyed on the same pair the unique index uses: the registry's names are
        // case-sensitive identifiers, and folding them would merge two distinct servers.
        var byKey = current.ToDictionary(x => Key(x.ExtensionId, x.Version), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in incoming)
        {
            var key = Key(entry.ExtensionId, entry.Version);
            if (!seen.Add(key))
                continue;

            var reference = $"{entry.Kind}:{entry.ExtensionId}@{entry.Version}";
            if (byKey.TryGetValue(key, out var existing))
            {
                existing.Kind = entry.Kind;
                existing.DisplayName = entry.DisplayName;
                existing.Description = entry.Description;
                existing.DetailJson = entry.DetailJson;
                existing.ManifestReference = reference;
                existing.ManifestHash = entry.ManifestHash;
                existing.RefreshedAt = now;
                existing.ExpiresAt = now + MarketRefreshPolicy.CatalogTtl;
                continue;
            }

            db.CatalogEntries.Add(new ExtensionCatalogRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant,
                SourceId = sourceId,
                ExtensionId = entry.ExtensionId,
                Version = entry.Version,
                Kind = entry.Kind,
                DisplayName = entry.DisplayName,
                Description = entry.Description,
                DetailJson = entry.DetailJson,
                ManifestReference = reference,
                ManifestHash = entry.ManifestHash,
                RefreshedAt = now,
                ExpiresAt = now + MarketRefreshPolicy.CatalogTtl,
            });
        }

        var dropped = partial
            ? []
            : current.Where(x => !seen.Contains(Key(x.ExtensionId, x.Version))).ToList();

        var referenced = dropped.Count == 0
            ? []
            : (await db.Installations.AsNoTracking()
                .Where(x => x.TenantId == tenant && x.SourceId == sourceId)
                .Select(x => x.CatalogId)
                .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        var retained = 0;
        foreach (var row in dropped)
        {
            if (!referenced.Contains(row.Id))
            {
                db.CatalogEntries.Remove(row);
                continue;
            }

            // The source stopped listing this one, but something installed still names it. The
            // row stays and stops claiming to be current: an install that outlived its listing is
            // a fact the catalog should show, not a row to erase out from under it.
            row.ExpiresAt = now;
            retained++;
        }

        return (seen.Count, dropped.Count - retained, retained);
    }

    public async Task<MarketCatalogPageDto> ListCatalogAsync(
        MarketCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        var tenant = _tenantContext.Current.TenantId;
        var kind = (query.Kind ?? string.Empty).Trim();
        if (kind.Length > 0 && !MarketEntryKinds.All.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            throw new MarketCatalogException(
                "invalid_request",
                $"No catalog kind is called '{kind}'. Known kinds: {string.Join(", ", MarketEntryKinds.All)}.");
        }

        var limit = MarketCatalogQuery.ClampLimit(query.Limit);
        if (query.Offset < 0)
            throw new MarketCatalogException("invalid_request", "offset must not be negative.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var matched = db.CatalogEntries.AsNoTracking().Where(x => x.TenantId == tenant);

        if (kind.Length > 0)
            matched = matched.Where(x => x.Kind == kind);
        if (query.SourceId is { } sourceId)
            matched = matched.Where(x => x.SourceId == sourceId);

        var search = (query.Search ?? string.Empty).Trim();
        if (search.Length > 0)
        {
            // Escaped, then wrapped: LIKE wildcards inside a user's query are the thing they
            // typed, not a pattern to widen the result set with.
            var pattern = "%" + EscapeLike(search) + "%";
            matched = matched.Where(x =>
                EF.Functions.Like(x.ExtensionId, pattern, "\\")
                || EF.Functions.Like(x.DisplayName, pattern, "\\")
                || (x.Description != null && EF.Functions.Like(x.Description, pattern, "\\")));
        }

        // One projection answers both "how many" and "how fresh", without asking SQLite to
        // order or aggregate a DateTimeOffset column — which this repository cannot do anywhere.
        var stamps = await matched.Select(x => x.RefreshedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (stamps.Count == 0)
            return new MarketCatalogPageDto { TotalAvailable = 0 };

        var rows = await matched
            .OrderBy(x => x.ExtensionId).ThenBy(x => x.Id)
            .Skip(query.Offset).Take(limit + 1)
            .Join(db.Sources.AsNoTracking(), e => e.SourceId, s => s.Id, (e, s) => new { Entry = e, s.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();

        return new MarketCatalogPageDto
        {
            Items = page.Select(x => Project(x.Entry, x.Name)).ToList(),
            TotalAvailable = stamps.Count,
            HasMore = hasMore,
            AsOf = stamps.Max(),
        };
    }

    public async Task<MarketCatalogEntryDto?> FindCatalogEntryAsync(
        Guid catalogId,
        CancellationToken cancellationToken = default)
    {
        var tenant = _tenantContext.Current.TenantId;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var found = await db.CatalogEntries.AsNoTracking()
            .Where(x => x.Id == catalogId && x.TenantId == tenant)
            .Join(db.Sources.AsNoTracking(), e => e.SourceId, s => s.Id, (e, s) => new { Entry = e, s.Name })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return found is null ? null : Project(found.Entry, found.Name);
    }

    private static MarketCatalogEntryDto Project(ExtensionCatalogRecord entry, string sourceName)
    {
        var detail = ReadDetail(entry.DetailJson);
        return new MarketCatalogEntryDto
        {
            CatalogId = entry.Id.ToString("N"),
            SourceId = entry.SourceId.ToString("N"),
            SourceName = sourceName,
            ExtensionId = entry.ExtensionId,
            Kind = entry.Kind,
            Version = entry.Version,
            DisplayName = entry.DisplayName,
            Description = string.IsNullOrEmpty(entry.Description) ? null : entry.Description,
            Homepage = detail.Homepage,
            RegistryType = detail.RegistryType,
            Transports = detail.Transports,
            ManifestHash = entry.ManifestHash,
            RefreshedAt = entry.RefreshedAt,
            ExpiresAt = entry.ExpiresAt,
            Installable = detail.Installable,
            InstallBlocker = detail.InstallBlocker,
        };
    }

    /// <summary>
    /// The stored detail blob, read tolerantly. It was written by this build from external text;
    /// a row whose blob cannot be read still lists, with the display fields missing, rather than
    /// turning one unreadable row into a 500 for the whole page.
    /// </summary>
    private static (string? Homepage, string? RegistryType, IReadOnlyList<string> Transports,
        bool Installable, string? InstallBlocker) ReadDetail(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, null, [], false, "This entry stores no detail.");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, null, [], false, "This entry's stored detail could not be read back.");

            var transports = new List<string>();
            if (root.TryGetProperty("transports", out var listed) && listed.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in listed.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                        transports.Add(value);
                }
            }

            // Presence, not re-validation: the install block was vetted by the adapter before it
            // was stored, and MarketInstallService checks it again at preview. What a reader gets
            // here is whether an install could ever be offered, so a row that will not produce a
            // proposal is not shown an install button.
            var installable = root.TryGetProperty("install", out var install)
                && install.ValueKind == JsonValueKind.Object;
            var blocker = installable
                ? null
                : Text(root, "install_blocker") ?? "This entry cannot be installed by this build.";

            return (Text(root, "homepage"), Text(root, "registry_type"), transports, installable, blocker);
        }
        catch (JsonException)
        {
            return (null, null, [], false, "This entry's stored detail could not be read back.");
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static MarketSourceDto Project(ExtensionSourceRecord source, int entryCount) => new()
    {
        Id = source.Id.ToString("N"),
        Name = source.Name,
        Kind = source.Kind,
        Location = source.Location,
        Enabled = source.Enabled,
        Revision = source.Revision,
        LastRefreshedAt = source.LastRefreshedAt,
        LastError = string.IsNullOrEmpty(source.LastError) ? null : source.LastError,
        EntryCount = entryCount,
    };

    private static MarketRefreshDto Unavailable(ExtensionSourceRecord source, string reason) => new()
    {
        SourceId = source.Id.ToString("N"),
        Outcome = MarketRefreshOutcome.Unavailable,
        Reason = reason,
    };

    /// <summary>
    /// An https URL and nothing else. Credentials in the userinfo part, a preset query, and a
    /// fragment are all refused because each would be Core sending something its own builder did
    /// not compose. Plain http is refused here rather than downgraded: the provider would fetch it
    /// and note the downgrade, which is the wrong place to decide that market metadata travels
    /// unencrypted.
    /// </summary>
    private static void ValidateLocation(string location)
    {
        if (location.Length == 0 || location.Length > MaxLocationChars)
            throw Invalid("location", $"A source location of at most {MaxLocationChars} characters is required.");

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri))
            throw Invalid("location", "The source location is not an absolute URL.");

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw Invalid("location", "A market source must be reached over https.");

        if (uri.UserInfo.Length > 0)
            throw Invalid("location", "A source location must not carry credentials.");

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw Invalid("location", "Query parameters and fragments are built by Core, not by the source.");

        if (uri.Host.Length == 0)
            throw Invalid("location", "The source location has no host.");
    }

    private static MarketCatalogException Invalid(string field, string message) =>
        new("invalid_request", $"{field}: {message}");

    private static string Key(string extensionId, string version) => extensionId + "\n" + version;

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
