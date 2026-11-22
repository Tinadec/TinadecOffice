using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Companion;

/// <summary>
/// Persistent per-Bot <c>Schedules</c>, <c>Plans</c>, and <c>Todos</c> store plus due-schedule
/// evaluation and 5-field UTC cron computation.
/// </summary>
public sealed class CompanionPlanningService(
    IDbContextFactory<CompanionDbContext> dbFactory,
    ITenantContextAccessor tenant,
    ICompanionChannel channel)
{
    private static readonly string[] ScheduleStatuses = ["active", "paused", "completed"];
    private static readonly string[] PlanStatuses = ["active", "completed", "paused", "archived"];
    private static readonly string[] PlanStepStatuses = ["pending", "in_progress", "completed", "blocked"];
    private static readonly string[] TodoPriorities = ["low", "medium", "high"];
    private static readonly string[] TodoStatuses = ["pending", "in_progress", "done", "cancelled"];

    private static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private Guid TenantId => tenant.Current.TenantId;

    // ── schedules ──

    public async Task<object> ListSchedulesAsync(Guid companionId, CancellationToken cancellationToken = default) =>
        await ListSchedulesCoreAsync(TenantId, companionId, cancellationToken).ConfigureAwait(false);

    internal async Task<List<object>> ListSchedulesCoreAsync(Guid tenantId, Guid companionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var rows = await db.Schedules.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanionId == companionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .OrderByDescending(x => x.Enabled)
            .ThenBy(x => x.NextRunAtUnixMs ?? long.MaxValue)
            .ThenByDescending(x => x.CreatedAt)
            .Select(CompanionContracts.ScheduleJson)
            .ToList();
    }

    public async Task<object> CreateScheduleAsync(
        Guid companionId, CompanionScheduleCreateRequest input, CancellationToken cancellationToken = default) =>
        await CreateScheduleCoreAsync(TenantId, companionId, input, cancellationToken).ConfigureAwait(false);

    internal async Task<object> CreateScheduleCoreAsync(
        Guid tenantId, Guid companionId, CompanionScheduleCreateRequest input, CancellationToken cancellationToken)
    {
        var title = RequireTitle(input.Title, "title");
        var prompt = RequirePrompt(input.Prompt);
        var cron = TrimToNull(input.CronExpression);
        var oneShot = input.OneShot ?? (cron is null && input.RunAt is not null);
        if (!oneShot && string.IsNullOrEmpty(cron))
            throw Invalid("Either cron_expression or one_shot + run_at is required.");
        if (oneShot && cron is null && input.RunAt is null)
            throw Invalid("run_at is required for a one_shot schedule without cron_expression.");

        var now = DateTimeOffset.UtcNow;
        var enabled = input.Enabled ?? true;
        DateTimeOffset? nextRunAt = null;
        if (cron is not null)
        {
            nextRunAt = CompanionCron.NextOccurrenceUtc(cron, now)
                ?? throw Invalid("cron_expression could not produce a valid future occurrence.");
        }
        else if (input.RunAt is { } runAt)
        {
            nextRunAt = runAt.ToUniversalTime();
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var row = new CompanionScheduleRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanionId = companionId,
            Title = title,
            Prompt = prompt,
            CronExpression = cron,
            RunAt = input.RunAt?.ToUniversalTime(),
            NextRunAt = enabled ? nextRunAt : null,
            NextRunAtUnixMs = enabled ? nextRunAt?.ToUnixTimeMilliseconds() : null,
            TargetConversationId = input.TargetConversationId,
            OneShot = oneShot,
            Enabled = enabled,
            Status = enabled ? "active" : "paused",
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Schedules.Add(row);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ScheduleJson(row);
    }

    public async Task<object?> PatchScheduleAsync(
        Guid companionId, Guid scheduleId, long expectedRevision, CompanionSchedulePatchRequest input, CancellationToken cancellationToken = default) =>
        await PatchScheduleCoreAsync(TenantId, companionId, scheduleId, expectedRevision, input, cancellationToken).ConfigureAwait(false);

    internal async Task<object?> PatchScheduleCoreAsync(
        Guid tenantId, Guid companionId, Guid scheduleId, long expectedRevision, CompanionSchedulePatchRequest input, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var row = await db.Schedules.SingleOrDefaultAsync(
            x => x.Id == scheduleId && x.CompanionId == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureOptionalRevision(row.Revision, expectedRevision);

        if (input.Title is not null) row.Title = RequireTitle(input.Title, "title");
        if (input.Prompt is not null) row.Prompt = RequirePrompt(input.Prompt);
        if (input.CronExpression is not null) row.CronExpression = TrimToNull(input.CronExpression);
        if (input.RunAt is not null) row.RunAt = input.RunAt.Value.ToUniversalTime();
        if (input.OneShot is { } oneShot) row.OneShot = oneShot;
        if (input.TargetConversationId is not null) row.TargetConversationId = input.TargetConversationId == Guid.Empty ? null : input.TargetConversationId;
        if (input.Enabled is { } enabled)
        {
            row.Enabled = enabled;
            if (enabled && row.Status == "paused") row.Status = "active";
            else if (!enabled && row.Status == "active") row.Status = "paused";
        }
        if (input.Status is not null)
        {
            row.Status = NormalizeChoice(input.Status, row.Status, ScheduleStatuses, "status");
            if (row.Status != "active") row.Enabled = false;
        }

        var now = DateTimeOffset.UtcNow;
        if (row.Enabled && row.Status == "active")
        {
            if (!string.IsNullOrWhiteSpace(row.CronExpression))
            {
                row.NextRunAt = CompanionCron.NextOccurrenceUtc(row.CronExpression, now)
                    ?? throw Invalid("cron_expression is invalid.");
                row.NextRunAtUnixMs = row.NextRunAt.Value.ToUnixTimeMilliseconds();
            }
            else if (row.RunAt is { } runAt)
            {
                row.NextRunAt = runAt;
                row.NextRunAtUnixMs = runAt.ToUnixTimeMilliseconds();
            }
            else
            {
                throw Invalid("An active schedule requires either cron_expression or run_at.");
            }
        }
        else
        {
            row.NextRunAt = null;
            row.NextRunAtUnixMs = null;
        }

        row.Revision++;
        row.UpdatedAt = now;
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.ScheduleJson(row);
    }

    public async Task<bool> DeleteScheduleAsync(
        Guid companionId, Guid scheduleId, long expectedRevision, CancellationToken cancellationToken = default) =>
        await DeleteScheduleCoreAsync(TenantId, companionId, scheduleId, expectedRevision, cancellationToken).ConfigureAwait(false);

    internal async Task<bool> DeleteScheduleCoreAsync(
        Guid tenantId, Guid companionId, Guid scheduleId, long expectedRevision, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var row = await db.Schedules.SingleOrDefaultAsync(
            x => x.Id == scheduleId && x.CompanionId == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return false;
        EnsureOptionalRevision(row.Revision, expectedRevision);
        db.Schedules.Remove(row);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<object> TriggerScheduleAsync(
        Guid companionId, Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var triggered = await channel.OnScheduleTickAsync(
            TenantId, companionId, scheduleId, manualTrigger: true, cancellationToken).ConfigureAwait(false);
        return new
        {
            schedule = triggered.ScheduleJson,
            conversation_id = triggered.ConversationId,
            run_ids = triggered.RunIds
        };
    }

    /// <summary>
    /// Evaluates due active schedules across tenants in the current database scope using integer
    /// <c>NextRunAtUnixMs</c> comparisons so SQLite translates the scan natively.
    /// </summary>
    public async Task<int> RunDueSchedulesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var nowMs = nowUtc.ToUnixTimeMilliseconds();
        List<(Guid TenantId, Guid CompanionId, Guid ScheduleId)> due;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var candidates = await db.Schedules.AsNoTracking()
                .Where(x => x.Enabled && x.Status == "active" && x.NextRunAtUnixMs != null && x.NextRunAtUnixMs <= nowMs)
                .OrderBy(x => x.NextRunAtUnixMs)
                .Take(50)
                .Select(x => new { x.TenantId, x.CompanionId, x.Id })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Count == 0) return 0;

            var pausedTenants = (await db.Settings.AsNoTracking()
                .Where(x => x.AutonomyPaused)
                .Select(x => x.TenantId)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet();

            due = candidates
                .Where(x => !pausedTenants.Contains(x.TenantId))
                .Select(x => (x.TenantId, x.CompanionId, x.Id))
                .ToList();
        }

        var fired = 0;
        foreach (var item in due)
        {
            try
            {
                await channel.OnScheduleTickAsync(
                    item.TenantId, item.CompanionId, item.ScheduleId, manualTrigger: false, cancellationToken).ConfigureAwait(false);
                fired++;
            }
            catch (CompanionException)
            {
                // Companion archived/deleted or schedule concurrently edited — skip cleanly.
            }
        }
        return fired;
    }

    // ── plans ──

    public async Task<object> ListPlansAsync(Guid companionId, CancellationToken cancellationToken = default) =>
        await ListPlansCoreAsync(TenantId, companionId, cancellationToken).ConfigureAwait(false);

    internal async Task<List<object>> ListPlansCoreAsync(Guid tenantId, Guid companionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var rows = await db.Plans.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanionId == companionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .Select(CompanionContracts.PlanJson)
            .ToList();
    }

    public async Task<object> CreatePlanAsync(
        Guid companionId, CompanionPlanCreateRequest input, CancellationToken cancellationToken = default) =>
        await CreatePlanCoreAsync(TenantId, companionId, input, cancellationToken).ConfigureAwait(false);

    internal async Task<object> CreatePlanCoreAsync(
        Guid tenantId, Guid companionId, CompanionPlanCreateRequest input, CancellationToken cancellationToken)
    {
        var title = RequireTitle(input.Title, "title");
        var status = NormalizeChoice(input.Status, "active", PlanStatuses, "status");
        var stepsJson = SerializeSteps(input.Steps);
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var row = new CompanionPlanRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanionId = companionId,
            Title = title,
            Summary = TrimMax(input.Summary, 2048, "summary"),
            Status = status,
            StepsJson = stepsJson,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Plans.Add(row);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.PlanJson(row);
    }

    public async Task<object?> PatchPlanAsync(
        Guid companionId, Guid planId, long expectedRevision, CompanionPlanPatchRequest input, CancellationToken cancellationToken = default) =>
        await PatchPlanCoreAsync(TenantId, companionId, planId, expectedRevision, input, cancellationToken).ConfigureAwait(false);

    internal async Task<object?> PatchPlanCoreAsync(
        Guid tenantId, Guid companionId, Guid planId, long expectedRevision, CompanionPlanPatchRequest input, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var row = await db.Plans.SingleOrDefaultAsync(
            x => x.Id == planId && x.CompanionId == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureOptionalRevision(row.Revision, expectedRevision);

        if (input.Title is not null) row.Title = RequireTitle(input.Title, "title");
        if (input.Summary is not null) row.Summary = TrimMax(input.Summary, 2048, "summary");
        if (input.Status is not null) row.Status = NormalizeChoice(input.Status, row.Status, PlanStatuses, "status");
        if (input.Steps is not null) row.StepsJson = SerializeSteps(input.Steps);

        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.PlanJson(row);
    }

    public async Task<bool> DeletePlanAsync(
        Guid companionId, Guid planId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var row = await db.Plans.SingleOrDefaultAsync(
            x => x.Id == planId && x.CompanionId == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return false;
        EnsureOptionalRevision(row.Revision, expectedRevision);
        db.Plans.Remove(row);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ── todos ──

    public async Task<object> ListTodosAsync(Guid companionId, CancellationToken cancellationToken = default) =>
        await ListTodosCoreAsync(TenantId, companionId, cancellationToken).ConfigureAwait(false);

    internal async Task<List<object>> ListTodosCoreAsync(Guid tenantId, Guid companionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var rows = await db.Todos.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.CompanionId == companionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .OrderBy(x => x.Status == "done" || x.Status == "cancelled" ? 1 : 0)
            .ThenByDescending(x => PriorityRank(x.Priority))
            .ThenByDescending(x => x.UpdatedAt)
            .Select(CompanionContracts.TodoJson)
            .ToList();
    }

    public async Task<object> CreateTodoAsync(
        Guid companionId, CompanionTodoCreateRequest input, CancellationToken cancellationToken = default) =>
        await CreateTodoCoreAsync(TenantId, companionId, input, cancellationToken).ConfigureAwait(false);

    internal async Task<object> CreateTodoCoreAsync(
        Guid tenantId, Guid companionId, CompanionTodoCreateRequest input, CancellationToken cancellationToken)
    {
        var title = RequireTitle(input.Title, "title");
        var priority = NormalizeChoice(input.Priority, "medium", TodoPriorities, "priority");
        var status = NormalizeChoice(input.Status, "pending", TodoStatuses, "status");
        var now = DateTimeOffset.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var row = new CompanionTodoRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanionId = companionId,
            Title = title,
            Note = TrimMax(input.Note, 2048, "note"),
            Priority = priority,
            Status = status,
            DueAt = input.DueAt?.ToUniversalTime(),
            PlanId = input.PlanId == Guid.Empty ? null : input.PlanId,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Todos.Add(row);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.TodoJson(row);
    }

    public async Task<object?> PatchTodoAsync(
        Guid companionId, Guid todoId, long expectedRevision, CompanionTodoPatchRequest input, CancellationToken cancellationToken = default) =>
        await PatchTodoCoreAsync(TenantId, companionId, todoId, expectedRevision, input, cancellationToken).ConfigureAwait(false);

    internal async Task<object?> PatchTodoCoreAsync(
        Guid tenantId, Guid companionId, Guid todoId, long expectedRevision, CompanionTodoPatchRequest input, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var row = await db.Todos.SingleOrDefaultAsync(
            x => x.Id == todoId && x.CompanionId == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return null;
        EnsureOptionalRevision(row.Revision, expectedRevision);

        if (input.Title is not null) row.Title = RequireTitle(input.Title, "title");
        if (input.Note is not null) row.Note = TrimMax(input.Note, 2048, "note");
        if (input.Priority is not null) row.Priority = NormalizeChoice(input.Priority, row.Priority, TodoPriorities, "priority");
        if (input.Status is not null) row.Status = NormalizeChoice(input.Status, row.Status, TodoStatuses, "status");
        if (input.DueAt is not null) row.DueAt = input.DueAt.Value.ToUniversalTime();
        if (input.PlanId is not null) row.PlanId = input.PlanId == Guid.Empty ? null : input.PlanId;

        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return CompanionContracts.TodoJson(row);
    }

    public async Task<bool> DeleteTodoAsync(
        Guid companionId, Guid todoId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await RequireCompanionAsync(db, tenantId, companionId, cancellationToken).ConfigureAwait(false);
        var row = await db.Todos.SingleOrDefaultAsync(
            x => x.Id == todoId && x.CompanionId == companionId && x.TenantId == tenantId, cancellationToken).ConfigureAwait(false);
        if (row is null) return false;
        EnsureOptionalRevision(row.Revision, expectedRevision);
        db.Todos.Remove(row);
        await SaveAsync(db, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ── helpers ──

    private static string SerializeSteps(IReadOnlyList<CompanionPlanStepDto>? steps)
    {
        if (steps is null || steps.Count == 0) return "[]";
        if (steps.Count > 100) throw Invalid("steps may contain at most 100 entries.");
        var normalized = new List<CompanionPlanStepDto>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var title = RequireTitle(step.Title, $"steps[{i}].title");
            var status = NormalizeChoice(step.Status, "pending", PlanStepStatuses, $"steps[{i}].status");
            var id = string.IsNullOrWhiteSpace(step.Id) ? $"step-{i + 1}" : step.Id.Trim();
            normalized.Add(new CompanionPlanStepDto(id, title, status, TrimMax(step.Note, 1024, $"steps[{i}].note")));
        }
        return JsonSerializer.Serialize(normalized, SnakeCase);
    }

    private static int PriorityRank(string priority) => priority switch
    {
        "high" => 3,
        "medium" => 2,
        _ => 1
    };

    private static async Task RequireCompanionAsync(
        CompanionDbContext db, Guid tenantId, Guid companionId, CancellationToken cancellationToken)
    {
        var exists = await db.Companions.AsNoTracking()
            .AnyAsync(x => x.Id == companionId && x.TenantId == tenantId, cancellationToken)
            .ConfigureAwait(false);
        if (!exists) throw new CompanionException("companion_not_found", "Companion not found.", 404);
    }

    private static void EnsureOptionalRevision(long actual, long expected)
    {
        if (expected >= 0 && actual != expected)
            throw new CompanionException("companion_revision_conflict", "The resource revision has changed.", 409);
    }

    private static async Task SaveAsync(CompanionDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new CompanionException("companion_revision_conflict", "The resource revision has changed.", 409);
        }
    }

    private static string RequireTitle(string? value, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw Invalid($"{field} is required.");
        if (trimmed.Length > 256) throw Invalid($"{field} must be at most 256 characters.");
        return trimmed;
    }

    private static string RequirePrompt(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw Invalid("prompt is required.");
        if (trimmed.Length > 16384) throw Invalid("prompt must be at most 16384 characters.");
        return trimmed;
    }

    private static string? TrimMax(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > max) throw Invalid($"{field} must be at most {max} characters.");
        return trimmed;
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeChoice(string? raw, string fallback, IReadOnlyList<string> allowed, string field)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var normalized = raw.Trim().ToLowerInvariant();
        if (!allowed.Contains(normalized, StringComparer.Ordinal))
            throw Invalid($"{field} must be one of: {string.Join(", ", allowed)}.");
        return normalized;
    }

    private static CompanionException Invalid(string message) => new("invalid_request", message, 400);
}

/// <summary>
/// Deterministic 5-field UTC cron parser (<c>minute hour day-of-month month day-of-week</c>).
/// Supports <c>*</c>, <c>*/step</c>, <c>a-b</c>, <c>a-b/step</c>, and comma-separated lists.
/// </summary>
public static class CompanionCron
{
    public static DateTimeOffset? NextOccurrenceUtc(string expression, DateTimeOffset afterUtc)
    {
        if (!TryParse(expression, out var schedule))
            throw new CompanionException("invalid_request", $"Invalid 5-field cron expression: '{expression}'.", 400);

        var start = afterUtc.ToUniversalTime();
        var candidate = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0, DateTimeKind.Utc)
            .AddMinutes(1);
        var limit = candidate.AddYears(2);

        while (candidate <= limit)
        {
            if (!schedule.Months[candidate.Month])
            {
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
                continue;
            }

            var domMatch = schedule.DaysOfMonth[candidate.Day];
            var dowMatch = schedule.DaysOfWeek[(int)candidate.DayOfWeek];
            var dayMatches = schedule.DomWildcard && schedule.DowWildcard
                ? true
                : schedule.DomWildcard
                    ? dowMatch
                    : schedule.DowWildcard
                        ? domMatch
                        : domMatch || dowMatch;

            if (!dayMatches)
            {
                candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);
                continue;
            }

            if (!schedule.Hours[candidate.Hour])
            {
                candidate = new DateTime(candidate.Year, candidate.Month, candidate.Day, candidate.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
                continue;
            }

            if (!schedule.Minutes[candidate.Minute])
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            return new DateTimeOffset(candidate, TimeSpan.Zero);
        }

        return null;
    }

    public static bool TryParse(string? expression, out ParsedCron parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var parts = expression.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;

        if (!TryParseField(parts[0], 0, 59, allowSevenSunday: false, out var minutes, out _)
            || !TryParseField(parts[1], 0, 23, allowSevenSunday: false, out var hours, out _)
            || !TryParseField(parts[2], 1, 31, allowSevenSunday: false, out var dom, out var domWildcard)
            || !TryParseField(parts[3], 1, 12, allowSevenSunday: false, out var months, out _)
            || !TryParseField(parts[4], 0, 6, allowSevenSunday: true, out var dow, out var dowWildcard))
        {
            return false;
        }

        parsed = new ParsedCron(minutes, hours, dom, months, dow, domWildcard, dowWildcard);
        return true;
    }

    private static bool TryParseField(
        string token, int min, int max, bool allowSevenSunday, out bool[] mask, out bool isWildcard)
    {
        mask = new bool[max + 1];
        isWildcard = token == "*";
        foreach (var segment in token.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = 1;
            var rangePart = segment;
            var slashIdx = segment.IndexOf('/');
            if (slashIdx >= 0)
            {
                if (!int.TryParse(segment[(slashIdx + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
                    return false;
                rangePart = segment[..slashIdx];
            }

            int rangeStart;
            int rangeEnd;
            if (rangePart == "*")
            {
                rangeStart = min;
                rangeEnd = max;
            }
            else
            {
                var dashIdx = rangePart.IndexOf('-');
                if (dashIdx >= 0)
                {
                    if (!TryParseCronInt(rangePart[..dashIdx], min, max, allowSevenSunday, out rangeStart)
                        || !TryParseCronInt(rangePart[(dashIdx + 1)..], min, max, allowSevenSunday, out rangeEnd))
                        return false;
                    if (allowSevenSunday && rangeEnd == 0 && rangePart[(dashIdx + 1)..] == "7")
                    {
                        mask[0] = true;
                        rangeEnd = 6;
                    }
                    if (rangeEnd < rangeStart) return false;
                }
                else
                {
                    if (!TryParseCronInt(rangePart, min, max, allowSevenSunday, out rangeStart))
                        return false;
                    rangeEnd = slashIdx >= 0 ? max : rangeStart;
                }
            }

            for (var v = rangeStart; v <= rangeEnd; v += step)
                mask[v] = true;
        }

        return mask.Any(x => x);
    }

    private static bool TryParseCronInt(string text, int min, int max, bool allowSevenSunday, out int value)
    {
        value = 0;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;
        if (allowSevenSunday && parsed == 7)
        {
            value = 0;
            return true;
        }
        if (parsed < min || parsed > max) return false;
        value = parsed;
        return true;
    }

    public readonly record struct ParsedCron(
        bool[] Minutes,
        bool[] Hours,
        bool[] DaysOfMonth,
        bool[] Months,
        bool[] DaysOfWeek,
        bool DomWildcard,
        bool DowWildcard);
}
