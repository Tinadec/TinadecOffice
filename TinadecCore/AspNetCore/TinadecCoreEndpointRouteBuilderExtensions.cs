using TinadecCore.AspNetCore.Endpoints;

namespace TinadecCore.AspNetCore;

/// <summary>
/// Mounts the complete TinadecCore <c>/api/v1</c> route surface onto any
/// ASP.NET Core endpoint route builder. The route surface is identical to the
/// standalone Api host, so embedders get the same contract without rebuilding it.
/// </summary>
public static class TinadecCoreEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapTinadecCore(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapCoreDiagnosticsEndpoints();
        endpoints.MapStorageEndpoints();
        endpoints.MapDmaeaEndpoints();
        endpoints.MapAgentConfigurationEndpoints();
        endpoints.MapModelAgentControlEndpoints();
        endpoints.MapAgentPackEndpoints();
        endpoints.MapInteractionsEndpoints();
        endpoints.MapControlPlaneEndpoints();
        endpoints.MapGovernanceEndpoints();
        endpoints.MapMemoryReviewEndpoints();
        endpoints.MapEvolutionEndpoints();
        endpoints.MapWorkspaceSnapshotEndpoints();
        endpoints.MapUserToolActionEndpoints();
        endpoints.MapStubEndpoints();
        return endpoints;
    }
}
