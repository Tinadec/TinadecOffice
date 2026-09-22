using System.Text.Json.Serialization;

namespace TinadecCore.Contracts.Dtos;

/// <summary>
/// One configured market source: somewhere Core is allowed to go and look for things a human
/// might later decide to install. A source is a declaration of provenance, not a capability —
/// adding one only ever makes rows appear in <see cref="MarketCatalogPageDto"/>.
/// </summary>
public sealed class MarketSourceDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Which adapter reads it. See <see cref="MarketSourceListDto.SupportedKinds"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    /// <summary>When the last completed read landed. Absent means never refreshed.</summary>
    [JsonPropertyName("last_refreshed_at")]
    public DateTimeOffset? LastRefreshedAt { get; init; }

    /// <summary>
    /// Why the last refresh did not land, as Core worded it. Kept alongside the row so
    /// "0 entries" can be read as an outage rather than an empty market.
    /// </summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    /// <summary>Catalog rows this source currently holds.</summary>
    [JsonPropertyName("entry_count")]
    public int EntryCount { get; init; }
}

/// <summary>
/// The source list. A bare array would be honest here — sources are Core's own rows, so an
/// empty list does mean "nothing configured" — but <see cref="SupportedKinds"/> has to travel
/// with it: the picker has to offer the kinds this build can actually read, and a client that
/// guesses gets a 400 it cannot predict.
/// </summary>
public sealed class MarketSourceListDto
{
    [JsonPropertyName("sources")]
    public IReadOnlyList<MarketSourceDto> Sources { get; init; } = [];

    [JsonPropertyName("supported_kinds")]
    public IReadOnlyList<string> SupportedKinds { get; init; } = [];
}

/// <summary>New-source request. There is deliberately no url, command, or path field.</summary>
public sealed class CreateMarketSourceRequestDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>An https URL Core will read with a fixed query shape. See MarketEndpoints.</summary>
    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
}

/// <summary>
/// The only editable property of a source. Name, kind, and location are not editable: the name
/// is the unique key, and changing a location in place would silently re-point rows the catalog
/// already holds at a different market. Recreate the source instead.
/// </summary>
public sealed class MarketSourceStateRequestDto
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
}

/// <summary>What one refresh actually did.</summary>
public sealed class MarketRefreshDto
{
    [JsonPropertyName("source_id")]
    public string SourceId { get; init; } = string.Empty;

    /// <summary><c>fetched</c>, <c>blocked</c>, or <c>unavailable</c>. See MarketRefreshOutcome.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = MarketRefreshOutcome.Unavailable;

    /// <summary>Rows the source listed. Only meaningful when the outcome is <c>fetched</c>.</summary>
    [JsonPropertyName("fetched_rows")]
    public int FetchedRows { get; init; }

    /// <summary>
    /// Listed rows Core could not store — no name, no published version, or a body Core
    /// declined to trust. Reported separately so a short catalog is never read as a small market.
    /// </summary>
    [JsonPropertyName("refused_rows")]
    public int RefusedRows { get; init; }

    /// <summary>
    /// Previously stored rows this source no longer lists. Reported only for a completed read —
    /// a fetch that never finished is not evidence that anything disappeared.
    /// </summary>
    [JsonPropertyName("removed_rows")]
    public int RemovedRows { get; init; }

    [JsonPropertyName("pages_fetched")]
    public int PagesFetched { get; init; }

    /// <summary>True when the read stopped at the page ceiling with a cursor still pending.</summary>
    [JsonPropertyName("truncated_pages")]
    public bool TruncatedPages { get; init; }

    /// <summary>Why nothing changed, for a non-<c>fetched</c> outcome.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("refreshed_at")]
    public DateTimeOffset? RefreshedAt { get; init; }
}

/// <summary>Vocabulary for <see cref="MarketRefreshDto.Outcome"/>.</summary>
public static class MarketRefreshOutcome
{
    /// <summary>The source answered completely, and the catalog now matches it.</summary>
    public const string Fetched = "fetched";

    /// <summary>The egress guard refused the target. Stored rows are untouched.</summary>
    public const string Blocked = "blocked";

    /// <summary>The read did not complete. Stored rows are untouched.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>One entry as a source described it. Nothing here has been executed or downloaded.</summary>
public sealed class MarketCatalogEntryDto
{
    [JsonPropertyName("catalog_id")]
    public string CatalogId { get; init; } = string.Empty;

    [JsonPropertyName("source_id")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("source_name")]
    public string SourceName { get; init; } = string.Empty;

    /// <summary>The source's own stable identifier for the thing, e.g. an MCP server name.</summary>
    [JsonPropertyName("extension_id")]
    public string ExtensionId { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>The version string the source published. Pinning an install is M2's job.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>Where the source says the project lives. A link to follow, never a path to write.</summary>
    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }

    /// <summary>Distribution channel the source names, e.g. <c>npm</c>, <c>pypi</c>, <c>oci</c>.</summary>
    [JsonPropertyName("registry_type")]
    public string? RegistryType { get; init; }

    /// <summary>Transports the source offers, e.g. <c>stdio</c>, <c>streamable-http</c>.</summary>
    [JsonPropertyName("transports")]
    public IReadOnlyList<string> Transports { get; init; } = [];

    /// <summary>
    /// SHA-256 over the canonical form of the entry body the source returned. This is a
    /// metadata fingerprint, not a package digest: no artifact bytes were fetched.
    /// </summary>
    [JsonPropertyName("manifest_hash")]
    public string ManifestHash { get; init; } = string.Empty;

    [JsonPropertyName("refreshed_at")]
    public DateTimeOffset RefreshedAt { get; init; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>A page of catalog rows plus how fresh they are.</summary>
public sealed class MarketCatalogPageDto
{
    [JsonPropertyName("items")]
    public IReadOnlyList<MarketCatalogEntryDto> Items { get; init; } = [];

    /// <summary>Rows matching the filter, across all pages.</summary>
    [JsonPropertyName("total_available")]
    public int TotalAvailable { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }

    /// <summary>
    /// Newest refresh stamp among the matching rows. Null when none matched, which is the only
    /// way a client can tell "this market is old" from "this market is empty".
    /// </summary>
    [JsonPropertyName("as_of")]
    public DateTimeOffset? AsOf { get; init; }
}
