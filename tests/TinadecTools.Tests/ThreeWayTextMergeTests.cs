using TinadecTools.Tools.Git;

namespace TinadecTools.Tests;

public sealed class ThreeWayTextMergeTests
{
    [Fact]
    public void Merge_AutoMergesNonOverlappingChanges()
    {
        var result = ThreeWayTextMerge.Merge("a\nb\nc\nd\n", "a\nours\nc\nd\n", "a\nb\nc\ntheirs\n");
        Assert.True(result.IsClean);
        Assert.Equal("a\nours\nc\ntheirs\n", result.Text);
    }

    [Fact]
    public void Merge_CollapsesIdenticalOverlappingChanges()
    {
        var result = ThreeWayTextMerge.Merge("a\nb\nc\n", "a\nsame\nc\n", "a\nsame\nc\n");
        Assert.True(result.IsClean);
        Assert.Equal("a\nsame\nc\n", result.Text);
    }

    [Fact]
    public void Merge_ReportsOnlyTheOverlappingRegion()
    {
        var result = ThreeWayTextMerge.Merge("a\nb\nc\n", "a\nours\nc\n", "a\ntheirs\nc\n");
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("b", conflict.Base);
        Assert.Equal("ours", conflict.Ours);
        Assert.Equal("theirs", conflict.Theirs);
        Assert.Contains("<<<<<<< ours", result.Text);
    }

    [Fact]
    public void Merge_OursResolutionStillKeepsNonOverlappingTheirsChanges()
    {
        var result = ThreeWayTextMerge.Merge(
            "a\nb\nc\nd\n",
            "a\nours\nc\nd\n",
            "a\ntheirs\nc\nremote-tail\n",
            TextConflictResolution.Ours);
        Assert.False(result.IsClean);
        Assert.Equal("a\nours\nc\nremote-tail\n", result.Text);
    }
}
