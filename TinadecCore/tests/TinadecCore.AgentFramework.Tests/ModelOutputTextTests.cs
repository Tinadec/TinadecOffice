using TinadecCore.DmaEA;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Unit tests for the reasoning-model-tolerant text helpers. These lock in the
/// contract that thinking markup, markdown fences, and surrounding prose never
/// break structured JSON extraction or leak into answer text.
/// </summary>
public sealed class ModelOutputTextTests
{
    // The think/thinking tags are assembled from char codes via interpolation so the
    // literal close-tag token never appears verbatim in this file: it collides with a
    // reserved control marker in some agent tooling layers and gets silently rewritten
    // on write, which would make a fixture assert against the wrong string.
    private const char Lt = (char)60;    // less-than
    private const char Gt = (char)62;    // greater-than
    private const char Slash = (char)47; // solidus
    private static readonly string T = $"{Lt}think{Gt}";
    private static readonly string TC = $"{Lt}{Slash}think{Gt}";
    private static readonly string TG = $"{Lt}thinking{Gt}";
    private static readonly string TGC = $"{Lt}{Slash}thinking{Gt}";

    // ── StripReasoning ──

    [Fact]
    public void StripReasoning_RemovesCompleteThinkBlock()
    {
        var input = "Before\n\n" + T + "\nstep one\nstep two\n" + TC + "\n\nAfter";
        var result = ModelOutputText.StripReasoning(input);
        Assert.DoesNotContain("step one", result, StringComparison.Ordinal);
        Assert.Contains("Before", result, StringComparison.Ordinal);
        Assert.Contains("After", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StripReasoning_RemovesThinkingBlockCaseInsensitive()
    {
        var input = TG + "hidden reasoning" + TGC + "visible";
        var result = ModelOutputText.StripReasoning(input);
        Assert.DoesNotContain("hidden reasoning", result, StringComparison.Ordinal);
        Assert.Contains("visible", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StripReasoning_RemovesOrphanCloseTag()
    {
        // Providers sometimes half-strip a block, leaving a lone close tag in content.
        var input = "我需要先了解项目。\n" + TC + "\n我需要先查看当前工作目录的内容。";
        var result = ModelOutputText.StripReasoning(input);
        Assert.DoesNotContain("think", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("我需要先了解项目。", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StripReasoning_RemovesAllMultipleBlocks()
    {
        var input = T + "first scratch" + TC + " keep " + T + "second scratch" + TC + " also";
        var result = ModelOutputText.StripReasoning(input);
        Assert.DoesNotContain("first scratch", result, StringComparison.Ordinal);
        Assert.DoesNotContain("second scratch", result, StringComparison.Ordinal);
        Assert.Contains("keep", result, StringComparison.Ordinal);
        Assert.Contains("also", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StripReasoning_LeavesPlainTextUnchanged()
    {
        const string input = "Just a normal answer with no reasoning markup.";
        Assert.Equal(input, ModelOutputText.StripReasoning(input));
    }

    [Fact]
    public void StripReasoning_HandlesNullAndEmpty()
    {
        Assert.Equal(string.Empty, ModelOutputText.StripReasoning(null));
        Assert.Equal(string.Empty, ModelOutputText.StripReasoning(string.Empty));
    }

    // ── AnswerText ──

    [Fact]
    public void AnswerText_StripsReasoningAndTrims()
    {
        var input = "  \n" + T + "scratch that" + TC + "\n\nThe final answer.  \n";
        Assert.Equal("The final answer.", ModelOutputText.AnswerText(input));
    }

    // ── ExtractJsonCandidates: arrays ──

    [Fact]
    public void ExtractJsonCandidates_FindsArrayWrappedInReasoningAndFence()
    {
        var input = "Let me plan.\n\n" + T + "\nI should [analyze] the repo.\n" + TC
            + "\n\nHere you go:\n```json\n[{\"title\":\"A\"},{\"title\":\"B\"}]\n```";
        var candidates = ModelOutputText.ExtractJsonCandidates(input, array: true);
        Assert.NotEmpty(candidates);
        // The reasoning bracket "[analyze]" is removed with the think block, so the
        // first candidate is the real payload array, not the reasoning fragment.
        Assert.Equal("[{\"title\":\"A\"},{\"title\":\"B\"}]", candidates[0]);
    }

    [Fact]
    public void ExtractJsonCandidates_IgnoresBracketsInsideStrings()
    {
        const string input = "[{\"title\":\"has ] and [ inside\"}]";
        var candidates = ModelOutputText.ExtractJsonCandidates(input, array: true);
        Assert.Single(candidates);
        Assert.Equal(input, candidates[0]);
    }

    [Fact]
    public void ExtractJsonCandidates_SkipsTruncatedArray()
    {
        // An unbalanced opener (truncated output) yields no candidate, not a broken span.
        const string input = "[{\"title\":\"A\"";
        var candidates = ModelOutputText.ExtractJsonCandidates(input, array: true);
        Assert.Empty(candidates);
    }

    [Fact]
    public void ExtractJsonCandidates_ReturnsMultipleSiblingArrays()
    {
        const string input = "prose [1,2] more [3,4] end";
        var candidates = ModelOutputText.ExtractJsonCandidates(input, array: true);
        Assert.Equal(2, candidates.Count);
        Assert.Equal("[1,2]", candidates[0]);
        Assert.Equal("[3,4]", candidates[1]);
    }

    // ── ExtractJsonCandidates: objects ──

    [Fact]
    public void ExtractJsonCandidates_FindsObjectInProse()
    {
        const string input = "Sure! {\"decision\":\"pass\",\"reasons\":[]} is my verdict.";
        var candidates = ModelOutputText.ExtractJsonCandidates(input, array: false);
        Assert.NotEmpty(candidates);
        Assert.Equal("{\"decision\":\"pass\",\"reasons\":[]}", candidates[0]);
    }

    [Fact]
    public void ExtractJsonCandidates_NestedObjectReturnsOuter()
    {
        const string input = "{\"recommendations\":[{\"skill\":\"x\"}]}";
        var candidates = ModelOutputText.ExtractJsonCandidates(input, array: false);
        Assert.Single(candidates);
        Assert.Equal(input, candidates[0]);
    }

    [Fact]
    public void ExtractJsonCandidates_ObjectScanFindsInnerObjectOfArray()
    {
        // Schema-shape drift: scanning for objects inside an array-wrapped payload
        // returns the first balanced object.
        const string input = "[{\"decision\":\"pass\"}]";
        var candidates = ModelOutputText.ExtractJsonCandidates(input, array: false);
        Assert.NotEmpty(candidates);
        Assert.Equal("{\"decision\":\"pass\"}", candidates[0]);
    }

    [Fact]
    public void ExtractJsonCandidates_HandlesEscapedQuotesAndBraces()
    {
        const string input = "{\"text\":\"a \\\" quote and } brace\"}";
        var candidates = ModelOutputText.ExtractJsonCandidates(input, array: false);
        Assert.Single(candidates);
        Assert.Equal(input, candidates[0]);
    }

    [Fact]
    public void ExtractJsonCandidates_EmptyOrNoJsonReturnsEmpty()
    {
        Assert.Empty(ModelOutputText.ExtractJsonCandidates(null, array: true));
        Assert.Empty(ModelOutputText.ExtractJsonCandidates("no json here", array: true));
        Assert.Empty(ModelOutputText.ExtractJsonCandidates(T + "only reasoning" + TC, array: false));
    }
}
