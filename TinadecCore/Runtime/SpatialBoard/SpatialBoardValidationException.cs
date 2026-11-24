namespace TinadecCore.Runtime.SpatialBoard;

/// <summary>
/// Thrown when a spatial board operation violates kind-specific schema constraints
/// (for example, passing task payload arrays in <c>props</c> on a <c>kind = "board"</c> projection node).
/// </summary>
public sealed class SpatialBoardValidationException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}
