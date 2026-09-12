using System.Text.Json;
using TinadecCore.AgentConfiguration;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Gate 2 (mode publish) pinning: binding envelopes may only narrow — capability
/// and tool grants outside the template are rejected, spawn budgets above the
/// runtime ceilings are rejected, and the operation-layer effective tool surface
/// must be empty (deny floor). Tool switches legitimately remove tools.
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
            tools ?? ["read_file", "write_file"],
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

    [Fact]
    public void OperationLayerWithEffectiveTools_IsRejected_DenyFloor()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            ModePublishGate.ValidateBindings("mode", [Binding(layer: "operation")], null));
        Assert.Contains("operation_tool_floor_violation", exception.Message);
    }

    [Fact]
    public void OperationLayerWithAllToolsSwitchedOff_Passes()
    {
        ModePublishGate.ValidateBindings("mode",
            [Binding(layer: "operation", tools: ["read_file"], switches: """{"read_file": false}""")], null);
    }

    [Fact]
    public void ExecutionLayerWithTools_Passes()
    {
        ModePublishGate.ValidateBindings("mode", [Binding()], null);
    }

    [Fact]
    public void AbsentEnvelope_IsTolerated()
    {
        ModePublishGate.ValidateBindings("mode", [Binding(envelope: null)], null);
    }
}
