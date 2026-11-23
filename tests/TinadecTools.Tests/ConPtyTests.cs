using System.Diagnostics;
using System.Text;
using TinadecTools.Runtime.Pty;

namespace TinadecTools.Tests;

/// <summary>
/// ConPTY is the only PTY backend in this build, so these run on Windows and report as skipped — not
/// passed — everywhere else. <see cref="PtyBackendContractTests"/> is the half that runs on every
/// platform, including the refusal a POSIX host gets until the <c>openpty</c> backend lands.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "ConPTY is the only PTY backend in this build";
        }
    }
}

/// <summary>
/// Tests that spawn a real child on a real terminal, so they share one collection and do not run
/// concurrently: this machine already carries the developer's other work, and a dozen conhosts asking
/// for CPU at once turns a slow assertion into a false failure.
/// </summary>
[CollectionDefinition("PTY", DisableParallelization = true)]
public abstract class PtyCollection;

internal static class PtyHarness
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Reads raw bytes until the marker appears. The contract of this backend is a byte stream, not text:
    /// a full-screen program paints control sequences that a line reader would either eat or split.
    /// A timeout reports everything the stream did produce, because "the child printed nothing" and "the
    /// child printed something unexpected" are different bugs to fix.
    /// </summary>
    internal static async Task<string> ReadUntilAsync(Stream output, string marker)
    {
        var seen = new MemoryStream();
        var buffer = new byte[8192];
        using var deadline = new CancellationTokenSource(Budget);
        try
        {
            while (true)
            {
                var read = await output.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                seen.Write(buffer, 0, read);
                if (Decode(seen).Contains(marker, StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        var text = Decode(seen);
        if (!text.Contains(marker, StringComparison.Ordinal))
        {
            throw new TimeoutException($"the PTY produced {seen.Length} byte(s) in {Budget.TotalSeconds}s but never '{marker}'. Seen so far: {Show(text)}");
        }

        return text;
    }

    internal static PtyStartRequest Run(string executable, params string[] arguments)
        => new(executable, arguments, Path.GetTempPath(), null, Columns: 137, Rows: 45);

    /// <summary>An interactive shell, because driving a TUI means sending keystrokes to a live prompt.</summary>
    internal static PtyStartRequest InteractiveShell(int columns = 90, int rows = 30)
        => new("cmd.exe", ["/d"], Path.GetTempPath(), null, Columns: columns, Rows: rows);

    internal static async Task WriteLineAsync(IPtySession session, string line)
    {
        await session.Input.WriteAsync(Encoding.ASCII.GetBytes(line + "\r")).ConfigureAwait(false);
        await session.Input.FlushAsync().ConfigureAwait(false);
    }

    private static string Decode(MemoryStream seen) => Encoding.UTF8.GetString(seen.ToArray());

    private static string Show(string text)
    {
        var escaped = text.Replace("\x1b", "ESC").Replace("\r", "\\r").Replace("\n", "\\n");
        return escaped.Length > 2000 ? escaped[..2000] : escaped;
    }
}

public sealed class PtyBackendContractTests
{
    [Fact]
    public void IsSupported_MatchesTheBackendsThatActuallyExist()
        => Assert.Equal(OperatingSystem.IsWindows(), PtyBackend.IsSupported);

    [Fact]
    public void Start_OnAPlatformWithoutABackend_RefusesRatherThanFakingATerminal()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var failure = Assert.Throws<PlatformNotSupportedException>(() => PtyBackend.Start(PtyHarness.Run("node", ["--version"])));
        Assert.Contains("pipes", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public void Start_RejectsAScreenTooSmallToHoldAPrompt()
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => PtyBackend.Start(
            new PtyStartRequest("node", ["--version"], Path.GetTempPath(), null, Columns: 1, Rows: 40)));
        Assert.Contains("2x2", failure.Message, StringComparison.Ordinal);
    }
}

[Collection("PTY")]
public sealed class ConPtySessionTests
{
    [WindowsFact]
    public async Task Output_IsTheChildsOwnBytes()
    {
        using var session = PtyBackend.Start(PtyHarness.Run("node", ["-p", "'hello-from-the-pty'"]));
        await PtyHarness.ReadUntilAsync(session.Output, "hello-from-the-pty");
    }

    [WindowsFact]
    public async Task Output_KeepsTheEscapeSequencesTheChildPaints()
    {
        using var session = PtyBackend.Start(PtyHarness.Run("node", ["-e", "process.stdout.write('\\x1b[31mRED-CODES\\x1b[0m')"]));
        var text = await PtyHarness.ReadUntilAsync(session.Output, "RED-CODES");
        Assert.Contains("\x1b[31m", text, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task SizeAtCreation_IsTheSizeTheChildIsAbleToAskFor()
    {
        // A program that asks the console for its size is the only witness that matters: over pipes the
        // same question answers "not a terminal", which is the defect this backend exists to remove.
        using var session = PtyBackend.Start(PtyHarness.Run("node", ["-p", "'W'+process.stdout.columns+'R'+process.stdout.rows"]));
        await PtyHarness.ReadUntilAsync(session.Output, "W137R45");
    }

    [WindowsFact]
    public async Task Resize_ChangesTheSizeTheNextProgramOnTheSameTerminalSees()
    {
        using var session = PtyBackend.Start(PtyHarness.InteractiveShell());
        const string probe = "node -p \"'W'+process.stdout.columns\"";

        await PtyHarness.WriteLineAsync(session, probe);
        var before = await PtyHarness.ReadUntilAsync(session.Output, "W90");

        session.Resize(140, 44);
        await PtyHarness.WriteLineAsync(session, probe);
        var after = await PtyHarness.ReadUntilAsync(session.Output, "W140");

        Assert.DoesNotContain("W140", before, StringComparison.Ordinal);
        Assert.Contains("W140", after, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Resize_RejectsASizeTheTerminalCannotAddress()
    {
        using var session = PtyBackend.Start(PtyHarness.InteractiveShell());
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => session.Resize(70_000, 30));
        Assert.Contains("70000", failure.Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task Input_ReachesTheChildAsKeystrokesAndComesBackAsOutput()
    {
        using var session = PtyBackend.Start(PtyHarness.InteractiveShell());
        await PtyHarness.WriteLineAsync(session, "echo typed-into-the-pty");
        await PtyHarness.ReadUntilAsync(session.Output, "typed-into-the-pty");
    }

    [WindowsFact]
    public void ExitCode_IsTheNumberTheChildExitedWith()
    {
        using var session = PtyBackend.Start(PtyHarness.Run("cmd.exe", ["/d", "/c", "exit 3"]));
        Assert.True(session.WaitForExit(TimeSpan.FromSeconds(45)), "the PTY child never exited");
        Assert.True(session.HasExited);
        Assert.Equal(3, session.ExitCode);
    }

    [WindowsFact]
    public void HasExited_IsNotGuessedFromOneParticularExitCode()
    {
        // 259 is the value GetExitCodeProcess reports for a process that is still running, so a check
        // built on it reads a child that exits 259 on purpose as alive forever.
        using var session = PtyBackend.Start(PtyHarness.Run("cmd.exe", ["/d", "/c", "exit 259"]));
        Assert.True(session.WaitForExit(TimeSpan.FromSeconds(45)), "the PTY child never exited");
        Assert.Equal(259, session.ExitCode);
        Assert.True(session.HasExited);
    }

    [WindowsFact]
    public void WaitForExit_IsFalseWhileTheChildIsStillRunning()
    {
        using var session = PtyBackend.Start(PtyHarness.Run("node", ["-e", "setTimeout(()=>{},60000)"]));
        Assert.False(session.WaitForExit(TimeSpan.FromMilliseconds(300)));
        Assert.False(session.HasExited);
    }

    [WindowsFact]
    public void Kill_TerminatesTheChild()
    {
        using var session = PtyBackend.Start(PtyHarness.Run("node", ["-e", "setTimeout(()=>{},60000)"]));
        session.Kill();
        Assert.True(session.WaitForExit(TimeSpan.FromSeconds(20)), "the PTY child survived Kill()");
    }

    [WindowsFact]
    public void Dispose_TerminatesAChildThatIsStillRunning()
    {
        var session = PtyBackend.Start(PtyHarness.Run("node", ["-e", "setTimeout(()=>{},60000)"]));
        var processId = session.ProcessId;
        session.Dispose();

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (!IsRunning(processId))
            {
                return;
            }

            Thread.Sleep(100);
        }

        Assert.Fail($"process {processId} was still alive 20s after its PTY session was disposed");
    }

    [WindowsFact]
    public async Task Environment_OverridesAreMergedOntoTheParentsOwn()
    {
        // The child asks for two things at once: the variable the caller named, and PATH, which the
        // caller never named. A block holding only the first is how a TUI starts with no PATH and fails
        // for a reason nobody can see in its arguments.
        var environment = new Dictionary<string, string?> { ["TINADEC_PTY_PROBE"] = "pty-env-ok" };
        using var session = PtyBackend.Start(new PtyStartRequest(
            "node",
            ["-p", "process.env.TINADEC_PTY_PROBE+'|'+(process.env.PATH?'path-present':'path-missing')"],
            Path.GetTempPath(),
            environment));

        await PtyHarness.ReadUntilAsync(session.Output, "pty-env-ok|path-present");
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

[Collection("PTY")]
public sealed class Win32CommandLineTests
{
    [Fact]
    public void Build_QuotesTheExecutableSoAPathWithSpacesSurvives()
        => Assert.Equal("\"C:\\Program Files\\node\\node.exe\" -p 1", Win32CommandLine.Build("C:\\Program Files\\node\\node.exe", ["-p", "1"]));

    [Fact]
    public void Build_QuotesOnlyTheExecutableAndAnythingWindowsWouldSplitOn()
        => Assert.Equal("\"rg\" --files", Win32CommandLine.Build("rg", ["--files"]));

    [Fact]
    public void Build_QuotesAnArgumentCarryingItsOwnSpaces()
        => Assert.Equal("\"node\" \"a b\"", Win32CommandLine.Build("node", ["a b"]));

    [Fact]
    public void Build_KeepsAnEmptyArgumentInItsOwnPosition()
        => Assert.Equal("\"node\" \"\" x", Win32CommandLine.Build("node", ["", "x"]));

    [Fact]
    public void Build_EscapesAQuoteAndDoublesTheBackslashesAheadOfIt()
        => Assert.Equal("\"node\" \"He said \\\"hi\\\"\"", Win32CommandLine.Build("node", ["He said \"hi\""]));

    [Fact]
    public void Build_DoublesATrailingBackslashRunSoTheClosingQuoteKeepsItsMeaning()
        => Assert.Equal("\"node\" \"C:\\dir\\\\\"", Win32CommandLine.Build("node", ["C:\\dir\\"]));

    [Fact]
    public void Build_LeavesABackslashAloneWhenItGuardsNothing()
        => Assert.Equal("\"node\" \"a\\b\"", Win32CommandLine.Build("node", ["a\\b"]));

    [Fact]
    public void Build_LeavesCmdsOwnMetacharactersUnquotedBecauseThisLayerCannotProtectThem()
    {
        // MSVCRT's tokenizer treats & | < > ^ as ordinary characters, so quoting them would be a lie
        // about what this layer does. cmd.exe re-parses them after that tokenizer, which is why a caller
        // that has a choice starts the program itself rather than putting a shell in front of it.
        Assert.Equal("\"cmd.exe\" a&b|c", Win32CommandLine.Build("cmd.exe", ["a&b|c"]));
    }

    [WindowsFact]
    public async Task Build_WhenWindowsParsesIt_GivesTheChildTheArgumentItStartedWith()
    {
        // The assertions above check the string this code wrote. This one checks the argument the child
        // received, which is the only thing the quoting rules are for.
        var sentinel = "a b\"c&d|e^f%VAR%\\g\"h\\";
        using var session = PtyBackend.Start(PtyHarness.Run(
            "node",
            ["-e", "process.stdout.write('<'+process.argv[1]+'>')", sentinel]));

        var text = await PtyHarness.ReadUntilAsync(session.Output, ">");
        var received = text[(text.LastIndexOf('<') + 1)..text.LastIndexOf('>')];
        Assert.Equal(sentinel, received);
    }
}
