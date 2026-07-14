using System.Text.Json.Nodes;
using TinadecCore.Services;
using TinadecCore.Storage;
using Tinadec.Contracts.Models;

namespace TinadecCore.Tests;

public sealed class CoreStoreTests
{
    [Fact]
    public void PersistsProjectsSessionsMessagesApprovalsAndEvents()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();

        var project = store.CreateProject("TinadecOffice", Environment.CurrentDirectory);
        var session = store.CreateSession(project.Id, "MVP");
        var message = store.AddMessage(session.Id, "user", "hello");
        var approval =
            store.CreateApproval(new CreateApprovalRequest(session.Id, "shell", "npm test", "npm test", project.Path));
        var envelope = store.AppendNewEvent("message.created", session.Id,
            new JsonObject { ["message_id"] = message.Id }, ["agent.message"]);

        Assert.Single(store.ListProjects());
        Assert.Single(store.ListSessions(project.Id));
        Assert.Single(store.ListMessages(session.Id));
        Assert.Single(store.ListApprovals("pending", session.Id));
        Assert.Equal(1, envelope.Seq);
        Assert.Equal(approval.Id, store.DecideApproval(approval.Id, "approved")?.Id);
    }

    [Fact]
    public void ExtensionInstallRequiresApprovalAndInstallsDisabled()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();

        var catalogItem = Assert.Single(store.ListMarketCatalog("skill", null, null));
        var preview =
            store.PreviewExtensionInstall(new InstallExtensionPreviewRequest(catalogItem.CatalogId, null, null, null));
        Assert.True(preview.RequiresApproval);

        var pending =
            store.InstallExtension(new InstallExtensionRequest(catalogItem.CatalogId, null, null, null, null));
        Assert.True(pending.ApprovalRequired);
        Assert.NotNull(pending.Approval);
        Assert.Empty(store.ListInstalledExtensions());

        store.DecideApproval(pending.Approval!.Id, "approved");
        var installed =
            store.InstallExtension(new InstallExtensionRequest(catalogItem.CatalogId, null, null, null,
                pending.Approval.Id));

        Assert.False(installed.ApprovalRequired);
        Assert.NotNull(installed.Extension);
        Assert.False(installed.Extension!.Enabled);
        Assert.Equal("installed_disabled", installed.Extension.Status);
    }

    [Fact]
    public void EnablingMcpAndAcpExtensionsPopulatesRuntimeRegistries()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();

        foreach (var kind in new[] { "mcp-server", "acp-adapter" })
        {
            var catalogItem = Assert.Single(store.ListMarketCatalog(kind, null, null));
            var pending =
                store.InstallExtension(new InstallExtensionRequest(catalogItem.CatalogId, null, null, null, null));
            store.DecideApproval(pending.Approval!.Id, "approved");
            var installed = store
                .InstallExtension(new InstallExtensionRequest(catalogItem.CatalogId, null, null, null,
                    pending.Approval.Id)).Extension!;
            store.SetExtensionEnabled(installed.Id, true);
        }

        Assert.Single(store.ListMcpServers());
        Assert.Single(store.ListAcpAdapters());
    }

    [Fact]
    public void CreatesAuditableTwoLayerOrchestrationSnapshot()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();

        var project = store.CreateProject("TinadecOffice", Environment.CurrentDirectory);
        var session = store.CreateSession(project.Id, "Agent runtime");
        var message = store.AddMessage(session.Id, "user", "implement the two-layer agent runtime");

        var snapshot = store.CreateOrchestrationRun(session.Id, message.Id, message.Content);

        Assert.NotNull(snapshot.Run);
        Assert.NotNull(snapshot.Graph);
        Assert.Equal(5, snapshot.Nodes.Count);
        Assert.Equal(snapshot.Nodes.Count, snapshot.Assignments.Count);
        Assert.Equal(snapshot.Nodes.Count, snapshot.StepResults.Count);
        Assert.Single(snapshot.ContextPacks);
        Assert.Single(snapshot.SupervisionFindings);
        Assert.Contains(snapshot.Assignments, assignment => assignment.AgentType == "search-specialist");
        Assert.Contains(snapshot.Assignments, assignment => assignment.AgentType == "code-writer");
    }

    [Fact]
    public void DisabledExecutionAgentIsNotAssigned()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();

        var testAgent = Assert.Single(store.ListAgentProfiles(), agent => agent.AgentType == "test-multimodal");
        store.SaveAgentProfile(testAgent.Id, new SaveAgentProfileRequest(
            testAgent.Name,
            testAgent.Layer,
            testAgent.AgentType,
            testAgent.Mode,
            testAgent.Description,
            testAgent.ModelRoutePurpose,
            testAgent.AllowedTools,
            testAgent.Capabilities,
            null,
            false));

        var project = store.CreateProject("TinadecOffice", Environment.CurrentDirectory);
        var session = store.CreateSession(project.Id, "Agent runtime");
        var message = store.AddMessage(session.Id, "user", "plan and validate the change");

        var snapshot = store.CreateOrchestrationRun(session.Id, message.Id, message.Content);

        Assert.DoesNotContain(snapshot.Assignments, assignment => assignment.AgentType == "test-multimodal");
        Assert.Contains(snapshot.Nodes, node => node.Title == "Prepare validation");
        Assert.Equal(snapshot.Nodes.Count - 1, snapshot.Assignments.Count);
    }

    [Fact]
    public void EvolutionAgentCandidatesRemainProposed()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();

        var evolutionAgent = Assert.Single(store.ListAgentProfiles(), agent => agent.Id == "agent_evolver");
        Assert.Equal("Evolution Agent", evolutionAgent.Name);
        Assert.Equal("evolver", evolutionAgent.AgentType);
        Assert.All(store.ListAgentCandidates(), candidate => Assert.Equal("proposed", candidate.Status));
    }

    [Fact]
    public void SeedsRequestedPlanningAndExecutionAgentsFromCore()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();

        var agents = store.ListAgentProfiles();

        // Layer 1 路 Planning 涓诲姩鏅鸿兘浣?        Assert.Contains(agents, agent => agent.Id == "agent_meeting" && agent.Layer == "planning");
        Assert.Contains(agents, agent => agent.Id == "agent_tool_assistant" && agent.Layer == "planning");
        Assert.Contains(agents, agent => agent.Id == "agent_evolver" && agent.Layer == "planning");
        Assert.Contains(agents, agent => agent.Id == "agent_context_compressor" && agent.Layer == "planning");
        Assert.Contains(agents, agent => agent.Id == "agent_supervisor" && agent.Layer == "planning");
        Assert.Contains(agents, agent => agent.Id == "agent_skill_learner" && agent.Layer == "planning");
        // Layer 2 路 Execution 琚姩鎵ц绫绘櫤鑳戒綋
        Assert.Contains(agents, agent => agent.Id == "executor_task_planner" && agent.Layer == "execution");
        Assert.Contains(agents, agent => agent.Id == "executor_test_multimodal" && agent.Layer == "execution");
        Assert.Contains(agents, agent => agent.Id == "executor_search_specialist" && agent.Layer == "execution");
        Assert.Contains(agents, agent => agent.Id == "executor_code_explorer" && agent.Layer == "execution");
        Assert.Contains(agents, agent => agent.Id == "executor_file_finder" && agent.Layer == "execution");
        Assert.Contains(agents, agent => agent.Id == "executor_git_manager" && agent.Layer == "execution");
        var gitAgent = Assert.Single(agents, agent => agent.Id == "executor_git_manager");
        Assert.Equal("Git Manager Subagent", gitAgent.Name);
        Assert.Equal("git", gitAgent.ModelRoutePurpose);
        Assert.Contains("git_worktree_manager", gitAgent.AllowedTools);
        Assert.Contains("git_stage", gitAgent.AllowedTools);
        Assert.Contains("git_unstage", gitAgent.AllowedTools);
        Assert.Contains("git_commit", gitAgent.AllowedTools);
        Assert.Contains("git_status", gitAgent.AllowedTools);
        Assert.Contains("git_conflict_preview", gitAgent.AllowedTools);
        Assert.Contains("git.push", gitAgent.Capabilities);
        Assert.Contains("handoff.explain", gitAgent.Capabilities);
        Assert.Contains("Never push", gitAgent.SystemPrompt ?? "");
        Assert.Contains(agents, agent => agent.Id == "executor_code_writer" && agent.Layer == "execution");
        Assert.Contains(agents, agent => agent.Id == "executor_designer" && agent.Layer == "execution");
        Assert.DoesNotContain(agents, agent => agent.Id.Contains("purifier", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(agents, agent => agent.Name.Contains("Purifier", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RegistersProgrammingToolsAsToolLayerCodeSuiteCapabilities()
    {
        var registry = new ToolRegistryService();

        var tools = registry.ListTools("programming");

        Assert.Contains(tools,
            tool => tool.Id == "search_files" && tool.Source == "codex-rust" && !tool.RequiresApproval);
        Assert.Contains(tools,
            tool => tool.Id == "glob_search" && tool.Source == "codex-rust" && !tool.RequiresApproval);
        Assert.Contains(tools, tool => tool.Id == "read_file" && tool.Source == "codex-rust" && !tool.RequiresApproval);
        Assert.Contains(tools,
            tool => tool.Id == "list_directory" && tool.Source == "codex-rust" && !tool.RequiresApproval);
        Assert.Contains(tools,
            tool => tool.Id == "grep_content" && tool.Source == "codex-rust" && !tool.RequiresApproval);
        Assert.Contains(tools,
            tool => tool.Id == "sandbox_exec" && tool.Source == "codex-rust" && tool.RequiresApproval);
        Assert.Contains(tools,
            tool => tool.Id == "apply_patch" && tool.Source == "codex-rust" && tool.RequiresApproval);
        Assert.Contains(tools,
            tool => tool.Id == "review_format" && tool.Source == "codex-rust" && !tool.RequiresApproval);
        Assert.Contains(tools,
            tool => tool.Id == "project_templates" && tool.Source == "code" && !tool.RequiresApproval);
        Assert.Contains(tools,
            tool => tool.Id == "project_template_scaffold" && tool.Source == "code" && tool.RequiresApproval &&
                    tool.Risk == "workspace-write");
        Assert.Contains(tools,
            tool => tool.Id == "language_runtime_probe" && tool.Source == "code" && !tool.RequiresApproval);
        Assert.Contains(tools, tool => tool.Id == "bash_environment" && tool.Source == "code" && tool.RequiresApproval);
        Assert.Contains(tools, tool => tool.Id == "debug_session" && tool.Source == "code" && tool.RequiresApproval);
        Assert.Contains(tools, tool => tool.Id == "code_editor" && tool.Source == "code" && tool.RequiresApproval);
        Assert.Contains(tools, tool =>
            tool.Id == "git_worktree_manager"
            && tool.Source == "code"
            && tool.RequiresApproval
            && tool.Capabilities.Contains("git.diff")
            && tool.Capabilities.Contains("git.stage")
            && tool.Capabilities.Contains("git.unstage")
            && tool.Capabilities.Contains("git.commit")
            && tool.Capabilities.Contains("git.push"));
        Assert.Contains(tools,
            tool => tool.Id == "language_runtime_probe" && tool.Capabilities.Contains("runtime.nodejs") &&
                    tool.Capabilities.Contains("runtime.java"));
        Assert.All(tools, tool => Assert.Equal("programming", tool.Domain));
    }

    [Fact]
    public void SeedsBuiltInPromptFragmentsForMeetingAgent()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();

        var fragments = store.ListPromptFragments();

        Assert.Contains(fragments,
            fragment => fragment.Id == "prompt_builtin_meeting_default" && fragment.TargetAgentId == "agent_meeting");
        Assert.Contains(fragments,
            fragment => fragment.Id == "prompt_builtin_tool_approval_boundaries" && fragment.IsBuiltIn);
        Assert.Contains(fragments, fragment => fragment.Id == "prompt_builtin_agent_mode" && fragment.Enabled);
        Assert.Contains(fragments,
            fragment => fragment.Id == "prompt_builtin_context_pack_rules" && fragment.Category == "context");
    }

    [Fact]
    public void BuiltInPromptFragmentsAreReadOnlyAndCloneable()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();
        var builtIn = store.GetPromptFragment("prompt_builtin_meeting_default")!;

        Assert.Throws<InvalidOperationException>(() => store.UpdatePromptFragment(
            builtIn.Id,
            new SavePromptFragmentRequest(
                builtIn.Key,
                builtIn.Title,
                builtIn.Scope,
                builtIn.TargetAgentId,
                builtIn.Category,
                "changed",
                builtIn.Priority,
                builtIn.Enabled)));

        var clone = store.ClonePromptFragment(builtIn.Id);

        Assert.NotNull(clone);
        Assert.False(clone!.IsBuiltIn);
        Assert.True(clone.Enabled);
        Assert.Equal(builtIn.Content, clone.Content);
        Assert.NotEqual(builtIn.Id, clone.Id);
    }

    [Fact]
    public void AgentSystemPromptSavesAsCustomPromptFragment()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();
        var meetingAgent = Assert.Single(store.ListAgentProfiles(), agent => agent.Id == "agent_meeting");

        store.SaveAgentProfile(meetingAgent.Id, new SaveAgentProfileRequest(
            meetingAgent.Name,
            meetingAgent.Layer,
            meetingAgent.AgentType,
            meetingAgent.Mode,
            meetingAgent.Description,
            meetingAgent.ModelRoutePurpose,
            meetingAgent.AllowedTools,
            meetingAgent.Capabilities,
            "custom override prompt",
            meetingAgent.Enabled));

        var overrideFragment = Assert.Single(store.ListPromptFragments(targetAgentId: meetingAgent.Id),
            fragment => fragment.Key == "agent.override.agent_meeting");
        Assert.False(overrideFragment.IsBuiltIn);
        Assert.Equal("custom override prompt", overrideFragment.Content);
        Assert.Equal(1000, overrideFragment.Priority);
    }

    [Fact]
    public void CompilesTaskGraphIntoMicrosoftAgentWorkflowPlan()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();

        var project = store.CreateProject("TinadecOffice", Environment.CurrentDirectory);
        var session = store.CreateSession(project.Id, "Workflow runtime");
        var message = store.AddMessage(session.Id, "user", "search code and prepare validation");
        var snapshot = store.CreateOrchestrationRun(session.Id, message.Id, message.Content);

        var runtime = new AgentWorkflowRuntime(new ToolRegistryService());
        var plan = runtime.Compile(snapshot);

        Assert.Equal(snapshot.Run!.Id, plan.RunId);
        Assert.Equal(AgentWorkflowRuntime.RuntimeName, plan.Runtime);
        Assert.Equal(snapshot.Assignments.Count, plan.Steps.Count);
        Assert.Contains(plan.Steps,
            step => step.AgentType == "search-specialist" && step.ToolIds.Contains("search_files"));
        Assert.Contains(plan.Steps,
            step => step.AgentType == "test-multimodal" && step.ToolIds.Contains("sandbox_exec"));
    }

    [Fact]
    public void GitManagerSubagentCompilesApprovedGitToolingForPushTasks()
    {
        var db = Path.Combine(Path.GetTempPath(), $"tinadec-test-{Guid.NewGuid():N}.db");
        var store = new CoreStore(db);
        store.Initialize();

        var project = store.CreateProject("TinadecOffice", Environment.CurrentDirectory);
        var session = store.CreateSession(project.Id, "Git workflow runtime");
        var message = store.AddMessage(session.Id, "user", "prepare a git commit and push explanation for this branch");
        var snapshot = store.CreateOrchestrationRun(session.Id, message.Id, message.Content);

        var gitAssignment = Assert.Single(snapshot.Assignments, assignment => assignment.AgentType == "git-manager");
        Assert.Equal("executor_git_manager", gitAssignment.AgentId);
        Assert.Contains("git_worktree_manager", gitAssignment.AllowedTools);

        var runtime = new AgentWorkflowRuntime(new ToolRegistryService());
        var plan = runtime.Compile(snapshot);
        var gitStep = Assert.Single(plan.Steps, step => step.AgentType == "git-manager");

        Assert.Contains("git_worktree_manager", gitStep.ToolIds);
        Assert.Contains("sandbox_exec", gitStep.ToolIds);
        Assert.Contains("review_format", gitStep.ToolIds);
        Assert.Contains("read_file", gitStep.ToolIds);
        Assert.Contains("grep_content", gitStep.ToolIds);
        Assert.Equal("approval", gitStep.PermissionMode);
    }
}
