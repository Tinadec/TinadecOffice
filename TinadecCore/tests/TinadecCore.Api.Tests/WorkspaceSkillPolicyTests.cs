using TinadecCore.Abstractions.Ports;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The discovery rules in isolation, so a rejected skill is attributed to the rule that rejected it
/// rather than to the filesystem walk around it. The names and limits are the Agent Skills format's,
/// and the reasons are what the model-facing index prints — so these strings are part of the contract
/// with whoever wrote the file, not debug output.
/// </summary>
public sealed class WorkspaceSkillPolicyTests
{
    private const string Path_ = "skills/probe/SKILL.md";

    [Theory]
    // The shape the format asks for.
    [InlineData("probe", "Summarise the changelog.", true, null)]
    // Names: lowercase alphanumerics joined by single hyphens, no leading or trailing hyphen.
    [InlineData("Probe", "Fine.", false, "name must be lowercase letters")]
    [InlineData("release notes", "Fine.", false, "name must be lowercase letters")]
    [InlineData("-probe", "Fine.", false, "name must be lowercase letters")]
    [InlineData("probe-", "Fine.", false, "name must be lowercase letters")]
    [InlineData("pro--be", "Fine.", false, "name must be lowercase letters")]
    [InlineData("", "Fine.", false, "frontmatter has no 'name'")]
    [InlineData("a-renamed-skill-that-is-longer-than-the-format-allows-and-so-must-be-refused-because-sixty-four", "Fine.", false, "name is longer than 64 characters")]
    // Descriptions: required, capped, and the only evidence the model gets before it opens anything.
    [InlineData("probe", "", false, "frontmatter has no 'description'")]
    [InlineData("probe", "Long description.", true, null)]
    public void FrontmatterIsAcceptedOnlyWhenItStatesBothFieldsTheWayTheFormatSpellsThem(
        string name, string description, bool expected, string? expectedReasonStart)
    {
        var content = $"---\nname: {name}\ndescription: {description}\n---\n\nBody.";

        var ok = WorkspaceSkillPolicy.TryRead(content, "probe", Path_, out var skill, out var reason);

        Assert.Equal(expected, ok);
        if (expected)
        {
            Assert.NotNull(skill);
            Assert.Equal(name, skill!.Name);
            Assert.Equal(description, skill.Description);
            Assert.Equal(Path_, skill.RelativePath);
        }
        else
        {
            Assert.StartsWith(expectedReasonStart!, reason, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The off switch is a line in the file, so it has to be read the way a person writes one: writing
    /// the key at all is the intent, and only an explicit negative re-enables. This matters because the
    /// alternative reading — "the value must say true" — makes a typo like `disabled: tru` silently
    /// advertise a skill its author turned off.
    /// </summary>
    [Theory]
    [InlineData("disabled: true", false)]
    [InlineData("disabled:", false)]
    [InlineData("DISABLED: True", false)]
    [InlineData("disabled: 1", false)]
    [InlineData("disabled: tru", false)]
    [InlineData("disabled: the changelog format changed under it", false)]
    [InlineData("disabled: false", true)]
    [InlineData("disabled: no", true)]
    [InlineData("disabled: OFF", true)]
    [InlineData("disabled: 0", true)]
    public void TheDisabledKeyIsWhatTurnsAWellFormedSkillOff(string frontmatterLine, bool expectedAdvertised)
    {
        var content = $"---\nname: probe\ndescription: Summarise the changelog.\n{frontmatterLine}\n---\n\nBody.";

        var ok = WorkspaceSkillPolicy.TryRead(content, "probe", Path_, out var skill, out var reason);

        Assert.Equal(expectedAdvertised, ok);
        if (expectedAdvertised)
        {
            Assert.Equal("probe", skill!.Name);
            Assert.Equal(string.Empty, reason);
        }
        else
        {
            Assert.Null(skill);
            Assert.Contains("'disabled:'", reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASwitchedOffSkillSaysItIsOffRatherThanLookingBrokenOrAbsent()
    {
        var content = "---\nname: probe\ndescription: Summarise the changelog.\ndisabled: true\n---\n\nBody.";
        Assert.False(WorkspaceSkillPolicy.TryRead(content, "probe", Path_, out _, out var reason));

        var index = WorkspaceSkillPolicy.Frame([], [new WorkspaceSkillPolicy.Refusal(Path_, reason)], 4_000, 0);

        // Two different facts the author must not have to guess between: the file is fine and switched
        // off, versus the file is wrong. The wording carries it, and the path is named either way.
        Assert.Contains(Path_, index);
        Assert.Contains("marked off by its own 'disabled:' frontmatter", index);
        Assert.DoesNotContain("no YAML frontmatter", index);
    }

    [Fact]
    public void DescriptionLongerThanTheFormatAllowsIsRefusedRatherThanCut()
    {
        // A 1024-character description is already an essay in the index. Trimming it here would put
        // words in the author's mouth; refusing names the file and the rule instead.
        var long_ = new string('x', WorkspaceSkillPolicy.MaxDescriptionLength + 1);

        var ok = WorkspaceSkillPolicy.TryRead(
            $"---\nname: probe\ndescription: {long_}\n---\n", "probe", Path_, out _, out var reason);

        Assert.False(ok);
        Assert.Equal($"description is longer than {WorkspaceSkillPolicy.MaxDescriptionLength} characters", reason);
    }

    [Fact]
    public void FrontmatterWithoutItsClosingDelimiterIsNotFrontmatter()
    {
        var ok = WorkspaceSkillPolicy.TryRead(
            "---\nname: probe\ndescription: Opening fence only, never closed.\n", "probe", Path_, out _, out var reason);

        Assert.False(ok);
        Assert.Contains("no YAML frontmatter block", reason);
    }

    [Fact]
    public void KeysAreMatchedCaseInsensitivelyAndAQuotedValueKeepsItsText()
    {
        // Editors and hand-written files disagree about key case and quoting; neither should be able to
        // make a well-described skill invisible.
        var ok = WorkspaceSkillPolicy.TryRead(
            "---\nName: probe\nDESCRIPTION: \"Quoted, with a colon: inside.\"\n---\n",
            "probe", Path_, out var skill, out _);

        Assert.True(ok);
        Assert.Equal("Quoted, with a colon: inside.", skill!.Description);
    }

    [Fact]
    public void TheIndexCountsWhatItLeftOutEvenWhenTheCeilingWasTheRunBudget()
    {
        var skills = Enumerable.Range(0, 6)
            .Select(i => new WorkspaceSkillPolicy.Skill($"skill-{i}", $"Description {i}.", $"skills/skill-{i}/SKILL.md"))
            .ToArray();

        // Room for some rows, not all: the sentence saying how many are missing is what keeps a
        // truncated index from reading like the complete list.
        var index = WorkspaceSkillPolicy.Frame(skills, [], 620, omittedSkills: 2);

        Assert.NotNull(index);
        var rendered = index!.Split('\n').Count(line => line.StartsWith("- skill-", StringComparison.Ordinal));
        Assert.InRange(rendered, 1, 5);
        Assert.Contains("skill-0", index);
        Assert.Contains($"{6 - rendered + 2} more skills are not listed here", index);
        Assert.DoesNotContain("skill-5", index);
    }

    [Fact]
    public void AnIndexThatCanNameNothingIsSilenceRatherThanAPromise()
    {
        var skills = new[] { new WorkspaceSkillPolicy.Skill("probe", "One row.", Path_) };

        Assert.Null(WorkspaceSkillPolicy.Frame(skills, [], 60, 0));
    }

    [Fact]
    public void RefusalsAreReportedEvenWhenNoSkillWasAdvertised()
    {
        // The author of the only skill in the repository is the person most likely to read this, and
        // an empty index would tell them nothing happened at all.
        var index = WorkspaceSkillPolicy.Frame(
            [],
            [new WorkspaceSkillPolicy.Refusal(Path_, "frontmatter has no 'description'")],
            4_000,
            0);

        Assert.NotNull(index);
        Assert.Contains(Path_, index);
        Assert.Contains("frontmatter has no 'description'", index);
    }

    [Fact]
    public void RefusalLinesAreCappedButTheCountOfTheRestIsStillStated()
    {
        var refusals = Enumerable.Range(0, WorkspaceSkillPolicy.MaxRefusalLines + 3)
            .Select(i => new WorkspaceSkillPolicy.Refusal($"skills/s{i}/SKILL.md", "no frontmatter"))
            .ToArray();

        var index = WorkspaceSkillPolicy.Frame([], refusals, 4_000, 0);

        Assert.NotNull(index);
        Assert.Contains("skills/s0/SKILL.md", index);
        Assert.Contains("and 3 more with the same problem.", index);
        Assert.DoesNotContain("skills/s7/SKILL.md", index);
    }

    [Fact]
    public void ThePathANameProducesIsThePathTheLoaderWouldReadItFrom()
    {
        // The market composes a skill's remote address and its local target from this one rule, so
        // the pair has to agree with what TryRead accepts: if it ever drifted, an install would
        // "succeed" into a directory the workspace never advertises, and the reviewer would have
        // approved a file that does nothing.
        var relative = WorkspaceSkillPolicy.RelativePathFor("pdf-forms");
        Assert.Equal("skills/pdf-forms/SKILL.md", relative);

        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tinadec-skill-path"));
        Assert.Equal(Path.Combine(root, "skills", "pdf-forms", "SKILL.md"),
            WorkspaceSkillPolicy.AbsolutePathFor(root, "pdf-forms"));

        var readable = WorkspaceSkillPolicy.TryRead(
            "---\nname: pdf-forms\ndescription: Fills forms.\n---\n", "pdf-forms", relative,
            out var skill, out var reason);

        Assert.True(readable, reason);
        Assert.Equal("pdf-forms", skill!.Name);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(12_000, WorkspaceSkillPolicy.MaxIndexChars)]
    [InlineData(2_000, 2_000)]
    public void BudgetConvertsToCharactersOnTheSameRateTheInstructionCeilingUses(int budget, int expected) =>
        Assert.Equal(expected, WorkspaceSkillPolicy.InlineCharLimit(budget));
}
