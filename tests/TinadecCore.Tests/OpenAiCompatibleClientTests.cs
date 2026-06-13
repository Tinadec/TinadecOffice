using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions;
using TinadecCore.Services;
using TinadecCore.Storage;
using Tinadec.Contracts.Models;

namespace TinadecCore.Tests;

public sealed class OpenAiCompatibleClientTests
{
    [Fact]
    public void BuildsChatCompletionsEndpointFromOpenAiCompatibleBaseUrl()
    {
        var uri = OpenAiCompatibleClient.BuildChatCompletionsEndpoint("https://api.example.test/v1/");

        Assert.Equal("https://api.example.test/v1/chat/completions", uri.ToString());
    }

    [Fact]
    public async Task BuildsBearerAuthorizedChatCompletionRequest()
    {
        var settings = new StoredModelSettings("https://api.example.test/v1", "test-model", null, DateTimeOffset.UtcNow);
        var messages = new[]
        {
            new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)
        };

        using var request = OpenAiCompatibleClient.BuildChatCompletionRequest(settings, "sk-test", messages);
        var body = await request.Content!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test", request.Headers.Authorization?.Parameter);
        Assert.Contains("\"model\":\"test-model\"", body);
        Assert.Contains("\"role\":\"user\"", body);
    }

    [Fact]
    public async Task BuildsNormalizedResponseFromOpenAiCompatibleChatCompletion()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "id": "chatcmpl_1",
                  "object": "chat.completion",
                  "created": 1710000000,
                  "choices": [
                    {
                      "index": 0,
                      "message": { "role": "assistant", "content": "Hello from the model" },
                      "finish_reason": "stop"
                    }
                  ],
                  "usage": {
                    "prompt_tokens": 11,
                    "completion_tokens": 7,
                    "total_tokens": 18
                  }
                }
                """)
        });
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        var settings = new StoredModelSettings("https://api.example.test/v1", "test-model", null, DateTimeOffset.UtcNow);

        var response = await client.CreateAssistantResponseAsync(settings, "sk-test", CreateMessages(), "provider-openai", CancellationToken.None);

        Assert.Equal("Hello from the model", response.TextContent);
        Assert.Equal(new ModelUsageDto(11, 7, 18), response.Usage);
        Assert.Equal(ModelFinishReason.Stop, response.FinishReason);
        Assert.Equal("provider-openai", response.Metadata.ProviderId);
        Assert.Equal("test-model", response.Metadata.Model);
        Assert.Equal("openai-compatible", response.Metadata.RawProviderName);
        Assert.Equal("chatcmpl_1", response.Metadata.Custom["response_id"]);
        Assert.Null(response.ErrorCategory);
        Assert.Null(response.ErrorMessage);
    }

    [Fact]
    public async Task LegacyReplyHelperStillReturnsAssistantContent()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "choices": [
                    { "message": { "role": "assistant", "content": "Legacy content" }, "finish_reason": "length" }
                  ]
                }
                """)
        });
        var client = new OpenAiCompatibleClient(new HttpClient(handler));
        var settings = new StoredModelSettings("https://api.example.test/v1", "test-model", null, DateTimeOffset.UtcNow);

        var content = await client.CreateAssistantReplyAsync(settings, "sk-test", CreateMessages(), CancellationToken.None);

        Assert.Equal("Legacy content", content);
    }

    [Fact]
    public async Task RuntimeCanHandleOpenAiCompatibleRequestThroughRegisteredModuleRuntime()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "choices": [
                    { "message": { "role": "assistant", "content": "Runtime content" }, "finish_reason": "tool_calls" }
                  ],
                  "usage": { "prompt_tokens": 3, "completion_tokens": 4, "total_tokens": 7 }
                }
                """)
        });
        var services = new ServiceCollection();
        services.AddSingleton(new OpenAiCompatibleClient(new HttpClient(handler)));
        services.AddSingleton<IModelProviderRuntime, OpenAiCompatibleProviderRuntime>();

        using var provider = services.BuildServiceProvider();
        var runtime = Assert.Single(provider.GetServices<IModelProviderRuntime>(), item => item.Id == "openai-compatible");

        var result = await runtime.GenerateAsync(CreateContext(), "sk-test", CreateMessages(), CancellationToken.None);

        Assert.True(runtime.CanHandle(CreateContext()));
        Assert.Equal("executed", result.Status);
        Assert.Equal("Runtime content", result.Content);
        Assert.Equal("openai-compatible", result.RuntimeId);
        Assert.False(result.UsedStubResponse);
        Assert.Null(result.ErrorCategory);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorCategory.AuthenticationFailed, false)]
    [InlineData((HttpStatusCode)429, ProviderErrorCategory.RateLimited, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderErrorCategory.ProviderUnavailable, true)]
    [InlineData(HttpStatusCode.BadRequest, ProviderErrorCategory.InvalidRequest, false)]
    public async Task RuntimeMapsOpenAiCompatibleHttpFailuresToNormalizedErrors(
        HttpStatusCode statusCode,
        ProviderErrorCategory expectedCategory,
        bool expectedRetryable)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = JsonContent("{\"error\":{\"message\":\"provider secret details\"}}")
        });
        var runtime = new OpenAiCompatibleProviderRuntime(new OpenAiCompatibleClient(new HttpClient(handler)));

        var result = await runtime.GenerateAsync(CreateContext(), "sk-test", CreateMessages(), CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal(expectedCategory, result.ErrorCategory);
        Assert.Equal(expectedRetryable, result.IsRetryable);
        Assert.Equal((int)statusCode, result.ProviderStatusCode);
        Assert.Equal("provider-openai", result.ErrorProviderId);
        Assert.Equal(result.SafeErrorMessage, result.Content);
    }

    private static MessageDto[] CreateMessages()
    {
        return
        [
            new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)
        ];
    }

    private static ResolvedModelInvocationContextDto CreateContext()
    {
        return new ResolvedModelInvocationContextDto(
            "planner",
            null,
            null,
            "https://api.example.test/v1",
            "test-model",
            null,
            "openai-compatible",
            "http",
            "provider-openai",
            false);
    }

    [Fact]
    public void OpenAiCompatibleRuntime_CanHandle_MatchesHostedOpenAiCompatibleDriversOnly()
    {
        var runtime = new OpenAiCompatibleProviderRuntime(
            new OpenAiCompatibleClient(new HttpClient()), null, 3);

        var openAiContext = new ResolvedModelInvocationContextDto(
            "openai-compatible", null, null, "https://api.openai.com/v1", "gpt-4", null, "openai-compatible", "http", "openai-compatible", false);
        var deepSeekContext = new ResolvedModelInvocationContextDto(
            "deepseek", null, null, "https://api.deepseek.com/v1", "deepseek-chat", null, "deepseek", "http", "deepseek", false);
        var pollinationsContext = new ResolvedModelInvocationContextDto(
            "pollinations", null, null, "https://gen.pollinations.ai/v1", "openai", null, "pollinations", "public-api", "pollinations", false);
        var lmStudioContext = new ResolvedModelInvocationContextDto(
            "lmstudio", null, null, "http://localhost:1234/v1", "default", null, "lmstudio", "local-server", "lmstudio", false);
        var anthropicContext = new ResolvedModelInvocationContextDto(
            "anthropic", null, null, "https://api.anthropic.com/v1", "claude-sonnet-4-6", null, "anthropic", "http", "anthropic", false);
        var cliContext = new ResolvedModelInvocationContextDto(
            "codex-cli", null, null, "", "gpt-5.4", null, "codex-cli", "cli", "codex-cli", false);

        Assert.True(runtime.CanHandle(openAiContext));
        Assert.True(runtime.CanHandle(deepSeekContext));
        Assert.True(runtime.CanHandle(pollinationsContext));
        Assert.False(runtime.CanHandle(lmStudioContext));
        Assert.False(runtime.CanHandle(anthropicContext));
        Assert.False(runtime.CanHandle(cliContext));
    }

    [Fact]
    public async Task ModelInvocationRuntime_AllowsPublicNoLoginOpenAiCompatibleProviderWithoutKey()
    {
        var provider = new ModelProviderInstanceDto(
            "provider-pollinations",
            "pollinations",
            "Pollinations",
            "public-api",
            "https://gen.pollinations.ai/v1",
            "openai",
            false,
            null,
            null,
            null,
            null,
            ["chat", "streaming", "public-api", "no-api-key"],
            true,
            "ready",
            "Provider is ready.",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var routeResolver = new FixedRouteResolver(new ResolvedModelInvocationContextDto(
            "chat", null, provider, provider.BaseUrl!, provider.Model!, null, provider.Driver, provider.ConnectionKind, provider.Id, false));
        var invocationRuntime = new ModelInvocationRuntime(
            routeResolver,
            new StubCredentialResolver(null),
            [new StubProviderRuntime("openai-compatible", context =>
                string.Equals(context.Driver, "pollinations", StringComparison.OrdinalIgnoreCase))]);

        var result = await invocationRuntime.InvokeAsync(
            "sess_1", "chat", [new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)], CancellationToken.None);

        Assert.Equal("executed", result.Status);
        Assert.Equal("Handled by openai-compatible", result.Content);
        Assert.Equal("provider-pollinations", result.Context.ProviderInstanceId);
    }

    [Fact]
    public async Task ModelInvocationRuntime_RejectsApiKeyTemplateWithoutKeyBeforeRuntimeDispatch()
    {
        var provider = new ModelProviderInstanceDto(
            "provider-deepseek",
            "deepseek",
            "DeepSeek",
            "http",
            "https://api.deepseek.com/v1",
            "deepseek-chat",
            false,
            null,
            null,
            null,
            null,
            ["chat"],
            true,
            "ready",
            "Provider is ready.",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var routeResolver = new FixedRouteResolver(new ResolvedModelInvocationContextDto(
            "chat", null, provider, provider.BaseUrl!, provider.Model!, null, provider.Driver, provider.ConnectionKind, provider.Id, false));
        var invocationRuntime = new ModelInvocationRuntime(
            routeResolver,
            new StubCredentialResolver(null),
            [new StubProviderRuntime("openai-compatible", _ => true)]);

        var result = await invocationRuntime.InvokeAsync(
            "sess_1", "chat", [new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow)], CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Equal(ProviderErrorCategory.AuthenticationFailed, result.ErrorCategory);
        Assert.Equal("provider-deepseek", result.ErrorProviderId);
    }

    [Fact]
    public async Task ModelInvocationRuntime_DispatchPrefersExactProviderIdMatch()
    {
        var routeResolver = new StubRouteResolver("anthropic", "anthropic", "http", "claude-sonnet-4-6");
        var credentialResolver = new StubCredentialResolver("sk-test-key-123");
        var openAiRuntime = new StubProviderRuntime("openai-compatible", _ => true);
        var anthropicRuntime = new StubProviderRuntime("anthropic", ctx =>
            string.Equals(ctx.Driver, "anthropic", StringComparison.OrdinalIgnoreCase));

        var runtimes = new[] { openAiRuntime, anthropicRuntime };
        var invocationRuntime = new ModelInvocationRuntime(
            routeResolver, credentialResolver, runtimes);

        var result = await invocationRuntime.InvokeAsync(
            "sess_1", "chat", new[] { new MessageDto("msg_1", "sess_1", "user", "Hello", DateTimeOffset.UtcNow) },
            CancellationToken.None);

        Assert.Equal("executed", result.Status);
        Assert.Equal("anthropic", result.Context.ProviderInstanceId);
    }

    private static StringContent JsonContent(string value)
    {
        return new StringContent(value, Encoding.UTF8, "application/json");
    }

    private sealed class StubRouteResolver(
        string providerInstanceId, string driver, string connectionKind, string effectiveModel)
        : IModelRouteResolver
    {
        public ResolvedModelInvocationContextDto Resolve(string purpose)
        {
            return new ResolvedModelInvocationContextDto(
                purpose, null, null, "https://api.example.com/v1", effectiveModel,
                null, driver, connectionKind, providerInstanceId, false);
        }
    }

    private sealed class FixedRouteResolver(ResolvedModelInvocationContextDto context) : IModelRouteResolver
    {
        public ResolvedModelInvocationContextDto Resolve(string purpose) => context;
    }

    private sealed class StubCredentialResolver(string? apiKey) : IModelCredentialResolver
    {
        public string? ResolveApiKey(ResolvedModelInvocationContextDto context) => apiKey;
    }

    private sealed class StubProviderRuntime(string id, Func<ResolvedModelInvocationContextDto, bool> canHandle)
        : IModelProviderRuntime
    {
        public string Id => id;
        public bool CanHandle(ResolvedModelInvocationContextDto context) => canHandle(context);
        public Task<ModelInvocationResultDto> GenerateAsync(
            ResolvedModelInvocationContextDto context, string? apiKey,
            IReadOnlyList<MessageDto> messages, CancellationToken cancellationToken,
            IReadOnlyList<ModelToolSpecDto>? tools = null)
        {
            return Task.FromResult(new ModelInvocationResultDto(
                "executed", $"Handled by {Id}", context, false, Id));
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handle(request));
        }
    }
}
