using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Api.Tests;

/// <summary>
/// A workspace's own <c>SKILL.md</c> files reaching the model as an index. Progressive disclosure is
/// the property under test in both directions: what must arrive (name, description, path) and what
/// must not (the body). Everything else here is the discovery contract the reference format states,
/// checked through the real provider and the real filesystem.
/// </summary>
public sealed class WorkspaceSkillApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-core-skill-tests", Guid.NewGuid().ToString("N"));
    private SkillFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new SkillFactory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a workspace holding exactly <paramref name="files"/> (paths are relative, directories
    /// are created as needed), then a project and a session bound to it.
    /// </summary>
    private async Task<(string WorkspaceRoot, Guid SessionId, Guid ProjectId)> OpenWorkspaceAsync(
        string label, params (string Path, string Content)[] files)
    {
        var workspaceRoot = Path.Combine(_root, label + "-workspace");
        foreach (var (relative, content) in files)
        {
            var target = Path.Combine(workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
        Directory.CreateDirectory(workspaceRoot);

        var client = _factory!.CreateClient();
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects",
            new { name = $"Skills {label}", path = workspaceRoot });
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var projectId = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions",
            new { project_id = projectId, title = $"Skills {label}" });
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (workspaceRoot, sessionId, projectId);
    }

    private static FrozenWorkspaceBinding BindingOf(Guid projectId, string root) =>
        new(projectId, root, [], false, null, [], false, WorkspacePathContracts.AbsoluteInRoot);

    private static string Skill(string name, string description, string body = "") =>
        $"---\nname: {name}\ndescription: {description}\n---\n\n{body}";

    private async Task<ContextEvidence?> SkillEvidenceAsync(
        Guid sessionId, FrozenWorkspaceBinding? workspace, int? tokenBudget = null, string? runId = null)
    {
        var provider = _factory!.Services.GetRequiredService<IContextProvider>();
        var pack = await provider.BuildContextAsync(new ContextBuildRequest(sessionId.ToString(), runId)
        {
            Workspace = workspace,
            TokenBudget = tokenBudget,
        });
        return pack.Evidence.FirstOrDefault(item => item.Source == "workspace_skills");
    }

    // ── the disclosure rule ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SkillsAreAdvertisedByNameAndDescriptionAndNothingElse()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("index",
            (".tinadec/skills/release-notes/SKILL.md",
                Skill("release-notes", "Summarise what changed since the last tag.",
                    "BODY-MUST-NOT-TRAVEL-9d2a: open the changelog and diff the tags.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("release-notes", evidence!.Content);
        Assert.Contains("Summarise what changed since the last tag.", evidence.Content);
        Assert.Contains(".tinadec/skills/release-notes/SKILL.md", evidence.Content);
        // The whole economy of the format rests on this: an index that inlined bodies would charge
        // every turn of every run for skills it never used.
        Assert.DoesNotContain("BODY-MUST-NOT-TRAVEL-9d2a", evidence.Content);
        Assert.Equal("1", evidence.Metadata["skill_count"]);
        Assert.True(evidence.EstimatedTokens > 0);
    }

    [Fact]
    public async Task TheIndexTellsTheModelTheBodyIsSomewhereElseAndWhereItRanks()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("framing",
            (".tinadec/skills/one/SKILL.md", Skill("one", "First skill.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("never its body", evidence!.Content);
        Assert.Contains("open its file with a file tool", evidence.Content);
        Assert.Contains("rank below the run's frozen permissions", evidence.Content);
    }

    // ── discovery ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FlatAndGroupedLayoutsAreBothFoundWhileASkillsOwnSubtreeIsNot()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("layout",
            (".tinadec/skills/flat/SKILL.md", Skill("flat", "At one level.")),
            (".tinadec/skills/grouped/nested/SKILL.md", Skill("nested", "Under a grouping directory.")),
            // Inside a skill package: references/ and its own deeper files belong to that skill, and
            // advertising them as separate skills would turn one package into several index rows.
            (".tinadec/skills/flat/references/deep/SKILL.md", Skill("deep", "Should not be a skill.")),
            // Not a skill location at all.
            ("docs/SKILL.md", Skill("docs", "In the wrong place.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains(".tinadec/skills/flat/SKILL.md", evidence!.Content);
        Assert.Contains(".tinadec/skills/grouped/nested/SKILL.md", evidence.Content);
        Assert.DoesNotContain(".tinadec/skills/flat/references/deep/SKILL.md", evidence.Content);
        Assert.DoesNotContain("In the wrong place.", evidence.Content);
        Assert.Equal("2", evidence.Metadata["skill_count"]);
    }

    [Fact]
    public async Task AWorkspaceWithoutSkillsProducesNoEvidence()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("empty",
            ("README.md", "No skills here."));

        Assert.Null(await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot)));
    }

    [Fact]
    public async Task AProjectlessRunIsNotShownSomeonesElseSkills()
    {
        var client = _factory!.CreateClient();
        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions", new { title = "Projectless skills" });
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Null(await SkillEvidenceAsync(sessionId, workspace: null));
    }

    // ── refusal, out loud ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ADescriptionlessSkillIsRefusedWithItsPathAndItsReason()
    {
        // The reference logs a warning for this and moves on. A desktop workbench has no log window
        // for the person who wrote the file, and "I added a skill and the agent ignores it" is
        // otherwise undiagnosable.
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("refused",
            (".tinadec/skills/broken/SKILL.md", "---\nname: broken\n---\n\nNo description declared."),
            (".tinadec/skills/healthy/SKILL.md", Skill("healthy", "Fine.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains(".tinadec/skills/broken/SKILL.md", evidence!.Content);
        Assert.Contains("frontmatter has no 'description'", evidence.Content);
        Assert.Contains(".tinadec/skills/healthy/SKILL.md", evidence.Content);
        Assert.Equal("1", evidence.Metadata["skill_count"]);
        Assert.Equal("1", evidence.Metadata["skill_refused"]);
    }

    [Fact]
    public async Task ASkillWhoseNameDisagreesWithItsDirectoryIsRefused()
    {
        // The index line is the only thing the model has, and it acts on it by opening a path. If the
        // name and the directory disagree, "use the skill called X" and "open path Y" stop naming one
        // thing — which is how a copied skill directory keeps describing its original.
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("mismatch",
            (".tinadec/skills/actual-dir/SKILL.md", Skill("other-dir", "Named after somewhere else.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("does not match its directory 'actual-dir'", evidence!.Content);
        Assert.Equal("0", evidence.Metadata["skill_count"]);
    }

    [Fact]
    public async Task ADuplicateNameAdvertisesOneSkillAndNamesTheOtherAsASupersededTwin()
    {
        // Identically-named directories under different groups are the only way two skills can collide,
        // because a skill's name has to match its own directory. Which one wins is decided by the
        // ordinal order of the group names, not by the order the filesystem happens to hand back.
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("duplicate",
            (".tinadec/skills/writing/probe/SKILL.md", Skill("probe", "Later in the walk.")),
            (".tinadec/skills/reviewing/probe/SKILL.md", Skill("probe", "First in the walk.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("First in the walk.", evidence!.Content);
        // The loser is named, with its reason — but its description never reaches the model, because
        // an index that advertised both would be asking which "probe" was meant.
        Assert.DoesNotContain("Later in the walk.", evidence.Content);
        Assert.Contains(
            ".tinadec/skills/writing/probe/SKILL.md: another skill already claims the name 'probe'",
            evidence.Content);
        Assert.Equal("1", evidence.Metadata["skill_count"]);
    }

    [Fact]
    public async Task AFrontmatterBlockSavedWithABomIsStillRead()
    {
        // "UTF-8 with BOM" is a default in some Windows editors. Refusing it would make one directory
        // work on one machine and not on another, with no difference the author can see.
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("bom",
            (".tinadec/skills/bommed/SKILL.md", "﻿" + Skill("bommed", "Described past the byte-order mark.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("Described past the byte-order mark.", evidence!.Content);
        Assert.Equal("1", evidence.Metadata["skill_count"]);
    }

    // ── budgets and stability ───────────────────────────────────────────────────────

    [Fact]
    public async Task ATinyBudgetDropsTheIndexInsteadOfAnnouncingNothing()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("budget",
            (".tinadec/skills/only/SKILL.md", Skill("only", "The one skill.")));

        // 40 characters cannot fit the framing sentence plus a row. Emitting the header alone would
        // spend the run's budget telling it to look for skills the pack refused to name.
        Assert.Null(await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot), tokenBudget: 40));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot), tokenBudget: 20_000);
        Assert.NotNull(evidence);
        Assert.Contains(".tinadec/skills/only/SKILL.md", evidence!.Content);
    }

    [Fact]
    public async Task OneRunKeepsItsIndexAndTheNextRunSeesANewSkill()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("stability",
            (".tinadec/skills/first/SKILL.md", Skill("first", "There at admission.")));
        var binding = BindingOf(projectId, workspaceRoot);

        var before = await SkillEvidenceAsync(sessionId, binding, runId: "run-stable-1");
        Assert.NotNull(before);
        Assert.Contains("first", before!.Content);

        var added = Path.Combine(workspaceRoot, ".tinadec", "skills", "second", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(added)!);
        File.WriteAllText(added, Skill("second", "Added while the run was in flight."));

        var during = await SkillEvidenceAsync(sessionId, binding, runId: "run-stable-1");
        Assert.NotNull(during);
        Assert.Contains("first", during!.Content);
        Assert.DoesNotContain(".tinadec/skills/second/SKILL.md", during.Content);

        var after = await SkillEvidenceAsync(sessionId, binding, runId: "run-stable-2");
        Assert.NotNull(after);
        Assert.Contains(".tinadec/skills/second/SKILL.md", after!.Content);
    }

    [Fact]
    public async Task SharedPackagesUseExactBindingsImmutableVersionsAndConditionalSaves()
    {
        var (_, _, projectId) = await OpenWorkspaceAsync("shared-shadow",
            (".tinadec/skills/probe/SKILL.md", Skill("probe", "Project procedure.")));
        var client = _factory!.CreateClient();
        var input = new
        {
            scope = "shared", name = "probe", content = Skill("probe", "Shared procedure.", "old-body"),
            files = new Dictionary<string, string> { ["references/example.txt"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("asset-body")) },
        };
        Assert.Equal((HttpStatusCode)428, (await client.PostAsJsonAsync("/api/v1/tools/skills/import", input)).StatusCode);
        using var import = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tools/skills/import") { Content = JsonContent.Create(input) };
        import.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        var create = await client.SendAsync(import);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var receipt = await create.Content.ReadFromJsonAsync<JsonElement>();
        // The write is queued, not done: the settings surface has to show what a human is approving,
        // and nothing may be readable under this id until that approval completes.
        Assert.Equal("pending", receipt.GetProperty("availability").GetString());
        Assert.Equal("awaiting_user", receipt.GetProperty("action_status").GetString());
        Assert.Contains("references/example.txt", receipt.GetProperty("package_files").GetRawText());
        var id = receipt.GetProperty("resource_id").GetGuid();
        var actionId = receipt.GetProperty("user_action_id").GetGuid();
        Assert.Null(await _factory.Services.GetRequiredService<IToolSkillResourceService>().GetAsync(id, null));

        var shared = await ApproveSkillAsync(id, actionId);
        Assert.NotNull(shared);
        var revision = shared!.Value.GetProperty("revision").GetInt64();
        var catalog = _factory.Services.GetRequiredService<IToolSkillCatalog>();
        var inherited = await catalog.ResolveAsync(projectId, null);
        Assert.Single(inherited);
        Assert.Equal("Project procedure.", inherited[0].Description);
        var explicitShared = Assert.Single(await catalog.ResolveAsync(projectId, [id]));
        Assert.Equal("Shared procedure.", explicitShared.Description);
        Assert.Equal("asset-body", await File.ReadAllTextAsync(Path.Combine(explicitShared.RootPath, "references", "example.txt")));
        var retainedBefore = Assert.Single((await catalog.CaptureSelectedAsync(projectId, [id])).Skills);
        Assert.Empty(await catalog.ResolveAsync(projectId, []));
        await Assert.ThrowsAsync<ToolSettingsException>(() => catalog.ResolveAsync(projectId, [Guid.NewGuid()]));

        var noCondition = await client.PutAsJsonAsync($"/api/v1/tools/skills/{id}", new { content = Skill("probe", "New procedure.", "new-body") });
        Assert.Equal((HttpStatusCode)428, noCondition.StatusCode);
        using var save = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tools/skills/{id}")
        {
            Content = JsonContent.Create(new { content = Skill("probe", "New procedure.", "new-body") }),
        };
        save.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        var saved = await client.SendAsync(save);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var updateReceipt = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("awaiting_user", updateReceipt.GetProperty("action_status").GetString());
        var updated = await ApproveSkillAsync(id, updateReceipt.GetProperty("user_action_id").GetGuid());
        Assert.NotNull(updated);
        var next = Assert.Single(await catalog.ResolveAsync(projectId, [id]));
        var retainedAfter = Assert.Single((await catalog.CaptureSelectedAsync(projectId, [id])).Skills);
        Assert.Equal(explicitShared.RootPath, next.RootPath);
        Assert.NotEqual(retainedBefore.RootPath, retainedAfter.RootPath);
        Assert.Contains("old-body", await File.ReadAllTextAsync(retainedBefore.SkillPath));
        Assert.Contains("new-body", await File.ReadAllTextAsync(next.SkillPath));
        Assert.Equal("asset-body", await File.ReadAllTextAsync(Path.Combine(next.RootPath, "references", "example.txt")));
        using var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tools/skills/{id}") { Content = JsonContent.Create(new { enabled = false }) };
        stale.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tools/skills?project_id={projectId}");
        Assert.Equal(2, list.GetProperty("skills").GetArrayLength());
        var shadowed = list.GetProperty("skills").EnumerateArray().Single(x => x.GetProperty("scope").GetString() == "shared");
        Assert.Contains("shadowed", shadowed.GetProperty("reason").GetString());
        Assert.Equal("shadowed", shadowed.GetProperty("reason_code").GetString());

        // Source edits are allowed; management must notice them and refuse a stale review.
        var beforeEdit = (await _factory.Services.GetRequiredService<IToolSkillResourceService>().GetAsync(id, projectId))!;
        await File.WriteAllTextAsync(next.SkillPath, Skill("probe", "Source edited procedure.", "edited-body"));
        var afterEdit = (await _factory.Services.GetRequiredService<IToolSkillResourceService>().GetAsync(id, projectId))!;
        Assert.NotEqual(beforeEdit.Revision, afterEdit.Revision); Assert.Equal("Source edited procedure.", afterEdit.Description);
        using var staleSource = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tools/skills/{id}") { Content = JsonContent.Create(new { enabled = false }) };
        staleSource.Headers.TryAddWithoutValidation("If-Match", $"\"{beforeEdit.Revision}\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(staleSource)).StatusCode);
        Assert.Contains("new-body", await File.ReadAllTextAsync(retainedAfter.SkillPath));
    }

    [Fact]
    public async Task ARejectedSharedSkillApprovalLeavesNothingBehind()
    {
        var client = _factory!.CreateClient();
        var input = new
        {
            scope = "shared", name = "rejected-skill", content = Skill("rejected-skill", "Never lands."),
        };
        using var import = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tools/skills/import") { Content = JsonContent.Create(input) };
        import.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        var created = await client.SendAsync(import);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = receipt.GetProperty("resource_id").GetGuid();
        var actionId = receipt.GetProperty("user_action_id").GetGuid();

        var queued = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        var permission = await client.PostAsJsonAsync(
            $"/api/v1/governance/permission-requests/{queued.GetProperty("permission_request_id").GetString()}/decision",
            new { approve = true, reason = "Envelope approved; the write itself is refused next." });
        Assert.Equal(HttpStatusCode.Accepted, permission.StatusCode);
        queued = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        var refused = await client.PostAsJsonAsync(
            $"/api/v1/approvals/{queued.GetProperty("action_approval_id").GetString()}/decision",
            new { decision = "rejected", reason = "Reviewer refused the skill write." });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);

        // A refusal is terminal: the resource never becomes discoverable, and no package directory
        // is left in Core content storage for a write that was turned down.
        Assert.Equal(0, (await _factory.Services.GetRequiredService<IToolSkillResourceService>().ListAsync(null)).Skills.Count(x => x.ResourceId == id));
        // "blocked" is the durable terminal status for a refused approval — the same value the
        // rest of the user-tool-action surface uses, not a skill-specific spelling.
        Assert.Equal("blocked", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task PackageDetailsExposeFilesAndAssetReplacementRequiresExplicitOptIn()
    {
        var client = _factory!.CreateClient();
        var input = new
        {
            scope = "shared", name = "package-details", source = "market", version = "1.2.3", commit = "0123456789abcdef",
            content = Skill("package-details", "Package detail test."),
            files = new Dictionary<string, string> { ["references/guide.txt"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("guide")) },
        };
        using var import = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tools/skills/import") { Content = JsonContent.Create(input) };
        import.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        var created = await client.SendAsync(import);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var receipt = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("references/guide.txt", receipt.GetProperty("package_files").GetRawText());
        var id = receipt.GetProperty("resource_id").GetGuid();
        await ApproveSkillAsync(id, receipt.GetProperty("user_action_id").GetGuid());
        var dto = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tools/skills/{id}");
        var file = await client.GetFromJsonAsync<JsonElement>($"/api/v1/tools/skills/{id}/files?path=references%2Fguide.txt");
        Assert.Equal("guide", file.GetProperty("content").GetString());
        Assert.Equal("guide", Encoding.UTF8.GetString(Convert.FromBase64String(file.GetProperty("base64").GetString()!)));
        var revision = dto.GetProperty("revision").GetInt64();
        using var accidental = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tools/skills/{id}") { Content = JsonContent.Create(new { content = Skill("package-details", "changed") , files = new Dictionary<string,string>() }) };
        accidental.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        Assert.Equal((HttpStatusCode)428, (await client.SendAsync(accidental)).StatusCode);
    }

    [Fact]
    public async Task FrozenAgentSkillIndexesDoNotCrossContaminateWithinOneRun()
    {
        var (root, sessionId, projectId) = await OpenWorkspaceAsync("agent-index",
            (".tinadec/skills/coding/SKILL.md", Skill("coding", "Code procedure.")),
            (".tinadec/skills/research/SKILL.md", Skill("research", "Research procedure.")));
        var resources = await _factory!.Services.GetRequiredService<IToolSkillCatalog>().ResolveAsync(projectId, null);
        var context = _factory.Services.GetRequiredService<IContextProvider>();
        async Task<ContextEvidence?> Index(string name)
        {
            var pack = await context.BuildContextAsync(new ContextBuildRequest(sessionId.ToString(), "same-run", AgentId: name)
            {
                Workspace = BindingOf(projectId, root),
                ToolExecutionContext = new ToolExecutionContextDto
                {
                    AgentDefinitionId = Guid.NewGuid(), SettingsHash = name,
                    Settings = JsonSerializer.SerializeToElement(new { skills = new { enabled = true, max_skills = 40, max_index_chars = 8000 } }),
                    SkillResources = resources.Where(x => x.Name == name).ToArray(),
                    AllowedToolIds = ["read_file", "command_run"],
                },
            });
            return pack.Evidence.FirstOrDefault(x => x.Source == "workspace_skills");
        }
        var results = await Task.WhenAll(Index("coding"), Index("research"));
        Assert.Contains("Code procedure.", results[0]!.Content);
        Assert.DoesNotContain("Research procedure.", results[0]!.Content);
        Assert.Contains("read_file, command_run", results[0]!.Content);
        Assert.Contains("Research procedure.", results[1]!.Content);
        Assert.DoesNotContain("Code procedure.", results[1]!.Content);
        Assert.Equal("coding", results[0]!.Metadata["tool_settings_hash"]);
        Assert.Equal("research", results[1]!.Metadata["tool_settings_hash"]);
    }

    [Theory]
    [InlineData("shell", false, true, "shell")]
    [InlineData("command_run", false, true, "command_run")]
    [InlineData("read_file", true, false, "read_file")]
    [InlineData("ls", true, false, null)]
    [InlineData("stat", true, false, null)]
    [InlineData("read_file", false, false, null)]
    [InlineData("shell", true, false, null)]
    [InlineData("*", false, true, "shell, command_run")]
    [InlineData("", true, true, null)]
    public async Task FrozenSkillIndexNamesOnlyEnabledBodyReaders(string tool, bool readEnabled, bool shellEnabled, string? expected)
    {
        var (root, sessionId, projectId) = await OpenWorkspaceAsync("body-readers-" + Guid.NewGuid().ToString("N"),
            (".tinadec/skills/probe/SKILL.md", Skill("probe", "One procedure.", "BODY-MUST-STAY-ON-DISK")));
        var resources = await _factory!.Services.GetRequiredService<IToolSkillCatalog>().ResolveAsync(projectId, null);
        var pack = await _factory.Services.GetRequiredService<IContextProvider>().BuildContextAsync(new ContextBuildRequest(sessionId.ToString(), null)
        {
            Workspace = BindingOf(projectId, root),
            AllowedToolIds = tool.Length == 0 ? [] : [tool],
            ToolExecutionContext = new ToolExecutionContextDto
            {
                Settings = JsonSerializer.SerializeToElement(new { skills = new { enabled = true }, read = new { enabled = readEnabled }, shell = new { enabled = shellEnabled } }),
                SkillResources = resources,
            },
        });
        if (expected is null)
        {
            Assert.DoesNotContain(pack.Evidence, x => x.Source == "workspace_skills");
            Assert.DoesNotContain(pack.Dropped, x => x.Source == "workspace_skills");
            return;
        }
        var text = Assert.Single(pack.Evidence.Where(x => x.Source == "workspace_skills")).Content;
        Assert.Contains(expected, text);
        Assert.DoesNotContain("open its file with a file tool", text);
        Assert.DoesNotContain("BODY-MUST-STAY-ON-DISK", text);
    }

    [Fact]
    public async Task SkillProviderDiscoversOnlyTheExplicitlyBoundProject()
    {
        var (_, _, projectId) = await OpenWorkspaceAsync("provider-project",
            (".tinadec/skills/probe/SKILL.md", Skill("probe", "Project procedure.")));
        var (_, _, otherProjectId) = await OpenWorkspaceAsync("provider-other");
        var provider = _factory!.Services.GetRequiredService<ISkillProvider>();
        Assert.Empty(await provider.ListSkillsAsync());
        var skill = Assert.Single(await provider.ListSkillsForProjectAsync(projectId));
        Assert.Equal("probe", skill.Name);
        Assert.NotNull(await provider.GetSkillForProjectAsync(skill.Id, projectId));
        Assert.Null(await provider.GetSkillAsync(skill.Id));
        Assert.Null(await provider.GetSkillForProjectAsync(skill.Id, otherProjectId));
    }

    [Fact]
    public async Task ProjectAdmissionRetainsBodyAndAssetsWhileManagementUsesLiveFiles()
    {
        var (root, _, projectId) = await OpenWorkspaceAsync("retained-project",
            (".tinadec/skills/probe/SKILL.md", Skill("probe", "Original procedure.", "old-body")),
            (".tinadec/skills/probe/references/guide.txt", "old-asset"));
        var resolver = _factory!.Services.GetRequiredService<IToolConfigurationResolver>();
        var first = await resolver.ResolveForRunAsync(projectId, []);
        var original = Assert.Single(first.SharedContext.SkillResources);
        Assert.False(WorkspaceSkillDiscovery.IsContained(root, original.RootPath));
        Assert.Contains("old-body", await File.ReadAllTextAsync(original.SkillPath));
        var liveBody = Path.Combine(root, ".tinadec", "skills", "probe", "SKILL.md");
        var liveAsset = Path.Combine(root, ".tinadec", "skills", "probe", "references", "guide.txt");
        await File.WriteAllTextAsync(liveBody, Skill("probe", "Updated procedure.", "new-body"));
        await File.WriteAllTextAsync(liveAsset, "new-asset");
        var second = await resolver.ResolveForRunAsync(projectId, []);
        var updated = Assert.Single(second.SharedContext.SkillResources);
        Assert.NotEqual(original.RootPath, updated.RootPath);
        Assert.Contains("old-body", await File.ReadAllTextAsync(original.SkillPath));
        Assert.Equal("old-asset", await File.ReadAllTextAsync(Path.Combine(original.RootPath, "references", "guide.txt")));
        Assert.Contains("new-body", await File.ReadAllTextAsync(updated.SkillPath));
        Assert.Equal("new-asset", await File.ReadAllTextAsync(Path.Combine(updated.RootPath, "references", "guide.txt")));
        var management = Assert.Single((await _factory.Services.GetRequiredService<IToolSkillResourceService>().ListAsync(projectId)).Skills);
        Assert.Equal(liveBody, management.Path);
        Assert.Equal("Updated procedure.", management.Description);
    }

    [Fact]
    public async Task ProjectAdmissionCapturesAssetOnlyChangesAsAnotherVersion()
    {
        var (root, _, projectId) = await OpenWorkspaceAsync("retained-assets",
            (".tinadec/skills/probe/SKILL.md", Skill("probe", "Same procedure.")),
            (".tinadec/skills/probe/references/guide.txt", "first"));
        var catalog = _factory!.Services.GetRequiredService<IToolSkillCatalog>();
        var first = Assert.Single((await catalog.CaptureAsync(projectId)).Skills);
        await File.WriteAllTextAsync(Path.Combine(root, ".tinadec", "skills", "probe", "references", "guide.txt"), "second");
        var second = Assert.Single((await catalog.CaptureAsync(projectId)).Skills);
        Assert.NotEqual(first.RootPath, second.RootPath);
        Assert.NotEqual(first.ContentHash, second.ContentHash);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(first.RootPath, "references", "guide.txt")));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(second.RootPath, "references", "guide.txt")));
    }

    /// <summary>
    /// A shared Skill write is now a governed action like any other: the settings surface queues it
    /// and a human has to pass both gates before a byte lands. The two decisions are walked in the
    /// durable order — permission envelope first, then the action's own approval — matching the
    /// market and approval-flow suites.
    /// </summary>
    private async Task<JsonElement?> ApproveSkillAsync(Guid resourceId, Guid actionId)
    {
        var client = _factory!.CreateClient();
        var queued = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        var status = queued.GetProperty("status").GetString();
        if (status is "awaiting_user" or "awaiting_delegate")
        {
            var permission = await client.PostAsJsonAsync(
                $"/api/v1/governance/permission-requests/{queued.GetProperty("permission_request_id").GetString()}/decision",
                new { approve = true, reason = "Reviewer confirmed the skill write." });
            Assert.Equal(HttpStatusCode.Accepted, permission.StatusCode);
            queued = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
            status = queued.GetProperty("status").GetString();
        }
        Assert.Equal("awaiting_approval", status);
        var decided = await client.PostAsJsonAsync(
            $"/api/v1/approvals/{queued.GetProperty("action_approval_id").GetString()}/decision",
            new { decision = "approved", reason = "Reviewer approved the skill write." });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        var final = await client.GetFromJsonAsync<JsonElement>($"/api/v1/user/tool-actions/{actionId}");
        Assert.Equal("completed", final.GetProperty("status").GetString());
        return await client.GetFromJsonAsync<JsonElement?>($"/api/v1/tools/skills/{resourceId}");
    }

    private sealed class SkillFactory : IsolatedApiFactory
    {
        private readonly string _root;
        public SkillFactory(string root) => _root = root;

        protected override void ConfigureIsolatedWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            });
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(_root, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(_root, "data"),
                ["Logging:LogLevel:Default"] = "Warning"
            }));
        }
    }
}
