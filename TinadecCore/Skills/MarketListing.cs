namespace TinadecCore.Skills;

/// <summary>
/// One pass by one source adapter: either a completed listing with its row counts, or a named
/// failure. Living outside both adapters is the point — a refresh outcome is the catalog service's
/// vocabulary, not a property of whichever format the source happens to publish, and the service
/// must not have to know which adapter answered when it reports `blocked` versus `unavailable`.
///
/// A refresh that did not complete is never expressed as an empty entry list, because the caller has
/// to be able to tell "the market is empty" from "we could not read it" — and must not delete rows on
/// the strength of the second one.
/// </summary>
internal sealed record MarketListing(
    bool Completed,
    string? Reason,
    bool Blocked,
    List<MarketEntry> Entries,
    int RefusedRows,
    int PagesFetched,
    bool TruncatedPages)
{
    internal static MarketListing Failed(string reason, bool blocked = false) =>
        new(false, reason, blocked, [], 0, 0, false);

    /// <summary>
    /// A pass that read the source to its end. <paramref name="truncated"/> says the adapter's own
    /// page ceiling cut it short, which is the one completed read that must not delete anything.
    /// </summary>
    internal static MarketListing Complete(List<MarketEntry> entries, int refused, int pages, bool truncated) =>
        new(true, null, false, entries, refused, pages, truncated);
}

/// <summary>
/// One row a source claims exists, before any of it is installable. <see cref="Version"/> is
/// mandatory because "install this" without a pin is not an actionable claim, and
/// <see cref="ManifestHash"/> is the digest of the source's own document for the row, which is what
/// later invalidates a proposal whose entry was republished under different bytes.
/// </summary>
internal sealed record MarketEntry(
    string ExtensionId,
    string Version,
    string Kind,
    string DisplayName,
    string? Description,
    string DetailJson,
    string ManifestHash);
