namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Narrow configuration view consumed by context assembly. The DmaEA configuration
/// implementation owns parsing and hot reload; context stays independent from that module.
/// </summary>
public interface IRuntimeContextSettings
{
    RuntimeContextSettings Current { get; }
}

public sealed record RuntimeContextSettings(
    int DefaultTokenBudget,
    int RecentMessageLimit,
    int ReviewedMemoryLimit);
