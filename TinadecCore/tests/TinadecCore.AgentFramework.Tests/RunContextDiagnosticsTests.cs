using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentConfiguration;
using TinadecCore.Context;
using TinadecCore.Contracts.Dtos;
using TinadecCore.Contracts.Events;
using TinadecCore.DmaEA;
using TinadecCore.DmaEA.LiveExplainer;
using TinadecCore.Lifecycle;
using TinadecCore.Persistence;
using TinadecCore.Runtime;
using TinadecCore.Tools;

namespace TinadecCore.AgentFramework.Tests;

public sealed class RunContextDiagnosticsTests
{
    [Fact]
    public void WindowBuckets_AndContextRotDetector_DetectsHighOccupancyDegradation_AndFallbacks()
    {
        var service = new RunContextDiagnosticsService(new FakeLifecycleManager());
        var t0 = DateTimeOffset.Parse("2026-11-22T01:00:00Z");

        var invocations = new[]
        {
            new ModelInvocationDto
            {
                Id = Guid.NewGuid(),
                CallId = Guid.NewGuid(),
                Attempt = 1,
                Model = "gemini-2.5-pro",
                Protocol = "openai",
                Status = "completed",
                InputTokens = 12_000, // 12% -> bucket 2 (10-15%)
                OutputTokens = 800,
                TotalTokens = 12_800,
                StartedAt = t0,
                CompletedAt = t0.AddMilliseconds(800)
            },
            new ModelInvocationDto
            {
                Id = Guid.NewGuid(),
                CallId = Guid.NewGuid(),
                Attempt = 1,
                Model = "gemini-2.5-pro",
                Protocol = "openai",
                Status = "failed",
                ErrorCategory = "timeout",
                InputTokens = 72_000, // 72% -> bucket 14 (70-75%)
                StartedAt = t0.AddMinutes(1),
                CompletedAt = t0.AddMinutes(1).AddMilliseconds(4000)
            },
            new ModelInvocationDto
            {
                Id = Guid.NewGuid(),
                CallId = Guid.NewGuid(),
                Attempt = 1,
                Model = "gemini-2.5-pro",
                Protocol = "openai",
                Status = "failed",
                ErrorCategory = "context_overflow",
                InputTokens = 100_000, // 100% -> bucket 19 (95-100%)
                StartedAt = t0.AddMinutes(2),
                CompletedAt = t0.AddMinutes(2).AddMilliseconds(5000)
            }
        };

        var dto = service.BuildDiagnostics(new RunContextDiagnosticsBuildInput(
            RunId: "run-rot-1",
            SessionId: "sess-1",
            Status: "failed",
            ModelInvocations: invocations,
            ContextWindowTokens: 100_000));

        var report = dto.WindowBuckets;
        Assert.Equal(20, report.Buckets.Count);
        Assert.Equal("0-5%", report.Buckets[0].Label);
        Assert.Equal("95-100%", report.Buckets[19].Label);
        Assert.Equal(1, report.Buckets[2].SampleCount);
        Assert.Equal(0.0, report.Buckets[2].ErrorRate);
        Assert.Equal(1, report.Buckets[14].SampleCount);
        Assert.Equal(1.0, report.Buckets[14].ErrorRate);
        Assert.Equal(1, report.Buckets[19].SampleCount);
        Assert.Equal(5000, report.Buckets[19].AvgLatencyMs);
        Assert.Equal(100.0, report.PeakOccupancyPct);
        Assert.True(report.DegradationDetected);
        Assert.Contains("high_occupancy_error_spike", report.DegradationReasonCodes);
        Assert.Contains("high_occupancy_latency_spike", report.DegradationReasonCodes);
        Assert.Contains(dto.ExecutiveFindings, f => f.Code == "CONTEXT_ROT_DETECTED");

        // Fallback to ContextPack samples when neither invocations nor tool executions exist.
        var packOnly = service.BuildDiagnostics(new RunContextDiagnosticsBuildInput(
            RunId: "run-pack-only",
            SessionId: "sess-1",
            Status: "completed",
            ContextPacks:
            [
                new DiagnosticContextPackSnapshot(
                    "pack-only-1",
                    "run-pack-only",
                    null,
                    2,
                    8000,
                    10000,
                    ["system_prompt", "session_history"],
                    [new("system_prompt", 3000), new("session_history", 5000)],
                    [new("reviewed_memory", 400)],
                    t0)
            ]));
        Assert.Single(packOnly.WindowBuckets.Samples);
        Assert.Equal("context_pack", packOnly.WindowBuckets.Samples[0].SourceKind);
        Assert.Equal(80.0, packOnly.WindowBuckets.PeakOccupancyPct);
    }

    [Fact]
    public void CompactionReconciliation_AndCategoryTree_MatchesMultiPackTreemapTruth()
    {
        var service = new RunContextDiagnosticsService(new FakeLifecycleManager());
        var t0 = DateTimeOffset.Parse("2026-11-22T01:00:00Z");
        var packs = new[]
        {
            new DiagnosticContextPackSnapshot(
                Id: "pack-1",
                RunId: "run-1",
                LaneKey: null,
                EvidenceCount: 6,
                EstimatedTokens: 7200,
                TokenBudget: 10000,
                Sources: ["system_prompt", "workspace_instructions", "workspace_skills", "reviewed_memory", "reviewed_memory", "session_history"],
                SourceTokens:
                [
                    new("system_prompt", 1200),
                    new("workspace_instructions", 1600),
                    new("workspace_skills", 900),
                    new("reviewed_memory", 700),
                    new("reviewed_memory", 500),
                    new("session_history", 2300)
                ],
                DroppedSources:
                [
                    new("session_history", 1400),
                    new("reviewed_memory", 300)
                ],
                CreatedAt: t0),
            new DiagnosticContextPackSnapshot(
                Id: "pack-2",
                RunId: "run-1",
                LaneKey: "engineer",
                EvidenceCount: 4,
                EstimatedTokens: 5400,
                TokenBudget: 10000,
                Sources: ["system_prompt", "session_compaction", "workspace_instructions", "tool"],
                SourceTokens:
                [
                    new("system_prompt", 1200),
                    new("session_compaction", 1100),
                    new("workspace_instructions", 1600),
                    new("tool", 1500)
                ],
                DroppedSources: [],
                CreatedAt: t0.AddMinutes(2))
        };

        var diagPack1 = service.BuildDiagnostics(new RunContextDiagnosticsBuildInput(
            "run-1", "sess-1", "running", ContextPacks: packs, SelectedPackId: "pack-1"));

        Assert.Equal(2, diagPack1.Compaction.PackCount);
        Assert.Equal(7200, diagPack1.Compaction.Reconciliation.RetainedTokens);
        Assert.Equal(1700, diagPack1.Compaction.Reconciliation.DroppedTokens);
        Assert.Equal(8900, diagPack1.Compaction.Reconciliation.PreCompactionTokens);
        Assert.Equal(72.0, diagPack1.Compaction.Reconciliation.UtilizationPercent);
        Assert.True(diagPack1.Compaction.Reconciliation.HasPricingData);
        Assert.Contains("reviewed_memory", diagPack1.Compaction.CriticalDroppedSources);

        var memoryCat = Assert.Single(diagPack1.Compaction.Root.Children, c => c.Category == ContextSourceCategories.Memory);
        Assert.Equal(1200, memoryCat.RetainedTokens);
        Assert.Equal(300, memoryCat.DroppedTokens);
        Assert.Equal(2, memoryCat.Children.Count);

        var diagPack2 = service.BuildDiagnostics(new RunContextDiagnosticsBuildInput(
            "run-1", "sess-1", "completed", ContextPacks: packs, SelectedPackId: "pack-2"));
        Assert.True(diagPack2.Compaction.Reconciliation.HasCompactionDigest);
        Assert.Equal(5400 - 7200, diagPack2.Compaction.Reconciliation.PreviousPackRetainedDelta);
        Assert.Equal(0 - 1700, diagPack2.Compaction.Reconciliation.PreviousPackDroppedDelta);
    }

    [Fact]
    public void UnifiedGovernanceSignals_DetectsAllNineSignals_Streaks_DiffStats_AndErrorFrames()
    {
        // Verify LiveExplainer GovernanceSignalCodes exposes all 9 unified codes and SentinelAlert supports each.
        Assert.Equal(9, GovernanceSignalCodes.All.Count);
        foreach (var code in GovernanceSignalCodes.All)
        {
            var alert = LiveExplainerSemanticFolder.CreateSentinelAlert(code, "unit-test");
            Assert.Equal(code, alert.Code);
            Assert.False(string.IsNullOrWhiteSpace(alert.SuggestedSteerPrompt));
        }

        var service = new RunContextDiagnosticsService(new FakeLifecycleManager());
        var t0 = DateTimeOffset.Parse("2026-11-22T01:00:00Z");

        DiagnosticToolExecutionItem MakeExec(
            string id,
            long seq,
            string toolId,
            string status = "completed",
            bool? success = true,
            string? errCat = null,
            string? args = null,
            string? resourcePath = "src/app.ts",
            string? cmd = null,
            string? summary = null,
            IReadOnlyList<string>? evidence = null) =>
            new(
                Id: id,
                RunId: "run-gov",
                SessionId: "sess-1",
                ToolId: toolId,
                ToolDisplayName: toolId,
                Status: status,
                ToolSuccess: success,
                ErrorCategory: errCat,
                Summary: summary,
                CheckpointSummary: null,
                ArgumentsSummary: args,
                ResourcePath: resourcePath,
                Command: cmd,
                Cwd: null,
                Query: null,
                TaskId: "task-1",
                AgentInstanceId: "agent-1",
                DurationMs: 1200,
                RequestedSeq: seq,
                RequestedAt: t0.AddSeconds(seq),
                UpdatedAt: t0.AddSeconds(seq + 1),
                EventTypes: ["tool.execution.requested"],
                Evidence: evidence ?? []);

        var execs = new[]
        {
            MakeExec("e-edit", 1, "edit_file", summary: "Patched src/auth.ts (+14/-3)"),
            MakeExec("e-stale-1", 2, "edit_file", "failed", false, "fs_stale_version",
                args: "{\"path\":\"src/auth.ts\",\"line\":42}",
                summary: "src/auth.ts:42:7 line_hash mismatch (Bearer secretJwtTokenValue)",
                evidence: ["41 | export function verify(token: string) {", "> 42 |   return jwt.decode(token)", "43 | }"]),
            MakeExec("e-stale-2", 3, "edit_file", "failed", false, "fs_stale_version",
                args: "{\"path\":\"src/auth.ts\",\"line\":42}",
                summary: "Repeated edit_file call failed with stale version"),
            MakeExec("e-not-obs", 4, "write_file", "failed", false, "fs_not_observed", summary: "File not observed before write"),
            MakeExec("e-rejected", 5, "shell", "rejected", false, cmd: "rm -rf dist", summary: "User rejected approval request"),
            MakeExec("e-worktree", 6, "write_file", "failed", false, "worktree_guard", summary: "Path escapes active worktree isolation boundary"),
            MakeExec("e-scope", 7, "edit_file", "failed", false, "write_scope_locked", summary: "outside write_scope: lease conflict"),
            MakeExec("e-timeout", 8, "web_fetch", "timeout", false, "timeout", summary: "web_fetch gave up on https://example.com after 15000 ms")
        };

        var diag = service.BuildDiagnostics(new RunContextDiagnosticsBuildInput(
            RunId: "run-gov",
            SessionId: "sess-1",
            Status: "failed",
            ContextPacks:
            [
                new DiagnosticContextPackSnapshot("p1", "run-gov", null, 2, 8200, 10000, ["system_prompt"], [new("system_prompt", 8200)], [], t0)
            ],
            ToolExecutions: execs,
            LifecycleSignals: [GovernanceSignalCodes.LoopDetected]));

        Assert.Equal(8, diag.Governance.TotalToolCalls);
        Assert.Equal(14, diag.Governance.TotalAddedLines);
        Assert.Equal(3, diag.Governance.TotalRemovedLines);
        Assert.Equal(2, diag.Governance.MaxSameArgsRetryStreak);

        foreach (var expectedSignal in GovernanceSignalCodes.Ordered)
        {
            Assert.Contains(expectedSignal, diag.Governance.ActiveSignals);
            Assert.True(diag.Governance.SignalCounts[expectedSignal] >= 1);
        }

        var firstFrame = Assert.Single(diag.Governance.ErrorFrames, f => f.SourceId == "e-stale-1");
        Assert.Equal("src/auth.ts", firstFrame.FilePath);
        Assert.Equal(42, firstFrame.Line);
        Assert.Contains(firstFrame.Lines, l => l.Highlight && l.LineNumber == 42);
        // Secret Bearer token inside summary must be redacted.
        Assert.DoesNotContain(firstFrame.Lines, l => l.Text.Contains("secretJwtTokenValue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunDossierService_AttachesBoundedContextHealthSummary()
    {
        var tenantId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var fakeLifecycle = new FakeLifecycleManager();
        fakeLifecycle.SeedRun(
            new RunState
            {
                RunId = runId.ToString(),
                TenantId = tenantId.ToString(),
                WorkspaceId = workspaceId.ToString(),
                SessionId = sessionId.ToString(),
                Status = RunStatus.Completed,
                RunKind = "root",
                PermissionMode = "default",
                StartedAt = now.AddMinutes(-3),
                CompletedAt = now,
                Summary = "Completed refactor"
            },
            [
                new EventEnvelope
                {
                    EventId = "evt-pack-1",
                    EventType = "context.packed",
                    RunId = runId.ToString(),
                    SessionId = sessionId.ToString(),
                    Timestamp = now.AddMinutes(-2),
                    Payload = new Dictionary<string, object?>
                    {
                        ["estimated_tokens"] = 8500,
                        ["token_budget"] = 10000,
                        ["sources"] = new[] { "system_prompt", "session_compaction" },
                        ["source_tokens"] = JsonSerializer.SerializeToElement(new[]
                        {
                            new { source = "system_prompt", tokens = 4500 },
                            new { source = "session_compaction", tokens = 4000 }
                        }),
                        ["dropped_sources"] = JsonSerializer.SerializeToElement(new[]
                        {
                            new { source = "session_history", tokens = 1200 }
                        })
                    }
                },
                new EventEnvelope
                {
                    EventId = "evt-tool-req-1",
                    EventType = "tool.execution.requested",
                    RunId = runId.ToString(),
                    SessionId = sessionId.ToString(),
                    Timestamp = now.AddMinutes(-1),
                    Payload = new Dictionary<string, object?>
                    {
                        ["execution_id"] = "exec-1",
                        ["tool_id"] = "edit_file",
                        ["arguments_summary"] = "path=src/a.cs",
                        ["sequence"] = 1L
                    }
                },
                new EventEnvelope
                {
                    EventId = "evt-tool-fail-1",
                    EventType = "tool.execution.failed",
                    RunId = runId.ToString(),
                    SessionId = sessionId.ToString(),
                    Timestamp = now.AddSeconds(-55),
                    Payload = new Dictionary<string, object?>
                    {
                        ["execution_id"] = "exec-1",
                        ["tool_id"] = "edit_file",
                        ["error_category"] = "fs_stale_version",
                        ["summary"] = "line_hash mismatch",
                        ["sequence"] = 2L
                    }
                }
            ]);

        var dossierService = new RunDossierService(fakeLifecycle);
        var dossier = await dossierService.BuildAsync(new RunDossierQuery(tenantId, workspaceId, sessionId, runId));

        Assert.NotNull(dossier);
        Assert.NotNull(dossier!.ContextHealth);
        Assert.Equal(85.0, dossier.ContextHealth!.PeakOccupancyPct);
        Assert.Equal(1200, dossier.ContextHealth.DroppedTokens);
        Assert.Equal(1, dossier.ContextHealth.PackCount);
        Assert.Equal(1, dossier.ContextHealth.CompactionCount);
        Assert.Contains(GovernanceSignalCodes.FsStaleVersion, dossier.ContextHealth.ActiveSignals);
        Assert.Contains(GovernanceSignalCodes.ContextPressure, dossier.ContextHealth.ActiveSignals);
    }

    [Fact]
    public async Task TinadecInspect_ContextAudit_AndRunDiagnostics_AreBoundedAndRedacted()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"tinadec-p1-inspect-{Guid.NewGuid():N}");
        var workspaceRoot = Path.Combine(tempRoot, "workspace");
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            const string sharedProseWithSecret =
                "Always read files before editing, validate revision numbers, and never expose api_key=superSecretEnv123 or sk-secretTokenValue99998888 in logs.";

            await File.WriteAllTextAsync(
                Path.Combine(workspaceRoot, "AGENTS.md"),
                $"# Workspace Conventions\n\n{sharedProseWithSecret}\n");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TinadecPersistence:Sqlite:DatabasePath"] = Path.Combine(tempRoot, "tinadec.db"),
                    ["TinadecPersistence:DataRoot"] = Path.Combine(tempRoot, "data"),
                    ["TinadecPersistence:SecretStore"] = "auto",
                    ["TinadecTools:DefaultWorkspaceRoot"] = workspaceRoot
                })
                .Build();

            var fakeLifecycle = new FakeLifecycleManager();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddLogging();
            services.AddTinadecPersistence(config, tempRoot);
            services.AddTinadecCore();
            services.AddSingleton<ILifecycleManager>(fakeLifecycle);

            await using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IStorageMigrationRunner>().RunAsync();

            var diOverlapAuditor = provider.GetRequiredService<IContextOverlapAuditor>();
            var diRunDiagnostics = provider.GetRequiredService<IRunContextDiagnosticsService>();
            Assert.NotNull(diOverlapAuditor);
            Assert.NotNull(diRunDiagnostics);

            var tenantCtx = provider.GetRequiredService<ITenantContextAccessor>().Current;
            var agentFactory = provider.GetRequiredService<IDbContextFactory<AgentConfigurationDbContext>>();
            await using (var agentDb = await agentFactory.CreateDbContextAsync())
            {
                agentDb.AgentDefinitions.Add(new AgentDefinitionRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantCtx.TenantId,
                    WorkspaceId = tenantCtx.WorkspaceId,
                    Slug = "custom-auditor-agent",
                    DisplayName = "Custom Auditor Agent",
                    Layer = "operation",
                    Role = "planner",
                    Status = "published",
                    Enabled = true,
                    SourceKind = "custom",
                    SourceKey = "custom-auditor-agent",
                    SystemPrompt = $"Coordinator instructions:\n{sharedProseWithSecret}\n",
                    CapabilitiesJson = "[]",
                    ToolScopeJson = "[]",
                    ModelStrategyJson = "{\"kind\":\"inherit\"}",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                await agentDb.SaveChangesAsync();
            }

            var sessionId = Guid.NewGuid();
            var runGuid = Guid.NewGuid();
            fakeLifecycle.SeedRun(
                new RunState
                {
                    RunId = runGuid.ToString(),
                    TenantId = tenantCtx.TenantId.ToString(),
                    WorkspaceId = tenantCtx.WorkspaceId.ToString(),
                    SessionId = sessionId.ToString(),
                    Status = RunStatus.Failed,
                    StartedAt = DateTimeOffset.UtcNow.AddSeconds(-15),
                    CompletedAt = DateTimeOffset.UtcNow
                },
                [
                    new EventEnvelope
                    {
                        EventId = "evt-pack-inspect",
                        EventType = "context.packed",
                        RunId = runGuid.ToString(),
                        SessionId = sessionId.ToString(),
                        Timestamp = DateTimeOffset.UtcNow.AddSeconds(-12),
                        Payload = new Dictionary<string, object?>
                        {
                            ["estimated_tokens"] = 7600,
                            ["token_budget"] = 10000,
                            ["sources"] = new[] { "system_prompt", "workspace_instructions" },
                            ["source_tokens"] = JsonSerializer.SerializeToElement(new[]
                            {
                                new { source = "system_prompt", tokens = 3600 },
                                new { source = "workspace_instructions", tokens = 4000 }
                            }),
                            ["dropped_sources"] = JsonSerializer.SerializeToElement(new[]
                            {
                                new { source = "reviewed_memory", tokens = 500 }
                            })
                        }
                    }
                ]);

            var lifecycleDbFactory = provider.GetRequiredService<IDbContextFactory<LifecycleDbContext>>();
            await using (var ldb = await lifecycleDbFactory.CreateDbContextAsync())
            {
                var t0 = DateTimeOffset.UtcNow.AddSeconds(-10);
                ldb.ModelInvocations.AddRange(
                    new ModelInvocationRecord
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantCtx.TenantId,
                        WorkspaceId = tenantCtx.WorkspaceId,
                        CallId = Guid.NewGuid(),
                        Attempt = 1,
                        SessionId = sessionId,
                        RunId = runGuid,
                        AgentDefinitionId = Guid.NewGuid(),
                        AgentVersionId = Guid.NewGuid(),
                        ModeVersionId = Guid.NewGuid(),
                        StrategySource = "route",
                        ProviderInstanceId = Guid.NewGuid(),
                        ProviderVersionId = Guid.NewGuid(),
                        Model = "gemini-2.5-flash",
                        Protocol = "openai",
                        Status = "succeeded",
                        InputTokens = 2000,
                        OutputTokens = 300,
                        TotalTokens = 2300,
                        StartedAt = t0,
                        CompletedAt = t0.AddMilliseconds(400)
                    },
                    new ModelInvocationRecord
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantCtx.TenantId,
                        WorkspaceId = tenantCtx.WorkspaceId,
                        CallId = Guid.NewGuid(),
                        Attempt = 1,
                        SessionId = sessionId,
                        RunId = runGuid,
                        AgentDefinitionId = Guid.NewGuid(),
                        AgentVersionId = Guid.NewGuid(),
                        ModeVersionId = Guid.NewGuid(),
                        StrategySource = "route",
                        ProviderInstanceId = Guid.NewGuid(),
                        ProviderVersionId = Guid.NewGuid(),
                        Model = "gemini-2.5-flash",
                        Protocol = "openai",
                        Status = "failed",
                        ErrorCategory = "timeout",
                        SafeErrorMessage = "Timed out with Authorization: Bearer topSecretBearer999",
                        InputTokens = 8200,
                        StartedAt = t0.AddSeconds(2),
                        CompletedAt = t0.AddSeconds(5)
                    });
                await ldb.SaveChangesAsync();
            }

            var gateway = provider.GetRequiredService<ITinadecConfigToolGateway>();
            var scope = new ToolInvocationScope(
                tenantCtx.TenantId,
                tenantCtx.WorkspaceId,
                Guid.Empty,
                CoreVirtualToolPolicy.ToWireSentinel(null),
                sessionId,
                runGuid,
                Guid.Empty,
                Guid.Empty,
                workspaceRoot,
                [CoreVirtualToolPolicy.TinadecInspectToolId],
                [],
                "hash",
                30,
                2,
                false);

            // 1. Verify tinadec_inspect(target = "context_audit")
            var auditResp = await gateway.ExecuteAsync(
                CoreVirtualToolPolicy.TinadecInspectToolId,
                scope,
                new ToolWireRequestDto
                {
                    ToolCallId = 1L,
                    ToolId = CoreVirtualToolPolicy.TinadecInspectToolId,
                    Params = JsonSerializer.SerializeToElement(new { target = "context_audit", limit = 10 })
                },
                CancellationToken.None);

            Assert.True(auditResp.IsSuccess, auditResp.Error);
            var auditJson = auditResp.Result!.Value.GetRawText();
            Assert.DoesNotContain("superSecretEnv123", auditJson, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-secretTokenValue99998888", auditJson, StringComparison.Ordinal);
            Assert.Equal("context_audit", auditResp.Result.Value.GetProperty("target").GetString());
            Assert.True(auditResp.Result.Value.GetProperty("pairs_total").GetInt32() >= 1);
            Assert.Equal(
                OverlapRemediationCodes.DedupeWorkspaceInstructions,
                auditResp.Result.Value.GetProperty("pairs")[0].GetProperty("remediation_code").GetString());

            // 2. Verify tinadec_inspect(target = "run_diagnostics", run_id = ...)
            var diagResp = await gateway.ExecuteAsync(
                CoreVirtualToolPolicy.TinadecInspectToolId,
                scope,
                new ToolWireRequestDto
                {
                    ToolCallId = 2L,
                    ToolId = CoreVirtualToolPolicy.TinadecInspectToolId,
                    Params = JsonSerializer.SerializeToElement(new { target = "run_diagnostics", run_id = runGuid.ToString() })
                },
                CancellationToken.None);

            Assert.True(diagResp.IsSuccess, diagResp.Error);
            var diagJson = diagResp.Result!.Value.GetRawText();
            Assert.DoesNotContain("topSecretBearer999", diagJson, StringComparison.Ordinal);
            Assert.Equal("run_diagnostics", diagResp.Result.Value.GetProperty("target").GetString());
            Assert.True(diagResp.Result.Value.GetProperty("window_buckets").GetProperty("degradation_detected").GetBoolean());
            Assert.Equal(20, diagResp.Result.Value.GetProperty("window_buckets").GetProperty("buckets").GetArrayLength());
            Assert.Contains(
                "reviewed_memory",
                diagResp.Result.Value.GetProperty("compaction").GetProperty("critical_dropped_sources").EnumerateArray().Select(x => x.GetString()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { /* ignore transient Windows handle */ }
            }
        }
    }

    [Fact]
    public void WinnowingAuditor_OneBasedLinesAndCjkParity_SmokeCheck()
    {
        var auditor = new ContextOverlapAuditor();
        const string cjkShared =
            "在多智能体并行开发时，任何模块不得直接引用兄弟业务模块程序集，所有跨模块调用必须经过抽象端口接口以通过架构门禁测试。并且长度超过六十四字符。";
        Assert.True(cjkShared.Length >= 64);

        var result = auditor.AuditSources(
        [
            new ContextAuditSourceInputDto { Id = "p1", Label = "Prompt", Kind = ContextAuditSourceKinds.SystemPrompt, Content = $"标题\n{cjkShared}" },
            new ContextAuditSourceInputDto { Id = "s1", Label = "Skill", Kind = ContextAuditSourceKinds.Skill, Content = $"# 技能\n\n{cjkShared}" }
        ]);

        var pair = Assert.Single(result.Pairs);
        Assert.Equal(OverlapRemediationCodes.MoveToSkillReferences, pair.RemediationCode);
        Assert.Equal("L2 <-> L3", pair.Matches[0].LineRangePair);
    }

    private sealed class FakeLifecycleManager : ILifecycleManager
    {
        private readonly Dictionary<string, RunState> _runs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, List<EventEnvelope>> _eventsBySession = [];

        public void SeedRun(RunState state, IReadOnlyList<EventEnvelope> events)
        {
            _runs[state.RunId] = state;
            if (Guid.TryParse(state.SessionId, out var sid))
            {
                _eventsBySession[sid] = [.. events];
            }
        }

        public Task<RunState> GetRunStateAsync(string runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_runs.TryGetValue(runId, out var r) ? r : new RunState { RunId = runId });

        public Task<IReadOnlyList<EventEnvelope>> ReplayEventsAsync(Guid? sessionId, long afterSequence, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EventEnvelope>>(
                sessionId.HasValue && _eventsBySession.TryGetValue(sessionId.Value, out var list) ? list : []);

        public Task<FrozenRunConfiguration?> GetFrozenRunConfigurationAsync(string runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<FrozenRunConfiguration?>(null);

        public Task<string> StartRunAsync(string sessionId, string? triggerMessageId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> StartRunAsync(RunStartRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunStartResult> StartOrGetRunAsync(RunStartRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunState?> FindRunByTriggerMessageAsync(string sessionId, string triggerMessageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteRunAsync(string runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetRunStatusAsync(string runId, string status, string? summary = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> CountActiveRunsAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AdvanceRunContextRevisionAsync(string runId, long contextRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<long> AppendEventAsync(Guid runId, string eventType, object? payload, string summary, string severity = "info", Guid? taskId = null, Guid? approvalId = null, string? toolId = null, CancellationToken cancellationToken = default, string? idempotencyKey = null) => throw new NotSupportedException();
        public Task<string> StartToolExecutionAsync(ToolExecutionStart start, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteToolExecutionAsync(string executionId, ToolExecutionCompletion completion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FailToolExecutionAsync(string executionId, string errorCategory, string safeMessage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunState>> ListNonTerminalRunsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunState>> ListActiveRunsAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunState>> ListLeaseEligibleRunsAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FrozenRunConfiguration> FreezeRunConfigurationAsync(string runId, FrozenRunConfigurationWrite configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunCheckpoint> SaveRunCheckpointAsync(string runId, RunCheckpointWrite checkpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunCheckpoint?> GetCurrentRunCheckpointAsync(string runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RunDirective>> ListPendingRunDirectivesAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunDirectiveDrainResult> DrainRunDirectivesAsync(Guid runId, IReadOnlyList<Guid> directiveIds, string drainedStatus, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RequeueRunDirectivesAsync(Guid fromRunId, IReadOnlyList<Guid> directiveIds, Guid toRunId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunDirective> EnqueueRunDirectiveAsync(RunDirectiveWrite write, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RunLease> TryAcquireRunLeaseAsync(string runId, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HeartbeatRunLeaseAsync(string runId, string ownerId, TimeSpan duration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseRunLeaseAsync(string runId, string ownerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTaskSnapshotAsync(Guid runId, object taskNode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DurableRunStreamChunk> AppendRunStreamAsync(string runId, DurableRunStreamAppend item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DurableRunStreamChunk>> ReplayRunStreamAsync(string runId, Guid? turnId, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
