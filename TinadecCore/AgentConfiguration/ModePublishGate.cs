using System.Text.Json;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentConfiguration;

/// <summary>
/// Gate 2 of the DmaEA checker (mode publish) — the binding-envelope rules that
/// graph validation (GraphValidation) does not cover. Enforced on the pack publish
/// path (AgentPackDomainException carries the snake_case code through the existing
/// problem+json mapping); the TOML-ceiling comparison here is structural, and the
/// authoritative fail-closed comparison runs again at run freeze.
///
/// Rules:
/// ① operation_tool_floor_violation — the effective tool surface of an
///    operation-layer node (template scope − envelope narrowing − tool_switches
///    removals) must be empty. The deny floor is not relaxable.
/// ② envelope_exceeds_boundary — envelope.capabilities ⊆ template capabilities,
///    envelope.tools ⊆ template tool scope (narrowing only, never widening), and
///    envelope.spawn numbers must sit inside the runtime ceilings when supplied.
/// ③ mutating_tool_without_write_grant — a binding whose EFFECTIVE tool surface
///    still holds a workspace-mutating tool must declare a write-level resource
///    grant. Otherwise the run freezes a mutating tool face and then denies every
///    call of it before the approval gate is ever consulted — the contradiction
///    that made a denied tool look like "the tool returned nothing".
/// </summary>
public static class ModePublishGate
{
    public sealed record SpawnCeilings(int MaxDepth, int MaxAgentsPerRun, int MaxParallelWorkers);

    public sealed record BindingSubject(
        string NodeKey,
        string AgentSlug,
        string Layer,
        IReadOnlyList<string> TemplateCapabilities,
        IReadOnlyList<string> TemplateTools,
        JsonElement? ToolSwitches,
        JsonElement? Envelope);

    public static void ValidateBindings(string modeKey, IReadOnlyList<BindingSubject> bindings, SpawnCeilings? ceilings)
    {
        foreach (var binding in bindings)
        {
            IReadOnlyList<string>? envelopeCapabilities = null;
            HashSet<string>? envelopeTools = null;
            if (binding.Envelope is { } envelope && envelope.ValueKind == JsonValueKind.Object)
            {
                if (envelope.TryGetProperty("capabilities", out var envelopeCapabilitiesElement))
                {
                    envelopeCapabilities = StringArray(envelopeCapabilitiesElement, modeKey, binding.NodeKey, "capabilities");
                    foreach (var capability in envelopeCapabilities)
                    {
                        if (!binding.TemplateCapabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
                            throw Fail("envelope_exceeds_boundary",
                                $"mode '{modeKey}' binding '{binding.NodeKey}' grants capability '{capability}' that the template does not hold; envelopes may only narrow.");
                    }
                }

                envelopeTools = envelope.TryGetProperty("tools", out var envelopeToolsElement)
                    ? StringArray(envelopeToolsElement, modeKey, binding.NodeKey, "tools").ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;
                if (envelopeTools is not null)
                {
                    foreach (var tool in envelopeTools)
                    {
                        if (!binding.TemplateTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
                            throw Fail("envelope_exceeds_boundary",
                                $"mode '{modeKey}' binding '{binding.NodeKey}' grants tool '{tool}' that the template does not hold; envelopes may only narrow.");
                    }
                }

                if (envelope.TryGetProperty("spawn", out var spawn) && spawn.ValueKind == JsonValueKind.Object && ceilings is not null)
                {
                    AssertWithinCeiling(modeKey, binding.NodeKey, spawn, "max_depth", ceilings.MaxDepth);
                    AssertWithinCeiling(modeKey, binding.NodeKey, spawn, "max_agents_per_run", ceilings.MaxAgentsPerRun);
                    AssertWithinCeiling(modeKey, binding.NodeKey, spawn, "max_parallel_workers", ceilings.MaxParallelWorkers);
                }
            }

            // The operation-layer tool floor is REMOVED BY DESIGN (2026-09-17). A mode may
            // now give its conversation identity a tool surface of its own — the
            // solo/master-slave shape, where the agent that talks to the user also edits
            // the workspace. An operation layer holding tools is therefore a supported
            // configuration, not a publish violation.
            //
            // What replaces the floor is not a weaker gate but a SHARPER one: operation
            // bindings no longer `continue` past the check below, so a mutating tool on
            // the conversation identity must declare an explicit write grant exactly like
            // any execution-layer worker's. The resource envelope and per-write human
            // approval are what still stand between the governance layer and the
            // workspace; layer membership is no longer one of the defences.
            var effective = (envelopeTools ?? binding.TemplateTools.ToHashSet(StringComparer.OrdinalIgnoreCase))
                .Where(tool => !IsSwitchedOff(binding.ToolSwitches, tool))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // A kept mutating tool needs a write-level grant to be reachable.
            var mutating = WorkspaceToolCatalog.MutatingMembers(effective);
            if (mutating.Count > 0 && !DeclaresWriteGrant(binding.Envelope))
            {
                throw Fail("mutating_tool_without_write_grant",
                    $"mode '{modeKey}' binding '{binding.NodeKey}' keeps workspace-mutating tool(s) {string.Join(", ", mutating)} "
                    + "but its envelope declares no write resource grant; declare envelope.resources.write (write implies read) or switch those tools off.");
            }
        }
    }

    /// <summary>
    /// True when the envelope declares at least one non-empty write prefix. The
    /// prefix itself narrows the target; the level is what this gate needs.
    /// </summary>
    private static bool DeclaresWriteGrant(JsonElement? envelope)
    {
        if (envelope is not { ValueKind: JsonValueKind.Object } value) return false;
        if (!value.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Object) return false;
        return resources.TryGetProperty("write", out var write)
            && write.ValueKind == JsonValueKind.Array
            && write.EnumerateArray().Any(prefix => prefix.ValueKind == JsonValueKind.String);
    }

    private static void AssertWithinCeiling(string modeKey, string nodeKey, JsonElement spawn, string key, int ceiling)
    {
        if (!spawn.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Number) return;
        if (!value.TryGetInt32(out var requested))
            throw Fail("envelope_exceeds_boundary", $"mode '{modeKey}' binding '{nodeKey}' spawn.{key} must be an integer.");
        if (requested > ceiling)
            throw Fail("envelope_exceeds_boundary",
                $"mode '{modeKey}' binding '{nodeKey}' spawn.{key}={requested} exceeds the runtime ceiling {ceiling}; envelopes may only narrow.");
    }

    private static bool IsSwitchedOff(JsonElement? switches, string toolId) =>
        switches is { } switchValue
        && switchValue.ValueKind == JsonValueKind.Object
        && switchValue.TryGetProperty(toolId, out var state)
        && (state.ValueKind == JsonValueKind.False
            || (state.ValueKind == JsonValueKind.String && string.Equals(state.GetString(), "false", StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<string> StringArray(JsonElement element, string modeKey, string nodeKey, string field)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Fail("envelope_exceeds_boundary", $"mode '{modeKey}' binding '{nodeKey}' envelope.{field} must be an array of strings.");
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw Fail("envelope_exceeds_boundary", $"mode '{modeKey}' binding '{nodeKey}' envelope.{field} must contain strings only.");
            values.Add(item.GetString()!);
        }
        return values;
    }

    // InvalidDataException is sealed, so failures are plain InvalidDataException
    // with the snake_case code as a message prefix; admission-level failures that
    // need structured codes throw AgentPackDomainException instead.
    private static InvalidDataException Fail(string code, string message) => new($"[{code}] {message}");
}
