using TinadecTools.Tools;

namespace TinadecTools.Tests;

public sealed class ToolConfirmationsTests
{
    [Fact]
    public void Require_AcceptsNonEmptyString()
    {
        ToolConfirmations.Require("yes", "confirm_probe");
    }

    [Fact]
    public void Require_ThrowsOnNullAndWhitespace()
    {
        Assert.Throws<InvalidOperationException>(() => ToolConfirmations.Require(null, "confirm_probe"));
        Assert.Throws<InvalidOperationException>(() => ToolConfirmations.Require("   ", "confirm_probe"));
        Assert.Throws<InvalidOperationException>(() => ToolConfirmations.Require("", "confirm_probe"));
    }

    [Fact]
    public void Require_NamesTheWireFieldTheModelCanFill()
    {
        // Callers pass nameof(args.ConfirmCommit); the model only knows confirm_commit.
        var error = Assert.Throws<InvalidOperationException>(() => ToolConfirmations.Require(null, "ConfirmCommit"));

        Assert.Contains("'confirm_commit'", error.Message);
        Assert.DoesNotContain("ConfirmCommit", error.Message);
        Assert.Contains("still approves", error.Message);
    }

    [Theory]
    [InlineData("ConfirmPush", "confirm_push")]
    [InlineData("ConfirmWorktreeCreate", "confirm_worktree_create")]
    [InlineData("confirm_fetch", "confirm_fetch")]
    public void ToWireName_ConvertsPropertyNamesToSnakeCase(string name, string expected) =>
        Assert.Equal(expected, ToolConfirmations.ToWireName(name));
}
