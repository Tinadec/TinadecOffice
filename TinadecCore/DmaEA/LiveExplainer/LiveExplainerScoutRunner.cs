namespace TinadecCore.DmaEA.LiveExplainer;

/// <summary>
/// L2 Ephemeral Read-Only Scout runner. Dispatches at most 1-2 short-lived, tool-less,
/// observer-isolated model calls (<c>IsObserverRun = true</c>) only when the lead narrator
/// encounters large diffs, repeated trial-and-error (&gt;=2 retries), or multi-teammate
/// diverging reasoning chains. Scouts return 2-3 condensed factual sentences and are discarded
/// immediately without touching conversation history or workspace state.
/// </summary>
public sealed class LiveExplainerScoutRunner(
    ILiveExplainerModelInvoker modelInvoker,
    TimeSpan? scoutTimeout = null)
{
    private const string ScoutSystemPrompt =
        "你是治理层的一次性只读侦察员（Ephemeral Read-Only Scout）。" +
        "你的唯一职责是速读执行层片段，用简体中文返回 2~3 句高密度客观事实摘要（不超过 120 字），" +
        "不寒暄、不使用 Markdown 标题、不编造未出现的文件或命令。";

    private readonly TimeSpan _timeout = scoutTimeout ?? TimeSpan.FromSeconds(6);
    private long _dispatchedCount;

    public long TotalScoutsDispatched => Interlocked.Read(ref _dispatchedCount);

    public async Task<string?> RunScoutsAsync(
        Guid observedRunId,
        IReadOnlyList<ScoutAssignment> assignments,
        CancellationToken cancellationToken = default)
    {
        if (assignments is null || assignments.Count == 0) return null;

        var bounded = assignments.Take(2).ToArray();
        var tasks = bounded.Select(assignment => ExecuteSingleScoutAsync(observedRunId, assignment, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var notes = results.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToArray();
        return notes.Length == 0 ? null : string.Join(" ", notes);
    }

    private async Task<string?> ExecuteSingleScoutAsync(
        Guid observedRunId,
        ScoutAssignment assignment,
        CancellationToken outerCancellation)
    {
        Interlocked.Increment(ref _dispatchedCount);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCancellation);
        cts.CancelAfter(_timeout);

        try
        {
            using var observerGuard = ObserverRunScope.Enter(observedRunId);
            var outcome = await modelInvoker.InvokeObserverAsync(new ObserverModelInvocation
            {
                ObservedRunId = observedRunId,
                Role = $"ephemeral_scout:{assignment.ScoutId}",
                IsObserverRun = true,
                SystemPrompt = ScoutSystemPrompt,
                UserPrompt = assignment.FocusPrompt,
                MaxOutputTokens = 180
            }, cts.Token).ConfigureAwait(false);

            if (!outcome.Succeeded || string.IsNullOrWhiteSpace(outcome.Text)) return null;
            return LiveExplainerSemanticFolder.TruncateSingleLine(outcome.Text, 200);
        }
        catch
        {
            // Iron Law #2: scout timeouts or model errors degrade silently with zero impact.
            return null;
        }
    }
}
