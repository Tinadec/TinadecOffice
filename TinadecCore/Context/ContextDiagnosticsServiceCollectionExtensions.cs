using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Context;

/// <summary>
/// DI registration helpers for the P1 Context Overlap Auditor.
/// </summary>
public static class ContextDiagnosticsServiceCollectionExtensions
{
    public static IServiceCollection AddContextOverlapAuditor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IContextOverlapAuditor, ContextOverlapAuditor>();
        return services;
    }
}
