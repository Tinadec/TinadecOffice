using System.Text.Json;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.DmaEA.Orchestration;

/// <summary>
/// Fail-closed validation of meeting-authored orchestration directives. Every
/// rule reads code facts only — the frozen tool manifest, the target run's lane
/// budget, and the payload's own graph. Nothing here trusts the model.
/// </summary>
public sealed class OrchestrationDirectiveValidator : IOrchestrationDirectiveValidator
{
    private static readonly string[] KnownVerbs = ["LANE_OPEN", "LANE_CLOSE", "LANE_WAIT", "LANE_STATUS_QUERY"];

    public OrchestrationDirectiveRejection? Validate(OrchestrationDirectiveCandidate candidate, OrchestrationDirectiveRules rules)
    {
        if (!KnownVerbs.Contains(candidate.Verb, StringComparer.Ordinal))
            return new("unknown_verb", $"Verb '{candidate.Verb}' is not in the orchestration verb whitelist.");
        if (!rules.LanesEnabled)
            return new("lanes_disabled", "Lanes are disabled for the target run.");
        if (rules.CurrentLaneCount >= rules.MaxLanesPerRun)
            return new("lane_budget_exhausted", $"The run already holds {rules.CurrentLaneCount} of {rules.MaxLanesPerRun} lanes.");
        if (string.IsNullOrWhiteSpace(candidate.LaneKey))
            return new("invalid_lane_payload", "lane_key is required.");
        if (rules.KnownLaneKeys.Contains(candidate.LaneKey, StringComparer.OrdinalIgnoreCase))
            return new("duplicate_lane", $"Lane '{candidate.LaneKey}' already exists on the target run.");

        JsonElement payload;
        try { payload = JsonDocument.Parse(candidate.PayloadJson).RootElement.Clone(); }
        catch (JsonException)
        {
            return new("invalid_lane_payload", "Payload is not valid JSON.");
        }
        if (payload.ValueKind != JsonValueKind.Object)
            return new("invalid_lane_payload", "Payload must be a JSON object.");

        var goal = StringProperty(payload, "goal");
        var title = StringProperty(payload, "title");
        if (string.IsNullOrWhiteSpace(goal) && string.IsNullOrWhiteSpace(title))
            return new("invalid_lane_payload", "goal or title is required.");
        if ((goal?.Length ?? 0) > 2000 || (title?.Length ?? 0) > 200)
            return new("invalid_lane_payload", "goal/title exceeds the length budget.");

        if (!payload.TryGetProperty("tasks", out var tasksElement) || tasksElement.ValueKind != JsonValueKind.Array || tasksElement.GetArrayLength() == 0)
            return new("invalid_lane_payload", "tasks must be a non-empty array.");
        if (tasksElement.GetArrayLength() > rules.MaxTasksPerLane)
            return new("task_budget_exceeded", $"A lane may declare at most {rules.MaxTasksPerLane} tasks.");

        var declaredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectDeclaredTools(payload, declaredTools);
        foreach (var task in tasksElement.EnumerateArray())
            CollectDeclaredTools(task, declaredTools);
        if (declaredTools.Contains("*") && rules.FrozenToolIds.All(id => !string.Equals(id, "*", StringComparison.Ordinal)))
            return new("tool_scope_widening", "A wildcard tool scope cannot exceed the frozen run manifest.");
        foreach (var tool in declaredTools)
        {
            if (string.Equals(tool, "*", StringComparison.Ordinal)) continue;
            if (!rules.FrozenToolIds.Contains(tool, StringComparer.OrdinalIgnoreCase))
                return new("tool_scope_widening", $"Tool '{tool}' is not in the frozen run manifest.");
        }

        var declaresMutating = declaredTools.Any(tool => rules.MutatingToolIds.Contains(tool, StringComparer.OrdinalIgnoreCase))
            || (declaredTools.Contains("*") && rules.MutatingToolIds.Count > 0);
        if (declaresMutating)
        {
            var preAuthorization = StringProperty(payload, "pre_authorization");
            if (string.IsNullOrWhiteSpace(preAuthorization))
                return new("lane_requires_preauthorization", "A lane declaring mutating tools requires a pre_authorization reference.");
            return new("preauthorization_unavailable", "Pre-authorization consumption lands with the M5 authorization work; mutating lanes cannot open yet.");
        }

        var taskKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasksElement.EnumerateArray())
        {
            if (task.ValueKind != JsonValueKind.Object)
                return new("invalid_lane_payload", "Every task must be an object.");
            var key = StringProperty(task, "task_key");
            if (string.IsNullOrWhiteSpace(key))
                return new("invalid_lane_payload", "Every task requires task_key.");
            if (!taskKeys.Add(key))
                return new("duplicate_task_key", $"Task key '{key}' is not unique within the lane.");
        }

        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var task in tasksElement.EnumerateArray())
        {
            var key = StringProperty(task, "task_key")!;
            var dependencies = new List<string>();
            if (task.TryGetProperty("dependencies", out var dependenciesElement) && dependenciesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var dependency in dependenciesElement.EnumerateArray())
                {
                    if (dependency.ValueKind != JsonValueKind.String)
                        return new("invalid_lane_payload", "dependencies must be strings.");
                    var name = dependency.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!taskKeys.Contains(name))
                        return new("cross_lane_dependency", $"Dependency '{name}' of task '{key}' is outside the lane; express cross-lane constraints as waits instead.");
                    dependencies.Add(name);
                }
            }
            edges[key] = dependencies;
        }
        if (HasCycle(edges))
            return new("cyclic_dependencies", "The lane's dependency graph contains a cycle.");

        if (payload.TryGetProperty("waits", out var waitsElement) && waitsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var wait in waitsElement.EnumerateArray())
            {
                if (wait.ValueKind != JsonValueKind.Object)
                    return new("invalid_lane_payload", "Each wait must be an object.");
                var lane = StringProperty(wait, "lane");
                if (string.IsNullOrWhiteSpace(lane))
                    return new("invalid_lane_payload", "Each wait requires lane.");
                if (!string.Equals(lane, candidate.LaneKey, StringComparison.OrdinalIgnoreCase)
                    && !rules.KnownLaneKeys.Contains(lane, StringComparer.OrdinalIgnoreCase))
                    return new("unknown_wait_lane", $"Wait targets unknown lane '{lane}'.");
            }
        }
        return null;
    }

    private static void CollectDeclaredTools(JsonElement element, HashSet<string> declaredTools)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        if (element.TryGetProperty("tool_scope", out var scope) && scope.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in scope.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } tool && !string.IsNullOrWhiteSpace(tool))
                    declaredTools.Add(tool.Trim());
            }
        }
    }

    private static string? StringProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return null;
        return property.GetString()?.Trim();
    }

    private static bool HasCycle(Dictionary<string, List<string>> edges)
    {
        // 0 = unseen, 1 = visiting, 2 = done.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in edges.Keys)
        {
            if (Visit(key)) return true;
        }
        return false;

        bool Visit(string key)
        {
            if (state.TryGetValue(key, out var mark)) return mark == 1;
            state[key] = 1;
            foreach (var dependency in edges.TryGetValue(key, out var list) ? list : [])
            {
                if (edges.ContainsKey(dependency) && Visit(dependency)) return true;
            }
            state[key] = 2;
            return false;
        }
    }
}
