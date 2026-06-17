using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Tinadec.Contracts.Models;
using TinadecCore.Abstractions;
using TinadecCore.Services;
using TinadecCore.Storage;
using TinadecModel.Providers;

namespace TinadecCore.Tests;

public sealed class LocalHttpProviderRuntimeTests
{
    [Fact]
    public void CatalogExposesLocalHttpAdapterStrategyDrivers()
    {
        var generic = GetTemplate("local-http");
        var openAiCompatible = GetTemplate("local-http-openai-compatible");
        var ollama = GetTemplate("local-http-ollama");
        var lmStudio = GetTemplate("lmstudio");
        var llamaCpp = GetTemplate("llamacpp");

        Assert.Equal("local-http", generic.ProviderFamily);
        Assert.Equal("http", generic.ConnectionKind);
        Assert.Equal("none", generic.CredentialKind);
        Assert.False(generic.Capabilities.SupportsTools);
        Assert.True(generic.Capabilities.SupportsStreaming);

        Assert.Equal("local-http", openAiCompatible.ProviderFamily);
        Assert.Equal("local-http-openai-compatible", openAiCompatible.Driver);
        Assert.True(openAiCompatible.Capabilities.SupportsJsonMode);

        Assert.Equal("local-http", ollama.ProviderFamily);
        Assert.Equal("local-http-ollama", ollama.Driver);
        Assert.False(ollama.Capabilities.SupportsTools);

        Assert.Equal("lmstudio", lmStudio.ProviderFamily);
        Assert.Equal("local-server", lmStudio.ConnectionKind);
        Assert.Equal("none", lmStudio.CredentialKind);

        Assert.Equal("llamacpp", llamaCpp.ProviderFamily);
        Assert.Equal("local-server", llamaCpp.ConnectionKind);
        Assert.Equal("none", llamaCpp.CredentialKind);
    }

    [Fact]
    public void LocalHttpModuleRegistersRuntimeAndCapabilities()
    {
        var services = new ServiceCollection();

        services.AddModelProviderModule<LocalHttpModule>();

        using var provider = services.BuildServiceProvider();
        var runtime = Assert.Single(provider.GetServices<IModelProviderRuntime>(), item => item.Id == "local-http");
        var catalog = provider.GetRequiredService<IModelProviderModuleCatalog>();

        Assert.True(runtime.CanHandle(CreateContext("local-http", "http://localhost:8080/invoke")));
        Assert.True(runtime.CanHandle(CreateContext("ollama", "http://localhost:11434/v1", "local-server")));
        Assert.True(runtime.CanHandle(CreateContext("lmstudio", "http://localhost:1234/v1", "local-server")));
        Assert.True(runtime.CanHandle(CreateContext("llamacpp", "http://localhost:8080/v1", "local-server")));
        var capabilities = catalog.GetCapabilities("local-http");
        Assert.NotNull(capabilities);
        Assert.True(capabilities.SupportsStreaming);
        Assert.False(capabilities.SupportsTools);
        Assert.Equal("none", capabilities.CredentialKind);
    }

    [Fact]
    public void SavedLocalHttpTemplateKeepsHttpConnectionKindForRuntimeResolution()
    {
        var store = CreateStore();
        var template = GetTemplate("local-http-openai-compatible");
        var saved = store.SaveModelProviderInstance(
            new SaveModelProviderInstanceRequest(
                "provider_local_http",
                template.Driver,
                template.DisplayName,
                template.ConnectionKind,
                template.DefaultBaseUrl,
                template.DefaultModel,
                ApiKey: null,
                ClearApiKey: false,
                BinaryPath: null,
                HomePath: null,
                ServerUrl: null,
                LaunchArgs: null,
                Capabilities: ["chat", "route:chat"],
                Enabled: true),
            encryptedApiKey: null);
        store.SaveModelRoute("chat", saved.Id, saved.Model);

        var context = new ModelRouteResolver(store).Resolve("chat");
        var runtime = CreateRuntime(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Equal("http", saved.ConnectionKind);
        Assert.Equal("http", context.ConnectionKind);
        Assert.True(runtime.CanHandle(context));
    }

    [Fact]
    public async Task OpenAiCompatibleStrategyUsesChatCompletionsMapping()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                    {
                      "choices": [
                        { "message": { "role": "assistant", "content": "OpenAI local response" }, "finish_reason": "stop" }
                      ],
                      "usage": { "prompt_tokens": 1, "completion_tokens": 2, "total_tokens": 3 }
                    }
                    """)
            };
        });
        var runtime = CreateRuntime(handler);

        var result = await runtime.GenerateAsync(
            CreateContext("local-http-openai-compatible", "http://localhost:1234/v1"),
            null,
            CreateMessages(),
            CancellationToken.None);

        Assert.Equal("executed", result.Status);
        Assert.Equal("OpenAI local response", result.Content);
        Assert.Equal("local-http", result.RuntimeId);
        Assert.Equal("/v1/chat/completions", capturedRequest?.RequestUri?.AbsolutePath);
    }

    [Theory]
    [InlineData("ollama", "http://localhost:11434/v1")]
    [InlineData("lmstudio", "http://localhost:1234/v1")]
    [InlineData("llamacpp", "http://localhost:8080/v1")]
    public async Task LocalServerOpenAiCompatibleDriversUseChatCompletionsMapping(string driver, string baseUrl)
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("{\"choices\":[{\"message\":{\"content\":\"Ollama response\"}}]}")
            };
        });
        var runtime = CreateRuntime(handler);

        var result = await runtime.GenerateAsync(
            CreateContext(driver, baseUrl, "local-server"),
            null,
            CreateMessages(),
            CancellationToken.None);

        Assert.Equal("executed", result.Status);
        Assert.Equal("Ollama response", result.Content);
        Assert.Equal("/v1/chat/completions", capturedRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task GenericStrategyPostsToConfiguredEndpointWithoutChatCompletionsAssumption()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new AsyncStubHttpMessageHandler(async request =>
        {
            capturedRequest = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("{\"text\":\"Generic local response\"}")
            };
        });
        var runtime = CreateRuntime(handler);

        var result = await runtime.GenerateAsync(
            CreateContext("local-http", "http://localhost:8080/invoke"),
            null,
            CreateMessages(),
            CancellationToken.None);

        Assert.Equal("executed", result.Status);
        Assert.Equal("Generic local response", result.Content);
        Assert.Equal("/invoke", capturedRequest?.RequestUri?.AbsolutePath);
        Assert.Contains("\"model\":\"local-model\"", capturedBody);
        Assert.DoesNotContain("chat/completions", capturedRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task HealthProbeUsesConfiguredEndpointWithoutAdapterPayload()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var runtime = CreateRuntime(handler);

        var result = await runtime.ProbeHealthAsync(
            CreateContext("local-http", "http://localhost:8080/health"),
            CancellationToken.None);

        Assert.Equal(ProviderHealthStatus.Healthy, result.Status);
        Assert.Null(result.ErrorCategory);
        Assert.Equal(HttpMethod.Get, capturedRequest?.Method);
        Assert.Equal("/health", capturedRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task ConnectionFailureMapsToProviderUnavailable()
    {
        var handler = new ThrowingHttpMessageHandler(_ => new HttpRequestException("Connection refused."));
        var runtime = CreateRuntime(handler);

        var result = await runtime.GenerateAsync(
            CreateContext("local-http", "http://localhost:9/invoke"),
            null,
            CreateMessages(),
            CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal(ProviderErrorCategory.ProviderUnavailable, result.ErrorCategory);
        Assert.True(result.IsRetryable);
        Assert.Null(result.ProviderStatusCode);
        Assert.Equal("provider-local", result.ErrorProviderId);
    }

    [Fact]
    public async Task TimeoutMapsToTimeout()
    {
        var handler = new ThrowingHttpMessageHandler(_ => new TaskCanceledException("The request timed out."));
        var runtime = CreateRuntime(handler);

        var result = await runtime.GenerateAsync(
            CreateContext("local-http", "http://localhost:8080/invoke"),
            null,
            CreateMessages(),
            CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal(ProviderErrorCategory.Timeout, result.ErrorCategory);
        Assert.True(result.IsRetryable);
        Assert.Equal("Provider request timed out.", result.SafeErrorMessage);
    }

    private static ModelProviderTemplateDto GetTemplate(string driver)
    {
        return Assert.Single(ModelProviderCatalog.ListTemplates(), template => template.Driver.Equals(driver, StringComparison.OrdinalIgnoreCase));
    }

    private static CoreStore CreateStore()
    {
        var store = new CoreStore(Path.Combine(Path.GetTempPath(), $"tinadec-local-http-{Guid.NewGuid():N}.db"));
        store.Initialize();
        store.DeleteModelProviderInstance("openai_default");
        return store;
    }

    private static LocalHttpProviderRuntime CreateRuntime(HttpMessageHandler handler)
    {
        return new LocalHttpProviderRuntime(new HttpClient(handler), new OpenAiCompatibleClient(new HttpClient(handler)));
    }

    private static MessageDto[] CreateMessages()
    {
        return
        [
            new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)
        ];
    }

    private static ResolvedModelInvocationContextDto CreateContext(string driver, string baseUrl, string connectionKind = "http")
    {
        return new ResolvedModelInvocationContextDto(
            "planner",
            null,
            null,
            baseUrl,
            "local-model",
            null,
            driver,
            connectionKind,
            "provider-local",
            false);
    }

    private static StringContent JsonContent(string value)
    {
        return new StringContent(value, Encoding.UTF8, "application/json");
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handle(request));
        }
    }

    private sealed class AsyncStubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handle(request);
        }
    }

    private sealed class ThrowingHttpMessageHandler(Func<HttpRequestMessage, Exception> createException) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(createException(request));
        }
    }
}
