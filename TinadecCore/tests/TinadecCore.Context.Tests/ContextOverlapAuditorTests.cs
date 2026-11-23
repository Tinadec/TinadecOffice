using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Context;
using TinadecCore.Contracts.Dtos;

namespace TinadecCore.Context.Tests;

public sealed class ContextOverlapAuditorTests
{
    private const string SharedRule =
        "Always read the exact target file with read_file before editing, verify line hashes against stale versions, and run scoped unit tests prior to completion.";

    [Fact]
    public void WinnowFingerprints_MatchesDualPrimeRabinKarp_AndRightmostTieBreaking()
    {
        var normalized = SharedRule.ToLowerInvariant();
        var fps1 = ContextOverlapAuditor.WinnowFingerprints(normalized, 64, 16);
        var fps2 = ContextOverlapAuditor.WinnowFingerprints(normalized, 64, 16);

        Assert.NotEmpty(fps1);
        Assert.Equal(fps1, fps2);

        // Identical k-grams across a uniform string must pick the rightmost index in each window.
        var uniform = new string('a', 80); // hashCount = 80 - 64 + 1 = 17, window = 16
        var uniformFps = ContextOverlapAuditor.WinnowFingerprints(uniform, 64, 16);
        Assert.Equal(2, uniformFps.Count);
        Assert.Equal(15, uniformFps[0].Pos);
        Assert.Equal(16, uniformFps[1].Pos);
        Assert.Equal(uniformFps[0].Hash, uniformFps[1].Hash);
    }

    [Fact]
    public void AuditSources_Detects64CharOverlap_ExactOneBasedLineRanges_AndMoveToSkillRemediation()
    {
        var auditor = new ContextOverlapAuditor();
        var sources = new[]
        {
            new ContextAuditSourceInputDto
            {
                Id = "sys-1",
                Label = "Coordinator System Prompt",
                Kind = ContextAuditSourceKinds.SystemPrompt,
                Content = string.Join('\n',
                    "You are the primary coordinator.",
                    "",
                    "Core execution rule:",
                    SharedRule,
                    "Keep replies concise.")
            },
            new ContextAuditSourceInputDto
            {
                Id = "skill-1",
                Label = "safe-code-edit",
                Kind = ContextAuditSourceKinds.Skill,
                Content = string.Join('\n',
                    "# Safe Code Edit Skill",
                    $"  {SharedRule.ToUpperInvariant()}  ",
                    "Extra checklist items unique to this skill.")
            },
            new ContextAuditSourceInputDto
            {
                Id = "agents-md",
                Label = "AGENTS.md",
                Kind = ContextAuditSourceKinds.WorkspaceInstructions,
                Content = string.Join('\n',
                    "Repository layout overview.",
                    "Build with bun run build.",
                    "No overlapping boilerplate here at all.")
            }
        };

        var result = auditor.AuditSources(sources, new WinnowingAuditOptions(KGramSize: 64, WindowSize: 16));

        Assert.Equal(64, result.KGramSize);
        Assert.Equal(16, result.WindowSize);
        Assert.Equal(3, result.SourceCount);
        Assert.Single(result.Pairs);

        var pair = result.Pairs[0];
        Assert.Equal("sys-1", pair.SourceAId);
        Assert.Equal("skill-1", pair.SourceBId);
        Assert.True(pair.SharedChars >= SharedRule.Length - 2);
        Assert.True(pair.OverlapPercent > 40.0);
        Assert.Equal(OverlapRemediationCodes.MoveToSkillReferences, pair.RemediationCode);
        Assert.Contains("SKILL.md", pair.RemediationSummary, StringComparison.Ordinal);

        var match = Assert.Single(pair.Matches);
        Assert.Equal(4, match.SourceALines.Start);
        Assert.Equal(4, match.SourceALines.End);
        Assert.Equal(2, match.SourceBLines.Start);
        Assert.Equal(2, match.SourceBLines.End);
        Assert.Equal("L4 <-> L2", match.LineRangePair);
    }

    [Fact]
    public void AuditSources_IgnoresSub64CharPassages_AndCoversAllFiveRemediationCodes()
    {
        var auditor = new ContextOverlapAuditor();
        const string shortShared = "Always read files before editing them carefully.";
        Assert.True(shortShared.Length < 64);

        var clean = auditor.AuditSources(
        [
            new ContextAuditSourceInputDto { Id = "a", Label = "A", Kind = ContextAuditSourceKinds.SystemPrompt, Content = $"Header line.\n{shortShared}\nUnique A tail." },
            new ContextAuditSourceInputDto { Id = "b", Label = "B", Kind = ContextAuditSourceKinds.Skill, Content = $"Skill title.\n{shortShared}\nUnique B tail." }
        ]);
        Assert.Empty(clean.Pairs);
        Assert.Equal(0, clean.DuplicateEstimatedTokens);

        const string longPassage =
            "Never modify files outside your assigned write_scope without coordinator approval and always run targeted unit tests before completion.";

        // 1. dedupe_workspace_instructions
        var wsAudit = auditor.AuditSources(
        [
            new ContextAuditSourceInputDto { Id = "ws", Label = "AGENTS.md", Kind = ContextAuditSourceKinds.WorkspaceInstructions, Content = $"Line 1\nLine 2\n{longPassage}" },
            new ContextAuditSourceInputDto { Id = "frag", Label = "global-rules", Kind = ContextAuditSourceKinds.PromptFragment, Content = $"{longPassage}\nExtra line." }
        ]);
        Assert.Equal(OverlapRemediationCodes.DedupeWorkspaceInstructions, Assert.Single(wsAudit.Pairs).RemediationCode);
        Assert.Equal("L3 <-> L1", wsAudit.Pairs[0].Matches[0].LineRangePair);

        // 2. dedupe_tool_schema_prose
        Assert.Equal(
            OverlapRemediationCodes.DedupeToolSchemaProse,
            ContextOverlapAuditor.ResolveOverlapRemediation(ContextAuditSourceKinds.ToolSchema, ContextAuditSourceKinds.SystemPrompt).Code);

        // 3. consolidate_shared_skill
        Assert.Equal(
            OverlapRemediationCodes.ConsolidateSharedSkill,
            ContextOverlapAuditor.ResolveOverlapRemediation(ContextAuditSourceKinds.Skill, ContextAuditSourceKinds.Skill).Code);

        // 4. extract_shared_fragment
        Assert.Equal(
            OverlapRemediationCodes.ExtractSharedFragment,
            ContextOverlapAuditor.ResolveOverlapRemediation(ContextAuditSourceKinds.SystemPrompt, ContextAuditSourceKinds.PipelineTemplate).Code);
    }

    [Fact]
    public void AuditSources_HandlesCjkTokenEstimation_MultiLineRanges_AndIntervalUnionDeduplication()
    {
        var cjkParagraph =
            "在执行任何文件写入或代码重构之前，智能体必须先通过只读文件工具完整核对目标行号与内容哈希，严禁越过工作区隔离边界修改其他并行队友持有的模块文件，并在提交前运行定向单元测试验证。";
        Assert.True(cjkParagraph.Length >= 64);

        var cjkTokens = ContextOverlapAuditor.EstimateTokens(cjkParagraph);
        var latinSameLength = new string('x', cjkParagraph.Length);
        var latinTokens = ContextOverlapAuditor.EstimateTokens(latinSameLength);
        // CJK (~1.5 chars/token) must estimate meaningfully higher token density than Latin (~4 chars/token).
        Assert.True(cjkTokens > latinTokens * 2);

        var auditor = new ContextOverlapAuditor();
        var multiLineA = string.Join('\n',
            "# 系统角色说明",
            "简介行",
            cjkParagraph[..42],
            cjkParagraph[42..],
            "尾部独立说明");
        var multiLineB = string.Join('\n',
            "---",
            cjkParagraph[..42],
            cjkParagraph[42..],
            "其他技能段落");

        // Add a third source sharing the same normalized CJK text to verify union deduplication never exceeds 100%.
        var multiLineC = $"{cjkParagraph[..42]} {cjkParagraph[42..]}";

        var audit = auditor.AuditSources(
        [
            new ContextAuditSourceInputDto { Id = "cjk-a", Label = "系统提示词", Kind = ContextAuditSourceKinds.SystemPrompt, Content = multiLineA },
            new ContextAuditSourceInputDto { Id = "cjk-b", Label = "技能规范", Kind = ContextAuditSourceKinds.Skill, Content = multiLineB },
            new ContextAuditSourceInputDto { Id = "cjk-c", Label = "流水线模板", Kind = ContextAuditSourceKinds.PipelineTemplate, Content = multiLineC }
        ]);

        Assert.Equal(3, audit.Pairs.Count);
        Assert.InRange(audit.OverallOverlapPercent, 1.0, 100.0);

        var pairAb = Assert.Single(audit.Pairs, p => p.SourceAId == "cjk-a" && p.SourceBId == "cjk-b");
        Assert.Equal("L3-L4 <-> L2-L3", pairAb.Matches[0].LineRangePair);
    }

    [Fact]
    public async Task AuditAsync_GathersWorkspaceInstructionsAndSkills_EnforcesSandbox_AndRedactsSecrets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"tinadec-overlap-test-{Guid.NewGuid():N}");
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"tinadec-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(outsideRoot);

        try
        {
            const string duplicatedPolicy =
                "Always validate optimistic concurrency revisions before mutating mode topologies, never commit build artifacts, and redact sk-liveSecretToken1234567890 from diagnostics.";

            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "AGENTS.md"),
                $"# Repo Rules\n\n{duplicatedPolicy}\n");

            var apmDir = Path.Combine(tempRoot, ".apm", "instructions");
            Directory.CreateDirectory(apmDir);
            await File.WriteAllTextAsync(
                Path.Combine(apmDir, "governance.instructions.md"),
                $"# APM Governance\n{duplicatedPolicy}\n");

            var skillDir = Path.Combine(tempRoot, ".tinadec", "skills", "topology-check");
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(
                Path.Combine(skillDir, "SKILL.md"),
                $"---\nname: topology-check\ndescription: Validates mode topology edits.\n---\n\n{duplicatedPolicy}\n");

            var services = new ServiceCollection();
            services.AddContextOverlapAuditor();
            using var provider = services.BuildServiceProvider();
            var auditor = provider.GetRequiredService<IContextOverlapAuditor>();

            var scope = new ContextAuditExecutionScope(
                Guid.NewGuid(),
                Guid.NewGuid(),
                AuthorizedWorkspaceRoot: tempRoot);

            var audit = await auditor.AuditAsync(new ContextOverlapAuditRequestDto(), scope);

            Assert.Equal("static_config", audit.Scope);
            Assert.True(audit.SourceCount >= 3);
            Assert.NotEmpty(audit.Pairs);
            Assert.Contains(audit.Sources, s => s.Path == "AGENTS.md");
            Assert.Contains(audit.Sources, s => s.Path == ".apm/instructions/governance.instructions.md");
            Assert.Contains(audit.Sources, s => s.Kind == ContextAuditSourceKinds.Skill);

            // Excerpts must redact embedded secret tokens.
            foreach (var match in audit.Matches)
            {
                Assert.DoesNotContain("sk-liveSecretToken1234567890", match.Excerpt, StringComparison.Ordinal);
            }

            // Reject workspace_root escaping the authorized sandbox.
            await Assert.ThrowsAsync<ArgumentException>(() =>
                auditor.AuditAsync(new ContextOverlapAuditRequestDto { WorkspaceRoot = outsideRoot }, scope));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, recursive: true);
        }
    }
}
