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
}