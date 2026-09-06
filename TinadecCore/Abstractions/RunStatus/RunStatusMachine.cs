namespace TinadecCore.Abstractions;

/// <summary>
/// Single source of truth for the 12-state run status vocabulary and its legal
/// transition table (plan §3.3 item 3). Pure, side-effect-free rules: C# lifecycle
/// services and the F# strategy layer both delegate here, and no business module
/// may keep a local table or compare statuses against ad-hoc string sets.
/// </summary>
public static class RunStatusMachine
{
    /// <summary>All known run statuses (vocabulary of record).</summary>
    public static readonly IReadOnlyList<string> KnownStates =
    [
        "planning", "understanding", "executing", "replanning",
        "awaiting_approval", "awaiting_delegate", "awaiting_user",
        "paused", "reviewing", "completed", "failed", "cancelled"
    ];

    private static readonly HashSet<string> Known = new(KnownStates, StringComparer.OrdinalIgnoreCase);

    public static bool IsKnown(string status) => Known.Contains(status);

    public static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled";

    public static bool CanTransition(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return true;
        if (IsTerminal(from)) return false;
        return to switch
        {
            "cancelled" => true,
            "failed" => true,
            "planning" => from is "planning",
            "understanding" => from is "planning",
            "executing" => from is "planning" or "understanding" or "replanning" or "paused" or "awaiting_approval" or "awaiting_delegate" or "awaiting_user" or "reviewing",
            "replanning" => from is "understanding" or "executing" or "awaiting_approval" or "reviewing",
            "awaiting_approval" => from is "understanding" or "executing" or "replanning",
            "awaiting_delegate" => from is "understanding" or "executing" or "replanning" or "awaiting_approval",
            "awaiting_user" => from is "understanding" or "executing" or "replanning" or "awaiting_approval" or "awaiting_delegate" or "reviewing",
            "reviewing" => from is "executing",
            "paused" => from is "understanding" or "executing" or "replanning" or "awaiting_approval" or "awaiting_delegate" or "awaiting_user" or "reviewing",
            "completed" => from is "executing" or "reviewing",
            _ => false
        };
    }
}
