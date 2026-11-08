using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Companion;

/// <summary>Hand-off between the message endpoint and the background run loop.</summary>
public interface ICompanionRuntime
{
    ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>Host knobs for draining the durable companion effect queue.</summary>
public sealed class CompanionRunOptions
{
    /// <summary>
    /// False leaves the queue intact instead of draining it. Tests drive
    /// <see cref="CompanionRuntime.RunPassAsync"/> themselves so a background tick cannot race them.
    /// </summary>
    public bool DrainEnabled { get; set; } = true;

    public int PollIntervalMilliseconds { get; set; } = 5000;

    /// <summary>How long a claim stays owned before another pass may take it back.</summary>
    public int ClaimLeaseSeconds { get; set; } = 300;

    public int MaxAttempts { get; set; } = 5;

    /// <summary>First retry wait in seconds; doubles per attempt. Small, because a companion reply
    /// is one model call and a person is waiting for it.</summary>
    public double BackoffBaseSeconds { get; set; } = 2;
}

/// <summary>
/// Executes the runs a user message owes, one at a time, off the request thread. The run row carries
/// its own tenant id, so nothing here reads a request-scoped tenant: the hosted instance runs in the
/// host container while endpoints only enqueue. A run freezes the companion's configuration before
/// the model is called, never executes a tool, and writes at most one reply message, so a companion
/// reply can never cascade into another companion's turn.
///
/// The queue is a table, not a channel: <c>companion_effects</c> rows are claimed under a lease, so
/// a host that dies mid-run leaves a row the next host can reclaim instead of a run that never
/// moves.
/// </summary>
public sealed class CompanionRuntime : BackgroundService, ICompanionRuntime
{
    internal const string RunReplyKind = "run_reply";
    private const int MaxMemories = 6;
    private const int MaxContextMessages = 30;
    private const int MaxErrorMessage = 2048;
    private const int MaxBatch = 4;
    private static readonly TimeSpan SelfHealInterval = TimeSpan.FromSeconds(30);
    // One day: long enough to inspect a failed reply after the fact, short enough that the queue
    // table stays a queue and not a transcript.
    private static readonly TimeSpan EffectRetention = TimeSpan.FromDays(1);

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
    private DateTimeOffset _lastHealAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPruneAt = DateTimeOffset.MinValue;

    public CompanionRuntime(
        IDbContextFactory<CompanionDbContext> dbFactory,
        IAgentChatClientFactory chatClients,
        ILogger<CompanionRuntime> logger,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
        CompanionRunOptions? options = null)
    {
        _dbFactory = dbFactory;
        _chatClients = chatClients;
        _logger = logger;
        _options = options ?? ReadOptions(configuration);
    }

    /// <summary>
    /// Read by hand rather than through the options binder: this module's package set does not
    /// carry the binder, and the four knobs do not justify adding it.
    /// </summary>
    private static CompanionRunOptions ReadOptions(Microsoft.Extensions.Configuration.IConfiguration? configuration)
    {
        var options = new CompanionRunOptions();
        if (configuration is null) return options;
        const string section = "TinadecCompanion:";
        if (bool.TryParse(configuration[section + "RunDrainEnabled"], out var drain)) options.DrainEnabled = drain;
        if (int.TryParse(configuration[section + "PollIntervalMilliseconds"], out var poll)) options.PollIntervalMilliseconds = poll;
        if (int.TryParse(configuration[section + "ClaimLeaseSeconds"], out var lease)) options.ClaimLeaseSeconds = lease;
        if (int.TryParse(configuration[section + "MaxAttempts"], out var attempts)) options.MaxAttempts = attempts;
        // Invariant culture: a fractional backoff written as "0.5" must not be read as 5 (or
        // rejected) just because the host's locale uses a comma as the decimal separator.
        if (double.TryParse(configuration[section + "BackoffBaseSeconds"],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var backoff))
            options.BackoffBaseSeconds = backoff;
        return options;
    }

    internal sealed record EffectClaim(Guid Id, Guid TenantId, Guid RunId, string Token, int Attempts);

    /// <summary>Raised when a run cannot continue; <paramref name="retryable"/> decides whether the
    /// effect goes back on the queue or settles as failed.</summary>
    private sealed class RunAbortException(string code, string message, bool retryable) : Exception(message)
    {
        public string Code { get; } = code;
        public bool Retryable { get; } = retryable;
    }

    /// <summary>
    /// Makes the run claimable and wakes the loop. The message path already writes the effect in the
    /// same transaction as the run; this is the idempotent net for any other caller.
    /// </summary>
    public async ValueTask EnqueueAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var run = await db.Runs.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
            if (run is null) return;
            var exists = await db.Effects.AsNoTracking()
                .AnyAsync(x => x.RunId == runId && x.Kind == RunReplyKind, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                db.Effects.Add(NewEffect(run));
                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException)
                {
                    // A concurrent writer inserted the same row first; the unique (run, kind) index
                    // is the authority and the row it wrote is equally claimable.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // The request already committed the run: failing to nudge the loop must not fail the
            // message. The poll pass still finds the row and startup self-heal covers the gap.
            _logger.LogWarning(exception, "Companion run {RunId} could not be queued for execution.", runId);
        }
        // One pending wake-up is enough: the pass drains every due row, so a count that piles up
        // per message would only make the loop spin on an empty queue.
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    internal static CompanionEffectRecord NewEffect(CompanionRunRecord run)
    {
        var now = DateTimeOffset.UtcNow;
        return new CompanionEffectRecord
        {
            Id = Guid.NewGuid(),
            TenantId = run.TenantId,
            CompanionId = run.CompanionId,
            ConversationId = run.ConversationId,
            RunId = run.Id,
            Kind = RunReplyKind,
            PayloadJson = JsonSerializer.Serialize(new { run_id = run.Id, companion_id = run.CompanionId }),
            Status = "pending",
            AvailableAt = now,
            AvailableAtUnixMs = now.ToUnixTimeMilliseconds(),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.DrainEnabled)
        {
            _logger.LogInformation("Companion run drain is disabled; owed effects stay in companion_effects.");
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.PollIntervalMilliseconds, 50, 60_000));
        var backlog = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            // A pass that settled rows may have left more due; go again without paying the poll
            // interval, which would otherwise throttle a burst of messages to one batch per tick.
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
                // One bad pass must not take the loop with it; rows keep their last visible state.
                _logger.LogError(exception, "Companion effect pass failed.");
            }
        }
    }

    /// <summary>
    /// One drain pass: heal runs that lost their effect, reclaim expired leases, then claim and
    /// execute due rows serially. The old channel loop was single-reader and a companion reply is a
    /// single model call someone is waiting on, so concurrency buys nothing here.
    /// </summary>
    internal async Task<int> RunPassAsync(CancellationToken cancellationToken = default)
    {
        await SelfHealAsync(cancellationToken).ConfigureAwait(false);
        await PruneSettledAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Public so a test or an operator can settle one run without the background loop.</summary>
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

    // ── queue ──

    /// <summary>
    /// A run row is what the user sees; an effect row is what the host still owes. If a host died
    /// between those two writes (or the run predates this queue), the run reads "queued" forever.
    /// Give every such run the effect it should have had.
    /// </summary>
    internal async Task<int> SelfHealAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastHealAt < SelfHealInterval) return 0;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            // Two flat reads instead of a correlated EXISTS: the same query shape has to work on
            // SQLite and PostgreSQL. Ordered in memory because SQLite cannot ORDER BY a
            // DateTimeOffset; the scan is bounded by the number of open runs.
            var queued = (await db.Runs.AsNoTracking()
                    .Where(run => run.Status == "queued")
                    .Take(2000)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .OrderBy(run => run.CreatedAt)
                .Take(500)
                .ToList();
            if (queued.Count == 0)
            {
                _lastHealAt = now;
                return 0;
            }
            var covered = (await db.Effects.AsNoTracking()
                    .Where(x => x.Kind == RunReplyKind)
                    .Select(x => new { x.RunId })
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Where(x => x.RunId is not null)
                .Select(x => x.RunId!.Value)
                .ToHashSet();
            var orphans = queued.Where(run => !covered.Contains(run.Id)).ToList();
            if (orphans.Count > 0)
            {
                foreach (var run in orphans) db.Effects.Add(NewEffect(run));
                try
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException)
                {
                    // A concurrent enqueue healed a subset; the rows it wrote are already claimable.
                }
                _logger.LogInformation("Companion self-heal queued {Count} run(s) that had no effect row.", orphans.Count);
            }
            _lastHealAt = now;
            return orphans.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Tables may not exist yet on a cold host; retry next pass instead of giving up.
            _logger.LogWarning(exception, "Companion self-heal could not run yet.");
            return 0;
        }
    }

    /// <summary>
    /// Keeps the queue table from growing without bound: a settled effect is only history, and the
    /// run row already carries the outcome a client reads.
    /// </summary>
    internal async Task PruneSettledAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPruneAt < SelfHealInterval) return;
        _lastPruneAt = now;
        var cutoff = now.Subtract(EffectRetention);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var settled = (await db.Effects
                    .Where(x => x.Status == "done" || x.Status == "failed")
                    .Take(1000)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Where(x => x.UpdatedAt < cutoff)
                .Select(x => x.Id)
                .ToList();
            if (settled.Count == 0) return;
            await db.Effects.Where(x => settled.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Retention is housekeeping; a cold or read-only database must not stop the drain.
            _logger.LogDebug(exception, "Companion effect retention pass skipped.");
        }
    }

    /// <summary>
    /// Returns leases whose owner stopped without settling them. The claim token is the fencing
    /// boundary: a late executor can no longer settle a row another host has taken.
    /// </summary>
    internal async Task ReclaimExpiredAsync(CancellationToken cancellationToken)
    {
        var beforeMs = DateTimeOffset.UtcNow
            .Subtract(TimeSpan.FromSeconds(Math.Clamp(_options.ClaimLeaseSeconds, 5, 3600)))
            .ToUnixTimeMilliseconds();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var expired = await db.Effects
            .Where(x => x.Status == "running" && x.ClaimedAtUnixMs != null && x.ClaimedAtUnixMs < beforeMs)
            .OrderBy(x => x.Id)
            .Take(100)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (expired.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var row in expired)
        {
            row.Status = "pending";
            row.ClaimToken = null;
            row.ClaimedAt = null;
            row.ClaimedAtUnixMs = null;
            row.AvailableAt = now;
            row.AvailableAtUnixMs = now.ToUnixTimeMilliseconds();
            row.LastError = "reclaimed: the previous host stopped before settling this effect";
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<EffectClaim>> ClaimDueAsync(int max, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // The due comparison and ordering run on the numeric twin: SQLite cannot translate either
        // operation on a DateTimeOffset.
        var ids = await db.Effects.AsNoTracking()
            .Where(x => x.Status == "pending" && x.AvailableAtUnixMs <= nowMs)
            .OrderBy(x => x.AvailableAtUnixMs).ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(Math.Clamp(max, 1, 20))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        var claims = new List<EffectClaim>();
        foreach (var id in ids)
        {
            var row = await db.Effects.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
            if (row.RunId is not { } runId) continue;
            var token = Guid.NewGuid().ToString("N");
            var won = await db.Effects
                .Where(x => x.Id == id && x.Status == "pending")
                .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.Status, "running")
                    .SetProperty(x => x.ClaimToken, token)
                    .SetProperty(x => x.ClaimedAt, now)
                    .SetProperty(x => x.ClaimedAtUnixMs, nowMs)
                    .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
            if (won != 1) continue;
            claims.Add(new EffectClaim(row.Id, row.TenantId, runId, token, row.Attempts + 1));
        }
        return claims;
    }

    private async Task<EffectClaim?> ClaimRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.Runs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null) return null;
        var effect = await db.Effects
            .SingleOrDefaultAsync(x => x.RunId == runId && x.Kind == RunReplyKind, cancellationToken).ConfigureAwait(false);
        if (effect is null)
        {
            effect = NewEffect(run);
            db.Effects.Add(effect);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid().ToString("N");
        var won = await db.Effects
            .Where(x => x.Id == effect.Id && x.Status == "pending")
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, "running")
                .SetProperty(x => x.ClaimToken, token)
                .SetProperty(x => x.ClaimedAt, now)
                .SetProperty(x => x.ClaimedAtUnixMs, now.ToUnixTimeMilliseconds())
                .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken).ConfigureAwait(false);
        return won == 1 ? new EffectClaim(effect.Id, effect.TenantId, runId, token, effect.Attempts + 1) : null;
    }

    private TimeSpan Backoff(int attempts)
    {
        var baseSeconds = Math.Clamp(_options.BackoffBaseSeconds, 0.001, 3600);
        var seconds = baseSeconds * Math.Pow(2, Math.Clamp(attempts - 1, 0, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, 300));
    }

    // ── execute ──

    private async Task<bool> ExecuteClaimAsync(EffectClaim claim, CancellationToken cancellationToken)
    {
        CompanionRunRecord run;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var effect = await db.Effects
                .SingleOrDefaultAsync(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token, cancellationToken)
                .ConfigureAwait(false);
            if (effect is null) return false; // another host owns it now

            var row = await db.Runs
                .SingleOrDefaultAsync(x => x.Id == claim.RunId && x.TenantId == claim.TenantId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                await SettleEffectAsync(db, effect, "done", "the run this effect belongs to no longer exists", cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
            if (row.Status is "completed" or "failed")
            {
                // Someone settled the run already (an operator, or a duplicate from before the
                // unique index existed). Nothing is owed.
                await SettleEffectAsync(db, effect, "done", null, cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (row.Status == "queued")
            {
                row.Status = "running";
                row.StartedAt = DateTimeOffset.UtcNow;
                // The feed reports a run transition as `companion.run`, so the transition has to
                // exist as a durable row before anyone can subscribe to it.
                db.Activities.Add(new CompanionActivityRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = row.TenantId,
                    Kind = "run_started",
                    CompanionId = row.CompanionId,
                    ConversationId = row.ConversationId,
                    Summary = "伙伴开始回复",
                    PayloadJson = JsonSerializer.Serialize(new { run_id = row.Id, status = "running" }),
                    CreatedAt = row.StartedAt.Value
                });
            }
            // A run left "running" by a dead host resumes in place; its claim was reclaimed above.
            run = row;
            await CompanionActivities.SaveAsync(db, run.TenantId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var prompt = await FreezeAsync(run, cancellationToken).ConfigureAwait(false);
            if (prompt is null)
            {
                // The run moved on under us; do not overwrite whoever moved it.
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var effect = await db.Effects
                    .SingleOrDefaultAsync(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token, cancellationToken)
                    .ConfigureAwait(false);
                if (effect is not null) await SettleEffectAsync(db, effect, "done", null, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var resolution = prompt.ProviderId is { } providerId
                ? await _chatClients.ResolveModelChatAsync(providerId, prompt.ModelName, cancellationToken).ConfigureAwait(false)
                : await _chatClients.ResolveChatAsync(prompt.ModelPurpose, cancellationToken).ConfigureAwait(false);
            if (!resolution.IsAvailable)
                throw new RunAbortException("companion_model_unavailable",
                    resolution.Error ?? "No chat route is available.", retryable: false);

            string reply;
            using (var client = await _chatClients.CreateAsync(resolution, cancellationToken).ConfigureAwait(false))
            {
                var options = new ChatOptions
                {
                    Instructions = prompt.Instructions,
                    ToolMode = ChatToolMode.None,
                    AllowMultipleToolCalls = false
                };
                var response = await client.GetResponseAsync(prompt.Messages, options, cancellationToken).ConfigureAwait(false);
                reply = response.Text?.Trim() ?? string.Empty;
            }

            if (reply.Length == 0)
                throw new RunAbortException("companion_run_failed", "The companion produced no reply.", retryable: true);

            await CompleteAsync(run, claim, reply, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseClaimAsync(claim, "interrupted by host shutdown", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (RunAbortException abort)
        {
            return await FailClaimAsync(claim, abort.Code, abort.Message, abort.Retryable, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await FailClaimAsync(claim, "companion_run_failed", exception.Message, retryable: true, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task SettleEffectAsync(
        CompanionDbContext db, CompanionEffectRecord effect, string status, string? note, CancellationToken cancellationToken)
    {
        effect.Status = status;
        effect.LastError = note;
        effect.ClaimToken = null;
        effect.ClaimedAt = null;
        effect.ClaimedAtUnixMs = null;
        effect.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }


    // ── freeze ──

    private sealed record FrozenPrompt(Guid? ProviderId, string? ModelName, string ModelPurpose, string Instructions, List<ChatMessage> Messages);

    /// <summary>
    /// Reads the companion, its private memories, enabled skills and the recent transcript, then
    /// writes the frozen configuration onto the run. Returns null when the run moved on while this
    /// host was reading it; a companion that is gone or archived aborts the run without a retry.
    /// </summary>
    private async Task<FrozenPrompt?> FreezeAsync(CompanionRunRecord run, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Runs.SingleOrDefaultAsync(x => x.Id == run.Id && x.Status == "running", cancellationToken).ConfigureAwait(false);
        if (row is null) return null;

        var companion = await db.Companions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == row.CompanionId && x.TenantId == row.TenantId && x.Status != "archived", cancellationToken)
            .ConfigureAwait(false);
        if (companion is null)
            throw new RunAbortException("companion_not_found", "Companion not found.", retryable: false);

        var triggerBody = await db.Messages.AsNoTracking()
            .Where(x => x.Id == row.TriggerMessageId && x.ConversationId == row.ConversationId && x.TenantId == row.TenantId)
            .Select(x => x.Body)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;

        // Only this companion's indexed memories take part: group content is never merged into a
        // member's private memory, and another companion's rows are not even a candidate.
        var allMemories = await db.Memories.AsNoTracking()
            .Where(x => x.TenantId == row.TenantId && x.CompanionId == companion.Id && x.IndexStatus == "indexed")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var memories = SelectMemories(allMemories, triggerBody);

        var skills = (await db.SkillBindings
                .Where(x => x.TenantId == row.TenantId && x.CompanionId == companion.Id && x.Enabled)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        var now = DateTimeOffset.UtcNow;
        row.ConfigSnapshotJson = JsonSerializer.Serialize(new
        {
            companion_id = companion.Id,
            revision = companion.Revision,
            name = companion.Name,
            persona_document = companion.PersonaDocument,
            model_purpose = companion.ModelPurpose,
            model_provider_id = companion.ModelProviderId,
            model = companion.ModelName,
            memories = memories.Select(x => new { id = x.Id, content = x.Content }),
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
        return new FrozenPrompt(companion.ModelProviderId, companion.ModelName, companion.ModelPurpose, Instructions(companion, memories, skills), messages);
    }

    private static string Instructions(
        CompanionAgentRecord companion,
        IReadOnlyList<CompanionMemoryRecord> memories,
        IReadOnlyList<CompanionSkillBindingRecord> skills)
    {
        var persona = string.IsNullOrWhiteSpace(companion.PersonaDocument) ? DefaultPersona : companion.PersonaDocument.Trim();
        var text = new System.Text.StringBuilder(persona);
        text.AppendLine().AppendLine();
        text.AppendLine($"你是伙伴域中的伙伴「{companion.Name}」。你只与本会话的成员交流，不调用任何工具、MCP 或外部设备；"
            + "不泄露其他成员的私有信息，群聊内容也不会自动成为任何成员的私有记忆。");
        if (memories.Count > 0)
        {
            text.AppendLine().AppendLine("你记得的事实（只有你自己可见）：");
            foreach (var memory in memories) text.AppendLine($"- {memory.Content}");
        }
        var withInstructions = skills.Where(x => !string.IsNullOrWhiteSpace(x.Instructions)).ToList();
        if (withInstructions.Count > 0)
        {
            text.AppendLine().AppendLine("可参考的工作手册（仅作说明，不执行工具）：");
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
                // The companion's own past turns stay assistant turns; another companion speaks as a
                // named participant, never as this companion.
                "companion" when row.AuthorCompanionId == run.CompanionId => new ChatMessage(ChatRole.Assistant, row.Body),
                "companion" => new ChatMessage(ChatRole.User,
                    $"{(row.AuthorCompanionId is { } id ? names.GetValueOrDefault(id, "伙伴") : "伙伴")}：{row.Body}"),
                _ => new ChatMessage(ChatRole.User, $"用户：{row.Body}")
            });
        }
        if (messages.Count == 0) messages.Add(new ChatMessage(ChatRole.User, "（本会话还没有消息）"));
        return messages;
    }

    /// <summary>
    /// Keyword/containment match against the trigger message, then the most recently updated rows
    /// fill the remaining slots so the budget is always used deliberately rather than randomly.
    /// </summary>
    private static List<CompanionMemoryRecord> SelectMemories(IReadOnlyList<CompanionMemoryRecord> memories, string body)
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

    private static string[] Terms(string body)
    {
        char[] separators = [' ', '\n', '\r', '\t', ',', '，', '.', '。', '?', '？', '!', '！', '、', ';', '；', ':', '：', '(', ')', '（', '）', '"', '“', '”'];
        return body.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ── settle ──

    private async Task CompleteAsync(CompanionRunRecord run, EffectClaim claim, string reply, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var effect = await db.Effects
            .SingleOrDefaultAsync(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token, cancellationToken)
            .ConfigureAwait(false);
        // The lease moved to another host: it owns the reply now, and writing one here would answer
        // the same message twice.
        if (effect is null) return;

        var row = await db.Runs.SingleOrDefaultAsync(x => x.Id == run.Id && x.Status == "running", cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            await SettleEffectAsync(db, effect, "done", "the run was settled by someone else", cancellationToken).ConfigureAwait(false);
            return;
        }
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(x => x.Id == row.ConversationId && x.TenantId == row.TenantId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            await SettleRunFailedAsync(db, effect, row, "companion_run_failed", "Conversation not found.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var message = await CompanionMessages.AppendAsync(
            db, row.TenantId, row.ConversationId, "companion", row.CompanionId, reply, [], null, now, cancellationToken).ConfigureAwait(false);
        row.Status = "completed";
        row.ReplyMessageId = message.Id;
        row.Rounds = 1;
        row.FinishedAt = now;
        conversation.LastMessageAt = now;
        effect.Status = "done";
        effect.LastError = null;
        effect.ClaimToken = null;
        effect.ClaimedAt = null;
        effect.ClaimedAtUnixMs = null;
        effect.UpdatedAt = now;
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
            PayloadJson = JsonSerializer.Serialize(new { run_id = row.Id, reply_message_id = message.Id }),
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
        }
    }

    /// <summary>
    /// Puts the claim back for a later pass. The run keeps reading "running" to clients, which is
    /// true: this host still owes it a reply, just not before the backoff has elapsed. Returns true
    /// only when the run reached a terminal state, so a pass counts settled runs and not retries.
    /// </summary>
    private async Task<bool> FailClaimAsync(
        EffectClaim claim, string code, string message, bool retryable, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var effect = await db.Effects
                .SingleOrDefaultAsync(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token, cancellationToken)
                .ConfigureAwait(false);
            if (effect is null) return false;
            var row = await db.Runs
                .SingleOrDefaultAsync(x => x.Id == claim.RunId && x.TenantId == claim.TenantId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                await SettleEffectAsync(db, effect, "failed", Truncate(message, MaxErrorMessage), cancellationToken).ConfigureAwait(false);
                return true;
            }

            var exhausted = !retryable || claim.Attempts >= Math.Max(1, _options.MaxAttempts);
            if (exhausted)
            {
                await SettleRunFailedAsync(db, effect, row, code, message, cancellationToken).ConfigureAwait(false);
                return true;
            }

            var now = DateTimeOffset.UtcNow;
            effect.Status = "pending";
            effect.ClaimToken = null;
            effect.ClaimedAt = null;
            effect.ClaimedAtUnixMs = null;
            var next = now.Add(Backoff(claim.Attempts));
            effect.AvailableAt = next;
            effect.AvailableAtUnixMs = next.ToUnixTimeMilliseconds();
            effect.LastError = Truncate(message, MaxErrorMessage);
            effect.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            // The host is stopping; the lease stays and is reclaimed by whoever runs next.
            return false;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not record the failure of companion run {RunId}.", claim.RunId);
            return false;
        }
    }

    /// <summary>
    /// A run whose owner is gone (host shutdown) goes back to pending rather than failed: the next
    /// host should finish the reply, and the effect is the thing that carries that intent.
    /// </summary>
    private async Task ReleaseClaimAsync(EffectClaim claim, string note, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var effect = await db.Effects
                .SingleOrDefaultAsync(x => x.Id == claim.Id && x.Status == "running" && x.ClaimToken == claim.Token, cancellationToken)
                .ConfigureAwait(false);
            if (effect is null) return;
            effect.Status = "pending";
            effect.ClaimToken = null;
            effect.ClaimedAt = null;
            effect.ClaimedAtUnixMs = null;
            effect.AvailableAt = DateTimeOffset.UtcNow;
            effect.AvailableAtUnixMs = effect.AvailableAt.ToUnixTimeMilliseconds();
            effect.LastError = Truncate(note, MaxErrorMessage);
            effect.UpdatedAt = effect.AvailableAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not release the claim on companion effect {EffectId}.", claim.Id);
        }
    }

    private async Task SettleRunFailedAsync(
        CompanionDbContext db, CompanionEffectRecord effect, CompanionRunRecord row,
        string code, string message, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        row.Status = "failed";
        row.ErrorCode = code;
        row.ErrorMessage = Truncate(message, MaxErrorMessage);
        row.FinishedAt = now;
        effect.Status = "failed";
        effect.LastError = Truncate(message, MaxErrorMessage);
        effect.ClaimToken = null;
        effect.ClaimedAt = null;
        effect.ClaimedAtUnixMs = null;
        effect.UpdatedAt = now;
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
