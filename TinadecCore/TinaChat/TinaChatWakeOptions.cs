namespace TinadecCore.TinaChat;

/// <summary>Host knobs for draining the durable TinaChat wake queue.</summary>
public sealed class TinaChatWakeOptions
{
    public const string SectionName = "TinadecTinaChat";

    public bool WakeDrainEnabled { get; set; } = true;
    public int WakeIntervalSeconds { get; set; } = 5;
    public int WakeBatchSize { get; set; } = 5;

    /// <summary>
    /// Turns run concurrently within one drain pass. A member turn is a model call of several
    /// seconds; draining them one by one made the whole organization wait on its slowest member.
    /// </summary>
    public int WakeParallelism { get; set; } = 4;

    /// <summary>Woken turns one standing member may take per hour. Over it, the wake is postponed to the next window, never dropped.</summary>
    public int MemberTurnsPerHour { get; set; } = 12;

    /// <summary>Woken turns the whole organization (one session) may take per hour — the backstop against members waking each other.</summary>
    public int OrganizationTurnsPerHour { get; set; } = 60;

    /// <summary>Model rounds one member turn may take (each round may call tools).</summary>
    public int MemberTurnRounds { get; set; } = 6;
}
