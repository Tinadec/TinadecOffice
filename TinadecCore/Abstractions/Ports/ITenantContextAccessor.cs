namespace TinadecCore.Abstractions.Ports;

/// <summary>Resolved actor and isolation boundary for a Core request.</summary>
public interface ITenantContextAccessor
{
    TenantContext Current { get; }
}

public sealed record TenantContext(
    Guid TenantId,
    Guid WorkspaceId,
    Guid PrincipalId,
    string Role,
    bool IsDevelopment = false);
