using System.Text.Json;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Gate 2 (mode publish) pinning: binding envelopes may only narrow — capability
/// and tool grants outside the template are rejected, spawn budgets above the
/// runtime ceilings are rejected, and a binding that keeps a workspace-mutating
/// tool must declare a write-level resource grant (otherwise the run would freeze
/// a mutating tool face and deny every call of it before the approval gate is
/// consulted). The operation-layer deny floor no longer exists: a mode may arm its
/// conversation identity with tools, and the write-grant rule above is what applies
/// to it instead. Tool switches legitimately remove tools.
/// </summary>
public sealed class ModePublishGateTests
{
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static ModePublishGate.BindingSubject Binding(
        string layer = "execution",
        string[]? capabilities = null,
        string[]? tools = null,
        string? envelope = null,
        string? switches = null) =>
        new("node1", "agent1", layer,
            capabilities ?? ["task.dispatch"],
            tools ?? ["read_file"],
            switches is null ? default : Json(switches),
            envelope is null ? default : Json(envelope));

    [Fact]
    public void EnvelopeGrantingUnknownCapability_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            ModePublishGate.ValidateBindings("mode", [Binding(envelope: """{"capabilities":["agent.create_temporary"]}""")], null));
        Assert.Contains("envelope_exceeds_boundary", exception.Message);
    }

    [Fact]
    public void EnvelopeGrantingUnknownTool_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            ModePublishGate.ValidateBindings("mode", [Binding(envelope: """{"tools":["shell"]}""")], null));
        Assert.Contains("envelope_exceeds_boundary", exception.Message);
    }

    [Fact]
    public void EnvelopeNarrowing_ToASubset_Passes()
    {
        ModePublishGate.ValidateBindings("mode",
            [Binding(envelope: """{"tools":["read_file"],"capabilities":["task.dispatch"]}""")], null);
    }

    [Fact]
    public void SpawnAboveRuntimeCeiling_IsRejected()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            ModePublishGate.ValidateBindings("mode",
                [Binding(envelope: """{"spawn":{"max_depth":8}}""")],
                new ModePublishGate.SpawnCeilings(MaxDepth: 2, MaxAgentsPerRun: 16, MaxParallelWorkers: 4)));
        Assert.Contains("envelope_exceeds_boundary", exception.Message);
    }

    [Fact]
    public void SpawnWithinRuntimeCeiling_Passes()
    {
        ModePublishGate.ValidateBindings("mode",
            [Binding(envelope: """{"spawn":{"max_depth":2,"max_agents_per_run":4,"max_parallel_workers":2}}""")],
            new ModePublishGate.SpawnCeilings(MaxDepth: 2, MaxAgentsPerRun: 16, MaxParallelWorkers: 4));
    }

    /// <summary>
    /// The operation-layer tool floor is REMOVED BY DESIGN (2026-09-17): a mode may give
    /// its conversation identity a tool surface of its own so the agent that talks to the
    /// user also edits the workspace. An operation layer holding tools is now a supported
    /// configuration. This test replaces the former
    /// <c>OperationLayerWithEffectiveTools_IsRejected_DenyFloor</c>, which pinned the
    /// opposite — the change is deliberate, not a regression.
    /// </summary>
    [Fact]
    public void OperationLayerWithEffectiveTools_Passes_FloorRemoved()
    {
        ModePublishGate.ValidateBindings("mode", [Binding(layer: "operation")], null);
        ModePublishGate.ValidateBindings("mode",
            [Binding(layer: "operation", tools: ["read_file", "file_search"])], null);
    }

    /// <summary>
    /// The floor's replacement is not "no gate for operation" but the SAME gate execution
    /// already faced: a mutating tool needs an explicit write grant. Operation bindings
    /// used to `continue` past this check, so removing the floor without removing that
    /// skip would have let a conversation identity keep write_file while declaring no
    /// write authorization at all.
    /// </summary>
    [Fact]
    public void OperationLayerKeepingMutatingTool_NeedsAWriteGrant_LikeExecution()
    {
        var withoutGrant = Assert.Throws<InvalidDataException>(() =>
            ModePublishGate.ValidateBindings("mode",
                [Binding(layer: "operation", tools: ["write_file"], envelope: """{"resources":{"read":[""]}}""")], null));
        Assert.Contains("mutating_tool_without_write_grant", withoutGrant.Message);
        Assert.Contains("write_file", withoutGrant.Message);

        ModePublishGate.ValidateBindings("mode",
            [Binding(layer: "operation", tools: ["write_file"], envelope: """{"resources":{"write":[""]}}""")], null);
    }

    [Fact]
    public void OperationLayerWithAllToolsSwitchedOff_Passes()
    {
        ModePublishGate.ValidateBindings("mode",
            [Binding(layer: "operation", tools: ["read_file"], switches: """{"read_file": false}""")], null);
    }

    [Fact]
    public void ExecutionLayerWithReadTools_Passes()
    {
        ModePublishGate.ValidateBindings("mode", [Binding()], null);
    }

    [Fact]
    public void AbsentEnvelope_IsToleratedForAReadOnlyFace()
    {
        ModePublishGate.ValidateBindings("mode", [Binding(envelope: null, tools: ["read_file", "file_search"])], null);
    }

    // ── ③ mutating tool face needs a write grant (the self-contradiction guard) ──

    [Fact]
    public void MutatingToolWithoutWriteGrant_IsRejected()
    {
        // No envelope at all: the spawn would freeze write_file while the instance
        // holds no workspace grant, so every call would be denied at run time.
        var missingEnvelope = Assert.Throws<InvalidDataException>(() =>
            ModePublishGate.ValidateBindings("mode", [Binding(tools: ["read_file", "write_file"])], null));
        Assert.Contains("mutating_tool_without_write_grant", missingEnvelope.Message);
        Assert.Contains("write_file", missingEnvelope.Message);

        // Read-only envelope: the level, not the prefix, is what is missing.
        var readOnlyEnvelope = Assert.Throws<InvalidDataException>(() =>
            ModePublishGate.ValidateBindings("mode",
                [Binding(tools: ["git_commit"], envelope: """{"resources":{"read":[""]}}""")], null));
        Assert.Contains("mutating_tool_without_write_grant", readOnlyEnvelope.Message);
        Assert.Contains("git_commit", readOnlyEnvelope.Message);
    }

    [Fact]
    public void MutatingToolWithWriteGrant_Passes()
    {
        ModePublishGate.ValidateBindings("mode",
            [Binding(tools: ["read_file", "write_file", "git_commit"], envelope: """{"resources":{"read":[""],"write":[""]}}""")], null);

        // A prefix-scoped write grant is a write grant.
        ModePublishGate.ValidateBindings("mode",
            [Binding(tools: ["write_file"], envelope: """{"resources":{"write":["src"]}}""")], null);
    }

    [Fact]
    public void MutatingToolSwitchedOff_PassesWithoutWriteGrant()
    {
        ModePublishGate.ValidateBindings("mode",
            [Binding(tools: ["write_file"], envelope: null, switches: """{"write_file": false}""")], null);
    }
}
