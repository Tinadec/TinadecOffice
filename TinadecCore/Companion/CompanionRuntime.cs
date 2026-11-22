using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Companion;

/// <summary>Hand-off between channel/message endpoints and the background run loop.</summary>
public interface ICompanionRuntime
{
    ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>Host knobs for draining due companion runs.</summary>
public sealed class CompanionRunOptions
{
    public bool DrainEnabled { get; set; } = true;
    public int PollIntervalMilliseconds { get; set; } = 5000;
    public int ClaimLeaseSeconds { get; set; } = 300;
    public int MaxAttempts { get; set; } = 5;
    public double BackoffBaseSeconds { get; set; } = 2;
}

/// <summary>
/// Executes queued companion runs under lease fencing directly on <see cref="CompanionRunRecord"/>,
/// streams live token deltas, tool call status, and avatar stage cues through
/// <see cref="ICompanionStreamBroker"/>, and hands off any <c>@Name</c> peer mentions in group
/// replies to <see cref="ICompanionChannel.OnMentionAsync"/> (governed by <c>max_bot_hop_depth</c>).
/// </summary>
public sealed class CompanionRuntime : BackgroundService, ICompanionRuntime
{
    internal const string DispatchActivityKind = "companion_dispatched";
    private const int MaxMemories = 6;
    private const int MaxContextMessages = 30;
    private const int MaxErrorMessage = 2048;
    private const int MaxBatch = 4;
    private const int MaxToolRounds = 6;
    private const int TaskPreviewLength = 80;

    private static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private const string DefaultPersona = "你是一个乐于助人的伙伴。";

    private readonly SemaphoreSlim _signal = new(0);
    private readonly IDbContextFactory<CompanionDbContext> _dbFactory;
    private readonly IAgentChatClientFactory _chatClients;
    private readonly ILogger<CompanionRuntime> _logger;
    private readonly CompanionRunOptions _options;
    private readonly ICompanionWorkGateway? _workGateway;
    private readonly ICompanionStreamBroker? _streamBroker;
    private readonly IServiceProvider? _services;
    private readonly IContextMemoryService? _contextMemory;

    public CompanionRuntime(
        IDbContextFactory<CompanionDbContext> dbFactory,
        IAgentChatClientFactory chatClients,
        ILogger<CompanionRuntime> logger,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
        CompanionRunOptions? options = null,
        ICompanionWorkGateway? workGateway = null,
        ICompanionStreamBroker? streamBroker = null,
        IServiceProvider? services = null,
        IContextMemoryService? contextMemory = null)
    {
        _dbFactory = dbFactory;
        _chatClients = chatClients;
        _logger = logger;
        _options = options ?? ReadOptions(configuration);
        _workGateway = workGateway;
        _streamBroker = streamBroker;
        _services = services;
        _contextMemory = contextMemory ?? services?.GetService<IContextMemoryService>();
    }

    private static CompanionRunOptions ReadOptions(Microsoft.Extensions.Configuration.IConfiguration? configuration)
    {
        var options = new CompanionRunOptions();
        if (configuration is null) return options;
        const string section = "TinadecCompanion:";
        if (bool.TryParse(configuration[section + "RunDrainEnabled"], out var drain)) options.DrainEnabled = drain;
        if (int.TryParse(configuration[section + "PollIntervalMilliseconds"], out var poll)) options.PollIntervalMilliseconds = poll;
        if (int.TryParse(configuration[section + "ClaimLeaseSeconds"], out var lease)) options.ClaimLeaseSeconds = lease;
        if (int.TryParse(configuration[section + "MaxAttempts"], out var attempts)) options.MaxAttempts = attempts;
        if (double.TryParse(configuration[section + "BackoffBaseSeconds"],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var backoff))
            options.BackoffBaseSeconds = backoff;
        return options;
    }

    internal sealed record RunClaim(Guid RunId, Guid TenantId, string Token, int Attempts);

    private sealed class RunAbortException(string code, string message, bool retryable) : Exception(message)
    {
        public string Code { get; } = code;
        public bool Retryable { get; } = retryable;
    }

    public async ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var nowMs = now.ToUnixTimeMilliseconds();
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await db.Runs
                .Where(x => x.Id == runId && x.Status == "queued" && x.AvailableAtUnixMs <= 0)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.AvailableAt, now)
                    .SetProperty(x => x.AvailableAtUnixMs, nowMs), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Companion run {RunId} could not be primed for execution.", runId);
        }

        if (_signal.CurrentCount == 0) _signal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.DrainEnabled)
        {
            _logger.LogInformation("Companion run drain is disabled; queued runs stay in companion_runs.");
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.PollIntervalMilliseconds, 50, 60_000));
        var backlog = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!backlog)
            {
                try
                {
                    await _signal.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            backlog = false;
            try
            {
                backlog = await RunPassAsync(stoppingToken).ConfigureAwait(false) >= MaxBatch;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Companion run pass failed.");
            }
        }
    }

    internal async Task<int> RunPassAsync(CancellationToken cancellationToken = default)
    {
        await ReclaimExpiredAsync(cancellationToken).ConfigureAwait(false);

        var processed = 0;
        foreach (var claim in await ClaimDueAsync(MaxBatch, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await ExecuteClaimAsync(claim, cancellationToken).ConfigureAwait(false)) processed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ReleaseClaimAsync(claim, "interrupted by host shutdown", CancellationToken.None).ConfigureAwait(false);
                break;
            }
            catch (Exception exception)
            {
                await FailClaimAsync(claim, "companion_run_failed", exception.Message, retryable: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        return processed;
    }

    public async Task ExecuteAsync(Guid runId, CancellationToken cancellationToken)
    {
        var claim = await ClaimRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (claim is null) return;
        try
        {
            await ExecuteClaimAsync(claim, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseClaimAsync(claim, "interrupted", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ── lease queue on companion_runs ──

    internal async Task<int> ReclaimExpiredAsync(CancellationToken cancellationToken)
    {
        var beforeMs = DateTimeOffset.UtcNow
            .Subtract(TimeSpan.FromSeconds(Math.Clamp(_options.ClaimLeaseSeconds, 5, 3600)))
            .ToUnixTimeMilliseconds();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var expired = await db.Runs
            .Where(x => x.Status == "running" && x.ClaimedAtUnixMs != null && x.ClaimedAtUnixMs < beforeMs)
            .OrderBy(x => x.Id)
            .Take(100)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (expired.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        foreach (var row in expired)
        {
            row.Status = "queued";
            row.ClaimToken = null;
            row.ClaimedAt = null;
            row.ClaimedAtUnixMs = null;
            row.AvailableAt = now;
            row.AvailableAtUnixMs = nowMs;
            row.LastError = "reclaimed: the previous host stopped before settling this run";
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return expired.Count;
    }

    internal async Task<IReadOnlyList<RunClaim>> ClaimDueAsync(int max, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await db.Runs.AsNoTracking()
            .Where(x => x.Status == "queued" && x.AvailableAtUnixMs <= nowMs)
            .OrderBy(x => x.AvailableAtUnixMs).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.TenantId, x.Attempts, x.StartedAt })
            .Take(Math.Clamp(max, 1, 20))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        var claims = new List<RunClaim>();
        foreach (var item in candidates)
        {
            var token = Guid.NewGuid().ToString("N");
            var startedAt = item.StartedAt ?? now;
            var won = await db.Runs
                .Where(x => x.Id == item.Id && x.Status == "queued")
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.Status, "running")
                    .SetProperty(x => x.StartedAt, startedAt)
                    .SetProperty(x => x.ClaimToken, token)
                    .SetProperty(x => x.ClaimedAt, now)
                    .SetProperty(x => x.ClaimedAtUnixMs, nowMs)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken).ConfigureAwait(false);
            if (won != 1) continue;
            claims.Add(new RunClaim(item.Id, item.TenantId, token, item.Attempts + 1));
        }
        return claims;
    }

    private async Task<RunClaim?> ClaimRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.Runs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null) return null;

        var token = Guid.NewGuid().ToString("N");
        var startedAt = run.StartedAt ?? now;
        var won = await db.Runs
            .Where(x => x.Id == runId && x.Status == "queued")
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, "running")
                .SetProperty(x => x.StartedAt, startedAt)
                .SetProperty(x => x.ClaimToken, token)
                .SetProperty(x => x.ClaimedAt, now)
                .SetProperty(x => x.ClaimedAtUnixMs, nowMs)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1), cancellationToken).ConfigureAwait(false);
        return won == 1 ? new RunClaim(run.Id, run.TenantId, token, run.Attempts + 1) : null;
    }

    private TimeSpan Backoff(int attempts)
    {
        var baseSeconds = Math.Clamp(_options.BackoffBaseSeconds, 0.001, 3600);
        var seconds = baseSeconds * Math.Pow(2, Math.Clamp(attempts - 1, 0, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, 300));
    }

    // ── execute ──

    private async Task<bool> ExecuteClaimAsync(RunClaim claim, CancellationToken cancellationToken)
    {
        CompanionRunRecord run;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = await db.Runs
                .SingleOrDefaultAsync(
                    x => x.Id == claim.RunId && x.TenantId == claim.TenantId && x.Status == "running" && x.ClaimToken == claim.Token,
                    cancellationToken)
                .ConfigureAwait(false);
            if (row is null) return false;

            if (claim.Attempts == 1)
            {
                var startedAt = row.StartedAt ?? DateTimeOffset.UtcNow;
                row.StartedAt = startedAt;
                db.Activities.Add(new CompanionActivityRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = row.TenantId,
                    Kind = "run_started",
                    CompanionId = row.CompanionId,
                    ConversationId = row.ConversationId,
                    Summary = "伙伴开始回复",
                    PayloadJson = JsonSerializer.Serialize(new { run_id = row.Id, status = "running", hop_depth = row.Depth }),
                    CreatedAt = startedAt
                });
                await CompanionActivities.SaveAsync(db, row.TenantId, cancellationToken).ConfigureAwait(false);
            }
            run = row;
        }

        _streamBroker?.PublishStageCue(
            run.TenantId, run.CompanionId, run.ConversationId, run.Id,
            emotion: "thinking", speaking: false, intensity: 0.5);

        try
        {
            var prompt = await FreezeAsync(run, claim.Token, cancellationToken).ConfigureAwait(false);
            if (prompt is null) return false;

            var resolution = prompt.ProviderId is { } providerId
                ? await _chatClients.ResolveModelChatAsync(providerId, prompt.ModelName, cancellationToken).ConfigureAwait(false)
                : await _chatClients.ResolveChatAsync(prompt.ModelPurpose, cancellationToken).ConfigureAwait(false);
            if (!resolution.IsAvailable)
                throw new RunAbortException("companion_model_unavailable",
                    resolution.Error ?? "No chat route is available.", retryable: false);

            string reply;
            using (var client = await _chatClients.CreateAsync(resolution, cancellationToken).ConfigureAwait(false))
            {
                reply = await RunTurnAsync(run, prompt, client, cancellationToken).ConfigureAwait(false);
            }

            if (reply.Length == 0)
                throw new RunAbortException("companion_run_failed", "The companion produced no reply.", retryable: true);

            await CompleteAsync(run, claim, reply, cancellationToken).ConfigureAwait(false);
            if (prompt.TriggerAuthorKind == "user")
            {
                await TryDistillTurnMemoryAsync(
                    run.TenantId, run.CompanionId, run.ConversationId, run.Id, prompt.TriggerBody, reply, cancellationToken)
                    .ConfigureAwait(false);
            }
            _streamBroker?.PublishStageCue(
                run.TenantId, run.CompanionId, run.ConversationId, run.Id,
                emotion: "idle", speaking: false, intensity: 0.25);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _streamBroker?.PublishStageCue(
                run.TenantId, run.CompanionId, run.ConversationId, run.Id,
                emotion: "idle", speaking: false, intensity: 0.2);
            await ReleaseClaimAsync(claim, "interrupted by host shutdown", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (RunAbortException abort)
        {
            _streamBroker?.PublishStageCue(
                run.TenantId, run.CompanionId, run.ConversationId, run.Id,
                emotion: "concerned", speaking: false, intensity: 0.6);
            return await FailClaimAsync(claim, abort.Code, abort.Message, abort.Retryable, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _streamBroker?.PublishStageCue(
                run.TenantId, run.CompanionId, run.ConversationId, run.Id,
                emotion: "concerned", speaking: false, intensity: 0.6);
            return await FailClaimAsync(claim, "companion_run_failed", exception.Message, retryable: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // ── freeze ──

    private sealed record SelectedCompanionMemory(Guid Id, string Content, string? Kind, DateTimeOffset UpdatedAt);

    private sealed record FrozenPrompt(
        Guid? ProviderId,
        string? ModelName,
        string ModelPurpose,
        string Instructions,
        List<ChatMessage> Messages,
        string TriggerBody,
        string TriggerAuthorKind);

    private async Task<FrozenPrompt?> FreezeAsync(CompanionRunRecord run, string claimToken, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Runs.SingleOrDefaultAsync(
            x => x.Id == run.Id && x.Status == "running" && x.ClaimToken == claimToken, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;

        var companion = await db.Companions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.CompanionId && x.TenantId == row.TenantId && x.Status != "archived", cancellationToken)
            .ConfigureAwait(false);
        if (companion is null)
            throw new RunAbortException("companion_not_found", "Companion not found.", retryable: false);

        var triggerRow = await db.Messages.AsNoTracking()
            .Where(x => x.Id == row.TriggerMessageId && x.ConversationId == row.ConversationId && x.TenantId == row.TenantId)
            .Select(x => new { x.Body, x.AuthorKind })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var triggerBody = triggerRow?.Body ?? string.Empty;
        var triggerAuthorKind = triggerRow?.AuthorKind ?? "user";

        List<SelectedCompanionMemory> memories;
        string? coreCompanionMarkdown = null;
        string? principalProfileMarkdown = null;
        var projectMemoryOverviews = new List<(string ProjectName, string Markdown)>();

        if (_contextMemory is not null)
        {
            await CompanionService.BackfillLegacyMemoriesCoreAsync(
                db, _contextMemory, row.TenantId, companion.Id, cancellationToken).ConfigureAwait(false);

            var companionDoc = await _contextMemory.GetDocumentAsync(
                "companion", companionId: companion.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (companionDoc.ActiveHotCount > 0 && !string.IsNullOrWhiteSpace(companionDoc.Markdown))
            {
                coreCompanionMarkdown = Truncate(companionDoc.Markdown.Trim(), 2400);
            }

            var companionItems = await _contextMemory.ListItemsAsync(
                scope: "companion",
                companionId: companion.Id,
                status: "active",
                includeVersions: false,
                limit: 100,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            memories = SelectMemories(
                companionItems.Items.Select(x => new SelectedCompanionMemory(x.Id, x.Content, x.Kind, x.UpdatedAt)).ToList(),
                triggerBody);

            try
            {
                var principalDoc = await _contextMemory.GetDocumentAsync("principal", cancellationToken: cancellationToken).ConfigureAwait(false);
                if (principalDoc.ActiveHotCount > 0 && !string.IsNullOrWhiteSpace(principalDoc.Markdown))
                {
                    principalProfileMarkdown = Truncate(principalDoc.Markdown.Trim(), 1600);
                }

                if (_workGateway is not null)
                {
                    var workspaces = await _workGateway.ListWorkspacesAsync(companion.Id, cancellationToken).ConfigureAwait(false);
                    if (workspaces is { IsSuccess: true, Value: { Count: > 0 } wsList })
                    {
                        foreach (var ws in wsList.Take(3))
                        {
                            var projDoc = await _contextMemory.GetDocumentAsync(
                                "project", projectId: ws.ProjectId, workspaceRoot: ws.RootPath, cancellationToken: cancellationToken).ConfigureAwait(false);
                            if (projDoc.ActiveHotCount > 0 && !string.IsNullOrWhiteSpace(projDoc.Markdown))
                            {
                                projectMemoryOverviews.Add((ws.Name, Truncate(projDoc.Markdown.Trim(), 1000)));
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not load read-only L3/L2 memory context for companion {CompanionId}.", companion.Id);
            }
        }
        else
        {
            var allMemories = await db.Memories.AsNoTracking()
                .Where(x => x.TenantId == row.TenantId && x.CompanionId == companion.Id && x.IndexStatus == "indexed")
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            memories = SelectMemories(
                allMemories.Select(x => new SelectedCompanionMemory(x.Id, x.Content, x.SourceKind, x.UpdatedAt)).ToList(),
                triggerBody);
        }

        var skills = (await db.SkillBindings
                .Where(x => x.TenantId == row.TenantId && x.CompanionId == companion.Id && x.Enabled)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        var todos = (await db.Todos.AsNoTracking()
                .Where(x => x.TenantId == row.TenantId && x.CompanionId == companion.Id && x.Status != "done" && x.Status != "cancelled")
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(x => x.UpdatedAt)
            .Take(8)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        row.ConfigSnapshotJson = JsonSerializer.Serialize(new
        {
            companion_id = companion.Id,
            revision = companion.Revision,
            name = companion.Name,
            role = CompanionContracts.NormalizeRole(companion.Role),
            persona_document = companion.PersonaDocument,
            model_purpose = companion.ModelPurpose,
            model_provider_id = companion.ModelProviderId,
            model = companion.ModelName,
            memories = memories.Select(x => new { id = x.Id, content = x.Content, kind = x.Kind }),
            skills = skills.Select(x => new { id = x.Id, name = x.Name, instructions = x.Instructions })
        }, SnakeCase);
        row.ConfigRevision = companion.Revision;
        row.LoadedMemoryIdsJson = JsonSerializer.Serialize(memories.Select(x => x.Id));
        row.LoadedSkillBindingIdsJson = JsonSerializer.Serialize(skills.Select(x => x.Id));
        foreach (var skill in skills)
        {
            skill.LastLoadedRunId = row.Id;
            skill.LastLoadedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var messages = await TranscriptAsync(db, row, cancellationToken).ConfigureAwait(false);
        return new FrozenPrompt(
            companion.ModelProviderId,
            companion.ModelName,
            companion.ModelPurpose,
            Instructions(companion, memories, coreCompanionMarkdown, principalProfileMarkdown, projectMemoryOverviews, skills, todos),
            messages,
            triggerBody,
            triggerAuthorKind);
    }

    private static string Instructions(
        CompanionAgentRecord companion,
        IReadOnlyList<SelectedCompanionMemory> memories,
        string? coreCompanionMarkdown,
        string? principalProfileMarkdown,
        IReadOnlyList<(string ProjectName, string Markdown)> projectMemoryOverviews,
        IReadOnlyList<CompanionSkillBindingRecord> skills,
        IReadOnlyList<CompanionTodoRecord> openTodos)
    {
        var persona = string.IsNullOrWhiteSpace(companion.PersonaDocument) ? DefaultPersona : companion.PersonaDocument.Trim();
        var text = new StringBuilder(persona);
        text.AppendLine().AppendLine();
        var roleLabel = CompanionContracts.NormalizeRole(companion.Role) == "session_partner" ? "会话拍档" : "伙伴";
        text.AppendLine($"你是伙伴域中的{roleLabel}「{companion.Name}」，定位为用户的跨工作区、跨设备总指挥官与个人幕僚长（Commander & Personal Chief-of-Staff）。你只与本会话的成员交流。");
        text.AppendLine();
        text.AppendLine("### 指挥职责与隔离边界");
        text.AppendLine("- **严守执行隔离**：你绝不直接读写代码文件、不直接执行 Shell/Git/MCP 命令。所有工程变更必须通过已授权的 `work_session` 连接器派发给目标工作会话中的工程智能体（在其自身审批门下执行）。");
        text.AppendLine("- **已授权工作区的完整指挥权**：在你持有 `work_session` 授权的工作区内（`*` 全局或指定项目），你拥有顶层统筹与指挥权——可随时查看可用模式（`companion_list_modes`）、新建工作会话（`companion_create_session`）、派发任务（`companion_dispatch`）、盘中纠偏引导（`companion_steer_run`）、紧急喊停（`companion_cancel_run`）、只读旁路问询（`companion_side_question`）以及查看待批审批项（`companion_list_approvals`）。");
        text.AppendLine("- **尊重与克制原则（Respectful Restraint）**：对于用户自己手动创建并正在推进的会话，除非用户明确授权你接手，或者你观察到明显的死循环/高危误操作需要保护性喊停，否则严禁无故插手、抢夺或打断用户的手动会话。");
        text.AppendLine("- **多伙伴协同**：如需与专业分工伙伴协作，可使用 `companion_relay`（或在群聊中 `@伙伴名字`，受 `max_bot_hop_depth` 跳数上限约束）。");
        text.AppendLine("- **回流与内联卡片汇报**：当收到工作会话状态回流（`awaiting_approval` 待审批、`browser_handoff` 浏览器人工接管、`completed` 完成、`failed` 失败）时，用精炼清晰的摘要向用户说明关键进展或待确认风险，配合界面内联工作会话卡片供用户一键批准/拒绝/接管，不要重复触发同一派活。");
        text.AppendLine();
        text.AppendLine("### 受控工具清单");
        text.AppendLine("- 工作区与会话指挥：`companion_list_modes` / `companion_list_workspaces` / `companion_create_workspace` / `companion_list_sessions` / `companion_create_session` / `companion_session_detail` / `companion_read_run` / `companion_dispatch` / `companion_steer_run` / `companion_cancel_run` / `companion_side_question` / `companion_list_approvals` / `companion_relay`");
        text.AppendLine("- 记忆检索与维护：`companion_memory_remember`（维护你自己的私有记忆）/ `companion_memory_search`（检索私有记忆 + 只读 L3 用户画像与 L2 项目记忆）/ `companion_project_note`（仅当策略允许时写入带溯源标记的项目笔记，否则请通过 `companion_dispatch` 交由工程会话验证并沉淀）");
        text.AppendLine("- 个人日程/计划/待办：`companion_list_schedules` / `companion_upsert_schedule` / `companion_delete_schedule` / `companion_list_plans` / `companion_upsert_plan` / `companion_list_todos` / `companion_upsert_todo`");

        if (!string.IsNullOrWhiteSpace(principalProfileMarkdown))
        {
            text.AppendLine().AppendLine("### L3 用户全局画像（只读参考）");
            text.AppendLine(principalProfileMarkdown);
        }

        if (projectMemoryOverviews.Count > 0)
        {
            text.AppendLine().AppendLine("### L2 已连接项目核心记忆（只读概览）");
            foreach (var (projectName, markdown) in projectMemoryOverviews)
            {
                text.AppendLine($"#### 项目：{projectName}");
                text.AppendLine(markdown);
            }
        }

        if (!string.IsNullOrWhiteSpace(coreCompanionMarkdown))
        {
            text.AppendLine().AppendLine("### 你的核心私有记忆文档（只有你自己可见）");
            text.AppendLine(coreCompanionMarkdown);
        }

        if (memories.Count > 0)
        {
            text.AppendLine().AppendLine("你记得的相关事实（只有你自己可见）：");
            foreach (var memory in memories) text.AppendLine($"- {memory.Content}");
        }
        if (openTodos.Count > 0)
        {
            text.AppendLine().AppendLine("你当前未完成的待办：");
            foreach (var todo in openTodos) text.AppendLine($"- [{todo.Priority}/{todo.Status}] {todo.Title}");
        }
        var withInstructions = skills.Where(x => !string.IsNullOrWhiteSpace(x.Instructions)).ToList();
        if (withInstructions.Count > 0)
        {
            text.AppendLine().AppendLine("可参考的工作手册（仅作说明）：");
            foreach (var skill in withInstructions) text.AppendLine($"- {skill.Name}：{skill.Instructions!.Trim()}");
        }
        return text.ToString().TrimEnd();
    }

    private static async Task<List<ChatMessage>> TranscriptAsync(
        CompanionDbContext db, CompanionRunRecord run, CancellationToken cancellationToken)
    {
        var rows = (await db.Messages.AsNoTracking()
                .Where(x => x.TenantId == run.TenantId && x.ConversationId == run.ConversationId)
                .OrderByDescending(x => x.Seq)
                .Take(MaxContextMessages)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.Seq)
            .ToList();
        var names = await db.Companions.AsNoTracking()
            .Where(x => x.TenantId == run.TenantId)
            .Select(x => new { x.Id, x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken).ConfigureAwait(false);

        var messages = new List<ChatMessage>();
        foreach (var row in rows)
        {
            messages.Add(row.AuthorKind switch
            {
                "system" => new ChatMessage(ChatRole.System, $"系统：{row.Body}"),
                "companion" when row.AuthorCompanionId == run.CompanionId => new ChatMessage(ChatRole.Assistant, row.Body),
                "companion" => new ChatMessage(ChatRole.User,
                    $"{(row.AuthorCompanionId is { } id ? names.GetValueOrDefault(id, "伙伴") : "伙伴")}：{row.Body}"),
                _ => new ChatMessage(ChatRole.User, $"用户：{row.Body}")
            });
        }
        if (messages.Count == 0) messages.Add(new ChatMessage(ChatRole.User, "（本会话还没有消息）"));
        return messages;
    }

    // ── streaming & bounded tool loop ──

    private async Task<string> RunTurnAsync(
        CompanionRunRecord run,
        FrozenPrompt prompt,
        IChatClient client,
        CancellationToken cancellationToken)
    {
        var planning = _services?.GetService<CompanionPlanningService>();
        var hasTools = _workGateway is not null || planning is not null || _contextMemory is not null;
        List<AITool> tools = !hasTools
            ? []
            : CompanionToolCatalog.Tools
                .Select(tool => (AITool)AIFunctionFactory.CreateDeclaration(tool.Id, tool.Description, tool.InputSchema, null))
                .ToList();
        var options = new ChatOptions
        {
            Instructions = prompt.Instructions,
            ToolMode = tools.Count == 0 ? ChatToolMode.None : ChatToolMode.Auto,
            AllowMultipleToolCalls = tools.Count > 0,
            Tools = tools.Count == 0 ? null : tools
        };

        var messages = prompt.Messages.ToList();
        var deltaSeq = 0;

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var (replies, text, seqAfter) = await StreamOrCompleteRoundAsync(
                run, client, messages, options, deltaSeq, cancellationToken).ConfigureAwait(false);
            deltaSeq = seqAfter;
            messages.AddRange(replies);

            var calls = replies.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
                .Where(call => !call.InformationalOnly && !string.IsNullOrWhiteSpace(call.Name))
                .ToList();
            if (calls.Count == 0) return text.Trim();

            var results = new List<AIContent>(calls.Count);
            foreach (var call in calls)
            {
                var callId = string.IsNullOrWhiteSpace(call.CallId) ? $"companion-{round}-{results.Count}" : call.CallId;
                var argsPreview = call.Arguments is null
                    ? null
                    : Truncate(JsonSerializer.Serialize(call.Arguments, SnakeCase), 240);
                _streamBroker?.PublishToolCall(
                    run.TenantId, run.Id, run.CompanionId, run.ConversationId,
                    callId, call.Name, "started", argsPreview, null);

                var toolOutput = await ExecuteToolAsync(
                    run.TenantId, run.CompanionId, run.ConversationId, run.Id,
                    call.Name, call.Arguments, _workGateway, planning, cancellationToken).ConfigureAwait(false);
                var isError = toolOutput.Contains("\"error\":", StringComparison.Ordinal);

                _streamBroker?.PublishToolCall(
                    run.TenantId, run.Id, run.CompanionId, run.ConversationId,
                    callId, call.Name, isError ? "failed" : "completed",
                    argsPreview, Truncate(toolOutput, 240));

                results.Add(new FunctionResultContent(callId, toolOutput));
            }
            messages.Add(new ChatMessage(ChatRole.Tool, results));
        }

        var closing = new ChatOptions
        {
            Instructions = prompt.Instructions,
            ToolMode = ChatToolMode.None,
            AllowMultipleToolCalls = false
        };
        var (_, closingText, _) = await StreamOrCompleteRoundAsync(
            run, client, messages, closing, deltaSeq, cancellationToken).ConfigureAwait(false);
        return closingText.Trim();
    }

    /// <summary>
    /// Streams live deltas via <see cref="IChatClient.GetStreamingResponseAsync"/> into
    /// <see cref="ICompanionStreamBroker"/>, falling back transparently to
    /// <see cref="IChatClient.GetResponseAsync"/> when a scripted test mock yields zero stream chunks.
    /// </summary>
    private async Task<(List<ChatMessage> Messages, string Text, int NextSeq)> StreamOrCompleteRoundAsync(
        CompanionRunRecord run,
        IChatClient client,
        IList<ChatMessage> messages,
        ChatOptions options,
        int deltaSeq,
        CancellationToken cancellationToken)
    {
        var updates = new List<ChatResponseUpdate>();
        var textBuilder = new StringBuilder();
        var announcedSpeaking = false;

        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            if (!string.IsNullOrEmpty(update.Text))
            {
                if (!announcedSpeaking)
                {
                    announcedSpeaking = true;
                    _streamBroker?.PublishStageCue(
                        run.TenantId, run.CompanionId, run.ConversationId, run.Id,
                        emotion: "speaking", speaking: true, intensity: 0.75);
                }
                deltaSeq++;
                textBuilder.Append(update.Text);
                _streamBroker?.PublishDelta(
                    run.TenantId, run.Id, run.CompanionId, run.ConversationId,
                    update.Text, deltaSeq);
            }
        }

        if (updates.Count > 0)
        {
            var aggregated = updates.ToChatResponse();
            var builtMessages = aggregated.Messages.Count != 0
                ? aggregated.Messages.ToList()
                : [new ChatMessage(ChatRole.Assistant, textBuilder.ToString())];
            return (builtMessages, aggregated.Text ?? textBuilder.ToString(), deltaSeq);
        }

        var fallback = await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        var fallbackText = fallback.Text ?? string.Empty;
        if (!string.IsNullOrEmpty(fallbackText))
        {
            _streamBroker?.PublishStageCue(
                run.TenantId, run.CompanionId, run.ConversationId, run.Id,
                emotion: "speaking", speaking: true, intensity: 0.75);
            deltaSeq++;
            _streamBroker?.PublishDelta(
                run.TenantId, run.Id, run.CompanionId, run.ConversationId,
                fallbackText, deltaSeq);
        }
        var fallbackMessages = fallback.Messages.Count != 0
            ? fallback.Messages.ToList()
            : [new ChatMessage(ChatRole.Assistant, fallbackText)];
        return (fallbackMessages, fallbackText, deltaSeq);
    }

    private static string DispatchKey(Guid scopeId, Guid sessionId, string task)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString("N") + "\n" + task.Trim()));
        return $"companion-dispatch-{scopeId:N}-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    internal async Task<string> ExecuteToolAsync(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        Guid scopeId,
        string name,
        IDictionary<string, object?>? arguments,
        ICompanionWorkGateway? gateway,
        CompanionPlanningService? planning,
        CancellationToken cancellationToken)
    {
        var args = JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, object?>(), SnakeCase);
        switch (name)
        {
            case CompanionToolCatalog.ListSchedules:
                return planning is null
                    ? Failure("tool_unavailable", "Companion planning service is not registered.")
                    : JsonSerializer.Serialize(await planning.ListSchedulesCoreAsync(tenantId, companionId, cancellationToken).ConfigureAwait(false), SnakeCase);

            case CompanionToolCatalog.UpsertSchedule:
            {
                if (planning is null)
                    return Failure("tool_unavailable", "Companion planning service is not registered.");
                try
                {
                    var runAt = TryDateTimeOffsetArg(args, "run_at");
                    var targetConv = TryGuidArg(args, "target_conversation_id", out var parsedConv) ? parsedConv : conversationId;
                    if (TryGuidArg(args, "schedule_id", out var scheduleId))
                    {
                        var patched = await planning.PatchScheduleCoreAsync(
                            tenantId,
                            companionId,
                            scheduleId,
                            expectedRevision: -1,
                            new CompanionSchedulePatchRequest(
                                Title: StringArg(args, "title"),
                                Prompt: StringArg(args, "prompt"),
                                CronExpression: StringArg(args, "cron_expression"),
                                RunAt: runAt,
                                OneShot: BoolArg(args, "one_shot"),
                                TargetConversationId: targetConv,
                                Enabled: BoolArg(args, "enabled")),
                            cancellationToken).ConfigureAwait(false);
                        return patched is null
                            ? Failure("schedule_not_found", "Schedule not found.")
                            : JsonSerializer.Serialize(patched, SnakeCase);
                    }

                    var created = await planning.CreateScheduleCoreAsync(
                        tenantId,
                        companionId,
                        new CompanionScheduleCreateRequest(
                            Title: StringArg(args, "title"),
                            Prompt: StringArg(args, "prompt"),
                            CronExpression: StringArg(args, "cron_expression"),
                            RunAt: runAt,
                            OneShot: BoolArg(args, "one_shot"),
                            TargetConversationId: targetConv,
                            Enabled: BoolArg(args, "enabled")),
                        cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.Serialize(created, SnakeCase);
                }
                catch (CompanionException ex)
                {
                    return Failure(ex.Code, ex.Message);
                }
            }

            case CompanionToolCatalog.DeleteSchedule:
            {
                if (planning is null)
                    return Failure("tool_unavailable", "Companion planning service is not registered.");
                if (!TryGuidArg(args, "schedule_id", out var scheduleId))
                    return Failure("invalid_arguments", "schedule_id must be a non-empty GUID.");
                var deleted = await planning.DeleteScheduleCoreAsync(tenantId, companionId, scheduleId, -1, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { deleted, schedule_id = scheduleId }, SnakeCase);
            }

            case CompanionToolCatalog.ListPlans:
                return planning is null
                    ? Failure("tool_unavailable", "Companion planning service is not registered.")
                    : JsonSerializer.Serialize(await planning.ListPlansCoreAsync(tenantId, companionId, cancellationToken).ConfigureAwait(false), SnakeCase);

            case CompanionToolCatalog.UpsertPlan:
            {
                if (planning is null)
                    return Failure("tool_unavailable", "Companion planning service is not registered.");
                try
                {
                    var steps = ParseStepsArg(args);
                    if (TryGuidArg(args, "plan_id", out var planId))
                    {
                        var patched = await planning.PatchPlanCoreAsync(
                            tenantId,
                            companionId,
                            planId,
                            expectedRevision: -1,
                            new CompanionPlanPatchRequest(
                                Title: StringArg(args, "title"),
                                Summary: StringArg(args, "summary"),
                                Status: StringArg(args, "status"),
                                Steps: steps),
                            cancellationToken).ConfigureAwait(false);
                        return patched is null
                            ? Failure("plan_not_found", "Plan not found.")
                            : JsonSerializer.Serialize(patched, SnakeCase);
                    }

                    var created = await planning.CreatePlanCoreAsync(
                        tenantId,
                        companionId,
                        new CompanionPlanCreateRequest(
                            Title: StringArg(args, "title"),
                            Summary: StringArg(args, "summary"),
                            Status: StringArg(args, "status"),
                            Steps: steps),
                        cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.Serialize(created, SnakeCase);
                }
                catch (CompanionException ex)
                {
                    return Failure(ex.Code, ex.Message);
                }
            }

            case CompanionToolCatalog.ListTodos:
                return planning is null
                    ? Failure("tool_unavailable", "Companion planning service is not registered.")
                    : JsonSerializer.Serialize(await planning.ListTodosCoreAsync(tenantId, companionId, cancellationToken).ConfigureAwait(false), SnakeCase);

            case CompanionToolCatalog.UpsertTodo:
            {
                if (planning is null)
                    return Failure("tool_unavailable", "Companion planning service is not registered.");
                try
                {
                    var dueAt = TryDateTimeOffsetArg(args, "due_at");
                    Guid? planId = TryGuidArg(args, "plan_id", out var parsedPlan) ? parsedPlan : null;
                    if (TryGuidArg(args, "todo_id", out var todoId))
                    {
                        var patched = await planning.PatchTodoCoreAsync(
                            tenantId,
                            companionId,
                            todoId,
                            expectedRevision: -1,
                            new CompanionTodoPatchRequest(
                                Title: StringArg(args, "title"),
                                Note: StringArg(args, "note"),
                                Priority: StringArg(args, "priority"),
                                Status: StringArg(args, "status"),
                                DueAt: dueAt,
                                PlanId: planId),
                            cancellationToken).ConfigureAwait(false);
                        return patched is null
                            ? Failure("todo_not_found", "Todo not found.")
                            : JsonSerializer.Serialize(patched, SnakeCase);
                    }

                    var created = await planning.CreateTodoCoreAsync(
                        tenantId,
                        companionId,
                        new CompanionTodoCreateRequest(
                            Title: StringArg(args, "title"),
                            Note: StringArg(args, "note"),
                            Priority: StringArg(args, "priority"),
                            Status: StringArg(args, "status"),
                            DueAt: dueAt,
                            PlanId: planId),
                        cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.Serialize(created, SnakeCase);
                }
                catch (CompanionException ex)
                {
                    return Failure(ex.Code, ex.Message);
                }
            }

            case CompanionToolCatalog.MemoryRemember:
                return await ExecuteMemoryRememberAsync(tenantId, companionId, conversationId, scopeId, args, cancellationToken)
                    .ConfigureAwait(false);

            case CompanionToolCatalog.MemorySearch:
                return await ExecuteMemorySearchAsync(tenantId, companionId, args, gateway, cancellationToken)
                    .ConfigureAwait(false);

            case CompanionToolCatalog.ProjectNote:
                return await ExecuteProjectNoteAsync(tenantId, companionId, scopeId, args, gateway, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (gateway is null)
            return Failure("tool_unavailable", "The companion work gateway is not registered in this host.");

        switch (name)
        {
            case CompanionToolCatalog.ListModes:
                return Serialize(await gateway.ListModesAsync(companionId, cancellationToken).ConfigureAwait(false));

            case CompanionToolCatalog.ListSessions:
            {
                Guid? filterProjectId = TryGuidArg(args, "project_id", out var parsedProject) ? parsedProject : null;
                return Serialize(await gateway.ListSessionsAsync(companionId, filterProjectId, cancellationToken).ConfigureAwait(false));
            }

            case CompanionToolCatalog.CreateSession:
            {
                Guid? projectId = TryGuidArg(args, "project_id", out var parsedProj) ? parsedProj : null;
                Guid? modeVersionId = null;
                if (StringArg(args, "mode_version_id") is { Length: > 0 } rawMode)
                {
                    if (!Guid.TryParse(rawMode, out var parsedMode) || parsedMode == Guid.Empty)
                        return Failure("invalid_arguments", "mode_version_id must be a valid UUID when provided.");
                    modeVersionId = parsedMode;
                }
                var title = StringArg(args, "title");
                var created = await gateway.CreateSessionAsync(
                    companionId, conversationId, projectId, title, modeVersionId, cancellationToken).ConfigureAwait(false);
                if (created is { IsSuccess: true, Value: { } createdSession })
                {
                    await RecordCommandActivityAsync(
                        tenantId, companionId, conversationId, "companion_session_created",
                        $"伙伴新建了工作会话「{createdSession.Title}」",
                        null,
                        new
                        {
                            session_id = createdSession.Id,
                            session_title = createdSession.Title,
                            project_id = createdSession.ProjectId,
                            project_name = createdSession.ProjectName,
                            mode_name = createdSession.ModeName
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                return Serialize(created);
            }

            case CompanionToolCatalog.ListWorkspaces:
                return Serialize(await gateway.ListWorkspacesAsync(companionId, cancellationToken).ConfigureAwait(false));

            case CompanionToolCatalog.SessionDetail:
            {
                if (!TryGuidArg(args, "session_id", out var detailSessionId))
                    return Failure("invalid_arguments", "session_id must be a non-empty GUID.");
                var limit = IntArg(args, "limit") ?? 20;
                return Serialize(await gateway.ReadSessionAsync(companionId, detailSessionId, limit, cancellationToken)
                    .ConfigureAwait(false));
            }

            case CompanionToolCatalog.Relay:
            {
                if (!TryGuidArg(args, "companion_id", out var targetCompanionId))
                    return Failure("invalid_arguments", "companion_id must be a non-empty GUID.");
                var body = StringArg(args, "content");
                if (string.IsNullOrWhiteSpace(body))
                    return Failure("invalid_arguments", "content is required.");
                var relayKey = $"companion-relay-{scopeId:N}-{targetCompanionId:N}-"
                    + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body.Trim())))[..12].ToLowerInvariant();
                return Serialize(await gateway.RelayToCompanionAsync(
                    companionId, targetCompanionId, body, relayKey, cancellationToken).ConfigureAwait(false));
            }

            case CompanionToolCatalog.ReadRun:
            {
                if (!TryGuidArg(args, "run_id", out var runId))
                    return Failure("invalid_arguments", "run_id must be a non-empty GUID.");
                return Serialize(await gateway.ReadRunAsync(companionId, runId, cancellationToken).ConfigureAwait(false));
            }

            case CompanionToolCatalog.Dispatch:
            {
                if (!TryGuidArg(args, "session_id", out var sessionId))
                    return Failure("invalid_arguments", "session_id must be a non-empty GUID.");
                var task = StringArg(args, "task");
                if (string.IsNullOrWhiteSpace(task))
                    return Failure("invalid_arguments", "task is required.");
                Guid? modeVersionId = null;
                if (StringArg(args, "mode_version_id") is { Length: > 0 } rawModeVersion)
                {
                    if (!Guid.TryParse(rawModeVersion, out var parsedModeVersion) || parsedModeVersion == Guid.Empty)
                        return Failure("invalid_arguments", "mode_version_id must be a valid UUID when provided.");
                    modeVersionId = parsedModeVersion;
                }
                var worktree = BoolArg(args, "worktree");
                var result = await gateway.DispatchAsync(
                    companionId,
                    sessionId,
                    task,
                    DispatchKey(scopeId, sessionId, task),
                    modeVersionId,
                    worktree,
                    conversationId,
                    cancellationToken).ConfigureAwait(false);
                if (result is { IsSuccess: true, Value: { } dispatch })
                {
                    await RecordDispatchAsync(tenantId, companionId, conversationId, scopeId, task.Trim(), dispatch, modeVersionId, worktree, cancellationToken)
                        .ConfigureAwait(false);
                }
                return Serialize(result);
            }

            case CompanionToolCatalog.SteerRun:
            {
                if (!TryGuidArg(args, "session_id", out var steerSessionId))
                    return Failure("invalid_arguments", "session_id must be a non-empty GUID.");
                var guidance = StringArg(args, "guidance");
                if (string.IsNullOrWhiteSpace(guidance))
                    return Failure("invalid_arguments", "guidance is required.");
                Guid? targetRunId = TryGuidArg(args, "run_id", out var parsedSteerRun) ? parsedSteerRun : null;
                var steerKey = DispatchKey(scopeId, steerSessionId, "steer:" + guidance);
                var steerResult = await gateway.SteerRunAsync(
                    companionId, conversationId, steerSessionId, targetRunId, guidance, steerKey, cancellationToken).ConfigureAwait(false);
                if (steerResult is { IsSuccess: true, Value: { } steered })
                {
                    await RecordCommandActivityAsync(
                        tenantId, companionId, conversationId, "companion_steered",
                        "伙伴向运行中的工作会话注入了引导纠偏",
                        Truncate(guidance.Trim(), 500),
                        new
                        {
                            session_id = steered.SessionId,
                            run_id = steered.RunId,
                            status = steered.Status,
                            guidance_preview = Truncate(guidance.Trim(), TaskPreviewLength)
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                return Serialize(steerResult);
            }

            case CompanionToolCatalog.CancelRun:
            {
                if (!TryGuidArg(args, "session_id", out var cancelSessionId))
                    return Failure("invalid_arguments", "session_id must be a non-empty GUID.");
                Guid? cancelRunId = TryGuidArg(args, "run_id", out var parsedCancelRun) ? parsedCancelRun : null;
                var reason = StringArg(args, "reason");
                var cancelResult = await gateway.CancelRunAsync(
                    companionId, cancelSessionId, cancelRunId, reason, cancellationToken).ConfigureAwait(false);
                if (cancelResult is { IsSuccess: true, Value: { } cancelled })
                {
                    await RecordCommandActivityAsync(
                        tenantId, companionId, conversationId, "companion_run_cancelled",
                        "伙伴喊停了工作会话运行",
                        reason is null ? null : Truncate(reason.Trim(), 300),
                        new
                        {
                            session_id = cancelled.SessionId,
                            run_id = cancelled.RunId,
                            status = cancelled.Status,
                            reason
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                return Serialize(cancelResult);
            }

            case CompanionToolCatalog.SideQuestion:
            {
                if (!TryGuidArg(args, "session_id", out var sideSessionId))
                    return Failure("invalid_arguments", "session_id must be a non-empty GUID.");
                var question = StringArg(args, "question");
                if (string.IsNullOrWhiteSpace(question))
                    return Failure("invalid_arguments", "question is required.");
                Guid? sideRunId = TryGuidArg(args, "run_id", out var parsedSideRun) ? parsedSideRun : null;
                return Serialize(await gateway.SideQuestionAsync(
                    companionId, sideSessionId, sideRunId, question, cancellationToken).ConfigureAwait(false));
            }

            case CompanionToolCatalog.ListApprovals:
            {
                Guid? approvalSessionId = TryGuidArg(args, "session_id", out var parsedAppSession) ? parsedAppSession : null;
                return Serialize(await gateway.ListApprovalsAsync(
                    companionId, approvalSessionId, cancellationToken).ConfigureAwait(false));
            }

            case CompanionToolCatalog.CreateWorkspace:
            {
                var workspaceName = StringArg(args, "name");
                var path = StringArg(args, "path");
                if (string.IsNullOrWhiteSpace(workspaceName))
                    return Failure("invalid_arguments", "name is required.");
                if (string.IsNullOrWhiteSpace(path))
                    return Failure("invalid_arguments", "path is required.");
                return Serialize(await gateway.CreateWorkspaceAsync(companionId, workspaceName, path, cancellationToken).ConfigureAwait(false));
            }

            default:
                return Failure("unknown_tool",
                    $"'{name}' is not one of the companion's tools. You have: "
                    + string.Join(", ", CompanionToolCatalog.Tools.Select(tool => tool.Id)) + ".");
        }
    }

    private async Task<string> ExecuteMemoryRememberAsync(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        Guid scopeId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        var action = (StringArg(args, "action") ?? "add").Trim().ToLowerInvariant();
        if (action is not ("add" or "supersede" or "revoke"))
            return Failure("invalid_arguments", "action must be add, supersede, or revoke.");

        if (_contextMemory is not null)
        {
            try
            {
                if (action == "revoke")
                {
                    if (!TryGuidArg(args, "item_id", out var revokeId))
                        return Failure("invalid_arguments", "item_id is required when action is revoke.");
                    var owned = await _contextMemory.ListItemsAsync(
                        scope: "companion", companionId: companionId, status: "active", includeVersions: false, limit: 200, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (owned.Items.All(x => x.Id != revokeId))
                        return Failure("memory_not_found", "Active private memory item was not found for this companion.");
                    var revoked = await _contextMemory.RevokeItemAsync(
                        revokeId, StringArg(args, "reason") ?? "Revoked by companion_memory_remember", cancellationToken).ConfigureAwait(false);
                    return JsonSerializer.Serialize(new { action = "revoke", id = revoked.Id, status = revoked.Status }, SnakeCase);
                }

                var content = StringArg(args, "content")?.Trim();
                if (string.IsNullOrEmpty(content))
                    return Failure("invalid_arguments", "content is required when action is add or supersede.");

                Guid? supersedeId = null;
                if (action == "supersede")
                {
                    if (!TryGuidArg(args, "item_id", out var targetId))
                        return Failure("invalid_arguments", "item_id is required when action is supersede.");
                    var owned = await _contextMemory.ListItemsAsync(
                        scope: "companion", companionId: companionId, status: "active", includeVersions: false, limit: 200, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    if (owned.Items.All(x => x.Id != targetId))
                        return Failure("memory_not_found", "Active private memory item was not found for this companion.");
                    supersedeId = targetId;
                }

                var upserted = await _contextMemory.UpsertItemAsync(new UpsertMemoryItemRequest
                {
                    Scope = "companion",
                    CompanionId = companionId,
                    Kind = StringArg(args, "kind") ?? "fact",
                    Section = StringArg(args, "section"),
                    Content = content,
                    SourceSessionId = conversationId,
                    SourceRunId = scopeId,
                    SupersedeItemId = supersedeId,
                    Reason = StringArg(args, "reason") ?? "tool:companion_memory_remember",
                    IsCompanionWriter = true
                }, cancellationToken).ConfigureAwait(false);

                return JsonSerializer.Serialize(new
                {
                    action,
                    id = upserted.Id,
                    kind = upserted.Kind,
                    section = upserted.Section,
                    content = upserted.Content,
                    in_hot_markdown = upserted.InHotMarkdown,
                    version = upserted.CurrentVersion
                }, SnakeCase);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
            {
                return Failure("memory_operation_failed", ex.Message);
            }
        }

        // Fallback when IContextMemoryService is not wired in minimal unit test hosts.
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        if (action == "revoke")
        {
            if (!TryGuidArg(args, "item_id", out var legacyRevokeId))
                return Failure("invalid_arguments", "item_id is required when action is revoke.");
            var existing = await db.Memories.SingleOrDefaultAsync(
                x => x.Id == legacyRevokeId && x.TenantId == tenantId && x.CompanionId == companionId && x.IndexStatus == "indexed",
                cancellationToken).ConfigureAwait(false);
            if (existing is null)
                return Failure("memory_not_found", "Memory item not found.");
            existing.IndexStatus = "deleted";
            existing.Revision++;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { action = "revoke", id = existing.Id, status = "revoked" }, SnakeCase);
        }

        var fallbackContent = StringArg(args, "content")?.Trim();
        if (string.IsNullOrEmpty(fallbackContent))
            return Failure("invalid_arguments", "content is required.");
        if (action == "supersede" && TryGuidArg(args, "item_id", out var legacySupersedeId))
        {
            var oldRow = await db.Memories.SingleOrDefaultAsync(
                x => x.Id == legacySupersedeId && x.TenantId == tenantId && x.CompanionId == companionId && x.IndexStatus == "indexed",
                cancellationToken).ConfigureAwait(false);
            if (oldRow is not null)
            {
                oldRow.Content = fallbackContent;
                oldRow.Revision++;
                oldRow.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { action = "supersede", id = oldRow.Id, content = oldRow.Content }, SnakeCase);
            }
        }

        var createdRow = new CompanionMemoryRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanionId = companionId,
            Content = fallbackContent,
            SourceKind = "companion_run",
            SourceConversationId = conversationId,
            Visibility = "private",
            IndexStatus = "indexed",
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Memories.Add(createdRow);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { action = "add", id = createdRow.Id, content = createdRow.Content }, SnakeCase);
    }

    private async Task<string> ExecuteMemorySearchAsync(
        Guid tenantId,
        Guid companionId,
        JsonElement args,
        ICompanionWorkGateway? gateway,
        CancellationToken cancellationToken)
    {
        var query = StringArg(args, "query")?.Trim();
        var scopeFilter = (StringArg(args, "scope") ?? "all").Trim().ToLowerInvariant();
        var limit = Math.Clamp(IntArg(args, "limit") ?? 10, 1, 50);
        Guid? requestedProjectId = TryGuidArg(args, "project_id", out var parsedProject) ? parsedProject : null;

        if (_contextMemory is null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rowsQuery = db.Memories.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.CompanionId == companionId && x.IndexStatus == "indexed");
            if (!string.IsNullOrEmpty(query))
                rowsQuery = rowsQuery.Where(x => EF.Functions.Like(x.Content, $"%{query}%"));
            var rows = (await rowsQuery.ToListAsync(cancellationToken).ConfigureAwait(false))
                .OrderByDescending(x => x.UpdatedAt)
                .Take(limit)
                .Select(x => new { id = x.Id, scope = "companion", kind = x.SourceKind, content = x.Content, updated_at = x.UpdatedAt })
                .ToList();
            return JsonSerializer.Serialize(new
            {
                companion_memories = rows,
                principal_memories = Array.Empty<object>(),
                project_memories = Array.Empty<object>()
            }, SnakeCase);
        }

        var companionList = new List<object>();
        var principalList = new List<object>();
        var projectList = new List<object>();

        if (scopeFilter is "all" or "companion")
        {
            var res = await _contextMemory.ListItemsAsync(
                scope: "companion", companionId: companionId, status: "active", query: query, includeVersions: false, limit: limit, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            companionList.AddRange(res.Items.Select(ProjectSearchItem));
        }

        if (scopeFilter is "all" or "principal")
        {
            var res = await _contextMemory.ListItemsAsync(
                scope: "principal", status: "active", query: query, includeVersions: false, limit: limit, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            principalList.AddRange(res.Items.Select(ProjectSearchItem));
        }

        if (scopeFilter is "all" or "project")
        {
            HashSet<Guid>? allowedProjects = null;
            if (gateway is not null)
            {
                var wsResult = await gateway.ListWorkspacesAsync(companionId, cancellationToken).ConfigureAwait(false);
                if (wsResult is { IsSuccess: true, Value: { } wsItems })
                    allowedProjects = wsResult.Value.Select(w => w.ProjectId).ToHashSet();
            }

            if (requestedProjectId is { } explicitPid)
            {
                if (allowedProjects is null || allowedProjects.Contains(explicitPid))
                {
                    var res = await _contextMemory.ListItemsAsync(
                        scope: "project", projectId: explicitPid, status: "active", query: query, includeVersions: false, limit: limit, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    projectList.AddRange(res.Items.Select(ProjectSearchItem));
                }
            }
            else if (allowedProjects is null || allowedProjects.Count > 0)
            {
                var res = await _contextMemory.ListItemsAsync(
                    scope: "project", status: "active", query: query, includeVersions: false, limit: limit, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                projectList.AddRange(res.Items
                    .Where(item => allowedProjects is null || (item.ProjectId is { } pid && allowedProjects.Contains(pid)))
                    .Take(limit)
                    .Select(ProjectSearchItem));
            }
        }

        return JsonSerializer.Serialize(new
        {
            companion_memories = companionList,
            principal_memories = principalList,
            project_memories = projectList
        }, SnakeCase);

        static object ProjectSearchItem(MemoryItemLedgerDto item) => new
        {
            id = item.Id,
            scope = item.Scope,
            kind = item.Kind,
            section = item.Section,
            project_id = item.ProjectId,
            content = item.Content,
            in_hot_markdown = item.InHotMarkdown,
            updated_at = item.UpdatedAt
        };
    }

    private async Task<string> ExecuteProjectNoteAsync(
        Guid tenantId,
        Guid companionId,
        Guid scopeId,
        JsonElement args,
        ICompanionWorkGateway? gateway,
        CancellationToken cancellationToken)
    {
        var content = StringArg(args, "content")?.Trim();
        if (string.IsNullOrEmpty(content))
            return Failure("invalid_arguments", "content is required.");

        if (_contextMemory is null)
            return Failure("tool_unavailable", "Context memory service is not registered in this host.");

        var policy = await _contextMemory.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        if (!policy.CompanionCanWriteProjectMemory)
        {
            return JsonSerializer.Serialize(new
            {
                error = "project_memory_read_only",
                policy = "companion_can_write_project_memory=false",
                recommended_tool = CompanionToolCatalog.Dispatch,
                message = "Companions have read-only access to L2 project memory by default (companion_can_write_project_memory is false in runtime.toml). Use companion_dispatch to have the project's work session verify and record project memory, or ask the user to enable companion_can_write_project_memory."
            }, SnakeCase);
        }

        Guid? projectId = TryGuidArg(args, "project_id", out var parsedProject) ? parsedProject : null;
        var workspaceRoot = StringArg(args, "workspace_root")?.Trim();

        if (gateway is not null && projectId is { } targetPid)
        {
            var workspaces = await gateway.ListWorkspacesAsync(companionId, cancellationToken).ConfigureAwait(false);
            if (!workspaces.IsSuccess || workspaces.Value is null || workspaces.Value.All(w => w.ProjectId != targetPid))
            {
                return Failure("work_session_unauthorized", "This companion is not authorized to write notes to the requested project workspace.");
            }
        }

        string companionName;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            companionName = await db.Companions.AsNoTracking()
                .Where(x => x.Id == companionId && x.TenantId == tenantId)
                .Select(x => x.Name)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? "Companion";
        }

        try
        {
            var taggedContent = content.StartsWith("[Companion:", StringComparison.OrdinalIgnoreCase)
                ? content
                : $"[Companion:{companionName}] {content}";
            var created = await _contextMemory.UpsertItemAsync(new UpsertMemoryItemRequest
            {
                Scope = "project",
                ProjectId = projectId,
                WorkspaceRoot = workspaceRoot,
                CompanionId = companionId,
                Kind = StringArg(args, "kind") ?? "fact",
                Section = StringArg(args, "section"),
                Content = taggedContent,
                SourceRunId = scopeId,
                Reason = $"Recorded by Companion {companionName} ({companionId:D}): {StringArg(args, "reason") ?? "companion_project_note"}",
                IsCompanionWriter = true
            }, cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                id = created.Id,
                scope = created.Scope,
                project_id = created.ProjectId,
                kind = created.Kind,
                section = created.Section,
                content = created.Content,
                provenance_companion_id = companionId,
                provenance_companion_name = companionName
            }, SnakeCase);
        }
        catch (UnauthorizedAccessException ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = "project_memory_read_only",
                policy = "companion_can_write_project_memory=false",
                recommended_tool = CompanionToolCatalog.Dispatch,
                message = ex.Message
            }, SnakeCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Failure("project_note_failed", ex.Message);
        }
    }

    private async Task RecordCommandActivityAsync(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        string kind,
        string summary,
        string? detail,
        object payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kind = kind,
                CompanionId = companionId,
                ConversationId = conversationId,
                Summary = summary,
                Detail = detail,
                PayloadJson = JsonSerializer.Serialize(payload, SnakeCase),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await CompanionActivities.SaveAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record companion command activity {Kind}.", kind);
        }
    }

    private async Task RecordDispatchAsync(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        Guid scopeId,
        string task,
        CompanionWorkDispatch dispatch,
        Guid? modeVersionId,
        bool? worktree,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Kind = DispatchActivityKind,
                CompanionId = companionId,
                ConversationId = conversationId,
                Summary = $"伙伴派活到会话「{dispatch.SessionTitle}」",
                Detail = Truncate(task, 500),
                PayloadJson = JsonSerializer.Serialize(new
                {
                    session_id = dispatch.SessionId,
                    run_id = dispatch.RunId,
                    session_title = dispatch.SessionTitle,
                    task_preview = Truncate(task, TaskPreviewLength),
                    mode_version_id = modeVersionId,
                    worktree
                }, SnakeCase),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await CompanionActivities.SaveAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not record the companion dispatch activity for scope {ScopeId}.", scopeId);
        }
    }

    private static string Serialize<T>(CompanionWorkResult<T> result) =>
        result.IsSuccess
            ? JsonSerializer.Serialize(result.Value, SnakeCase)
            : Failure(result.Error ?? "tool_failed", result.Message ?? "The call was refused.");

    private static string Failure(string error, string message) =>
        JsonSerializer.Serialize(new { error, message }, SnakeCase);

    private static string? StringArg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? BoolArg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTimeOffset? TryDateTimeOffsetArg(JsonElement args, string name) =>
        StringArg(args, name) is { Length: > 0 } raw && DateTimeOffset.TryParse(raw, out var parsed)
            ? parsed
            : null;

    private static IReadOnlyList<CompanionPlanStepDto>? ParseStepsArg(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("steps", out var stepsEl)
            || stepsEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var list = new List<CompanionPlanStepDto>();
        foreach (var item in stepsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            list.Add(new CompanionPlanStepDto(
                Id: StringArg(item, "id"),
                Title: StringArg(item, "title"),
                Status: StringArg(item, "status"),
                Note: StringArg(item, "note")));
        }
        return list;
    }

    private static bool TryGuidArg(JsonElement args, string name, out Guid value)
    {
        value = Guid.Empty;
        return Guid.TryParse(StringArg(args, name), out value) && value != Guid.Empty;
    }

    private static int? IntArg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static List<SelectedCompanionMemory> SelectMemories(IReadOnlyList<SelectedCompanionMemory> memories, string body)
    {
        if (memories.Count == 0) return [];
        var terms = Terms(body);
        var scored = memories
            .Select(memory => new
            {
                Row = memory,
                Score = (body.Contains(memory.Content, StringComparison.OrdinalIgnoreCase) ? 100 : 0)
                    + terms.Count(term => memory.Content.Contains(term, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Row.UpdatedAt)
            .Take(MaxMemories)
            .Select(x => x.Row)
            .ToList();
        if (scored.Count < MaxMemories)
        {
            scored.AddRange(memories
                .Where(memory => !scored.Contains(memory))
                .OrderByDescending(x => x.UpdatedAt)
                .Take(MaxMemories - scored.Count));
        }
        return scored;
    }

    private static readonly Regex ExplicitMemorySignalRegex = new(
        @"(?:请记住|记住|记一下|我喜欢|我不喜欢|我偏好|我的偏好|以后默认|以后请|总是使用|不要再|remember\s+that|please\s+remember|i\s+prefer|my\s+preference\s+is|always\s+use|never\s+use)[:：]?\s*(?<fact>[^\r\n]{6,220})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TrivialGreetings = new(StringComparer.OrdinalIgnoreCase)
    {
        "hi", "hello", "hey", "ok", "okay", "thanks", "thank you", "ping",
        "你好", "您好", "嗨", "早", "早安", "晚安", "在吗", "好的", "好", "收到", "谢谢", "多谢", "嗯", "哦"
    };

    /// <summary>
    /// Lightweight, bounded post-turn memory distillation into <see cref="IContextMemoryService"/>
    /// (<c>scope = "companion"</c>), skipping greetings/empty turns and avoiding extra LLM round-trips.
    /// </summary>
    internal async Task TryDistillTurnMemoryAsync(
        Guid tenantId,
        Guid companionId,
        Guid? conversationId,
        Guid? sourceRunId,
        string? userInput,
        string? companionReply,
        CancellationToken cancellationToken)
    {
        if (_contextMemory is null || string.IsNullOrWhiteSpace(userInput)) return;

        var trimmed = userInput.Trim();
        if (trimmed.Length < 10
            || TrivialGreetings.Contains(trimmed.TrimEnd('!', '！', '.', '。', '~', '～', '?', '？'))
            || trimmed.StartsWith("[工作会话回流", StringComparison.Ordinal)
            || trimmed.StartsWith("[定时任务", StringComparison.Ordinal))
        {
            return;
        }

        var matches = ExplicitMemorySignalRegex.Matches(trimmed);
        if (matches.Count == 0) return;

        try
        {
            var policy = await _contextMemory.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(policy.EvolutionMode, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(policy.EvolutionMode, "disabled", StringComparison.OrdinalIgnoreCase)
                || policy.CandidateOnly)
            {
                return;
            }

            var existing = await _contextMemory.ListItemsAsync(
                scope: "companion",
                companionId: companionId,
                status: "active",
                includeVersions: false,
                limit: 100,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var seen = existing.Items.Select(x => x.Content.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (Match match in matches)
            {
                if (added >= 2) break;
                var candidate = match.Value.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (candidate.Length is < 8 or > 240) continue;
                if (!seen.Add(candidate)) continue;

                var kind = candidate.Contains("喜欢", StringComparison.Ordinal)
                    || candidate.Contains("偏好", StringComparison.Ordinal)
                    || candidate.Contains("默认", StringComparison.Ordinal)
                    || candidate.Contains("prefer", StringComparison.OrdinalIgnoreCase)
                        ? "bond"
                        : "fact";

                await _contextMemory.UpsertItemAsync(new UpsertMemoryItemRequest
                {
                    Scope = "companion",
                    CompanionId = companionId,
                    Kind = kind,
                    Content = candidate,
                    SourceSessionId = conversationId,
                    SourceRunId = sourceRunId,
                    Reason = "post_turn_distillation",
                    IsCompanionWriter = true,
                    RespectEvolutionMode = true
                }, cancellationToken).ConfigureAwait(false);
                added++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Bounded post-turn memory distillation skipped for companion {CompanionId}.", companionId);
        }
    }

    internal async Task TryDistillAutonomyOutcomeAsync(
        Guid companionId,
        string action,
        string summary,
        Guid? conversationId,
        CancellationToken cancellationToken)
    {
        if (_contextMemory is null || string.IsNullOrWhiteSpace(summary) || action is "none") return;

        try
        {
            var policy = await _contextMemory.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(policy.EvolutionMode, "off", StringComparison.OrdinalIgnoreCase)
                || string.Equals(policy.EvolutionMode, "disabled", StringComparison.OrdinalIgnoreCase)
                || policy.CandidateOnly)
            {
                return;
            }

            var note = Truncate(summary.Replace('\r', ' ').Replace('\n', ' ').Trim(), 200);
            var existing = await _contextMemory.ListItemsAsync(
                scope: "companion",
                companionId: companionId,
                status: "active",
                kind: "episode",
                includeVersions: false,
                limit: 30,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (existing.Items.Any(x => string.Equals(x.Content.Trim(), note, StringComparison.OrdinalIgnoreCase)))
                return;

            await _contextMemory.UpsertItemAsync(new UpsertMemoryItemRequest
            {
                Scope = "companion",
                CompanionId = companionId,
                Kind = "episode",
                Content = note,
                SourceSessionId = conversationId,
                Reason = $"post_autonomy_distillation:{action}",
                IsCompanionWriter = true,
                RespectEvolutionMode = true
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Bounded post-autonomy memory distillation skipped for companion {CompanionId}.", companionId);
        }
    }

    private static string[] Terms(string body)
    {
        char[] separators = [' ', '\n', '\r', '\t', ',', '，', '.', '。', '?', '？', '!', '！', '、', ';', '；', ':', '：', '(', ')', '（', '）', '"', '“', '”'];
        return body.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ── settle & bot-to-bot mention relay ──

    private async Task CompleteAsync(CompanionRunRecord run, RunClaim claim, string reply, CancellationToken cancellationToken)
    {
        CompanionMessageRecord message;
        CompanionRunRecord settledRun;
        List<Guid> peerMentions;

        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = await db.Runs.SingleOrDefaultAsync(
                x => x.Id == run.Id && x.Status == "running" && x.ClaimToken == claim.Token, cancellationToken).ConfigureAwait(false);
            if (row is null) return;

            var conversation = await db.Conversations
                .SingleOrDefaultAsync(x => x.Id == row.ConversationId && x.TenantId == row.TenantId, cancellationToken).ConfigureAwait(false);
            if (conversation is null)
            {
                await SettleRunFailedAsync(db, row, "companion_run_failed", "Conversation not found.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var activePeerIds = await db.ConversationMembers.AsNoTracking()
                .Where(x => x.TenantId == row.TenantId && x.ConversationId == row.ConversationId
                    && x.MemberKind == "companion" && x.Status == "active"
                    && x.CompanionId != null && x.CompanionId != row.CompanionId)
                .Select(x => x.CompanionId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var activePeers = activePeerIds.Count == 0
                ? []
                : await db.Companions.AsNoTracking()
                    .Where(x => x.TenantId == row.TenantId && activePeerIds.Contains(x.Id))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            peerMentions = CompanionChannelService.ResolveMentions(reply, activePeers, row.CompanionId);

            var now = DateTimeOffset.UtcNow;
            message = await CompanionMessages.AppendAsync(
                db, row.TenantId, row.ConversationId, "companion", row.CompanionId, reply, peerMentions, null, now, cancellationToken)
                .ConfigureAwait(false);
            row.Status = "completed";
            row.ReplyMessageId = message.Id;
            row.Rounds = Math.Max(1, row.Rounds + 1);
            row.FinishedAt = now;
            row.ClaimToken = null;
            row.ClaimedAt = null;
            row.ClaimedAtUnixMs = null;
            row.LastError = null;
            conversation.LastMessageAt = now;

            var name = await db.Companions.AsNoTracking()
                .Where(x => x.Id == row.CompanionId && x.TenantId == row.TenantId)
                .Select(x => x.Name)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? "伙伴";
            db.Activities.Add(new CompanionActivityRecord
            {
                Id = Guid.NewGuid(),
                TenantId = row.TenantId,
                Kind = "run_completed",
                CompanionId = row.CompanionId,
                ConversationId = row.ConversationId,
                Summary = $"伙伴 {name} 完成了回复",
                Detail = Truncate(reply, 500),
                PayloadJson = JsonSerializer.Serialize(new
                {
                    run_id = row.Id,
                    reply_message_id = message.Id,
                    hop_depth = row.Depth,
                    mentioned_companion_ids = peerMentions
                }),
                CreatedAt = now
            });

            try
            {
                await CompanionActivities.SaveAsync(db, row.TenantId,
                    token => CompanionMessages.SaveAsync(db, message, row.TenantId, row.ConversationId, token), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DbUpdateException exception)
            {
                _logger.LogWarning(exception, "Companion reply for run {RunId} could not allocate a message sequence.", row.Id);
                await FailClaimAsync(claim, "companion_run_failed", "The reply could not be stored.", retryable: true, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            settledRun = row;
        }

        if (peerMentions.Count > 0)
        {
            var channel = _services?.GetService<ICompanionChannel>()
                ?? new CompanionChannelService(_dbFactory, this);
            await channel.OnMentionAsync(settledRun.TenantId, settledRun, message, peerMentions, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> FailClaimAsync(
        RunClaim claim, string code, string message, bool retryable, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Runs
                .SingleOrDefaultAsync(
                    x => x.Id == claim.RunId && x.TenantId == claim.TenantId && x.Status == "running" && x.ClaimToken == claim.Token,
                    cancellationToken)
                .ConfigureAwait(false);
            if (row is null) return false;

            var exhausted = !retryable || claim.Attempts >= Math.Max(1, _options.MaxAttempts);
            if (exhausted)
            {
                await SettleRunFailedAsync(db, row, code, message, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var now = DateTimeOffset.UtcNow;
            var next = now.Add(Backoff(claim.Attempts));
            row.Status = "queued";
            row.ClaimToken = null;
            row.ClaimedAt = null;
            row.ClaimedAtUnixMs = null;
            row.AvailableAt = next;
            row.AvailableAtUnixMs = next.ToUnixTimeMilliseconds();
            row.LastError = Truncate(message, MaxErrorMessage);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not record the failure of companion run {RunId}.", claim.RunId);
            return false;
        }
    }

    private async Task ReleaseClaimAsync(RunClaim claim, string note, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.Runs
                .SingleOrDefaultAsync(
                    x => x.Id == claim.RunId && x.Status == "running" && x.ClaimToken == claim.Token,
                    cancellationToken)
                .ConfigureAwait(false);
            if (row is null) return;
            var now = DateTimeOffset.UtcNow;
            row.Status = "queued";
            row.ClaimToken = null;
            row.ClaimedAt = null;
            row.ClaimedAtUnixMs = null;
            row.AvailableAt = now;
            row.AvailableAtUnixMs = now.ToUnixTimeMilliseconds();
            row.LastError = Truncate(note, MaxErrorMessage);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not release the claim on companion run {RunId}.", claim.RunId);
        }
    }

    private async Task SettleRunFailedAsync(
        CompanionDbContext db, CompanionRunRecord row,
        string code, string message, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        row.Status = "failed";
        row.ErrorCode = code;
        row.ErrorMessage = Truncate(message, MaxErrorMessage);
        row.LastError = Truncate(message, MaxErrorMessage);
        row.ClaimToken = null;
        row.ClaimedAt = null;
        row.ClaimedAtUnixMs = null;
        row.FinishedAt = now;
        var name = await db.Companions.AsNoTracking()
            .Where(x => x.Id == row.CompanionId && x.TenantId == row.TenantId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        db.Activities.Add(new CompanionActivityRecord
        {
            Id = Guid.NewGuid(),
            TenantId = row.TenantId,
            Kind = "run_failed",
            CompanionId = row.CompanionId,
            ConversationId = row.ConversationId,
            Summary = name is null ? $"伙伴运行失败：{code}" : $"伙伴 {name} 运行失败：{code}",
            Detail = Truncate(message, 500),
            PayloadJson = JsonSerializer.Serialize(new { run_id = row.Id, error_code = code }),
            CreatedAt = now
        });
        await CompanionActivities.SaveAsync(db, row.TenantId, cancellationToken).ConfigureAwait(false);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
