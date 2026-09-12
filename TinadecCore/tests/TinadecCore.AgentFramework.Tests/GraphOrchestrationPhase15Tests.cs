using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;
using TinadecCore.Runtime;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Phase 1.5 pinning: the frozen Graph section only exists for modes with declared
/// edges (empty-array = free-form, canonical bytes unchanged — the dual-read
/// window), the drift corpus (office shape / vibe fixture / synthetic divergence)
/// pins the shadow resolver's zero-drift and detectability guarantees, and the
/// edge-dispatch authority plus the lanes×graph freeze rule fail closed.
/// </summary>
public sealed class GraphOrchestrationPhase15Tests
{
    private static RuntimeAgentDefinition Agent(string id, string layer, string[]? capabilities = null, string[]? tools = null) =>
        new(id, layer, "task_executor", "on_demand", capabilities ?? [], DirectUserOutput: false, ContextAccess: "read")
        { AllowedTools = tools ?? [] };

    private static FrozenGraphNode Node(string key, string slug, string layer, bool isConversation = false) =>
        new(key, slug, layer, isConversation);

    private static FormalModeRoster Roster(
        IReadOnlyList<RuntimeAgentRosterEntry> operation,
        IReadOnlyList<RuntimeAgentRosterEntry> execution,
        IReadOnlyList<FrozenGraphNode> nodes,
        IReadOnlyList<DeclaredGraphEdge> edges,
        string? conversationKey,
        string? conversationSlug) =>
        new(operation, execution, Guid.NewGuid(), 1, "topology-hash", "profile")
        {
            Edges = edges,
            HasDeclaredEdges = edges.Count > 0,
            GraphNodes = nodes.Select(node => new DeclaredGraphNode(node.NodeKey, node.AgentSlug, node.Layer)).ToArray(),
            ConversationNodeKey = conversationKey,
            ConversationTemplateSlug = conversationSlug
        };

    private static RuntimeAgentRosterEntry Entry(string id, string layer, string lifecycle, bool directUserOutput, string contextAccess) =>
        new(id, layer, "role", lifecycle, ["task.dispatch"], directUserOutput, contextAccess, [], "");

    // ── C: frozen Graph section — dual-read + canonical stability ──────────────

    private static FrozenRunConfigurationV1 MinimalConfiguration(FrozenGraph? graph) => new(
        "frozen-run-configuration/v1", "baseline-hash", 1,
        "conversation", "vibe", "profile-id", "ask",
        new SpawnPolicy(2, 16, 4),
        new SchedulingPolicy(2, 2, true),
        new SupervisionPolicy(true, 2),
        new ContextPolicy(8192, 24, true),
        new MemoryPolicy(true, 8, ["workspace"], ["fact"]),
        new ToolRuntimePolicy("tinadec-tools-process", true, true, 120, 4),
        [Agent("meeting", "operation")],
        [Agent("worker.search", "execution")],
        null,
        [],
        "")
    { Graph = graph };

    [Fact]
    public void FreeFormModes_WriteNoGraphKey_CanonicalBytesStable()
    {
        var body = JsonSerializer.Serialize(MinimalConfiguration(null), FrozenRunConfigurationV1.JsonOptions);
        Assert.DoesNotContain("\"graph\":", body, StringComparison.Ordinal);

        // Dual-read: a body frozen before the section existed deserializes with
        // Graph = null and the engine takes the legacy path.
        var roundTripped = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(body, FrozenRunConfigurationV1.JsonOptions);
        Assert.NotNull(roundTripped);
        Assert.Null(roundTripped.Graph);
    }

    [Fact]
    public void GraphSection_RoundTrips_TierNodesAndEdges()
    {
        var graph = new FrozenGraph(
            FrozenGraphTiers.SelfDispatch, "meeting", "meeting-1",
            [Node("meeting-1", "meeting", "operation", isConversation: true), Node("worker-1", "worker.search", "execution")],
            [new DeclaredGraphEdge("e1", "meeting-1", "worker-1")]);
        var body = JsonSerializer.Serialize(MinimalConfiguration(graph), FrozenRunConfigurationV1.JsonOptions);
        Assert.Contains("\"graph\"", body, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(body, FrozenRunConfigurationV1.JsonOptions);
        Assert.NotNull(roundTripped?.Graph);
        Assert.Equal(FrozenGraphTiers.SelfDispatch, roundTripped.Graph.Tier);
        Assert.Equal("meeting", roundTripped.Graph.ConversationTemplateSlug);
        Assert.Equal(2, roundTripped.Graph.Nodes.Count);
        Assert.Single(roundTripped.Graph.Edges);
    }

    // ── Tier rules ─────────────────────────────────────────────────────────────

    [Fact]
    public void TierRule_TemporaryOrAliasHolder_IsSelfDispatch_PersistentOnlyIsDeterministic()
    {
        var temporary = new[] { Agent("meeting", "operation", ["user.respond", "agent.create_temporary"]) };
        var alias = new[] { Agent("meeting", "operation", ["user.respond", "agent.spawn"]) };
        var persistentOnly = new[] { Agent("meeting", "operation", ["user.respond", "agent.create_persistent"]) };

        Assert.Equal(FrozenGraphTiers.SelfDispatch, AgentRuntimeConfigurationResolver.DeriveGraphTier(temporary, "meeting"));
        Assert.Equal(FrozenGraphTiers.SelfDispatch, AgentRuntimeConfigurationResolver.DeriveGraphTier(alias, "meeting"));
        Assert.Equal(FrozenGraphTiers.Deterministic, AgentRuntimeConfigurationResolver.DeriveGraphTier(persistentOnly, "meeting"));
    }

    // ── A: drift corpus (office shape is edge-less and never enters; vibe zero-drift; divergence detectable) ──

    /// <summary>Vibe-shaped corpus: meeting + two declared dispatch targets. Zero drift.</summary>
    [Fact]
    public void VibeCorpus_ZeroDrift()
    {
        var operation = new[] { Entry("meeting", "operation", "session", directUserOutput: true, contextAccess: "manage") };
        var execution = new[]
        {
            Entry("worker.search", "execution", "on_demand", false, "read"),
            Entry("worker.global_engineering", "execution", "on_demand", false, "read"),
        };
        var roster = Roster(
            operation, execution,
            [Node("meeting-1", "meeting", "operation"), Node("search-1", "worker.search", "execution"), Node("eng-1", "worker.global_engineering", "execution")],
            [new DeclaredGraphEdge("e1", "meeting-1", "search-1"), new DeclaredGraphEdge("e2", "meeting-1", "eng-1")],
            "meeting-1", "meeting");

        var derived = GraphModeResolver.DeriveOverrides(roster);
        var drift = GraphModeResolver.Compare(roster, derived);
        Assert.True(drift.IsEmpty, string.Join("; ", drift.Differences));
    }

    /// <summary>Synthetic divergence: the explicit conversation marker points at a
    /// non-meeting node — legacy semantics say meeting, graph semantics disagree,
    /// and the shadow report MUST catch it (anti-vacuous pin).</summary>
    [Fact]
    public void SyntheticDivergence_IsDetected_AndActiveAppliesWhileShadowKeepsLegacy()
    {
        var operation = new[]
        {
            Entry("meeting", "operation", "session", directUserOutput: true, contextAccess: "manage"),
            new RuntimeAgentRosterEntry("orchestrator", "operation", "role", "persistent", ["task.dispatch"], false, "read", [], "")
            {
                ModelStrategySource = "user_binding"
            },
        };
        var execution = new[] { Entry("worker.search", "execution", "on_demand", false, "read") };
        var roster = Roster(
            operation, execution,
            [Node("orchestrator-1", "orchestrator", "operation"), Node("search-1", "worker.search", "execution")],
            [new DeclaredGraphEdge("e1", "orchestrator-1", "search-1")],
            "orchestrator-1", "orchestrator");

        var derived = GraphModeResolver.DeriveOverrides(roster);
        var drift = GraphModeResolver.Compare(roster, derived);
        Assert.False(drift.IsEmpty);
        Assert.Contains(drift.Differences, item => item.Contains("meeting.DirectUserOutput", StringComparison.Ordinal));
        Assert.Contains(drift.Differences, item => item.Contains("orchestrator.DirectUserOutput", StringComparison.Ordinal));

        // Shadow keeps the legacy roster untouched; active applies the derived semantics.
        Assert.True(roster.Operation.Single(item => item.Id == "orchestrator").DirectUserOutput == false);

        var active = GraphModeResolver.ApplyOverrides(roster, derived);
        Assert.True(active.Operation.Single(item => item.Id == "orchestrator").DirectUserOutput);
        Assert.Equal("manage", active.Operation.Single(item => item.Id == "orchestrator").ContextAccess);
        Assert.Equal("session", active.Operation.Single(item => item.Id == "orchestrator").Lifecycle);
        // The policy stack is never overridden by the graph layer.
        Assert.Equal("user_binding", active.Operation.Single(item => item.Id == "orchestrator").ModelStrategySource);
    }

    // ── B: edge-dispatch authority (pure) ──────────────────────────────────────

    private static FrozenGraph VibeGraph() => new(
        FrozenGraphTiers.SelfDispatch, "meeting", "meeting-1",
        [Node("meeting-1", "meeting", "operation", isConversation: true), Node("search-1", "worker.search", "execution"), Node("eng-1", "worker.global_engineering", "execution")],
        [new DeclaredGraphEdge("e1", "meeting-1", "search-1"), new DeclaredGraphEdge("e2", "meeting-1", "eng-1")]);

    [Fact]
    public void DispatchAuthority_DeclaredTargetsAllowed_UndeclaredRejected()
    {
        var graph = VibeGraph();
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "worker.search"));
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "worker.global_engineering"));
        // worker.search → worker.global_engineering has no declared edge: rejected.
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(graph, "worker.git"));
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(graph, ""));
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(null, "anything")); // free-form never consults
    }

    // ── B: lanes × graph freeze rule ───────────────────────────────────────────

    [Fact]
    public void FreezeGate_GraphTierWithLanesEnabled_Rejected_AtAdmission()
    {
        var operation = new[] { Agent("meeting", "operation", ["user.respond"]) };
        var execution = new[] { Agent("worker.search", "execution") };
        var identity = new RunFreezeGate.ConversationIdentity("meeting-1", "meeting");

        var exception = Assert.Throws<RunAdmissionException>(() =>
            RunFreezeGate.Validate(identity, operation, execution, VibeGraph(), lanesEnabled: true));
        Assert.Equal("graph_tier_lanes_unsupported", exception.Code);

        // The passing side: graph tier with lanes disabled (the vibe default).
        RunFreezeGate.Validate(identity, operation, execution, VibeGraph(), lanesEnabled: false);
        // Legacy: no graph, lanes on → untouched.
        RunFreezeGate.Validate(identity, operation, execution, null, lanesEnabled: true);
    }
}
