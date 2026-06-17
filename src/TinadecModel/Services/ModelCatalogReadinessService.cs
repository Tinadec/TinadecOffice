using Tinadec.Contracts.Models;
using TinadecModel.Abstractions;
using TinadecModel.Storage;
using TinadecModel.Providers;

namespace TinadecModel.Services;

public sealed class ModelCatalogReadinessService(
    IModelStore store,
    IModelProviderModuleCatalog moduleCatalog)
{
    public ModelCatalogReadinessReceiptDto Check()
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var templates = ModelProviderCatalog.ListTemplates();
        var providers = store.ListModelProviderInstances();
        var configuredByDriver = providers
            .GroupBy(provider => provider.Driver, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var moduleFamilies = moduleCatalog
            .ListModules()
            .Select(module => module.ProviderFamily)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var templateReceipts = templates
            .Select(template => BuildTemplate(template, configuredByDriver, moduleFamilies))
            .OrderBy(template => StatusSortKey(template.Status))
            .ThenBy(template => template.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var readyCount = templateReceipts.Count(template => Is(template.Status, "ready"));
        var warningCount = templateReceipts.Count(template => Is(template.Status, "warning"));
        var blockedCount = templateReceipts.Count(template => Is(template.Status, "blocked"));
        var status = blockedCount > 0
            ? "blocked"
            : warningCount > 0
                ? "warning"
                : "ready";

        return new ModelCatalogReadinessReceiptDto(
            status,
            generatedAt,
            $"model_catalog_readiness_{generatedAt:yyyyMMddHHmmssfff}",
            templates.Count,
            readyCount,
            warningCount,
            blockedCount,
            moduleFamilies.Count,
            providers.Count,
            templateReceipts.Count(template => template.SupportsLiveDiscovery),
            templateReceipts,
            [
                "Core owns model catalog readiness; Gateway and Desktop may display this receipt but must not recompute template or runtime-module status.",
                "Live model discovery is advisory, credential-gated for remote providers, public-endpoint-gated for no-login providers, and loopback-only for local-server templates.",
                "Static provider templates remain visible even when live discovery is unavailable or disabled."
            ]);
    }

    private static ModelCatalogTemplateReadinessDto BuildTemplate(
        ModelProviderTemplateDto template,
        IReadOnlyDictionary<string, int> configuredByDriver,
        IReadOnlySet<string> moduleFamilies)
    {
        var runtimeModuleFamily = ResolveRuntimeModuleFamily(template);
        var moduleRegistered = moduleFamilies.Contains(runtimeModuleFamily);
        var configuredCount = configuredByDriver.TryGetValue(template.Driver, out var count) ? count : 0;
        var hasRequiredFields = !string.IsNullOrWhiteSpace(template.Driver)
            && !string.IsNullOrWhiteSpace(template.ProviderFamily)
            && !string.IsNullOrWhiteSpace(template.DisplayName)
            && !string.IsNullOrWhiteSpace(template.ConnectionKind)
            && !string.IsNullOrWhiteSpace(template.CredentialKind);
        var discoveryPolicy = ResolveLiveDiscoveryPolicy(template);
        var supportsLiveDiscovery = !Is(discoveryPolicy, "static_template_only");
        var status = ResolveStatus(hasRequiredFields, moduleRegistered, configuredCount);
        var summary = status switch
        {
            "blocked" => "Template is configured in Core but cannot resolve a runtime module.",
            "warning" when !hasRequiredFields => "Template is missing required catalog metadata.",
            "warning" => "Template is visible, but no matching runtime module is registered.",
            _ => "Template is available through the Core catalog and runtime module registry."
        };

        return new ModelCatalogTemplateReadinessDto(
            template.ProviderFamily,
            template.Driver,
            template.DisplayName,
            template.ConnectionKind,
            template.CredentialKind,
            status,
            runtimeModuleFamily,
            moduleRegistered ? "registered" : "missing",
            configuredCount,
            supportsLiveDiscovery,
            discoveryPolicy,
            summary,
            [
                $"provider_family:{template.ProviderFamily}",
                $"driver:{template.Driver}",
                $"connection_kind:{template.ConnectionKind}",
                $"credential_kind:{template.CredentialKind}",
                $"runtime_module_family:{runtimeModuleFamily}",
                $"runtime_module_status:{(moduleRegistered ? "registered" : "missing")}",
                $"configured_instance_count:{configuredCount}",
                $"live_discovery_policy:{discoveryPolicy}",
                $"default_base_url:{template.DefaultBaseUrl ?? "(none)"}",
                $"default_model:{template.DefaultModel ?? "(none)"}"
            ]);
    }

    private static string ResolveStatus(bool hasRequiredFields, bool moduleRegistered, int configuredCount)
    {
        if (!hasRequiredFields)
        {
            return "warning";
        }

        if (moduleRegistered)
        {
            return "ready";
        }

        return configuredCount > 0 ? "blocked" : "warning";
    }

    private static string ResolveRuntimeModuleFamily(ModelProviderTemplateDto template)
    {
        if (template.ConnectionKind.Equals("cli", StringComparison.OrdinalIgnoreCase))
        {
            return "cli";
        }

        if (template.ConnectionKind.Equals("local-server", StringComparison.OrdinalIgnoreCase)
            || template.ProviderFamily.Equals("local-http", StringComparison.OrdinalIgnoreCase))
        {
            return "local-http";
        }

        if (ProviderTemplateRules.IsOpenAiCompatibleDriver(template.Driver))
        {
            return "openai-compatible";
        }

        return template.ProviderFamily;
    }

    private static string ResolveLiveDiscoveryPolicy(ModelProviderTemplateDto template)
    {
        if (template.ConnectionKind.Equals("cli", StringComparison.OrdinalIgnoreCase))
        {
            return "workspace_cli_advisory";
        }

        if (template.ConnectionKind.Equals("local-server", StringComparison.OrdinalIgnoreCase)
            || IsLoopback(template.DefaultBaseUrl))
        {
            return "loopback_only_advisory";
        }

        if (template.ConnectionKind.Equals("public-api", StringComparison.OrdinalIgnoreCase))
        {
            return "public_endpoint_advisory";
        }

        if (IsApiKeyCredential(template.CredentialKind) || IsApiKeyCredential(template.Capabilities.CredentialKind))
        {
            return "credential_gated_remote_advisory";
        }

        return "static_template_only";
    }

    private static bool IsLoopback(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    private static bool IsApiKeyCredential(string? value)
    {
        return string.Equals(value, "api_key", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "api-key", StringComparison.OrdinalIgnoreCase);
    }

    private static int StatusSortKey(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "blocked" => 0,
            "warning" => 1,
            "ready" => 2,
            _ => 3
        };
    }

    private static bool Is(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
