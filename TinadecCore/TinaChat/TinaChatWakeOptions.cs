namespace TinadecCore.TinaChat;

/// <summary>Host knobs for draining the durable TinaChat wake queue.</summary>
public sealed class TinaChatWakeOptions
{
    public const string SectionName = "TinadecTinaChat";

    public bool WakeDrainEnabled { get; set; } = true;
    public int WakeIntervalSeconds { get; set; } = 5;
    public int WakeBatchSize { get; set; } = 5;
}
