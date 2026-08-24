using TinadecTools.Runtime.Sandbox;
using TinadecTools.Tools.FileRW;

namespace TinadecTools.Tests;

public sealed class SandboxRuntimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_800_001)]
    public void ValidateTimeout_RejectsValuesOutsideConfiguredRange(int timeoutMs)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CommandSandboxRuntime.ValidateTimeout(timeoutMs));
    }

    [Fact]
    public void BuildPermissions_AlwaysIncludesWorkspaceWriteAccess()
    {
        var permissions = CommandSandboxRuntime.BuildPermissions(null, null, null);

        Assert.Contains(WorkspacePathResolver.WorkspaceRoot, permissions.ReadPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(WorkspacePathResolver.WorkspaceRoot, permissions.WritePaths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPermissions_AllowsExplicitEnvironmentVariableNames()
    {
        var permissions = CommandSandboxRuntime.BuildPermissions(null, null, ["MY_PACKAGE_TOKEN"]);

        Assert.Equal(["MY_PACKAGE_TOKEN"], permissions.EnvironmentVariableNames);
    }

    [Fact]
    public void MergeGrants_DeduplicatesPathsAndEnvironmentNames()
    {
        var existing = new SandboxPolicyFile
        {
            ReadPaths = [@"C:\\tools"],
            WritePaths = [@"C:\\cache"],
            EnvironmentVariables = ["TOKEN"]
        };
        var permissions = new SandboxPermissions
        {
            ReadPaths = [@"C:\\TOOLS"],
            WritePaths = [@"C:\\cache\\"],
            EnvironmentVariableNames = ["token"]
        };

        var merged = SandboxPolicyStore.MergeGrants(permissions, existing);

        Assert.Single(merged.ReadPaths);
        Assert.Single(merged.WritePaths);
        Assert.Single(merged.EnvironmentVariables);
    }

    [Fact]
    public void BuildPermissions_RejectsInvalidEnvironmentVariableNames()
    {
        Assert.Throws<ArgumentException>(() => CommandSandboxRuntime.BuildPermissions(null, null, ["BAD=NAME"]));
    }

    [Fact]
    public void ValidateRequest_RejectsWorkingDirectoryOutsideWorkspace()
    {
        var outside = Path.GetFullPath(Path.Combine(WorkspacePathResolver.WorkspaceRoot, ".."));

        Assert.Throws<UnauthorizedAccessException>(() => SandboxRequestValidator.Validate(
            "git", [], outside, 1));
    }

    [Fact]
    public void ValidateRequestAcceptsTimeoutBoundaries()
    {
        SandboxRequestValidator.Validate("git", ["value with spaces", "quote\"value", "semi;colon", "amp&value"], WorkspacePathResolver.WorkspaceRoot, 1);
        SandboxRequestValidator.Validate("git", [], WorkspacePathResolver.WorkspaceRoot, 1_800_000, new Dictionary<string, string> { ["SAFE_NAME"] = "safe" });
    }
}
