using Microsoft.Extensions.Options;
using TinadecCore.AspNetCore;
using TinadecCore.Persistence;
using TinadecCore.Runtime;

var builder = WebApplication.CreateBuilder(args);

// Shared database abstraction (SQLite default; PostgreSQL optional) before business modules.
builder.Services.AddTinadecPersistence(builder.Configuration, builder.Environment.ContentRootPath);

// Register all TinadecCore modules, then the mountable HTTP layer.
builder.Services.AddTinadecCore();
builder.Services.AddTinadecCoreHttp();

builder.Services.AddOpenApi();

var app = builder.Build();

// RFC 9457 + snake_case ProblemDetails with trace_id/code extension.
app.UseTinadecCoreExceptionHandler();
app.UseStatusCodePages();

// Core-internal OpenAPI is the source of truth; Gateway generates its own external OpenAPI.
app.MapOpenApi("/openapi/core.json").WithSummary("TinadecCore internal OpenAPI");

// SQLite migrates at local startup. PostgreSQL only does so when explicitly configured.
using (var scope = app.Services.CreateScope())
{
    var storageOptions = scope.ServiceProvider.GetRequiredService<IOptions<TinadecPersistenceOptions>>().Value;
    var connection = scope.ServiceProvider.GetRequiredService<IDatabaseConnectionInfo>();
    if (storageOptions.Enabled && connection.IsConfigured)
    {
        await scope.ServiceProvider.GetRequiredService<IStorageMigrationRunner>().RunAsync();
        await scope.ServiceProvider.GetRequiredService<TinadecCore.Lifecycle.StorageLifecycleService>().ReconcileAsync();
    }
}

// Idempotent dev bootstrap: chat route + planner/executor agents when missing.
using (var seedScope = app.Services.CreateScope())
{
    await TinadecCore.Runtime.DevSeed.SeedIfMissingAsync(seedScope.ServiceProvider, CancellationToken.None);
}

// Complete /api/v1 route surface lives in the packable TinadecCore.AspNetCore layer.
app.MapTinadecCore();

app.Run();

/// <summary>
/// Exposed for integration test hosting (WebApplicationFactory).
/// </summary>
public partial class Program;
