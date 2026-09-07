using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// 用户级运行时绑定（配置体验改造 A）的往返契约。此前该链路零测试覆盖，导致三处
/// 不一致长期潜伏：(1) AgentModelResolver.PreviewAsync 缺 user_binding 档，智能体中心
/// 保存绑定后 effective_previews 仍反映保存前的解析链；(2) PutAgentRuntimeBinding 对
/// fixed 强制要求 model 非空，CLI/ACP 运行时（model 合法为 null）保存必然 400；
/// (3) FormalModeResolver 对 null-model 绑定静默忽略。本套钉住 (1)(2) 的端点可观测行为。
/// </summary>
public sealed class AgentRuntimeBindingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-agent-runtime-binding-tests", Guid.NewGuid().ToString("N"));
    private AgentRuntimeBindingFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new AgentRuntimeBindingFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A1 + 往返持久化：PUT fixed 绑定后，GET /agents 回显 model_binding，且该智能体的
    /// 每个 effective_preview 的 strategy_source 都收敛到 user_binding（绑定优先于
    /// mode_node_override 与 agent_version，与 FormalModeResolver 的运行时优先级一致）。
    /// </summary>
    [Fact]
    public async Task PutRuntimeBinding_Then_ListAgents_Returns_Binding_And_Preview_Reflects_UserBinding()
    {
        var client = _factory!.CreateClient();

        // fixed 策略需要一个真实 provider 行；冻结不会真正调用它。
        using var providerResponse = await client.PostAsJsonAsync("/api/v1/model-providers",
            new { driver = "openai", display_name = "Binding Provider", base_url = "http://localhost", model = "binding-model", api_key = "sk-test" });
        var providerBody = await providerResponse.Content.ReadAsStringAsync();
        Assert.True(providerResponse.IsSuccessStatusCode, $"provider save failed: {providerResponse.StatusCode} {providerBody}");
        var providerId = JsonSerializer.Deserialize<JsonElement>(providerBody).GetProperty("id").GetGuid();

        var agents = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        Assert.NotNull(agents);
        var target = agents!.Single(a => a.GetProperty("slug").GetString() == "meeting"
            && a.GetProperty("source_kind").GetString() == "bootstrap");
        var agentId = target.GetProperty("id").GetGuid();

        using var putResponse = await client.PutAsJsonAsync($"/api/v1/agents/{agentId}/runtime-binding",
            new { mode = "fixed", provider_instance_id = providerId, model = "binding-model" });
        var putBody = await putResponse.Content.ReadAsStringAsync();
        Assert.True(putResponse.IsSuccessStatusCode, $"runtime-binding save failed: {putResponse.StatusCode} {putBody}");
        var saved = JsonSerializer.Deserialize<JsonElement>(putBody);
        Assert.Equal("fixed", saved.GetProperty("mode").GetString());
        Assert.Equal(providerId, saved.GetProperty("provider_instance_id").GetGuid());
        Assert.Equal("binding-model", saved.GetProperty("model").GetString());

        var after = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        Assert.NotNull(after);
        var agentAfter = after!.Single(a => a.GetProperty("id").GetGuid() == agentId);
        var binding = agentAfter.GetProperty("model_binding");
        Assert.Equal("fixed", binding.GetProperty("mode").GetString());
        Assert.Equal(providerId, binding.GetProperty("provider_instance_id").GetGuid());
        Assert.Equal("binding-model", binding.GetProperty("model").GetString());

        var previews = agentAfter.GetProperty("effective_previews").EnumerateObject()
            .Select(p => p.Value).Where(v => v.TryGetProperty("strategy_source", out _)).ToArray();
        Assert.NotEmpty(previews);
        Assert.All(previews, p => Assert.Equal("user_binding", p.GetProperty("strategy_source").GetString()));
    }

    /// <summary>
    /// A2 + A1：CLI/ACP 运行时（driver=claude-cli → protocol=acp）的 fixed 绑定 model 为 null
    /// 应被接受（200 而非 400），并同样产出 user_binding 预览——BindingToStrategy 不因 model
    /// 为空而跳过，FreezeStrategyAsync 对 ACP/OpencodeServe 协议放行 null model。
    /// </summary>
    [Fact]
    public async Task PutRuntimeBinding_With_CliProvider_And_Null_Model_Is_Accepted()
    {
        var client = _factory!.CreateClient();

        using var providerResponse = await client.PostAsJsonAsync("/api/v1/model-providers",
            new { driver = "claude-cli", display_name = "Claude Code", connection_kind = "cli" });
        var providerBody = await providerResponse.Content.ReadAsStringAsync();
        Assert.True(providerResponse.IsSuccessStatusCode, $"cli provider save failed: {providerResponse.StatusCode} {providerBody}");
        var providerId = JsonSerializer.Deserialize<JsonElement>(providerBody).GetProperty("id").GetGuid();

        var agents = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        Assert.NotNull(agents);
        var target = agents!.Single(a => a.GetProperty("slug").GetString() == "worker.browser"
            && a.GetProperty("source_kind").GetString() == "bootstrap");
        var agentId = target.GetProperty("id").GetGuid();

        using var putResponse = await client.PutAsJsonAsync($"/api/v1/agents/{agentId}/runtime-binding",
            new { mode = "fixed", provider_instance_id = providerId, model = (string?)null });
        var putBody = await putResponse.Content.ReadAsStringAsync();
        Assert.True(putResponse.IsSuccessStatusCode, $"cli fixed binding should be accepted: {putResponse.StatusCode} {putBody}");
        var saved = JsonSerializer.Deserialize<JsonElement>(putBody);
        Assert.Equal("fixed", saved.GetProperty("mode").GetString());
        Assert.Equal(providerId, saved.GetProperty("provider_instance_id").GetGuid());
        // Core 的 JSON 序列化省略 null 值，故 CLI 绑定的 model 为 null 时该键缺席；
        // 两种形态（null / 缺席）都合法，Desktop AgentModelBindingDto.model 亦为可选。
        var modelKind = saved.TryGetProperty("model", out var modelProp) ? modelProp.ValueKind : JsonValueKind.Undefined;
        Assert.True(modelKind is JsonValueKind.Null or JsonValueKind.Undefined, $"expected null/absent model, got {modelKind}");

        var after = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        Assert.NotNull(after);
        var agentAfter = after!.Single(a => a.GetProperty("id").GetGuid() == agentId);
        var previews = agentAfter.GetProperty("effective_previews").EnumerateObject()
            .Select(p => p.Value).Where(v => v.TryGetProperty("strategy_source", out _)).ToArray();
        Assert.NotEmpty(previews);
        Assert.All(previews, p => Assert.Equal("user_binding", p.GetProperty("strategy_source").GetString()));
    }

    private sealed class AgentRuntimeBindingFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public AgentRuntimeBindingFactory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
