using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TinadecCore.Abstractions.Ports;

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
            ("skills/release-notes/SKILL.md",
                Skill("release-notes", "Summarise what changed since the last tag.",
                    "BODY-MUST-NOT-TRAVEL-9d2a: open the changelog and diff the tags.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("release-notes", evidence!.Content);
        Assert.Contains("Summarise what changed since the last tag.", evidence.Content);
        Assert.Contains("skills/release-notes/SKILL.md", evidence.Content);
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
            ("skills/one/SKILL.md", Skill("one", "First skill.")));

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
            ("skills/flat/SKILL.md", Skill("flat", "At one level.")),
            ("skills/grouped/nested/SKILL.md", Skill("nested", "Under a grouping directory.")),
            // Inside a skill package: references/ and its own deeper files belong to that skill, and
            // advertising them as separate skills would turn one package into several index rows.
            ("skills/flat/references/deep/SKILL.md", Skill("deep", "Should not be a skill.")),
            // Not a skill location at all.
            ("docs/SKILL.md", Skill("docs", "In the wrong place.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("skills/flat/SKILL.md", evidence!.Content);
        Assert.Contains("skills/grouped/nested/SKILL.md", evidence.Content);
        Assert.DoesNotContain("skills/flat/references/deep/SKILL.md", evidence.Content);
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
            ("skills/broken/SKILL.md", "---\nname: broken\n---\n\nNo description declared."),
            ("skills/healthy/SKILL.md", Skill("healthy", "Fine.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("skills/broken/SKILL.md", evidence!.Content);
        Assert.Contains("frontmatter has no 'description'", evidence.Content);
        Assert.Contains("skills/healthy/SKILL.md", evidence.Content);
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
            ("skills/actual-dir/SKILL.md", Skill("other-dir", "Named after somewhere else.")));

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
            ("skills/writing/probe/SKILL.md", Skill("probe", "Later in the walk.")),
            ("skills/reviewing/probe/SKILL.md", Skill("probe", "First in the walk.")));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("First in the walk.", evidence!.Content);
        // The loser is named, with its reason — but its description never reaches the model, because
        // an index that advertised both would be asking which "probe" was meant.
        Assert.DoesNotContain("Later in the walk.", evidence.Content);
        Assert.Contains(
            "skills/writing/probe/SKILL.md: another skill already claims the name 'probe'",
            evidence.Content);
        Assert.Equal("1", evidence.Metadata["skill_count"]);
    }

    [Fact]
    public async Task AFrontmatterBlockSavedWithABomIsStillRead()
    {
        // "UTF-8 with BOM" is a default in some Windows editors. Refusing it would make one directory
        // work on one machine and not on another, with no difference the author can see.
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("bom",
            ("skills/bommed/SKILL.md", "﻿" + Skill("bommed", "Described past the byte-order mark.")));

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
            ("skills/only/SKILL.md", Skill("only", "The one skill.")));

        // 40 characters cannot fit the framing sentence plus a row. Emitting the header alone would
        // spend the run's budget telling it to look for skills the pack refused to name.
        Assert.Null(await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot), tokenBudget: 40));

        var evidence = await SkillEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot), tokenBudget: 20_000);
        Assert.NotNull(evidence);
        Assert.Contains("skills/only/SKILL.md", evidence!.Content);
    }

    [Fact]
    public async Task OneRunKeepsItsIndexAndTheNextRunSeesANewSkill()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("stability",
            ("skills/first/SKILL.md", Skill("first", "There at admission.")));
        var binding = BindingOf(projectId, workspaceRoot);

        var before = await SkillEvidenceAsync(sessionId, binding, runId: "run-stable-1");
        Assert.NotNull(before);
        Assert.Contains("first", before!.Content);

        var added = Path.Combine(workspaceRoot, "skills", "second", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(added)!);
        File.WriteAllText(added, Skill("second", "Added while the run was in flight."));

        var during = await SkillEvidenceAsync(sessionId, binding, runId: "run-stable-1");
        Assert.NotNull(during);
        Assert.Contains("first", during!.Content);
        Assert.DoesNotContain("skills/second/SKILL.md", during.Content);

        var after = await SkillEvidenceAsync(sessionId, binding, runId: "run-stable-2");
        Assert.NotNull(after);
        Assert.Contains("skills/second/SKILL.md", after!.Content);
    }

    private sealed class SkillFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public SkillFactory(string root) => _root = root;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Testing");
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
