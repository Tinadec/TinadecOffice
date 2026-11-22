using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AspNetCore;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Skills;

namespace TinadecCore.Api.Tests;

public sealed class McpAndSkillEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tinadec-mcp-skill-{Guid.NewGuid():N}");
    private readonly string _fakeHome;
    private readonly Factory _factory;

    public McpAndSkillEngineTests()
    {
        _fakeHome = Path.Combine(_root, "home");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_fakeHome);
        _factory = new Factory(_root, _fakeHome);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task CanonicalMcpLifecycle_ImportJsonToggleTestDelete_AndRedactsSecretsEverywhere()
    {
        var client = _factory.CreateClient();

        // 1. Market routes are completely unmapped (#164)
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/market/sources")).StatusCode);

        // 2. Create HTTP MCP server with secret header
        var createResp = await client.PostAsJsonAsync("/api/v1/mcp/servers", new
        {
            id = "linear",
            name = "Linear Cloud",
            transport = "http",
            url = "https://mcp.linear.app/mcp?token=super-secret-query",
            headers = new Dictionary<string, string?> { ["Authorization"] = "Bearer secret-linear-token" },
            enabled = true,
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("linear", created.GetProperty("id").GetString());
        Assert.Equal("http", created.GetProperty("transport").GetString());
        Assert.Equal("********", created.GetProperty("headers").GetProperty("Authorization").GetString());

        // 3. Import JSON (.mcp.json + Codex TOML) via canonical /api/v1/mcp/servers/import-json
        var importJsonResp = await client.PostAsJsonAsync("/api/v1/mcp/servers/import-json", new
        {
            json = """
            {
              "mcpServers": {
                "github": {
                  "command": "npx",
                  "args": ["-y", "@modelcontextprotocol/server-github@0.6.2"],
                  "env": { "GITHUB_TOKEN": "ghp_secret_value_123" }
                }
              }
            }
            """,
        });
        Assert.Equal(HttpStatusCode.OK, importJsonResp.StatusCode);
        var imported = await importJsonResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, imported.GetProperty("imported_count").GetInt32());
        var github = imported.GetProperty("imported")[0];
        Assert.Equal("********", github.GetProperty("env").GetProperty("GITHUB_TOKEN").GetString());
        Assert.DoesNotContain("ghp_secret_value_123", await importJsonResp.Content.ReadAsStringAsync());

        // 4. Test handshake returns discovered tools cleanly
        var testResp = await client.PostAsync("/api/v1/mcp/servers/github/test", null);
        Assert.Equal(HttpStatusCode.OK, testResp.StatusCode);
        var testBody = await testResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("connected", testBody.GetProperty("status").GetString());
        Assert.Equal(1, testBody.GetProperty("tool_count").GetInt32());
        Assert.Equal("create_issue", testBody.GetProperty("tools")[0].GetProperty("name").GetString());

        // 5. Secrets materialize dynamically in ToolExecutionContext without restart, while tinadec_inspect redacts them
        var resolver = _factory.Services.GetRequiredService<IToolConfigurationResolver>();
        var ctxBefore = await resolver.ResolveAsync(null);
        Assert.Equal(2, ctxBefore.McpServers.Count);
        var materialized = await resolver.MaterializeForCallAsync(ctxBefore, ["mcp_invoke"]);
        var githubSnap = Assert.Single(materialized.McpServers, s => s.Id == "github");
        Assert.Equal("ghp_secret_value_123", githubSnap.Env!["GITHUB_TOKEN"]);

        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var inspectResp = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecInspectToolId,
            Scope(),
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "mcp" }),
            CancellationToken.None);
        Assert.True(inspectResp.IsSuccess, inspectResp.Error);
        var inspectRaw = JsonSerializer.Serialize(inspectResp.Result);
        Assert.DoesNotContain("ghp_secret_value_123", inspectRaw);
        Assert.DoesNotContain("secret-linear-token", inspectRaw);
        Assert.DoesNotContain("super-secret-query", inspectRaw);

        // 6. Direct toggle via PATCH /enabled immediately updates dynamic resolution
        var disableResp = await client.PatchAsJsonAsync("/api/v1/mcp/servers/github/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);
        Assert.False((await disableResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        var ctxAfterDisable = await resolver.ResolveAsync(null);
        Assert.DoesNotContain(ctxAfterDisable.McpServers, s => s.Id == "github");

        // 7. Re-enable via /api/v1/tools/mcp/servers/{resourceId}/enabled and delete
        var githubResourceId = github.GetProperty("resource_id").GetGuid();
        var reenableResp = await client.PatchAsJsonAsync($"/api/v1/tools/mcp/servers/{githubResourceId}/enabled", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, reenableResp.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/mcp/servers/github")).StatusCode);
    }

    [Fact]
    public async Task ApmPureFileAdapter_DiscoversSkillsMcpPromptsAndAgents_ImportsCustomAgent_RoutesSecretPlaceholders_AndFlagsHiddenUnicode()
    {
        // 1. Direct UnicodeSafetyScanner & ApmPackageParser unit checks
        var cleanWithLeadingBom = UnicodeSafetyScanner.ScanText("\uFEFF# Clean Skill\nNo smuggling here.", "SKILL.md");
        Assert.False(cleanWithLeadingBom.HasHiddenUnicode);

        var smuggledText = "# Skill\nZeroWidth:\u200B Bidi:\u202E Tag:" + char.ConvertFromUtf32(0xE0041) + " MidBom:\uFEFF";
        var smuggledScan = UnicodeSafetyScanner.ScanText(smuggledText, "SKILL.md");
        Assert.True(smuggledScan.HasHiddenUnicode);
        Assert.Equal(2, smuggledScan.ZeroWidthCount);
        Assert.Equal(1, smuggledScan.BidiOverrideCount);
        Assert.Equal(1, smuggledScan.UnicodeTagCount);
        Assert.NotEmpty(smuggledScan.Warnings);

        var parsedLock = ApmPackageParser.ParseLockfile("""
            packages:
              - key: acme/sec-audit
                repo: https://github.com/acme/sec-audit.git
                commit: 0123456789abcdef0123456789abcdef01234567
                version: 1.2.0
                integrity: sha256-deadbeefcafe00112233445566778899aabbccddeeff00112233445566778899
            """);
        var lockedPkg = parsedLock.ResolveForModulePath("apm_modules/acme/sec-audit");
        Assert.NotNull(lockedPkg);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", lockedPkg!.Commit);

        // 2. Seed an APM workspace with apm.yml, apm.lock.yaml, apm_modules skill (with hidden Unicode), .apm/prompts, and .apm/agents
        var client = _factory.CreateClient();
        var apmWorkspace = Path.Combine(_root, "apm-workspace");
        Directory.CreateDirectory(apmWorkspace);

        const string apmYaml = """
            name: sample-apm-app
            version: 0.1.0
            dependencies:
              apm:
                - acme/sec-audit
              mcp:
                apm-github:
                  command: npx
                  args: ["-y", "@modelcontextprotocol/server-github@0.6.2"]
                  env:
                    GITHUB_PERSONAL_ACCESS_TOKEN: "${GITHUB_PAT}"
            """;
        File.WriteAllText(Path.Combine(apmWorkspace, "apm.yml"), apmYaml);
        File.WriteAllText(Path.Combine(apmWorkspace, "apm.lock.yaml"), """
            packages:
              - key: acme/sec-audit
                repo: https://github.com/acme/sec-audit.git
                commit: 89abcdef0123456789abcdef0123456789abcdef
                version: 1.0.0
            """);

        var modSkillDir = Path.Combine(apmWorkspace, "apm_modules", "acme", "sec-audit");
        Directory.CreateDirectory(modSkillDir);
        File.WriteAllText(Path.Combine(modSkillDir, "SKILL.md"),
            "---\nname: sec-audit\ndescription: Security audit skill\n---\nInspect inputs carefully.\u200B");

        Directory.CreateDirectory(Path.Combine(apmWorkspace, ".apm", "prompts"));
        File.WriteAllText(Path.Combine(apmWorkspace, ".apm", "prompts", "code-review.prompt.md"),
            "---\ndescription: Review PR changes\n---\nReview ${input:target} thoroughly.");

        Directory.CreateDirectory(Path.Combine(apmWorkspace, ".apm", "agents"));
        File.WriteAllText(Path.Combine(apmWorkspace, ".apm", "agents", "security-reviewer.agent.md"),
            "---\nname: Security Reviewer\ndescription: Audits auth boundaries\nrole: verifier\ntools: [read_file, git_diff]\n---\nYou audit auth boundaries.");

        var projResp = await client.PostAsJsonAsync("/api/v1/projects", new { name = "APM Project", path = apmWorkspace });
        Assert.Equal(HttpStatusCode.Created, projResp.StatusCode);
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // 3. Discover APM items with commit SHA, SHA-256 hash, ${VAR} secret_placeholders, and hidden Unicode warnings
        var discoverResp = await client.GetAsync($"/api/v1/tools/external-cli/discover?project_id={projectId}");
        Assert.Equal(HttpStatusCode.OK, discoverResp.StatusCode);
        var items = (await discoverResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToList();

        var apmSkill = Assert.Single(items, x => x.GetProperty("ecosystem").GetString() == "apm"
            && x.GetProperty("kind").GetString() == "skill"
            && x.GetProperty("name").GetString() == "sec-audit");
        Assert.Equal("89abcdef0123456789abcdef0123456789abcdef", apmSkill.GetProperty("commit").GetString());
        Assert.False(string.IsNullOrWhiteSpace(apmSkill.GetProperty("content_hash").GetString()));
        Assert.True(apmSkill.GetProperty("has_hidden_unicode").GetBoolean());
        Assert.NotEmpty(apmSkill.GetProperty("security_warnings").EnumerateArray());

        var apmPrompt = Assert.Single(items, x => x.GetProperty("ecosystem").GetString() == "apm" && x.GetProperty("kind").GetString() == "command");
        Assert.Equal("code-review", apmPrompt.GetProperty("name").GetString());

        var apmAgent = Assert.Single(items, x => x.GetProperty("ecosystem").GetString() == "apm" && x.GetProperty("kind").GetString() == "agent");
        var apmMcp = Assert.Single(items, x => x.GetProperty("ecosystem").GetString() == "apm" && x.GetProperty("kind").GetString() == "mcp-server");
        Assert.Contains("GITHUB_PAT", apmMcp.GetProperty("secret_placeholders").EnumerateArray().Select(x => x.GetString()));

        // 4. One-click import .apm/agents/security-reviewer.agent.md -> custom AgentDefinition without touching AgentPack
        var importAgentResp = await client.PostAsJsonAsync("/api/v1/tools/external-cli/import", new
        {
            project_id = projectId,
            item_ids = new[] { apmAgent.GetProperty("item_id").GetString()! }
        });
        Assert.Equal(HttpStatusCode.OK, importAgentResp.StatusCode);
        var importAgentJson = await importAgentResp.Content.ReadFromJsonAsync<JsonElement>();
        var importedAgent = Assert.Single(importAgentJson.GetProperty("imported_agents").EnumerateArray());
        var agentsList = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/agents");
        var customAgent = Assert.Single(agentsList!, a => a.GetProperty("id").GetGuid() == importedAgent.GetProperty("id").GetGuid());
        Assert.Equal("custom", customAgent.GetProperty("source_kind").GetString());

        // 5. Import apm.yml MCP with ${GITHUB_PAT} + secret_values routed to ISecretStore, and export back safely as ${GITHUB_PAT}
        var mcpImportResp = await client.PostAsJsonAsync("/api/v1/mcp/servers/import-json", new
        {
            json = apmYaml,
            secret_values = new Dictionary<string, string> { ["GITHUB_PAT"] = "ghp_super_secret_apm_999" }
        });
        Assert.Equal(HttpStatusCode.OK, mcpImportResp.StatusCode);
        var mcpImportBody = await mcpImportResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("GITHUB_PAT", mcpImportBody.GetProperty("secret_placeholders").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("ghp_super_secret_apm_999", await mcpImportResp.Content.ReadAsStringAsync());

        var resolver = _factory.Services.GetRequiredService<IToolConfigurationResolver>();
        var materialized = await resolver.MaterializeForCallAsync(await resolver.ResolveAsync(null), ["mcp_invoke"]);
        var apmGithubSnap = Assert.Single(materialized.McpServers, s => s.Id == "apm-github");
        Assert.Equal("ghp_super_secret_apm_999", apmGithubSnap.Env!["GITHUB_PERSONAL_ACCESS_TOKEN"]);

        foreach (var fmt in new[] { "json", "apm" })
        {
            var exportResp = await client.GetAsync($"/api/v1/mcp/servers/export?format={fmt}");
            Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
            var exportText = await exportResp.Content.ReadAsStringAsync();
            Assert.DoesNotContain("ghp_super_secret_apm_999", exportText);
            Assert.DoesNotContain("********", exportText);
            Assert.Contains("${GITHUB_PAT}", exportText);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/mcp/servers/apm-github")).StatusCode);
    }

    [Fact]
    public async Task ExternalCliDiscoverAndInstall_RedactsSecrets_DefaultsStdioDisabled_AndHandlesAllSkillSources()
    {
        var client = _factory.CreateClient();

        // Seed external CLI configs across Claude Code, Codex, Z Code, MiniMax Code, and Pi
        File.WriteAllText(Path.Combine(_fakeHome, ".claude.json"), """
        {
          "mcpServers": {
            "claude-fs": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-filesystem@0.6.2", "--token=secret_arg_token"],
              "env": { "CLAUDE_API_KEY": "sk-secret-claude-123" }
            }
          }
        }
        """);
        Directory.CreateDirectory(Path.Combine(_fakeHome, ".claude", "commands"));
        File.WriteAllText(Path.Combine(_fakeHome, ".claude", "commands", "deploy-check.md"), "# Deploy Check\nRun verification.");

        Directory.CreateDirectory(Path.Combine(_fakeHome, ".codex", "skills", "codex-reviewer"));
        File.WriteAllText(Path.Combine(_fakeHome, ".codex", "skills", "codex-reviewer", "SKILL.md"),
            "---\nname: codex-reviewer\ndescription: Review diffs safely\n---\nCheck everything.");
        File.WriteAllText(Path.Combine(_fakeHome, ".codex", "config.toml"), """
        [mcp_servers.codex_http]
        url = "https://mcp.example.com/sse"
        [mcp_servers.codex_http.http_headers]
        Authorization = "Bearer codex_secret_header"
        """);

        Directory.CreateDirectory(Path.Combine(_fakeHome, ".zcode", "skills", "z-helper"));
        File.WriteAllText(Path.Combine(_fakeHome, ".zcode", "skills", "z-helper", "SKILL.md"), "# Z Helper\nDoes helpful things without frontmatter.");

        Directory.CreateDirectory(Path.Combine(_fakeHome, ".gemini", "antigravity", "skills", "agy-skill"));
        File.WriteAllText(Path.Combine(_fakeHome, ".gemini", "antigravity", "skills", "agy-skill", "SKILL.md"), "---\nname: agy-skill\ndescription: AGY skill\n---\nBody");
        File.WriteAllText(Path.Combine(_fakeHome, ".gemini", "antigravity", "mcp_config.json"), "{\"mcpServers\":{\"agy_mcp\":{\"command\":\"npx\",\"args\":[\"agy-mcp\"]}}}");

        Directory.CreateDirectory(Path.Combine(_fakeHome, ".gemini", "skills", "gem-skill"));
        File.WriteAllText(Path.Combine(_fakeHome, ".gemini", "skills", "gem-skill", "SKILL.md"), "---\nname: gem-skill\ndescription: Gemini skill\n---\nBody");

        Directory.CreateDirectory(Path.Combine(_fakeHome, ".qwen", "skills", "qwen-skill"));
        File.WriteAllText(Path.Combine(_fakeHome, ".qwen", "skills", "qwen-skill", "SKILL.md"), "---\nname: qwen-skill\ndescription: Qwen skill\n---\nBody");

        // 1. Discover redacts all secrets & flags stdio MCP default_enabled = false
        var discoverResp = await client.GetAsync("/api/v1/tools/external-cli/discover");
        Assert.Equal(HttpStatusCode.OK, discoverResp.StatusCode);
        var discoverRaw = await discoverResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-secret-claude-123", discoverRaw);
        Assert.DoesNotContain("secret_arg_token", discoverRaw);
        Assert.DoesNotContain("codex_secret_header", discoverRaw);

        var discovered = JsonDocument.Parse(discoverRaw).RootElement;
        var items = discovered.GetProperty("items").EnumerateArray().ToList();
        var claudeFs = Assert.Single(items, x => x.GetProperty("item_id").GetString() == "claude_code:mcp:claude-fs");
        Assert.True(claudeFs.GetProperty("has_secrets").GetBoolean());
        Assert.False(claudeFs.GetProperty("default_enabled").GetBoolean());

        var codexHttp = Assert.Single(items, x => x.GetProperty("item_id").GetString() == "codex:mcp:codex_http");
        Assert.True(codexHttp.GetProperty("default_enabled").GetBoolean());
        Assert.Contains(items, x => x.GetProperty("item_id").GetString() == "antigravity:mcp:agy_mcp");
        Assert.Contains(items, x => x.GetProperty("item_id").GetString() == "antigravity:skill:agy-skill");
        Assert.Contains(items, x => x.GetProperty("item_id").GetString() == "gemini_cli:skill:gem-skill");
        Assert.Contains(items, x => x.GetProperty("item_id").GetString() == "qwen_code:skill:qwen-skill");

        // 2. Import selected external items via POST /api/v1/tools/skills/install
        var importResp = await client.PostAsJsonAsync("/api/v1/tools/skills/install", new
        {
            source_type = "external_cli_import",
            item_ids = new[] { "claude_code:mcp:claude-fs", "claude_code:command:deploy-check", "codex:skill:codex-reviewer", "z_code:skill:z-helper" },
            enable_stdio_mcp = false,
        });
        Assert.Equal(HttpStatusCode.OK, importResp.StatusCode);
        var importBody = await importResp.Content.ReadFromJsonAsync<JsonElement>();
        var importedMcp = Assert.Single(importBody.GetProperty("installed_mcp_servers").EnumerateArray());
        Assert.False(importedMcp.GetProperty("enabled").GetBoolean());
        Assert.Equal("********", importedMcp.GetProperty("env").GetProperty("CLAUDE_API_KEY").GetString());
        Assert.Equal(2, importBody.GetProperty("installed_skills").GetArrayLength());
        Assert.Single(importBody.GetProperty("installed_commands").EnumerateArray());
        Assert.True(File.Exists(Path.Combine(_root, ".tinadec", "commands", "deploy-check.md")));

        // Verify raw secret migrated into ISecretStore
        var resolver = _factory.Services.GetRequiredService<IToolConfigurationResolver>();
        var mcpService = _factory.Services.GetRequiredService<IMcpServerService>();
        await mcpService.SetEnabledAsync("claude-fs", enabled: true);
        var materialized = await resolver.MaterializeForCallAsync(await resolver.ResolveAsync(null), ["mcp_invoke"]);
        Assert.Equal("sk-secret-claude-123", Assert.Single(materialized.McpServers, s => s.Id == "claude-fs").Env!["CLAUDE_API_KEY"]);

        // 3. Install from local_dir + toggle enabled + reload
        var localSkillDir = Path.Combine(_root, "local-skill");
        Directory.CreateDirectory(Path.Combine(localSkillDir, "references"));
        File.WriteAllText(Path.Combine(localSkillDir, "SKILL.md"), "---\nname: local-lint\ndescription: Lint workspace\n---\nRun linter.");
        File.WriteAllText(Path.Combine(localSkillDir, "references", "guide.txt"), "Lint guide");

        var localInstallResp = await client.PostAsJsonAsync("/api/v1/tools/skills/install", new
        {
            source_type = "local_dir",
            local_path = localSkillDir,
        });
        Assert.Equal(HttpStatusCode.OK, localInstallResp.StatusCode);
        var localInstalled = Assert.Single((await localInstallResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("installed_skills").EnumerateArray());
        var localSkillId = localInstalled.GetProperty("resource_id").GetGuid();

        var toggleSkillResp = await client.PatchAsJsonAsync($"/api/v1/tools/skills/{localSkillId}/enabled", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, toggleSkillResp.StatusCode);
        Assert.False((await toggleSkillResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());

        var reloadResp = await client.PostAsync("/api/v1/tools/skills/reload", null);
        Assert.Equal(HttpStatusCode.OK, reloadResp.StatusCode);

        // 4. Install from git_url (https GitHub URL routed through #fetch)
        var gitInstallResp = await client.PostAsJsonAsync("/api/v1/tools/skills/install", new
        {
            source_type = "git_url",
            git_url = "https://github.com/acme/skills/tree/main/skills/git-audit",
        });
        Assert.Equal(HttpStatusCode.OK, gitInstallResp.StatusCode);
        var gitInstalled = Assert.Single((await gitInstallResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("installed_skills").EnumerateArray());
        Assert.Equal("git-audit", gitInstalled.GetProperty("name").GetString());

        // 5. Route tinadec_mcp_apply & tinadec_skill_apply through the exact same canonical services
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var mcpUpsert = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecMcpApplyToolId,
            Scope(),
            Wire(CoreVirtualToolPolicy.TinadecMcpApplyToolId, new
            {
                action = "upsert",
                input = new { id = "sse-docs", transport = "sse", url = "https://docs.example.com/sse", enabled = true },
            }),
            CancellationToken.None);
        Assert.True(mcpUpsert.IsSuccess, mcpUpsert.Error);
        Assert.NotNull(await mcpService.GetAsync("sse-docs"));
    }

    [Fact]
    public async Task AgentNativeDebugTargets_ExposeRecentFailuresSystemHealthAndRunReplayOverInspectAndLoopbackMcp()
    {
        var client = _factory.CreateClient();

        // Record a 5xx failure with a secret-like token to verify redaction
        var journal = _factory.Services.GetRequiredService<ServerFailureJournal>();
        journal.Add(new ServerFailure(
            "POST",
            "/api/v1/runs?api_key=leak_me_123",
            500,
            "internal_error",
            "System.InvalidOperationException",
            "Auth header failed: Bearer sk-secret-failure-token",
            "System.Exception",
            "Root failure token=abc_secret_xyz",
            "trace-500-test",
            DateTimeOffset.UtcNow));

        // 1. recent_failures via loopback MCP resources/read
        var failRes = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "resources/read",
            @params = new { uri = "tinadec://recent-failures" },
        });
        Assert.Equal(HttpStatusCode.OK, failRes.StatusCode);
        var failBody = await failRes.Content.ReadAsStringAsync();
        Assert.Contains("trace-500-test", failBody);
        Assert.DoesNotContain("sk-secret-failure-token", failBody);
        Assert.DoesNotContain("leak_me_123", failBody);
        Assert.DoesNotContain("abc_secret_xyz", failBody);

        // 2. system_health via loopback MCP tools/call
        var healthRes = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = "tinadec_inspect", arguments = new { target = "system_health" } },
        });
        Assert.Equal(HttpStatusCode.OK, healthRes.StatusCode);
        var healthJson = await healthRes.Content.ReadFromJsonAsync<JsonElement>();
        var structuredHealth = healthJson.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal("system_health", structuredHealth.GetProperty("target").GetString());
        Assert.True(structuredHealth.TryGetProperty("readiness_items", out var readinessItems));
        Assert.True(readinessItems.GetArrayLength() > 0);
        Assert.True(structuredHealth.TryGetProperty("model_health", out _));
        Assert.True(structuredHealth.TryGetProperty("mcp_servers", out _));

        // 3. run_replay for missing run returns clean run_not_found without throwing 500
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();
        var missingReplay = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecInspectToolId,
            Scope(),
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "run_replay", run_id = Guid.NewGuid().ToString() }),
            CancellationToken.None);
        Assert.False(missingReplay.IsSuccess);
        Assert.Contains("run_not_found", missingReplay.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TinadecInspect_And_Apply_ContextMemory_And_McpResource_WorkEndToEnd()
    {
        var gateway = _factory.Services.GetRequiredService<ITinadecConfigToolGateway>();

        var inspect = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecInspectToolId,
            Scope(),
            Wire(CoreVirtualToolPolicy.TinadecInspectToolId, new { target = "context_memory" }),
            CancellationToken.None);
        Assert.True(inspect.IsSuccess, inspect.Error);
        var inspectResult = inspect.Result!.Value;
        Assert.Equal("context_memory", inspectResult.GetProperty("target").GetString());
        Assert.Equal("managed", inspectResult.GetProperty("policy").GetProperty("strategy").GetString());
        Assert.Equal(3, inspectResult.GetProperty("strategy_presets").GetArrayLength());

        var updatePolicy = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId,
            Scope(),
            Wire(CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId, new
            {
                action = "set_strategy",
                policy = new { strategy = "deep_curator", core_markdown_max_tokens = 2048 },
            }),
            CancellationToken.None);
        Assert.True(updatePolicy.IsSuccess, updatePolicy.Error);
        Assert.Equal("deep_curator", updatePolicy.Result!.Value.GetProperty("policy").GetProperty("strategy").GetString());
        Assert.Equal(2048, updatePolicy.Result!.Value.GetProperty("policy").GetProperty("core_markdown_max_tokens").GetInt32());

        var upsert = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId,
            Scope(),
            Wire(CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId, new
            {
                action = "upsert_memory",
                memory = new { scope = "principal", kind = "preference", content = "Use bun test in TinadecGateway." },
            }),
            CancellationToken.None);
        Assert.True(upsert.IsSuccess, upsert.Error);
        var itemId = upsert.Result!.Value.GetProperty("item").GetProperty("id").GetGuid();

        var supersede = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId,
            Scope(),
            Wire(CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId, new
            {
                action = "supersede_memory",
                item_id = itemId,
                memory = new { scope = "principal", kind = "preference", content = "Run bun test twice for OpenAPI snapshot." },
            }),
            CancellationToken.None);
        Assert.True(supersede.IsSuccess, supersede.Error);
        var supersededId = supersede.Result!.Value.GetProperty("item").GetProperty("id").GetGuid();

        var revoke = await gateway.ExecuteAsync(
            CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId,
            Scope(),
            Wire(CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId, new
            {
                action = "revoke_memory",
                item_id = supersededId,
                reason = "cleanup",
            }),
            CancellationToken.None);
        Assert.True(revoke.IsSuccess, revoke.Error);
        Assert.Equal("revoked", revoke.Result!.Value.GetProperty("item").GetProperty("status").GetString());

        using var client = _factory.CreateClient();
        var listRes = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 10,
            method = "tools/list",
        });
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var listBody = await listRes.Content.ReadAsStringAsync();
        Assert.Contains(CoreVirtualToolPolicy.TinadecContextMemoryApplyToolId, listBody);

        var resRead = await client.PostAsJsonAsync("/api/v1/mcp/tinadec", new
        {
            jsonrpc = "2.0",
            id = 11,
            method = "resources/read",
            @params = new { uri = "tinadec://context-memory" },
        });
        Assert.Equal(HttpStatusCode.OK, resRead.StatusCode);
        var resReadBody = await resRead.Content.ReadAsStringAsync();
        Assert.Contains("deep_curator", resReadBody);
    }

    private ToolInvocationScope Scope()
    {
        var tenant = _factory.Services.GetRequiredService<ITenantContextAccessor>().Current;
        return new(
            tenant.TenantId, tenant.WorkspaceId, tenant.PrincipalId,
            Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty,
            _root, CoreVirtualToolPolicy.TinadecConfigToolIds.ToArray(), [], "test", 600, 0, false);
    }

    private static ToolWireRequestDto Wire(string toolId, object parameters) => new()
    {
        ToolId = toolId,
        SessionId = "mcp-skill-engine-test",
        Params = JsonSerializer.SerializeToElement(parameters),
    };

    private sealed class Factory(string root, string fakeHome) : IsolatedApiFactory
    {
        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(root, "data"),
                ["TinadecTools:DefaultWorkspaceRoot"] = root,
                ["TinadecTools:ExternalCliUserHome"] = fakeHome,
                ["Logging:LogLevel:Default"] = "Warning",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolProvider>();
                services.AddSingleton<IToolProvider>(new FakeToolProvider());
            });
        }
    }

    private sealed class FakeToolProvider : IToolProvider
    {
        private static readonly string Commit = new('a', 40);

        public Task<ToolManifestDto> EnsureStartedAsync(string root, CancellationToken ct = default) => GetManifestAsync(root, ct);
        public Task<ToolManifestDto> GetManifestAsync(string root, CancellationToken ct = default) =>
            Task.FromResult(new ToolManifestDto { ProtocolVersion = 2, Tools = [] });
        public Task ShutdownAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<ToolWireResponseDto> CallAsync(
            string root,
            ToolWireRequestDto request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (request.ToolId == "mcp_list")
            {
                return Task.FromResult(new ToolWireResponseDto
                {
                    IsSuccess = true,
                    Result = JsonSerializer.SerializeToElement(new
                    {
                        config_path = Path.Combine(root, "mcp.json"),
                        servers = new[]
                        {
                            new
                            {
                                id = "github",
                                name = "github",
                                status = "connected",
                                tools = new[]
                                {
                                    new { id = "create_issue", name = "create_issue", description = "Create issue" },
                                },
                            },
                        },
                    }),
                });
            }

            if (request.ToolId == "#fetch" && request.Params is { } p && p.TryGetProperty("url", out var urlEl))
            {
                var url = urlEl.GetString() ?? string.Empty;
                var skillMdBytes = Encoding.UTF8.GetBytes("---\nname: git-audit\ndescription: Audit from git\n---\nAudit steps.");
                var gitHeader = Encoding.ASCII.GetBytes($"blob {skillMdBytes.Length}\0");
                var gitObj = new byte[gitHeader.Length + skillMdBytes.Length];
                gitHeader.CopyTo(gitObj, 0);
                skillMdBytes.CopyTo(gitObj, gitHeader.Length);
                var blobSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(gitObj));

                string body;
                if (url.Contains("/branches/main", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new { commit = new { sha = Commit } });
                }
                else if (url.Contains($"/git/trees/{Commit}", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new
                    {
                        truncated = false,
                        tree = new[]
                        {
                            new { path = "skills/git-audit/SKILL.md", sha = blobSha, mode = "100644", size = skillMdBytes.Length, type = "blob" },
                        },
                    });
                }
                else if (url.Contains("/git/blobs/", StringComparison.Ordinal))
                {
                    body = JsonSerializer.Serialize(new { sha = blobSha, content = Convert.ToBase64String(skillMdBytes), encoding = "base64" });
                }
                else
                {
                    body = "{}";
                }

                return Task.FromResult(new ToolWireResponseDto
                {
                    IsSuccess = true,
                    Result = JsonSerializer.SerializeToElement(new { success = true, body }),
                });
            }

            return Task.FromResult(new ToolWireResponseDto { IsSuccess = true, Result = JsonSerializer.SerializeToElement(new { }) });
        }
    }
}
