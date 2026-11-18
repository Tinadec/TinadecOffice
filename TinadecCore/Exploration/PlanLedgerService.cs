using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Exploration;

/// <summary>
/// The run's shared plan (B2), stored as <c>intent</c> nodes in the exploration graph. Ordinals are
/// assigned from the existing <c>run:{runId}:intent:{ordinal}</c> keys, so a retry cannot fork the
/// order; claim and completion transition only rows the caller owns. The ledger is deliberately
/// separate from the graph's free-form intents: a row is part of this ledger only when it carries
/// the run prefix, so a model recording an intent through <c>exploration_record</c> does not silently
/// become schedulable work.
/// </summary>
public sealed class PlanLedgerService(IDbContextFactory<ExplorationDbContext> factory) : IPlanLedger
{
    public const int MaxLedgerIntents = 200;
    public const int MaxIntentsPerWrite = 64;
    private const int MaxTitleChars = 300;
    private const int MaxContentChars = 8_000;
    private const int MaxNoteChars = 1_000;
    private const int MaxEvidenceRefs = 32;
    private const int MaxEvidenceRefChars = 512;

    public async Task<IReadOnlyList<PlanIntent>> ListAsync(
        PlanLedgerScope scope,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        RequireScope(scope);
        if (status is not null && !PlanIntentStatuses.IsKnown(status))
            throw new ArgumentException($"'{status}' is not a plan intent status; use {string.Join(", ", PlanIntentStatuses.All)}.", nameof(status));

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await LedgerRows(db, scope)
            .AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows
            .Select(ToIntent)
            .Where(intent => status is null || string.Equals(intent.Status, status, StringComparison.OrdinalIgnoreCase))
            .OrderBy(intent => intent.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<PlanIntent>> AddAsync(
        PlanLedgerScope scope,
        IReadOnlyList<PlanIntentDraft> intents,
        CancellationToken cancellationToken = default)
    {
        RequireScope(scope);
        var drafts = NormalizeDrafts(intents);

        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = await LedgerRows(db, scope)
                .Select(row => row.SourceKey)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Count + drafts.Count > MaxLedgerIntents)
                throw new ArgumentException($"The plan ledger would exceed {MaxLedgerIntents} intents; replan instead of appending.", nameof(intents));
            var rows = BuildRows(scope, drafts, NextOrdinal(existing));
            db.Nodes.AddRange(rows);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return rows.Select(ToIntent).ToArray();
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // A concurrent append took the same ordinal; recompute from the rows that won.
            }
        }
    }

    public async Task<PlanIntent> ClaimAsync(
        PlanLedgerScope scope,
        Guid intentId,
        CancellationToken cancellationToken = default)
    {
        RequireScope(scope);
        var actor = RequireActor(scope);
        var now = DateTimeOffset.UtcNow;

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var claimed = await LedgerRows(db, scope)
            .Where(row => row.Id == intentId && row.Status == PlanIntentStatuses.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, PlanIntentStatuses.Running)
                .SetProperty(row => row.ClaimedByAgentInstanceId, actor)
                .SetProperty(row => row.UpdatedAtUnixMs, now.ToUnixTimeMilliseconds())
                .SetProperty(row => row.UpdatedAt, now), cancellationToken).ConfigureAwait(false);

        var current = await FindAsync(db, scope, intentId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The intent is not part of this run's plan ledger.");
        if (claimed == 1) return ToIntent(current);
        if (IsOwner(current, actor) && SameStatus(current, PlanIntentStatuses.Running)) return ToIntent(current);
        throw Denied(current, actor);
    }

    public Task<PlanIntent> CompleteAsync(
        PlanLedgerScope scope,
        Guid intentId,
        string? note = null,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(scope, intentId, PlanIntentStatuses.Done, Optional(note, MaxNoteChars), cancellationToken);

    public Task<PlanIntent> BlockAsync(
        PlanLedgerScope scope,
        Guid intentId,
        string reason,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(scope, intentId, PlanIntentStatuses.Blocked,
            Required(reason, "reason", MaxNoteChars), cancellationToken);

    public async Task<IReadOnlyList<PlanIntent>> ReplanAsync(
        PlanLedgerScope scope,
        IReadOnlyList<PlanIntentDraft> intents,
        CancellationToken cancellationToken = default)
    {
        RequireScope(scope);
        var drafts = NormalizeDrafts(intents);
        var now = DateTimeOffset.UtcNow;

        return await Persistence.SqliteTransientRetry.WriteInTransactionAsync(async ct =>
        {
            await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var rows = await LedgerRows(db, scope).ToListAsync(ct).ConfigureAwait(false);
            if (rows.Count + drafts.Count > MaxLedgerIntents)
                throw new ArgumentException($"The plan ledger would exceed {MaxLedgerIntents} intents.", nameof(intents));

            foreach (var row in rows.Where(row =>
                         SameStatus(row, PlanIntentStatuses.Pending) || SameStatus(row, PlanIntentStatuses.Running)))
            {
                row.Status = PlanIntentStatuses.Abandoned;
                row.Note = "Replaced by a replan.";
                row.UpdatedAtUnixMs = now.ToUnixTimeMilliseconds();
                row.UpdatedAt = now;
            }

            var appended = BuildRows(scope, drafts, NextOrdinal(rows.Select(row => row.SourceKey)));
            db.Nodes.AddRange(appended);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (IReadOnlyList<PlanIntent>)appended.Select(ToIntent).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlanIntent> TransitionAsync(
        PlanLedgerScope scope,
        Guid intentId,
        string target,
        string? note,
        CancellationToken cancellationToken)
    {
        RequireScope(scope);
        var actor = RequireActor(scope);
        var now = DateTimeOffset.UtcNow;

        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var updated = await LedgerRows(db, scope)
            .Where(row => row.Id == intentId && row.Status == PlanIntentStatuses.Running
                && row.ClaimedByAgentInstanceId == actor)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, target)
                .SetProperty(row => row.Note, note)
                .SetProperty(row => row.UpdatedAtUnixMs, now.ToUnixTimeMilliseconds())
                .SetProperty(row => row.UpdatedAt, now), cancellationToken).ConfigureAwait(false);

        var current = await FindAsync(db, scope, intentId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The intent is not part of this run's plan ledger.");
        if (updated == 1) return ToIntent(current);
        // A retried completion (the loop may replay a call whose response was lost) is not a conflict.
        if (IsOwner(current, actor) && SameStatus(current, target)) return ToIntent(current);
        throw Denied(current, actor);
    }

    private static IQueryable<ExplorationNodeRecord> LedgerRows(ExplorationDbContext db, PlanLedgerScope scope) =>
        db.Nodes.Where(row => row.TenantId == scope.TenantId
            && row.WorkspaceId == scope.WorkspaceId
            && row.SessionId == scope.SessionId
            && row.RunId == scope.RunId
            && row.Kind == ExplorationNodeKinds.Intent
            && row.SourceKey.StartsWith(SourcePrefix(scope)));

    private static Task<ExplorationNodeRecord?> FindAsync(
        ExplorationDbContext db,
        PlanLedgerScope scope,
        Guid intentId,
        CancellationToken cancellationToken) =>
        LedgerRows(db, scope).AsNoTracking().FirstOrDefaultAsync(row => row.Id == intentId, cancellationToken);

    private static List<PlanIntentDraft> NormalizeDrafts(IReadOnlyList<PlanIntentDraft>? intents)
    {
        if (intents is not { Count: > 0 })
            throw new ArgumentException("A ledger write needs at least one intent.");
        if (intents.Count > MaxIntentsPerWrite)
            throw new ArgumentException($"Add at most {MaxIntentsPerWrite} intents per call.");
        var result = new List<PlanIntentDraft>(intents.Count);
        foreach (var intent in intents)
        {
            ArgumentNullException.ThrowIfNull(intent);
            var title = Required(intent.Title, "title", MaxTitleChars);
            var content = Optional(intent.Content, MaxContentChars);
            var refs = NormalizeEvidenceRefs(intent.EvidenceRefs);
            result.Add(new PlanIntentDraft(title, content, refs));
        }
        return result;
    }

    private static List<ExplorationNodeRecord> BuildRows(PlanLedgerScope scope, IReadOnlyList<PlanIntentDraft> drafts, int nextOrdinal)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = new List<ExplorationNodeRecord>(drafts.Count);
        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index];
            var ordinal = nextOrdinal + index;
            rows.Add(new ExplorationNodeRecord
            {
                Id = Guid.NewGuid(),
                TenantId = scope.TenantId,
                WorkspaceId = scope.WorkspaceId,
                SessionId = scope.SessionId,
                RunId = scope.RunId,
                TaskId = scope.TaskId,
                AgentInstanceId = scope.AgentInstanceId,
                Kind = ExplorationNodeKinds.Intent,
                Title = draft.Title,
                Content = draft.Content,
                EvidenceRefsJson = JsonSerializer.Serialize(draft.EvidenceRefs ?? []),
                SourceKey = $"{SourcePrefix(scope)}{ordinal}",
                Status = PlanIntentStatuses.Pending,
                CreatedAtUnixMs = now.ToUnixTimeMilliseconds() + index,
                CreatedAt = now.AddMilliseconds(index),
                UpdatedAtUnixMs = now.ToUnixTimeMilliseconds() + index,
                UpdatedAt = now.AddMilliseconds(index)
            });
        }
        return rows;
    }

    private static int NextOrdinal(IEnumerable<string> sourceKeys)
    {
        var highest = 0;
        foreach (var key in sourceKeys)
        {
            var marker = key.LastIndexOf(":intent:", StringComparison.Ordinal);
            if (marker >= 0 && int.TryParse(key[(marker + ":intent:".Length)..], out var ordinal) && ordinal > highest)
                highest = ordinal;
        }
        return highest + 1;
    }

    private static string SourcePrefix(PlanLedgerScope scope) => $"run:{scope.RunId:N}:intent:";

    private static PlanIntent ToIntent(ExplorationNodeRecord row)
    {
        var marker = row.SourceKey.LastIndexOf(":intent:", StringComparison.Ordinal);
        var ordinal = marker >= 0 && int.TryParse(row.SourceKey[(marker + ":intent:".Length)..], out var parsed) ? parsed : 0;
        return new PlanIntent(
            row.Id,
            row.SourceKey,
            ordinal,
            row.Title,
            row.Content,
            row.Status ?? PlanIntentStatuses.Pending,
            row.TaskId,
            row.ClaimedByAgentInstanceId,
            JsonSerializer.Deserialize<string[]>(row.EvidenceRefsJson) ?? [],
            row.Note,
            row.CreatedAt,
            row.UpdatedAt ?? row.CreatedAt);
    }

    private static bool IsOwner(ExplorationNodeRecord row, Guid actor) => row.ClaimedByAgentInstanceId == actor;

    private static bool SameStatus(ExplorationNodeRecord row, string status) =>
        string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase);

    private static PlanLedgerDeniedException Denied(ExplorationNodeRecord row, Guid actor) =>
        SameStatus(row, PlanIntentStatuses.Pending)
            ? new PlanLedgerDeniedException(
                $"policy_denied: intent {ToIntent(row).Ordinal} is still pending; claim it before completing or blocking it.")
            : SameStatus(row, PlanIntentStatuses.Running)
                ? new PlanLedgerDeniedException(
                    $"policy_denied: intent {ToIntent(row).Ordinal} is claimed by another worker; only its owner may update it.")
                : new PlanLedgerDeniedException(
                    $"policy_denied: intent {ToIntent(row).Ordinal} is already {row.Status ?? PlanIntentStatuses.Pending}.");

    private static void RequireScope(PlanLedgerScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.TenantId == Guid.Empty || scope.WorkspaceId == Guid.Empty
            || scope.SessionId == Guid.Empty || scope.RunId == Guid.Empty)
            throw new ArgumentException("A plan ledger belongs to a tenant, workspace, session and run.", nameof(scope));
    }

    private static Guid RequireActor(PlanLedgerScope scope)
    {
        if (scope.AgentInstanceId is not { } actor || actor == Guid.Empty)
            throw new ArgumentException("Only a run's agent instance can claim or update a plan intent.", nameof(scope));
        return actor;
    }

    private static string Required(string? value, string field, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{field} is required.");
        var text = value.Trim();
        if (text.Length > max) throw new ArgumentException($"{field} must be at most {max} characters.");
        return text;
    }

    private static string? Optional(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length <= max ? text : text[..max] + " …(truncated)";
    }

    private static IReadOnlyList<string> NormalizeEvidenceRefs(IReadOnlyList<string>? refs)
    {
        if (refs is not { Count: > 0 }) return [];
        if (refs.Count > MaxEvidenceRefs) throw new ArgumentException($"evidence_refs must contain at most {MaxEvidenceRefs} entries.");
        var result = new List<string>(refs.Count);
        foreach (var reference in refs)
        {
            var value = reference?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (value.Length > MaxEvidenceRefChars) throw new ArgumentException($"Each evidence_ref must be at most {MaxEvidenceRefChars} characters.");
            if (!result.Contains(value, StringComparer.Ordinal)) result.Add(value);
        }
        return result;
    }
}
