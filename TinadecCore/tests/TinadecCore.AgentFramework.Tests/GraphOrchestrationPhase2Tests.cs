using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Phase 2 pinning: schema v2 freezes a Graph section for EVERY mode (free_form
/// is a tier on disk, not the absence of one), the tier derivation is the
/// four-branch decision (conversation identity holds tools → solo_dispatch;
/// no edges → free_form; edges + spawn authority → self_dispatch; edges without
/// spawn authority → deterministic), the dispatch and spawn authorities are
/// tier-aware pure functions, and the freeze gate accepts the free-form
/// single-director shape and the tool-holding solo master while still rejecting
/// lanes×graph.
/// </summary>
public sealed class GraphOrchestrationPhase2Tests
{
    private static RuntimeAgentDefinition Agent(string id, string layer, string[]? capabilities = null, string[]? tools = null) =>
        new(id, layer, "task_executor", "on_demand", capabilities ?? [], DirectUserOutput: false, ContextAccess: "read")
        { AllowedTools = tools ?? [] };

    private static FrozenGraphNode Node(string key, string slug, string layer, bool isConversation = false) =>
        new(key, slug, layer, isConversation);

    private static FrozenSpawnableTemplate Template(string slug, string[]? tools = null, string[]? capabilities = null) =>
        new(slug, Guid.NewGuid(), Guid.NewGuid(), "hash-" + slug, "task_executor",
            tools ?? ["read_file"], capabilities ?? ["task.dispatch"]);

    // ── coverage tie-break: an open-ended task goes to the WIDEST template ─────

    /// <summary>
    /// An open-ended task (no declared tools or capabilities) has nothing to narrow
    /// against, so "fewest tools" picked the NARROWEST template. That is how a real
    /// run's request to write a file was handed to the read-only search worker, which
    /// then reported it had no way to do the job while the run closed as completed.
    /// With no requirement left to satisfy, breadth is the only signal — and the
    /// honest one.
    /// </summary>
    [Fact]
    public void FindCoverage_OpenEndedTask_PrefersTheWidestTemplate()
    {
        var graph = SpawnableGraph(
            Template("search", ["read_file", "file_search", "ls"]),
            Template("global_engineering", ["read_file", "write_file", "shell", "git_commit"]));

        var coverage = GraphSpawnAuthority.FindCoverage(graph, [], []);

        Assert.NotNull(coverage);
        Assert.Equal("global_engineering", coverage!.Slug);
    }

    [Fact]
    public void FindCoverage_OpenEndedTask_PrefersAWildcardCeiling()
    {
        var graph = SpawnableGraph(
            Template("specialist", ["read_file", "write_file", "shell", "git_commit"]),
            Template("generalist", ["*"]));

        var coverage = GraphSpawnAuthority.FindCoverage(graph, [], []);

        Assert.NotNull(coverage);
        Assert.Equal("generalist", coverage!.Slug);
    }

    /// <summary>
    /// A task that DID declare requirements is already covered: among the templates
    /// that cover it, the narrowest ceiling stays the least-privilege choice.
    /// </summary>
    [Fact]
    public void FindCoverage_DeclaredRequirements_KeepTheNarrowestCoveringTemplate()
    {
        var graph = SpawnableGraph(
            Template("search", ["read_file", "file_search"]),
            Template("global_engineering", ["read_file", "write_file", "shell"]));

        var coverage = GraphSpawnAuthority.FindCoverage(graph, ["read_file"], []);

        Assert.NotNull(coverage);
        Assert.Equal("search", coverage!.Slug);
    }

    private static FrozenGraph SpawnableGraph(params FrozenSpawnableTemplate[] templates) => new(
        FrozenGraphTiers.FreeForm, "meeting", "meeting",
        [Node("meeting", "meeting", "operation", isConversation: true)], [])
    {
        SpawnableTemplates = templates
    };

    private static FrozenRunConfigurationV1 MinimalConfiguration(FrozenGraph? graph) => new(
        FrozenRunConfigurationV1.CurrentSchemaVersion, "baseline-hash", 1,
        Guid.Parse("00000000-0000-0000-0000-0000000000cc"), "profile-id", "ask",
        new SpawnPolicy(2, 16, 4),
        new SchedulingPolicy(2, 2, true),
        new SupervisionPolicy(true, 2),
        new ContextPolicy(8192, 24, true),
        new MemoryPolicy(true, 8, ["workspace"], ["fact"]),
        new ToolRuntimePolicy("tinadec-tools-process", true, true, 120, 4),
        [Agent("meeting", "operation", ["user.respond", "agent.create_temporary"])],
        [Agent("search", "execution", tools: ["mcp_search", "read_file"])],
        [],
        "")
    { Graph = graph };

    // ── every mode freezes a Graph section; schema v3 replaced the mode string pair ──

    [Fact]
    public void SchemaVersion_Is_V3Literal()
    {
        Assert.Equal("frozen-run-configuration/v3", FrozenRunConfigurationV1.CurrentSchemaVersion);
    }

    [Fact]
    public void GraphBody_WritesGraphSection_AndRoundTripsTierNodesEdges()
    {
        var graph = new FrozenGraph(
            FrozenGraphTiers.SelfDispatch, "meeting", "meeting-1",
            [Node("meeting-1", "meeting", "operation", isConversation: true), Node("search-1", "search", "execution")],
            [new DeclaredGraphEdge("e1", "meeting-1", "search-1")]);
        var body = JsonSerializer.Serialize(MinimalConfiguration(graph), FrozenRunConfigurationV1.JsonOptions);
        Assert.Contains("\"graph\":", body, StringComparison.Ordinal);
        Assert.Contains("\"tier\":\"self_dispatch\"", body, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\":\"frozen-run-configuration/v3\"", body, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(body, FrozenRunConfigurationV1.JsonOptions);
        Assert.NotNull(roundTripped?.Graph);
        Assert.Equal(FrozenGraphTiers.SelfDispatch, roundTripped.Graph.Tier);
        Assert.Equal("meeting", roundTripped.Graph.ConversationTemplateSlug);
        Assert.Equal(2, roundTripped.Graph.Nodes.Count);
        Assert.Single(roundTripped.Graph.Edges);
        Assert.Equal(FrozenRunConfigurationV1.CurrentSchemaVersion, roundTripped.SchemaVersion);
    }

    [Fact]
    public void FreeFormGraphSection_CarriesTierOnDisk()
    {
        // The free_form shape is a Graph section with no dispatch edges — the
        // phase 1.5 "edge-less modes write no Graph key" guarantee is retired.
        var graph = new FrozenGraph(
            FrozenGraphTiers.FreeForm, "meeting", "meeting-1",
            [Node("meeting-1", "meeting", "operation", isConversation: true)],
            []);
        var body = JsonSerializer.Serialize(MinimalConfiguration(graph), FrozenRunConfigurationV1.JsonOptions);
        Assert.Contains("\"tier\":\"free_form\"", body, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(body, FrozenRunConfigurationV1.JsonOptions);
        Assert.NotNull(roundTripped?.Graph);
        Assert.Empty(roundTripped.Graph.Edges);
    }

    [Fact]
    public void EdgeDataContract_FreezesVerbatim_AndOmitsWhenAbsent()
    {
        var withContract = new DeclaredGraphEdge("e1", "meeting-1", "search-1")
        {
            DataContract = JsonSerializer.SerializeToElement(new { request = new[] { "query" } })
        };
        var withoutContract = new DeclaredGraphEdge("e2", "search-1", "meeting-1");
        var graph = new FrozenGraph(
            FrozenGraphTiers.SelfDispatch, "meeting", "meeting-1",
            [Node("meeting-1", "meeting", "operation", isConversation: true), Node("search-1", "search", "execution")],
            [withContract, withoutContract]);
        var body = JsonSerializer.Serialize(MinimalConfiguration(graph), FrozenRunConfigurationV1.JsonOptions);

        Assert.Contains("\"dataContract\":", body, StringComparison.Ordinal);
        var roundTripped = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(body, FrozenRunConfigurationV1.JsonOptions);
        Assert.NotNull(roundTripped?.Graph);
        Assert.NotNull(roundTripped.Graph.Edges[0].DataContract);
        Assert.Null(roundTripped.Graph.Edges[1].DataContract);
        // (An empty condition object is normalized to null at PARSE time —
        // ParseDeclaredEdges freezes only non-empty contracts.)
    }

    // ── four-branch tier derivation ────────────────────────────────────────────

    [Fact]
    public void TierDerivation_NoEdges_IsFreeForm_RegardlessOfNodeCount()
    {
        var operation = new[] { Agent("meeting", "operation", ["user.respond", "agent.create_temporary"]) };
        var execution = new[] { Agent("search", "execution") };
        Assert.Equal(FrozenGraphTiers.FreeForm,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(operation, hasDeclaredEdges: false, "meeting"));

        // Edge-less + spawn authority is still free_form even with a full roster.
        Assert.Equal(FrozenGraphTiers.FreeForm,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(operation.Concat(execution).ToArray(), hasDeclaredEdges: false, "meeting"));
    }

    /// <summary>
    /// The conversation identity holding a tool surface makes the mode solo, and that
    /// verdict outranks all three historical branches: "the master executes" answers a
    /// different question (who works) than edges and spawn authority do (how dispatch is
    /// shaped). It holds with edges, with spawn authority, and with neither.
    /// </summary>
    [Fact]
    public void TierDerivation_ConversationHoldingTools_IsSoloDispatch_OutranksEveryOtherBranch()
    {
        var holdsTools = Agent("meeting", "operation", ["user.respond", "agent.create_temporary"], ["read_file", "write_file", "shell"]);
        var holdsToolsNoSpawn = Agent("meeting", "operation", ["user.respond"], ["read_file", "write_file"]);

        Assert.Equal(FrozenGraphTiers.SoloDispatch,
            AgentRuntimeConfigurationResolver.DeriveGraphTier([holdsTools], hasDeclaredEdges: true, "meeting"));
        Assert.Equal(FrozenGraphTiers.SoloDispatch,
            AgentRuntimeConfigurationResolver.DeriveGraphTier([holdsTools], hasDeclaredEdges: false, "meeting"));
        // No spawn capability at all: still solo, because edge-less solo must not fall
        // through to free_form — free_form carries no tool surface for the master.
        Assert.Equal(FrozenGraphTiers.SoloDispatch,
            AgentRuntimeConfigurationResolver.DeriveGraphTier([holdsToolsNoSpawn], hasDeclaredEdges: false, "meeting"));
        // A conversation slug that is not in the roster cannot vote: the branch is keyed
        // on the resolved identity, not on a name appearing somewhere.
        Assert.Equal(FrozenGraphTiers.FreeForm,
            AgentRuntimeConfigurationResolver.DeriveGraphTier([holdsTools], hasDeclaredEdges: false, "absent-slug"));
    }

    /// <summary>
    /// Regression guard for the three modes that predate the tier: an EMPTY conversation
    /// tool_scope must reproduce the historical derivation exactly, so fixed_pipeline /
    /// vibe_graph / free_director keep behaving as they did.
    /// </summary>
    [Fact]
    public void TierDerivation_EmptyConversationToolScope_KeepsTheHistoricalBranches()
    {
        var spawn = Agent("meeting", "operation", ["user.respond", "agent.create_temporary"]);
        var noSpawn = Agent("meeting", "operation", ["user.respond"]);

        Assert.Equal(FrozenGraphTiers.SelfDispatch,
            AgentRuntimeConfigurationResolver.DeriveGraphTier([spawn], hasDeclaredEdges: true, "meeting"));
        Assert.Equal(FrozenGraphTiers.Deterministic,
            AgentRuntimeConfigurationResolver.DeriveGraphTier([noSpawn], hasDeclaredEdges: true, "meeting"));
        Assert.Equal(FrozenGraphTiers.FreeForm,
            AgentRuntimeConfigurationResolver.DeriveGraphTier([spawn], hasDeclaredEdges: false, "meeting"));
    }

    /// <summary>
    /// solo_dispatch must be able to hand work off (that tier exists to dispatch
    /// AGGRESSIVELY while the master also works), and its dispatch reach must equal
    /// self_dispatch's: declared edge targets plus spawned-template slugs.
    /// </summary>
    [Fact]
    public void SoloDispatch_CarriesSpawnAuthority_AndDispatchesLikeSelfDispatch()
    {
        Assert.True(GraphSpawnAuthority.CarriesSpawnAuthority(FrozenGraphTiers.SoloDispatch));
        Assert.False(GraphSpawnAuthority.CarriesSpawnAuthority(FrozenGraphTiers.Deterministic));

        var graph = VibeGraph(FrozenGraphTiers.SoloDispatch, [Template("evolved_worker")]);
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "search"));
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "global_engineering"));
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "evolved_worker"));
        // Off-graph and off-whitelist stays denied: solo widens WHO works, not what may
        // be reached.
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(graph, "unrelated_worker"));
    }

    /// <summary>
    /// A solo master needs no execution layer at all — it can do the work itself with
    /// the tools it holds. Every other tier still requires one.
    /// </summary>
    [Fact]
    public void FreezeGate_SoloMasterWithoutExecutionLayer_Passes_OtherTiersStillFail()
    {
        var master = Agent("meeting", "operation", ["user.respond"], ["read_file", "write_file"]);
        var soloGraph = new FrozenGraph(FrozenGraphTiers.SoloDispatch, "meeting", "meeting-1",
            [Node("meeting-1", "meeting", "operation", isConversation: true)], []);

        RunFreezeGate.Validate(new RunFreezeGate.ConversationIdentity("meeting-1", "meeting"), [master], [], soloGraph, lanesEnabled: false);

        var deterministicGraph = new FrozenGraph(FrozenGraphTiers.Deterministic, "meeting", "meeting-1",
            [Node("meeting-1", "meeting", "operation", isConversation: true)], []);
        var error = Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.Validate(new RunFreezeGate.ConversationIdentity("meeting-1", "meeting"), [master], [], deterministicGraph, lanesEnabled: false));
        Assert.Equal("mode_topology_invalid", error.Code);
    }

    [Fact]
    public void TierDerivation_WithEdges_SpawnAuthorityDecides()
    {
        var temporary = new[] { Agent("meeting", "operation", ["user.respond", "agent.create_temporary"]) };
        var alias = new[] { Agent("meeting", "operation", ["user.respond", "agent.spawn"]) };
        var persistentOnly = new[] { Agent("meeting", "operation", ["user.respond", "agent.create_persistent"]) };
        var none = new[] { Agent("meeting", "operation", ["user.respond"]) };

        Assert.Equal(FrozenGraphTiers.SelfDispatch,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(temporary, hasDeclaredEdges: true, "meeting"));
        Assert.Equal(FrozenGraphTiers.SelfDispatch,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(alias, hasDeclaredEdges: true, "meeting"));
        Assert.Equal(FrozenGraphTiers.Deterministic,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(persistentOnly, hasDeclaredEdges: true, "meeting"));
        Assert.Equal(FrozenGraphTiers.Deterministic,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(none, hasDeclaredEdges: true, "meeting"));
    }

    // ── tier-aware dispatch authority ──────────────────────────────────────────

    private static FrozenGraph VibeGraph(string tier = FrozenGraphTiers.SelfDispatch, FrozenSpawnableTemplate[]? spawnable = null) => new(
        tier, "meeting", "meeting-1",
        [Node("meeting-1", "meeting", "operation", isConversation: true), Node("search-1", "search", "execution"), Node("eng-1", "global_engineering", "execution")],
        [new DeclaredGraphEdge("e1", "meeting-1", "search-1"), new DeclaredGraphEdge("e2", "meeting-1", "eng-1")])
    { SpawnableTemplates = spawnable ?? [] };

    [Fact]
    public void DispatchAuthority_DeterministicEdgeTargetsOnly()
    {
        var graph = VibeGraph(FrozenGraphTiers.Deterministic,
            [Template("evolved_worker")]); // deterministic whitelist is inert for dispatch
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "search"));
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "global_engineering"));
        // Spawned-template slug is NOT a dispatch target in the deterministic tier.
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(graph, "evolved_worker"));
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(graph, "worker.git"));
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(graph, ""));
    }

    [Fact]
    public void DispatchAuthority_SelfDispatch_AllowsWhitelistMembersBeyondTheRoster()
    {
        var graph = VibeGraph(FrozenGraphTiers.SelfDispatch, [Template("evolved_worker")]);
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "search"));
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "evolved_worker"));
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(graph, "outside_whitelist"));
    }

    [Fact]
    public void DispatchAuthority_FreeForm_AllowsAnyWorker()
    {
        var graph = VibeGraph(FrozenGraphTiers.FreeForm);
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "search"));
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "anything_at_all"));
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(null, "anything"));
    }

    // ── spawn authority ────────────────────────────────────────────────────────

    [Fact]
    public void SpawnAuthority_DeterministicAlwaysDenied()
    {
        var graph = VibeGraph(FrozenGraphTiers.Deterministic, [Template("search")]);
        Assert.False(GraphSpawnAuthority.IsSpawnAllowed(graph, "search"));
        Assert.False(GraphSpawnAuthority.IsSpawnAllowed(null, "search"));
    }

    [Fact]
    public void SpawnAuthority_WhitelistMembershipDecides()
    {
        var graph = VibeGraph(FrozenGraphTiers.SelfDispatch, [Template("search"), Template("evolved_worker")]);
        Assert.True(GraphSpawnAuthority.IsSpawnAllowed(graph, "search"));
        Assert.True(GraphSpawnAuthority.IsSpawnAllowed(graph, "EVOLVED_WORKER")); // case-insensitive
        Assert.False(GraphSpawnAuthority.IsSpawnAllowed(graph, "outside_whitelist"));
        Assert.False(GraphSpawnAuthority.IsSpawnAllowed(graph, ""));

        // Empty whitelist (no relationship agent_types) denies everything.
        Assert.False(GraphSpawnAuthority.IsSpawnAllowed(VibeGraph(FrozenGraphTiers.SelfDispatch), "search"));
    }

    [Fact]
    public void SpawnSelection_CoversRequirements_AndPrefersClosestFit()
    {
        var broad = Template("broad", tools: ["read_file", "write_file", "shell", "mcp_search"]);
        var narrow = Template("narrow", tools: ["read_file"]);
        var wild = Template("wild", tools: ["*"]);
        var graph = VibeGraph(FrozenGraphTiers.FreeForm, [broad, narrow, wild]);

        Assert.Equal("narrow", GraphSpawnAuthority.SelectSpawnable(graph, ["read_file"], [])!.Slug);
        Assert.Equal("broad", GraphSpawnAuthority.SelectSpawnable(graph, ["read_file", "shell"], [])!.Slug);
        // A wildcard ceiling covers any requirement.
        Assert.Equal("wild", GraphSpawnAuthority.SelectSpawnable(graph, ["mcp_invoke"], [])!.Slug);
        // Capability requirements must be covered too (closest fit still wins).
        Assert.Equal("narrow", GraphSpawnAuthority.SelectSpawnable(graph, ["read_file"], ["task.dispatch"])!.Slug);
        // No coverage → no selection (no wildcard ceiling anywhere).
        var noWildcard = VibeGraph(FrozenGraphTiers.FreeForm, [narrow, broad]);
        Assert.Null(GraphSpawnAuthority.SelectSpawnable(noWildcard, ["git_push"], []));
        Assert.Null(GraphSpawnAuthority.SelectSpawnable(VibeGraph(FrozenGraphTiers.Deterministic, [broad]), ["read_file"], []));
        Assert.Null(GraphSpawnAuthority.SelectSpawnable(null, ["read_file"], []));
    }

    // ── freeze gate: free-form single-director shape + lanes rule ──────────────

    [Fact]
    public void FreezeGate_FreeFormDirectorWithoutExecutionRoster_IsAccepted()
    {
        var director = Agent("meeting", "operation", ["user.respond", "agent.create_temporary", "agent.spawn"]);
        var graph = new FrozenGraph(FrozenGraphTiers.FreeForm, "meeting", "meeting-1",
            [Node("meeting-1", "meeting", "operation", isConversation: true)], []);
        var identity = new RunFreezeGate.ConversationIdentity("meeting-1", "meeting");

        // Empty execution roster is legal only for the free_form director with
        // spawn authority.
        RunFreezeGate.Validate(identity, [director], [], graph, lanesEnabled: false);
    }

    [Fact]
    public void FreezeGate_EmptyExecutionRoster_OtherwiseRejected()
    {
        var coordinator = Agent("meeting", "operation", ["user.respond"]); // no spawn authority
        var graph = new FrozenGraph(FrozenGraphTiers.FreeForm, "meeting", "meeting-1",
            [Node("meeting-1", "meeting", "operation", isConversation: true)], []);
        var identity = new RunFreezeGate.ConversationIdentity("meeting-1", "meeting");

        var exception = Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.Validate(identity, [coordinator], [], graph, lanesEnabled: false));
        Assert.Equal("mode_topology_invalid", exception.Code);

        // Deterministic tier with an empty execution roster is also rejected even
        // when the holder carries spawn caps (edges exist → not free-form).
        var spawnCapable = Agent("meeting", "operation", ["user.respond", "agent.create_temporary"]);
        var edgeGraph = new FrozenGraph(FrozenGraphTiers.Deterministic, "meeting", "meeting-1",
            [Node("meeting-1", "meeting", "operation", isConversation: true)],
            [new DeclaredGraphEdge("e1", "meeting-1", "search-1")]);
        Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.Validate(identity, [spawnCapable], [], edgeGraph, lanesEnabled: false));

        // Legacy null-graph recovery tolerance still rejects an empty roster.
        Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.Validate(null, [coordinator], [], null, lanesEnabled: false));
    }

    [Fact]
    public void FreezeGate_GraphTierWithLanesEnabled_Rejected_AtAdmission()
    {
        var operation = new[] { Agent("meeting", "operation", ["user.respond"]) };
        var execution = new[] { Agent("search", "execution") };
        var identity = new RunFreezeGate.ConversationIdentity("meeting-1", "meeting");

        var exception = Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.Validate(identity, operation, execution, VibeGraph(), lanesEnabled: true));
        Assert.Equal("graph_tier_lanes_unsupported", exception.Code);
    }
}
