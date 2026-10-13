using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.DmaEA;

namespace TinadecCore.Api.Tests;

public sealed partial class ToolChainEndpointTests
{
    private static FunctionCallContent SpaceCall(string id, string tool, params (string Key, object? Value)[] values) =>
        new(id, tool, values.ToDictionary(item => item.Key, item => item.Value));

    private async Task<(HttpClient Client, Guid Session, string Workspace)> SpaceFixtureAsync(ToolScriptedClient script, FakeToolProvider provider)
    {
        var workspace = Path.Combine(_root, "space-workspace");
        Directory.CreateDirectory(workspace);
        _factory = new ToolChainFactory(_root, script, provider);
        var client = _factory.CreateClient();
        await InstallGraphSeedPackAsync(client);
        var project = await (await client.PostAsJsonAsync("/api/v1/projects", new { name = "space project", path = workspace })).Content.ReadFromJsonAsync<JsonElement>();
        var response = await client.PostAsJsonAsync("/api/v1/sessions", new { project_id = project.GetProperty("id").GetGuid(), view_mode = "space" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (client, session.GetProperty("id").GetGuid(), workspace);
    }

    [Fact]
    public async Task SpacePlanFirst_ReadsContinue_WritesRequireAVisiblePlan_AndDispatchIsAbsent()
    {
        var path = Path.Combine(_root, "space-workspace", "result.txt");
        var script = new ToolScriptedClient().WhenWorkerTurns(
            [SpaceCall("read", "read_file", ("filepath", path))],
            [SpaceCall("early-write", "write_file", ("filepath", path), ("content", "too early"))],
            [SpaceCall("plan", "plan_update", ("steps", new[] { new { step = "Write the verified result", status = "in_progress" } }))],
            [SpaceCall("write", "write_file", ("filepath", path), ("content", "planned"))],
            [new TextContent("The verified result was written. TASK_OUTCOME: completed")]).WhenMeeting("Done.");
        var provider = new FakeToolProvider();
        var fixture = await SpaceFixtureAsync(script, provider);
        var active = StartStreamingInvoke(fixture.Client, fixture.Session, new
        {
            content = "Inspect and write the result", client_message_id = "space-plan", permission_mode = "full-access",
            space_options = new { plan_first = true, multi_agent = false }
        });
        var run = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains(chunks, chunk => KindOf(chunk) == "done");
        Assert.Equal(new[] { "read_file", "write_file" }, provider.ReceivedToolIds);
        Assert.All(script.WorkerToolNames, names => Assert.DoesNotContain("task_dispatch", names));
        using var scope = _factory!.Services.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<ILifecycleManager>();
        var events = await lifecycle.ReplayEventsAsync(fixture.Session, 0);
        Assert.Contains(events, item => item.EventType == "plan.updated");
        var frozen = await lifecycle.GetFrozenRunConfigurationAsync(run.ToString());
        var configuration = JsonSerializer.Deserialize<FrozenRunConfigurationV1>(frozen!.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(configuration.SpaceOptions!.PlanFirst);
        Assert.False(configuration.SpaceOptions.MultiAgent);
        Assert.StartsWith("mode:", configuration.RuntimeProfileId);
        Assert.Contains(":space:", configuration.RuntimeProfileId);
    }

    [Theory]
    [InlineData("full-access", false, false)]
    [InlineData("delegate-both", false, false)]
    [InlineData("full-access", true, false)]
    [InlineData("full-access", false, true)]
    public async Task SpaceSpec_ConfirmsEachDocumentWithHuman_EvenWithUnattendedPermissions(string permission, bool workflow, bool projectless)
    {
        var script = new ToolScriptedClient().WhenWorkerTurns(
            [SpaceCall("requirements", "spec_propose", ("stage", "requirements"), ("document", "# Requirements\nThe user can inspect the result."))],
            [SpaceCall("design", "spec_propose", ("stage", "design"), ("document", "# Design\nUse the existing result view."))],
            [SpaceCall("tasks", "spec_propose", ("stage", "tasks"), ("document", "# Tasks\n- [ ] Verify the existing result view"))],
            [new TextContent("The approved tasks were checked. TASK_OUTCOME: completed")])
            .WhenPlanner("""[{"task_key":"inspect","title":"Inspect the result view","description":"Check the existing result view","success_criteria":["The result view is checked"],"dependencies":[],"required_capabilities":[],"required_tools":[],"assignee":"search","priority":1,"risk":"low"}]""")
            .WhenWorkerText("The result view is checked. TASK_OUTCOME: completed").WhenMeeting("Done.");
        var provider = new FakeToolProvider();
        var fixture = await SpaceFixtureAsync(script, provider);
        var session = fixture.Session;
        if (projectless)
        {
            var free = await fixture.Client.PostAsJsonAsync("/api/v1/sessions", new { view_mode = "space" });
            Assert.True(free.IsSuccessStatusCode, await free.Content.ReadAsStringAsync());
            session = (await free.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        var active = StartStreamingInvoke(fixture.Client, session, new
        {
            content = "Check the result view using specifications", client_message_id = "space-spec", permission_mode = permission,
            space_options = new { spec_enabled = true, plan_first = true, multi_agent = workflow,
                workflow_mode_version_id = workflow ? await LatestPublishedModeVersionIdAsync("fixed_pipeline") : (Guid?)null }
        });
        var run = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("run_id").GetGuid();
        foreach (var stage in new[] { "requirements", "design", "tasks" })
        {
            Guid approval;
            try { approval = await WaitForPendingApprovalAsync(fixture.Client, session, run, TimeSpan.FromSeconds(30)); }
            catch (TimeoutException) { throw new Xunit.Sdk.XunitException("Spec did not park. Model tool results: " + string.Join("\n", script.WorkerToolResults)); }
            var detail = await fixture.Client.GetStringAsync($"/api/v1/approvals/{approval}");
            Assert.Contains("spec_propose", detail);
            Assert.Contains(stage, detail);
            var projected = JsonSerializer.Deserialize<JsonElement>(detail).GetProperty("arguments").GetString()!;
            using (var complete = JsonDocument.Parse(projected))
                Assert.StartsWith("# " + char.ToUpperInvariant(stage[0]) + stage[1..], complete.RootElement.GetProperty("document").GetString());
            var runScope = await fixture.Client.PostAsJsonAsync($"/api/v1/approvals/{approval}/decision", new { decision = "approved", scope = "run" });
            Assert.Equal(HttpStatusCode.BadRequest, runScope.StatusCode);
            var decision = await fixture.Client.PostAsJsonAsync($"/api/v1/approvals/{approval}/decision", new { decision = "approved" });
            Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        }
        var chunks = await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains(chunks, chunk => KindOf(chunk) == "done");
        Assert.Empty(provider.ReceivedToolIds);
        using var scope = _factory!.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(session, 0);
        Assert.Equal(3, events.Count(item => item.EventType == "spec.confirmed"));
        Assert.Single(events, item => item.EventType == "plan.updated");
        Assert.Equal(workflow ? 1 : 0, script.PlannerCalls);
        if (workflow)
        {
            var plannerInput = Assert.Single(script.PlannerInstructions) + "\n" + Assert.Single(script.PlannerPrompts);
            Assert.Contains("The user can inspect the result.", plannerInput);
            Assert.Contains("Use the existing result view.", plannerInput);
            Assert.Contains("Verify the existing result view", plannerInput);
            Assert.Contains("All three stages are confirmed", plannerInput);
        }
    }

    [Fact]
    public async Task SpaceWorkflowWithoutCollaboration_IsRejectedBeforeAnyModelCall()
    {
        var script = new ToolScriptedClient();
        var fixture = await SpaceFixtureAsync(script, new FakeToolProvider());
        var response = await fixture.Client.PostAsJsonAsync($"/api/v1/sessions/{fixture.Session}/interactions", new
        {
            content = "Execute the workflow", dispatch_mode = "queued",
            space_options = new { workflow_mode_version_id = await LatestPublishedModeVersionIdAsync("fixed_pipeline"), multi_agent = false }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_session_settings", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, script.WorkerCalls);
        Assert.Equal(0, script.PlannerCalls);
    }

    [Fact]
    public async Task SpaceSubmissionCapturedWithoutModelOverride_DoesNotReadALaterSessionOverride()
    {
        var fixture = await SpaceFixtureAsync(new ToolScriptedClient(), new FakeToolProvider());
        var providerResponse = await fixture.Client.PostAsJsonAsync("/api/v1/model-providers", new
        { driver = "openai", display_name = "Later selection", base_url = "http://localhost", model = "later-model", api_key = "test" });
        Assert.True(providerResponse.IsSuccessStatusCode, await providerResponse.Content.ReadAsStringAsync());
        var provider = await providerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var changed = await fixture.Client.PatchAsJsonAsync($"/api/v1/sessions/{fixture.Session}", new
        { meeting_model_override = new { provider_instance_id = provider.GetProperty("id").GetGuid(), model = "later-model" } });
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        var frozen = await _factory!.Services.GetRequiredService<IAgentRuntimeConfigurationResolver>().ResolveSubmissionAsync(
            new FullDuplexInvocation(fixture.Session, "The queued task", "captured-model", "default", null, null,
                SpaceOptions: new(), SessionSettingsCaptured: true));
        var root = frozen.OperationAgents.Single(agent => agent.Id == frozen.Graph!.ConversationTemplateSlug);
        Assert.NotEqual("session_override", root.ModelPlan!.StrategySource);
        Assert.All(root.ModelPlan.Candidates, candidate => Assert.NotEqual("later-model", candidate.Model));
    }

    /// <summary>
    /// A session keeps the model it was pinned to after that provider is deleted in the model
    /// center (or in a scope that never had it). Its next task used to fail at freeze with
    /// "Provider '…' was not found"; it now falls back to the default route. A live provider
    /// is still honoured, so the fallback is not simply ignoring every saved choice.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionModelOverride_OfADeletedProvider_FallsBackToTheDefaultRoute(bool deleteProvider)
    {
        var fixture = await SpaceFixtureAsync(new ToolScriptedClient(), new FakeToolProvider());
        var providerResponse = await fixture.Client.PostAsJsonAsync("/api/v1/model-providers", new
        { driver = "openai", display_name = "Pinned", base_url = "http://localhost", model = "pinned-model", api_key = "test" });
        Assert.True(providerResponse.IsSuccessStatusCode, await providerResponse.Content.ReadAsStringAsync());
        var provider = await providerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var providerId = provider.GetProperty("id").GetGuid();
        var pinned = await fixture.Client.PatchAsJsonAsync($"/api/v1/sessions/{fixture.Session}", new
        { meeting_model_override = new { provider_instance_id = providerId, model = "pinned-model" } });
        Assert.True(pinned.IsSuccessStatusCode, await pinned.Content.ReadAsStringAsync());
        if (deleteProvider)
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/model-providers/{providerId}?force=true");
            delete.Headers.TryAddWithoutValidation("If-Match", $"\"{provider.GetProperty("revision").GetInt64()}\"");
            var deleted = await fixture.Client.SendAsync(delete);
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync());
        }

        var frozen = await _factory!.Services.GetRequiredService<IAgentRuntimeConfigurationResolver>().ResolveSubmissionAsync(
            new FullDuplexInvocation(fixture.Session, "Next task", "next-task", "default", null, null, SpaceOptions: new()));

        var root = frozen.OperationAgents.Single(agent => agent.Id == frozen.Graph!.ConversationTemplateSlug);
        if (deleteProvider)
        {
            Assert.NotEqual("session_override", root.ModelPlan!.StrategySource);
            Assert.All(root.ModelPlan.Candidates, candidate => Assert.NotEqual(providerId, candidate.ProviderInstanceId));
        }
        else
        {
            Assert.Equal("session_override", root.ModelPlan!.StrategySource);
            Assert.Equal(providerId, Assert.Single(root.ModelPlan.Candidates).ProviderInstanceId);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SpaceWorktree_IsPreparedBeforeWork_AndFailureNeverUsesOriginalCheckout(bool failCreation, bool workflow)
    {
        var roots = new List<string>();
        var provider = new FakeToolProvider
        {
            FailWhen = request => failCreation && request.ToolId == "git_worktree_create",
            OnCall = (root, request) =>
            {
                roots.Add(root);
                if (request.ToolId == "git_worktree_create" && !failCreation)
                    Directory.CreateDirectory(request.Params!.Value.GetProperty("path").GetString()!);
            },
            ResultFor = (_, request) => request.ToolId == "git_worktree_create"
                ? new { path = request.Params!.Value.GetProperty("path").GetString(), branch = request.Params!.Value.GetProperty("branch").GetString() }
                : null,
        };
        var script = new ToolScriptedClient().WhenWorkerTurns(
            [SpaceCall("read-isolated", "ls", ("path", "."))],
            [new TextContent("Inspected the isolated workspace. TASK_OUTCOME: completed")])
            .WhenPlanner("""[{"task_key":"inspect","title":"Inspect the isolated workspace","description":"Inspect the assigned worktree","success_criteria":["The workspace is inspected"],"dependencies":[],"required_capabilities":[],"required_tools":[],"assignee":"search","priority":1,"risk":"low"}]""")
            .WhenMeeting("Done.");
        var fixture = await SpaceFixtureAsync(script, provider);
        try
        {
        foreach (var arguments in new[] { new[] { "init" }, new[] { "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "Initial" } })
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = fixture.Workspace, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var git = Process.Start(start)!;
            var output = git.StandardOutput.ReadToEndAsync();
            var error = git.StandardError.ReadToEndAsync();
            await git.WaitForExitAsync();
            Assert.True(git.ExitCode == 0, await error);
            await output;
        }
        var active = StartStreamingInvoke(fixture.Client, fixture.Session, new
        { content = "Inspect in an isolated worktree", client_message_id = "space-worktree", permission_mode = "full-access",
            space_options = new { worktree = true, multi_agent = workflow,
                workflow_mode_version_id = workflow ? await LatestPublishedModeVersionIdAsync("fixed_pipeline") : (Guid?)null } });
        var run = (await active.Acknowledgement.WaitAsync(TimeSpan.FromSeconds(60))).GetProperty("run_id").GetGuid();
        if (failCreation)
        {
            await WaitForRunStatusAsync(fixture.Client, run, "awaiting_user", "failed", "completed");
            await _factory!.Services.GetRequiredService<IFullDuplexRunCoordinator>().ControlAsync(run, new RunControlCommand("cancel", "stop-failed-worktree"));
        }
        try { await active.Completion.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { throw new Xunit.Sdk.XunitException(await RunDiagnosticAsync(fixture.Client, run)); }
        if (provider.ReceivedToolIds.Count == 0) throw new Xunit.Sdk.XunitException(await RunDiagnosticAsync(fixture.Client, run));
        Assert.Equal("git_worktree_create", provider.ReceivedToolIds[0]);
        if (failCreation)
        {
            Assert.Single(provider.ReceivedToolIds);
            Assert.Equal(0, script.WorkerCalls);
        }
        else
        {
            Assert.Equal(new[] { "git_worktree_create", "ls" }, provider.ReceivedToolIds);
            Assert.Equal(fixture.Workspace, roots[0]);
            Assert.Equal(Path.Combine(fixture.Workspace, ".tinadec", "worktrees", run.ToString("N")), roots[1]);
            Assert.True(Directory.Exists(roots[1]));
            Assert.Equal(workflow ? 1 : 0, script.PlannerCalls);
            if (workflow)
            {
                var plannerInput = Assert.Single(script.PlannerInstructions) + "\n" + Assert.Single(script.PlannerPrompts);
                Assert.True(plannerInput.Contains(roots[1], StringComparison.Ordinal)
                    || plannerInput.Contains(JsonSerializer.Serialize(roots[1])[1..^1], StringComparison.Ordinal),
                    "The planner did not receive the actual worktree execution root.");
            }
        }
        }
        finally
        {
            // Git objects are read-only on Windows. Only this test's freshly
            // created temporary workspace is normalized for the fixture cleanup.
            Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, Path.GetFullPath(fixture.Workspace), StringComparison.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(fixture.Workspace, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }
    }
    private async Task<string> RunDiagnosticAsync(HttpClient client, Guid runId)
    {
        var orchestration = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs/{runId}/orchestration");
        var state = await _factory!.Services.GetRequiredService<ILifecycleManager>().GetRunStateAsync(runId.ToString());
        var events = await _factory.Services.GetRequiredService<ILifecycleManager>().ReplayEventsAsync(Guid.Parse(state.SessionId), 0);
        return "Run: " + orchestration.GetRawText() + "\nEvents: " + JsonSerializer.Serialize(events.Where(x => x.RunId == runId.ToString()).TakeLast(20));
    }
}
