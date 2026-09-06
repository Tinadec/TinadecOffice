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
    private readonly IDbContextFactory<ModelControlDbContext> _models;
    private readonly IContentStore _content;
    private readonly ISecretStore _secrets;
    private readonly ITenantContextAccessor _tenant;
    private readonly IModelProvider _modelProvider;
    private readonly AgentPackService _agentPacks;
    private readonly IToolProvider _toolProvider;
    private readonly ModelProbeService _modelProbe;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReadinessService>? _logger;
    private readonly object _toolGate = new();
    private (DateTimeOffset ExpiresAt, ReadinessItem ToolProvider, ReadinessItem ManifestHash)? _toolCache;

    public ReadinessService(
        IDatabaseReadiness database,
        IDbContextFactory<AgentConfigurationDbContext> agentConfig,
        IDbContextFactory<ModelControlDbContext> models,
        IContentStore content,
        ISecretStore secrets,
        ITenantContextAccessor tenant,
        IModelProvider modelProvider,
        AgentPackService agentPacks,
        IToolProvider toolProvider,
        ModelProbeService modelProbe,
        IConfiguration configuration,
        ILogger<ReadinessService>? logger = null)
    {
        _database = database;
        _agentConfig = agentConfig;
        _models = models;
        _content = content;
        _secrets = secrets;
        _tenant = tenant;
        _modelProvider = modelProvider;
        _agentPacks = agentPacks;
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
            Reason = "模型探针尚未执行（结果缓存 60s，过期或路由变更后需重新探测）。",
            Action = "POST /api/v1/model-probe?force=true 触发一次真实 1-token 请求。",
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
            Reason = ready ? $"数据库可达（{probe.Detail}）" : (probe.Detail ?? $"数据库状态 {probe.StateName}。"),
            Action = ready ? null : "检查 TinadecPersistence 连接配置（data/tinadec.db 或 PostgreSQL 连接串）并重启 Core。",
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
                Reason = "Content store 写/读/删 round trip 成功。",
                CheckedAt = checkedAt
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReadinessItem
            {
                Id = "core_storage",
                Status = "blocked",
                Reason = $"Core 存储 round trip 失败：{ex.GetType().Name}: {ex.Message}",
                Action = "检查存储目录/数据库 schema（迁移未跑或磁盘不可写时会出现该错误），必要时删除 data/ 重新初始化。",
                CheckedAt = checkedAt
            };
        }
    }

    // ──────────────────────────────────────────────────────────
    // agent_pack + default_mode — pack installations fall back to the
    // bootstrap-seeded built-in agent directory.
    // ──────────────────────────────────────────────────────────
    private async Task<ReadinessItem[]> CheckAgentPackAndDefaultModeAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentPackInstallationView> packs;
        WorkspaceDefaultsRecord? defaults;
        try
        {
            packs = await _agentPacks.ListAsync(cancellationToken).ConfigureAwait(false);
            await using var db = await _agentConfig.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var tenant = _tenant.Current;
            // SQLite cannot translate DateTimeOffset ORDER BY; the per-tenant row
            // count is tiny, so order client-side instead.
            var defaultRows = await db.WorkspaceDefaults.AsNoTracking()
                .Where(item => item.TenantId == tenant.TenantId && item.WorkspaceId == tenant.WorkspaceId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            defaults = defaultRows.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = $"Agent 目录查询失败：{ex.GetType().Name}: {ex.Message}";
            return
            [
                new ReadinessItem { Id = "agent_pack", Status = "blocked", Reason = reason, Action = "检查数据库迁移是否完成；重启 Core 会重跑迁移与 bootstrap。", CheckedAt = checkedAt },
                new ReadinessItem { Id = "default_mode", Status = "blocked", Reason = reason, Action = "检查数据库迁移是否完成；重启 Core 会重跑迁移与 bootstrap。", CheckedAt = checkedAt }
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
                Reason = activePacks.Length > 0
                    ? $"已安装 {packs.Count} 个 Agent Pack（{activePacks.Length} 个 active）。"
                    : "存在 Agent Pack 安装记录但没有 active 版本。",
                Action = activePacks.Length > 0 ? null : "在 Agent Pack 页面重新安装/激活对应的 pack。",
                CheckedAt = checkedAt,
                Data = new
                {
                    source = "agent-pack",
                    packs = packs.Select(item => new { pack_id = item.PackId, status = item.Status, active_version = item.ActiveVersion })
                }
            };
        }
        else if (defaults is not null)
        {
            agentPack = new ReadinessItem
            {
                Id = "agent_pack",
                Status = "ready",
                Reason = "未安装外部 Agent Pack；内置 Agent 目录已由 bootstrap seed。",
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
                Reason = "工作区没有任何 Agent Pack，且内置 Agent 目录尚未 bootstrap。",
                Action = "重启 Core 触发 DevSeed/BootstrapAgentDirectory，或在 Agent Pack 页面安装 pack（POST /api/v1/agent-packs/install-preview → install）。",
                CheckedAt = checkedAt
            };
        }

        var hasDefaultMode = defaults is { ArchivedAt: null, DefaultModeVersionId: not null };
        var defaultMode = new ReadinessItem
        {
            Id = "default_mode",
            Status = hasDefaultMode ? "ready" : "blocked",
            Reason = hasDefaultMode
                ? "工作区已设置默认 Agent Mode。"
                : "工作区没有 active 的默认 Agent Mode；会话创建与交互提交会被拒绝。",
            Action = hasDefaultMode ? null : "在 Agent Center 发布一个模式并设为默认（workspace-defaults），或安装包含 mode 的 Agent Pack。",
            CheckedAt = checkedAt,
            Data = hasDefaultMode ? new { mode_version_id = defaults!.DefaultModeVersionId } : null
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
            Reason = modelReadiness.IsReady
                ? modelReadiness.StatusMessage
                : string.Join("；", modelReadiness.Warnings.Count > 0 ? modelReadiness.Warnings : ["chat 路由不可用。"]),
            Action = modelReadiness.IsReady ? null : "在模型中心检查 chat 路由（GET /api/v1/model-routes）与 provider 绑定；干净环境重启 Core 会自动 seed 一条 chat 路由。",
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
            providerItem = providerItem with { Status = "unavailable", Reason = $"provider 配置读取失败：{configError}", Action = "查看 Core 日志；必要时在模型中心重建 provider。" };
            secretItem = secretItem with { Status = "unavailable", Reason = "provider 配置不可读，无法检查密钥。", Action = "修复 provider 配置后再检查密钥。" };
        }
        else if (!routeFound)
        {
            providerItem = providerItem with { Status = "unavailable", Reason = "chat 路由不存在，无法归属 provider。", Action = "重启 Core 让 DevSeed 自动创建 chat 路由，或在模型中心绑定一条 chat 路由。" };
            secretItem = secretItem with { Status = "unavailable", Reason = "chat 路由不存在，无法检查 provider 密钥。", Action = providerItem.Action };
        }
        else if (provider is null)
        {
            providerItem = providerItem with { Status = "blocked", Reason = "chat 路由引用的 provider 实例缺失或已删除。", Action = "在模型中心重新绑定 chat 路由到一个存在的 provider。" };
            secretItem = secretItem with { Status = "unavailable", Reason = "provider 缺失，无法检查密钥。", Action = providerItem.Action };
        }
        else if (!provider.Enabled)
        {
            providerItem = providerItem with
            {
                Status = "blocked",
                Reason = $"chat provider '{provider.DisplayName}' 已停用。",
                Action = "在模型中心启用该 provider（模型中心 → provider 列表 → 启用）。",
                Data = ProviderData()
            };
            secretItem = secretItem with { Status = "unavailable", Reason = "provider 已停用，密钥状态未检查。", Action = providerItem.Action };
        }
        else if (providerVersion is null)
        {
            providerItem = providerItem with
            {
                Status = "blocked",
                Reason = "provider 当前版本缺失（配置内容不可用）。",
                Action = "在模型中心重新保存 provider 配置（base_url/model），生成新的版本。",
                Data = ProviderData()
            };
            secretItem = secretItem with { Status = "unavailable", Reason = "provider 版本缺失，密钥状态未检查。", Action = providerItem.Action };
        }
        else if (string.IsNullOrWhiteSpace(baseUrl))
        {
            providerItem = providerItem with
            {
                Status = "blocked",
                Reason = "provider 配置缺少 base_url。",
                Action = "在模型中心为 provider 补充 base_url（OpenAI 兼容端点通常以 /v1 结尾）。",
                Data = ProviderData()
            };
            secretItem = secretItem with { Status = "unavailable", Reason = "base_url 缺失，密钥状态未检查。", Action = providerItem.Action };
        }
        else
        {
            providerItem = providerItem with
            {
                Status = "ready",
                Reason = $"chat provider '{provider.DisplayName}' 已启用（driver {provider.Driver}，model {model ?? provider.Driver}）。",
                Data = ProviderData()
            };

            if (string.IsNullOrWhiteSpace(provider.SecretReference))
            {
                secretItem = secretItem with
                {
                    Status = "blocked",
                    Reason = "provider 尚未录入 API key。",
                    Action = $"在模型中心为 provider {provider.DisplayName} 录入 API key（模型中心 → provider → 编辑 → 保存 API key），或运行 e2e 脚本自动录入。",
                    Data = new { provider_instance_id = provider.Id, has_key = false }
                };
            }
            else
            {
                var key = await _secrets.GetAsync(provider.SecretReference, cancellationToken).ConfigureAwait(false);
                secretItem = secretItem with
                {
                    Status = string.IsNullOrWhiteSpace(key) ? "blocked" : "ready",
                    Reason = string.IsNullOrWhiteSpace(key)
                        ? "provider 已配置密钥引用，但 SecretStore 中读不到有效 key。"
                        : "provider API key 已存入 SecretStore。",
                    Action = string.IsNullOrWhiteSpace(key)
                        ? $"在模型中心为 provider {provider.DisplayName} 重新录入 API key。"
                        : null,
                    Data = new { provider_instance_id = provider.Id, has_key = !string.IsNullOrWhiteSpace(key) }
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
            const string action = "dotnet build TinadecTools/TinadecTools（或在配置 TinadecTools:ExecutablePath 指定已构建的可执行文件）。";
            toolProvider = new ReadinessItem
            {
                Id = "tool_provider",
                Status = "unavailable",
                Reason = "未找到 TinadecTools 可执行文件（TinadecTools/bin/{Debug|Release}/net10.0/ 下不存在）。",
                Action = action,
                CheckedAt = checkedAt
            };
            manifestHash = new ReadinessItem
            {
                Id = "manifest_hash",
                Status = "unavailable",
                Reason = "TinadecTools 不可用，无法获取工具 manifest hash。",
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
                    Reason = $"TinadecTools 进程可用，manifest 声明 {manifest.Tools.Count} 个工具。",
                    CheckedAt = checkedAt,
                    Data = new { workspace_root = workspaceRoot, tool_count = manifest.Tools.Count, protocol_version = manifest.ProtocolVersion }
                };
                manifestHash = new ReadinessItem
                {
                    Id = "manifest_hash",
                    Status = string.IsNullOrWhiteSpace(manifest.ManifestHash) ? "degraded" : "ready",
                    Reason = string.IsNullOrWhiteSpace(manifest.ManifestHash)
                        ? "TinadecTools manifest 未携带 manifest_hash；冻结与失配检测将退化。"
                        : "工具 manifest hash 已取得。",
                    CheckedAt = checkedAt,
                    Data = string.IsNullOrWhiteSpace(manifest.ManifestHash) ? null : new { manifest_hash = manifest.ManifestHash }
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var reason = $"TinadecTools manifest 握手失败：{ex.GetType().Name}: {ex.Message}";
                toolProvider = new ReadinessItem
                {
                    Id = "tool_provider",
                    Status = "unavailable",
                    Reason = reason,
                    Action = "dotnet build TinadecTools/TinadecTools 后重启 Core；若反复失败检查 TinadecTools:DefaultWorkspaceRoot。",
                    CheckedAt = checkedAt
                };
                manifestHash = new ReadinessItem
                {
                    Id = "manifest_hash",
                    Status = "unavailable",
                    Reason = "工具进程不可用，manifest hash 未知。",
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
