using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The policy surface has to distinguish "this is the shipped value" from "somebody changed it".
/// That distinction is the deviation list, so it is pinned here rather than only through HTTP.
/// </summary>
public sealed class AgentRuntimePolicyReportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tinadec-policy-report-" + Guid.NewGuid().ToString("N"));

    private const string Baseline = """
        schema_version = 1

        [spawn]
        max_depth = 2
        max_agents_per_run = 16
        max_parallel_workers = 4

        [scheduling]
        max_active_runs_per_session = 2
        worker_retry_limit = 2
        preserve_partial_results = true

        [supervision]
        required_before_final = true
        max_revision_rounds = 2

        [context]
        default_token_budget = 131072
        task_token_budget = 0
        run_token_budget = 0
        recent_message_limit = 128

        [memory]
        candidate_only = true
        retrieval_limit = 8

        [tools]
        provider = "tinadec-tools-process"
        mutation_requires_approval = true
        serialize_workspace_writes = true
        default_timeout_seconds = 600
        max_tool_rounds = 0
        max_tool_calls = 500
        """;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void AnUntouchedDocumentMatchesTheBaselineExactly()
    {
        var (snapshot, path) = Load(Baseline);

        var comparison = AgentRuntimePolicyReport.Compare(snapshot, path);

        Assert.True(comparison.DefaultsAvailable);
        Assert.Empty(comparison.Deviations);
    }

    [Fact]
    public void OnlyTheKeysThatChangedAreReported()
    {
        var (snapshot, path) = Load(Baseline);
        var edited = snapshot with
        {
            Scheduling = snapshot.Scheduling with { MaxActiveRunsPerSession = 5 },
            Supervision = snapshot.Supervision with { MaxRevisionRounds = 4 },
        };

        var comparison = AgentRuntimePolicyReport.Compare(edited, path);

        Assert.True(comparison.DefaultsAvailable);
        Assert.Equal(
            ["scheduling.max_active_runs_per_session", "supervision.max_revision_rounds"],
            comparison.Deviations);
    }

    [Fact]
    public void ALegacyDocumentIsJudgedByItsEffectiveTaskFuse()
    {
        // No task_token_budget key means "half the old behaviour": the effective fuse falls back to
        // the context window, so the deviation is about what the document runs with, not about the
        // key being absent.
        //
        // The effective side and the baseline side must be two different documents. Editing the
        // file that Compare also reads as its baseline moved both sides at once and the comparison
        // was trivially empty, so this drops the key from the snapshot instead and leaves the
        // shipped baseline carrying the key.
        var (snapshot, path) = Load(Baseline);
        var legacy = snapshot with { Context = snapshot.Context with { TaskTokenBudget = null } };

        var comparison = AgentRuntimePolicyReport.Compare(legacy, path);

        Assert.True(comparison.DefaultsAvailable);
        Assert.Contains("context.task_token_budget", comparison.Deviations, StringComparer.Ordinal);
    }

    [Fact]
    public void AMissingBaselineDropsTheComparisonInsteadOfFailing()
    {
        var (snapshot, _) = Load(Baseline);

        var comparison = AgentRuntimePolicyReport.Compare(snapshot, Path.Combine(_directory, "absent.toml"));

        Assert.False(comparison.DefaultsAvailable);
        Assert.Empty(comparison.Deviations);
    }

    private (AgentRuntimeConfigurationSnapshot Snapshot, string Path) Load(string text)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "baseline-" + Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(path, text);
        return (AgentRuntimeConfigurationStore.LoadSnapshot(path, 1), path);
    }
}
