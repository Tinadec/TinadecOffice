using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Models;
using TinadecCore.Persistence;

namespace TinadecCore.Runtime;

/// <summary>
/// One row of the unified readiness receipt (plan §5.3 item 1). <see cref="Status"/>
/// is one of <c>ready | degraded | blocked | unavailable</c>:
/// <list type="bullet">
/// <item><c>blocked</c> — configuration is missing/wrong; the flow cannot start until the user acts.</item>
/// <item><c>degraded</c> — configured, but the live check failed (e.g. probe call rejected).</item>
/// <item><c>unavailable</c> — the check itself could not run (tool process missing, probe not executed yet).</item>
/// </list>
/// </summary>
public sealed record ReadinessItem
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = "unavailable";
    /// <summary>
    /// Stable identifier of the reason (for example <c>secret_missing</c>). Clients word the reason and
    /// its action from this code and the facts in <see cref="Data"/>, so a localized UI never depends on
    /// server text; <see cref="Reason"/> and <see cref="Action"/> stay English diagnostics for logs and
    /// API callers.
    /// </summary>
    public string? ReasonCode { get; init; }
    public string? Reason { get; init; }
    public string? Action { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public object? Data { get; init; }
}

/// <summary>
/// Unified readiness receipt. Overall rule: any <c>blocked</c> item → <c>blocked</c>;
/// otherwise any <c>degraded</c>/<c>unavailable</c> → <c>degraded</c>; otherwise
/// <c>ready</c>. The single exception is <c>model_probe</c> <c>unavailable</c>
/// (probe not executed yet), which does not degrade the receipt.
/// </summary>
public sealed record ReadinessReceipt
{
    public string Status { get; init; } = "degraded";
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<ReadinessItem> Items { get; init; } = [];
}

/// <summary>
/// Aggregates the ten fixed readiness items: database, core_storage, agent_pack,
/// default_mode, model_provider, model_secret, model_route, model_probe,
/// tool_provider, manifest_hash. Every item carries an actionable <see cref="ReadinessItem.Action"/>
/// (a command or a model-center page hint) so a clean environment always shows the
/// next concrete step. Gateway layers its own items on top; Core never emits them.
/// </summary>
public sealed class ReadinessService
{
    private const string ModelProbeItemId = ModelProbeService.Id;

    private readonly IDatabaseReadiness _database;
    private readonly IDbContextFactory<AgentConfigurationDbContext> _agentConfig;
    private readonly GlobalAgentConfiguration _globalAgentConfig;
    private readonly IDbContextFactory<ModelControlDbContext> _models;
    private readonly IContentStore _content;
    private readonly ISecretStore _secrets;
    private readonly ITenantContextAccessor _tenant;
    private readonly IModelProvider _modelProvider;
    private readonly IToolProvider _toolProvider;
    private readonly ModelProbeService _modelProbe;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReadinessService>? _logger;
    private readonly object _toolGate = new();
    private (DateTimeOffset ExpiresAt, ReadinessItem ToolProvider, ReadinessItem ManifestHash)? _toolCache;

    public ReadinessService(
        IDatabaseReadiness database,
        IDbContextFactory<AgentConfigurationDbContext> agentConfig,
        GlobalAgentConfiguration globalAgentConfig,
        IDbContextFactory<ModelControlDbContext> models,
        IContentStore content,
        ISecretStore secrets,
        ITenantContextAccessor tenant,
        IModelProvider modelProvider,
        IToolProvider toolProvider,
        ModelProbeService modelProbe,
        IConfiguration configuration,
        ILogger<ReadinessService>? logger = null)
    {
        _database = database;
        _agentConfig = agentConfig;
        _globalAgentConfig = globalAgentConfig;
        _models = models;
        _content = content;
        _secrets = secrets;
        _tenant = tenant;
        _modelProvider = modelProvider;
        _toolProvider = toolProvider;
        _modelProbe = modelProbe;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ReadinessReceipt> GetReceiptAsync(CancellationToken cancellationToken = default)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var items = new List<ReadinessItem>(10)
        {
            await CheckDatabaseAsync(cancellationToken).ConfigureAwait(false),
            await CheckCoreStorageAsync(checkedAt, cancellationToken).ConfigureAwait(false),
        };
        items.AddRange(await CheckAgentPackAndDefaultModeAsync(checkedAt, cancellationToken).ConfigureAwait(false));
        items.AddRange(await CheckModelLayersAsync(checkedAt, cancellationToken).ConfigureAwait(false));
        items.Add(_modelProbe.Peek() ?? new ReadinessItem
        {
            Id = ModelProbeItemId,
            Status = "unavailable",
            ReasonCode = "probe_not_run",
            Reason = "The model probe has not run yet (results are cached for 60 s and need a new probe after they expire or the route changes).",
            Action = "POST /api/v1/model-probe?force=true sends one real 1-token request.",
            CheckedAt = checkedAt
        });
        items.AddRange(await CheckToolProviderAsync(checkedAt, cancellationToken).ConfigureAwait(false));

        var overall = items.Any(item => item.Status == "blocked") ? "blocked"
            : items.Any(item => item.Status is "degraded" or "unavailable"
                && !(item.Id == ModelProbeItemId && item.Status == "unavailable")) ? "degraded"
            : "ready";
        return new ReadinessReceipt { Status = overall, CheckedAt = checkedAt, Items = items };
    }

    // ──────────────────────────────────────────────────────────
    // database — physical connectivity probe (SELECT 1).
    // ──────────────────────────────────────────────────────────
    private async Task<ReadinessItem> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        var probe = await _database.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var ready = probe.State == DatabaseReadinessState.Ready;
        return new ReadinessItem
        {
            Id = "database",
            Status = ready ? "ready" : "blocked",
            ReasonCode = ready ? "database_ready" : "database_unavailable",
            Reason = ready ? $"Database reachable ({probe.Detail})." : (probe.Detail ?? $"Database state {probe.StateName}."),
            Action = ready ? null : "Check the TinadecPersistence connection settings (data/tinadec.db or the PostgreSQL connection string) and restart Core.",
            Data = new { provider = probe.Provider, state = probe.StateName, detail = probe.Detail }
        };
    }

    // ──────────────────────────────────────────────────────────
    // core_storage — real content-store round trip (put → read → delete).
    // Covers migration/schema health of the content pipeline that every
    // provider config and pack manifest depends on.
    // ──────────────────────────────────────────────────────────
    private async Task<ReadinessItem> CheckCoreStorageAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        const string probePayload = "tinadec-core-storage-readiness-probe";
        var tenant = _tenant.Current;
        try
        {
            await using var payload = new MemoryStream(Encoding.UTF8.GetBytes(probePayload));
            var reference = await _content.PutAsync(
                new ContentWriteRequest(tenant.TenantId, tenant.WorkspaceId, "readiness-probe", "text/plain", payload),
                cancellationToken).ConfigureAwait(false);
            try
            {
                await using var read = await _content.OpenReadAsync(reference, cancellationToken).ConfigureAwait(false);
                var roundTrip = await new StreamReader(read).ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                if (roundTrip != probePayload)
                {
                    throw new InvalidDataException("Content store round trip returned different bytes than written.");
                }
            }
            finally
            {
                try { await _content.DeleteAsync(reference, cancellationToken).ConfigureAwait(false); }
                catch (Exception cleanupEx) { _logger?.LogDebug(cleanupEx, "Readiness probe content cleanup failed."); }
            }
            return new ReadinessItem
            {
                Id = "core_storage",
                Status = "ready",
                ReasonCode = "storage_ready",
                Reason = "Content store write/read/delete round trip succeeded.",
                CheckedAt = checkedAt
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReadinessItem
            {
                Id = "core_storage",
                Status = "blocked",
                ReasonCode = "storage_failed",
                Reason = $"Core storage round trip failed: {ex.GetType().Name}: {ex.Message}",
                Action = "Check the storage directory and database schema (missing migrations or an unwritable disk cause this); reinitialize data/ if needed.",
                CheckedAt = checkedAt,
                Data = new { error = $"{ex.GetType().Name}: {ex.Message}" }
            };
        }
    }

    // ──────────────────────────────────────────────────────────
    // agent_pack + default_mode — pack installations fall back to the
    // bootstrap-seeded built-in agent directory.
    // ──────────────────────────────────────────────────────────
    private async Task<ReadinessItem[]> CheckAgentPackAndDefaultModeAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        IReadOnlyList<(string PackId, string Status, string? ActiveVersion)> packs;
        Guid? defaultModeVersionId;
        try
        {
            var tenant = _tenant.Current;
            packs = await _globalAgentConfig.ListPacksAsync(tenant.TenantId, tenant.WorkspaceId, cancellationToken).ConfigureAwait(false);
            defaultModeVersionId = await AgentConfigurationLookup.GetDefaultModeVersionIdAsync(
                _agentConfig, _globalAgentConfig, tenant.TenantId, tenant.WorkspaceId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            var reason = $"Agent directory query failed: {error}";
            const string action = "Check that database migrations completed; restarting Core reruns migrations and bootstrap.";
            return
            [
                new ReadinessItem { Id = "agent_pack", Status = "blocked", ReasonCode = "catalog_unreadable", Reason = reason, Action = action, CheckedAt = checkedAt, Data = new { error } },
                new ReadinessItem { Id = "default_mode", Status = "blocked", ReasonCode = "catalog_unreadable", Reason = reason, Action = action, CheckedAt = checkedAt, Data = new { error } }
            ];
        }

        var activePacks = packs.Where(item => item.Status == "active").ToArray();
        ReadinessItem agentPack;
        if (packs.Count > 0)
        {
            agentPack = new ReadinessItem
            {
                Id = "agent_pack",
                Status = activePacks.Length > 0 ? "ready" : "degraded",
                ReasonCode = activePacks.Length > 0 ? "packs_active" : "packs_inactive",
                Reason = activePacks.Length > 0
                    ? $"{packs.Count} agent pack(s) installed, {activePacks.Length} active."
                    : "Agent pack installations exist, but none is active.",
                Action = activePacks.Length > 0 ? null : "Reinstall or activate the pack on the agent pack page.",
                CheckedAt = checkedAt,
                Data = new
                {
                    source = "agent-pack",
                    count = packs.Count,
                    active = activePacks.Length,
                    packs = packs.Select(item => new { pack_id = item.PackId, status = item.Status, active_version = item.ActiveVersion })
                }
            };
        }
        else if (defaultModeVersionId is not null)
        {
            agentPack = new ReadinessItem
            {
                Id = "agent_pack",
                Status = "ready",
                ReasonCode = "builtin_directory",
                Reason = "No external agent pack is installed; bootstrap seeded the built-in agent directory.",
                CheckedAt = checkedAt,
                Data = new { source = "bootstrap", bootstrap_directory = _configuration["TinadecAgent:BootstrapDirectoryPath"] ?? "builtin" }
            };
        }
        else
        {
            agentPack = new ReadinessItem
            {
                Id = "agent_pack",
                Status = "blocked",
                ReasonCode = "no_agents",
                Reason = "The workspace has no agent pack, and the built-in agent directory was not bootstrapped.",
                Action = "Restart Core to run DevSeed/BootstrapAgentDirectory, or install a pack on the agent pack page (POST /api/v1/agent-packs/install-preview, then install).",
                CheckedAt = checkedAt
            };
        }

        var hasDefaultMode = defaultModeVersionId is not null;
        var defaultMode = new ReadinessItem
        {
            Id = "default_mode",
            Status = hasDefaultMode ? "ready" : "blocked",
            ReasonCode = hasDefaultMode ? "default_mode_ready" : "default_mode_missing",
            Reason = hasDefaultMode
                ? "The workspace has a default agent mode."
                : "The workspace has no active default agent mode; creating conversations and submitting interactions will be refused.",
            Action = hasDefaultMode ? null : "Publish a mode in Agent Center and make it the workspace default, or install an agent pack that contains a mode.",
            CheckedAt = checkedAt,
            Data = hasDefaultMode ? new { mode_version_id = defaultModeVersionId } : null
        };
        return [agentPack, defaultMode];
    }

    // ──────────────────────────────────────────────────────────
    // model_provider / model_secret / model_route — the three cheap
    // configuration layers; the fourth (model_probe) is appended by the caller.
    // ──────────────────────────────────────────────────────────
    private async Task<ReadinessItem[]> CheckModelLayersAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        // model_route reuses the model module's own route resolution (plan: 复用 CheckReadinessAsync).
        var modelReadiness = await _modelProvider.CheckReadinessAsync(cancellationToken).ConfigureAwait(false);
        var route = new ReadinessItem
        {
            Id = "model_route",
            Status = modelReadiness.IsReady ? "ready" : "blocked",
            ReasonCode = modelReadiness.IsReady ? "route_ready" : "route_unavailable",
            Reason = modelReadiness.IsReady
                ? modelReadiness.StatusMessage
                : string.Join("; ", modelReadiness.Warnings.Count > 0 ? modelReadiness.Warnings : ["The chat route is unavailable."]),
            Action = modelReadiness.IsReady ? null : "Check the chat route (GET /api/v1/model-routes) and its provider binding in the model center; restarting Core in a clean environment seeds a chat route.",
            CheckedAt = checkedAt
        };

        var tenant = _tenant.Current;
        bool routeFound = false;
        ModelProviderRecord? provider = null;
        ModelProviderVersionRecord? providerVersion = null;
        string? configError = null;
        string? model = null;
        string? baseUrl = null;
        try
        {
            await using var db = await _models.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var routeRow = await db.Routes.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Purpose == "chat" && x.TenantId == tenant.TenantId
                    && x.WorkspaceId == tenant.WorkspaceId && x.DeletedAt == null, cancellationToken).ConfigureAwait(false);
            if (routeRow is not null)
            {
                routeFound = true;
                var routeVersion = await db.RouteVersions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == routeRow.CurrentVersionId, cancellationToken).ConfigureAwait(false);
                var candidate = routeVersion is null
                    ? null
                    : await db.RouteCandidates.AsNoTracking()
                        .Where(x => x.RouteVersionId == routeVersion.Id)
                        .OrderBy(x => x.Position)
                        .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (candidate is not null)
                {
                    provider = await db.Providers.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == candidate.ProviderInstanceId && x.DeletedAt == null, cancellationToken).ConfigureAwait(false);
                    if (provider?.CurrentVersionId is { } versionId)
                    {
                        providerVersion = await db.ProviderVersions.AsNoTracking()
                            .SingleOrDefaultAsync(x => x.Id == versionId, cancellationToken).ConfigureAwait(false);
                    }
                    model = candidate.Model;
                }
            }
            if (providerVersion is not null)
            {
                await using var stream = await _content.OpenReadAsync(
                    new ContentReference(providerVersion.ContentReference, providerVersion.ContentHash, providerVersion.ContentLength, "application/json"),
                    cancellationToken).ConfigureAwait(false);
                using var document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("base_url", out var baseUrlElement) && baseUrlElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    baseUrl = baseUrlElement.GetString();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            configError = ex.Message;
        }

        var providerItem = new ReadinessItem { Id = "model_provider", CheckedAt = checkedAt };
        var secretItem = new ReadinessItem { Id = "model_secret", CheckedAt = checkedAt };
        if (configError is not null)
        {
            providerItem = providerItem with { Status = "unavailable", ReasonCode = "provider_config_unreadable", Reason = $"Reading the provider configuration failed: {configError}", Action = "Check the Core log; recreate the provider in the model center if needed.", Data = new { error = configError } };
            secretItem = secretItem with { Status = "unavailable", ReasonCode = "secret_unchecked_config", Reason = "The provider configuration is unreadable, so the key was not checked.", Action = "Fix the provider configuration, then check the key again." };
        }
        else if (!routeFound)
        {
            providerItem = providerItem with { Status = "unavailable", ReasonCode = "provider_no_route", Reason = "No chat route exists, so no provider is assigned.", Action = "Restart Core so DevSeed creates a chat route, or bind a chat route in the model center." };
            secretItem = secretItem with { Status = "unavailable", ReasonCode = "secret_unchecked_no_route", Reason = "No chat route exists, so the provider key was not checked.", Action = providerItem.Action };
        }
        else if (provider is null)
        {
            providerItem = providerItem with { Status = "blocked", ReasonCode = "provider_missing", Reason = "The provider referenced by the chat route is missing or deleted.", Action = "Rebind the chat route to an existing provider in the model center." };
            secretItem = secretItem with { Status = "unavailable", ReasonCode = "secret_unchecked_provider_missing", Reason = "The provider is missing, so the key was not checked.", Action = providerItem.Action };
        }
        else if (!provider.Enabled)
        {
            providerItem = providerItem with
            {
                Status = "blocked",
                ReasonCode = "provider_disabled",
                Reason = $"Chat provider '{provider.DisplayName}' is disabled.",
                Action = "Enable the provider in the model center (provider list, then Enable).",
                Data = ProviderData()
            };
            secretItem = secretItem with { Status = "unavailable", ReasonCode = "secret_unchecked_provider_disabled", Reason = "The provider is disabled, so the key was not checked.", Action = providerItem.Action, Data = ProviderData() };
        }
        else if (providerVersion is null)
        {
            providerItem = providerItem with
            {
                Status = "blocked",
                ReasonCode = "provider_version_missing",
                Reason = "The current provider version is missing (its configuration content is unavailable).",
                Action = "Save the provider configuration (base_url/model) again in the model center to create a new version.",
                Data = ProviderData()
            };
            secretItem = secretItem with { Status = "unavailable", ReasonCode = "secret_unchecked_provider_version", Reason = "The provider version is missing, so the key was not checked.", Action = providerItem.Action, Data = ProviderData() };
        }
        else if (string.IsNullOrWhiteSpace(baseUrl))
        {
            providerItem = providerItem with
            {
                Status = "blocked",
                ReasonCode = "provider_base_url_missing",
                Reason = "The provider configuration has no base_url.",
                Action = "Add a base_url for the provider in the model center (OpenAI-compatible endpoints usually end with /v1).",
                Data = ProviderData()
            };
            secretItem = secretItem with { Status = "unavailable", ReasonCode = "secret_unchecked_base_url", Reason = "base_url is missing, so the key was not checked.", Action = providerItem.Action, Data = ProviderData() };
        }
        else
        {
            providerItem = providerItem with
            {
                Status = "ready",
                ReasonCode = "provider_ready",
                Reason = $"Chat provider '{provider.DisplayName}' is enabled (driver {provider.Driver}, model {model ?? provider.Driver}).",
                Data = ProviderData()
            };

            if (string.IsNullOrWhiteSpace(provider.SecretReference))
            {
                secretItem = secretItem with
                {
                    Status = "blocked",
                    ReasonCode = "secret_missing",
                    Reason = "The provider has no API key yet.",
                    Action = $"Enter an API key for provider {provider.DisplayName} in the model center (provider, Edit, save the API key), or let the e2e script enter it.",
                    Data = new { provider_instance_id = provider.Id, provider_display_name = provider.DisplayName, has_key = false }
                };
            }
            else
            {
                var key = await _secrets.GetAsync(provider.SecretReference, cancellationToken).ConfigureAwait(false);
                secretItem = secretItem with
                {
                    Status = string.IsNullOrWhiteSpace(key) ? "blocked" : "ready",
                    ReasonCode = string.IsNullOrWhiteSpace(key) ? "secret_unreadable" : "secret_ready",
                    Reason = string.IsNullOrWhiteSpace(key)
                        ? "The provider has a key reference, but SecretStore holds no valid key."
                        : "The provider API key is stored in SecretStore.",
                    Action = string.IsNullOrWhiteSpace(key)
                        ? $"Enter the API key for provider {provider.DisplayName} again in the model center."
                        : null,
                    Data = new { provider_instance_id = provider.Id, provider_display_name = provider.DisplayName, has_key = !string.IsNullOrWhiteSpace(key) }
                };
            }
        }

        return [providerItem, secretItem, route];

        object? ProviderData() => new
        {
            provider_instance_id = provider!.Id,
            provider_display_name = provider.DisplayName,
            driver = provider.Driver,
            model,
            base_url = baseUrl,
            enabled = provider.Enabled
        };
    }

    // ──────────────────────────────────────────────────────────
    // tool_provider + manifest_hash — reuse the tool-layer manifest fetch.
    // A missing/unstartable TinadecTools process is unavailable (not blocked):
    // chat runs fine without tools, but the result degrades the receipt and the
    // action points at the build command.
    // ──────────────────────────────────────────────────────────
    private async Task<ReadinessItem[]> CheckToolProviderAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        lock (_toolGate)
        {
            if (_toolCache is { } cache && cache.ExpiresAt > checkedAt)
            {
                return [cache.ToolProvider, cache.ManifestHash];
            }
        }

        ReadinessItem toolProvider;
        ReadinessItem manifestHash;
        if (ProbeExecutablePath(_configuration) is null)
        {
            // Name the one command that fixes it, and say that `npm run dev` already
            // runs it — a bare "build the tools project" left a developer guessing
            // which of the three symptoms (zero tools / TOOL_MANIFEST_UNAVAILABLE /
            // process_exit) they were looking at.
            const string action = "Run npm run dev:tools (or dotnet build TinadecTools/TinadecTools); npm run dev builds the tool process first. TinadecTools:ExecutablePath can also name a built executable.";
            toolProvider = new ReadinessItem
            {
                Id = "tool_provider",
                Status = "unavailable",
                ReasonCode = "tools_not_built",
                Reason = "The TinadecTools executable was not found (nothing under TinadecTools/bin/{Debug|Release}/net10.0/).",
                Action = action,
                CheckedAt = checkedAt
            };
            manifestHash = new ReadinessItem
            {
                Id = "manifest_hash",
                Status = "unavailable",
                ReasonCode = "manifest_unavailable",
                Reason = "TinadecTools is unavailable, so the tool manifest hash is unknown.",
                Action = action,
                CheckedAt = checkedAt
            };
        }
        else
        {
            var workspaceRoot = _configuration["TinadecTools:DefaultWorkspaceRoot"];
            if (string.IsNullOrWhiteSpace(workspaceRoot)) workspaceRoot = Directory.GetCurrentDirectory();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
                var manifest = await _toolProvider.GetManifestAsync(workspaceRoot, timeoutCts.Token).ConfigureAwait(false);
                toolProvider = new ReadinessItem
                {
                    Id = "tool_provider",
                    Status = "ready",
                    ReasonCode = "tools_ready",
                    Reason = $"The TinadecTools process is available; its manifest declares {manifest.Tools.Count} tools.",
                    CheckedAt = checkedAt,
                    Data = new { workspace_root = workspaceRoot, tool_count = manifest.Tools.Count, protocol_version = manifest.ProtocolVersion }
                };
                manifestHash = new ReadinessItem
                {
                    Id = "manifest_hash",
                    Status = string.IsNullOrWhiteSpace(manifest.ManifestHash) ? "degraded" : "ready",
                    ReasonCode = string.IsNullOrWhiteSpace(manifest.ManifestHash) ? "manifest_hash_missing" : "manifest_ready",
                    Reason = string.IsNullOrWhiteSpace(manifest.ManifestHash)
                        ? "The TinadecTools manifest carries no manifest_hash; freezing and mismatch detection degrade."
                        : "The tool manifest hash was obtained.",
                    CheckedAt = checkedAt,
                    Data = string.IsNullOrWhiteSpace(manifest.ManifestHash) ? null : new { manifest_hash = manifest.ManifestHash }
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var error = $"{ex.GetType().Name}: {ex.Message}";
                toolProvider = new ReadinessItem
                {
                    Id = "tool_provider",
                    Status = "unavailable",
                    ReasonCode = "tools_handshake_failed",
                    Reason = $"TinadecTools manifest handshake failed: {error}",
                    Action = "Build TinadecTools/TinadecTools and restart Core; if it keeps failing, check TinadecTools:DefaultWorkspaceRoot.",
                    CheckedAt = checkedAt,
                    Data = new { error }
                };
                manifestHash = new ReadinessItem
                {
                    Id = "manifest_hash",
                    Status = "unavailable",
                    ReasonCode = "manifest_unavailable",
                    Reason = "The tool process is unavailable, so the manifest hash is unknown.",
                    Action = toolProvider.Action,
                    CheckedAt = checkedAt
                };
            }
        }

        lock (_toolGate)
        {
            _toolCache = (checkedAt.AddSeconds(10), toolProvider, manifestHash);
        }
        return [toolProvider, manifestHash];
    }

    /// <summary>
    /// Mirrors <see cref="Tools.TinadecToolsProcessManager"/> executable probing so
    /// readiness can report a missing build without spawning a process.
    /// </summary>
    private static string? ProbeExecutablePath(IConfiguration configuration)
    {
        var configured = configuration["TinadecTools:ExecutablePath"];
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;

        var executableName = OperatingSystem.IsWindows() ? "TinadecTools.exe" : "TinadecTools";
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, executableName) };
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var toolsDirectory = Path.Combine(directory.FullName, "TinadecTools");
            if (Directory.Exists(toolsDirectory))
            {
                foreach (var build in new[] { "Debug", "Release" })
                {
                    candidates.Add(Path.Combine(toolsDirectory, "bin", build, "net10.0", executableName));
                }
            }
            directory = directory.Parent;
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
