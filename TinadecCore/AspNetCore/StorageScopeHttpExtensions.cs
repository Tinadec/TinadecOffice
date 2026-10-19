using TinadecCore.Abstractions.Ports;
using TinadecCore.Runtime;

namespace TinadecCore.AspNetCore;

public static class StorageScopeHttpExtensions
{
    public const string StorageHeader = "X-Tinadec-Storage-Id";

    public static IServiceCollection AddTinadecStorageScopes(this IServiceCollection services)
    {
        services.AddSingleton<IStorageScopeRegistry>(sp => new StorageScopeRegistry(sp, sp.GetRequiredService<IConfiguration>(),
            scopedServices => scopedServices.AddTinadecCoreHttp()));
        services.AddSingleton<IWorkspaceRegistry>(sp => (IWorkspaceRegistry)sp.GetRequiredService<IStorageScopeRegistry>());
        services.AddSingleton<StorageMaintenanceService>();
        services.AddSingleton<ContentCollectionService>();
        services.AddHostedService<StorageScopeRecoveryWorker>();
        return services;
    }

    public static IApplicationBuilder UseTinadecStorageScopes(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        // Every mounted API request is a host operation. Directory restrictions alone
        // cannot stop an Agent shell from calling loopback HTTP to read protected data,
        // approve its own tool action or mutate another registered project's facts.
        if (context.Request.Path.StartsWithSegments("/api/v1")
            && !(HttpMethods.IsGet(context.Request.Method) && context.Request.Path is var publicPath
                && (publicPath == "/api/v1/health" || publicPath == "/api/v1/host-challenge")))
        {
            var expected = Environment.GetEnvironmentVariable("TINADEC_HOST_CONTROL_TOKEN");
            var supplied = context.Request.Headers["X-Tinadec-Host-Control"].ToString();
            if (string.IsNullOrEmpty(expected) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(supplied)))
            { await ErrorAsync(context, 403, "host_authorization_required", "This API requires a trusted host request."); return; }
        }
        if (!context.Request.Path.StartsWithSegments("/api/v1") || context.Request.Path.StartsWithSegments("/api/v1/storage"))
        { await next(context).ConfigureAwait(false); return; }
        var registry = context.RequestServices.GetRequiredService<IStorageScopeRegistry>();
        var ids = context.Request.Headers[StorageHeader];
        if (ids.Count > 1) { await ErrorAsync(context, 400, "invalid_storage_scope", "Exactly one storage scope header is allowed."); return; }
        // Agent packs are installed once for the application. A project header must
        // never create a second, divergent pack installation inside that project.
        var id = context.Request.Path.StartsWithSegments("/api/v1/agent-packs")
            ? "user"
            : ids.Count == 0 ? "user" : ids.ToString();
        try
        {
            var runControl = context.Request.Path.StartsWithSegments("/api/v1/runs")
                || context.Request.Path.StartsWithSegments("/api/v1/approvals")
                || context.Request.Path.StartsWithSegments("/api/v1/governance");
            var newRunStream = context.Request.Path.StartsWithSegments("/api/v1/runs")
                && context.Request.Path.Value?.TrimEnd('/').EndsWith("/stream", StringComparison.OrdinalIgnoreCase) == true;
            // Late controls may drain an accepted run. A reconnect is a new long-lived
            // lease and must not keep a scope that is already closing alive indefinitely.
            await using var lease = await (runControl && !newRunStream && registry is StorageScopeRegistry owned
                ? owned.AcquireRunControlAsync(id, context.RequestAborted)
                : registry.AcquireAsync(id, context.RequestAborted)).ConfigureAwait(false);
            context.Response.Headers[StorageHeader] = lease.Descriptor.StorageId;
            var hostServices = context.RequestServices;
            await using var requestScope = lease.Services.CreateAsyncScope();
            context.RequestServices = requestScope.ServiceProvider;
            try
            {
                // A durable transfer freezes mutations at the source. Control inputs retain
                // their own admission guard so an already accepted run can finish draining.
                if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
                    || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
                {
                    var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
                    if (segments.Length >= 4 && segments[2] == "sessions" && Guid.TryParse(segments[3], out var sessionId)
                        && !(segments.Length >= 5 && segments[4] is "migrate" or "interactions"))
                        hostServices.GetRequiredService<ISessionScopeTransferService>().AssertSessionWritable(sessionId, lease.Descriptor.StorageId);
                }
                await next(context).ConfigureAwait(false);
            }
            finally { context.RequestServices = hostServices; }
        }
        catch (KeyNotFoundException ex) when (!context.Response.HasStarted) { await ErrorAsync(context, 404, "storage_scope_not_found", ex.Message); }
        catch (DirectoryNotFoundException ex) when (!context.Response.HasStarted) { await ErrorAsync(context, 409, "storage_scope_unavailable", ex.Message); }
    });

    /// <summary>
    /// One shape for every scope-level refusal: code, trace, the classification a client branches
    /// on, and the recovery actions it may offer. Previously a failing scope answered with a bare
    /// code, which is why "the folder is gone" reached the user as an unexplained conflict.
    /// </summary>
    internal static Task ErrorAsync(HttpContext context, int status, string code, string detail)
    {
        var classification = ErrorClassification.Classify(code, status);
        return Results.Problem(
            statusCode: status, title: code, detail: detail,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["trace_id"] = context.TraceIdentifier,
                ["category"] = classification.Category,
                ["retryable"] = classification.Retryable,
                ["actions"] = classification.Actions,
            }).ExecuteAsync(context);
    }
}
