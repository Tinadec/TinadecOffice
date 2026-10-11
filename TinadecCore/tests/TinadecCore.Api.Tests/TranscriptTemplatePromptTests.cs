using TinadecCore.Prompts;

namespace TinadecCore.Api.Tests;

/// <summary>
/// The conversation template is not a display preference. "Explain more" is an instruction to
/// the model, and no amount of desktop rendering makes a model explain itself — so the rule
/// worth pinning is what reaches the prompt, and what deliberately does not.
/// </summary>
public sealed class TranscriptTemplatePromptTests
{
    [Fact]
    public void Concise_AddsNoInstruction()
    {
        // Brevity is not worth spending context on: the baseline already asks for an answer, so
        // there is nothing to add. An "instruction" here would cost tokens to say nothing.
        Assert.Null(PromptAssembler.ExplanationInstruction("concise"));
    }

    [Fact]
    public void AnUnsetTemplateChangesNothing()
    {
        Assert.Null(PromptAssembler.ExplanationInstruction(null));
        Assert.Null(PromptAssembler.ExplanationInstruction("  "));
        // Sessions that predate the setting read as standard.
        Assert.Null(PromptAssembler.ExplanationInstruction("standard"));
    }

    [Fact]
    public void Detailed_AsksForTheReasoningNotJustTheResult()
    {
        var instruction = PromptAssembler.ExplanationInstruction("detailed");

        Assert.NotNull(instruction);
        Assert.Contains("detailed", instruction, StringComparison.OrdinalIgnoreCase);
        // The point of the setting: a reader who was not watching should still follow it.
        Assert.Contains("why", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("assum", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("evidence", instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DETAILED")]
    [InlineData(" Detailed ")]
    public void TheTemplateIsNormalizedBeforeItIsRead(string template)
    {
        Assert.NotNull(PromptAssembler.ExplanationInstruction(template));
    }

    [Fact]
    public void Information_AsksForAViewAndStatesItsConstraints()
    {
        var instruction = PromptAssembler.ExplanationInstruction("information");

        Assert.NotNull(instruction);
        Assert.Contains("widget_html", instruction, StringComparison.Ordinal);
        // The frame is sandboxed, so the instruction has to say what that forbids — a widget that
        // reaches for the network or nests a frame simply will not work, and saying so here is
        // cheaper than a broken view the reader has to diagnose.
        Assert.Contains("sandboxed", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("network", instruction, StringComparison.OrdinalIgnoreCase);
        // And it stays a preference: rendering everything as HTML is not the goal.
        Assert.Contains("prose", instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownTemplateAddsNothingRatherThanGuessing()
    {
        // The session validator rejects unknown values before a run is admitted; reaching here
        // with one means an old or hand-edited row, and silence is the safe reading.
        Assert.Null(PromptAssembler.ExplanationInstruction("verbose"));
    }
}
