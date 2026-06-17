using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Services;
using TinadecCore.Storage;
using TinadecModel.Providers;

namespace TinadecCore.Tests;

public sealed class ModelProviderCatalogTests
{
    [Fact]
    public void OpenAiCompatibleTemplateExposesCapabilityMetadata()
    {
        var template = GetTemplate("openai-compatible");

        Assert.Equal("openai-compatible", template.ProviderFamily);
        Assert.Equal("openai-compatible", template.Driver);
        Assert.Equal("http", template.ConnectionKind);
        Assert.Equal("api_key", template.CredentialKind);
        Assert.True(template.Capabilities.SupportsStreaming);
        Assert.True(template.Capabilities.SupportsTools);
        Assert.True(template.Capabilities.SupportsJsonMode);
        Assert.True(template.Capabilities.SupportsSystemPrompt);
        Assert.False(template.Capabilities.RequiresWorkspace);
    }

    [Fact]
    public void AnthropicTemplateIsDistinctFromOpenAiCompatible()
    {
        var template = GetTemplate("anthropic");

        Assert.Equal("anthropic", template.ProviderFamily);
        Assert.Equal("anthropic", template.Driver);
        Assert.NotEqual("openai-compatible", template.ProviderFamily);
        Assert.Equal("api_key", template.CredentialKind);
        Assert.True(template.Capabilities.SupportsStreaming);
        Assert.True(template.Capabilities.SupportsTools);
        Assert.True(template.Capabilities.SupportsJsonMode);
        Assert.True(template.Capabilities.SupportsSystemPrompt);
        Assert.False(template.Capabilities.RequiresWorkspace);
    }

    [Fact]
    public void LocalHttpTemplateExposesLocalConnectionCapabilities()
    {
        var template = GetTemplate("local-http");

        Assert.Equal("local-http", template.ProviderFamily);
        Assert.Equal("local-http", template.Driver);
        Assert.Equal("http", template.ConnectionKind);
        Assert.Equal("none", template.CredentialKind);
        Assert.True(template.Capabilities.SupportsStreaming);
        Assert.False(template.Capabilities.SupportsTools);
        Assert.False(template.Capabilities.SupportsJsonMode);
        Assert.True(template.Capabilities.SupportsSystemPrompt);
        Assert.False(template.Capabilities.RequiresWorkspace);
    }

    [Fact]
    public void CodexCliTemplateRequiresWorkspaceAndSupportsTools()
    {
        var template = GetTemplate("codex-cli");

        Assert.Equal("codex-cli", template.ProviderFamily);
        Assert.Equal("codex-cli", template.Driver);
        Assert.Equal("cli", template.ConnectionKind);
        Assert.Equal("cli", template.CredentialKind);
        Assert.False(template.Capabilities.SupportsStreaming);
        Assert.True(template.Capabilities.SupportsTools);
        Assert.False(template.Capabilities.SupportsJsonMode);
        Assert.True(template.Capabilities.SupportsSystemPrompt);
        Assert.True(template.Capabilities.RequiresWorkspace);
    }

    [Fact]
    public void CatalogTemplatesExposeUniqueDrivers()
    {
        var drivers = ModelProviderCatalog.ListTemplates().Select(template => template.Driver).ToArray();

        Assert.Equal(drivers.Length, drivers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("pollinations", "public-api", "openai-compatible", "none")]
    [InlineData("lmstudio", "local-server", "local-http", "none")]
    [InlineData("llamacpp", "local-server", "local-http", "none")]
    public void NoLoginFreeTemplatesExposeCredentialFreeRuntimeMetadata(
        string driver,
        string expectedConnectionKind,
        string expectedRuntimeFamily,
        string expectedCredentialKind)
    {
        var template = GetTemplate(driver);

        Assert.Equal(expectedConnectionKind, template.ConnectionKind);
        Assert.Equal(expectedCredentialKind, template.CredentialKind);
        Assert.Equal(expectedCredentialKind, template.Capabilities.CredentialKind);
        Assert.True(template.Capabilities.SupportsStreaming);
        Assert.True(template.Capabilities.SupportsSystemPrompt);
        Assert.False(template.Capabilities.RequiresWorkspace);
        Assert.False(ProviderTemplateRules.RequiresApiKey(template.Driver, template.ConnectionKind));

        var receipt = BuildCatalogReadinessReceipt();
        Assert.Contains(receipt.Templates, item =>
            item.Driver == driver
            && item.RuntimeModuleFamily == expectedRuntimeFamily
            && item.RuntimeModuleStatus == "registered");
    }

    [Fact]
    public void CatalogReadinessReceiptMapsTemplatesToRuntimeModulesAndAdvisoryPolicies()
    {
        var receipt = BuildCatalogReadinessReceipt();

        Assert.Equal("ready", receipt.Status);
        Assert.Equal(ModelProviderCatalog.ListTemplates().Count, receipt.TemplateCount);
        Assert.Equal(receipt.TemplateCount, receipt.ReadyTemplateCount);
        Assert.Equal(0, receipt.WarningTemplateCount);
        Assert.Equal(0, receipt.BlockedTemplateCount);
        Assert.Equal(4, receipt.RuntimeModuleCount);
        Assert.True(receipt.AdvisoryProbeTemplateCount > 0);
        Assert.Contains(receipt.Templates, template =>
            template.Driver == "openai-compatible"
            && template.RuntimeModuleFamily == "openai-compatible"
            && template.RuntimeModuleStatus == "registered"
            && template.LiveDiscoveryPolicy == "credential_gated_remote_advisory");
        Assert.Contains(receipt.Templates, template =>
            template.Driver == "ollama"
            && template.RuntimeModuleFamily == "local-http"
            && template.RuntimeModuleStatus == "registered"
            && template.LiveDiscoveryPolicy == "loopback_only_advisory");
        Assert.Contains(receipt.Templates, template =>
            template.Driver == "pollinations"
            && template.RuntimeModuleFamily == "openai-compatible"
            && template.RuntimeModuleStatus == "registered"
            && template.LiveDiscoveryPolicy == "public_endpoint_advisory");
        Assert.Contains(receipt.Templates, template =>
            template.Driver == "codex-cli"
            && template.RuntimeModuleFamily == "cli"
            && template.RuntimeModuleStatus == "registered"
            && template.LiveDiscoveryPolicy == "workspace_cli_advisory");
        Assert.Contains(receipt.DesignNotes, note => note.Contains("advisory", StringComparison.OrdinalIgnoreCase));
    }

    private static ModelProviderTemplateDto GetTemplate(string driver)
    {
        return Assert.Single(ModelProviderCatalog.ListTemplates(), template => template.Driver.Equals(driver, StringComparison.OrdinalIgnoreCase));
    }

    private static ModelCatalogReadinessReceiptDto BuildCatalogReadinessReceipt()
    {
        var store = new CoreStore(Path.Combine(Path.GetTempPath(), $"tinadec-model-catalog-readiness-{Guid.NewGuid():N}.db"));
        store.Initialize();
        var service = new ModelCatalogReadinessService(store, CreateRegisteredModuleCatalog());

        return service.Check();
    }

    private static ModelProviderModuleCatalog CreateRegisteredModuleCatalog()
    {
        return new ModelProviderModuleCatalog(
        [
            Module("openai-compatible"),
            Module("anthropic"),
            Module("local-http"),
            Module("cli")
        ]);
    }

    private static ModelProviderModuleMetadata Module(string providerFamily)
    {
        return new ModelProviderModuleMetadata(
            providerFamily,
            new ProviderCapabilityDto(
                SupportsStreaming: true,
                SupportsTools: true,
                SupportsJsonMode: true,
                SupportsSystemPrompt: true,
                MaxContextTokens: null,
                RequiresWorkspace: providerFamily.Equals("cli", StringComparison.OrdinalIgnoreCase),
                CredentialKind: providerFamily.Equals("local-http", StringComparison.OrdinalIgnoreCase) ? "none" : "api_key",
                HealthStatus: ProviderHealthStatus.Unknown));
    }
}
