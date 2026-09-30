using Microsoft.EntityFrameworkCore;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentGraph;

/// <summary>
/// The environment registry over the workspace's rows and the resource ledger (todo E1). A slot is an
/// exclusive <c>environment</c> lease on <c>&lt;key&gt;#&lt;slot&gt;</c>, so the ledger's serialization
/// is what guarantees one holder per slot — two runs asking at the same instant cannot both get it.
/// </summary>
internal sealed class EnvironmentRegistryService(
    IDbContextFactory<AgentGraphDbContext> factory,
    IResourceLeaseService leases,
    ITenantContextAccessor tenant) : IEnvironmentRegistry
{
    public async Task<IReadOnlyList<EnvironmentView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.Environments.AsNoTracking()
            .Where(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var holders = await HoldersAsync(cancellationToken).ConfigureAwait(false);
        return rows.OrderBy(x => x.Key, StringComparer.Ordinal).Select(row => View(row, holders)).ToArray();
    }

    public async Task<EnvironmentView> RegisterAsync(EnvironmentRegistration registration, CancellationToken cancellationToken = default)
    {
        var key = registration.Key?.Trim() ?? string.Empty;
        Validate(key, registration.Kind, registration.DisplayName, registration.ConnectionJson, registration.Capacity);
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.Environments.AnyAsync(x => x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId && x.Key == key, cancellationToken).ConfigureAwait(false))
            throw new EnvironmentRegistryException(409, "environment_exists", $"An environment named '{key}' is already registered.");
        var now = DateTimeOffset.UtcNow;
        var row = new EnvironmentRecord
        {
            Id = Guid.NewGuid(),
            TenantId = scope.TenantId,
            WorkspaceId = scope.WorkspaceId,
            Key = key,
            Kind = registration.Kind,
            DisplayName = registration.DisplayName.Trim(),
            Description = string.IsNullOrWhiteSpace(registration.Description) ? null : registration.Description.Trim(),
            ConnectionJson = string.IsNullOrWhiteSpace(registration.ConnectionJson) ? "{}" : registration.ConnectionJson,
            Capacity = registration.Capacity,
            Status = EnvironmentStatuses.Available,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Environments.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw new EnvironmentRegistryException(409, "environment_exists", $"An environment named '{key}' is already registered.");
        }
        return View(row, []);
    }

    public async Task<EnvironmentView> UpdateAsync(Guid environmentId, EnvironmentUpdate update, CancellationToken cancellationToken = default)
    {
        var scope = tenant.Current;
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Environments.SingleOrDefaultAsync(x => x.Id == environmentId && x.TenantId == scope.TenantId && x.WorkspaceId == scope.WorkspaceId, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentRegistryException(404, "not_found", "The environment was not found.");
        if (update.Status is { } status && status is not (EnvironmentStatuses.Available or EnvironmentStatuses.Disabled))
            throw new EnvironmentRegistryException(400, "invalid_request", "status must be available or disabled.");
        Validate(row.Key, row.Kind, update.DisplayName ?? row.DisplayName, update.ConnectionJson, update.Capacity ?? row.Capacity);
        if (update.DisplayName is not null) row.DisplayName = update.DisplayName.Trim();
        if (update.Description is not null) row.Description = string.IsNullOrWhiteSpace(update.Description) ? null : update.Description.Trim();
        if (update.ConnectionJson is not null) row.ConnectionJson = string.IsNullOrWhiteSpace(update.ConnectionJson) ? "{}" : update.ConnectionJson;
        if (update.Capacity is { } capacity) row.Capacity = capacity;
        // Disabling stops new assignments; runs already holding a slot keep it until they release it
        // or end — taking an environment out from under a running task is the user's cancel, not ours.
        if (update.Status is not null) row.Status = update.Status;
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return View(row, await HoldersAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<EnvironmentAcquireResult> AcquireAsync(EnvironmentAcquireRequest request, CancellationToken cancellationToken = default)
    {
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);
        var key = request.Key?.Trim();
        EnvironmentView[] candidates;
        if (!string.IsNullOrEmpty(key))
        {
            var named = all.FirstOrDefault(x => x.Key == key);
            if (named is null) return Refused($"No environment is named '{key}'. {Choices(all)}");
            if (named.Status != EnvironmentStatuses.Available) return Refused($"Environment '{key}' is disabled.");
            candidates = [named];
        }
        else if (EnvironmentKinds.IsKnown(request.Kind))
        {
            candidates = all.Where(x => x.Kind == request.Kind && x.Status == EnvironmentStatuses.Available).ToArray();
            if (candidates.Length == 0) return Refused($"No available environment of kind '{request.Kind}'. {Choices(all)}");
        }
        else
        {
            return Refused($"Name an environment (key) or a kind: {string.Join(", ", EnvironmentKinds.All)}.");
        }

        // Already holding one is not a second request: a retried call gets the same slot back.
        foreach (var candidate in candidates)
            if (candidate.Holders.FirstOrDefault(holder => holder.RunId == request.RunId) is { } held)
                return new EnvironmentAcquireResult(candidate, held.Slot, held.LeaseId, null, []);

        var busy = new List<EnvironmentHolder>();
        // Emptiest first, so a kind request spreads runs over the environments instead of piling up.
        foreach (var candidate in candidates.OrderByDescending(x => x.FreeSlots).ThenBy(x => x.Key, StringComparer.Ordinal))
        {
            for (var slot = 1; slot <= candidate.Capacity; slot++)
            {
                if (candidate.Holders.Any(holder => holder.Slot == slot)) continue;
                var decision = await leases.AcquireAsync(new ResourceAcquireRequest(
                    new ResourceClaim(ResourceLeaseKinds.Environment, SlotKey(candidate.Key, slot), Exclusive: true),
                    request.SessionId, request.RunId, request.TaskId, request.AgentInstanceId,
                    string.IsNullOrWhiteSpace(request.Reason) ? $"environment {candidate.Key}" : request.Reason.Trim(),
                    ResourceLeasePurposes.Assignment), cancellationToken).ConfigureAwait(false);
                if (decision.Granted)
                {
                    var after = (await ListAsync(cancellationToken).ConfigureAwait(false)).First(x => x.Id == candidate.Id);
                    return new EnvironmentAcquireResult(after, slot, decision.Lease!.Id, null, []);
                }
            }
            busy.AddRange(candidate.Holders);
        }
        return new EnvironmentAcquireResult(null, null, null,
            candidates.Length == 1 ? $"Environment '{candidates[0].Key}' is fully in use." : "Every matching environment is fully in use.", busy);
    }

    public async Task<int> ReleaseAsync(Guid runId, string key, CancellationToken cancellationToken = default)
    {
        var prefix = (key?.Trim() ?? string.Empty) + "#";
        var released = 0;
        foreach (var lease in await leases.ListActiveAsync(null, cancellationToken).ConfigureAwait(false))
        {
            if (lease.RunId != runId || lease.Kind != ResourceLeaseKinds.Environment
                || !lease.ResourceKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            released += await leases.ReleaseAsync(lease.Id, cancellationToken).ConfigureAwait(false);
        }
        return released;
    }

    internal static string SlotKey(string key, int slot) => $"{key}#{slot}";

    private static EnvironmentAcquireResult Refused(string error) => new(null, null, null, error, []);

    private static string Choices(IReadOnlyList<EnvironmentView> all) =>
        all.Count == 0
            ? "No environment is registered in this workspace."
            : "Registered: " + string.Join(", ", all.Take(20).Select(x => $"{x.Key} ({x.Kind}, {x.FreeSlots}/{x.Capacity} free{(x.Status == EnvironmentStatuses.Available ? "" : ", disabled")})")) + ".";

    private static void Validate(string key, string kind, string displayName, string? connectionJson, int capacity)
    {
        if (EnvironmentRules.KeyError(key) is { } keyError) throw new EnvironmentRegistryException(400, "invalid_request", keyError);
        if (!EnvironmentKinds.IsKnown(kind)) throw new EnvironmentRegistryException(400, "invalid_request", $"kind must be one of: {string.Join(", ", EnvironmentKinds.All)}.");
        if (string.IsNullOrWhiteSpace(displayName)) throw new EnvironmentRegistryException(400, "invalid_request", "display_name is required.");
        if (capacity is < 1 or > EnvironmentRules.MaxCapacity) throw new EnvironmentRegistryException(400, "invalid_request", $"capacity must be between 1 and {EnvironmentRules.MaxCapacity}.");
        if (EnvironmentRules.ConnectionError(connectionJson) is { } connectionError) throw new EnvironmentRegistryException(400, "environment_connection_invalid", connectionError);
    }

    private async Task<Dictionary<string, List<EnvironmentHolder>>> HoldersAsync(CancellationToken cancellationToken)
    {
        var holders = new Dictionary<string, List<EnvironmentHolder>>(StringComparer.OrdinalIgnoreCase);
        foreach (var lease in await leases.ListActiveAsync(null, cancellationToken).ConfigureAwait(false))
        {
            if (lease.Kind != ResourceLeaseKinds.Environment) continue;
            var hash = lease.ResourceKey.LastIndexOf('#');
            if (hash <= 0 || !int.TryParse(lease.ResourceKey.AsSpan(hash + 1), out var slot)) continue;
            var key = lease.ResourceKey[..hash];
            if (!holders.TryGetValue(key, out var list)) holders[key] = list = [];
            list.Add(new EnvironmentHolder(lease.Id, slot, lease.SessionId, lease.RunId, lease.TaskId, lease.Reason));
        }
        return holders;
    }

    private static EnvironmentView View(EnvironmentRecord row, Dictionary<string, List<EnvironmentHolder>> holders) =>
        new(row.Id, row.Key, row.Kind, row.DisplayName, row.Description, row.ConnectionJson, row.Capacity, row.Status,
            holders.TryGetValue(row.Key, out var list) ? list.OrderBy(x => x.Slot).ToArray() : [], row.UpdatedAt);
}
