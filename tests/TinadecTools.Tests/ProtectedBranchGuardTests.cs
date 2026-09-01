using TinadecTools.Tools.Command;

namespace TinadecTools.Tests;

public sealed class ProtectedBranchGuardTests
{
    [Theory]
    [InlineData("git push origin main", "protected_branch_push", "main")]
    [InlineData("git push origin MAIN", "protected_branch_push", "MAIN")]
    [InlineData("git push origin HEAD:main", "protected_branch_push", "main")]
    [InlineData("git push origin HEAD:refs/heads/master", "protected_branch_push", "master")]
    [InlineData("git push origin main:master", "protected_branch_push", "master")]
    [InlineData("git push origin :main", "protected_branch_push", "main")]
    [InlineData("git push --force origin main", "protected_branch_push", "main")]
    [InlineData("git push origin +main", "protected_branch_push", "main")]
    [InlineData("git push origin feature-x main", "protected_branch_push", "main")]
    [InlineData("git push --delete origin master", "protected_branch_push", "master")]
    [InlineData("git -C sub push origin main", "protected_branch_push", "main")]
    [InlineData("git -c credential.interactive=never push origin main", "protected_branch_push", "main")]
    [InlineData("sudo git push origin main", "protected_branch_push", "main")]
    [InlineData("git push origin refs/heads/main", "protected_branch_push", "main")]
    [InlineData("cd app && git push origin main && echo done", "protected_branch_push", "main")]
    [InlineData("echo hi; git push origin main", "protected_branch_push", "main")]
    [InlineData("cmd /c \"git push origin main\"", "protected_branch_push", "main")]
    [InlineData("git push --all", "push_target_all", null)]
    [InlineData("git push --mirror", "push_target_all", null)]
    [InlineData("git push", "push_target_unknown", null)]
    [InlineData("git push origin", "push_target_unknown", null)]
    [InlineData("git push --repo upstream", "push_target_unknown", null)]
    public void ShellCommand_PushTowardProtectedBranch_IsDenied(string command, string reasonCode, string? detailFragment)
    {
        var verdict = ProtectedBranchGuard.EvaluateShellCommand(command);
        Assert.False(verdict.Allowed);
        Assert.Equal(reasonCode, verdict.ReasonCode);
        if (detailFragment is not null) Assert.Contains(detailFragment, verdict.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("git push origin feature-x")]
    [InlineData("git push origin HEAD:refs/heads/feature-x")]
    [InlineData("git push origin feature-x:release-1.2")]
    [InlineData("git push origin feature-x --force-with-lease")]
    [InlineData("git push origin refs/tags/v1")]
    [InlineData("git push origin :refs/tags/v1")]
    [InlineData("git push origin HEAD:refs/tags/v1")]
    [InlineData("git -C sub push origin feature-x")]
    [InlineData("git status")]
    [InlineData("git commit -m \"does something\"")]
    [InlineData("git pushx origin main")]
    [InlineData("echo git push origin main")]
    [InlineData("echo hello && git push origin feature-x")]
    public void ShellCommand_WithoutProtectedPush_IsAllowed(string command)
    {
        var verdict = ProtectedBranchGuard.EvaluateShellCommand(command);
        Assert.True(verdict.Allowed, $"{verdict.ReasonCode}: {verdict.Detail}");
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("master", true)]
    [InlineData("MAIN", true)]
    [InlineData("refs/heads/main", true)]
    [InlineData("feature-x", false)]
    [InlineData("mainframe", false)]
    [InlineData("refs/tags/v1", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsProtected_MatchesTrunkNamesOnly(string? branch, bool expected) =>
        Assert.Equal(expected, ProtectedBranchGuard.IsProtected(branch));

    [Fact]
    public void EvaluatePushBranch_DeniesProtectedBranch()
    {
        var verdict = ProtectedBranchGuard.EvaluatePushBranch("main");
        Assert.False(verdict.Allowed);
        Assert.Equal("protected_branch_push", verdict.ReasonCode);
        Assert.True(ProtectedBranchGuard.EvaluatePushBranch("feature-x").Allowed);
    }
}
