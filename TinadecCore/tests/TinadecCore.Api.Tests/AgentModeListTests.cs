using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TinadecCore.Api.Tests;

/// <summary>
/// 模式选择下拉的服务端契约（配置体验改造 B）：GET /api/v1/agent-modes 默认返回
/// 全部行（pack 花名册合约），可选项的 published 过滤由客户端按 status + 
/// latest_published_mode_version_id 做；显式 ?status=published 可做服务端过滤。
/// 每项携带 latest_published_mode_version_id —— 下拉提交的 mode_version_id 由此而来，
/// 类型混淆（AgentMode id 当 ModeVersion id）不再可能。
/// </summary>
public sealed class AgentModeListTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-agent-mode-list-tests", Guid.NewGuid().ToString("N"));
    private AgentModeListFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new AgentModeListFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AgentModes_DefaultList_ReturnsPublishedOnly_WithLatestVersionIds()
    {
        var client = _factory!.CreateClient();
        // 服务端过滤：?status=published 恰好返回 bootstrap 的 7 个 published mode
        // （default-mode + 6 个 conversation.*）。
        var response = await client.GetAsync("/api/v1/agent-modes?status=published");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var modes = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.Equal(7, modes!.Length);
        Assert.All(modes, mode =>
        {
            Assert.Equal("published", mode.GetProperty("status").GetString());
            var versionId = mode.GetProperty("latest_published_mode_version_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(versionId), $"{mode.GetProperty("slug").GetString()} 缺少 latest_published_mode_version_id");
        });
        Assert.Contains(modes, mode => mode.GetProperty("slug").GetString() == "default-mode");
        Assert.Contains(modes, mode => mode.GetProperty("slug").GetString() == "conversation.plan");

        // 显式 status=draft 仍可用于智能体中心画布（本工作区无 draft，应为空）。
        var drafts = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes?status=draft");
        Assert.Empty(drafts!);
    }

    [Fact]
    public async Task AgentModes_DefaultList_ReturnsAllRows_WithLatestVersionIds()
    {
        var client = _factory!.CreateClient();
        var modes = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agent-modes");
        Assert.Equal(7, modes!.Length);
        Assert.All(modes, mode =>
        {
            var versionId = mode.GetProperty("latest_published_mode_version_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(versionId), $"{mode.GetProperty("slug").GetString()} 缺少 latest_published_mode_version_id");
        });
    }

    [Fact]
    public async Task Interactions_WithRandomModeVersionId_IsRejectedAsInvalidRequest()
    {
        var client = _factory!.CreateClient();
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "mode-list", path = Path.Combine(_root, "workspace") })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), title = "mode list session" })).Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        // 不存在的 mode_version_id 在预校验被拒（400 invalid_request），静默降级已被封死。
        var response = await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/interactions", new { content = "目标", dispatch_mode = "queued", mode_version_id = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("code").GetString());
    }

    private sealed class AgentModeListFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public AgentModeListFactory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
