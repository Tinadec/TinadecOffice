using System.Text;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Per-task tool-round overrides: TOML parse, freeze-time validation ceiling,
/// and the effective-limit resolution the worker tool loop consumes.
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
    public void Parse_ShippedDefaultBaselineCarriesTestOverride()
    {
        var baseline = LocateShippedBaseline();
        Assert.True(File.Exists(baseline), $"Shipped baseline TOML was not found at {baseline}.");

        var snapshot = AgentRuntimeConfigurationStore.LoadSnapshot(baseline, version: 1);

        Assert.Equal(12, snapshot.Tools.Overrides["test"]);
        Assert.Equal(4, snapshot.Tools.MaxToolRounds);
    }

    [Fact]
    public void Parse_RejectsOverrideAbovePerTaskCeiling()
    {
        Assert.Throws<InvalidDataException>(() => LoadSnapshot([.. BaselineToml(), "", "[tools.task_round_overrides]", "test = 13"]));
    }

    [Fact]
    public void Parse_RejectsNegativeOverride()
    {
        Assert.Throws<InvalidDataException>(() => LoadSnapshot([.. BaselineToml(), "", "[tools.task_round_overrides]", "test = -1"]));
    }

    [Fact]
    public void Validate_RejectsAboveCeilingAndKeepsGlobalMaximum()
    {
        Assert.Throws<InvalidDataException>(() => ToolRuntimePolicy.Validate(Policy(maxRounds: 4, overrides: new Dictionary<string, int> { ["test"] = ToolRuntimePolicy.MaxTaskOverrideRounds + 1 })));
        // The global default keeps its own, higher safety ceiling.
        ToolRuntimePolicy.Validate(Policy(maxRounds: ToolRuntimePolicy.MaximumRounds));
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
    public void ResolveTaskRoundLimit_EmptyOverridesFallsBackToGlobalDefault()
    {
        var policy = Policy(maxRounds: 4, overrides: null);

        Assert.Empty(policy.Overrides);
        Assert.Equal(4, policy.ResolveTaskRoundLimit("test", "high"));
    }

    private static ToolRuntimePolicy Policy(int maxRounds, IReadOnlyDictionary<string, int>? overrides = null) =>
        new("tinadec-tools", true, true, 120, maxRounds, overrides);

    private static string[] BaselineToml() =>
    [
        "schema_version = 1",
        "[spawn]",
        "max_depth = 2",
        "max_agents_per_run = 8",
        "max_parallel_workers = 4",
        "[scheduling]",
        "max_active_runs_per_session = 2",
        "worker_retry_limit = 2",
        "preserve_partial_results = true",
        "[supervision]",
        "required_before_final = true",
        "max_revision_rounds = 2",
        "[context]",
        "default_token_budget = 8192",
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
        "max_tool_rounds = 4"
    ];

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
