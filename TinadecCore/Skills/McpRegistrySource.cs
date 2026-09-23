using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Skills;

/// <summary>
/// Reads the official MCP Registry (<c>registry.modelcontextprotocol.io</c>) through the Tool
/// Provider's reserved <c>#fetch</c> control tool.
///
/// Why the read goes out through the tool process rather than an <see cref="HttpClient"/> in
/// Core: the provider owns the server-side request forgery guard, and it is the one that can
/// apply it where it matters — inside the connect callback, against the address it is about to
/// dial. A second HTTP stack in Core would be a second policy to keep in sync and to audit, and
/// the first thing wrong with it would be that it disagreed with the tool layer.
///
/// What comes back is untrusted external text. It is stored as a claim about a thing and
/// nothing more: no command, no package, no path is derived from it here, and it never reaches a
/// model. <see cref="Entry.Version"/> is mandatory because "install this" without a pinned
/// version is not an actionable claim.
/// </summary>
internal static class McpRegistrySource
{
    /// <summary>Reserved control tool; never offered to a model in a manifest.</summary>
    internal const string FetchToolId = "#fetch";

    private const int PageSize = 100;

    /// <summary>Ask for the provider's own ceiling: a truncated JSON body is an unusable body.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private const int MaxDescriptionChars = 2000;
    private const int MaxDetailChars = 12_000;

    // Ceilings on the fields that later become a command line. They are small on purpose: a
    // package name, a version and a flag do not need room, and the slack is what an external
    // source would use to make a proposal unreadable rather than to say something true.
    private const int MaxCommandChars = 256;
    private const int MaxIdentifierChars = 256;
    private const int MaxVersionChars = 128;
    private const int MaxRuntimeArguments = 8;
    private const int MaxEnvironmentRequests = 16;
    private const int MaxEnvironmentNameChars = 128;
    private const int MaxEnvironmentDescriptionChars = 240;

    /// <summary>
    /// One completed pass or one named failure. A refresh that did not complete is never
    /// expressed as an empty entry list, because the caller has to be able to tell "the market
    /// is empty" from "we could not read it" — and must not delete rows on the strength of the
    /// second one.
    /// </summary>
    internal sealed record Result(
        bool Completed,
        string? Reason,
        bool Blocked,
        List<Entry> Entries,
        int RefusedRows,
        int PagesFetched,
        bool TruncatedPages)
    {
        internal static Result Failed(string reason, bool blocked = false) =>
            new(false, reason, blocked, [], 0, 0, false);
    }

    internal sealed record Entry(
        string ExtensionId,
        string Version,
        string Kind,
        string DisplayName,
        string? Description,
        string DetailJson,
        string ManifestHash);

    internal static async Task<Result> FetchAsync(
        IToolProvider provider,
        string workspaceRoot,
        string location,
        CancellationToken cancellationToken)
    {
        var entries = new List<Entry>();
        var cursor = string.Empty;
        var refused = 0;
        var pages = 0;

        for (var page = 0; page < MarketRefreshPolicy.MaxListingPages; page++)
        {
            var request = BuildPageUrl(location, cursor);
            if (request is null)
                return Result.Failed($"'{location}' is not a usable https registry endpoint.");

            var fetch = await FetchPageAsync(provider, workspaceRoot, request, cancellationToken)
                .ConfigureAwait(false);

            if (fetch.Error is not null)
                return Result.Failed(fetch.Error, fetch.Blocked);

            pages++;

            if (!TryReadListing(fetch.Body!, out var rows, out var nextCursor, out var parseError))
                return Result.Failed(parseError ?? "The registry response is not a server listing.");

            foreach (var row in rows)
            {
                var entry = Project(row);
                if (entry is null)
                {
                    // Counted rather than skipped quietly: the row count the source advertised and
                    // the count Core stored must differ visibly, if they differ at all.
                    refused++;
                    continue;
                }

                entries.Add(entry);
            }

            cursor = nextCursor ?? string.Empty;
            if (string.IsNullOrEmpty(cursor))
                return new Result(true, null, false, entries, refused, pages, false);
        }

        // A cursor was still pending when the ceiling was reached: this catalog is a prefix of
        // the market, and says so.
        return new Result(true, null, false, entries, refused, pages, true);
    }

    /// <summary>
    /// Core builds the whole URL; the source row contributes only the endpoint. A query or
    /// fragment already in the stored location is refused instead of merged, so a saved source
    /// cannot smuggle parameters into the request the guard will let through.
    /// </summary>
    private static string? BuildPageUrl(string location, string cursor)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
            return null;

        var builder = new StringBuilder(uri.GetLeftPart(UriPartial.Path));
        builder.Append("?limit=").Append(PageSize);
        if (cursor.Length > 0)
            builder.Append("&cursor=").Append(Uri.EscapeDataString(cursor));

        return builder.ToString();
    }

    private sealed record Page(string? Body, string? Error, bool Blocked = false);

    private static async Task<Page> FetchPageAsync(
        IToolProvider provider,
        string workspaceRoot,
        string url,
        CancellationToken cancellationToken)
    {
        JsonElement result;
        try
        {
            var response = await provider.CallAsync(
                workspaceRoot,
                new ToolWireRequestDto
                {
                    ToolId = FetchToolId,
                    // No run owns a market refresh; the provider only echoes this on wire events.
                    SessionId = "market-refresh",
                    Approved = false,
                    Params = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["url"] = url,
                        ["max_bytes"] = MaxBytes,
                        ["timeout_ms"] = (int)RequestTimeout.TotalMilliseconds,
                    })
                },
                RequestTimeout + TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return new Page(null, response.Error ?? "The tool provider rejected the fetch.");

            if (response.Result is not { } payload || payload.ValueKind != JsonValueKind.Object)
                return new Page(null, $"{FetchToolId} returned no object payload.");

            result = payload;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Page(null, ex.Message);
        }

        // The guard's refusal is a different fact from a network failure: one means Core asked
        // for something it should not have, the other means the market was unreachable.
        if (result.TryGetProperty("blocked", out var blocked) && blocked.ValueKind == JsonValueKind.True)
            return new Page(null, ReadText(result, "error") ?? "The egress guard refused this target.", true);

        if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            return new Page(null, ReadText(result, "error") ?? $"Fetching {url} did not succeed.");

        var body = ReadText(result, "body");
        if (string.IsNullOrEmpty(body))
            return new Page(null, $"Fetching {url} returned no readable body.");

        return new Page(body, null, false);
    }

    private static bool TryReadListing(
        string body,
        out IReadOnlyList<JsonElement> rows,
        out string? nextCursor,
        out string? error)
    {
        rows = [];
        nextCursor = null;
        error = null;

        JsonElement document;
        try
        {
            document = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException ex)
        {
            error = $"The registry response is not JSON: {ex.Message}";
            return false;
        }

        if (document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("servers", out var servers)
            || servers.ValueKind != JsonValueKind.Array)
        {
            error = "The registry response carries no servers array.";
            return false;
        }

        var listed = new List<JsonElement>();
        foreach (var row in servers.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;

            // Every registry row wraps the descriptor in "server"; an unwrapped row is not a
            // server the registry v0 contract describes, so it is refused rather than guessed at.
            if (row.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.Object)
                listed.Add(server);
        }

        rows = listed;

        if (document.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String)
            nextCursor = next.GetString();

        return true;
    }

    private static Entry? Project(JsonElement server)
    {
        var extensionId = ReadText(server, "name");
        var version = ReadText(server, "version");
        if (string.IsNullOrWhiteSpace(extensionId) || string.IsNullOrWhiteSpace(version))
            return null;

        var title = ReadText(server, "title");
        var description = Bound(ReadText(server, "description"), MaxDescriptionChars);

        // repository is an object ({url, source}), not a string; the registry's own shape.
        var repository = server.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object
            ? ReadText(repo, "url")
            : null;
        var homepage = repository is not null
            && Uri.TryCreate(repository, UriKind.Absolute, out var repositoryUri)
            && string.Equals(repositoryUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && repositoryUri.Host.Length > 0
                ? repository
                : null;

        var registryType = FirstString(server, "packages", "registryType", "registry_type");
        var packageName = FirstString(server, "packages", "identifier", "name");
        var transports = CollectTransports(server);
        var (install, installBlocker) = ProjectInstall(server);

        var detail = new Dictionary<string, object?>
        {
            ["homepage"] = homepage,
            ["registry_type"] = registryType,
            ["package_name"] = packageName,
            ["transports"] = transports,
            ["install"] = install,
            ["install_blocker"] = installBlocker,
        };
        var detailJson = JsonSerializer.Serialize(detail);
        if (detailJson.Length > MaxDetailChars)
        {
            // Drop the collections before dropping the row: the scalars are what the list renders,
            // and an over-long blob from an external source must not decide how big our rows get.
            detail["transports"] = Array.Empty<string>();
            detailJson = JsonSerializer.Serialize(detail);
        }

        if (detailJson.Length > MaxDetailChars)
        {
            // Never store a cut-short install block: a truncated read of a command line is worse
            // than no command line, and this column has to stay parseable JSON.
            detail["install"] = null;
            detail["install_blocker"] = "This entry's package description is larger than Core stores.";
            detailJson = JsonSerializer.Serialize(detail);
        }

        return new Entry(
            extensionId.Trim(),
            version.Trim(),
            MarketEntryKinds.McpServer,
            Bound(string.IsNullOrWhiteSpace(title) ? extensionId : title!.Trim(), 512) ?? extensionId,
            description,
            detailJson,
            CanonicalJson.Sha256Hex(server));
    }

    private static List<string> CollectTransports(JsonElement server)
    {
        var found = new List<string>();
        foreach (var array in new[] { "remotes", "packages" })
        {
            if (!server.TryGetProperty(array, out var elements) || elements.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var element in elements.EnumerateArray())
            {
                // Remotes say {"type": "streamable-http"}; packages nest theirs one level deeper
                // as {"transport": {"type": "stdio"}}. A flat transport_type is the old spelling.
                var value = element.ValueKind != JsonValueKind.Object
                    ? null
                    : element.TryGetProperty("transport", out var nested) && nested.ValueKind == JsonValueKind.Object
                        ? ReadText(nested, "type")
                        : ReadText(element, "type", "transport_type");

                if (!string.IsNullOrWhiteSpace(value) && !found.Contains(value, StringComparer.OrdinalIgnoreCase))
                    found.Add(value.Trim());
            }
        }

        return found;
    }

    private static string? FirstString(JsonElement root, string array, params string[] keys)
    {
        if (!root.TryGetProperty(array, out var elements) || elements.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var element in elements.EnumerateArray())
        {
            var value = ReadText(element, keys);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    /// <summary>
    /// The closed, local reading of one external package claim: what command would be run, with
    /// what pinned arguments, and which environment keys it asks for.
    ///
    /// This is the only place in the market surface that turns registry text into a command line,
    /// and it refuses rather than guesses. A registry row is an outside party's assertion about a
    /// package, so every field that later reaches a process spawn is checked against a character
    /// whitelist here, at the boundary, instead of being sanitised at each use site. Nothing about
    /// the row is treated as a reason to run anything — the proposal this feeds is executed only
    /// after a human approves a governed <c>write_file</c>.
    /// </summary>
    internal static (Dictionary<string, object?>? Install, string? Blocker) ProjectInstall(JsonElement server)
    {
        if (!server.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array
            || packages.EnumerateArray().FirstOrDefault(p => p.ValueKind == JsonValueKind.Object)
                is not { ValueKind: JsonValueKind.Object } package)
        {
            return (null, "This entry publishes no package record, so there is no command to install.");
        }

        var registryType = (ReadText(package, "registryType", "registry_type") ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsIdentifierShape(registryType))
            return (null, "This entry's registry type is missing or is not a plain name.");

        // Older schema revisions spelled the transport as a sibling string; the current one nests
        // it as {"type": "stdio"}. The tool layer only knows how to spawn a command, so anything
        // that announces itself as non-stdio is refused rather than quietly reinterpreted.
        var transport = package.TryGetProperty("transport", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadText(nested, "type")
            : ReadText(package, "transport_type");
        if (transport is not null && !string.Equals(transport.Trim(), "stdio", StringComparison.OrdinalIgnoreCase))
            return (null, $"This entry publishes a {transport.Trim()} package; the tool layer starts servers by command.");

        var version = Token(ReadText(package, "version") ?? ReadText(server, "version"), MaxVersionChars);
        if (version is null || version is "latest" or "next" or "*" or "0.0.0")
            return (null, "This entry has no exact version to pin, so installing it would mean trusting whatever "
                + "the package host serves at run time.");

        var identifier = Token(ReadText(package, "identifier", "name"), MaxIdentifierChars);
        if (identifier is null)
            return (null, "This entry's package identifier is missing or carries characters a command line cannot "
                + "carry safely.");

        var command = Token(ReadText(package, "runtimeHint") ?? LauncherFor(registryType), MaxCommandChars);
        if (command is null)
            return (null, $"Core has no launcher rule for registry type '{registryType}', so it cannot name a command to run.");

        var args = new List<string>();
        foreach (var value in RuntimeArgumentValues(package))
        {
            if (args.Count >= MaxRuntimeArguments)
                return (null, "This entry publishes more runtime arguments than Core will review at once.");

            var arg = Token(Substitute(value, version), MaxCommandChars);
            if (arg is null)
                return (null, "This entry publishes a runtime argument that is too long or carries unsafe characters.");

            args.Add(arg);
        }

        // The pin is the point of the whole proposal: the version string must appear in the
        // argument that names the package, so approving the command approves that version. A
        // package host may reject a range but will happily serve "the version I meant when I
        // published this", so an identifier that already carries the version is left alone.
        var alreadyPinned = identifier.EndsWith("@" + version, StringComparison.Ordinal)
            || identifier.EndsWith("==" + version, StringComparison.Ordinal);
        var coordinate = alreadyPinned
            ? identifier
            : registryType is "npm"
                ? $"{identifier}@{version}"
                : $"{identifier}=={version}";

        coordinate = Token(Substitute(coordinate, version), MaxIdentifierChars + MaxVersionChars + 2);
        if (coordinate is null)
            return (null, "The pinned package coordinate this entry describes is not a usable command argument.");

        args.Add(coordinate);

        return (new Dictionary<string, object?>
        {
            ["registry_type"] = registryType,
            ["identifier"] = identifier,
            ["version"] = version,
            ["command"] = command,
            ["args"] = args,
            ["environment"] = EnvironmentRequests(package),
        }, null);
    }

    private static string? LauncherFor(string registryType) => registryType switch
    {
        "npm" => "npx",
        "pypi" or "uv" => "uvx",
        _ => null,
    };

    /// <summary>Positional runtime arguments, from either spelling the registry has used.</summary>
    private static IEnumerable<string> RuntimeArgumentValues(JsonElement package)
    {
        if (package.TryGetProperty("runtimeArguments", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var type = ReadText(item, "type");
                if (type is not null && !type.Equals("positional", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = ReadText(item, "value");
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value.Trim();
            }
        }

        // The older schema spelled these as a {"-y": true} map, where the key is the flag.
        if (package.TryGetProperty("packageArguments", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in map.EnumerateObject())
            {
                var on = property.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.String => !string.IsNullOrWhiteSpace(property.Value.GetString()),
                    _ => false,
                };

                if (on && !string.IsNullOrWhiteSpace(property.Name) && !property.Name.Contains('{'))
                    yield return property.Name[0] == '-' ? property.Name : "-" + property.Name;
            }
        }
    }

    /// <summary>
    /// Names, flags and short descriptions only. The registry describes variables it expects but
    /// never carries their values, and nothing here would accept a value if it did: secrets belong
    /// in <c>ISecretStore</c> and reach the server through the config entry's own env indirection.
    /// </summary>
    private static List<Dictionary<string, object?>> EnvironmentRequests(JsonElement package)
    {
        var result = new List<Dictionary<string, object?>>();
        if (!package.TryGetProperty("environmentVariables", out var found)
            && !package.TryGetProperty("environment", out found))
            return result;

        if (found.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in found.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || result.Count >= MaxEnvironmentRequests)
                continue;

            var name = Token(ReadText(item, "name"), MaxEnvironmentNameChars);
            if (name is null)
                continue;

            result.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["required"] = item.TryGetProperty("isRequired", out var required) && required.ValueKind == JsonValueKind.True,
                ["secret"] = item.TryGetProperty("isSecret", out var secret) && secret.ValueKind == JsonValueKind.True,
                ["description"] = Bound(ReadText(item, "description"), MaxEnvironmentDescriptionChars),
            });
        }

        return result;
    }

    /// <summary>
    /// A string destined for a command line. Anything outside the whitelist — whitespace, quotes,
    /// a shell metacharacter, a control code, or a path separator that could escape a package
    /// name — makes the whole value null instead of "cleaned up".
    /// </summary>
    private static string? Token(string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();
        if (value.Length > max || value.Contains('{') || value.Contains('}'))
            return null;

        foreach (var character in value)
        {
            if (!IsCommandSafe(character))
                return null;
        }

        return value;
    }

    private static bool IsCommandSafe(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            or '@' or ':' or '.' or '_' or '/' or '+' or '-' or '=';

    private static bool IsIdentifierShape(string value) =>
        value.Length > 0 && value.Length <= 32 && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static string Substitute(string value, string version) =>
        value.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);

    private static string? ReadText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    /// <summary>
    /// Length-bound without cutting mid-surrogate-pair, and null for empty rather than a string
    /// of nothing — an absent description and a blank one should not be two wire shapes.
    /// </summary>
    private static string? Bound(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length <= max)
            return trimmed;

        var cut = trimmed[..max];
        if (char.IsHighSurrogate(cut[^1]))
            cut = cut[..^1];

        return cut;
    }
}

/// <summary>
/// Canonical JSON used for the stored <c>manifest_hash</c>: object members written in ordinal
/// key order through <see cref="Utf8JsonWriter"/>, so the digest describes the body and not the
/// order the registry happened to serialise it in.
///
/// This is deliberately not RFC 8785. It only has to be stable for one producer (the registry)
/// and one consumer (this store), and a full number-formatting ruleset would be more code than
/// the guarantee it buys — registry entries carry no floats whose spelling is in question.
/// </summary>
internal static class CanonicalJson
{
    internal static string Sha256Hex(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            Write(element, writer);

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(item, writer);

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
