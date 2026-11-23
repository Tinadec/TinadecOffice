using System.Text.Json;
using TinadecTools.Runtime.Sandbox;
using TinadecTools.Runtime.Sandbox.Posix;

namespace TinadecTools.Tests;

/// <summary>
/// The POSIX sandbox surface that can be asserted without a POSIX kernel: the seatbelt
/// profile text, the launcher's wire payload, the Landlock right set, and the write-target
/// calculation. The parts that need a real kernel or a real macOS runner (does Landlock
/// actually deny the write, does sandbox-exec accept this profile) are covered by the real
/// process tests in posix-core CI, not here.
/// </summary>
public sealed class PosixSandboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tinadec-posix-sandbox-" + Guid.NewGuid().ToString("N"));

    public PosixSandboxTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void BubblewrapWritableDeviceBindStaysAtTheExactNullDevice()
    {
        Assert.Equal("--dev-bind", BubblewrapLauncher.WritableBindOption("/dev/null"));
        Assert.Equal("--bind", BubblewrapLauncher.WritableBindOption("/dev"));
        Assert.Equal("--bind", BubblewrapLauncher.WritableBindOption("/tmp/granted"));
    }

    // ── seatbelt profile (macOS) ──────────────────────────────────────────────

    /// <summary>
    /// The narrowed right set that makes a non-directory grant possible. /dev/null is a character
    /// device, and landlock_add_rule refuses any directory-scoped right on it — the whole handled
    /// set therefore has to collapse to exactly write-and-truncate, no more. The values are pinned
    /// literally so widening the set (adding MAKE_REG, say) turns this red instead of turning every
    /// sandboxed Linux command into exit 126, which is what the first CI run measured.
    /// </summary>
    [Theory]
    [InlineData(1, 1UL << 1)]
    [InlineData(2, 1UL << 1)]
    [InlineData(4, (1UL << 1) | (1UL << 14))]
    public void FileScopedRights_KeepsWriteAndTruncateAndNothingDirectoryShaped(int abi, ulong expected)
    {
        Assert.Equal(expected, LandlockApi.FileScopedRights(LandlockApi.HandledAccessFs(abi)));
    }

    [Fact]
    public void SeatbeltProfile_GrantsTheDeclaredPathAndItsResolvedFormWithoutInventingOthers()
    {
        // The macOS leg measured writes denied *inside* the granted temp dir: /var is a symlink to
        // /private/var and seatbelt matches the resolved path. Both forms must be in the profile,
        // and nothing else may appear — a grant that silently grows clauses is a widening.
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);

        var profile = SeatbeltProfile.Build([workspace]);

        var clauses = profile.Split('\n').Count(line => line.Contains("(subpath ") || line.Contains("(literal "));
        Assert.True(clauses is 1 or 2, $"expected one clause per grant, or two when the path resolves elsewhere: {profile}");
        Assert.Contains($"(subpath {Quote(workspace)})", profile);
    }

    [Fact]
    public void SeatbeltProfile_AllowsEverythingThenRestrictsWritesToGrants()
    {
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);

        var profile = SeatbeltProfile.Build([workspace]);

        Assert.StartsWith("(version 1)", profile);
        Assert.Contains("(allow default)", profile);
        // The blanket deny before the grants is what makes an unlisted path unwritable;
        // without it the allow clauses below would be decoration.
        Assert.Contains("(deny file-write*)", profile);
        Assert.Contains($"(allow file-write*", profile);
        Assert.Contains($"(subpath {Quote(workspace)})", profile);
        Assert.True(profile.IndexOf("(deny file-write*)", StringComparison.Ordinal)
            < profile.IndexOf("(allow file-write*", StringComparison.Ordinal));
    }

    [Fact]
    public void SeatbeltProfile_UsesLiteralForFilesAndSubpathForDirectories()
    {
        var dir = Path.Combine(_root, "dir");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(_root, "null-ish");
        File.WriteAllText(file, string.Empty);

        var profile = SeatbeltProfile.Build([dir, file]);

        Assert.Contains($"(subpath {Quote(dir)})", profile);
        Assert.Contains($"(literal {Quote(file)})", profile);
    }

    [Fact]
    public void SeatbeltProfile_EscapesQuotesAndBackslashesSoAPathCannotBecomeSyntax()
    {
        // A workspace path is user-influenced. If `") (allow file-write*` inside a directory
        // name closed the literal, the path itself would be writing the profile.
        var tricky = Path.Combine(_root, "we\"ird\\name");
        var profile = SeatbeltProfile.Build([tricky]);

        Assert.Contains("\\\"", profile);
        Assert.Contains("\\\\", profile);
        // Exactly one allow block regardless of how the name reads.
        Assert.Equal(1, profile.Split("(allow file-write*", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void SeatbeltProfile_WithNoGrantsStaysDenyAllWrites()
    {
        var profile = SeatbeltProfile.Build([]);
        Assert.Contains("(deny file-write*)", profile);
        Assert.DoesNotContain("(allow file-write*", profile);
    }

    // ── landlock right set (Linux) ────────────────────────────────────────────

    [Fact]
    public void HandledAccessFs_ConfiguresOnlyWriteSideRights()
    {
        var v1 = LandlockApi.HandledAccessFs(1);
        // Bit 0 = EXECUTE, bits 2/3 = READ_FILE/READ_DIR: none of them may be handled,
        // because handling a right is what turns its denial on for every ungranted path.
        Assert.Equal(0UL, v1 & (1UL << 0));
        Assert.Equal(0UL, v1 & (1UL << 2));
        Assert.Equal(0UL, v1 & (1UL << 3));
        Assert.NotEqual(0UL, v1 & (1UL << 1));   // WRITE_FILE
        Assert.NotEqual(0UL, v1 & (1UL << 4));   // REMOVE_DIR
        Assert.NotEqual(0UL, v1 & (1UL << 5));   // REMOVE_FILE
        Assert.NotEqual(0UL, v1 & (1UL << 7));   // MAKE_DIR
        Assert.NotEqual(0UL, v1 & (1UL << 8));   // MAKE_REG
        Assert.Equal(0UL, v1 & (1UL << 13));     // REFER needs ABI 2
        Assert.Equal(0UL, v1 & (1UL << 14));     // TRUNCATE needs ABI 4
    }

    [Theory]
    [InlineData(2, 1UL << 13)]
    [InlineData(4, 1UL << 14)]
    public void HandledAccessFs_WidensOnlyWhenTheKernelAbiSupportsIt(int abi, ulong expectedRight)
    {
        Assert.NotEqual(0UL, LandlockApi.HandledAccessFs(abi) & expectedRight);
    }

    [Fact]
    public void HandledAccessFs_NeverHandlesDeviceOrSocketCreation()
    {
        // Those rights are deliberately left unhandled: confining them buys nothing for a
        // same-user sandbox (the user can already open the devices they can reach), and
        // denial would surface as a broken build rather than a prevented attack.
        var all = LandlockApi.HandledAccessFs(6);
        Assert.Equal(0UL, all & (1UL << 6));   // MAKE_CHAR
        Assert.Equal(0UL, all & (1UL << 9));   // MAKE_SOCK
        Assert.Equal(0UL, all & (1UL << 10));  // MAKE_FIFO
        Assert.Equal(0UL, all & (1UL << 11));  // MAKE_BLOCK
        Assert.Equal(0UL, all & (1UL << 15));  // IOCTL_DEV
    }

    // ── launcher wire payload ─────────────────────────────────────────────────

    [Fact]
    public void LinuxPayload_RoundTripsThroughTheSourceGeneratedContext_InSnakeCase()
    {
        // The launcher decodes what the parent encoded, with no reflection in between; a key
        // spelled differently is a command that silently loses its environment.
        var payload = new LinuxSandboxPayload
        {
            Executable = "/bin/sh",
            Arguments = ["-c", "echo hi"],
            WorkingDirectory = "/tmp/ws",
            Environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
            WritePaths = ["/tmp/ws"]
        };

        var json = JsonSerializer.Serialize(payload, SandboxJsonContext.Default.LinuxSandboxPayload);
        Assert.Contains("\"working_directory\"", json);
        Assert.Contains("\"write_paths\"", json);
        Assert.DoesNotContain("WorkingDirectory", json);

        var back = JsonSerializer.Deserialize(json, SandboxJsonContext.Default.LinuxSandboxPayload)!;
        Assert.Equal(payload.Executable, back.Executable);
        Assert.Equal(payload.Arguments, back.Arguments);
        Assert.Equal(payload.WorkingDirectory, back.WorkingDirectory);
        Assert.Equal(payload.WritePaths, back.WritePaths);
        Assert.Equal("/usr/bin", back.Environment["PATH"]);
    }

    [Fact]
    public void LinuxLauncherMode_IsRecognisedOnlyAsTheFirstArgument()
    {
        Assert.True(LinuxSandboxLauncher.IsMode([LinuxSandboxLauncher.ModeArg, "payload"]));
        Assert.False(LinuxSandboxLauncher.IsMode([LinuxSandboxLauncher.ModeArg]));
        Assert.False(LinuxSandboxLauncher.IsMode(["--verbose", LinuxSandboxLauncher.ModeArg]));
        Assert.False(LinuxSandboxLauncher.IsMode([]));
    }

    // ── write-target calculation (both platforms) ─────────────────────────────

    [Fact]
    public void WriteTargets_AlwaysIncludeTheScratchCacheAndTemporaryDirectory()
    {
        var temp = Path.Combine(_root, "tmp-scratch");
        Directory.CreateDirectory(temp);
        var environment = new Dictionary<string, string> { ["TMPDIR"] = temp };
        var permissions = new SandboxPermissions { WritePaths = [Path.Combine(_root, "ws")] };

        var targets = PosixSandboxBackend.WriteTargets(
            new SandboxRunnerRequest { WorkingDirectory = Path.Combine(_root, "ws") }, permissions, environment);

        // A command that cannot write its own temp files fails in a way users read as a
        // broken sandbox, so these are part of the grant set by design.
        Assert.Contains(targets, t => t.EndsWith("tinadec-sandbox-cache", StringComparison.Ordinal));
        Assert.Contains(targets, t => t.Equals(temp, StringComparison.Ordinal));
        Assert.Contains(targets, t => t.EndsWith("ws", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteTargets_DeduplicatesUnderCaseSensitivePosixComparison()
    {
        var dir = Path.Combine(_root, "grant");
        Directory.CreateDirectory(dir);
        var permissions = new SandboxPermissions { WritePaths = [dir, dir + Path.DirectorySeparatorChar, dir] };

        var targets = PosixSandboxBackend.WriteTargets(
            new SandboxRunnerRequest { WorkingDirectory = dir }, permissions, new Dictionary<string, string>());

        var grants = targets.Where(t => t.Equals(dir, StringComparison.Ordinal)).ToList();
        Assert.Single(grants);
    }

    [Fact]
    public void BubblewrapMountView_AncestorReadExceptionDoesNotCoverProtectedDirectoryBeforeRemountRo()
    {
        var baseDir = Path.Combine(_root, "app-base");
        var protectedTemp = Path.Combine(baseDir, ".tinadec", "temp");
        Directory.CreateDirectory(protectedTemp);

        var start = new System.Diagnostics.ProcessStartInfo("bwrap");
        var permissions = new SandboxPermissions
        {
            WritePaths = [baseDir],
            ProtectedPaths = [protectedTemp],
            ReadExceptions = [baseDir + Path.DirectorySeparatorChar]
        };

        BubblewrapLauncher.AddMountView(start, permissions, [baseDir]);

        var args = start.ArgumentList.ToList();
        var roBindBase = IndexOfSequence(args, "--ro-bind", baseDir, baseDir);
        var tmpfsTemp = IndexOfSequence(args, "--tmpfs", protectedTemp);
        var remountTemp = IndexOfSequence(args, "--remount-ro", protectedTemp);
        Assert.True(roBindBase >= 0, $"expected normalized ancestor --ro-bind in: {string.Join(' ', args)}");
        Assert.True(tmpfsTemp > roBindBase, "ancestor --ro-bind must come before child --tmpfs so --remount-ro hits the tmpfs mountpoint");
        Assert.True(remountTemp > tmpfsTemp, "--remount-ro must come after --tmpfs");
    }

    private static int IndexOfSequence(IReadOnlyList<string> items, params string[] sequence)
    {
        for (var i = 0; i <= items.Count - sequence.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < sequence.Length; j++)
            {
                if (!string.Equals(items[i + j], sequence[j], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }
            if (matched) return i;
        }
        return -1;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
