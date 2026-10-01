namespace TinadecCore.Contracts.Dtos;

/// <summary>A standing approval a person gave (todo E7). </summary>
public sealed record ApprovalRuleDto(
    Guid Id,
    string Kind,
    string ToolId,
    string? Pattern,
    Guid? SessionId,
    Guid CreatedByPrincipalId,
    DateTimeOffset CreatedAt,
    int UseCount);

public sealed record ApprovalRuleCreateRequest(string Kind, string ToolId, string? Pattern = null, Guid? SessionId = null);
