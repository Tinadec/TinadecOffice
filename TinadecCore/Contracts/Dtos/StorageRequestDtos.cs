namespace TinadecCore.Contracts.Dtos;

/// <summary>HTTP input contracts for the Core-owned project and session storage surface.</summary>
public sealed class CreateProjectRequest
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class CreateSessionRequest
{
    public string ProjectId { get; set; } = string.Empty;
    public string? Title { get; set; }
}

public sealed class UpdateSessionRequest
{
    public string Title { get; set; } = string.Empty;
}

public sealed class CreateMessageRequest
{
    public string Content { get; set; } = string.Empty;
}
