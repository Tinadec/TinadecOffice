using System.Text.Json;
using System.Text.RegularExpressions;

namespace TinadecCore.Abstractions.Ports;

/// <summary>The kinds of environment the steward hands out (todo E1).</summary>
public static class EnvironmentKinds
{
    public const string Local = "local";
    public const string Cloud = "cloud";
    public const string Remote = "remote";
    public const string Terminal = "terminal";
    public const string Test = "test";

    public static readonly IReadOnlyList<string> All = [Local, Cloud, Remote, Terminal, Test];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

public static class EnvironmentStatuses
{
    public const string Available = "available";
    public const string Disabled = "disabled";
}

/// <summary>Who holds one slot of an environment right now (a live <c>environment</c> lease).</summary>
public sealed record EnvironmentHolder(Guid LeaseId, int Slot, Guid? SessionId, Guid? RunId, Guid? TaskId, string Reason);

/// <summary>
/// One registered environment and who is using it. <see cref="ConnectionJson"/> describes how to reach
/// it (host, url, image, working directory…) and never carries a secret: a credential is named by a
/// <c>secret_ref</c> the secret store resolves.
/// </summary>
public sealed record EnvironmentView(
    Guid Id,
    string Key,
    string Kind,
    string DisplayName,
    string? Description,
    string ConnectionJson,
    int Capacity,
    string Status,
    IReadOnlyList<EnvironmentHolder> Holders,
    DateTimeOffset UpdatedAt)
{
    public int FreeSlots => Status == EnvironmentStatuses.Available ? Math.Max(0, Capacity - Holders.Count) : 0;
}

public sealed record EnvironmentRegistration(string Key, string Kind, string DisplayName, string? Description, string? ConnectionJson, int Capacity = 1);

/// <summary>A change to a registration; null leaves a field as it is.</summary>
public sealed record EnvironmentUpdate(string? DisplayName = null, string? Description = null, string? ConnectionJson = null, int? Capacity = null, string? Status = null);

/// <summary>
/// Asks for one environment for a run: a named one (<see cref="Key"/>) or any available one of a
/// <see cref="Kind"/>. The lease is the run's until it releases it or the run ends.
/// </summary>
public sealed record EnvironmentAcquireRequest(Guid SessionId, Guid RunId, Guid? TaskId, Guid? AgentInstanceId, string? Key, string? Kind, string Reason);

/// <summary>
/// The outcome. Granted: the environment and the slot this run holds. Refused: why, and who holds
/// what was asked for — the caller decides whether to wait, pick another, or say so.
/// </summary>
public sealed record EnvironmentAcquireResult(EnvironmentView? Environment, int? Slot, Guid? LeaseId, string? Error, IReadOnlyList<EnvironmentHolder> BusyHolders)
{
    public bool Granted => LeaseId is not null;
}

public sealed class EnvironmentRegistryException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>
/// The environment steward's registry (todo E1): which test, cloud, remote, local and terminal
/// environments this workspace has, and — through the resource ledger (<c>kind=environment</c>,
/// purpose <c>assignment</c>) — which run holds each. Allocation is the steward's decision; the
/// ledger only records it and refuses a second holder, so two runs never share a slot. A lease ends
/// with its run like every other, so a crashed run never keeps an environment.
/// </summary>
public interface IEnvironmentRegistry
{
    Task<IReadOnlyList<EnvironmentView>> ListAsync(CancellationToken cancellationToken = default);

    Task<EnvironmentView> RegisterAsync(EnvironmentRegistration registration, CancellationToken cancellationToken = default);

    Task<EnvironmentView> UpdateAsync(Guid environmentId, EnvironmentUpdate update, CancellationToken cancellationToken = default);

    Task<EnvironmentAcquireResult> AcquireAsync(EnvironmentAcquireRequest request, CancellationToken cancellationToken = default);

    /// <summary>Releases what the run holds of one environment (every slot). Returns how many leases ended.</summary>
    Task<int> ReleaseAsync(Guid runId, string key, CancellationToken cancellationToken = default);
}

/// <summary>Shape rules shared by every entry point: the REST API and the agents' tools.</summary>
public static partial class EnvironmentRules
{
    public const int MaxCapacity = 64;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex KeyPattern();

    // A connection description names its credential; it never is one.
    [GeneratedRegex("pass(word|wd)?|secret|token|api[-_]?key|private[-_]?key|credential|cookie", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyPattern();

    public static string? KeyError(string? key) =>
        key is not null && KeyPattern().IsMatch(key) ? null
        : "key must be 1-64 characters of lowercase letters, digits, '.', '_' or '-', starting with a letter or digit.";

    /// <summary>
    /// Null when the description is a JSON object with no secret-looking field; otherwise why not.
    /// A field that holds a credential must be replaced by <c>secret_ref</c> (a secret store name).
    /// </summary>
    public static string? ConnectionError(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return "connection must be a JSON object."; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "connection must be a JSON object.";
            if (json.Length > 8192) return "connection is limited to 8192 characters.";
            return FindSecret(document.RootElement) is { } field
                ? $"connection field '{field}' looks like a credential. Store the secret in the secret store and name it with secret_ref instead."
                : null;
        }
    }

    private static string? FindSecret(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!property.Name.Equals("secret_ref", StringComparison.OrdinalIgnoreCase) && SecretKeyPattern().IsMatch(property.Name)) return property.Name;
                if (FindSecret(property.Value) is { } nested) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (FindSecret(item) is { } nested) return nested;
        }
        return null;
    }
}
