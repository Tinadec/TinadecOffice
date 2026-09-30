using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Command prefix matching (todo E7): deliberately narrow, because "npm test && rm -rf ." starts
/// with "npm test" too. The engine's own check is the only one that counts — a caller naming a rule
/// id in its arguments cannot ride in on it.
/// </summary>
public sealed class ApprovalRulesTests
{
    [Theory]
    // One simple command only.
    [InlineData("npm test", true)]
    [InlineData("npm test -- -u", true)]
    [InlineData("echo hello", true)]
    // Chaining / redirection / substitution are never a single command, so never coverable.
    [InlineData("npm test && rm -rf .", false)]
    [InlineData("npm test | grep fail", false)]
    [InlineData("echo hi > out.txt", false)]
    [InlineData("echo ^stop", false)]
    [InlineData("echo %PATH%", false)]
    [InlineData("echo `whoami`", false)]
    [InlineData("echo $PWD", false)]
    [InlineData("npm test\nrm -rf .", false)]
    [InlineData("   ", false)]
    public void IsSimple_FlagsEverythingTheShellWouldInterpretAsMoreThanOneCommand(string command, bool expected) =>
        Assert.Equal(expected, CommandPrefixRules.IsSimple(command));

    [Theory]
    // Executable word matches case-insensitively; the arguments exactly.
    [InlineData("npm test", "npm test -u", true)]
    [InlineData("npm test", "npm test", true)]
    [InlineData("NPM test", "npm test", true)]
    [InlineData("npm test", "npm TEST", false)]
    [InlineData("npm test", "npm", false)]
    [InlineData("npm test", "npm run test", false)]
    [InlineData("git push", "git PUSH", false)] // the person approved the exact case
    [InlineData("npm test", "npm test && del *.dll", false)]
    [InlineData("npm test", "npm test > fail.txt", false)]
    public void Matches_ExactWordsOnly(string prefix, string command, bool expected) =>
        Assert.Equal(expected, CommandPrefixRules.Matches(command, prefix));

    [Fact]
    public void CommandOf_ShellAndCommandRun_AndRefusesSandboxWideningCalls()
    {
        Assert.Equal("npm test", CommandPrefixRules.CommandOf("shell",
            JsonDocument.Parse("{\"command\":\"npm test\",\"cwd\":\"C:/ws\"}").RootElement));
        Assert.Equal("npm -u test", CommandPrefixRules.CommandOf("command_run",
            JsonDocument.Parse("{\"executable\":\"npm\",\"arguments\":[\"-u\",\"test\"]}").RootElement));
        Assert.Null(CommandPrefixRules.CommandOf("command_run",
            JsonDocument.Parse("{\"executable\":\"npm\",\"stdin\":\"hello\"}").RootElement));
        Assert.Null(CommandPrefixRules.CommandOf("command_run",
            JsonDocument.Parse("{\"executable\":\"npm\",\"persist_grants\":true}").RootElement));
        Assert.Null(CommandPrefixRules.CommandOf("shell", JsonDocument.Parse("{}").RootElement));
        Assert.Null(CommandPrefixRules.CommandOf("unknown", JsonDocument.Parse("{\"command\":\"npm test\"}").RootElement));
    }
}
