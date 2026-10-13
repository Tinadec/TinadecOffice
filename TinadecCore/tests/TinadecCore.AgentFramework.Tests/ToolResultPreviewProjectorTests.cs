using System.Text;
using System.Text.Json;
using TinadecCore.Abstractions.Ports;
using TinadecCore.Contracts.Dtos;
using Xunit;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// The result preview is a new way for tool output to leave Core, so both halves are
/// pinned: it must be useful (the lines, matches and output tail a reader expects) and it
/// must stay bounded and secret-free (no raw parameters, no secret keys, no credential files).
/// </summary>
public sealed class ToolResultPreviewProjectorTests
{
    [Fact]
    public void ReadFile_ProjectsNumberedLines_WhateverTheLineRecordCasing()
    {
        var result = """
            {"success":true,"file_hash":"h","all_contents":[
              {"content":{"Content":"first","LineNumber":1},"line_hash":"a"},
              {"content":{"content":"second","line_number":2},"line_hash":"b"}]}
            """;

        var preview = Project("read_file", """{"filepath":"src/app.ts"}""", result).Preview!;

        Assert.Equal("file_lines", preview.Kind);
        Assert.Equal("src/app.ts", preview.Path);
        Assert.Equal(["first", "second"], preview.Lines!.Select(line => line.Text));
        Assert.Equal([1, 2], preview.Lines!.Select(line => line.LineNumber ?? 0));
        Assert.Equal(2, preview.TotalCount);
    }

    [Fact]
    public void ReadFile_KeepsAtMostSixtyLinesOfFourHundredCharacters()
    {
        var lines = Enumerable.Range(1, 100)
            .Select(i => $$"""{"content":{"Content":"{{new string('x', 900)}}","LineNumber":{{i}}},"line_hash":"h"}""");
        var result = $$"""{"success":true,"all_contents":[{{string.Join(',', lines)}}]}""";

        var dto = Project("read_file", """{"filepath":"big.txt"}""", result, maxBytes: 64 * 1024);

        Assert.True(dto.Truncated);
        Assert.Equal(60, dto.Preview!.Lines!.Count);
        Assert.All(dto.Preview.Lines!, line => Assert.Equal(400, line.Text.Length));
        Assert.Equal(100, dto.Preview.TotalCount);
    }

    [Fact]
    public void SearchMatches_DropMatchesInsideCredentialFiles()
    {
        var result = """
            {"success":true,"total_match_count":3,"truncated":false,"lines":[
              {"filepath":"/w/src/a.ts","relative_path":"src/a.ts","line_number":4,"content":"useResults()","is_match":true},
              {"filepath":"/w/.env.local","relative_path":".env.local","line_number":1,"content":"API_KEY=sk-live","is_match":true},
              {"filepath":"/w/src/b.ts","relative_path":"src/b.ts","line_number":9,"content":"// ctx","is_match":false}]}
            """;

        var dto = Project("file_search", """{"pattern":"useResults","path":"."}""", result);

        Assert.Equal("search_matches", dto.Preview!.Kind);
        Assert.Equal(["src/a.ts", "src/b.ts"], dto.Preview.Lines!.Select(line => line.Path));
        Assert.DoesNotContain("sk-live", Serialize(dto), StringComparison.Ordinal);
        Assert.Equal(3, dto.Preview.TotalCount);
        Assert.True(dto.Truncated);
    }

    [Fact]
    public void Directory_ListsEntriesAndReportsMorePages()
    {
        var entries = Enumerable.Range(0, 90).Select(i => $$"""{"name":"f{{i}}.ts","path":"src/f{{i}}.ts","type":"file","size":{{i}}}""");
        var result = $$"""{"success":true,"has_more":false,"entries":[{{string.Join(',', entries)}}]}""";

        var dto = Project("ls", """{"path":"src"}""", result);

        Assert.Equal("directory", dto.Preview!.Kind);
        Assert.Equal(80, dto.Preview.Entries!.Count);
        Assert.Equal("f0.ts", dto.Preview.Entries![0].Name);
        Assert.Equal("file", dto.Preview.Entries![0].Type);
        Assert.Equal(90, dto.Preview.TotalCount);
        Assert.True(dto.Truncated);
    }

    [Fact]
    public void CommandOutput_KeepsTheTailOfEachStreamAndTheExitCode()
    {
        var stdout = "HEAD-" + new string('o', 20_000) + "-TAIL";
        var result = JsonSerializer.Serialize(new
        {
            success = false,
            terminal_session_id = "t",
            command = "npm test",
            status = "completed",
            exit_code = 1,
            stdout,
            stderr = "1 failing",
            stdout_truncated = false,
            stderr_truncated = false,
            timed_out = false,
            duration_ms = 1200,
        });

        var dto = Project("shell", """{"command":"npm test"}""", result, maxBytes: 64 * 1024);
        var preview = dto.Preview!;

        Assert.Equal("command_output", preview.Kind);
        Assert.Equal(1, preview.ExitCode);
        Assert.False(preview.TimedOut);
        Assert.EndsWith("-TAIL", preview.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("HEAD-", preview.Stdout, StringComparison.Ordinal);
        Assert.Equal(8 * 1024, Encoding.UTF8.GetByteCount(preview.Stdout!));
        Assert.Equal("1 failing", preview.Stderr);
        Assert.True(dto.Truncated);
    }

    [Fact]
    public void CommandOutput_NeverSplitsAMultiByteCharacter()
    {
        var result = JsonSerializer.Serialize(new { success = true, exit_code = 0, stdout = string.Concat(Enumerable.Repeat("测😀", 5000)), stderr = "" });

        var preview = Project("shell", """{"command":"x"}""", result, maxBytes: 64 * 1024).Preview!;

        Assert.True(Encoding.UTF8.GetByteCount(preview.Stdout!) <= 8 * 1024);
        Assert.False(char.IsLowSurrogate(preview.Stdout![0]));
        Assert.Equal(preview.Stdout, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(preview.Stdout)));
    }

    [Fact]
    public void GitDiff_IsTextWithDiffLanguage_ButWithheldWhenItTouchesACredentialFile()
    {
        var plain = """{"success":true,"truncated":false,"sections":[{"kind":"working_tree","diff":"diff --git a/src/a.ts b/src/a.ts\n+x","files":[]}]}""";
        var secret = """{"success":true,"sections":[{"kind":"working_tree","diff":"diff --git a/config/server.pem b/config/server.pem\n+-----BEGIN","files":[]}]}""";

        var shown = Project("git_diff", """{"target":"working_tree"}""", plain).Preview!;
        var hidden = Project("git_diff", """{"target":"working_tree"}""", secret);

        Assert.Equal("text", shown.Kind);
        Assert.Equal("diff", shown.Language);
        Assert.Contains("+x", shown.Text, StringComparison.Ordinal);
        Assert.Equal("withheld", hidden.Preview!.Kind);
        Assert.Equal("sensitive_path", hidden.Preview.Reason);
        Assert.DoesNotContain("BEGIN", Serialize(hidden), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("app/.env.production")]
    [InlineData(@"C:\keys\server.PEM")]
    [InlineData("certs/client.p12")]
    [InlineData("/home/u/.ssh/id_ed25519")]
    [InlineData("id_rsa.pub")]
    [InlineData(".npmrc")]
    [InlineData("deploy/credentials.json")]
    [InlineData("vault.kdbx")]
    public void ReadingACredentialShapedFile_IsWithheld(string path)
    {
        var dto = Project("read_file", JsonSerializer.Serialize(new { filepath = path }),
            """{"success":true,"all_contents":[{"content":{"Content":"TOKEN=abc","LineNumber":1}}]}""");

        Assert.Equal("withheld", dto.Preview!.Kind);
        Assert.Equal("sensitive_path", dto.Preview.Reason);
        Assert.DoesNotContain("TOKEN=abc", Serialize(dto), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/environment.ts")]
    [InlineData("docs/keys.md")]
    [InlineData("README.md")]
    public void OrdinaryFiles_AreNotMistakenForCredentials(string path) =>
        Assert.False(ToolResultPreviewProjector.IsSensitivePath(path));

    [Fact]
    public void JsonFallback_DropsSecretKeysAtAnyDepth_AndBoundsDepthArraysAndStrings()
    {
        var deep = """{"a":{"b":{"c":{"d":{"e":{"f":{"g":1}}}}}}}""";
        var result = $$"""
            {"token":"sk-top","nested":{"items":[{"password":"p1","name":"ok"}]},
             "list":[{{string.Join(',', Enumerable.Range(0, 30))}}],
             "long":"{{new string('y', 1000)}}","deep":{{deep}}}
            """;

        var dto = Project("git_status", "{}", result, maxBytes: 64 * 1024);
        var json = Serialize(dto);

        Assert.Equal("json", dto.Preview!.Kind);
        Assert.DoesNotContain("sk-top", json, StringComparison.Ordinal);
        Assert.DoesNotContain("p1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"token\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"ok\"", json, StringComparison.Ordinal);
        Assert.Equal(20, dto.Preview.Value!["list"]!.AsArray().Count);
        Assert.Equal(400, dto.Preview.Value!["long"]!.GetValue<string>().Length);
        Assert.Contains("{…}", json, StringComparison.Ordinal);
        Assert.True(dto.Truncated);
    }

    [Fact]
    public void Arguments_AreTheEvidenceProjection_NeverTheRawParameters()
    {
        var parameters = """{"filepath":"src/a.ts","content":"SECRET BODY","api_key":"sk-1"}""";

        var dto = Project("write_file", parameters, """{"success":true}""");
        var json = Serialize(dto);

        Assert.DoesNotContain("SECRET BODY", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-1", json, StringComparison.Ordinal);
        Assert.Contains("src/a.ts", dto.Arguments!.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("requested")]
    [InlineData("pending")]
    [InlineData("approved")]
    [InlineData("running")]
    public void NonTerminalExecutions_HaveNoPreview(string status) =>
        Assert.Null(Project("read_file", """{"filepath":"a.ts"}""",
            """{"success":true,"all_contents":[]}""", status: status).Preview);

    [Fact]
    public void FailedExecutionWithoutResult_HasNoPreviewButKeepsTheSafeMessage()
    {
        var dto = Project("shell", """{"command":"x"}""", null, status: "failed",
            errorCategory: "tool_runtime_unavailable", safeMessage: "Tool runtime unavailable.");

        Assert.Null(dto.Preview);
        Assert.Equal("tool_runtime_unavailable", dto.ErrorCategory);
        Assert.Equal("Tool runtime unavailable.", dto.SafeErrorMessage);
    }

    [Fact]
    public void ToolReportedFailure_IsShownAsWhatTheToolSaid()
    {
        var dto = Project("read_file", """{"filepath":"missing.ts"}""", """{"success":false,"error":"File not found.","all_contents":[]}""");

        Assert.Equal("json", dto.Preview!.Kind);
        Assert.Equal("File not found.", dto.Preview.Value!["error"]!.GetValue<string>());
    }

    [Fact]
    public void UnparseableResult_IsWithheld_NotEchoed()
    {
        var dto = Project("read_file", """{"filepath":"a.ts"}""", "not json TOKEN=1");

        Assert.Equal("withheld", dto.Preview!.Kind);
        Assert.DoesNotContain("TOKEN", Serialize(dto), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 16 * 1024)]
    [InlineData(0, 1024)]
    [InlineData(10, 1024)]
    [InlineData(4096, 4096)]
    [InlineData(1_000_000, 64 * 1024)]
    public void MaxBytes_IsClampedIntoOneToSixtyFourKiB(int? requested, int expected) =>
        Assert.Equal(expected, ToolResultPreviewProjector.ClampMaxBytes(requested));

    [Fact]
    public void TheWholePreview_StaysWithinTheByteBudget()
    {
        var entries = Enumerable.Range(0, 80).Select(i => $$"""{"name":"{{new string('n', 300)}}{{i}}","type":"file"}""");
        var result = $$"""{"success":true,"entries":[{{string.Join(',', entries)}}]}""";

        var dto = Project("ls", """{"path":"."}""", result, maxBytes: 2048);

        Assert.True(dto.Truncated);
        Assert.True(dto.Preview!.Entries!.Sum(entry => Encoding.UTF8.GetByteCount(entry.Name)) <= 2048);
    }

    private static ToolExecutionPreviewDto Project(
        string toolId,
        string parameters,
        string? result,
        int maxBytes = ToolResultPreviewProjector.DefaultMaxBytes,
        string status = "completed",
        string? errorCategory = null,
        string? safeMessage = null) =>
        ToolResultPreviewProjector.Project(Snapshot(toolId, parameters, result, status, errorCategory, safeMessage), maxBytes);

    private static ToolExecutionSnapshot Snapshot(string toolId, string parameters, string? result, string status, string? errorCategory, string? safeMessage) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, toolId, "key", "low", false, false, status, parameters, "hash", 1, result, errorCategory, safeMessage,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static string Serialize(ToolExecutionPreviewDto dto) =>
        JsonSerializer.Serialize(dto, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
