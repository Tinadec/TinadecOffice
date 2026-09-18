using System.Text;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Per-task tool-round overrides plus the round/token/fuse configuration: TOML
/// parse, freeze-time validation ceilings, the effective-limit resolution the
/// worker tool loop consumes, and the cross-key relation between the compression
/// threshold and the context budget.
/// </summary>
public sealed class ToolRoundsPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tinadec-tool-rounds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Parse_AppliesTaskRoundOverridesFromToml()
    {
        var snapshot = LoadSnapshot([.. BaselineToml(), "", "[tools.task_round_overrides]", "test = 12", "high = 8"]);

        Assert.Equal(12, snapshot.Tools.Overrides["test"]);
        Assert.Equal(8, snapshot.Tools.Overrides["HIGH"]);
    }

    [Fact]
    public void Parse_ShippedDefaultBaseline_RunsUnlimitedByDefault_AndKeepsTheCallFuse()
    {
        var baseline = LocateShippedBaseline();
        Assert.True(File.Exists(baseline), $"Shipped baseline TOML was not found at {baseline}.");

        var snapshot = AgentRuntimeConfigurationStore.LoadSnapshot(baseline, version: 1);

        // Rounds are a safety fuse, not the convergence mechanism: the shipped
        // baseline runs unlimited and every category override follows it. The
        // absolute call fuse survives that (it is not a second spelling of the
        // round limit), and so do the budget values this round re-based.
        Assert.Equal(0, snapshot.Tools.MaxToolRounds);
        Assert.Equal(0, snapshot.Tools.Overrides["test"]);
        Assert.Equal(0, snapshot.Tools.Overrides["build"]);
        Assert.Equal(0, snapshot.Tools.Overrides["refactor"]);
        Assert.Equal(0, snapshot.Tools.Overrides["investigate"]);
        Assert.Equal(ToolRuntimePolicy.DefaultMaxToolCalls, snapshot.Tools.MaxToolCalls);
        Assert.Equal(65536, snapshot.Context.DefaultTokenBudget);
        Assert.Equal(128, snapshot.Context.RecentMessageLimit);
        Assert.Equal(ContextPolicy.DefaultRunTokenBudget, snapshot.Context.RunTokenBudget);
        Assert.Equal(48000, snapshot.Triggers.ContextTokenThreshold);
        Assert.True(snapshot.Triggers.ContextTokenThreshold < snapshot.Context.DefaultTokenBudget);
    }

    [Fact]
    public void Parse_RejectsOverrideAbovePerTaskCeiling()
    {
        Assert.Throws<InvalidDataException>(() => LoadSnapshot(
            [.. BaselineToml(), "", "[tools.task_round_overrides]", $"test = {ToolRuntimePolicy.MaxTaskOverrideRounds + 1}"]));
    }

    [Fact]
    public void Parse_NegativeOverride_MeansUnlimited()
    {
        var snapshot = LoadSnapshot([.. BaselineToml(), "", "[tools.task_round_overrides]", "test = -1"]);

        Assert.Equal(-1, snapshot.Tools.Overrides["test"]);
        Assert.Equal(-1, snapshot.Tools.ResolveTaskRoundLimit("test", "low"));
    }

    [Fact]
    public void Parse_NonPositiveRounds_MeanUnlimited_ForGlobalAndOverride()
    {
        var snapshot = LoadSnapshot(
            [.. BaselineToml(maxToolRounds: -2), "", "[tools.task_round_overrides]", "test = 0", "high = 8"]);

        Assert.Equal(-2, snapshot.Tools.MaxToolRounds);
        Assert.Equal(0, snapshot.Tools.ResolveTaskRoundLimit("test", "low"));
        // A positive override still narrows one class while the global default stays open.
        Assert.Equal(8, snapshot.Tools.ResolveTaskRoundLimit("high", "high"));
        Assert.Equal(-2, snapshot.Tools.ResolveTaskRoundLimit("unknown", "medium"));
    }

    [Fact]
    public void Parse_MissingMaxToolCalls_KeepsTheDefaultFuse()
    {
        var snapshot = LoadSnapshot(BaselineToml());

        Assert.Equal(ToolRuntimePolicy.DefaultMaxToolCalls, snapshot.Tools.MaxToolCalls);
    }

    [Fact]
    public void Parse_UnlimitedRounds_DoesNotRemoveTheCallFuse()
    {
        // The two keys are independent: round convergence is off, the absolute
        // call fuse is not. Conflating them is how an unattended run loses its
        // only hard stop.
        var snapshot = LoadSnapshot(BaselineToml(maxToolRounds: 0));

        Assert.Equal(0, snapshot.Tools.MaxToolRounds);
        Assert.Equal(ToolRuntimePolicy.DefaultMaxToolCalls, snapshot.Tools.MaxToolCalls);
    }

    [Fact]
    public void Parse_RoundFuseAndTokenFuseAreIndependentlyConfigured()
    {
        var snapshot = LoadSnapshot(BaselineToml(maxToolRounds: 12, maxToolCalls: 7, tokenBudget: 4096));

        Assert.Equal(12, snapshot.Tools.MaxToolRounds);
        Assert.Equal(7, snapshot.Tools.MaxToolCalls);
        Assert.Equal(4096, snapshot.Context.DefaultTokenBudget);
    }

    [Fact]
    public void Parse_RejectsNegativeMaxToolCalls()
    {
        Assert.Throws<InvalidDataException>(() => LoadSnapshot(BaselineToml(maxToolCalls: -1)));
    }

    [Fact]
    public void Parse_RejectsCompressionThresholdAtOrAboveContextBudget()
    {
        // The two settings are one mechanism: if the threshold reaches the budget,
        // compaction either never fires or always fires. The load must fail with
        // both keys named instead of letting a run discover the drift mid-flight.
        var failure = Assert.Throws<InvalidDataException>(() => LoadSnapshot(
            [.. BaselineToml(tokenBudget: 8192), "", "[triggers]", "enabled = true", "context_token_threshold = 8192",
                "compress_on_task_closed = false", "recommend_on_task_created = false", "curate_on_run_closed = false",
                "git_steward_on_run_closed = false"]));

        Assert.Contains("context_token_threshold", failure.Message, StringComparison.Ordinal);
        Assert.Contains("default_token_budget", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsCompressionThresholdBelowContextBudget()
    {
        var snapshot = LoadSnapshot(
            [.. BaselineToml(tokenBudget: 8192), "", "[triggers]", "enabled = true", "context_token_threshold = 6000",
                "compress_on_task_closed = false", "recommend_on_task_created = false", "curate_on_run_closed = false",
                "git_steward_on_run_closed = false"]);

        Assert.Equal(6000, snapshot.Triggers.ContextTokenThreshold);
        Assert.Equal(8192, snapshot.Context.DefaultTokenBudget);
    }

    [Fact]
    public void Validate_RejectsAboveCeilingAndKeepsGlobalMaximum()
    {
        Assert.Throws<InvalidDataException>(() => ToolRuntimePolicy.Validate(Policy(maxRounds: 4, overrides: new Dictionary<string, int> { ["test"] = ToolRuntimePolicy.MaxTaskOverrideRounds + 1 })));
        // The global default keeps its own, higher safety ceiling.
        ToolRuntimePolicy.Validate(Policy(maxRounds: ToolRuntimePolicy.MaximumRounds));
        // Every non-positive value is the documented "unlimited" spelling.
        ToolRuntimePolicy.Validate(Policy(maxRounds: 0));
        ToolRuntimePolicy.Validate(Policy(maxRounds: -1));
        ToolRuntimePolicy.Validate(Policy(maxRounds: 4, overrides: new Dictionary<string, int> { ["test"] = -1 }));
        Assert.Throws<InvalidDataException>(() => ToolRuntimePolicy.Validate(Policy(maxRounds: 4) with { MaxToolCalls = -1 }));
    }

    [Fact]
    public void ResolveTaskRoundLimit_CategoryWinsThenRiskThenDefault()
    {
        var policy = Policy(maxRounds: 4, overrides: new Dictionary<string, int> { ["test"] = 12, ["high"] = 8 });

        Assert.Equal(12, policy.ResolveTaskRoundLimit("test", "high"));
        // Whitespace-only category behaves as absent; the risk class doubles as the key.
        Assert.Equal(8, policy.ResolveTaskRoundLimit("  ", "High"));
        Assert.Equal(4, policy.ResolveTaskRoundLimit("unknown-category", "medium"));
        Assert.Equal(4, policy.ResolveTaskRoundLimit(null, "medium"));
    }

    [Fact]
    public void ResolveTaskRoundLimit_UnmatchedCategoryStillFallsThroughToRisk()
    {
        // A category the baseline never declared must not shadow a matching risk
        // override: the fallback chain is category -> risk -> global default.
        var policy = Policy(maxRounds: 4, overrides: new Dictionary<string, int> { ["high"] = 8 });

        Assert.Equal(8, policy.ResolveTaskRoundLimit("unknown-category", "high"));
    }

    [Fact]
    public void ResolveTaskRoundLimitSource_NamesWhereTheLimitCameFrom()
    {
        var policy = Policy(maxRounds: 4, overrides: new Dictionary<string, int> { ["test"] = 12, ["high"] = 8 });

        Assert.Equal("category_override", policy.ResolveTaskRoundLimitSource("test", "high"));
        Assert.Equal("risk_override", policy.ResolveTaskRoundLimitSource("unknown-category", "high"));
        Assert.Equal("risk_override", policy.ResolveTaskRoundLimitSource("  ", "high"));
        Assert.Equal("global_default", policy.ResolveTaskRoundLimitSource("unknown-category", "medium"));
    }

    [Fact]
    public void ResolveTaskRoundLimit_EmptyOverridesFallsBackToGlobalDefault()
    {
        var policy = Policy(maxRounds: 4, overrides: null);

        Assert.Empty(policy.Overrides);
        Assert.Equal(4, policy.ResolveTaskRoundLimit("test", "high"));
    }

    private static ToolRuntimePolicy Policy(int maxRounds, IReadOnlyDictionary<string, int>? overrides = null) =>
        new("tinadec-tools", true, true, 120, maxRounds, overrides);

    private static string[] BaselineToml(int maxToolRounds = 4, int? maxToolCalls = null, int tokenBudget = 8192)
    {
        var lines = new List<string>
        {
            "schema_version = 1",
            "[spawn]",
            "max_depth = 2",
            "max_agents_per_run = 16",
            "max_parallel_workers = 4",
            "[scheduling]",
            "max_active_runs_per_session = 2",
            "worker_retry_limit = 2",
            "preserve_partial_results = true",
            "[supervision]",
            "required_before_final = true",
            "max_revision_rounds = 2",
            "[context]",
            $"default_token_budget = {tokenBudget}",
            "recent_message_limit = 24",
            "optimistic_revision = true",
            "[memory]",
            "candidate_only = true",
            "retrieval_limit = 8",
            "allowed_scopes = [\"workspace\"]",
            "allowed_kinds = [\"fact\"]",
            "[tools]",
            "provider = \"tinadec-tools\"",
            "mutation_requires_approval = true",
            "serialize_workspace_writes = true",
            "default_timeout_seconds = 120",
            $"max_tool_rounds = {maxToolRounds}"
        };
        if (maxToolCalls is { } fuse) lines.Add($"max_tool_calls = {fuse}");
        return [.. lines];
    }

    private AgentRuntimeConfigurationSnapshot LoadSnapshot(IReadOnlyList<string> lines)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "agent-runtime.toml");
        File.WriteAllLines(path, lines, Encoding.UTF8);
        return AgentRuntimeConfigurationStore.LoadSnapshot(path, version: 1);
    }

    private static string LocateShippedBaseline()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "DmaEA", "Configuration", "default-agent-runtime.toml");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return Path.Combine("DmaEA", "Configuration", "default-agent-runtime.toml");
    }
}
