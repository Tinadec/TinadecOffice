using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Turning one catalog claim into a file on disk, in two steps that cannot be collapsed.
///
/// A preview computes everything — the pinned command, the exact bytes, the absolute target path,
/// the hash the write will be conditioned on — and stores it under a digest and a deadline. An
/// apply re-checks that nothing moved and then asks the governed write path to perform the write;
/// it does not write anything itself. That indirection is the whole design: the only filesystem
/// mutation reachable from the market is a <c>write_file</c> user tool action, so a market install
/// inherits the workspace snapshot, the per-file review, and the one-time approval that every
/// other governed write already has. There is no second way to put a file on disk from here, and
/// no code path in this module that spawns a process.
///
/// What this therefore cannot claim: that approving an install ran anything. The package is
/// fetched and started by the Tool Provider the first time the server is used, and a package
/// host version string is a name, not a content digest — see
/// <see cref="MarketInstallPolicy.VersionPinNote"/>.
/// </summary>
public interface IMarketInstallService
{
    /// <summary>
    /// Freezes what an install would do for one catalog row in one project. Throws
    /// <see cref="MarketCatalogException"/> instead of returning a hollow proposal when the entry
    /// cannot be expressed as a command, the config target resolves outside the project, or the
    /// provider could not be read.
    /// </summary>
    Task<MarketInstallProposalDto> PreviewInstallAsync(
        Guid catalogId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Freezes the reverse of an existing installation: the same file with that one entry taken
    /// back out. Removing an entry never deletes package bytes the provider may have downloaded,
    /// which is the same reasoning attachments use for content stored by digest.
    /// </summary>
    Task<MarketInstallProposalDto> PreviewUninstallAsync(
        Guid installationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands a still-valid proposal to the governed write path. Returns the installation with the
    /// status of the action it created; the write itself happens when a human approves that
    /// action, or is refused.
    /// </summary>
    Task<MarketInstallationDto> ApplyAsync(
        Guid proposalId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MarketInstallationDto>> ListInstallationsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Numbers the install surface reports against, beside the port that uses them.</summary>
public static class MarketInstallPolicy
{
    /// <summary>
    /// How long a proposal stays applyable. Fifteen minutes, matching the agent pack preview,
    /// because the useful deadline is "while a person is still reading this screen", and a stale
    /// proposal must be recomputed rather than applied on faith.
    /// </summary>
    public static readonly TimeSpan ProposalTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Ceiling on the config bytes a proposal may carry. Generous for a server list, small enough
    /// that a stored proposal cannot become an unbounded blob table.
    /// </summary>
    public const int MaxConfigChars = 64 * 1024;

    /// <summary>
    /// Stated on every proposal, because it is the one thing about pinning this phase cannot
    /// close: a version string fixes which release is named, not which bytes will be served for
    /// it. Closing it needs a registry that hands out content digests, or a lockfile surface this
    /// product does not have.
    /// </summary>
    public const string VersionPinNote =
        "The pinned version is the package host's name for a release, not a content digest: a host "
        + "can serve different bytes for the same version. Nothing here has downloaded or run the package.";

    /// <summary>
    /// Ceiling on a fetched <c>SKILL.md</c> a proposal may carry. <see
    /// cref="WorkspaceSkillPolicy.MaxFileBytes"/> is the size at which the loader refuses to parse a
    /// skill at all, so a proposal above this line freezes bytes that the workspace would never
    /// advertise — the install would succeed and do nothing.
    /// </summary>
    public const long MaxSkillBodyBytes = WorkspaceSkillPolicy.MaxFileBytes;

    /// <summary>
    /// Stated instead of <see cref="VersionPinNote"/> for a kind whose content Core reads itself.
    /// The difference is real, not rhetorical: a package host serves whatever it means by a version
    /// string at run time, while a skill install writes the exact bytes that were fetched, hashed,
    /// and shown — and never fetches again at apply.
    /// </summary>
    public const string ContentPinNote =
        "These bytes were fetched once, when the proposal was previewed, and are what the write will "
        + "put on disk; Core does not re-read the source when the approval is granted. The source can "
        + "publish a different file afterwards, and this proposal will still write these.";
}
