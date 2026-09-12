using System.Text.Json;

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

            // ① operation deny floor over the EFFECTIVE surface (template scope −
            // envelope − switches) — enforced regardless of whether the binding
            // carries an envelope: an operation template that declares tools is
            // already a violation a binding cannot repair (it may only remove).
            if (string.Equals(binding.Layer, "operation", StringComparison.Ordinal))
            {
                var effective = (envelopeTools ?? binding.TemplateTools.ToHashSet(StringComparer.OrdinalIgnoreCase))
                    .Where(tool => !IsSwitchedOff(binding.ToolSwitches, tool))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (effective.Count > 0)
                    throw Fail("operation_tool_floor_violation",
                        $"mode '{modeKey}' operation node '{binding.NodeKey}' has an effective tool surface ({string.Join(", ", effective.OrderBy(t => t, StringComparer.Ordinal))}); operation-layer agents cannot invoke tools.");
            }
        }
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
