using TinadecCore.Abstractions.Ports;
using Xunit;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The approval contract has to be decision-grade: it must show what the agent is
/// about to do and must not become a side channel for file bodies or credentials.
/// Both halves are pinned here.
/// </summary>
public sealed class ApprovalEvidenceProjectorTests
{
    [Fact]
    public void ShellCallExposesTheCommandAndWorkingDirectory()
    {
        var evidence = ApprovalEvidenceProjector.Project(
            "shell",
            """{"command":"git status --porcelain","cwd":"C:\\repo","long_lived":false,"timeout_ms":60000}""");

        Assert.Equal("git status --porcelain", evidence.Command);
        Assert.Equal(@"C:\repo", evidence.WorkingDirectory);
        Assert.Contains("git status --porcelain", evidence.Summary);
        Assert.Contains("shell", evidence.Summary);
        Assert.Contains("\"long_lived\":false", evidence.Arguments);
        Assert.Contains("\"timeout_ms\":60000", evidence.Arguments);
    }

    [Fact]
    public void WriteFileCallNamesThePath_ButNeverCarriesTheBody()
    {
        var body = new string('x', 40_000);
        var evidence = ApprovalEvidenceProjector.Project(
            "write_file",
            $$"""{"filepath":"C:\\repo\\src\\big.ts","content":"{{body}}","file_hash":"deadbeef"}""");

        Assert.Equal(@"C:\repo\src\big.ts", evidence.ResourcePath);
        Assert.Contains(@"src\big.ts", evidence.Summary);
        Assert.DoesNotContain(body, evidence.Arguments, StringComparison.Ordinal);
        Assert.Contains("40000 chars, sha256 ", evidence.Arguments);
    }

    [Fact]
    public void BulkFingerprintIsStableAndChangesWithContent()
    {
        var first = ApprovalEvidenceProjector.Project("write_file", """{"filepath":"a","content":"hello"}""");
        var same = ApprovalEvidenceProjector.Project("write_file", """{"filepath":"a","content":"hello"}""");
        var other = ApprovalEvidenceProjector.Project("write_file", """{"filepath":"a","content":"hellp"}""");

        Assert.Equal(first.Arguments, same.Arguments);
        Assert.NotEqual(first.Arguments, other.Arguments);
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("token")]
    [InlineData("client_secret")]
    [InlineData("nonce")]
    public void SecretShapedKeysAreRedactedEntirely(string key)
    {
        var evidence = ApprovalEvidenceProjector.Project(
            "mcp_invoke",
            $$"""{"server_id":"gh","tool_name":"list","{{key}}":"sk-super-secret-value-123"}""");

        Assert.Contains("\"[redacted]\"", evidence.Arguments);
        Assert.DoesNotContain("sk-super-secret-value-123", evidence.Arguments, StringComparison.Ordinal);
        // A redacted value must not leak through the headline either.
        Assert.DoesNotContain("sk-super-secret-value-123", evidence.Summary, StringComparison.Ordinal);
        Assert.Contains("gh", evidence.Arguments);
    }

    [Fact]
    public void LongScalarIsTruncatedWithAnExplicitMarker()
    {
        var value = new string('y', 5_000);
        var evidence = ApprovalEvidenceProjector.Project("git_checkout", $$"""{"branch":"{{value}}"}""");

        Assert.Contains("…[truncated]", evidence.Summary, StringComparison.Ordinal);
        Assert.True(evidence.Summary.Length < 500);
    }

    [Fact]
    public void ArrayOfPathsIsListedAndOverflowIsCountedNotDroppedSilently()
    {
        var evidence = ApprovalEvidenceProjector.Project(
            "git_stage",
            """{"paths":["p0","p1","p2","p3","p4","p5","p6","p7","p8","p9"]}""");

        Assert.Contains("\"+2 more\"", evidence.Arguments);
        Assert.Contains("\"p0\"", evidence.Arguments);
        Assert.DoesNotContain("\"p9\"", evidence.Arguments);
    }

    [Fact]
    public void DigestStaysWithinTheDurableColumnBudget()
    {
        var wide = string.Join(',', Enumerable.Range(0, 80).Select(i => $$"""{"k{{i}}":"v{{i}}{{new string('z', 60)}}"}"""));
        var evidence = ApprovalEvidenceProjector.Project("mcp_invoke", "{" + wide + "}");

        Assert.True(
            evidence.Arguments.Length <= ApprovalEvidenceProjector.MaximumDigestLength,
            $"projected {evidence.Arguments.Length} characters, ceiling is {ApprovalEvidenceProjector.MaximumDigestLength}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a bare string\"")]
    public void UnparseableOrNonObjectParametersDegradeInsteadOfThrowing(string? parameters)
    {
        var evidence = ApprovalEvidenceProjector.Project("shell", parameters);

        Assert.StartsWith("shell", evidence.Summary);
        Assert.Null(evidence.Command);
        Assert.Null(evidence.ResourcePath);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Arguments));
    }

    [Fact]
    public void MissingCommandAndPathFallBackToTheFirstReadableFact()
    {
        var evidence = ApprovalEvidenceProjector.Project(
            "git_branch_delete",
            """{"branch":"feature/x","confirm_branch_delete":"yes"}""");

        Assert.Contains("feature/x", evidence.Summary);
        Assert.Equal("feature/x", evidence.ResourcePath);
    }

    [Fact]
    public void ToolIdIsStillNamedWhenThereIsNothingElseToShow()
    {
        var evidence = ApprovalEvidenceProjector.Project("sandbox_reset", "{}");

        Assert.Equal("sandbox_reset", evidence.Summary);
        Assert.Equal("{}", evidence.Arguments);
    }
}
