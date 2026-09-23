using System.Text.Json;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Skills;

/// <summary>
/// Reads a repository of Agent Skills: one JSON index that names the skills it holds, and — per
/// skill, only when somebody asks to install it — the <c>SKILL.md</c> the index does not carry.
///
/// The two reads are deliberately separated. An MCP registry row describes a package the package
/// host will resolve later, so a catalog entry is the whole story; a skill row describes a document
/// whose bytes <em>are</em> the thing, and those bytes are what a person has to see before approving.
/// Fetching every listed document at refresh time would make a 400-skill repository 401 requests to
/// answer one list call, and would freeze content nobody chose.
///
/// What the index is allowed to say is narrow by construction: a name, a description, a version, a
/// home page. In particular, <c>url</c>/<c>path</c>/<c>download_url</c>-shaped fields are read for
/// display and never dialed. The address Core fetches is composed here from the source's own stored
/// location plus a name that has already passed <see cref="WorkspaceSkillPolicy.ValidateName"/> —
/// so the origin of every market read stays a set a human chose when registering the source, one
/// path segment can't contain <c>..</c>, and a listing cannot point Core at a host its operator
/// never approved. A repository whose layout differs from <c>&lt;index dir&gt;/&lt;name&gt;/SKILL.md</c>
/// is not readable by this adapter, and that is the cost paid for the guarantee.
/// </summary>
internal static class SkillRepositorySource
{
    /// <summary>
    /// Spelling for a source that lists a skill without saying which release it means. A skill
    /// carries no package coordinate, so unlike a registry row this is not a claim Core has to
    /// refuse: the pin for a skill is the bytes frozen at preview, not a string in the index.
    /// </summary>
    internal const string Unversioned = "unversioned";

    /// <summary>Ceiling on stored rows per index. Past it the listing is a prefix, and says so.</summary>
    internal const int MaxRows = 400;

    private const int MaxDescriptionChars = 2000;
    private const int MaxDetailChars = 4000;
    private const int MaxVersionChars = 128;
    private const int MaxHomepageChars = 2048;
    private const int MaxTitleChars = 512;

    internal static async Task<MarketListing> FetchAsync(
        IToolProvider provider,
        string workspaceRoot,
        string location,
        CancellationToken cancellationToken)
    {
        var page = await MarketFetch.FetchPageAsync(provider, workspaceRoot, location, cancellationToken)
            .ConfigureAwait(false);

        if (page.Error is not null)
            return MarketListing.Failed(page.Error, page.Blocked);

        if (!TryReadIndex(page.Body!, out var rows, out var parseError))
            return MarketListing.Failed(parseError ?? "The response is not a skill index.");

        var entries = new List<MarketEntry>();
        var refused = 0;

        foreach (var row in rows)
        {
            if (entries.Count >= MaxRows)
            {
                // Reported as truncated rather than dropped quietly: a refresh that stopped early
                // must not be allowed to delete the rows past the ceiling.
                return MarketListing.Complete(entries, refused, 1, true);
            }

            var entry = Project(row);
            if (entry is null)
            {
                refused++;
                continue;
            }

            entries.Add(entry);
        }

        return MarketListing.Complete(entries, refused, 1, false);
    }

    /// <summary>
    /// An index is an array of skill rows, or an object holding one under <c>skills</c>. Anything
    /// else is refused with the shape named, because "we found no skills" and "this is not a skill
    /// index" are different answers to a person who just registered a source.
    /// </summary>
    private static bool TryReadIndex(string body, out IReadOnlyList<JsonElement> rows, out string? error)
    {
        rows = [];
        error = null;

        JsonElement document;
        try
        {
            document = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException ex)
        {
            error = $"The skill index is not JSON: {ex.Message}";
            return false;
        }

        JsonElement listed;
        if (document.ValueKind == JsonValueKind.Array)
        {
            listed = document;
        }
        else if (document.ValueKind == JsonValueKind.Object
                 && (document.TryGetProperty("skills", out listed)
                     || document.TryGetProperty("entries", out listed))
                 && listed.ValueKind == JsonValueKind.Array)
        {
            // Two keys, not one: an index written by hand usually names its array after what is in
            // it, and reading either spelling costs one clause. Anything else is the next branch.
        }
        else
        {
            error = "The skill index carries no 'skills' array.";
            return false;
        }

        var accepted = new List<JsonElement>();
        foreach (var row in listed.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object)
                accepted.Add(row);
        }

        rows = accepted;
        return true;
    }

    /// <summary>
    /// One index row, or null when it cannot be stored. A row is stored only if its name is a valid
    /// skill name: that single rule is what makes the identifier safe to interpolate into a path and
    /// a URL alike, so there is no later use site that has to remember to re-check it. A repository
    /// holding <c>My_Skill</c> therefore lists it as a refusal count rather than as a row nobody can
    /// install — and the author of that repository is told the same rule by
    /// <see cref="WorkspaceSkillPolicy.ValidateName"/> when they run the workspace locally.
    /// </summary>
    private static MarketEntry? Project(JsonElement row)
    {
        var name = MarketFetch.Text(row, "name", "slug", "id")?.Trim();
        if (!WorkspaceSkillPolicy.ValidateName(name, out _))
            return null;

        var declared = Shown(MarketFetch.Text(row, "version", "tag"), MaxVersionChars);
        var homepage = HttpsOnly(MarketFetch.Text(row, "homepage", "html_url", "url", "link"));
        var description = MarketFetch.Bound(MarketFetch.Text(row, "description", "summary"), MaxDescriptionChars);
        var title = Shown(MarketFetch.Text(row, "title", "display_name"), MaxTitleChars);

        // The same two-key contract a registry row stores: an "install" member means "a proposal
        // could be built from this", and its absence carries the sentence saying why. For a skill
        // the whole description is one path, because the bytes it points at are the thing itself.
        var detail = new Dictionary<string, object?>
        {
            ["declared_version"] = declared,
            ["homepage"] = homepage,
            ["install"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["target_relative_path"] = WorkspaceSkillPolicy.RelativePathFor(name!),
            },
        };
        var detailJson = JsonSerializer.Serialize(detail);
        if (detailJson.Length > MaxDetailChars)
            return null;

        return new MarketEntry(
            name!,
            declared ?? Unversioned,
            MarketEntryKinds.Skill,
            string.IsNullOrWhiteSpace(title) ? name! : title!,
            description,
            detailJson,
            CanonicalJson.Sha256Hex(row));
    }

    /// <summary>
    /// Reads one skill document through the same transport as the index. Never takes a URL from the
    /// caller: the address is composed here, from the source location and the entry's identifier, so
    /// an install can be pointed at a document its listing did not mention.
    /// </summary>
    internal static async Task<(string? Body, string? Error)> ReadDocumentAsync(
        IToolProvider provider,
        string workspaceRoot,
        string location,
        string extensionId,
        CancellationToken cancellationToken)
    {
        if (!TryDocumentUrl(location, extensionId, out var url, out var error))
            return (null, error);

        var page = await MarketFetch.FetchPageAsync(provider, workspaceRoot, url!, cancellationToken)
            .ConfigureAwait(false);

        if (page.Error is not null)
            return (null, page.Error);

        var body = page.Body!;
        if (body.Length > MarketInstallPolicy.MaxSkillBodyBytes)
        {
            return (null,
                $"'{url}' is {body.Length} characters, more than the {MarketInstallPolicy.MaxSkillBodyBytes} "
                + $"Core will write into a workspace. A document this large is also past the point where "
                + $"the skill loader reads it at all ({WorkspaceSkillPolicy.MaxFileBytes} bytes).");
        }

        return (body, null);
    }

    /// <summary>
    /// The one place a skill's remote address is decided: the directory holding the index, plus the
    /// skill's own directory, plus the file name the format fixes. Fails for a name that is not a
    /// skill name and for a location that is not a bare https URL, so a stored source cannot be
    /// edited into a proxy for somewhere else.
    /// </summary>
    internal static bool TryDocumentUrl(string location, string name, out string? url, out string? error)
    {
        url = null;
        error = null;

        if (!WorkspaceSkillPolicy.ValidateName(name, out var reason))
        {
            error = $"This entry cannot become a skill file: {reason}.";
            return false;
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.Host.Length == 0)
        {
            error = $"'{location}' is not a usable https index endpoint, so no skill document can be "
                    + "reached from it.";
            return false;
        }

        var path = uri.AbsolutePath;
        var cut = path.LastIndexOf('/');
        var directory = cut < 0 ? "/" : path[..(cut + 1)];

        url = $"{uri.Scheme}://{uri.Authority}{directory}{name}/{WorkspaceSkillPolicy.SkillFileName}";
        return true;
    }

    /// <summary>
    /// A stored link, kept only if it is https and parses. Nothing fetches this address from Core —
    /// it is the string a person may choose to open in a browser to read the author's own page
    /// before approving. A relative or <c>http:</c> value is dropped rather than normalised, because
    /// a displayed link whose scheme Core chose is a link the source did not write.
    /// </summary>
    private static string? HttpsOnly(string? value)
    {
        var candidate = Shown(value, MaxHomepageChars);
        if (candidate is null)
            return null;

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
               && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
               && uri.Host.Length > 0
            ? candidate
            : null;
    }

    /// <summary>
    /// Trimmed, or null when it does not fit. Unlike <see cref="MarketFetch.Bound"/> this never
    /// shortens: an identifier, a version or a link cut to a ceiling is not a smaller true thing, it
    /// is a different one — and these three fields exist only to be shown as the source wrote them.
    /// </summary>
    private static string? Shown(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > max ? null : trimmed;
    }
}
