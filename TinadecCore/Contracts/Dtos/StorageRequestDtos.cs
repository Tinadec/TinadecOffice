namespace TinadecCore.Contracts.Dtos;

/// <summary>HTTP input contracts for the Core-owned project and session storage surface.</summary>
public sealed class CreateProjectRequest
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class UpdateProjectRequest
{
    public string? Name { get; set; }
}

public sealed class CreateSessionRequest
{
    public string? ProjectId { get; set; }
    public string? Title { get; set; }
    public Guid? ModeVersionId { get; set; }
    public MeetingModelOverrideDto? MeetingModelOverride { get; set; }
}

public sealed class UpdateSessionRequest
{
    public string? Title { get; set; }
    public Guid? ModeVersionId { get; set; }
    public MeetingModelOverrideDto? MeetingModelOverride { get; set; }
}

public sealed class MigrateSessionRequest
{
    public string? TargetProjectId { get; set; }
    public string? ProjectName { get; set; }
    public string? ProjectPath { get; set; }
}

public sealed class CreateMessageRequest
{
    public string Content { get; set; } = string.Empty;
}
