using System.Text;
using System.Text.Json;
using TinadecTools.Tools.Browser;

namespace TinadecTools.Tests;

public sealed class EgoLiteConnectorTests : IDisposable
{
    private readonly string _tempDir;

    public EgoLiteConnectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tinadec-ego-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private sealed class RecordingEgoRunner : IEgoLiteProcessRunner
    {
        public sealed record Invocation(string CliPath, string[] Arguments, string? ImportedScriptPath, byte[]? ScriptBytes, string? ScriptText);

        public List<Invocation> Invocations { get; } = new();
        public Func<Invocation, EgoProcessResult> Handler { get; set; } =
            _ => new EgoProcessResult(0, EgoLiteBrowserConnector.ResultSentinel + """{"ok":true,"space_id":"7","active_page":"p1"}""", "");

        public Task<EgoProcessResult> ExecuteAsync(
            string cliPath,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            string? scriptPath = null;
            byte[]? bytes = null;
            string? text = null;

            if (arguments.Count >= 3 && arguments[0] == "nodejs" && arguments[1] == "-e")
            {
                var expr = arguments[2];
                const string prefix = "await import('";
                const string suffix = "')";
                if (expr.StartsWith(prefix, StringComparison.Ordinal) && expr.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var uriText = expr[prefix.Length..^suffix.Length];
                    if (Uri.TryCreate(uriText, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
                    {
                        scriptPath = fileUri.LocalPath;
                        if (File.Exists(scriptPath))
                        {
                            bytes = File.ReadAllBytes(scriptPath);
                            text = Encoding.UTF8.GetString(bytes);
                        }
                    }
                }
            }

            var invocation = new Invocation(cliPath, arguments.ToArray(), scriptPath, bytes, text);
            Invocations.Add(invocation);
            return Task.FromResult(Handler(invocation));
        }
    }

    private (EgoLiteBrowserConnector Connector, RecordingEgoRunner Runner, string FakeCliPath) CreateConnector()
    {
        var fakeCli = Path.Combine(_tempDir, "bin", "ego-browser.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeCli)!);
        File.WriteAllText(fakeCli, "stub");

        var runner = new RecordingEgoRunner();
        var env = new EgoLiteDiscoveryEnvironment
        {
            IsWindows = true,
            IsMacOS = false,
            FileExists = File.Exists,
            GetEnvironmentVariable = name => name == "EGO_BROWSER_CLI" ? fakeCli : null,
        };
        var connector = new EgoLiteBrowserConnector(
            runner,
            env,
            new BrowserNavigationPolicy(allowPrivate: true),
            Path.Combine(_tempDir, "rounds"));
        return (connector, runner, fakeCli);
    }

    [Fact]
    public void DiscoverCliPath_FindsMacOsAppBundleAndWindowsInstallLocations()
    {
        var macFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            EgoLiteBrowserConnector.MacOsDefaultBundleHelper,
        };
        var macEnv = new EgoLiteDiscoveryEnvironment
        {
            IsMacOS = true,
            IsWindows = false,
            FileExists = macFiles.Contains,
            GetDirectories = _ => Array.Empty<string>(),
            GetEnvironmentVariable = _ => null,
            UserHome = "/Users/alice",
        };
        Assert.Equal(EgoLiteBrowserConnector.MacOsDefaultBundleHelper, EgoLiteBrowserConnector.DiscoverCliPath(macEnv));

        var winExe = Path.Combine(@"C:\Users\alice\AppData\Local", "ego lite", "Application", "ego-browser.exe");
        var winEnv = new EgoLiteDiscoveryEnvironment
        {
            IsWindows = true,
            IsMacOS = false,
            FileExists = p => string.Equals(p, winExe, StringComparison.OrdinalIgnoreCase),
            GetEnvironmentVariable = _ => null,
            LocalAppData = @"C:\Users\alice\AppData\Local",
            UserHome = @"C:\Users\alice",
            ProgramFiles = @"C:\Program Files",
        };
        Assert.Equal(winExe, EgoLiteBrowserConnector.DiscoverCliPath(winEnv));
    }

    [Fact]
    public async Task MissingCli_ReportsCleanUnavailableResult()
    {
        var env = new EgoLiteDiscoveryEnvironment
        {
            IsWindows = true,
            IsMacOS = false,
            FileExists = _ => false,
            GetEnvironmentVariable = _ => null,
            LocalAppData = string.Empty,
            UserHome = string.Empty,
            ProgramFiles = string.Empty,
        };
        var connector = new EgoLiteBrowserConnector(new RecordingEgoRunner(), env, roundsBaseDirectory: _tempDir);
        var ctx = BrowserConnectorContext.Resolve("s1", driverOverride: "ego_lite");

        var res = await connector.ProfilesListAsync(ctx, new BrowserProfilesListArgs { Driver = "ego_lite" }, CancellationToken.None);
        Assert.False(res.Ok);
        Assert.Equal("ego_browser_unavailable", res.Code);
        Assert.False(res.EgoAvailable);
    }

    [Fact]
    public async Task Rounds_StrictlyFollowWindowsUtf8MjsModuleRuleWithForwardSlashesAndZeroNonAsciiInEval()
    {
        var (connector, runner, fakeCli) = CreateConnector();
        runner.Handler = inv => new EgoProcessResult(
            0,
            EgoLiteBrowserConnector.ResultSentinel + """{"ok":true,"space_id":"12","active_page":"p1","url":"https://example.com/","title":"中文标题"}""",
            "");

        var ctx = BrowserConnectorContext.Resolve("s-utf8", driverOverride: "ego_lite", profileIdOverride: "Profile 2");
        var nav = await connector.NavigateAsync(ctx, new BrowserNavigateArgs { Url = "https://example.com/" }, CancellationToken.None);
        Assert.True(nav.Ok, nav.Message);
        Assert.Equal("12", nav.SpaceId);

        // Second round with Chinese text to verify round-02.mjs naming and UTF-8 no-BOM encoding!
        var typed = await connector.TypeAsync(ctx, new BrowserTypeArgs { Ref = 3, Text = "你好世界 笔记本电脑" }, CancellationToken.None);
        Assert.True(typed.Ok, typed.Message);

        Assert.Equal(2, runner.Invocations.Count);

        // Verify Round 1 & Round 2 .mjs naming, UTF-8 without BOM, forward slashes, single-line pure-ASCII -e!
        var round1 = runner.Invocations[0];
        var round2 = runner.Invocations[1];
        Assert.Equal(fakeCli, round1.CliPath);
        Assert.Equal(["nodejs", "-e"], round1.Arguments[..2]);
        Assert.StartsWith("await import('file:///", round1.Arguments[2], StringComparison.Ordinal);
        Assert.DoesNotContain("\\", round1.Arguments[2], StringComparison.Ordinal);
        Assert.DoesNotContain("\n", round1.Arguments[2], StringComparison.Ordinal);
        Assert.True(round1.Arguments[2].All(ch => ch <= 127), "-e argument must be pure ASCII");

        Assert.EndsWith("round-01.mjs", round1.ImportedScriptPath!, StringComparison.Ordinal);
        Assert.EndsWith("round-02.mjs", round2.ImportedScriptPath!, StringComparison.Ordinal);

        // Must NOT have a UTF-8 BOM (EF BB BF).
        Assert.False(round2.ScriptBytes!.Length >= 3
                     && round2.ScriptBytes[0] == 0xEF
                     && round2.ScriptBytes[1] == 0xBB
                     && round2.ScriptBytes[2] == 0xBF);
        // Non-ASCII text lives inside the UTF-8 .mjs module, intact!
        Assert.Contains("你好世界 笔记本电脑", round2.ScriptText);
        Assert.Contains("Profile 2", round1.ScriptText);
    }

    [Fact]
    public async Task ProfilesRouter_CombinesBuiltinElectronAndEgoLiteProfiles()
    {
        var (egoConnector, runner, _) = CreateConnector();
        runner.Handler = _ => new EgoProcessResult(
            0,
            EgoLiteBrowserConnector.ResultSentinel + """{"ok":true,"profiles":[{"id":"Default","name":"Personal","isDefault":true},{"id":"Profile 2","name":"Work","isDefault":false}]}""",
            "");

        var fakeBridge = new MinimalElectronBridge();
        var builtinConnector = new BuiltinElectronBrowserConnector(new BrowserToolRuntime(fakeBridge, new BrowserNavigationPolicy()));
        var router = new BrowserConnectorRouter(builtinConnector, egoConnector);

        var ctx = BrowserConnectorContext.Resolve("s-prof");
        var all = await router.ProfilesListAsync(ctx, new BrowserProfilesListArgs(), CancellationToken.None);

        Assert.True(all.Ok);
        Assert.True(all.EgoAvailable);
        Assert.Equal(3, all.Profiles!.Length);
        Assert.Contains(all.Profiles, p => p.Driver == "builtin_electron" && p.Id == BuiltinElectronBrowserConnector.BuiltinProfileId);
        Assert.Contains(all.Profiles, p => p.Driver == "ego_lite" && p.Id == "Profile 2" && p.Name == "Work");
    }

    [Fact]
    public async Task TaskSpaceLifecycle_CreateClaimHandoffTakeoverFinish_AndRefuseFinishedTakeover()
    {
        var (connector, runner, _) = CreateConnector();
        var ctx = BrowserConnectorContext.Resolve("s-life", driverOverride: "ego_lite");

        // 1. Create TaskSpace
        var created = await connector.CreateTaskSpaceAsync(ctx, "checkout-flow", "Profile 2", CancellationToken.None);
        Assert.True(created.Ok, created.Message);
        Assert.Equal("7", created.SpaceId);

        // 2. HandOff
        var handedOff = await connector.HandOffAsync(ctx, new BrowserHandoffArgs { Reason = "captcha" }, CancellationToken.None);
        Assert.True(handedOff.Ok);
        Assert.Equal("browser_handoff_requested", handedOff.Code);
        Assert.Equal("user", handedOff.Control);

        // Blocked while user controls space
        var blocked = await connector.SnapshotAsync(ctx, new BrowserSnapshotArgs(), CancellationToken.None);
        Assert.False(blocked.Ok);
        Assert.Equal("browser_user_in_control", blocked.Code);

        // 3. TakeOver
        runner.Handler = _ => new EgoProcessResult(
            0,
            EgoLiteBrowserConnector.ResultSentinel + """{"ok":true,"space_id":"7","active_page":"p1","tabs":[{"label":"p1","url":"https://shop.test/done","title":"Done","active":true}]}""",
            "");
        var resumed = await connector.TakeOverAsync(ctx, new BrowserTakeoverArgs(), CancellationToken.None);
        Assert.True(resumed.Ok, resumed.Message);
        Assert.Equal("browser_control_resumed", resumed.Code);
        Assert.Equal("agent", resumed.Control);

        // 4. Finish({ keep: ["p1"] })
        runner.Handler = _ => new EgoProcessResult(
            0,
            EgoLiteBrowserConnector.ResultSentinel + """{"ok":true,"space_id":"7","kept_tabs":["p1"],"closed_tabs":["p2"]}""",
            "");
        var finished = await connector.FinishAsync(ctx, new BrowserFinishArgs { Keep = ["p1"] }, CancellationToken.None);
        Assert.True(finished.Ok, finished.Message);
        Assert.Equal("browser_task_space_completed", finished.Code);
        Assert.Equal("finished", finished.Control);
        Assert.Contains("p1", finished.KeptTabs!);
        Assert.Contains("p2", finished.ClosedTabs!);

        // 5. TakeOver on finished space MUST be refused cleanly per ego-errors.ts!
        var refused = await connector.TakeOverAsync(ctx, new BrowserTakeoverArgs(), CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal("browser_task_space_finished", refused.Code);
        Assert.True(refused.Permanent);
        Assert.Contains("EGO_TASK_SPACE_INACTIVE", refused.Message);

        // 6. Explicit user-approved ClaimTaskSpace CAN reclaim kept tabs when requested
        runner.Handler = _ => new EgoProcessResult(
            0,
            EgoLiteBrowserConnector.ResultSentinel + """{"ok":true,"space_id":"7","active_page":"p1","tabs":[{"label":"p1","url":"https://shop.test/done","title":"Done","active":true}]}""",
            "");
        var claimed = await connector.TakeOverAsync(ctx, new BrowserTakeoverArgs { ClaimUserSpace = true }, CancellationToken.None);
        Assert.True(claimed.Ok, claimed.Message);
        Assert.Equal("browser_task_space_claimed", claimed.Code);
    }

    [Fact]
    public async Task RunWorkflow_EnforcesNavigationPolicyAndRunsSingleRoundMjsWithMultimodalScreenshot()
    {
        var (connector, runner, _) = CreateConnector();
        var ctx = BrowserConnectorContext.Resolve("s-ego-run", driverOverride: "ego_lite");

        // Blocked URL in step 1 -> 0 .mjs invocations!
        var blocked = await connector.RunAsync(ctx, new BrowserRunArgs
        {
            Steps =
            [
                new BrowserRunStep { Action = "goto", Url = "https://example.com/" },
                new BrowserRunStep { Action = "goto", Url = "file:///C:/Windows/win.ini" },
            ],
        }, CancellationToken.None);
        Assert.False(blocked.Ok);
        Assert.Equal("browser_navigation_blocked", blocked.Code);
        Assert.Empty(runner.Invocations);

        // Valid single-round workflow
        var shotFile = Path.Combine(_tempDir, "ego-final.png");
        await File.WriteAllBytesAsync(shotFile, [137, 80, 78, 71]);
        runner.Handler = _ => new EgoProcessResult(
            0,
            EgoLiteBrowserConnector.ResultSentinel + JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["space_id"] = "9",
                ["active_page"] = "p1",
                ["url"] = "https://example.com/account",
                ["title"] = "Account",
                ["snapshot"] = "root\n  button \"Logout\" [ref=5, loc=ambiguous]",
                ["screenshot_path"] = shotFile,
                ["run_steps"] = new object[]
                {
                    new Dictionary<string, object?> { ["index"] = 0, ["action"] = "goto", ["ok"] = true, ["url"] = "https://example.com/login" },
                    new Dictionary<string, object?> { ["index"] = 1, ["action"] = "fill", ["ok"] = true },
                    new Dictionary<string, object?> { ["index"] = 2, ["action"] = "click", ["ok"] = true },
                },
                ["extractions"] = new object[]
                {
                    new Dictionary<string, object?> { ["name"] = "header", ["selector"] = "h1", ["value"] = "Welcome" },
                },
            }),
            "[ego-browser:notice] Ego Lite update available");

        var result = await connector.RunAsync(ctx, new BrowserRunArgs
        {
            Steps =
            [
                new BrowserRunStep { Action = "goto", Url = "https://example.com/login" },
                new BrowserRunStep { Action = "fill", Selector = "loc=role:textbox[name='Email']", Text = "a@b.com" },
                new BrowserRunStep { Action = "click", Selector = "loc=role:button[name='Sign in']" },
            ],
            IncludeSnapshot = true,
            IncludeScreenshot = true,
        }, CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Single(runner.Invocations);
        Assert.Equal(3, result.RunSteps!.Length);
        Assert.Equal("Welcome", result.Extractions![0].Value);
        Assert.DoesNotContain("loc=ambiguous", result.Snapshot);
        Assert.Contains("[ref=5]", result.Snapshot);
        Assert.Equal(shotFile, result.ScreenshotPath);
        Assert.Contains(result.Notices!, n => n.Code == "ego_update_available");
    }

    private sealed class MinimalElectronBridge : IBrowserBridge
    {
        public ValueTask<BrowserBridgeResponse> SendAsync(BrowserBridgeCall call, TimeSpan timeout, CancellationToken cancellationToken) =>
            new(BrowserBridgeResponse.Failure("browser_host_unavailable", "offline"));
    }
}
