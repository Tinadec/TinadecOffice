using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Storage;

namespace TinadecCore.Services;

public sealed class RuntimeReadinessService(
    CoreStore store,
    IToolRegistry tools)
{
    public RuntimeReadinessReceiptDto Check()
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var components = new[]
        {
            Guard("storage", "Core Store", CheckStorage),
            Guard("agent_profiles", "Agent Profiles", CheckAgentProfiles),
            Guard("tool_registry", "Tool Registry", CheckToolRegistry),
            Guard("model_routes", "Model Routes", CheckModelRoutes),
            Guard("extension_runtime", "Extension Runtime", CheckExtensionRuntime)
        };

        var readyCount = components.Count(component => Is(component, "ready"));
        var warningCount = components.Count(component => Is(component, "warning"));
        var blockedCount = components.Count(component => Is(component, "blocked"));
        var status = blockedCount > 0 ? "blocked" : warningCount > 0 ? "warning" : "ready";

        return new RuntimeReadinessReceiptDto(
            status,
            generatedAt,
            AgentWorkflowRuntime.RuntimeName,
            $"readiness_{generatedAt:yyyyMMddHHmmssfff}",
            components,
            readyCount,
            warningCount,
            blockedCount);
    }

    private RuntimeReadinessComponentDto CheckStorage()
    {
        var projects = store.ListProjects();
        return Component(
            "storage",
            "Core Store",
            "ready",
            $"SQLite store responded with {projects.Count} project records.",
            [
                "sqlite:available",
                $"project_count:{projects.Count}"
            ]);
    }

    private RuntimeReadinessComponentDto CheckAgentProfiles()
    {
        var agents = store.ListAgentProfiles();
        var planningCount = agents.Count(agent => agent.Enabled && Is(agent.Layer, "planning"));
        var executionCount = agents.Count(agent => agent.Enabled && Is(agent.Layer, "execution"));
        var status = planningCount > 0 && executionCount > 0 ? "ready" : "blocked";
        var summary = status == "ready"
            ? "Planning and execution agent layers both have enabled profiles."
            : "Core needs at least one enabled planning agent and one enabled execution agent.";

        return Component(
            "agent_profiles",
            "Agent Profiles",
            status,
            summary,
            [
                $"profile_count:{agents.Count}",
                $"enabled_planning_count:{planningCount}",
                $"enabled_execution_count:{executionCount}",
                $"built_in_count:{agents.Count(agent => agent.IsBuiltIn)}"
            ]);
    }

    private RuntimeReadinessComponentDto CheckToolRegistry()
    {
        var summary = tools.Describe();
        var status = summary.CanonicalToolCount == 0
            ? "blocked"
            : summary.DuplicateToolIdCount > 0
                ? "warning"
                : "ready";
        var text = status switch
        {
            "blocked" => "No canonical tools are registered.",
            "warning" => "Core canonicalized duplicate tool ids by registry policy.",
            _ => "Core tool registry is canonical and ready for Gateway/Desktop projection."
        };

        var evidence = new List<string>
        {
            $"declared_tool_count:{summary.DeclaredToolCount}",
            $"canonical_tool_count:{summary.CanonicalToolCount}",
            $"duplicate_tool_id_count:{summary.DuplicateToolIdCount}",
            $"source_precedence:{string.Join(">", summary.SourcePrecedence)}"
        };
        if (summary.DuplicateToolIds.Count > 0)
        {
            evidence.Add($"duplicate_tool_ids:{string.Join(",", summary.DuplicateToolIds)}");
        }

        return Component("tool_registry", "Tool Registry", status, text, evidence);
    }

    private RuntimeReadinessComponentDto CheckModelRoutes()
    {
        var routes = store.ListModelRoutes();
        var providers = store.ListModelProviderInstances();
        var enabledProviders = providers.Where(provider => provider.Enabled).ToArray();
        var providersById = providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        var enabledProviderIds = enabledProviders.Select(provider => provider.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingProviderRoutes = routes
            .Where(route => !providersById.ContainsKey(route.ProviderInstanceId))
            .Select(route => route.Purpose)
            .OrderBy(purpose => purpose, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var disabledProviderRoutes = routes
            .Where(route => providersById.ContainsKey(route.ProviderInstanceId) && !enabledProviderIds.Contains(route.ProviderInstanceId))
            .Select(route => route.Purpose)
            .OrderBy(purpose => purpose, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unavailableProviderRoutes = routes
            .Where(route => providersById.TryGetValue(route.ProviderInstanceId, out var provider)
                && enabledProviderIds.Contains(route.ProviderInstanceId)
                && !provider.Status.Equals("ready", StringComparison.OrdinalIgnoreCase))
            .Select(route => route.Purpose)
            .OrderBy(purpose => purpose, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var status = routes.Count > 0
            && enabledProviders.Length > 0
            && missingProviderRoutes.Length == 0
            && disabledProviderRoutes.Length == 0
            && unavailableProviderRoutes.Length == 0
                ? "ready"
                : "warning";
        var summary = status == "ready"
            ? "Model routes resolve to enabled provider instances."
            : "Model routing is incomplete or references unavailable providers.";

        var evidence = new List<string>
        {
            $"route_count:{routes.Count}",
            $"provider_count:{providers.Count}",
            $"enabled_provider_count:{enabledProviders.Length}"
        };
        evidence.AddRange(routes.Select(route => $"route:{route.Purpose}->{route.ProviderInstanceId}"));
        if (missingProviderRoutes.Length > 0)
        {
            evidence.Add($"missing_provider_routes:{string.Join(",", missingProviderRoutes)}");
        }
        if (disabledProviderRoutes.Length > 0)
        {
            evidence.Add($"disabled_provider_routes:{string.Join(",", disabledProviderRoutes)}");
        }
        if (unavailableProviderRoutes.Length > 0)
        {
            evidence.Add($"unavailable_provider_routes:{string.Join(",", unavailableProviderRoutes)}");
        }

        return Component("model_routes", "Model Routes", status, summary, evidence);
    }

    private RuntimeReadinessComponentDto CheckExtensionRuntime()
    {
        var installed = store.ListInstalledExtensions();
        var enabled = installed.Count(extension => extension.Enabled);
        var mcpServers = store.ListMcpServers();
        var acpAdapters = store.ListAcpAdapters();

        return Component(
            "extension_runtime",
            "Extension Runtime",
            "ready",
            enabled == 0
                ? "No enabled extensions; built-in Tool-layer providers remain available."
                : "Enabled extensions are visible through Core runtime registries.",
            [
                $"installed_extension_count:{installed.Count}",
                $"enabled_extension_count:{enabled}",
                $"mcp_server_count:{mcpServers.Count}",
                $"acp_adapter_count:{acpAdapters.Count}"
            ]);
    }

    private static RuntimeReadinessComponentDto Guard(
        string id,
        string name,
        Func<RuntimeReadinessComponentDto> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex)
        {
            return Component(id, name, "blocked", $"Readiness probe failed: {ex.Message}", [$"exception:{ex.GetType().Name}"]);
        }
    }

    private static RuntimeReadinessComponentDto Component(
        string id,
        string name,
        string status,
        string summary,
        IReadOnlyList<string> evidence)
    {
        return new RuntimeReadinessComponentDto(id, name, status, summary, evidence);
    }

    private static bool Is(RuntimeReadinessComponentDto component, string status)
    {
        return Is(component.Status, status);
    }

    private static bool Is(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
