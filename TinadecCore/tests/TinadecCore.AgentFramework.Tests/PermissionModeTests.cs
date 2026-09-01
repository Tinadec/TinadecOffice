using TinadecCore.DmaEA;
using TinadecCore.Tools;
using Xunit;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Pins the M5 permission-mode unfreeze: unattended modes survive frozen
/// admission verbatim, the scope resolver no longer rejects them at resume,
/// and deny/unknown stay fail-closed at both gates.
/// </summary>
public class PermissionModeTests
{
    [Fact]
    public void PermissionMode_FullAccess_DoesNotThrowAtScopeResume()
    {
        ToolInvocationScopeResolver.EnsurePermissionModeExecutable("full-access");
        ToolInvocationScopeResolver.EnsurePermissionModeExecutable("Full-Access ");
        ToolInvocationScopeResolver.EnsurePermissionModeExecutable("auto-approve");
        ToolInvocationScopeResolver.EnsurePermissionModeExecutable("ask");
        ToolInvocationScopeResolver.EnsurePermissionModeExecutable("default");
        ToolInvocationScopeResolver.EnsurePermissionModeExecutable(null);
        ToolInvocationScopeResolver.EnsurePermissionModeExecutable("");
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("sudo")]
    public void PermissionMode_DenyAndUnknown_StillFailClosed(string mode)
    {
        Assert.Throws<UnauthorizedAccessException>(() => ToolInvocationScopeResolver.EnsurePermissionModeExecutable(mode));
    }

    [Theory]
    [InlineData("full-access", "full-access")]
    [InlineData(" Full-Access ", "full-access")]
    [InlineData("auto-approve", "auto-approve")]
    [InlineData("deny", "deny")]
    [InlineData("default", "ask")]
    [InlineData("ask", "ask")]
    [InlineData(null, "ask")]
    [InlineData("", "ask")]
    [InlineData("sudo", "ask")]
    public void PermissionMode_FullAccess_PassesThroughFrozenAdmission(string? input, string expected)
    {
        Assert.Equal(expected, AgentRuntimeConfigurationResolver.NormalizePermissionMode(input));
    }
}
