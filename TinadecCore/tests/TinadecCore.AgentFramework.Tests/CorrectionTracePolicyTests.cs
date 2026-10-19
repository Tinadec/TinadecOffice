using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

public sealed class CorrectionTracePolicyTests
{
    private static readonly CorrectionRuntimePolicyDto Policy = new()
    {
        Kind = CorrectionRuntimePolicy.StructuredInvestigationKind,
        Version = CorrectionRuntimePolicy.StructuredInvestigationVersion,
        Features =
        [
            CorrectionRuntimePolicy.CausalTraceFeature,
            CorrectionRuntimePolicy.CompletionContractFeature,
            CorrectionRuntimePolicy.FailingReproRequiredFeature,
            CorrectionRuntimePolicy.ClarificationLedgerFeature,
            CorrectionRuntimePolicy.UserReproductionReopensFeature,
            CorrectionRuntimePolicy.LocalHunkRevertFeature,
        ],
    };

    [Fact]
    public void ProposalCannotManufactureEvidence()
    {
        var trace = CorrectionTracePolicy.Create(Run("run-1"), "修复恢复失败");
        using var proposal = JsonDocument.Parse("""
            {
              "phase": "OBSERVE",
              "reason": "先区分写入遗漏与恢复覆盖",
              "hypotheses": [
                {"id":"H1","statement":"恢复时覆盖字段","kill_criterion":"恢复前后字段一致","evidence_ids":["ev:forged"]}
              ]
            }
            """);

        Assert.Null(CorrectionTracePolicy.ApplyProposal(trace, proposal.RootElement));
        var hypothesis = Assert.Single(trace.Hypotheses);
        Assert.Empty(hypothesis.EvidenceIds);
        Assert.Empty(trace.Evidence);
        Assert.Equal("low", hypothesis.Confidence);
    }

    [Fact]
    public void OnlyOneBlockingQuestionCanRemainOpen()
    {
        var trace = CorrectionTracePolicy.Create(Run("run-question"), "需要用户澄清");
        using var first = JsonDocument.Parse("""
            {
              "open_questions": [
                {"id":"q1","question":"哪个项目？","blocking":true,"options":["A","B"]}
              ]
            }
            """);
        using var second = JsonDocument.Parse("""
            {
              "open_questions": [
                {"id":"q2","question":"哪个环境？","blocking":true,"options":["本地","生产"]}
              ]
            }
            """);

        Assert.Null(CorrectionTracePolicy.ApplyProposal(trace, first.RootElement));
        Assert.Equal(
            "Only one blocking correction question can be open at a time.",
            CorrectionTracePolicy.ApplyProposal(trace, second.RootElement));
        Assert.Equal("q1", Assert.Single(trace.OpenQuestions).Id);
    }

    [Fact]
    public void ClarificationLedgerMustBeEnabledByTheModePolicy()
    {
        var trace = CorrectionTracePolicy.Create(Run("run-policy"), "需要用户澄清");
        var causalOnly = new CorrectionRuntimePolicyDto
        {
            Kind = CorrectionRuntimePolicy.StructuredInvestigationKind,
            Version = CorrectionRuntimePolicy.StructuredInvestigationVersion,
            Features = [CorrectionRuntimePolicy.CausalTraceFeature],
        };
        using var proposal = JsonDocument.Parse("""
            {
              "open_questions": [
                {"id":"q1","question":"哪个项目？","blocking":true}
              ]
            }
            """);

        Assert.Equal(
            "This correction mode does not enable the clarification ledger.",
            CorrectionTracePolicy.ApplyProposal(trace, proposal.RootElement, causalOnly));
        Assert.Empty(trace.OpenQuestions);
    }

    [Fact]
    public void MutationInvalidatesVerificationAndRequiresSameFocusedRepro()
    {
        var trace = CorrectionTracePolicy.Create(Run("run-2"), "修复状态恢复，必须跑同一个 test");
        Fact(trace, "read_file", """{"path":"src/state.ts"}""", "read-1", """{"success":true}""", false);
        Fact(trace, "shell", """{"command":"npm test -- state-restore"}""", "test-fail", """{"exit_code":1}""", false);
        Fact(trace, "replace_lines", """{"path":"src/state.ts"}""", "edit-1", """{"success":true}""", true);
        Fact(trace, "shell", """{"command":"npm test -- state-restore"}""", "test-pass", """{"exit_code":0}""", false);

        Assert.Equal("src/state.ts", Assert.Single(trace.Completion.ModifiedArtifacts));
        Assert.True(trace.Completion.VerifiedArtifacts.Contains("src/state.ts"),
            $"verified=[{string.Join(",", trace.Completion.VerifiedArtifacts)}] modified=[{string.Join(",", trace.Completion.ModifiedArtifacts)}] repro={trace.Completion.Repro?.Command}");
        Assert.NotNull(trace.Completion.Repro);
        Assert.Equal("test-fail", trace.Completion.Repro!.FailedBeforeExecutionId);
        Assert.Equal("test-pass", trace.Completion.Repro.PassedAfterExecutionId);
        Assert.Null(CorrectionTracePolicy.EvaluateCompletion(trace, Policy, "修复状态恢复，必须跑同一个 test", true));
        Assert.Equal("ready", trace.Completion.Status);
        Assert.Equal("CONVERGE", trace.Phase);
    }

    [Fact]
    public void ParallelMergeKeepsTheNewerVerificationAndAllAuditFacts()
    {
        var owner = CorrectionTracePolicy.Create(Run("run-merge"), "修复状态恢复，必须跑同一个 test");
        Fact(owner, "read_file", """{"path":"src/state.ts"}""", "read-1", """{"success":true}""", false);
        Fact(owner, "shell", """{"command":"npm test"}""", "test-fail", """{"exit_code":1}""", false);
        Fact(owner, "replace_lines", """{"path":"src/state.ts"}""", "edit-1", """{"success":true}""", true);
        Assert.NotNull(CorrectionTracePolicy.EvaluateCompletion(owner, Policy, owner.Goal, true));

        var worker = JsonSerializer.Deserialize<CorrectionTraceState>(
            JsonSerializer.Serialize(owner))!;
        Fact(worker, "shell", """{"command":"npm test"}""", "test-pass", """{"exit_code":0}""", false);
        Assert.Null(CorrectionTracePolicy.EvaluateCompletion(worker, Policy, worker.Goal, true));

        var merged = CorrectionTracePolicy.Merge(owner, worker);

        Assert.Equal(owner.EpisodeId, merged.EpisodeId);
        Assert.Equal("ready", merged.Completion.Status);
        Assert.Equal("test-fail", merged.Completion.Repro!.FailedBeforeExecutionId);
        Assert.Equal("test-pass", merged.Completion.Repro.PassedAfterExecutionId);
        Assert.Contains("src/state.ts", merged.Completion.VerifiedArtifacts);
        Assert.Contains(merged.Evidence, item => item.ExecutionId == "test-fail");
        Assert.Contains(merged.Evidence, item => item.ExecutionId == "test-pass");
        Assert.Contains(merged.Evidence, item => item.ExecutionId == "edit-1");
    }

    [Fact]
    public void FocusedReproIgnoresOnlyKnownExitCodeCaptureSyntax()
    {
        var trace = CorrectionTracePolicy.Create(Run("run-normalize"), "修复状态恢复，必须跑同一个 test");
        Fact(trace, "shell", """{"command":"npm test 2>&1; echo \"EXIT_CODE=$?\""}""", "test-fail", """{"exit_code":1}""", false);
        Fact(trace, "replace_lines", """{"path":"src/state.ts"}""", "edit-1", """{"success":true}""", true);
        Fact(trace, "shell", """{"command":"npm test"}""", "test-pass", """{"exit_code":0}""", false);

        Assert.Null(CorrectionTracePolicy.EvaluateCompletion(trace, Policy, trace.Goal, true));
        Assert.Equal("ready", trace.Completion.Status);

        var different = CorrectionTracePolicy.Create(Run("run-normalize-different"), "修复状态恢复，必须跑同一个 test");
        Fact(different, "shell", """{"command":"npm test && echo done"}""", "test-fail-2", """{"exit_code":1}""", false);
        Fact(different, "replace_lines", """{"path":"src/state.ts"}""", "edit-2", """{"success":true}""", true);
        Fact(different, "shell", """{"command":"npm test"}""", "test-pass-2", """{"exit_code":0}""", false);

        Assert.NotNull(CorrectionTracePolicy.EvaluateCompletion(different, Policy, different.Goal, true));
        Assert.Equal("blocked", different.Completion.Status);
    }

    [Fact]
    public void DiagnosticsRequireEveryModifiedArtifact()
    {
        var trace = CorrectionTracePolicy.Create(Run("run-3"), "修复普通代码问题");
        Fact(trace, "replace_lines", """{"path":"src/a.ts"}""", "edit-a", """{"success":true}""", true);
        Fact(trace, "replace_lines", """{"path":"src/b.ts"}""", "edit-b", """{"success":true}""", true);
        Fact(trace, "shell", """{"command":"npm run lint","path":"src/a.ts"}""", "lint-a", """{"exit_code":0}""", false);

        Assert.NotNull(CorrectionTracePolicy.EvaluateCompletion(trace, Policy, "修复普通代码问题", true));
        Assert.Equal("blocked", trace.Completion.Status);
        Assert.Contains("src/b.ts", trace.Completion.ModifiedArtifacts);
        Assert.DoesNotContain("src/b.ts", trace.Completion.VerifiedArtifacts);
    }

    [Fact]
    public void UserAuthorityCannotBeSatisfiedByAMachinePass()
    {
        var trace = CorrectionTracePolicy.Create(Run("run-4"), "检查页面视觉恢复");
        trace.Evidence.Add(new CorrectionEvidenceState
        {
            Id = "ev:1",
            Source = "tool",
            Kind = "verification_passed",
            Strength = "strong",
            Summary = "build passed",
        });

        Assert.NotNull(CorrectionTracePolicy.EvaluateCompletion(trace, Policy, "检查页面视觉恢复", false));
        Assert.Equal("blocked", trace.Completion.Status);
        CorrectionTracePolicy.MarkUserResolved(trace, "页面已经正常");
        Assert.Equal("resolved", trace.Completion.Status);
        Assert.Equal("user", trace.Completion.Authority);
    }

    [Fact]
    public void ReopenInvalidatesOldVerificationAndStartsBacktracking()
    {
        var source = CorrectionTracePolicy.Create(Run("run-5"), "修复旧问题");
        source.Completion.ModifiedArtifacts.Add("src/a.ts");
        source.Completion.VerifiedArtifacts.Add("src/a.ts");
        source.Completion.Status = "ready";

        var reopened = CorrectionTracePolicy.Clone(source, Run("run-6"));
        CorrectionTracePolicy.Reopen(reopened, "原症状仍复现");

        Assert.Equal("BACKTRACK", reopened.Phase);
        Assert.Equal("reopened", reopened.Completion.Status);
        Assert.Empty(reopened.Completion.VerifiedArtifacts);
        Assert.Equal(source.EpisodeId, reopened.EpisodeId);
        Assert.Equal(source.RunId.ToString(), reopened.ParentRunId?.ToString());
    }

    private static RunState Run(string id) => new()
    {
        RunId = Guid.TryParse(id, out var guid) ? id : Guid.NewGuid().ToString(),
        SessionId = Guid.NewGuid().ToString(),
        Status = RunStatus.Executing,
    };

    private static void Fact(
        CorrectionTraceState trace,
        string toolId,
        string arguments,
        string executionId,
        string resultJson,
        bool mutates)
    {
        using var result = JsonDocument.Parse(resultJson);
        CorrectionTracePolicy.RecordToolFact(trace, toolId, arguments, new ToolDispatchResultDto
        {
            Status = "completed",
            ExecutionId = executionId,
            Result = result.RootElement.Clone(),
        }, mutates, "main");
    }
}
