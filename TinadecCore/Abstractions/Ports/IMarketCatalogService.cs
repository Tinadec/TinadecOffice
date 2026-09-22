using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Durable market knowledge: which sources Core is allowed to read, and what those sources
/// said the last time they were read.
///
/// This port is deliberately read-mostly and installation-free. Nothing here puts a package
/// on disk, edits a config file, or hands a listing to a model — an entry is a claim a source
/// made about a thing, kept so a human can weigh it later. The boundary between "stored" and
/// "trusted" is the reason <see cref="RefreshSourceAsync"/> returns an outcome rather than a
/// row count: a failed read must leave the previous catalog standing and say so.
/// </summary>
public interface IMarketCatalogService
{
    /// <summary>Source kinds this build has an adapter for. A kind outside this set cannot be created.</summary>
    IReadOnlyList<string> SupportedKinds { get; }

    Task<IReadOnlyList<MarketSourceDto>> ListSourcesAsync(CancellationToken cancellationToken = default);

    Task<MarketSourceDto> CreateSourceAsync(
        CreateMarketSourceRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns a source's adapter on or off. Off means what it says: refreshing it is refused, and
    /// the rows it already contributed stay readable until a refresh from an enabled source
    /// changes them. Without this, a source created disabled could never be enabled again.
    /// </summary>
    Task<MarketSourceDto> SetSourceEnabledAsync(
        Guid sourceId,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Hard-forgets a source and its rows. False when this tenant never had that id.</summary>
    Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one source to completion and makes the catalog match it. Never throws for a source
    /// that simply could not be reached — the outcome says which of the three things happened.
    /// </summary>
    /// <param name="workspaceRoot">
    /// Which Tool Provider process the listing is fetched through. The caller resolves it the
    /// same way the MCP inventory does; it selects a process, it is not a filesystem grant.
    /// </param>
    Task<MarketRefreshDto> RefreshSourceAsync(
        Guid sourceId,
        string workspaceRoot,
        CancellationToken cancellationToken = default);

    Task<MarketCatalogPageDto> ListCatalogAsync(
        MarketCatalogQuery query,
        CancellationToken cancellationToken = default);

    Task<MarketCatalogEntryDto?> FindCatalogEntryAsync(
        Guid catalogId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Catalog filter. <see cref="Search"/> is matched as literal text: the store escapes LIKE
/// wildcards, so a query containing <c>%</c> narrows rather than widens. Ordering is by
/// <see cref="MarketCatalogEntryDto.ExtensionId"/>, so <see cref="Offset"/> pages are stable as
/// long as no refresh lands between two of them.
/// </summary>
public sealed record MarketCatalogQuery(
    string? Kind = null,
    string? Search = null,
    Guid? SourceId = null,
    int Limit = 50,
    int Offset = 0)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>Clamped rather than refused: a client asking for 5000 rows wants "as many as possible".</summary>
    public static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxLimit);
}

/// <summary>
/// Numbers a refresh reports against, living next to the port so the wire and the adapter read
/// one source of truth. <see cref="MaxListingPages"/> is observable as
/// <c>truncated_pages</c>, and <see cref="CatalogTtl"/> as each row's <c>expires_at</c>.
/// </summary>
public static class MarketRefreshPolicy
{
    /// <summary>
    /// Ceiling on listing pages per refresh. Paginated sources advertise no total, so without a
    /// ceiling one refresh could hold an HTTP request open indefinitely.
    /// </summary>
    public const int MaxListingPages = 5;

    /// <summary>How long stored rows stay worth showing without a refresh.</summary>
    public static readonly TimeSpan CatalogTtl = TimeSpan.FromDays(7);
}

/// <summary>Vocabulary for <see cref="MarketCatalogEntryDto.Kind"/> — what an adapter may emit.</summary>
public static class MarketEntryKinds
{
    public static readonly IReadOnlyList<string> All = ["mcp-server", "skill", "acp-adapter", "tool-pack"];

    public const string McpServer = "mcp-server";
}

/// <summary>
/// A market request Core can answer with a machine code instead of a stack trace. The HTTP
/// layer owns the status; the code is the part a client branches on.
/// </summary>
public sealed class MarketCatalogException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Vocabulary for <see cref="MarketCatalogException.Code"/>.</summary>
public static class MarketErrorCodes
{
    /// <summary>A field was missing, oversized, or not a usable https URL.</summary>
    public const string InvalidSource = "invalid_market_source";

    /// <summary>The kind has no adapter in this build. Names the kinds that do.</summary>
    public const string UnsupportedKind = "unsupported_market_source_kind";

    /// <summary>Same tenant, same name already registered.</summary>
    public const string DuplicateSource = "market_source_exists";

    public const string SourceNotFound = "market_source_not_found";

    public const string EntryNotFound = "market_entry_not_found";

    /// <summary>A disabled source was asked to refresh; its rows stay as they are.</summary>
    public const string SourceDisabled = "market_source_disabled";
}
