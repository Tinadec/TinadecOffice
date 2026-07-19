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

    [Fact]
    public void Merge_TheirsResolutionKeepsNonOverlappingOursChangesAndPicksTheirsSide()
    {
        var result = ThreeWayTextMerge.Merge(
            "a\nb\nc\nd\n",
            "a\nours-headed\nc\nd\n",
            "a\ntheirs\nc\nremote-tail\n",
            TextConflictResolution.Theirs);
        Assert.False(result.IsClean);
        Assert.Equal("a\ntheirs\nc\nremote-tail\n", result.Text);
    }

    [Fact]
    public void Merge_BothResolutionConcatenatesOursAndTheirsAtConflict()
    {
        var result = ThreeWayTextMerge.Merge(
            "a\nb\nc\n",
            "a\nours\nc\n",
            "a\ntheirs\nc\n",
            TextConflictResolution.Both);
        Assert.False(result.IsClean);
        Assert.Single(result.Conflicts);
        Assert.Equal("a\nours\ntheirs\nc\n", result.Text);
    }

    [Fact]
    public void Merge_MarkersDefault_IncludesClassicGitConflictMarkers()
    {
        var result = ThreeWayTextMerge.Merge(
            "a\nb\nc\n",
            "a\nours\nc\n",
            "a\ntheirs\nc\n");
        Assert.False(result.IsClean);
        Assert.Contains("<<<<<<< ours", result.Text);
        Assert.Contains("||||||| base", result.Text);
        Assert.Contains("=======", result.Text);
        Assert.Contains(">>>>>>> theirs", result.Text);
    }

    [Fact]
    public void Merge_LargePairAboveMaxLcsCellsCollapsesToOneWholesaleChange()
    {
        // ponytail: 故意越过 MaxLcsCells 阈值，验证 LCS DP 矩阵降级为整段替换.
        // 一旦折叠，ours 与 theirs 一致 (单段替换) 不应再产冲突；折叠目标是"仍能合并"而非"恰好冲突".
        var baseText = string.Join('\n', Enumerable.Range(0, 2200).Select(i => "b" + i)) + "\n";
        var oursText = string.Join('\n', Enumerable.Range(0, 2200).Select(i => "o" + i)) + "\n";
        var theirsText = string.Join('\n', Enumerable.Range(0, 2200).Select(i => "t" + i)) + "\n";
        var result = ThreeWayTextMerge.Merge(baseText, oursText, theirsText);
        // ours 与 theirs 都整段替换，会触发重叠并产 1 个冲突；折叠不会让冲突归零.
        Assert.False(result.IsClean);
        Assert.Single(result.Conflicts);
    }
}
