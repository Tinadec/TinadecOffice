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
}
