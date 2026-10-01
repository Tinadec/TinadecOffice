namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// A claim on a finite resource: a path being written, a worktree, an environment, a terminal.
///
/// Why this exists: two agents working in one session can touch the same file, the same worktree
/// or the same environment without either of them knowing, and the second write silently destroys
/// the first. Nothing recorded "who is using what", so no reviewer could see the collision coming
/// — the governance layer was blind to the one fact that predicts it. A lease is that record.
/// </summary>
public sealed record ResourceClaim(string Kind, string ResourceKey, bool Exclusive);

/// <summary>Kinds of resource a lease can name. Kept as constants so the ledger stays queryable.</summary>
public static class ResourceLeaseKinds
{
    public const string Path = "path";
    public const string Worktree = "worktree";
    public const string Environment = "environment";
    public const string Terminal = "terminal";
    public const string GitBranch = "git_branch";
}

public static class ResourceLeaseStatuses
{
    public const string Active = "active";
    public const string Released = "released";
}

/// <summary>Why a lease exists. The two differ in who took it and what "outside it" means.</summary>
public static class ResourceLeasePurposes
{
    /// <summary>Taken by a tool call for the file or worktree it names.</summary>
    public const string Tool = "tool";

    /// <summary>
    /// Declared by the dispatcher of a task: the paths the task is allowed to write. Taken when the
    /// task is dispatched, so an overlap is visible before any command runs, and it is also the
    /// yardstick an after-the-fact change check measures the task's real writes against.
    /// </summary>
    public const string WriteScope = "write_scope";

    /// <summary>A worktree or environment assigned to a run by a steward.</summary>
    public const string Assignment = "assignment";
}

/// <summary>
/// One lease as the rest of Core sees it. Deliberately not the EF entity: the run engine and the
/// governance roles consume this through <see cref="IResourceLeaseService"/> and must not depend on
/// a persistence type.
/// </summary>
public sealed record ResourceLeaseInfo(
    Guid Id,
    string Kind,
    string ResourceKey,
    bool Exclusive,
    Guid? SessionId,
    Guid? RunId,
    Guid? TaskId,
    Guid? AgentInstanceId,
    string Status,
    string Purpose = ResourceLeasePurposes.Tool,
    string Reason = "");

/// <summary>Outcome of an acquire attempt. A refusal names the holder so the caller can report who it lost to.</summary>
public sealed record ResourceLeaseDecision(ResourceLeaseInfo? Lease, IReadOnlyList<ResourceLeaseInfo> Conflicts)
{
    public bool Granted => Lease is not null;
}

public sealed record ResourceAcquireRequest(
    ResourceClaim Claim,
    Guid? SessionId,
    Guid? RunId,
    Guid? TaskId = null,
    Guid? AgentInstanceId = null,
    string Reason = "",
    string Purpose = ResourceLeasePurposes.Tool);

/// <summary>
/// Whether a run still owns its leases. Implemented over the run lifecycle; the ledger asks only when
/// a conflict is found, so a lease left behind by a host that died mid-run is reclaimed by the next
/// run that needs the resource instead of blocking it forever.
/// </summary>
public interface IRunLivenessProbe
{
    Task<bool> IsLiveAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The resource ledger: "who is using what" as a durable fact.
///
/// Why it is Core's job: a lease is orchestration state — it decides whether work may start, and it
/// is what a governance role reads to arbitrate a collision. Tool-layer code reports what it is about
/// to touch; only the engine can decide whether that is allowed right now.
/// </summary>
public interface IResourceLeaseService
{
    /// <summary>
    /// Records the claim when nothing exclusive overlaps it, and refuses otherwise. Overlap is
    /// containment for filesystem kinds (a directory covers every file under it) and exact identity
    /// for the others. Registering the same claim twice for one owner is idempotent, so a retried tool
    /// call does not collide with itself. Concurrent acquirers are serialized: of two runs claiming
    /// overlapping resources at the same instant, exactly one wins.
    /// </summary>
    Task<ResourceLeaseDecision> AcquireAsync(ResourceAcquireRequest request, CancellationToken cancellationToken = default);

    /// <summary>The live leases held by other runs that <paramref name="claim"/> would collide with. Writes nothing.</summary>
    Task<IReadOnlyList<ResourceLeaseInfo>> ProbeAsync(ResourceClaim claim, Guid? runId, CancellationToken cancellationToken = default);

    /// <summary>Releases every active lease a run holds. Called when the run reaches a terminal state.</summary>
    Task<int> ReleaseRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the leases one task holds. Called when the task reaches a terminal status, so a
    /// finished task stops blocking a sibling in another run of the same session. Holding leases
    /// until the whole run ends would serialize parallel runs for the run's entire lifetime, which
    /// is exactly the behaviour the ledger exists to permit rather than prevent.
    /// </summary>
    Task<int> ReleaseTaskAsync(Guid runId, Guid taskId, CancellationToken cancellationToken = default);

    Task<int> ReleaseAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>Active leases, optionally narrowed to one session — the read a governance role makes.</summary>
    Task<IReadOnlyList<ResourceLeaseInfo>> ListActiveAsync(Guid? sessionId = null, CancellationToken cancellationToken = default);

    /// <summary>The active leases one task holds (its declared write scope among them).</summary>
    Task<IReadOnlyList<ResourceLeaseInfo>> ListTaskAsync(Guid runId, Guid taskId, CancellationToken cancellationToken = default);
}

/// <summary>
/// How a collision is told: one event shape for every place a claim loses, and one sentence a model
/// or a person can act on. Shared by the run engine (write scopes) and the tool dispatcher (tool
/// claims, after-the-fact changes) so a reviewer subscribed to <c>lease_conflict</c> reads one format.
/// </summary>
public static class ResourceLeaseMessages
{
    public const string ConflictEventType = "lease.conflict";

    /// <summary>A task's declared write scope overlapped a held resource when the task was dispatched or started.</summary>
    public const string DetectedAtWriteScope = "write_scope";

    /// <summary>A tool call's named file or worktree was held.</summary>
    public const string DetectedAtToolCall = "tool_call";

    /// <summary>A command already ran, and what it changed overlaps another run's claim or leaves the task's scope.</summary>
    public const string DetectedAfterTheFact = "after_the_fact";

    public static object ConflictPayload(ResourceClaim claim, IReadOnlyList<ResourceLeaseInfo> holders, Guid runId, Guid? taskId, string detectedAt) => new
    {
        run_id = runId,
        task_id = taskId,
        detected_at = detectedAt,
        claim = new { kind = claim.Kind, resource_key = claim.ResourceKey },
        holders = holders.Select(holder => new
        {
            lease_id = holder.Id,
            kind = holder.Kind,
            resource_key = holder.ResourceKey,
            purpose = holder.Purpose,
            run_id = holder.RunId,
            task_id = holder.TaskId,
            agent_instance_id = holder.AgentInstanceId
        }).ToArray()
    };

    public static string Describe(ResourceClaim claim, IReadOnlyList<ResourceLeaseInfo> holders) =>
        $"'{claim.ResourceKey}' overlaps "
        + string.Join("; ", holders.Select(holder =>
            $"{holder.Kind} '{holder.ResourceKey}' ({holder.Purpose}) held by run {holder.RunId?.ToString() ?? "the host"}{(holder.TaskId is { } heldBy ? $", task {heldBy}" : string.Empty)}"))
        + ".";
}

/// <summary>
/// The pure half of the ledger: given a claim and the active leases, which ones collide.
///
/// Pure on purpose, so the negative cases (a shared read against a shared read, the same run
/// re-entering its own claim, a released lease) are unit-tested directly instead of through a
/// database, and so the engine can ask "would this conflict" without writing anything.
/// </summary>
public static class ResourceLeasePolicy
{
    /// <summary>
    /// Two claims collide when at least one of them is exclusive and they name the same resource.
    ///
    /// Paths and worktrees live in one filesystem, so for them "the same resource" is containment,
    /// not string equality: a claim on a directory covers every file under it. That is what makes a
    /// declared write scope mean anything — a task that declared <c>src/</c> must block another run's
    /// write to <c>src/a.ts</c> — and what makes a write inside a worktree collide with the run the
    /// worktree is assigned to. The other kinds (an environment, a terminal, a branch) are opaque
    /// names and compare exactly.
    /// </summary>
    public static bool Conflicts(ResourceClaim candidate, ResourceClaim holder)
    {
        if (!candidate.Exclusive && !holder.Exclusive) return false;
        if (IsFileSystem(candidate.Kind) && IsFileSystem(holder.Kind))
            return Covers(candidate.ResourceKey, holder.ResourceKey) || Covers(holder.ResourceKey, candidate.ResourceKey);
        return string.Equals(candidate.Kind, holder.Kind, StringComparison.OrdinalIgnoreCase)
            && SameKey(candidate.ResourceKey, holder.ResourceKey);
    }

    /// <summary>Whether two keys name the same resource, spelling differences aside.</summary>
    public static bool SameKey(string left, string right) =>
        string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Canonical spelling of a key: separators unified and trailing separators dropped, so
    /// <c>C:\repo\a.ts</c> and <c>c:/repo/a.ts/</c> are one resource. Case is compared
    /// insensitively rather than folded here — Windows paths are case-insensitive, and a lowercased
    /// key would make the ledger's own record harder for a person to read.
    /// </summary>
    public static string Canonical(string value)
    {
        var trimmed = value.Trim().Replace('\\', '/');
        while (trimmed.Length > 1 && trimmed.EndsWith('/')) trimmed = trimmed[..^1];
        return trimmed;
    }

    /// <summary>The comparison form of a key: canonical and case-folded. What the ledger stores and queries by.</summary>
    public static string Normalize(string value) => Canonical(value).ToLowerInvariant();

    /// <summary>
    /// A normalized key and every directory above it, nearest first: <c>c:/ws/src/a.ts</c> →
    /// <c>c:/ws/src/a.ts</c>, <c>c:/ws/src</c>, <c>c:/ws</c>, <c>c:</c>. The holders that cover a
    /// candidate are exactly the leases on one of these keys, which keeps the overlap query an index
    /// lookup instead of a scan of every active lease in the workspace.
    /// </summary>
    public static IReadOnlyList<string> SelfAndAncestors(string normalizedKey)
    {
        var keys = new List<string>();
        var current = normalizedKey;
        while (current.Length > 0)
        {
            keys.Add(current);
            var cut = current.LastIndexOf('/');
            if (cut <= 0) break;
            current = current[..cut];
        }
        return keys;
    }

    public static bool IsFileSystem(string kind) =>
        string.Equals(kind, ResourceLeaseKinds.Path, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, ResourceLeaseKinds.Worktree, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="ancestor"/> is <paramref name="path"/> or a directory above it. The
    /// segment check matters: <c>C:/repo</c> covers <c>C:/repo/a.ts</c> but not <c>C:/repository</c>.
    /// </summary>
    public static bool Covers(string ancestor, string path)
    {
        var root = Canonical(ancestor);
        var target = Canonical(path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        return target.Length == root.Length || root.EndsWith('/') || target[root.Length] == '/';
    }

    /// <summary>
    /// The active leases a claim would collide with, ignoring leases the same run already holds.
    /// Inside one run the engine advances tool-capable workers one at a time, so two of its tasks
    /// never write concurrently; the ledger arbitrates between runs, which do.
    /// A lease with no run (held by the host rather than by a run) is treated as held by someone
    /// else, so a run cannot take a resource the host reserved.
    /// </summary>
    public static IReadOnlyList<ResourceLeaseInfo> Conflicts(
        ResourceClaim candidate,
        Guid? runId,
        IEnumerable<ResourceLeaseInfo> activeLeases) =>
        Conflicts(candidate, runId, taskId: null, activeLeases);

    /// <summary>
    /// Task-aware conflict filtering. A task may re-enter its own claim, but a different task in the
    /// same run is still a separate writer. The older run-only overload deliberately keeps its
    /// historical "same run is one owner" behavior for read-only probes and legacy callers.
    /// </summary>
    public static IReadOnlyList<ResourceLeaseInfo> Conflicts(
        ResourceClaim candidate,
        Guid? runId,
        Guid? taskId,
        IEnumerable<ResourceLeaseInfo> activeLeases) =>
        activeLeases
            .Where(lease => string.Equals(lease.Status, ResourceLeaseStatuses.Active, StringComparison.Ordinal)
                && !(lease.RunId == runId && (taskId is null || lease.TaskId == taskId))
                && Conflicts(candidate, new ResourceClaim(lease.Kind, lease.ResourceKey, lease.Exclusive)))
            .ToArray();

    /// <summary>
    /// The paths among <paramref name="changedPaths"/> that no lease in <paramref name="scope"/> covers:
    /// what a task wrote outside the write scope it declared. Empty when the task declared no scope,
    /// because an undeclared scope bounds nothing beyond the task's resource envelope.
    /// </summary>
    public static IReadOnlyList<string> OutsideScope(IEnumerable<string> changedPaths, IEnumerable<ResourceLeaseInfo> scope)
    {
        var declared = scope
            .Where(lease => string.Equals(lease.Purpose, ResourceLeasePurposes.WriteScope, StringComparison.Ordinal)
                && IsFileSystem(lease.Kind))
            .Select(lease => lease.ResourceKey)
            .ToArray();
        if (declared.Length == 0) return [];
        return changedPaths.Where(path => !declared.Any(root => Covers(root, path))).ToArray();
    }
}
