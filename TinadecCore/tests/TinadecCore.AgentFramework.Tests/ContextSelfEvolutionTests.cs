using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TinadecCore.Abstractions;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Context;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Contracts.Events;
using TinadecCore.DmaEA;
using TinadecCore.Persistence;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// P2 Track 3 Context Self-Evolution tests:
/// - <see cref="RuntimeTomlCommentedWriter"/> audit-commented TOML persistence &amp; <c>auto_tune_low_risk_context_policy = false</c> switch
/// - <see cref="ContextPolicyProposalService"/> conservative pending queue, multi-run degradation aggregator (&gt;=3 of 5), and Winnowing dedup (&gt;=200 tokens)
/// - <see cref="FullDuplexRunEngine.DispatchEvolutionAsync"/> bounded Context &amp; Trajectory Evidence injection + failure_pattern / supervision_rule curation
/// </summary>
public sealed class ContextSelfEvolutionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "tinadec-p2-evolution-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RuntimeTomlCommentedWriter_AppliesGlobalProposal_PreservesComments_WritesAuditBlock_AndValidatesViaTomlyn()
    {
        var scope = CreateScope("toml-writer");
        Directory.CreateDirectory(scope.Config);
        Directory.CreateDirectory(scope.State);

        var baselineToml = LoadBundledRuntimeToml();
        var runtimePath = Path.Combine(scope.Config, "runtime.toml");
        await File.WriteAllTextAsync(runtimePath, baselineToml, Encoding.UTF8);

        var scopeDocs = new ScopeConfigurationDocuments(scope, [new RuntimeConfigurationDocumentValidator()]);
        var initialDoc = await scopeDocs.ReadAsync("runtime");
        Assert.Empty(initialDoc.Diagnostics);
        Assert.False(RuntimeTomlCommentedWriter.ReadAutoTuneLowRiskSwitch(initialDoc.Text));

        var proposalId = Guid.Parse("7f3a9e12-4b10-4c88-9a11-223344556677");
        var appliedAt = new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);
        var proposal = new ContextEvolutionProposalDto
        {
            Id = proposalId,
            Kind = ContextEvolutionProposalKinds.ContextPolicyProposal,
            Status = ContextEvolutionProposalStatuses.Pending,
            TargetScope = ContextEvolutionTargetScopes.Global,
            Title = "Tune global context policy",
            EvidenceRunIds = ["run-101", "run-102", "run-103"],
            PolicyPatch = new ContextPolicyPatchDto
            {
                CompactionTriggerRatio = 0.65,
                RecentMessageLimit = 96
            },
            MetricsComparison = new ContextEvolutionMetricsComparisonDto
            {
                WindowRunCount = 5,
                DegradedRunCount = 3,
                DegradationRate = 0.60,
                AvgPeakOccupancyPct = 88.4,
                HighOccupancyErrorRate = 0.42,
                LowOccupancyErrorRate = 0.05,
                BeforeValues = new Dictionary<string, string>
                {
                    ["compaction_trigger_ratio"] = "0.75",
                    ["recent_message_limit"] = "128"
                },
                AfterValues = new Dictionary<string, string>
                {
                    ["compaction_trigger_ratio"] = "0.65",
                    ["recent_message_limit"] = "96"
                }
            }
        };

        var updatedToml = RuntimeTomlCommentedWriter.ApplyContextPolicyProposal(initialDoc.Text, proposal, appliedAt);
        var savedDoc = await scopeDocs.SaveIfMatchAsync("runtime", updatedToml, initialDoc.ContentHash);
        Assert.Empty(savedDoc.Diagnostics);

        // 1. Existing comments and unrelated sections survive intact
        Assert.Contains("# 上下文管理策略 (Context Management Policy)", savedDoc.Text, StringComparison.Ordinal);
        Assert.Contains("[approval]", savedDoc.Text, StringComparison.Ordinal);
        Assert.Contains("[triggers]", savedDoc.Text, StringComparison.Ordinal);

        // 2. Audit comments next to updated keys include proposal ID, run_id list, degradation stats, before->after, and timestamp
        Assert.Contains($"# [context-evolution] proposal_id={proposalId:D} | applied_at=2026-11-24T01:00:00Z", savedDoc.Text, StringComparison.Ordinal);
        Assert.Contains("evidence_run_ids=[run-101, run-102, run-103]", savedDoc.Text, StringComparison.Ordinal);
        Assert.Contains("degraded=3/5 (60%)", savedDoc.Text, StringComparison.Ordinal);
        Assert.Contains($"compaction_trigger_ratio = 0.65 # proposal={proposalId:D} (0.75 -> 0.65)", savedDoc.Text, StringComparison.Ordinal);
        Assert.Contains($"recent_message_limit = 96 # proposal={proposalId:D} (128 -> 96)", savedDoc.Text, StringComparison.Ordinal);

        // 3. Reserved conservative auto-tune switch is written and defaults to false
        Assert.Contains("auto_tune_low_risk_context_policy = false", savedDoc.Text, StringComparison.Ordinal);
        Assert.False(RuntimeTomlCommentedWriter.ReadAutoTuneLowRiskSwitch(savedDoc.Text));

        // 4. Snapshot loader parses updated TOML cleanly
        var snapshot = AgentRuntimeConfigurationStore.LoadSnapshotText(savedDoc.Text, savedDoc.Path, 2);
        Assert.Equal(0.65, snapshot.Context.CompactionTriggerRatio);
        Assert.Equal(96, snapshot.Context.RecentMessageLimit);

        // 5. Re-applying a second proposal replaces the prior [context-evolution] block on that key instead of stacking duplicates
        var secondProposalId = Guid.Parse("99887766-5544-3322-1100-aabbccddeeff");
        var secondToml = RuntimeTomlCommentedWriter.ApplyContextPolicyProposal(
            savedDoc.Text,
            new ContextEvolutionProposalDto
            {
                Id = secondProposalId,
                Kind = ContextEvolutionProposalKinds.ContextPolicyProposal,
                EvidenceRunIds = ["run-201", "run-202", "run-203"],
                PolicyPatch = new ContextPolicyPatchDto { CompactionTriggerRatio = 0.55 }
            },
            appliedAt.AddHours(1));

        Assert.Contains($"compaction_trigger_ratio = 0.55 # proposal={secondProposalId:D} (0.65 -> 0.55)", secondToml, StringComparison.Ordinal);
        var secondSnapshot = AgentRuntimeConfigurationStore.LoadSnapshotText(secondToml, savedDoc.Path, 3);
        Assert.Equal(0.55, secondSnapshot.Context.CompactionTriggerRatio);
        Assert.Equal(96, secondSnapshot.Context.RecentMessageLimit);
    }

    [Fact]
    public void RuntimeTomlCommentedWriter_ReadAutoTuneSwitch_AndValidatePatchBounds()
    {
        Assert.False(RuntimeTomlCommentedWriter.ReadAutoTuneLowRiskSwitch(null));
        Assert.False(RuntimeTomlCommentedWriter.ReadAutoTuneLowRiskSwitch("[context]\n# auto_tune_low_risk_context_policy = true\n"));
        Assert.True(RuntimeTomlCommentedWriter.ReadAutoTuneLowRiskSwitch("[context]\nauto_tune_low_risk_context_policy = true\n"));
        Assert.True(RuntimeTomlCommentedWriter.ReadAutoTuneLowRiskSwitch("[evolution]\nauto_tune_low_risk_context_policy = true\n"));

        Assert.Throws<ArgumentException>(() => RuntimeTomlCommentedWriter.ValidatePatch(new ContextPolicyPatchDto { CompactionTriggerRatio = 0.25 }));
        Assert.Throws<ArgumentException>(() => RuntimeTomlCommentedWriter.ValidatePatch(new ContextPolicyPatchDto { RecentMessageLimit = 500 }));
        Assert.Throws<ArgumentException>(() => RuntimeTomlCommentedWriter.ValidatePatch(new ContextPolicyPatchDto { ContextStrategy = "invalid_tier" }));
    }

    [Fact]
    public async Task ContextPolicyProposalService_PromptDedupProposal_QueuesPendingOnlyAtOrAbove200Tokens_With1BasedLines()
    {
        var scope = CreateScope("dedup-queue");
        var service = new ContextPolicyProposalService(new FixedTenantAccessor(), scope);

        // Below 200 wasted tokens -> ignored (no noisy proposals)
        var smallResult = await service.RecordRunAndGenerateProposalsAsync(new RunContextEvolutionObservation
        {
            RunId = Guid.NewGuid().ToString("D"),
            OverlapPairs =
            [
                new ContextOverlapPairEvidenceInput(
                    "agents-md", "AGENTS.md", "workspace_instructions", "AGENTS.md", 1, 8,
                    "skill-git", "git-workflow/SKILL.md", "skill", "skills/git/SKILL.md", 1, 8,
                    OverlapChars: 300, EstimatedWastedTokens: 120,
                    RemediationCode: OverlapRemediationCodes.DedupeWorkspaceInstructions,
                    Excerpt: "Short overlap below threshold")
            ]
        });
        Assert.Empty(smallResult);

        // >= 200 wasted tokens -> queued as pending human-reviewable prompt_dedup_proposal
        var run1 = Guid.NewGuid().ToString("D");
        var run2 = Guid.NewGuid().ToString("D");
        var created = await service.RecordRunAndGenerateProposalsAsync(new RunContextEvolutionObservation
        {
            RunId = run1,
            OverlapPairs =
            [
                new ContextOverlapPairEvidenceInput(
                    "agents-md", "AGENTS.md", "workspace_instructions", "AGENTS.md", 12, 48,
                    "skill-arch", "arch-guide/SKILL.md", "skill", "skills/arch/SKILL.md", 5, 41,
                    OverlapChars: 1120, EstimatedWastedTokens: 285,
                    RemediationCode: OverlapRemediationCodes.MoveToSkillReferences,
                    Excerpt: "Repeated architecture boundary prose across workspace instructions and skill")
            ]
        });

        var proposal = Assert.Single(created);
        Assert.Equal(ContextEvolutionProposalKinds.PromptDedupProposal, proposal.Kind);
        Assert.Equal(ContextEvolutionProposalStatuses.Pending, proposal.Status);
        Assert.True(proposal.RequiresHumanApproval);
        Assert.Equal(OverlapRemediationCodes.MoveToSkillReferences, proposal.RemediationCode);
        Assert.NotNull(proposal.DedupEvidence);
        Assert.Equal("L12-L48 <-> L5-L41", proposal.DedupEvidence!.LineRangeLabel);
        Assert.Equal(285, proposal.DedupEvidence.EstimatedWastedTokens);
        Assert.NotNull(proposal.ComposerPrompt);

        // Second run observing the same overlap updates the pending proposal's evidence_run_ids instead of duplicating
        var refreshed = await service.RecordRunAndGenerateProposalsAsync(new RunContextEvolutionObservation
        {
            RunId = run2,
            OverlapPairs =
            [
                new ContextOverlapPairEvidenceInput(
                    "agents-md", "AGENTS.md", "workspace_instructions", "AGENTS.md", 12, 48,
                    "skill-arch", "arch-guide/SKILL.md", "skill", "skills/arch/SKILL.md", 5, 41,
                    OverlapChars: 1120, EstimatedWastedTokens: 290,
                    RemediationCode: OverlapRemediationCodes.MoveToSkillReferences,
                    Excerpt: "Repeated architecture boundary prose across workspace instructions and skill")
            ]
        });

        var singlePending = Assert.Single((await service.ListProposalsAsync(new ContextEvolutionProposalQuery(Status: "pending"))).Items);
        Assert.Equal(proposal.Id, Assert.Single(refreshed).Id);
        Assert.Equal([run1, run2], singlePending.EvidenceRunIds);
    }

    [Fact]
    public async Task ContextPolicyProposalService_MultiRunDegradation_NeverMutatesTomlUntilHumanApplies()
    {
        var scope = CreateScope("multi-run-global");
        Directory.CreateDirectory(scope.Config);
        Directory.CreateDirectory(scope.State);
        var runtimePath = Path.Combine(scope.Config, "runtime.toml");
        await File.WriteAllTextAsync(runtimePath, LoadBundledRuntimeToml(), Encoding.UTF8);

        var scopeDocs = new ScopeConfigurationDocuments(scope, [new RuntimeConfigurationDocumentValidator()]);
        var service = new ContextPolicyProposalService(new FixedTenantAccessor(), scope, scopeDocs);

        var degradedRunIds = new List<string>();
        for (var i = 1; i <= 4; i++)
        {
            var rId = $"00000000-0000-0000-0000-00000000000{i}";
            var isDegraded = i is 1 or 2; // Only 2 of first 4 runs degraded -> below >=3 threshold
            if (isDegraded) degradedRunIds.Add(rId);

            var generated = await service.RecordRunAndGenerateProposalsAsync(new RunContextEvolutionObservation
            {
                RunId = rId,
                PeakOccupancyPct = isDegraded ? 87.5 : 42.0,
                DegradationDetected = isDegraded,
                DegradationReasons = isDegraded ? ["HIGH_OCCUPANCY_ERROR_SPIKE"] : [],
                HighOccupancyErrorRate = isDegraded ? 0.35 : 0.0,
                LowOccupancyErrorRate = 0.04,
                EffectiveStrategy = "managed",
                EffectiveCompactionTriggerRatio = 0.75,
                EffectiveRecentMessageLimit = 128,
                ForceGlobalPolicyScope = true
            });
            Assert.Empty(generated);
        }

        // 5th run is degraded -> now 3 of last 5 runs degraded -> queues PENDING proposal without touching runtime.toml!
        var fifthRunId = "00000000-0000-0000-0000-000000000005";
        degradedRunIds.Add(fifthRunId);
        var proposals = await service.RecordRunAndGenerateProposalsAsync(new RunContextEvolutionObservation
        {
            RunId = fifthRunId,
            PeakOccupancyPct = 91.0,
            DegradationDetected = true,
            DegradationReasons = ["HIGH_OCCUPANCY_ERROR_SPIKE", "HIGH_OCCUPANCY_LATENCY_SPIKE"],
            HighOccupancyErrorRate = 0.40,
            LowOccupancyErrorRate = 0.05,
            EffectiveStrategy = "managed",
            EffectiveCompactionTriggerRatio = 0.75,
            EffectiveRecentMessageLimit = 128,
            ForceGlobalPolicyScope = true
        });

        var pending = Assert.Single(proposals);
        Assert.Equal(ContextEvolutionProposalKinds.ContextPolicyProposal, pending.Kind);
        Assert.Equal(ContextEvolutionProposalStatuses.Pending, pending.Status);
        Assert.Equal(ContextEvolutionTargetScopes.Global, pending.TargetScope);
        Assert.True(pending.RequiresHumanApproval);
        Assert.Equal(degradedRunIds, pending.EvidenceRunIds);
        Assert.Equal(0.65, pending.PolicyPatch!.CompactionTriggerRatio);
        Assert.Equal(96, pending.PolicyPatch.RecentMessageLimit);

        // Verify conservative posture: runtime.toml on disk is STILL untouched at 0.75 before approval!
        var preApprovalSnapshot = AgentRuntimeConfigurationStore.LoadSnapshot(runtimePath, 1);
        Assert.Equal(0.75, preApprovalSnapshot.Context.CompactionTriggerRatio);
        Assert.Equal(128, preApprovalSnapshot.Context.RecentMessageLimit);

        // Human approves & applies -> writes commented runtime.toml and updates status to "applied"
        var applyResult = await service.ApplyGlobalPolicyProposalAsync(
            pending.Id,
            new ApplyContextEvolutionProposalRequest { Reason = "Approved by operator after inspecting 3 degraded runs." });

        Assert.True(applyResult.Applied);
        Assert.Equal(ContextEvolutionProposalStatuses.Applied, applyResult.Proposal.Status);
        Assert.NotNull(applyResult.UpdatedTomlPreview);
        Assert.Contains(fifthRunId, applyResult.UpdatedTomlPreview!, StringComparison.Ordinal);

        var diskToml = await File.ReadAllTextAsync(runtimePath, Encoding.UTF8);
        Assert.Contains($"proposal_id={pending.Id:D}", diskToml, StringComparison.Ordinal);
        Assert.Contains("auto_tune_low_risk_context_policy = false", diskToml, StringComparison.Ordinal);
        var postApprovalSnapshot = AgentRuntimeConfigurationStore.LoadSnapshot(runtimePath, 2);
        Assert.Equal(0.65, postApprovalSnapshot.Context.CompactionTriggerRatio);
        Assert.Equal(96, postApprovalSnapshot.Context.RecentMessageLimit);
    }

    [Fact]
    public async Task ContextPolicyProposalService_RepeatedCriticalDroppedSources_QueuesModeProposal_AndRejectLocksDecision()
    {
        var scope = CreateScope("mode-critical-drops");
        var service = new ContextPolicyProposalService(new FixedTenantAccessor(), scope);
        var modeId = Guid.NewGuid();

        IReadOnlyList<ContextEvolutionProposalDto> lastGenerated = [];
        for (var i = 1; i <= 3; i++)
        {
            lastGenerated = await service.RecordRunAndGenerateProposalsAsync(new RunContextEvolutionObservation
            {
                RunId = $"11111111-1111-1111-1111-00000000000{i}",
                ModeId = modeId,
                ModeSlug = "deep-research",
                ModeName = "Deep Research",
                PeakOccupancyPct = 79.0,
                DegradationDetected = false,
                DroppedSources = ["workspace_instructions", "mode_pipeline", "reviewed_memory"],
                DroppedTokens = 2400,
                EffectiveStrategy = "managed",
                EffectiveCompactionTriggerRatio = 0.75,
                EffectiveRecentMessageLimit = 128
            });
        }

        var modeProposal = Assert.Single(lastGenerated);
        Assert.Equal(ContextEvolutionTargetScopes.Mode, modeProposal.TargetScope);
        Assert.Equal(modeId, modeProposal.TargetModeId);
        Assert.Equal(3, modeProposal.MetricsComparison!.DroppedCriticalRunCount);
        Assert.Contains("workspace_instructions", modeProposal.MetricsComparison.DroppedSources);

        var rejected = await service.RejectProposalAsync(modeProposal.Id, "Keep current mode budget for benchmark.");
        Assert.Equal(ContextEvolutionProposalStatuses.Rejected, rejected.Status);
        Assert.Equal("Keep current mode budget for benchmark.", rejected.DecisionReason);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RejectProposalAsync(modeProposal.Id, "Duplicate decision"));
    }

    [Theory]
    [InlineData("auto_evolve", false)]
    [InlineData("candidate_only", true)]
    public async Task DispatchEvolutionAsync_InjectsBoundedEvidence_QueuesPendingProposals_AndHonorsEvolutionMode(
        string evolutionMode,
        bool expectedCandidateOnly)
    {
        var scope = CreateScope($"curator-{evolutionMode}");
        var runId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var curatorJson = """
            {
              "memory_candidates": [
                {
                  "scope": "workspace",
                  "kind": "failure_pattern",
                  "content": "Avoid repeating edit_file with identical stale line anchors after FS_STALE_VERSION; re-read target slice first.",
                  "confidence": 0.91,
                  "applicability": "mode:code-implementer",
                  "expiry_condition": "when edit_file auto-rebases stale hunks"
                },
                {
                  "scope": "workspace",
                  "kind": "supervision_rule",
                  "content": "Reject closeout if SAME_ARGS_RETRY streak >= 3 remains unresolved.",
                  "confidence": 0.86
                }
              ],
              "agent_candidates": []
            }
            """;

        var chatClient = new CapturingChatClient(curatorJson);
        var upsertedMemoryItems = new List<UpsertMemoryItemRequest>();
        var contextMemory = PortProxy.Create<IContextMemoryService>((method, args) =>
        {
            if (method.Name == nameof(IContextMemoryService.UpsertItemAsync) && args is [UpsertMemoryItemRequest req, ..])
            {
                upsertedMemoryItems.Add(req);
                return Task.FromResult(new MemoryItemLedgerDto
                {
                    Id = Guid.NewGuid(),
                    Scope = req.Scope,
                    Kind = req.Kind ?? "fact",
                    Content = req.Content,
                    CurrentVersion = 1,
                    SourceRunId = req.SourceRunId,
                    Applicability = req.Applicability,
                    ExpiryCondition = req.ExpiryCondition
                });
            }
            if (method.Name == nameof(IContextMemoryService.GetDocumentAsync) && args is [string docScope, ..])
            {
                return Task.FromResult(new MemoryDocumentDto { Scope = docScope });
            }
            return PortProxy.DefaultReturn(method.ReturnType);
        });
        var longTermMemory = new RecordingLongTermMemoryService();
        var proposalService = new ContextPolicyProposalService(new FixedTenantAccessor(), scope);
        var diagnosticsStub = new StubRunContextDiagnosticsService(new RunContextDiagnosticsDto
        {
            RunId = runId.ToString("D"),
            SessionId = sessionId.ToString("D"),
            Status = "completed",
            WindowBuckets = new ContextWindowBucketReportDto
            {
                PeakOccupancyPct = 89.2,
                LowOccupancyErrorRate = 0.04,
                HighOccupancyErrorRate = 0.38,
                LowOccupancyAvgLatencyMs = 420,
                HighOccupancyAvgLatencyMs = 1350,
                DegradationDetected = true,
                DegradationReasonCodes = ["HIGH_OCCUPANCY_ERROR_SPIKE"]
            },
            Compaction = new ContextCompactionDiagnosticsDto
            {
                TotalDroppedTokensAcrossPacks = 1850,
                CriticalDroppedSources = ["workspace_instructions", "reviewed_memory"]
            },
            Governance = new RunGovernanceDiagnosticsDto
            {
                ActiveSignals = [UnifiedGovernanceSignalCodes.SameArgsRetry, UnifiedGovernanceSignalCodes.FsStaleVersion],
                SignalCounts = new Dictionary<string, int>
                {
                    [UnifiedGovernanceSignalCodes.SameArgsRetry] = 4,
                    [UnifiedGovernanceSignalCodes.FsStaleVersion] = 2
                },
                MaxSameArgsRetryStreak = 4
            }
        });
        var overlapStub = new StubContextOverlapAuditor(new ContextOverlapAuditDto
        {
            Scope = "run_snapshot",
            RunId = runId,
            Pairs =
            [
                new ContextOverlapPairSummaryDto
                {
                    Id = "pair-1",
                    SourceAId = "agents-md",
                    SourceALabel = "AGENTS.md",
                    SourceAKind = ContextAuditSourceKinds.WorkspaceInstructions,
                    SourceBId = "skill-core",
                    SourceBLabel = "core-dev/SKILL.md",
                    SourceBKind = ContextAuditSourceKinds.Skill,
                    SharedChars = 980,
                    EstimatedDuplicateTokens = 260,
                    RemediationCode = OverlapRemediationCodes.DedupeWorkspaceInstructions,
                    RemediationSummary = "Deduplicate repeated build commands between AGENTS.md and core-dev/SKILL.md.",
                    Matches =
                    [
                        new OverlapPassageMatchDto
                        {
                            SourceALines = new LineRangeDto { Start = 10, End = 44 },
                            SourceBLines = new LineRangeDto { Start = 4, End = 38 },
                            LineRangePair = "L10-L44 <-> L4-L38",
                            EstimatedDuplicateTokens = 260,
                            Excerpt = new string('A', 600),
                            RemediationCode = OverlapRemediationCodes.DedupeWorkspaceInstructions
                        }
                    ]
                }
            ],
            Sources =
            [
                new ContextOverlapSourceStatsDto { Id = "agents-md", Label = "AGENTS.md", Path = "AGENTS.md" },
                new ContextOverlapSourceStatsDto { Id = "skill-core", Label = "core-dev/SKILL.md", Path = "skills/core-dev/SKILL.md" }
            ]
        });

        var appendedEventTypes = new List<string>();
        var lifecycle = PortProxy.Create<ILifecycleManager>((method, args) =>
        {
            if (method.Name == nameof(ILifecycleManager.AppendEventAsync))
            {
                if (args is [EventEnvelope env, ..]) appendedEventTypes.Add(env.EventType);
                else if (args is [Guid, string eventType, ..]) appendedEventTypes.Add(eventType);
                return PortProxy.DefaultReturn(method.ReturnType);
            }
            if (method.Name == nameof(ILifecycleManager.GetRunStateAsync))
            {
                return Task.FromResult(new RunState { RunId = runId.ToString("D"), SessionId = sessionId.ToString("D"), Status = RunStatus.Executing });
            }
            return PortProxy.DefaultReturn(method.ReturnType);
        });

        var conversations = PortProxy.Create<IConversationStore>((method, args) =>
        {
            if (method.Name == nameof(IConversationStore.GetContextRevisionAsync))
            {
                return Task.FromResult(1L);
            }
            if (method.Name == nameof(IConversationStore.ApplyContextPatchAsync) && args is [ContextPatchRequest req, ..])
            {
                return Task.FromResult(new ContextPatchApplyResult(Guid.NewGuid(), "applied", req.BaseRevision + 1, req.BaseRevision + 1));
            }
            return PortProxy.DefaultReturn(method.ReturnType);
        });

        var instances = PortProxy.Create<IAgentInstanceService>((method, args) =>
        {
            if (method.Name == nameof(IAgentInstanceService.ListByRunAsync))
            {
                return Task.FromResult<IReadOnlyList<RuntimeAgentInstance>>([]);
            }
            if (method.Name == nameof(IAgentInstanceService.CreateRootAsync) && args is [RuntimeAgentSeed seed, ..])
            {
                var now = DateTimeOffset.UtcNow;
                return Task.FromResult(new RuntimeAgentInstance(
                    Guid.NewGuid(), seed.RunId, null, seed.TaskId, seed.Layer, seed.Role, 0, false, "running",
                    seed.Capabilities, seed.AllowedTools, seed.AllowedResources, seed.BudgetTokens, now, now,
                    seed.AgentDefinitionId ?? Guid.Empty,
                    seed.AgentVersionId ?? Guid.Empty,
                    seed.VersionContentHash));
            }
            return PortProxy.DefaultReturn(method.ReturnType);
        });

        var services = new ServiceCollection()
            .AddSingleton<IContextPolicyProposalService>(proposalService)
            .AddSingleton<IRunContextDiagnosticsService>(diagnosticsStub)
            .AddSingleton<IContextOverlapAuditor>(overlapStub)
            .AddSingleton<IContextMemoryService>(contextMemory)
            .AddSingleton<ILongTermMemoryService>(longTermMemory)
            .BuildServiceProvider();

        var engine = new FullDuplexRunEngine(
            lifecycle,
            conversations,
            instances,
            new StubContextProvider(sessionId.ToString("D"), runId.ToString("D")),
            new MinimalPromptAssembler(),
            new CapturingChatClientFactory(chatClient),
            services,
            NullLogger<FullDuplexRunEngine>.Instance,
            modelResolver: CreateTestModelResolver(),
            contextMemory: contextMemory);

        var curatorDef = new RuntimeAgentDefinition(
            "experience_curator",
            "operation",
            "experience_curator",
            "on_demand",
            ["memory.curate"],
            DirectUserOutput: false,
            ContextAccess: "full")
        {
            AgentDefinitionId = Guid.NewGuid(),
            AgentVersionId = Guid.NewGuid(),
            VersionContentHash = "hash-curator",
            ModelPlan = DefaultModelPlan()
        };

        var modeId = Guid.NewGuid();
        var modeVersionId = Guid.NewGuid();
        var config = new FrozenRunConfigurationV1(
            FrozenRunConfigurationV1.CurrentSchemaVersion,
            "baseline-hash",
            1,
            modeVersionId,
            $"mode:{modeId:D}:1",
            "ask",
            new SpawnPolicy(2, 16, 4),
            new SchedulingPolicy(2, 2, true),
            new SupervisionPolicy(true, 2),
            new ContextPolicy(65_536, 128, true) { Strategy = "managed", CompactionTriggerRatio = 0.75 },
            new MemoryPolicy(
                CandidateOnly: expectedCandidateOnly,
                RetrievalLimit: 8,
                AllowedScopes: ["principal", "workspace", "project", "agent"],
                AllowedKinds: ["fact", "failure_pattern", "supervision_rule", "success_pattern"])
            {
                EvolutionMode = evolutionMode
            },
            new ToolRuntimePolicy("test", true, true, 60, 0),
            [curatorDef],
            [],
            [new RunConfigurationBinding("agent_mode_version", modeId, modeVersionId, "mode-hash")])
        {
            ModeSlug = "code-implementer",
            ModeDisplayName = "Code Implementer",
            Workspace = new FrozenWorkspaceBinding(Guid.NewGuid(), _tempRoot, [], false, null, [], false, WorkspacePathContracts.AbsoluteInRoot)
        };

        var checkpoint = new FullDuplexCheckpointV1
        {
            SessionId = sessionId,
            RunId = runId,
            UserGoal = "Refactor context diagnostics module",
            Tasks =
            [
                new DurableTaskNode
                {
                    TaskId = Guid.NewGuid(),
                    TaskKey = "edit-1",
                    Title = "Edit module",
                    Status = "completed",
                    ResultStatus = "completed",
                    ResultSummary = "Completed after resolving stale file retries"
                }
            ]
        };

        var runState = new RunState
        {
            RunId = runId.ToString("D"),
            SessionId = sessionId.ToString("D"),
            TenantId = Guid.Empty.ToString("D"),
            WorkspaceId = Guid.Empty.ToString("D"),
            Status = RunStatus.Completed
        };

        await engine.DispatchEvolutionAsync(
            new OperationalTriggerMatch(curatorDef, OperationalTriggerPoint.RunFinalized, "run_closed"),
            runState,
            config,
            checkpoint,
            CancellationToken.None);

        // 1. Verify bounded Context & Trajectory Evidence was injected into curator prompt
        Assert.NotNull(chatClient.LastUserPrompt);
        Assert.Contains("Context & Trajectory Evidence:", chatClient.LastUserPrompt!, StringComparison.Ordinal);
        Assert.Contains("89.2%", chatClient.LastUserPrompt!, StringComparison.Ordinal);
        Assert.Contains("HIGH_OCCUPANCY_ERROR_SPIKE", chatClient.LastUserPrompt!, StringComparison.Ordinal);
        Assert.Contains("workspace_instructions", chatClient.LastUserPrompt!, StringComparison.Ordinal);
        Assert.Contains("SAME_ARGS_RETRY=4", chatClient.LastUserPrompt!, StringComparison.Ordinal);
        Assert.Contains("L10-L44 <-> L4-L38", chatClient.LastUserPrompt!, StringComparison.Ordinal);

        // 2. Verify pending prompt_dedup_proposal queued automatically with event
        var queuedList = await proposalService.ListProposalsAsync(new ContextEvolutionProposalQuery(Status: "pending"));
        var dedup = Assert.Single(queuedList.Items);
        Assert.Equal(ContextEvolutionProposalKinds.PromptDedupProposal, dedup.Kind);
        Assert.Equal("L10-L44 <-> L4-L38", dedup.DedupEvidence!.LineRangeLabel);
        Assert.Contains("context.evolution.proposal_queued", appendedEventTypes);

        // 3. Verify failure_pattern & supervision_rule persistence honors evolution_mode with provenance
        if (!expectedCandidateOnly)
        {
            Assert.Equal(2, upsertedMemoryItems.Count);
            Assert.Empty(longTermMemory.Candidates);
            var fp = Assert.Single(upsertedMemoryItems, x => x.Kind == "failure_pattern");
            Assert.Equal(runId, fp.SourceRunId);
            Assert.Equal("mode:code-implementer", fp.Applicability);
            Assert.Equal("when edit_file auto-rebases stale hunks", fp.ExpiryCondition);

            var sr = Assert.Single(upsertedMemoryItems, x => x.Kind == "supervision_rule");
            Assert.Equal(runId, sr.SourceRunId);
            Assert.Equal("mode:code-implementer", sr.Applicability);
            Assert.False(string.IsNullOrWhiteSpace(sr.ExpiryCondition));
        }
        else
        {
            Assert.Empty(upsertedMemoryItems);
            Assert.Equal(2, longTermMemory.Candidates.Count);
            var fp = Assert.Single(longTermMemory.Candidates, x => x.Kind == "failure_pattern");
            Assert.Equal(runId, fp.SourceRunId);
            Assert.Equal("mode:code-implementer", fp.Applicability);
            Assert.Equal("when edit_file auto-rebases stale hunks", fp.ExpiryCondition);

            var sr = Assert.Single(longTermMemory.Candidates, x => x.Kind == "supervision_rule");
            Assert.Equal(runId, sr.SourceRunId);
            Assert.Equal("mode:code-implementer", sr.Applicability);
            Assert.False(string.IsNullOrWhiteSpace(sr.ExpiryCondition));
        }
    }

    [Fact]
    public async Task BuildContextAsync_RuntimeContextPressure_EmitsSignalAndWakesCompressorEarly_On70PctWithRetryOrDroppedCriticalSources()
    {
        var runId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var compactionAppliedTcs = new TaskCompletionSource<ContextPatchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var appendedEventTypes = new List<string>();

        var checkpointWithRetry = new FullDuplexCheckpointV1
        {
            SessionId = sessionId,
            RunId = runId,
            UserGoal = "Fix failing build",
            Tasks =
            [
                new DurableTaskNode
                {
                    TaskId = Guid.NewGuid(),
                    TaskKey = "task-1",
                    Status = "running",
                    ToolTurns =
                    [
                        new WorkerToolTurn { CallId = "c1", ToolId = "edit_file", ArgumentsJson = "{\"path\":\"a.cs\"}", ErrorCategory = "fs_stale_version", ToolSuccess = false },
                        new WorkerToolTurn { CallId = "c2", ToolId = "edit_file", ArgumentsJson = "{\"path\":\"a.cs\"}", ErrorCategory = "fs_stale_version", ToolSuccess = false }
                    ]
                }
            ]
        };
        var checkpointJson = JsonSerializer.Serialize(checkpointWithRetry, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var lifecycle = PortProxy.Create<ILifecycleManager>((method, args) =>
        {
            if (method.Name == nameof(ILifecycleManager.AppendEventAsync))
            {
                lock (appendedEventTypes)
                {
                    if (args is [EventEnvelope env, ..]) appendedEventTypes.Add(env.EventType);
                    else if (args is [Guid, string eventType, ..]) appendedEventTypes.Add(eventType);
                }
                return PortProxy.DefaultReturn(method.ReturnType);
            }
            if (method.Name == nameof(ILifecycleManager.GetCurrentRunCheckpointAsync))
            {
                return Task.FromResult<RunCheckpoint?>(new RunCheckpoint(
                    Guid.NewGuid(), runId.ToString("D"), 1, "executing", checkpointJson, "ref", "hash", checkpointJson.Length, 1, "idem", DateTimeOffset.UtcNow));
            }
            return PortProxy.DefaultReturn(method.ReturnType);
        });

        var conversations = PortProxy.Create<IConversationStore>((method, args) =>
        {
            if (method.Name == nameof(IConversationStore.GetContextRevisionAsync))
            {
                return Task.FromResult(2L);
            }
            if (method.Name == nameof(IConversationStore.ApplyContextPatchAsync) && args is [ContextPatchRequest req, ..])
            {
                compactionAppliedTcs.TrySetResult(req);
                return Task.FromResult(new ContextPatchApplyResult(Guid.NewGuid(), "applied", req.BaseRevision + 1, req.BaseRevision + 1));
            }
            return PortProxy.DefaultReturn(method.ReturnType);
        });

        var instances = PortProxy.Create<IAgentInstanceService>((method, _) => PortProxy.DefaultReturn(method.ReturnType));
        var compressorChat = new CapturingChatClient("当前目标: 修复构建\n关键约束: 保持兼容\n已完成事项: 定位冲突");

        // Pack is at 72% occupancy (72,000 / 100,000), BELOW static CompactionTriggerRatio = 0.85,
        // so static threshold sets compaction_recommended = "false" — ONLY P2 runtime bleeding control wakes it!
        var contextProvider = PortProxy.Create<IContextProvider>((method, _) =>
        {
            if (method.Name == nameof(IContextProvider.BuildContextAsync))
            {
                return Task.FromResult(new ContextPack
                {
                    SessionId = sessionId.ToString("D"),
                    RunId = runId.ToString("D"),
                    TokenBudget = 100_000,
                    EstimatedTokens = 72_000,
                    Evidence = [new ContextEvidence { Source = "conversation_turns", Content = "Turn history", EstimatedTokens = 72_000 }],
                    Dropped = [],
                    Metadata = new Dictionary<string, string>
                    {
                        ["compaction_recommended"] = "false",
                        ["context_strategy"] = "managed"
                    }
                });
            }
            return PortProxy.DefaultReturn(method.ReturnType);
        });

        var services = new ServiceCollection().BuildServiceProvider();
        var engine = new FullDuplexRunEngine(
            lifecycle,
            conversations,
            instances,
            contextProvider,
            new MinimalPromptAssembler(),
            new CapturingChatClientFactory(compressorChat),
            services,
            NullLogger<FullDuplexRunEngine>.Instance,
            modelResolver: CreateTestModelResolver());

        var compressorDef = new RuntimeAgentDefinition(
            "context_compressor",
            "operation",
            "context_compressor",
            "on_demand",
            ["context.compress"],
            DirectUserOutput: false,
            ContextAccess: "full")
        {
            AgentDefinitionId = Guid.NewGuid(),
            AgentVersionId = Guid.NewGuid(),
            VersionContentHash = "hash-compressor",
            ModelPlan = DefaultModelPlan()
        };

        var workerDef = new RuntimeAgentDefinition(
            "worker.code",
            "execution",
            "task_executor",
            "on_demand",
            ["tool.code"],
            DirectUserOutput: false,
            ContextAccess: "scoped");

        var modeId = Guid.NewGuid();
        var modeVersionId = Guid.NewGuid();
        var config = new FrozenRunConfigurationV1(
            FrozenRunConfigurationV1.CurrentSchemaVersion,
            "baseline",
            1,
            modeVersionId,
            "space",
            "ask",
            new SpawnPolicy(2, 16, 4),
            new SchedulingPolicy(2, 2, true),
            new SupervisionPolicy(true, 2),
            new ContextPolicy(100_000, 128, true) { Strategy = "managed", CompactionTriggerRatio = 0.85 },
            new MemoryPolicy(false, 8, [], []),
            new ToolRuntimePolicy("test", true, true, 60, 0),
            [compressorDef],
            [workerDef],
            [new RunConfigurationBinding("agent_mode_version", modeId, modeVersionId, "mode-hash")]);

        var runState = new RunState
        {
            RunId = runId.ToString("D"),
            SessionId = sessionId.ToString("D"),
            ContextRevision = 2,
            Status = RunStatus.Executing
        };

        var builtPack = await engine.BuildContextAsync(runState, config, workerDef, "Fix a.cs", "Fix failing build", CancellationToken.None);

        Assert.Equal("true", builtPack.Metadata["compaction_recommended"]);
        Assert.Equal("true", builtPack.Metadata["context_pressure"]);
        Assert.Equal(UnifiedGovernanceSignalCodes.ContextPressure, builtPack.Metadata["context_pressure_signal"]);

        var appliedPatch = await compactionAppliedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("compaction", appliedPatch.Kind);
        Assert.Contains("修复构建", appliedPatch.Content, StringComparison.Ordinal);

        lock (appendedEventTypes)
        {
            Assert.Contains("context.pressure", appendedEventTypes);
        }
    }

    [Fact]
    public async Task BuildContextAsync_DroppedCriticalSources_TriggersEarlyContextPressureEvenBelow70Pct()
    {
        var runId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var appendedEventTypes = new List<string>();

        var lifecycle = PortProxy.Create<ILifecycleManager>((method, args) =>
        {
            if (method.Name == nameof(ILifecycleManager.AppendEventAsync))
            {
                if (args is [EventEnvelope env, ..]) appendedEventTypes.Add(env.EventType);
                else if (args is [Guid, string eventType, ..]) appendedEventTypes.Add(eventType);
                return PortProxy.DefaultReturn(method.ReturnType);
            }
            return PortProxy.DefaultReturn(method.ReturnType);
        });
        var conversations = PortProxy.Create<IConversationStore>((method, _) => PortProxy.DefaultReturn(method.ReturnType));
        var instances = PortProxy.Create<IAgentInstanceService>((method, _) => PortProxy.DefaultReturn(method.ReturnType));

        var contextProvider = PortProxy.Create<IContextProvider>((method, _) =>
        {
            if (method.Name == nameof(IContextProvider.BuildContextAsync))
            {
                return Task.FromResult(new ContextPack
                {
                    SessionId = sessionId.ToString("D"),
                    RunId = runId.ToString("D"),
                    TokenBudget = 65_536,
                    EstimatedTokens = 40_000, // 61% occupancy, but critical sources were dropped!
                    Evidence = [],
                    Dropped =
                    [
                        new ContextEvidence { Source = "workspace_instructions", Content = "AGENTS.md", EstimatedTokens = 4000 },
                        new ContextEvidence { Source = "reviewed_memory", Content = "Core facts", EstimatedTokens = 1500 }
                    ],
                    Metadata = new Dictionary<string, string> { ["compaction_recommended"] = "false" }
                });
            }
            return PortProxy.DefaultReturn(method.ReturnType);
        });

        var engine = new FullDuplexRunEngine(
            lifecycle,
            conversations,
            instances,
            contextProvider,
            new MinimalPromptAssembler(),
            new CapturingChatClientFactory(new CapturingChatClient("")),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<FullDuplexRunEngine>.Instance);

        var workerDef = new RuntimeAgentDefinition("worker", "execution", "task_executor", "on_demand", [], false, "scoped");
        var config = new FrozenRunConfigurationV1(
            FrozenRunConfigurationV1.CurrentSchemaVersion, "baseline", 1, Guid.NewGuid(), "space", "ask",
            new(2, 16, 4), new(2, 2, true), new(true, 2),
            new(65_536, 128, true) { Strategy = "managed", CompactionTriggerRatio = 0.80 },
            new(false, 8, [], []), new("test", true, true, 60, 0), [], [workerDef], []);

        var pack = await engine.BuildContextAsync(
            new RunState { RunId = runId.ToString("D"), SessionId = sessionId.ToString("D"), ContextRevision = 1, Status = RunStatus.Executing },
            config, workerDef, "task", "goal", CancellationToken.None);

        Assert.Equal("true", pack.Metadata["compaction_recommended"]);
        Assert.Equal("critical_sources_dropped", pack.Metadata["context_pressure_reason"]);
        Assert.Contains("context.pressure", appendedEventTypes);
    }

    [Fact]
    public async Task FullComposition_IntegratesRealP1AuditorAndDiagnosticsWithP2ProposalService()
    {
        var compRoot = Path.Combine(_tempRoot, "full-comp");
        Directory.CreateDirectory(compRoot);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(compRoot, "tinadec.db"),
                ["TinadecPersistence:DataRoot"] = Path.Combine(compRoot, "data"),
                ["TinadecPersistence:SecretStore"] = "auto"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(config);
        services.AddLogging();
        services.AddTinadecPersistence(config, compRoot);
        TinadecCore.Runtime.TinadecCoreServiceCollectionExtensions.AddTinadecCore(services);
        await using var provider = services.BuildServiceProvider();

        var overlapAuditor = provider.GetService<IContextOverlapAuditor>();
        var runDiagnostics = provider.GetService<IRunContextDiagnosticsService>();
        var proposalService = ContextPolicyProposalServiceRegistry.Resolve(provider);
        var proposalServiceSecondResolve = ContextPolicyProposalServiceRegistry.Resolve(provider);

        Assert.NotNull(overlapAuditor);
        Assert.NotNull(runDiagnostics);
        Assert.NotNull(proposalService);
        Assert.Same(proposalService, proposalServiceSecondResolve);

        // Run real P1 64-char Winnowing auditor on duplicated multi-line instructions (~250+ tokens)
        var sharedBlock = string.Join("\n", Enumerable.Range(1, 24).Select(i =>
            $"Rule {i:00}: Every mutation tool must respect workspace write_scope boundaries, avoid stale file version overwrites, and verify build diagnostics before completion."));
        var audit = overlapAuditor!.AuditSources(
        [
            new ContextAuditSourceInputDto
            {
                Id = "agents-md",
                Label = "AGENTS.md",
                Kind = ContextAuditSourceKinds.WorkspaceInstructions,
                Content = "# Repository Guidelines\n" + sharedBlock + "\n# End"
            },
            new ContextAuditSourceInputDto
            {
                Id = "skill-eng",
                Label = "engineering/SKILL.md",
                Kind = ContextAuditSourceKinds.Skill,
                Content = "---\nname: engineering\n---\n" + sharedBlock
            }
        ]);

        var topPair = Assert.Single(audit.Pairs);
        Assert.True(topPair.EstimatedDuplicateTokens >= ContextPolicyProposalService.DefaultOverlapWasteTokenThreshold);
        Assert.NotEmpty(topPair.Matches);
        var topMatch = topPair.Matches[0];

        // Feed real P1 audit output into P2 proposal service
        var queued = await proposalService!.RecordRunAndGenerateProposalsAsync(new RunContextEvolutionObservation
        {
            RunId = Guid.NewGuid().ToString("D"),
            PeakOccupancyPct = 84.0,
            DegradationDetected = true,
            OverlapPairs =
            [
                new ContextOverlapPairEvidenceInput(
                    topPair.SourceAId, topPair.SourceALabel, topPair.SourceAKind, "AGENTS.md",
                    topMatch.SourceALines.Start, topMatch.SourceALines.End,
                    topPair.SourceBId, topPair.SourceBLabel, topPair.SourceBKind, "skills/engineering/SKILL.md",
                    topMatch.SourceBLines.Start, topMatch.SourceBLines.End,
                    topPair.SharedChars, topPair.EstimatedDuplicateTokens, topPair.RemediationCode, topMatch.Excerpt)
            ]
        });

        var dedupProposal = Assert.Single(queued);
        Assert.Equal(ContextEvolutionProposalKinds.PromptDedupProposal, dedupProposal.Kind);
        Assert.Equal(ContextEvolutionProposalStatuses.Pending, dedupProposal.Status);
        Assert.Equal("L2-L25 <-> L4-L27", dedupProposal.DedupEvidence!.LineRangeLabel);
    }

    private static FrozenModelPlan DefaultModelPlan() => new(
        "route",
        "agent_version",
        [new FrozenModelCandidate(0, Guid.NewGuid(), Guid.NewGuid(), "gemini-2.5-flash", "openai")]);

    private static IAgentModelResolver CreateTestModelResolver() =>
        PortProxy.Create<IAgentModelResolver>((method, _) =>
        {
            if (method.Name == nameof(IAgentModelResolver.ResolveInvocationCandidatesAsync))
            {
                IReadOnlyList<ChatResolution> candidates =
                [
                    new ChatResolution { IsAvailable = true, Model = "gemini-2.5-flash", Protocol = "openai" }
                ];
                return Task.FromResult(candidates);
            }
            if (method.Name == nameof(IAgentModelResolver.StartInvocationAsync))
            {
                return Task.FromResult(Guid.NewGuid());
            }
            if (method.Name == nameof(IAgentModelResolver.CompleteInvocationAsync))
            {
                return Task.CompletedTask;
            }
            return PortProxy.DefaultReturn(method.ReturnType);
        });

    private StorageScopeDescriptor CreateScope(string name)
    {
        var root = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(root);
        return new StorageScopeDescriptor("test-" + name, "user", root);
    }

    private static string LoadBundledRuntimeToml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DmaEA", "Configuration", "default-agent-runtime.toml")))
        {
            dir = dir.Parent;
        }
        var path = dir is not null
            ? Path.Combine(dir.FullName, "DmaEA", "Configuration", "default-agent-runtime.toml")
            : Path.Combine(AppContext.BaseDirectory, "DmaEA", "Configuration", "default-agent-runtime.toml");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private sealed class FixedTenantAccessor : ITenantContextAccessor
    {
        public TenantContext Current { get; } = new(Guid.Empty, Guid.Empty, Guid.Empty, "owner", IsDevelopment: true);
    }

    private sealed class StubRunContextDiagnosticsService(RunContextDiagnosticsDto result) : IRunContextDiagnosticsService
    {
        public RunContextDiagnosticsDto BuildDiagnostics(RunContextDiagnosticsBuildInput input) => result;
        public Task<RunContextDiagnosticsDto?> DiagnoseRunAsync(Guid runId, Guid? tenantId = null, Guid? workspaceId = null, int? contextWindowTokens = null, string? selectedPackId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<RunContextDiagnosticsDto?>(result);
    }

    private sealed class StubContextOverlapAuditor(ContextOverlapAuditDto result) : IContextOverlapAuditor
    {
        public ContextOverlapAuditDto AuditSources(IReadOnlyList<ContextAuditSourceInputDto> sources, WinnowingAuditOptions? options = null) => result;
        public Task<ContextOverlapAuditDto> AuditAsync(ContextOverlapAuditRequestDto request, ContextAuditExecutionScope? scope = null, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class CapturingChatClient(string responseText) : IChatClient
    {
        public string? LastInstructions { get; private set; }
        public string? LastUserPrompt { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("test-curator");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastInstructions = options?.Instructions;
            LastUserPrompt = string.Join("\n", messages.Select(m => m.Text));
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, responseText)]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CapturingChatClientFactory(IChatClient client) : IAgentChatClientFactory
    {
        public Task<ChatResolution> ResolveChatAsync(string route, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResolution { IsAvailable = true, Model = "gemini-2.5-flash", Protocol = "openai" });

        public Task<IChatClient> CreateAsync(ChatResolution resolution, CancellationToken cancellationToken = default) =>
            Task.FromResult(client);
    }

    private sealed class StubContextProvider(string sessionId, string runId) : IContextProvider
    {
        public Task<ContextPack> BuildContextAsync(ContextBuildRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ContextPack
            {
                SessionId = sessionId,
                RunId = runId,
                TokenBudget = 65_536,
                EstimatedTokens = 42_000,
                Evidence = [new ContextEvidence { Source = "conversation_turns", Content = "Recent turn summary", EstimatedTokens = 1200 }],
                Dropped = [new ContextEvidence { Source = "workspace_instructions", Content = "AGENTS.md", EstimatedTokens = 900 }]
            });
    }

    private sealed class MinimalPromptAssembler : IPromptAssembler
    {
        public Task<PromptAssemblyResult> AssembleAsync(string agentId, ContextPack? contextPack, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PromptAssemblyResult { Instructions = $"base:{agentId}" });
    }

    private sealed class RecordingLongTermMemoryService : ILongTermMemoryService
    {
        public List<MemoryCandidateProposal> Candidates { get; } = [];

        public Task<MemoryCandidate> CreateCandidateAsync(MemoryCandidateProposal proposal, CancellationToken cancellationToken = default)
        {
            Candidates.Add(proposal);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new MemoryCandidate(
                Guid.NewGuid(),
                proposal.SourceRunId,
                proposal.GeneratedByInstanceId,
                proposal.Scope,
                proposal.Kind,
                "proposed",
                proposal.Confidence,
                proposal.Content,
                null,
                null,
                now,
                now,
                proposal.Evidence,
                proposal.Applicability,
                proposal.ExpiryCondition));
        }

        public Task<IReadOnlyList<MemoryCandidate>> ListCandidatesAsync(MemoryCandidateQuery? query = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryCandidate>>([]);

        public Task<MemoryCandidate> DecideCandidateAsync(Guid candidateId, string decision, string? reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LongTermMemoryItem>> ListItemsAsync(MemoryItemQuery? query = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LongTermMemoryItem>>([]);

        public Task<LongTermMemoryItem> RevokeAsync(Guid itemId, string? reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public class PortProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?>? _handler;

        public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var proxy = Create<T, PortProxy>();
            ((PortProxy)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod is null ? null : _handler!(targetMethod, args);

        public static object? DefaultReturn(Type returnType)
        {
            if (returnType == typeof(void)) return null;
            if (returnType == typeof(Task)) return Task.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GetGenericArguments()[0];
                object? defaultVal = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [defaultVal]);
            }
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}
