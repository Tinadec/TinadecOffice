using System.Text.Json;

namespace TinadecCore.Abstractions.Ports;

/// <summary>The two kinds of standing approval a person can give (todo E7).</summary>
public static class ApprovalRuleKinds
{
    /// <summary>
    /// "Commands that start with this are fine": a simple shell/command_run call whose leading words are
    /// the rule's prefix runs without asking again. The person gave the approval once, for exactly that.
    /// </summary>
    public const string CommandPrefix = "command_prefix";

    /// <summary>
    /// "The delegated gates may decide this tool too": the per-session opt-in that lets a delegate-*
    /// permission mode hand shell (human-only by default) to the reviewer/conversation gates.
    /// </summary>
    public const string DelegateTool = "delegate_tool";

    public static readonly IReadOnlyList<string> All = [CommandPrefix, DelegateTool];
}

/// <summary>Only the command tools have a per-session delegated-approval opt-in.
/// The gate policy still applies its risk ceiling; this is not an approval itself.</summary>
public static class DelegatedToolOptInRules
{
    public static bool IsEligibleTool(string? toolId) => toolId is "shell" or "command_run";
}

/// <summary>A standing approval. <see cref="SessionId"/> null means the whole workspace.</summary>
public sealed record ApprovalRuleView(
    Guid Id,
    string Kind,
    string ToolId,
    string? Pattern,
    Guid? SessionId,
    Guid CreatedByPrincipalId,
    DateTimeOffset CreatedAt,
    int UseCount);

public sealed record ApprovalRuleCreate(string Kind, string ToolId, string? Pattern, Guid? SessionId);

public sealed class ApprovalRuleException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>
/// Standing approvals (todo E7, architecture §7.4 items 6–7). Every rule is a person's explicit
/// decision, never a model's: a rule never widens a resource envelope (the PDP's boundary rules run
/// first), and the gates that honour one re-check that it is still active and scoped to the run's
/// session — the caller naming a rule is not trusted to have matched it.
/// </summary>
public interface IApprovalRules
{
    Task<IReadOnlyList<ApprovalRuleView>> ListAsync(Guid? sessionId, CancellationToken cancellationToken = default);

    Task<ApprovalRuleView> CreateAsync(ApprovalRuleCreate rule, CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(Guid ruleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active prefix rule of the session (or the workspace) that covers this command, if any.
    /// Matching does not count a use — a call can be matched more than once on its way through the
    /// PDP and the approval layer; <see cref="RecordUseAsync"/> marks the one actual consumption.
    /// </summary>
    Task<ApprovalRuleView?> MatchCommandAsync(Guid sessionId, string toolId, string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-verifies a rule a caller named: active, of this tool, scoped to this run's session or the
    /// workspace. Verification does not count a use.
    /// </summary>
    Task<bool> VerifyCommandRuleAsync(Guid ruleId, Guid runId, string toolId, CancellationToken cancellationToken = default);

    /// <summary>Counts the one consumption of a rule — the call whose approval was minted from it.</summary>
    Task<ApprovalRuleView> RecordUseAsync(Guid ruleId, CancellationToken cancellationToken = default);

    /// <summary>True when the run's session opted the tool into delegated approval.</summary>
    Task<bool> IsDelegatedToolAsync(Guid runId, string toolId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Which commands a prefix rule may cover, and how. Deliberately narrow: a rule is matched against the
/// words of ONE simple command. Anything the shell would interpret as more than one command or as a
/// substitution — chaining, pipes, redirection, cmd escapes, variable expansion, subshells, line
/// breaks — is never covered, because "npm test &amp;&amp; rm -rf ." starts with "npm test" too.
/// </summary>
public static class CommandPrefixRules
{
    /// <summary>The tools a prefix rule can be written for.</summary>
    public static readonly IReadOnlyList<string> Tools = ["shell", "command_run"];

    public const int MaxPrefixLength = 256;

    private static readonly string[] Forbidden = ["&", "|", ";", "<", ">", "^", "%", "`", "$", "$(", "(", ")", "\n", "\r"];

    public static bool IsRuleTool(string? toolId) => toolId is not null && Tools.Contains(toolId, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the text is one simple command: nothing the shell would chain, redirect or expand.</summary>
    public static bool IsSimple(string command) =>
        !string.IsNullOrWhiteSpace(command) && !Forbidden.Any(token => command.Contains(token, StringComparison.Ordinal));

    /// <summary>The words of a command, split on whitespace; quotes are kept as part of their word.</summary>
    public static string[] Words(string command) =>
        command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Why a prefix cannot be a rule, or null when it can.</summary>
    public static string? PrefixError(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return "A command prefix rule needs the prefix, e.g. \"npm test\".";
        if (prefix.Length > MaxPrefixLength) return $"A prefix is at most {MaxPrefixLength} characters.";
        if (!IsSimple(prefix)) return "A prefix must be the start of one simple command: no &, |, ;, <, >, ^, %, backticks, $( or parentheses.";
        return null;
    }

    /// <summary>
    /// True when the command is simple and its first words are exactly the prefix's words. The
    /// executable (first word) compares case-insensitively — Windows resolves it that way — the
    /// arguments exactly, so "git push" never covers "git PUSH --force".
    /// </summary>
    public static bool Matches(string command, string prefix)
    {
        if (!IsSimple(command) || !IsSimple(prefix)) return false;
        var words = Words(command);
        var wanted = Words(prefix);
        if (wanted.Length == 0 || words.Length < wanted.Length) return false;
        if (!string.Equals(words[0], wanted[0], StringComparison.OrdinalIgnoreCase)) return false;
        for (var index = 1; index < wanted.Length; index++)
            if (!string.Equals(words[index], wanted[index], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// The command a call would run, as the rule is matched against it: <c>shell</c>'s <c>command</c>,
    /// or <c>command_run</c>'s executable and arguments. Null when the call carries none.
    /// </summary>
    public static string? CommandOf(string toolId, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (string.Equals(toolId, "shell", StringComparison.OrdinalIgnoreCase))
            return parameters.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String ? command.GetString() : null;
        if (!string.Equals(toolId, "command_run", StringComparison.OrdinalIgnoreCase)) return null;
        // A call that widens its own sandbox (extra paths, environment, persisted grants) or feeds stdin is
        // more than "this command": no prefix rule covers it, so it asks like any other call.
        foreach (var widening in SandboxWideningFields)
            if (parameters.TryGetProperty(widening, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)
                && !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0)
                && !(value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString())))
                return null;
        var executable = FirstString(parameters, "executable", "command", "file", "program");
        if (string.IsNullOrWhiteSpace(executable)) return null;
        var arguments = FirstArray(parameters, "arguments", "args");
        return arguments.Length == 0 ? executable : executable + " " + string.Join(' ', arguments);
    }

    private static readonly string[] SandboxWideningFields =
        ["additional_read_paths", "additional_write_paths", "environment_variable_names", "persist_grants", "stdin"];

    private static string? FirstString(JsonElement parameters, params string[] names)
    {
        foreach (var name in names)
            if (parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private static string[] FirstArray(JsonElement parameters, params string[] names)
    {
        foreach (var name in names)
        {
            if (!parameters.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Array)
            {
                var entries = value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).ToArray();
                return entries.All(item => item is not null) ? entries.Cast<string>().ToArray() : [];
            }
            if (value.ValueKind == JsonValueKind.String) return [value.GetString() ?? string.Empty];
        }
        return [];
    }
}
