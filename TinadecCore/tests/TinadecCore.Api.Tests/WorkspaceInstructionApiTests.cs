using System.Net;
using System.Net.Http.Json;
using System.Text;
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
/// A project's own instruction file reaching the model. The property under test is a boundary
/// property, so it is tested through the real provider and the real filesystem: which name wins,
/// how much of the file the model is shown, what it is told when it is not shown all of it, and
/// what happens when the run has no workspace at all.
/// </summary>
public sealed class WorkspaceInstructionApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-core-instruction-tests", Guid.NewGuid().ToString("N"));
    private InstructionFactory? _factory;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new InstructionFactory(_root);
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
    /// Creates a workspace directory holding exactly <paramref name="files"/>, then a project and a
    /// session bound to it, so the root in the frozen binding below is the root a real admission
    /// would have frozen.
    /// </summary>
    private async Task<(string WorkspaceRoot, Guid SessionId, Guid ProjectId)> OpenWorkspaceAsync(
        string label, params (string Name, string Content)[] files)
    {
        var workspaceRoot = Path.Combine(_root, label + "-workspace");
        Directory.CreateDirectory(workspaceRoot);
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(workspaceRoot, name), content);
        }

        var client = _factory!.CreateClient();
        var projectResponse = await client.PostAsJsonAsync("/api/v1/projects",
            new { name = $"Instructions {label}", path = workspaceRoot });
        Assert.Equal(HttpStatusCode.Created, projectResponse.StatusCode);
        var projectId = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var sessionResponse = await client.PostAsJsonAsync("/api/v1/sessions",
            new { project_id = projectId, title = $"Instructions {label}" });
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var sessionId = (await sessionResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (workspaceRoot, sessionId, projectId);
    }

    private static FrozenWorkspaceBinding BindingOf(Guid projectId, string root) =>
        new(projectId, root, [], false, null, [], false, WorkspacePathContracts.AbsoluteInRoot);

    private async Task<ContextEvidence?> InstructionEvidenceAsync(
        Guid sessionId, FrozenWorkspaceBinding? workspace, int? tokenBudget = null, string? runId = null)
    {
        var provider = _factory!.Services.GetRequiredService<IContextProvider>();
        var pack = await provider.BuildContextAsync(new ContextBuildRequest(sessionId.ToString(), runId)
        {
            Workspace = workspace,
            TokenBudget = tokenBudget,
        });
        return pack.Evidence.FirstOrDefault(item => item.Source == "workspace_instructions");
    }

    // ── what the model is told ──────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectInstructions_EnterThePackAsEvidenceNamedForWhatTheyAre()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("basic",
            ("AGENTS.md", "Keep every public surface documented in the same commit."));

        var evidence = await InstructionEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("Keep every public surface documented in the same commit.", evidence!.Content);
        Assert.Contains("AGENTS.md", evidence.Content);
        // The precedence sentence is part of the payload, not an assumption about the model:
        // a project document must not read as a licence to act beyond the frozen run.
        Assert.Contains("rank below the run's frozen permissions", evidence.Content);
        Assert.Equal("AGENTS.md", evidence.Metadata["instruction_file"]);
        Assert.True(evidence.EstimatedTokens > 0);
    }

    [Fact]
    public async Task OverrideWinsAndTheDocumentsItSuppressesStayUnread()
    {
        // A repository migrating between harnesses often carries three of these. Showing all of
        // them hands the model three sets of rules to reconcile by itself, so one wins.
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("precedence",
            ("AGENTS.override.md", "LOCAL-OVERRIDE-RULE"),
            ("AGENTS.md", "PLAIN-AGENTS-RULE"),
            ("CLAUDE.md", "CLAUDE-RULE"));

        var evidence = await InstructionEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("LOCAL-OVERRIDE-RULE", evidence!.Content);
        Assert.DoesNotContain("PLAIN-AGENTS-RULE", evidence.Content);
        Assert.DoesNotContain("CLAUDE-RULE", evidence.Content);
        Assert.Equal("AGENTS.override.md", evidence.Metadata["instruction_file"]);
    }

    [Fact]
    public async Task VendorNamedFileIsReadWhenTheProjectHasNoAgentsFile()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("vendor",
            ("CONTEXT.md", "CONTEXT-FILE-RULE"));

        var evidence = await InstructionEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("CONTEXT-FILE-RULE", evidence!.Content);
    }

    [Fact]
    public async Task WorkspaceWithoutAnInstructionFileProducesNoEvidenceAtAll()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("silent",
            ("README.md", "Nothing here instructs an agent."));

        Assert.Null(await InstructionEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot)));
    }

    [Fact]
    public async Task ProjectlessRunIsNeverShownSomeonesInstructions()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("unbound",
            ("AGENTS.md", "RULE-FOR-ANOTHER-PROJECT"));

        // No binding means no granted root: the file exists on disk, and it is still not read,
        // because the only address this reader accepts is the one admission froze.
        Assert.Null(await InstructionEvidenceAsync(sessionId, workspace: null));
        var granted = await InstructionEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));
        Assert.Contains("RULE-FOR-ANOTHER-PROJECT", granted!.Content);
    }

    // ── how much of it the model is shown ───────────────────────────────────────────

    /// <summary>
    /// The stability promise behind the per-run memo: a run is admitted under a frozen set of
    /// permissions, so it must also be argued with a frozen set of rules. An edit landing mid-run
    /// reaches the next run, not the one already in flight.
    /// </summary>
    [Fact]
    public async Task ARunKeepsTheInstructionsItStartedWithAndTheNextRunSeesTheEdit()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("stable",
            ("AGENTS.md", "RULE-V1"));
        var binding = BindingOf(projectId, workspaceRoot);

        var firstTurn = await InstructionEvidenceAsync(sessionId, binding, runId: "11111111-1111-1111-1111-111111111111");
        Assert.NotNull(firstTurn);
        Assert.Contains("RULE-V1", firstTurn!.Content);

        File.WriteAllText(Path.Combine(workspaceRoot, "AGENTS.md"), "RULE-V2");

        var sameRun = await InstructionEvidenceAsync(sessionId, binding, runId: "11111111-1111-1111-1111-111111111111");
        Assert.Contains("RULE-V1", sameRun!.Content);
        Assert.DoesNotContain("RULE-V2", sameRun.Content);

        var nextRun = await InstructionEvidenceAsync(sessionId, binding, runId: "22222222-2222-2222-2222-222222222222");
        Assert.Contains("RULE-V2", nextRun!.Content);
    }

    [Fact]
    public async Task LongFileIsCutAndTheCutIsStatedInTheBodyTheModelReads()
    {
        var body = string.Concat(Enumerable.Repeat("abcdefgh", 900)) + "TAIL-MARKER-9f3c";
        Assert.Equal(7216, body.Length);
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("truncated",
            ("AGENTS.md", body));

        var evidence = await InstructionEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot), tokenBudget: 512);

        Assert.NotNull(evidence);
        Assert.Contains("[Truncated at 512 characters", evidence!.Content);
        Assert.Contains("file tool", evidence.Content);
        Assert.Equal("512", evidence.Metadata["char_limit"]);
        // The cap is the claim being tested, and the marker has to be unique to survive it: an
        // earlier draft asserted the file's tail was absent using a repeating payload, whose "tail"
        // also appears in the retained head, so the assertion could never have failed.
        Assert.DoesNotContain("TAIL-MARKER-9f3c", evidence.Content);
        Assert.True(evidence.Content.Length < 1_200, $"inline cap leaked: {evidence.Content.Length} chars");
    }

    [Fact]
    public async Task FileOverTheReadCeilingIsNamedInsteadOfQuietlySkipped()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("oversized",
            ("AGENTS.md", new string('x', 600 * 1024)));

        var evidence = await InstructionEvidenceAsync(sessionId, BindingOf(projectId, workspaceRoot));

        Assert.NotNull(evidence);
        Assert.Contains("above the inline ceiling", evidence!.Content);
        Assert.Contains("file tool", evidence.Content);
        Assert.DoesNotContain(new string('x', 64), evidence.Content);
    }

    [Fact]
    public async Task InstructionsCannotPushTheConversationOutOfItsOwnPack()
    {
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("starve",
            ("AGENTS.md", new string('y', 6000)));
        var client = _factory!.CreateClient();
        await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "what did I ask last?" });

        var provider = _factory!.Services.GetRequiredService<IContextProvider>();
        var pack = await provider.BuildContextAsync(new ContextBuildRequest(sessionId.ToString(), null)
        {
            Workspace = BindingOf(projectId, workspaceRoot),
            TokenBudget = 1024,
        });

        var instructions = pack.Evidence.Single(item => item.Source == "workspace_instructions");
        var history = pack.Evidence.SingleOrDefault(item => item.Source == "session_history");
        // The file text is capped at a quarter of the budget (1024 tokens → 1024 characters →
        // ~256 tokens) and the framing line rides on top of it; the transcript survives, which is
        // the point: instructions describe the repository, they do not own the pack.
        Assert.True(instructions.EstimatedTokens is > 200 and <= 384, $"took {instructions.EstimatedTokens} of 1024 tokens");
        Assert.NotNull(history);
        Assert.True(pack.EstimatedTokens <= 1024);
    }

    [Fact]
    public async Task EvidenceTheBudgetCutsIsNamedWithItsPriceInsteadOfVanishing()
    {
        // Until the cut itself was recorded, a short pack and an empty workspace produced the same
        // observable run: `evidence_count` of 1 and no complaint. This is the shape where the two
        // part — instructions the run kept and a transcript too large to ride along with them.
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("cut",
            ("AGENTS.md", new string('y', 6000)));
        var client = _factory!.CreateClient();
        for (var turn = 0; turn < 3; turn++)
            await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = new string('m', 2000) });

        var pack = await _factory!.Services.GetRequiredService<IContextProvider>().BuildContextAsync(
            new ContextBuildRequest(sessionId.ToString(), null)
            {
                Workspace = BindingOf(projectId, workspaceRoot),
                TokenBudget = 600,
            });

        Assert.All(pack.Evidence, item => Assert.NotEqual("session_history", item.Source));
        var cut = Assert.Single(pack.Dropped);
        Assert.Equal("session_history", cut.Source);
        // A name without a price cannot answer the question the panel exists to ask, which is how
        // much room the source would have needed — that is what tells you to raise the budget
        // rather than to go looking for a source that was never there.
        Assert.True(cut.EstimatedTokens > 0, "a dropped source priced at nothing explains nothing");
        Assert.Equal(pack.Evidence.Sum(item => item.EstimatedTokens), pack.EstimatedTokens);
        Assert.True(pack.EstimatedTokens <= pack.TokenBudget);
    }

    [Fact]
    public async Task EvidenceThatFitsLeavesTheDroppedListEmptyRatherThanAbsent()
    {
        // The negative control: without it the assertion above could be satisfied by a builder that
        // drops something on every pack.
        var (workspaceRoot, sessionId, projectId) = await OpenWorkspaceAsync("roomy",
            ("AGENTS.md", "Use pnpm, never npm."));
        var client = _factory!.CreateClient();
        await client.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/messages", new { content = "short question" });

        var pack = await _factory!.Services.GetRequiredService<IContextProvider>().BuildContextAsync(
            new ContextBuildRequest(sessionId.ToString(), null)
            {
                Workspace = BindingOf(projectId, workspaceRoot),
                TokenBudget = 8192,
            });

        Assert.NotEmpty(pack.Evidence);
        Assert.Empty(pack.Dropped);
    }

    // ── the containment rule the reader is built on ─────────────────────────────────

    [Theory]
    [InlineData("/work/root", "/work/root/AGENTS.md", true)]
    [InlineData("/work/root/", "/work/root/src/AGENTS.md", true)]
    [InlineData("/work/root", "/work/root", true)]
    [InlineData("/work/root", "/work/rootless/AGENTS.md", false)]
    [InlineData("/work/root", "/work/other/AGENTS.md", false)]
    [InlineData("/work/root", "/work", false)]
    public void ContainmentRejectsSiblingsAndParentsNotJustOutsidePaths(string root, string candidate, bool expected) =>
        Assert.Equal(expected, WorkspaceInstructionPolicy.IsInsideRoot(root, candidate));

    [Fact]
    public void CandidateOrderIsThePrecedenceRuleAndMatchesCaseInsensitively()
    {
        Assert.Equal("AGENTS.md", WorkspaceInstructionPolicy.Select(["README.md", "agents.md", "CLAUDE.md"]));
        Assert.Equal("CLAUDE.md", WorkspaceInstructionPolicy.Select(["CLAUDE.md", "CONTEXT.md"]));
        Assert.Null(WorkspaceInstructionPolicy.Select(["README.md", "AGENTS.txt"]));
    }

    private sealed class InstructionFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        public InstructionFactory(string root) => _root = root;

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
