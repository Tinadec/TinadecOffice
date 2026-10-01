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

    /// <summary>
    /// Rows this source stopped listing that a live installation still references. Those rows stay,
    /// aged out, so an installed server never loses the record of what was approved for it.
    /// </summary>
    [JsonPropertyName("retained_rows")]
    public int RetainedRows { get; init; }

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

    /// <summary>
    /// Whether this build could express the entry as something to install. False is not a defect
    /// in the entry: a remote-only listing is a real server, it is just one the tool layer cannot
    /// start by command. <see cref="InstallBlocker"/> says which case this is.
    /// </summary>
    [JsonPropertyName("installable")]
    public bool Installable { get; init; }

    [JsonPropertyName("install_blocker")]
    public string? InstallBlocker { get; init; }
}

/// <summary>
/// The install request. A market install is always for one project: the config file that gets
/// written is that project's tool workspace, which is also what decides which Tool Provider
/// process performs the write.
/// </summary>
public sealed class MarketInstallRequestDto
{
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }
}

/// <summary>
/// Everything a person needs in order to say yes or no, frozen before anything was written: the
/// exact command, the exact file, the exact bytes, and the moment after which Core will refuse to
/// act on it. Nothing in here is recomputed at apply time — apply re-checks its inputs and then
/// writes these bytes, so what was approved is what lands.
/// </summary>
public sealed class MarketInstallProposalDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary><c>install</c> or <c>uninstall</c>: both are one governed file write.</summary>
    [JsonPropertyName("action")]
    public string Action { get; init; } = MarketInstallActions.Install;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("catalog_id")]
    public string? CatalogId { get; init; }

    [JsonPropertyName("installation_id")]
    public string? InstallationId { get; init; }

    [JsonPropertyName("source_name")]
    public string SourceName { get; init; } = string.Empty;

    [JsonPropertyName("extension_id")]
    public string ExtensionId { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    /// <summary>The exact version this proposal pins. Never a range, never <c>latest</c>.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>The key the entry occupies in the server config.</summary>
    [JsonPropertyName("server_id")]
    public string ServerId { get; init; } = string.Empty;

    /// <summary>The command line this write overwrites, when the config already named that server.</summary>
    [JsonPropertyName("replaces_command")]
    public string? ReplacesCommand { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("args")]
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Names and flags only. A proposal can never carry a secret value.</summary>
    [JsonPropertyName("environment")]
    public IReadOnlyList<MarketEnvironmentRequestDto> Environment { get; init; } = [];

    /// <summary>Absolute path of the file that will be written.</summary>
    [JsonPropertyName("target_path")]
    public string TargetPath { get; init; } = string.Empty;

    /// <summary>The exact bytes to be written, shown so the human is not approving a hash blind.</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// The file's current hash as the Tool Provider computed it, which is what makes the write
    /// fail rather than clobber if the file changed after this proposal was made.
    /// </summary>
    [JsonPropertyName("expected_file_hash")]
    public string? ExpectedFileHash { get; init; }

    /// <summary>SHA-256 over the canonical proposal; binds the reviewed bytes to the write.</summary>
    [JsonPropertyName("digest")]
    public string Digest { get; init; } = string.Empty;

    /// <summary>After this instant apply refuses and a new preview is required.</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>What this phase cannot guarantee, stated for the reader of the approval.</summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>One environment variable a package asks for. Values are never part of this shape.</summary>
public sealed class MarketEnvironmentRequestDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("secret")]
    public bool Secret { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
/// What is installed where. The row is Core's record of a proposal a human approved; whether the
/// write actually happened is read from the linked user tool action rather than copied here, so a
/// failed or still-pending approval can never be reported as an install.
/// </summary>
public sealed class MarketInstallationDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("catalog_id")]
    public string CatalogId { get; init; } = string.Empty;

    [JsonPropertyName("source_name")]
    public string SourceName { get; init; } = string.Empty;

    [JsonPropertyName("extension_id")]
    public string ExtensionId { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("server_id")]
    public string ServerId { get; init; } = string.Empty;

    [JsonPropertyName("config_path")]
    public string ConfigPath { get; init; } = string.Empty;

    /// <summary><c>installing</c> or <c>removing</c>; a finished removal is reported as gone.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = MarketInstallationStates.Installing;

    [JsonPropertyName("install_action_id")]
    public string InstallActionId { get; init; } = string.Empty;

    [JsonPropertyName("uninstall_action_id")]
    public string? UninstallActionId { get; init; }

    /// <summary>
    /// Status of whichever action is currently in front of the user, read live from the user tool
    /// action. A governed write crosses two human gates, so a panel points at whichever one is
    /// open: <c>awaiting_user</c> for the permission request, then <c>awaiting_approval</c> for
    /// the approval envelope that carries the frozen bytes.
    /// </summary>
    [JsonPropertyName("action_status")]
    public string? ActionStatus { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// The installation ledger. An envelope rather than a bare array because the row count alone
/// cannot say whether the list is empty because nothing was installed or because this workspace
/// has never used the surface.
/// </summary>
public sealed class MarketInstallationListDto
{
    [JsonPropertyName("installations")]
    public IReadOnlyList<MarketInstallationDto> Installations { get; init; } = [];
}

/// <summary>Vocabulary for <see cref="MarketInstallProposalDto.Action"/>.</summary>
public static class MarketInstallActions
{
    public const string Install = "install";
    public const string Uninstall = "uninstall";
}

/// <summary>Vocabulary for <see cref="MarketInstallationDto.State"/>.</summary>
public static class MarketInstallationStates
{
    /// <summary>An install write was approved for this entry and is not known to have finished removing.</summary>
    public const string Installing = "installing";

    /// <summary>An uninstall write is pending or in flight; the entry is still in the config.</summary>
    public const string Removing = "removing";
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
