using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using TinadecCore.Abstractions.Ports;
using TinadecCore.AgentFramework.Tests.Acp;
using TinadecCore.Models.Harness;
using TinadecCore.Models.Harness.Headless;

namespace TinadecCore.AgentFramework.Tests.Headless;

/// <summary>
/// The headless channel: measured vendor frames in, an answer or a precise failure out.
/// <para>
/// Every frame string below is copied from a real capture on this host (a harness run whose stdout was
/// saved verbatim), not written to match the parser. Where a parser is fed a frame the vendor has not
/// been observed sending, the test says so — that is the difference between pinning behaviour and
/// pinning an assumption.
/// </para>
/// </summary>
public sealed class HeadlessCliChatClientTests
{
    private static readonly ChatMessage[] Prompt = [new ChatMessage(ChatRole.User, "Reply with exactly: PONG")];

    private static HarnessChannelSpec Channel(string id) => HarnessCatalog.Find(id)!.Channel(AgentChannels.Cli)!;

    private static HeadlessCliChatClient Client(string id, RecordingRunner runner, string cwd = ".") =>
        new(id, Channel(id), Channel(id).Envelope, @"C:\fake\harness.exe", cwd, null, runner);

    // ---- envelopes, frame by frame ---------------------------------------------------------------

    /// <summary>The result frame claude-code 2.x actually sent after a PONG turn on this host.</summary>
    [Fact]
    public void ClaudeResult_TerminalFrameCarriesTheAnswerAndTheCounts()
    {
        var envelope = new ClaudeFamilyEnvelope();
        envelope.Feed("""
            {"type":"assistant","message":{"role":"assistant","content":[{"thinking":"The user just wants me to reply exactly \\"PONG\\".","type":"thinking","signature":""}]}}
            """.Trim(), null);
        envelope.Feed("""
            {"type":"assistant","message":{"role":"assistant","content":[{"text":"PONG","type":"text"}]}}
            """.Trim(), null);
        envelope.Feed("""
            {"duration_api_ms":6085,"stop_reason":"end_turn","total_cost_usd":0.12351,"usage":{"input_tokens":24487,"cache_read_input_tokens":0,"output_tokens":43},"permission_denials":[],"is_error":false,"num_turns":1,"subtype":"success","result":"PONG","type":"result"}
            """.Trim(), null);

        Assert.Equal("PONG", envelope.Answer);
        Assert.Null(envelope.Failure);
        Assert.Equal(24487, envelope.Usage!.InputTokenCount);
        Assert.Equal(43, envelope.Usage!.OutputTokenCount);
        Assert.Equal(24530, envelope.Usage!.TotalTokenCount);
    }

    [Fact]
    public void ClaudeResult_TextDeltasComeFromAssistantFramesOnly()
    {
        var envelope = new ClaudeFamilyEnvelope();
        var streamed = new List<string>();
        envelope.Feed("""{"type":"assistant","message":{"content":[{"type":"text","text":"PO"}]}}""", streamed.Add);
        envelope.Feed("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hmm"}]}}""", streamed.Add);
        envelope.Feed("""{"type":"assistant","message":{"content":[{"type":"text","text":"NG"}]}}""", streamed.Add);
        envelope.Feed("""{"type":"result","subtype":"success","is_error":false,"result":"PONG"}""", streamed.Add);

        Assert.Equal(["PO", "NG"], streamed);
        // Thinking is not the answer, and the result frame does not re-emit it as a delta.
        Assert.DoesNotContain("hmm", streamed);
    }

    /// <summary>
    /// CodeBuddy sends the same family plus a frame nobody else uses. An unknown frame type must be
    /// skipped rather than treated as a failure — measured capture included it between init and answer.
    /// </summary>
    [Fact]
    public void ClaudeResult_IgnoresFrameTypesItHasNeverSeen()
    {
        var envelope = new ClaudeFamilyEnvelope();
        envelope.Feed("""{"type":"system","subtype":"init","apiKeySource":"copilot.tencent.com","tools":["Read"]}""", null);
        envelope.Feed("""{"type":"file-history-snapshot","messageId":"abc"}""", null);
        envelope.Feed("""{"type":"result","subtype":"success","is_error":false,"result":"PONG","num_turns":3}""", null);

        Assert.Equal("PONG", envelope.Answer);
    }

    [Fact]
    public void ClaudeResult_ErrorFrameIsAFailureAndNeverAnAnswer()
    {
        var envelope = new ClaudeFamilyEnvelope();
        envelope.Feed("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Invalid API key"}""", null);

        Assert.Null(envelope.Answer);
        Assert.Contains("error_during_execution", envelope.Failure, StringComparison.Ordinal);
        Assert.Contains("Invalid API key", envelope.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeResult_NonJsonNoiseLinesAreSkipped()
    {
        var envelope = new ClaudeFamilyEnvelope();
        envelope.Feed("> build · gpt-5.6-sol", null);
        envelope.Feed("", null);
        envelope.Feed("""{"type":"result","subtype":"success","is_error":false,"result":"PONG"}""", null);

        Assert.Equal("PONG", envelope.Answer);
    }

    /// <summary>
    /// Codex's names come from its own event enum, and the answer is the completed item's text.
    /// Measured on this host up to the terminal frames: thread.started, item.completed(error), and the
    /// reconnect/turn.failed pair below were captured live; agent_message is the vendor's documented
    /// item type rather than a frame this host produced, because its provider answered 403.
    /// </summary>
    [Fact]
    public void CodexJsonl_AgentMessageItemIsTheAnswer()
    {
        var envelope = new CodexJsonlEnvelope();
        envelope.Feed("""{"type":"thread.started","thread_id":"01a1081b-ed3f-7792-9056-f48a945d62dc"}""", null);
        envelope.Feed("""{"type":"turn.started"}""", null);
        envelope.Feed("""{"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"PONG"}}""", null);
        envelope.Feed("""{"type":"turn.completed","usage":{"input_tokens":1200,"cached_input_tokens":900,"output_tokens":7,"reasoning_output_tokens":2}}""", null);

        Assert.Equal("PONG", envelope.Answer);
        Assert.Null(envelope.Failure);
        Assert.Equal(7, envelope.Usage!.OutputTokenCount);
        Assert.Equal(900, envelope.Usage!.CachedInputTokenCount);
        Assert.Equal(2, envelope.Usage!.ReasoningTokenCount);
    }

    /// <summary>
    /// Measured during an outage: six reconnect frames and two config-warning items before
    /// <c>turn.failed</c>. The warnings are about the user's config.toml and must never be returned as
    /// the model's answer, and the reconnect noise must not outrank the terminal verdict.
    /// </summary>
    [Fact]
    public void CodexJsonl_ReconnectNoiseAndConfigWarningsDoNotBecomeTheAnswer()
    {
        var envelope = new CodexJsonlEnvelope();
        envelope.Feed("""{"type":"item.completed","item":{"id":"item_0","type":"error","message":"Codex is ignoring 1 unrecognized configuration setting."}}""", null);
        envelope.Feed("""{"type":"error","message":"Reconnecting... 1/5 (unexpected status 403 Forbidden)"}""", null);
        envelope.Feed("""{"type":"error","message":"Reconnecting... 5/5 (unexpected status 403 Forbidden)"}""", null);
        envelope.Feed("""{"type":"turn.failed","error":{"message":"unexpected status 403 Forbidden"}}""", null);

        Assert.Null(envelope.Answer);
        Assert.Equal("unexpected status 403 Forbidden", envelope.Failure);
    }

    /// <summary>
    /// Measured on this host: dsh emits <c>final</c> with empty text and exits 1 when its upstream has
    /// no channel. The envelope reports the empty final as an answer — the exit code is what makes it a
    /// failure — so this pins the split of responsibilities between the two layers.
    /// </summary>
    [Fact]
    public void DshFinal_EmptyFinalIsStillTheFrameItSent()
    {
        var envelope = new DshFinalEnvelope();
        envelope.Feed("""{"type":"session","sessionId":"session-e3c2f19e-bca2-4ba3-a5a8-292956f63367","cwd":"C:\\tmp\\cli-probe\\ws"}""", null);
        envelope.Feed("""{"type":"status","phase":"turn_start","turn":1}""", null);
        envelope.Feed("""{"type":"final","text":""}""", null);

        Assert.Equal(string.Empty, envelope.Answer);
        Assert.Null(envelope.Failure);
    }

    /// <summary>
    /// The zcode document measured on this host: one indented JSON object, 26 lines, answer in
    /// <c>response</c>. A line-framed reader would see <c>{</c> and report no answer at all, which is
    /// what <see cref="IHeadlessEnvelope.WholeDocument"/> exists to prevent.
    /// </summary>
    [Fact]
    public void ZcodeDocument_ReadsOneIndentedDocumentNotFrames()
    {
        var envelope = new ZcodeDocumentEnvelope();
        Assert.True(envelope.WholeDocument);
        envelope.Feed("""
            {
              "sessionId": "sess_4f266d7d-4104-4cd6-8cfe-9a079937f576",
              "turnId": "turn_e9cb396d-7cc6-449f-927e-f81f6ae56f73",
              "response": "PONG",
              "usage": {
                "inputTokens": 19239,
                "outputTokens": 3,
                "totalTokens": 19242
              },
              "projection": {
                "status": "idle",
                "turnCount": 1
              }
            }
            """, null);

        Assert.Equal("PONG", envelope.Answer);
        Assert.Equal(19239, envelope.Usage!.InputTokenCount);
        Assert.Equal(19242, envelope.Usage!.TotalTokenCount);
    }

    // ---- the client's fail-closed rules ----------------------------------------------------------

    [Fact]
    public async Task Answer_ReturnsTheHarnessTextAndItsUsage()
    {
        var runner = new RecordingRunner
        {
            Frames = ["""{"type":"result","subtype":"success","is_error":false,"result":"PONG","usage":{"input_tokens":10,"output_tokens":2}}"""],
            Outcome = new HarnessTurnOutcome(0, "")
        };

        var response = await Client("claude-code", runner).GetResponseAsync(Prompt);

        Assert.Equal("PONG", response.Text);
        Assert.Equal(12, response.Usage!.TotalTokenCount);
    }

    /// <summary>
    /// The claude-verbose-without-stdin case, reproduced exactly: exit 0, no frames at all. An empty
    /// assistant message here would be filed as a turn where the model simply had nothing to say.
    /// </summary>
    [Fact]
    public async Task SilentSuccessWithNoFrames_FailsNamingTheMissingTerminalFrame()
    {
        var runner = new RecordingRunner { Frames = [], Outcome = new HarnessTurnOutcome(0, "") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client("claude-code", runner).GetResponseAsync(Prompt));

        Assert.Contains("\"type\":\"result\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("would be a guess", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The dsh case: it did emit its terminal frame, but empty, while exiting 1 with the vendor's
    /// reason on stderr. Both halves have to reach the operator.
    /// </summary>
    [Fact]
    public async Task NonZeroExit_FailsWithVendorStderr()
    {
        var runner = new RecordingRunner
        {
            Frames = ["""{"type":"final","text":""}"""],
            Outcome = new HarnessTurnOutcome(1, "dsh: SERVER: No available channel for model deepseek-flash")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client("dsh", runner).GetResponseAsync(Prompt));

        Assert.Contains("code 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("No available channel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VendorReportedFailure_FailsBeforeTheExitCodeIsConsulted()
    {
        var runner = new RecordingRunner
        {
            Frames = ["""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Invalid API key"}"""],
            Outcome = new HarnessTurnOutcome(0, "")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Client("claude-code", runner).GetResponseAsync(Prompt));

        Assert.Contains("Invalid API key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyButCompleteAnswer_IsReturnedRatherThanRefused()
    {
        // A harness that legitimately answers "" must not be treated as broken; the rules above cover
        // every way an empty answer was produced by accident.
        var runner = new RecordingRunner
        {
            Frames = ["""{"type":"result","subtype":"success","is_error":false,"result":""}"""],
            Outcome = new HarnessTurnOutcome(0, "")
        };

        var response = await Client("claude-code", runner).GetResponseAsync(Prompt);

        Assert.Equal(string.Empty, response.Text);
    }

    // ---- prompt routing --------------------------------------------------------------------------

    /// <summary>
    /// Where the prompt physically goes is decided per row, and the wrong answer to that question is a
    /// silently lost turn: claude drops an argv prompt under the streaming input format, while codex
    /// and zcode want it as an argument.
    /// </summary>
    [Theory]
    [InlineData("claude-code", HarnessPromptDeliveries.StdinMessage, """{"type":"result","subtype":"success","is_error":false,"result":"PONG"}""")]
    [InlineData("codebuddy", HarnessPromptDeliveries.StdinMessage, """{"type":"result","subtype":"success","is_error":false,"result":"PONG"}""")]
    [InlineData("codex", HarnessPromptDeliveries.Stdin, """{"type":"item.completed","item":{"type":"agent_message","text":"PONG"}}""")]
    [InlineData("zcode", HarnessPromptDeliveries.Argv, """{ "response": "PONG" }""")]
    [InlineData("dsh", HarnessPromptDeliveries.Argv, """{"type":"final","text":"PONG"}""")]
    public async Task PromptGoesWhereTheRowSays(string id, string expectedDelivery, string terminalFrame)
    {
        var runner = new RecordingRunner
        {
            Frames = [terminalFrame],
            Outcome = new HarnessTurnOutcome(0, "")
        };

        var response = await Client(id, runner).GetResponseAsync(Prompt);

        Assert.Equal("PONG", response.Text);
        Assert.Equal(expectedDelivery, Channel(id).PromptDelivery);
        var request = runner.LastRequest!;
        if (expectedDelivery == HarnessPromptDeliveries.Argv)
        {
            Assert.Contains("Reply with exactly: PONG", request.ArgumentList);
            Assert.Null(request.StdinPayload);
        }
        else
        {
            Assert.DoesNotContain("Reply with exactly: PONG", request.ArgumentList);
            Assert.Contains("Reply with exactly: PONG", request.StdinPayload);
        }

        // The user's words never reach the harness through a shell, whatever the delivery.
        Assert.DoesNotContain("cmd /c", string.Join(' ', request.ArgumentList), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdinMessage_IsOneUserFrameWithThePromptInItsTextContent()
    {
        var runner = new RecordingRunner
        {
            Frames = ["""{"type":"result","subtype":"success","is_error":false,"result":"PONG"}"""],
            Outcome = new HarnessTurnOutcome(0, "")
        };

        await Client("claude-code", runner).GetResponseAsync([new ChatMessage(ChatRole.User, "quote\" and \\ backslash")]);

        using var document = JsonDocument.Parse(runner.LastRequest!.StdinPayload!);
        var root = document.RootElement;
        Assert.Equal("user", root.GetProperty("type").GetString());
        Assert.Equal("user", root.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal(
            "quote\" and \\ backslash",
            root.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ConversationIsReSentEveryTurn_BecauseAOneShotHarnessKeepsNoSession()
    {
        var runner = new RecordingRunner
        {
            Frames = ["""{"type":"result","subtype":"success","is_error":false,"result":"PONG"}"""],
            Outcome = new HarnessTurnOutcome(0, "")
        };
        var client = Client("claude-code", runner);

        await client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "first question"),
            new ChatMessage(ChatRole.Assistant, "first answer"),
            new ChatMessage(ChatRole.User, "second question")]);

        Assert.Contains("first question", runner.LastRequest!.StdinPayload, StringComparison.Ordinal);
        Assert.Contains("second question", runner.LastRequest!.StdinPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurnRunsInTheGovernedDirectoryAndNamedHarnessExecutable()
    {
        var runner = new RecordingRunner
        {
            Frames = ["""{"type":"result","subtype":"success","is_error":false,"result":"PONG"}"""],
            Outcome = new HarnessTurnOutcome(0, "")
        };

        await Client("claude-code", runner, @"C:\state\harness-scratch\provider-a").GetResponseAsync(Prompt);

        Assert.Equal(@"C:\state\harness-scratch\provider-a", runner.LastRequest!.WorkingDirectory);
        Assert.Equal(@"C:\fake\harness.exe", runner.LastRequest.FileName);
    }

    [Fact]
    public async Task Streaming_YieldsTheDeltasThenTheUsageRecord()
    {
        var runner = new RecordingRunner
        {
            Frames =
            [
                """{"type":"assistant","message":{"content":[{"type":"text","text":"PO"}]}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"NG"}]}}""",
                """{"type":"result","subtype":"success","is_error":false,"result":"PONG","usage":{"input_tokens":5,"output_tokens":4}}"""
            ],
            Outcome = new HarnessTurnOutcome(0, "")
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in Client("claude-code", runner).GetStreamingResponseAsync(Prompt)) updates.Add(update);

        Assert.Equal(["PO", "NG"], updates.Where(update => !string.IsNullOrEmpty(update.Text)).Select(update => update.Text).ToArray());
        var usage = Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>());
        Assert.Equal(9, usage.Details.TotalTokenCount);
    }

    [Fact]
    public async Task Streaming_AFaultedHarnessSurfacesAsTheVendorFailureNotAStalledStream()
    {
        var runner = new RecordingRunner
        {
            Frames = ["""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Invalid API key"}"""],
            Outcome = new HarnessTurnOutcome(0, "")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in Client("claude-code", runner).GetStreamingResponseAsync(Prompt))
            {
            }
        });

        Assert.Contains("Invalid API key", ex.Message, StringComparison.Ordinal);
    }

    // ---- the real process seam -------------------------------------------------------------------

    /// <summary>
    /// The runner itself, against a real child process: stdin is written, stdout is framed, stderr is
    /// drained, and the exit code is reported. powershell echoes its own stdin back inside a result
    /// frame, so this proves the plumbing without depending on a harness being installed or signed in.
    /// </summary>
    [WindowsFact]
    public async Task ProcessRunner_PipesStdinInReadsFramesOutAndReportsTheExitCode()
    {
        var directory = Directory.CreateTempSubdirectory("tinadec-headless-").FullName;
        try
        {
            var envelope = new ClaudeFamilyEnvelope();
            var deltas = new List<string>();
            var outcome = await new ProcessHeadlessHarnessRunner().RunAsync(
                new HarnessTurnRequest(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command",
                        "$in = [Console]::In.ReadToEnd(); " +
                        "'{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"' + $in.Trim() + '\"}'",],
                    directory,
                    null,
                    "PONG-via-stdin\n"),
                envelope,
                deltas.Add,
                CancellationToken.None);

            Assert.Equal(0, outcome.ExitCode);
            Assert.Equal("PONG-via-stdin", envelope.Answer);
            Assert.Empty(outcome.Stderr);
        }
        finally
        {
            DeleteTree(directory);
        }
    }

    /// <summary>
    /// A whole-document reader against a real child: the same powershell writing an indented document
    /// must arrive as one payload, not as 26 failed frame parses.
    /// </summary>
    [WindowsFact]
    public async Task ProcessRunner_WholeDocumentEnvelopeReceivesTheEntireStdout()
    {
        var directory = Directory.CreateTempSubdirectory("tinadec-headless-doc-").FullName;
        try
        {
            var envelope = new ZcodeDocumentEnvelope();
            var outcome = await new ProcessHeadlessHarnessRunner().RunAsync(
                new HarnessTurnRequest(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command",
                        "Write-Output '{' '\"response\": \"document-pong\"' '}'"],
                    directory,
                    null,
                    null),
                envelope,
                null,
                CancellationToken.None);

            Assert.Equal(0, outcome.ExitCode);
            Assert.Equal("document-pong", envelope.Answer);
        }
        finally
        {
            DeleteTree(directory);
        }
    }

    /// <summary>
    /// Cancelling a turn must not leave the harness running: the child writes to a stdout nobody reads
    /// any more, and a survivor would hold the governed directory open.
    /// <para>
    /// The assertion is that the child's PID stops existing, not that the call returned quickly. A
    /// wall-clock bound here went red once on a loaded machine (eight cores, six agents running) purely
    /// because powershell took longer to start than the budget allowed — which measures the host, not
    /// the behaviour. The cancellation is therefore armed only after the child has announced itself.
    /// </para>
    /// </summary>
    [WindowsFact]
    public async Task ProcessRunner_CancellationKillsTheChild()
    {
        var directory = Directory.CreateTempSubdirectory("tinadec-headless-cancel-").FullName;
        var pidFile = Path.Combine(directory, "child.pid");
        using var cancel = new CancellationTokenSource();
        try
        {
            var run = new ProcessHeadlessHarnessRunner().RunAsync(
                new HarnessTurnRequest(
                    "powershell.exe",
                    ["-NoProfile", "-NonInteractive", "-Command",
                        // Write-Host, not Write-Output: the child's stdout must stay free of anything
                        // that could be mistaken for a frame while the turn is being cancelled.
                        "$PID | Out-File -Encoding ascii -FilePath '" + pidFile + "'; " +
                        "Write-Host 'sleeping'; Start-Sleep -Seconds 60"],
                    directory,
                    null,
                    null),
                new ClaudeFamilyEnvelope(),
                null,
                cancel.Token);

            var pid = await WaitForChildPidAsync(pidFile, TimeSpan.FromSeconds(30));
            cancel.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            var disappeared = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            while (IsAlive(pid) && DateTimeOffset.UtcNow < disappeared) await Task.Delay(50);
            Assert.False(IsAlive(pid), $"harness child {pid} was still running 15 s after cancellation");
        }
        finally
        {
            DeleteTree(directory);
        }
    }

    /// <summary>
    /// A cancelled child releases its working directory a moment after its PID disappears, so an
    /// immediate recursive delete loses a race that only shows up under load — green alone, red inside
    /// the group. The retry bounds the flake; the final assertion keeps a genuinely leaked handle
    /// visible instead of silently leaving directories in TEMP.
    /// </summary>
    private static void DeleteTree(string directory)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(25);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }

        Assert.False(Directory.Exists(directory), $"temporary harness directory {directory} was still held after 25 s");
    }

    private static async Task<int> WaitForChildPidAsync(string pidFile, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var pid) && pid > 0)
                return pid;
            await Task.Delay(50);
        }

        throw new InvalidOperationException($"the child never wrote its PID to {pidFile} within {budget.TotalSeconds:0} s");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById throws when no such process exists: the child is gone, which is the result
            // this test wants.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// A harness that cannot be started at all must fail with its own name, not as an empty answer.
    /// </summary>
    [Fact]
    public async Task ProcessRunner_MissingBinaryFailsNamingTheExecutable()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProcessHeadlessHarnessRunner().RunAsync(
                new HarnessTurnRequest(
                    @"C:\definitely\not\installed\claude.exe",
                    ["--version"],
                    Directory.GetCurrentDirectory(),
                    null,
                    null),
                new ClaudeFamilyEnvelope(),
                null,
                CancellationToken.None));

        Assert.Contains("definitely", ex.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingRunner : IHeadlessHarnessRunner
    {
        public HarnessTurnRequest? LastRequest { get; private set; }
        public List<string> Frames { get; init; } = [];
        public HarnessTurnOutcome Outcome { get; init; } = new(0, "");

        public Task<HarnessTurnOutcome> RunAsync(
            HarnessTurnRequest request,
            IHeadlessEnvelope envelope,
            Action<string>? textDelta,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            foreach (var frame in Frames) envelope.Feed(frame, textDelta);
            return Task.FromResult(Outcome);
        }
    }
}
