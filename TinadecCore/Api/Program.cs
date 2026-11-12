using Microsoft.Extensions.Options;
using TinadecCore.AspNetCore;
using TinadecCore.AspNetCore.Auth;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using TinadecCore.Abstractions.Ports;

var builder = WebApplication.CreateBuilder(args);
var scopesEnabled = builder.Configuration.GetValue<bool?>("TinadecStorage:Enabled") ?? !builder.Environment.IsEnvironment("Testing");
var initialConfiguration = scopesEnabled && !File.Exists(Path.Combine(StorageScopePaths.UserRoot(builder.Configuration["TinadecStorage:UserRoot"]), "config", "agents.toml"));
// Bootstrap still fails fast: without a readable storage document there is no database to open, and
// guessing a default path could point the app at the wrong data. What changed is where the reason
// goes — this stage runs before the HTTP host exists, so /readiness cannot report it. The receipt is
// the only channel left for the desktop host to name the broken file.
var userRootForBootstrap = scopesEnabled
    ? StorageScopePaths.UserRoot(builder.Configuration["TinadecStorage:UserRoot"])
    : null;
IScopeStorageLocations? userScope = null;
if (scopesEnabled)
{
    try
    {
        userScope = StorageHostBootstrap.Configure(builder.Configuration);
    }
    catch (Exception error) when (error is ConfigurationDocumentException or InvalidDataException)
    {
        StartupFailureReceipt.Write(userRootForBootstrap!, DocumentFailureCode(error), "storage", error.Message);
        throw;
    }
}
if (userScope is not null) builder.Services.AddSingleton<IScopeStorageLocations>(userScope);

// Logging is a side channel. Console/Debug by default; the Windows Event Log
// provider is opt-in — without elevation its first Warning write throws when
// the event source does not exist, which must never influence run outcomes.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
if (userScope is not null)
{
    try
    {
        builder.Logging.AddProvider(new ScopeDiagnosticLoggerProvider(userScope));
    }
    catch (Exception error) when (error is ConfigurationDocumentException or InvalidDataException)
    {
        // logging.toml is read by the scope logger provider, also before the host exists.
        StartupFailureReceipt.Write(userRootForBootstrap!, DocumentFailureCode(error), "logging", error.Message);
        throw;
    }
    // Bootstrap completed: a receipt from an earlier failed start must not survive as current advice.
    StartupFailureReceipt.Clear(userRootForBootstrap!);
}
if (builder.Configuration.GetValue<bool>("Logging:EventLog:Enabled"))
{
    builder.Logging.AddEventLog();
}

// Shared database abstraction (SQLite default; PostgreSQL optional) before business modules.
builder.Services.AddTinadecPersistence(builder.Configuration, builder.Environment.ContentRootPath);
// Startup keeps running when a configuration document is invalid; the state is what /readiness reports.
builder.Services.AddSingleton<StartupConfigurationState>();

// Register all TinadecCore modules, then the mountable HTTP layer.
if (scopesEnabled) builder.Services.AddHostedService<StorageHostShutdownService>();
builder.Services.AddTinadecCore();
builder.Services.AddTinadecCoreHttp();
builder.Services.AddTinadecOidc(builder.Configuration);
if (scopesEnabled)
{
    builder.Services.AddTinadecConfigurationFiles();
    builder.Services.AddTinadecStorageScopes();
    builder.Services.AddTinadecSessionTransfers();
    builder.Services.AddSingleton<UserStorageHostLease>();
    builder.Services.AddSingleton<IProtectedStorageRoots>(sp => new HostProtectedRoots(sp.GetRequiredService<IStorageScopeRegistry>(), "user"));
}

builder.Services.AddOpenApi();

var app = builder.Build();
if (scopesEnabled) _ = app.Services.GetRequiredService<UserStorageHostLease>();

// RFC 9457 + snake_case ProblemDetails with trace_id/code extension.
app.UseTinadecCoreExceptionHandler();
app.UseStatusCodePages();
if (scopesEnabled) app.UseTinadecStorageScopes();

// Core-internal OpenAPI is the source of truth; Gateway generates its own external OpenAPI.
app.MapOpenApi("/openapi/core.json").WithSummary("TinadecCore internal OpenAPI");

// SQLite migrates at local startup. PostgreSQL only does so when explicitly configured.
// A configuration document that fails validation is recorded, not fatal: the process must still
// reach app.Run() so a client can read /api/v1/readiness and the per-scope availability rows that
// name the broken file. Anything else (no database, disk error) keeps failing fast as before.
var startupConfiguration = app.Services.GetRequiredService<StartupConfigurationState>();
using (var scope = app.Services.CreateScope())
{
    var storageOptions = scope.ServiceProvider.GetRequiredService<IOptions<TinadecPersistenceOptions>>().Value;
    var connection = scope.ServiceProvider.GetRequiredService<IDatabaseConnectionInfo>();
    if (storageOptions.Enabled && connection.IsConfigured)
    {
        var abandonedPurges = 0;
        try
        {
            await scope.ServiceProvider.GetRequiredService<IStorageMigrationRunner>().RunAsync();
            await scope.ServiceProvider.GetRequiredService<TinadecCore.Lifecycle.StorageLifecycleService>().ReconcileAsync();
            if (scopesEnabled) abandonedPurges = (await scope.ServiceProvider.GetRequiredService<ProjectSessionLifecycleService>().RecoverPendingPurgesAsync()).Count;
        }
        catch (Exception error) when (StartupConfigurationState.IsRecoverable(error))
        {
            startupConfiguration.Record("storage", error);
            app.Logger.LogError(error,
                "Storage startup continued with {Count} configuration diagnostic(s); see /api/v1/readiness.",
                startupConfiguration.Diagnostics.Count);
        }
        if (abandonedPurges > 0)
        {
            // An abandoned purge means a delete did not finish: say so in the receipt instead of leaving
            // it to look like a completed cleanup.
            startupConfiguration.Record("purge", "session_purge_abandoned",
                abandonedPurges + " interrupted session purge(s) were abandoned after a configuration error; their data is still on disk.");
            app.Logger.LogError("Abandoned {Count} session purge journal(s) after a configuration error.", abandonedPurges);
        }
    }
}

// Idempotent dev bootstrap: chat route + planner/executor agents when missing.
using (var seedScope = app.Services.CreateScope())
{
    try
    {
        if (!scopesEnabled || initialConfiguration)
            await TinadecCore.Runtime.DevSeed.SeedIfMissingAsync(seedScope.ServiceProvider, CancellationToken.None);
        if (scopesEnabled)
            await seedScope.ServiceProvider.GetRequiredService<IConfigurationProjectionCoordinator>().ReconcileAsync();
    }
    catch (Exception error) when (StartupConfigurationState.IsRecoverable(error))
    {
        startupConfiguration.Record("projection", error);
        app.Logger.LogError(error,
            "Configuration projection attach was skipped at startup; requests will report the document diagnostics.");
    }
}

// Complete /api/v1 route surface lives in the packable TinadecCore.AspNetCore layer.
app.MapTinadecCore();

app.Run();

static string DocumentFailureCode(Exception error)
    => error is ConfigurationDocumentException document ? document.Code : "configuration_invalid";

/// <summary>
/// Exposed for integration test hosting (WebApplicationFactory).
/// </summary>
public partial class Program;
