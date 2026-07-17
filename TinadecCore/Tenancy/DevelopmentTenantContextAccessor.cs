using Microsoft.Extensions.Options;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Tenancy;

internal sealed class DevelopmentTenantContextAccessor : ITenantContextAccessor
{
    public static readonly Guid TenantId = Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d001");
    public static readonly Guid WorkspaceId = Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d002");
    public static readonly Guid PrincipalId = Guid.Parse("c7d1e4f6-a251-4a4e-9a01-1a0f4b77d003");
    private readonly TenancyOptions _options;

    public DevelopmentTenantContextAccessor(IOptions<TenancyOptions> options) => _options = options.Value;

    public TenantContext Current => _options.EnableDevelopmentIdentity
        ? new TenantContext(TenantId, WorkspaceId, PrincipalId, "owner", true)
        : throw new InvalidOperationException("No verified tenant context is available. Configure an authenticated ITenantContextAccessor.");
}
