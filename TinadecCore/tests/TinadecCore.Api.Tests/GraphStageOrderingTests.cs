using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Two runtime defects pinned by unit tests, both invisible without them:
///
/// ① ROUTE-A STAGE ORDERING. <c>GraphEdgeAuthority.IsDispatchAllowed</c> used to permit only
///   a worker whose incoming edge came from the conversation node or from an
///   <c>operation</c>-layer node, so a legitimate <c>execution -&gt; execution</c> stage edge
///   (<c>leader -&gt; m1 -&gt; m2</c>) was rejected and every pipeline mode threw
///   WorkerAssignmentException at dispatch. Reachability is now transitive forward from the
///   roots, while <c>execution -&gt; operation</c> backflow stays prohibited.
///
/// ② EXPLICIT TIER. <c>DeriveGraphTier</c> never read <c>OrchestrationTier</c>, so a mode that
///   declared <c>deterministic</c> still ran as whatever its tool face implied. An explicit
///   tier now wins outright, and the Core self-configuration tools no longer count as
///   "the master does the work".
/// </summary>
public sealed class GraphStageOrderingTests
{
    // ── ① route-A stage ordering ────────────────────────────────────────────────

    /// <summary>
    /// The pipeline shape: conversation → m1 → m2, all execution workers chained. Both
    /// workers must be dispatchable, which is exactly what the old single-hop rule refused
    /// for m2.
    /// </summary>
    [Fact]
    public void DispatchAuthority_ExecutionToExecutionStageEdge_IsAllowed()
    {
        var graph = PipelineGraph();

        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "m1"));
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "m2"), "a chained execution->execution stage target must be dispatchable");
        Assert.True(GraphEdgeAuthority.IsDispatchAllowed(graph, "m3"), "reachability is transitive, not single-hop");
        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(graph, "off_graph_worker"));
    }

    /// <summary>
    /// Backflow is the one direction the walk must never produce: an executor cannot hand
    /// work UP the graph. <c>m1 -&gt; reviewer</c> (execution → operation) must not make
    /// <c>reviewer</c> a dispatch target, and the direction predicate refuses it outright.
    /// </summary>
    [Fact]
    public void DispatchAuthority_ExecutionToOperationBackflow_IsRejected()
    {
        var graph = PipelineGraph(withBackflow: true);

        Assert.False(GraphEdgeAuthority.IsDispatchAllowed(graph, "reviewer"),
            "an operation node reached only by execution backflow must never be dispatchable");
        Assert.False(GraphEdgeAuthority.IsEdgeDirectionAllowed("execution", "operation"));
        // The legal directions stay legal.
        Assert.True(GraphEdgeAuthority.IsEdgeDirectionAllowed("operation", "execution"));
        Assert.True(GraphEdgeAuthority.IsEdgeDirectionAllowed("operation", "operation"));
        Assert.True(GraphEdgeAuthority.IsEdgeDirectionAllowed("execution", "execution"));
    }

    /// <summary>
    /// The chained edges are surfaced to the planner as ordered stage dependencies — that is
    /// what tells the planner to make the downstream task depend on the upstream one instead
    /// of running both in parallel.
    /// </summary>
    [Fact]
    public void StageDependencies_ReportExecutionToExecutionEdgesInOrder()
    {
        var dependencies = GraphEdgeAuthority.StageDependencies(PipelineGraph());

        Assert.Equal(2, dependencies.Count);
        Assert.Contains(dependencies, stage => stage.PredecessorSlug == "m1" && stage.SuccessorSlug == "m2" && stage.EdgeKey == "m1->m2");
        Assert.Contains(dependencies, stage => stage.PredecessorSlug == "m2" && stage.SuccessorSlug == "m3" && stage.EdgeKey == "m2->m3");
        // leader->m1 is an operation->execution hand-off, not a stage dependency.
        Assert.DoesNotContain(dependencies, stage => stage.PredecessorSlug == "leader");

        // Deterministic output: two reads of the same frozen graph are byte-identical, so the
        // planner prompt does not churn between attempts.
        Assert.Equal(dependencies.Select(stage => stage.PredecessorSlug + ">" + stage.SuccessorSlug),
            GraphEdgeAuthority.StageDependencies(PipelineGraph()).Select(stage => stage.PredecessorSlug + ">" + stage.SuccessorSlug));
    }

    /// <summary>A graph with no stage edges contributes no stage instructions at all.</summary>
    [Fact]
    public void StageDependencies_EmptyWhenNoExecutionChainExists()
    {
        var graph = new FrozenGraph(FrozenGraphTiers.Deterministic, "leader", "leader-node",
            [Node("leader-node", "leader", "operation", isConversation: true), Node("m1", "m1", "execution")],
            [new DeclaredGraphEdge("leader->m1", "leader-node", "m1")]);

        Assert.Empty(GraphEdgeAuthority.StageDependencies(graph));
        Assert.Empty(GraphEdgeAuthority.StageDependencies(null));
    }

    // ── ② explicit tier ────────────────────────────────────────────────────────

    /// <summary>
    /// Every declared tier wins over what inference would have chosen. Each case is built so
    /// inference would pick something DIFFERENT, which is the only way the assertion proves
    /// the parameter is read.
    /// </summary>
    [Theory]
    // Inference would say solo_dispatch (the conversation identity holds a tool face).
    [InlineData("deterministic", "meeting", true, FrozenGraphTiers.Deterministic)]
    [InlineData("self_dispatch", "meeting", true, FrozenGraphTiers.SelfDispatch)]
    [InlineData("free_form", "meeting", true, FrozenGraphTiers.FreeForm)]
    [InlineData("solo_dispatch", "meeting", true, FrozenGraphTiers.SoloDispatch)]
    // Inference would say free_form (no declared edges, no conversation tools).
    [InlineData("deterministic", "meeting", false, FrozenGraphTiers.Deterministic)]
    [InlineData("self_dispatch", "meeting", false, FrozenGraphTiers.SelfDispatch)]
    [InlineData("solo_dispatch", "meeting", false, FrozenGraphTiers.SoloDispatch)]
    // Inference would say deterministic (edges, no spawn authority, no tools).
    [InlineData("free_form", "silent", true, FrozenGraphTiers.FreeForm)]
    [InlineData("self_dispatch", "silent", true, FrozenGraphTiers.SelfDispatch)]
    // The alias the editor writes folds onto the canonical name.
    [InlineData("solo", "meeting", true, FrozenGraphTiers.SoloDispatch)]
    [InlineData("SOLO", "meeting", false, FrozenGraphTiers.SoloDispatch)]
    [InlineData(" Deterministic ", "silent", true, FrozenGraphTiers.Deterministic)]
    public void DeriveGraphTier_ExplicitTierWinsOverInference(string declaredTier, string conversationSlug, bool holdsTools, string expected)
    {
        var operation = new[]
        {
            Agent("meeting", "operation", ["user.respond"], holdsTools ? ["read_file", "write_file"] : []),
            Agent("silent", "operation", ["user.respond"], []),
        };

        Assert.Equal(expected,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(operation, hasDeclaredEdges: true, conversationSlug, declaredTier));
    }

    /// <summary>
    /// Null/blank/unrecognized declarations fall back to the historical inference — an old mode
    /// that never opted in must keep its old tier.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not_a_tier")]
    public void DeriveGraphTier_WithoutAUsableTier_FallsBackToInference(string? declaredTier)
    {
        // A conversation identity holding a real tool face still infers solo_dispatch.
        var executor = new[] { Agent("meeting", "operation", ["user.respond"], ["read_file"]) };
        Assert.Equal(FrozenGraphTiers.SoloDispatch,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(executor, hasDeclaredEdges: true, "meeting", declaredTier));

        // An empty tool face with no declared edges still infers free_form.
        var director = new[] { Agent("meeting", "operation", ["user.respond"], []) };
        Assert.Equal(FrozenGraphTiers.FreeForm,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(director, hasDeclaredEdges: false, "meeting", declaredTier));
    }

    /// <summary>
    /// The self-configuration tools are a governance affordance, not a work surface: a mode
    /// whose conversation identity holds only <c>tinadec_*</c> tools must NOT be silently
    /// degraded to solo_dispatch. A real tool alongside them still makes it solo.
    /// </summary>
    [Fact]
    public void DeriveGraphTier_TinadecConfigToolsDoNotCountAsAToolSurface()
    {
        var configOnly = new[] { Agent("meeting", "operation", ["user.respond"], [CoreVirtualToolPolicy.TinadecInspectToolId]) };
        // No edges + no executor face → free_form, exactly as if it held nothing.
        Assert.Equal(FrozenGraphTiers.FreeForm,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(configOnly, hasDeclaredEdges: false, "meeting"));

        var allConfigTools = new[]
        {
            Agent("meeting", "operation", ["user.respond"], [.. CoreVirtualToolPolicy.TinadecConfigToolIds])
        };
        Assert.Equal(FrozenGraphTiers.FreeForm,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(allConfigTools, hasDeclaredEdges: false, "meeting"));

        // One genuine tool alongside them still flips it to solo: the master really does work.
        var mixed = new[] { Agent("meeting", "operation", ["user.respond"], [CoreVirtualToolPolicy.TinadecInspectToolId, "write_file"]) };
        Assert.Equal(FrozenGraphTiers.SoloDispatch,
            AgentRuntimeConfigurationResolver.DeriveGraphTier(mixed, hasDeclaredEdges: false, "meeting"));
    }

    /// <summary>
    /// The mapping itself, for callers that only need the name translation (the resolver reads
    /// the snapshot's declared string and hands it to the freeze).
    /// </summary>
    [Theory]
    [InlineData("solo", FrozenGraphTiers.SoloDispatch)]
    [InlineData("solo_dispatch", FrozenGraphTiers.SoloDispatch)]
    [InlineData("free_form", FrozenGraphTiers.FreeForm)]
    [InlineData("self_dispatch", FrozenGraphTiers.SelfDispatch)]
    [InlineData("deterministic", FrozenGraphTiers.Deterministic)]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MapDeclaredTier_TranslatesTheEditorVocabulary(string? declared, string? expected) =>
        Assert.Equal(expected, AgentRuntimeConfigurationResolver.MapDeclaredTier(declared));

    /// <summary>
    /// The snapshot reader that feeds the freeze: a published snapshot's declared tier and
    /// instructions are read back, and a snapshot published before the fields existed reads as
    /// null rather than failing.
    /// </summary>
    [Fact]
    public void SnapshotDocuments_ReadTierAndInstructions_AndTolerateLegacyBodies()
    {
        const string modern = """{"schema":"tinadec.mode_version/v1","orchestration_tier":"solo","instructions":"  Keep it short.  ","nodes":[],"edges":[]}""";
        Assert.Equal(FrozenGraphTiers.SoloDispatch, FormalModeSnapshotDocuments.ReadOrchestrationTier(modern));
        Assert.Equal("Keep it short.", FormalModeSnapshotDocuments.ReadInstructions(modern));

        const string legacy = """{"schema":"tinadec.mode_version/v1","nodes":[],"edges":[]}""";
        Assert.Null(FormalModeSnapshotDocuments.ReadOrchestrationTier(legacy));
        Assert.Null(FormalModeSnapshotDocuments.ReadInstructions(legacy));

        // Malformed bodies degrade instead of throwing on a read.
        Assert.Null(FormalModeSnapshotDocuments.ReadOrchestrationTier("{not json"));
        Assert.Null(FormalModeSnapshotDocuments.ReadInstructions(null));
        Assert.Null(FormalModeSnapshotDocuments.ReadOrchestrationTier("""{"orchestration_tier":"nonsense"}"""));
    }

    // ── fixtures ───────────────────────────────────────────────────────────────

    /// <summary>
    /// leader (operation) → m1 → m2 → m3 (execution chain), plus an off-graph worker.
    /// <paramref name="withBackflow"/> adds m1 → reviewer, an execution→operation edge.
    /// </summary>
    private static FrozenGraph PipelineGraph(bool withBackflow = false)
    {
        var nodes = new List<FrozenGraphNode>
        {
            Node("leader-node", "leader", "operation", isConversation: true),
            Node("m1", "m1", "execution"),
            Node("m2", "m2", "execution"),
            Node("m3", "m3", "execution"),
            Node("off-graph", "off_graph_worker", "execution"),
        };
        var edges = new List<DeclaredGraphEdge>
        {
            new("leader->m1", "leader-node", "m1"),
            new("m1->m2", "m1", "m2"),
            new("m2->m3", "m2", "m3"),
        };
        if (withBackflow)
        {
            nodes.Add(Node("reviewer-node", "reviewer", "operation"));
            edges.Add(new DeclaredGraphEdge("m1->reviewer", "m1", "reviewer-node"));
        }
        return new FrozenGraph(FrozenGraphTiers.Deterministic, "leader", "leader-node", nodes, edges);
    }

    private static FrozenGraphNode Node(string key, string slug, string layer, bool isConversation = false) =>
        new(key, slug, layer, isConversation);

    private static RuntimeAgentDefinition Agent(string id, string layer, string[]? capabilities = null, string[]? tools = null) =>
        new(id, layer, "task_executor", "on_demand", capabilities ?? [], DirectUserOutput: false, ContextAccess: "read")
        { AllowedTools = tools ?? [] };
}
