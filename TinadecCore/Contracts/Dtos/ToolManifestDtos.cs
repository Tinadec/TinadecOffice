namespace TinadecCore.Contracts.Dtos;

public sealed class ToolRegistrySummaryDto
{
    public int DeclaredToolCount { get; init; }
    public int CanonicalToolCount { get; init; }
    public int DuplicateToolIdCount { get; init; }
    public IReadOnlyList<string> DuplicateToolIds { get; init; } = [];
    public IReadOnlyList<string> SourcePrecedence { get; init; } = [];
    public string SelectionPolicy { get; init; } = "first-source-wins";
}

public sealed class AgentLayerManifestDto
{
    public string Layer { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public int AgentCount { get; init; }
    public int EnabledAgentCount { get; init; }
    public int MaxParallelExecutors { get; init; }
    public bool WorktreeIsolation { get; init; }
    public bool ApprovalRequired { get; init; }
    public IReadOnlyList<string> AgentTypes { get; init; } = [];
    public IReadOnlyList<string> ToolIds { get; init; } = [];
}

public sealed class ToolProviderManifestDto
{
    public string Source { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Layer { get; init; } = string.Empty;
    public string Status { get; init; } = "active";
    public int ToolCount { get; init; }
    public int ActiveToolCount { get; init; }
    public int FutureToolCount { get; init; }
    public int ApprovalRequiredCount { get; init; }
    public int ReadOnlyCount { get; init; }
    public IReadOnlyList<string> CapabilityPrefixes { get; init; } = [];
}

public sealed class ToolRiskManifestDto
{
    public string Risk { get; init; } = string.Empty;
    public int ToolCount { get; init; }
    public bool RequiresHumanCheckpoint { get; init; }
    public string PolicySummary { get; init; } = string.Empty;
}

public sealed class ToolDescriptorDto
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Risk { get; init; } = "low";
    public bool RequiresApproval { get; init; }
    public string ExecuteEndpoint { get; init; } = string.Empty;
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}
