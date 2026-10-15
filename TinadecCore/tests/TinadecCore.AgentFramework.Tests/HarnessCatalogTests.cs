using TinadecCore.Abstractions.Ports;

namespace TinadecCore.AgentFramework.Tests;

/// <summary>
/// Pins the channel vocabulary and the harness catalog against the specific fabrications that made
/// the ACP channel unconnectable: driver names that implied a channel, a protocol inferred from a
/// driver string, and an invented port flag handed to binaries that never mentioned one.
/// </summary>
public sealed class HarnessCatalogTests
{
    private static readonly string[] KnownProtocols =
    [
        ChatProtocols.OpenAiChat,
        ChatProtocols.OpenAiResponses,
        ChatProtocols.AnthropicMessages,
        ChatProtocols.Acp,
        ChatProtocols.OpencodeServe,
        ChatProtocols.HeadlessCli,
        ChatProtocols.Tui
    ];

    [Fact]
    public void Catalog_EveryChannelIsKnownAndEveryProtocolIsDeclared()
    {
        foreach (var spec in HarnessCatalog.All)
        {
            Assert.NotEmpty(spec.Channels);
            Assert.NotNull(spec.Id);
            Assert.NotEmpty(spec.BinaryNames);
            Assert.StartsWith("https://", spec.DocsUrl, StringComparison.Ordinal);
            foreach (var channel in spec.Channels)
            {
                Assert.Contains(channel.Channel, AgentChannels.All);
                Assert.Contains(channel.Protocol, KnownProtocols);
            }

            if (spec.HttpServer is { } http) Assert.Contains(http.Protocol, KnownProtocols);
            if (spec.HttpServer is null) Assert.Contains(spec.DefaultProtocol, KnownProtocols);

            // A protocol may not appear twice on one harness: it would make the (harness, channel)
            // lookup ambiguous, which is the exact coupling this vocabulary removes.
            Assert.Equal(spec.Channels.Select(channel => channel.Protocol).Distinct().Count(), spec.Channels.Count);
        }
    }

    /// <summary>
    /// The guard that makes the replacement irreversible. <c>claude --help</c> and
    /// <c>codex --help</c> on this host mention ACP zero times, and ZCode's own CLI ships an
    /// app-server rather than an ACP endpoint. Re-advertising acp for any of them re-creates the
    /// fabrication this batch deleted.
    /// </summary>
    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex")]
    [InlineData("zcode")]
    public void HarnessesWithoutAnAcpEndpoint_DeclareNoAcpChannel(string id)
    {
        var spec = HarnessCatalog.Find(id);
        Assert.NotNull(spec);
        Assert.DoesNotContain(AgentChannels.Acp, spec.Channels.Select(channel => channel.Channel));
        Assert.Null(spec.Channel(AgentChannels.Acp));
        Assert.NotEqual(ChatProtocols.Acp, HarnessCatalog.ProtocolFor(id, null));
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("cursor")]
    [InlineData("codebuddy")]
    [InlineData("dsh")]
    [InlineData("kimi-code")]
    public void AcpNativeHarnesses_ResolveAcpForTheirChannel(string id)
        => Assert.Equal(ChatProtocols.Acp, HarnessCatalog.ProtocolFor(id, AgentChannels.Acp));

    /// <summary>Cursor's real ACP entry point is the <c>acp</c> subcommand, not a port flag.</summary>
    [Fact]
    public void Cursor_AcpChannelUsesTheSubcommandRatherThanAnInjectedPort()
    {
        var channel = HarnessCatalog.Find("cursor")!.Channel(AgentChannels.Acp);
        Assert.NotNull(channel);
        Assert.Equal(["acp"], channel.Argv);
        Assert.Empty(channel.MandatoryArgs);
    }

    /// <summary>
    /// CodeBuddy's non-interactive run stops on a permission prompt unless <c>-y</c> is present, so
    /// a probe without it reads as an environment problem rather than a missing flag. The flag is
    /// per-channel: injecting it into <c>codebuddy --acp</c> would be unverified behavior.
    /// </summary>
    [Fact]
    public void Codebuddy_HeadlessChannelCarriesTheNonInteractiveFlag()
    {
        var spec = HarnessCatalog.Find("codebuddy")!;
        Assert.Equal(["--acp"], spec.Channel(AgentChannels.Acp)!.Argv);
        Assert.Empty(spec.Channel(AgentChannels.Acp)!.MandatoryArgs);
        Assert.Equal(["-y"], spec.Channel(AgentChannels.Cli)!.MandatoryArgs);
        // The flag has to reach the process, and MandatoryArgs had no consumer until the headless
        // client materialized argv — this is where that promise is pinned.
        Assert.Contains("-y", HarnessCatalog.MaterializeChannelArgv("codebuddy", AgentChannels.Cli, "hello"));
        Assert.Equal(HarnessPromptDeliveries.StdinMessage, spec.Channel(AgentChannels.Cli)!.PromptDelivery);
        Assert.DoesNotContain(HarnessCatalog.PromptPlaceholder, spec.Channel(AgentChannels.Cli)!.Argv);
    }

    /// <summary>
    /// CodeBuddy's <c>--help</c> POSTs telemetry to galileotelemetry.tencent.com before printing, so
    /// it hangs or fails offline. A version probe that shells out to it turns discovery into a
    /// network request; the tolerance covers the offline non-zero exit.
    /// </summary>
    [Fact]
    public void Codebuddy_VersionProbeRefusesTheTelemetryBearingHelpFlag()
    {
        var probe = HarnessCatalog.Find("codebuddy")!.VersionProbe;
        Assert.Contains("--help", probe.NeverUse);
        Assert.DoesNotContain("--help", probe.Argv);
        Assert.True(probe.TolerateNonZeroExit);
    }

    [Fact]
    public void BundledBinaries_AreSearchedBeforePathForTheHarnessesThatShipOutsideIt()
    {
        // Verified on this host: codebuddy lives under WorkBuddy's unpacked resources, kimi under
        // ~/.kimi-code/bin, and neither is on PATH. A stale global npm copy must not win.
        foreach (var id in new[] { "codebuddy", "kimi-code", "cursor" })
        {
            Assert.NotEmpty(HarnessCatalog.Find(id)!.ExtraSearchRoots);
        }

        Assert.Contains("{ProgramFiles}", HarnessCatalog.Find("codebuddy")!.ExtraSearchRoots.Single(root => root.Contains("ProgramFiles", StringComparison.Ordinal)));
        // Tokens, not expanded paths: expansion happens at resolution time so tests can substitute roots.
        foreach (var spec in HarnessCatalog.All)
        {
            foreach (var root in spec.ExtraSearchRoots)
            {
                Assert.DoesNotContain(@"C:\", root, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("/home/", root, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// <c>opencode</c> is the one path that worked before this vocabulary existed, so a stored row
    /// naming that driver must keep resolving to the HTTP server protocol with no channel recorded.
    /// </summary>
    [Fact]
    public void LegacyOpencodeRow_WithoutChannel_StillResolvesTheHttpServerProtocol()
        => Assert.Equal(ChatProtocols.OpencodeServe, HarnessCatalog.ResolveProtocol(null, "opencode", null));

    [Fact]
    public void ResolveProtocol_ConfiguredValueAlwaysWins()
    {
        Assert.Equal(ChatProtocols.AnthropicMessages, HarnessCatalog.ResolveProtocol("anthropic-messages", "opencode", "acp"));
        Assert.Equal(ChatProtocols.Acp, HarnessCatalog.ResolveProtocol(null, "opencode", AgentChannels.Acp));
    }

    [Fact]
    public void ResolveProtocol_HttpApiDrivers_StillComeFromTheDriverMap()
    {
        Assert.Equal(ChatProtocols.AnthropicMessages, HarnessCatalog.ResolveProtocol(null, "anthropic", null));
        Assert.Equal(ChatProtocols.AnthropicMessages, HarnessCatalog.ResolveProtocol(null, "claude", null));
        Assert.Equal(ChatProtocols.OpenAiResponses, HarnessCatalog.ResolveProtocol(null, "openai-responses", null));
        Assert.Equal(ChatProtocols.OpenAiChat, HarnessCatalog.ResolveProtocol(null, "openai", null));
    }

    /// <summary>
    /// The driver half of the old inference mapped all three CLI drivers to acp. They are not catalog
    /// ids, so the only honest answer left is the default — and the model center shows such a row as
    /// an unrecognized harness rather than pretending the connect would work.
    /// </summary>
    [Theory]
    [InlineData("claude-cli")]
    [InlineData("codex-cli")]
    [InlineData("cursor-acp")]
    public void ObsoleteChannelBearingDrivers_InventNoProtocol(string driver)
    {
        Assert.Null(HarnessCatalog.Find(driver));
        Assert.NotEqual(ChatProtocols.Acp, HarnessCatalog.ResolveProtocol(null, driver, null));
        Assert.Equal(ChatProtocols.OpenAiChat, HarnessCatalog.ResolveProtocol(null, driver, null));
    }

    [Fact]
    public void InferFromDriver_NoLongerMapsAnyHarnessDriver()
    {
        foreach (var spec in HarnessCatalog.All)
        {
            Assert.Equal(ChatProtocols.OpenAiChat, ChatProtocols.InferFromDriver(spec.Id));
        }
    }

    /// <summary>
    /// A channel decides how Core spawns and talks to a process, so an unknown value must not
    /// silently become a default the way an unknown protocol becomes openai-chat.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("grpc")]
    [InlineData("acpp")]
    [InlineData("stdio")]
    public void AgentChannels_UnknownValuesNormalizeToNullRatherThanAGuess(string? channel)
        => Assert.Null(AgentChannels.Normalize(channel));

    [Fact]
    public void AgentChannels_AreCaseAndWhitespaceTolerant()
    {
        Assert.Equal(AgentChannels.Acp, AgentChannels.Normalize(" ACP "));
        Assert.Equal(AgentChannels.Cli, AgentChannels.Normalize("CLI"));
        Assert.Equal(AgentChannels.Tui, AgentChannels.Normalize("tui"));
        Assert.Equal(3, AgentChannels.All.Count);
    }

    [Fact]
    public void UnsupportedChannel_ResolvesNoProtocolRatherThanTheHarnessDefault()
    {
        // zcode has no acp channel. Asking for one must not fall back to headless-cli: that would
        // silently run a different shape than the caller requested.
        Assert.Null(HarnessCatalog.ProtocolFor("zcode", AgentChannels.Acp));
        Assert.Null(HarnessCatalog.ProtocolFor("opencode", "acpp"));
        Assert.Null(HarnessCatalog.ProtocolFor(null, AgentChannels.Acp));
    }

    [Fact]
    public void EveryHarness_VendorsItsOwnCredentialAndConfigFactsOrSaysItDoesNot()
    {
        foreach (var spec in HarnessCatalog.All)
        {
            // EnvVars names only: Core never reads, logs, or persists a value from any of them, so
            // an unverified harness records an empty list instead of a guessed variable name.
            Assert.All(spec.EnvVars, name => Assert.Matches("^[A-Z][A-Z0-9_]*$", name));
            Assert.All(spec.BinaryNames, name => Assert.DoesNotContain('/', name));
            Assert.False(string.IsNullOrWhiteSpace(spec.Vendor));
            Assert.False(string.IsNullOrWhiteSpace(spec.DisplayName));
        }
    }

    /// <summary>
    /// A harness whose channel list carries a caveat-worthy vendor behaviour must record it in the
    /// catalog, because discovery is the only place a user can read it before a connect fails.
    /// </summary>
    [Theory]
    [InlineData("codebuddy")]
    [InlineData("dsh")]
    [InlineData("kimi-code")]
    [InlineData("cursor")]
    [InlineData("zcode")]
    public void HarnessesWithMeasuredVendorQuirks_CarryThemAsCaveats(string id)
        => Assert.NotEmpty(HarnessCatalog.Find(id)!.KnownCaveats);

    /// <summary>
    /// Measured on this host: <c>claude -p P --output-format stream-json</c> exits 1 saying
    /// "When using --print, --output-format=stream-json requires --verbose", and adding
    /// <c>--input-format stream-json</c> makes an argv prompt vanish with a zero exit and zero output.
    /// Both halves of that row are pinned here, because each one is a way to lose a turn silently.
    /// </summary>
    [Fact]
    public void ClaudeCode_HeadlessRowCarriesVerboseAndTakesThePromptOnStdin()
    {
        var channel = HarnessCatalog.Find("claude-code")!.Channel(AgentChannels.Cli)!;
        Assert.Contains("--verbose", channel.Argv);
        Assert.Contains("--input-format", channel.Argv);
        Assert.Equal(HarnessPromptDeliveries.StdinMessage, channel.PromptDelivery);
        Assert.DoesNotContain(HarnessCatalog.PromptPlaceholder, channel.Argv);
        Assert.Equal(HarnessHeadlessEnvelopes.ClaudeResult, channel.Envelope);

        var argv = HarnessCatalog.MaterializeChannelArgv("claude-code", AgentChannels.Cli, "hello");
        Assert.DoesNotContain("hello", argv);
    }

    /// <summary>
    /// Core spawns harnesses in a minted scratch directory, which is never a git repository, and codex
    /// refuses there without <c>--skip-git-repo-check</c> (measured: exit 1 with that exact sentence).
    /// The <c>-</c> positional is what makes the prompt arrive over stdin instead of the command line.
    /// </summary>
    [Fact]
    public void Codex_HeadlessRowRunsOutsideGitAndReadsThePromptFromStdin()
    {
        var channel = HarnessCatalog.Find("codex")!.Channel(AgentChannels.Cli)!;
        Assert.Contains("--skip-git-repo-check", channel.Argv);
        Assert.Contains("-", channel.Argv);
        Assert.Equal(HarnessPromptDeliveries.Stdin, channel.PromptDelivery);
        Assert.Equal(HarnessHeadlessEnvelopes.CodexJsonl, channel.Envelope);
    }

    /// <summary>
    /// Measured: <c>dsh tui</c> is not a subcommand. dsh reads <c>tui</c> as a profile name and dies
    /// with <c>profile "tui" does not exist</c> — the terminal UI is the <c>dsh-tui</c> plugin profile,
    /// which the vendor ships separately (<c>dsh plugin --profile dsh-tui add …</c>).
    /// </summary>
    [Fact]
    public void Dsh_TuiRowNamesThePluginProfileRatherThanAnInventedSubcommand()
    {
        var channel = HarnessCatalog.Find("dsh")!.Channel(AgentChannels.Tui)!;
        Assert.Equal(["--profile", "dsh-tui"], channel.Argv);
        Assert.DoesNotContain("tui", channel.Argv);
    }

    /// <summary>
    /// Drivability on the one-shot channel is claimed only for harnesses this build has watched answer.
    /// The other two rows are deliberately <see cref="HarnessHeadlessEnvelopes.None"/>: kimi-code is
    /// signed out here and opencode's provider was unreachable, so their stdout has only ever been seen
    /// failing, and a parser invented for them would be a guess wearing a fixture.
    /// </summary>
    [Theory]
    [InlineData("claude-code", true)]
    [InlineData("codebuddy", true)]
    [InlineData("codex", true)]
    [InlineData("dsh", true)]
    [InlineData("zcode", true)]
    [InlineData("kimi-code", false)]
    [InlineData("opencode", false)]
    [InlineData("cursor", false)]
    [InlineData("not-a-harness", false)]
    public void HeadlessEnvelopes_AreDeclaredOnlyWhereAnAnswerWasCaptured(string id, bool expected)
        => Assert.Equal(expected, HarnessCatalog.HasVerifiedHeadlessEnvelope(id));

    /// <summary>
    /// The invariant that makes a silent empty answer impossible by construction, on the rows that
    /// carry a prompt: a headless row either has the placeholder and declares argv delivery, or
    /// declares stdin delivery and must not carry it. A row that does both would drop the prompt on the
    /// floor with exit code 0. Non-headless rows take no prompt, so they are out of scope.
    /// </summary>
    [Fact]
    public void PromptDeliveryAndPlaceholder_NeverDisagreeOnAnyHeadlessRow()
    {
        var rows = HarnessCatalog.All
            .SelectMany(spec => spec.Channels.Select(channel => (spec.Id, channel)))
            .Where(row => row.channel.Protocol == ChatProtocols.HeadlessCli)
            .ToList();

        Assert.Equal(7, rows.Count);
        foreach (var (id, channel) in rows)
        {
            var carries = channel.Argv.Contains(HarnessCatalog.PromptPlaceholder, StringComparer.Ordinal);
            Assert.Equal(channel.PromptDelivery == HarnessPromptDeliveries.Argv, carries);
        }
    }

    [Fact]
    public void MaterializeArgv_SubstitutesThePromptOnceAndKeepsEveryOtherEntry()
    {
        var argv = HarnessCatalog.MaterializeChannelArgv("zcode", AgentChannels.Cli, "PONG please");
        Assert.Equal(["-p", "PONG please", "--json", "--mode", "build"], argv);
    }

    /// <summary>
    /// A stdin-delivered row still materializes to a usable command line — without the prompt anywhere
    /// in it. This is the shape that makes the measured claude silent-drop impossible to re-introduce.
    /// </summary>
    [Fact]
    public void MaterializeArgv_ForAStdinRowLeavesThePromptOutOfTheCommandEntirely()
    {
        var argv = HarnessCatalog.MaterializeChannelArgv("claude-code", AgentChannels.Cli, "hello");
        Assert.DoesNotContain("hello", argv);
        Assert.Contains("-p", argv);
        Assert.Contains("--verbose", argv);
    }

    [Fact]
    public void MaterializeArgv_RequiresPromptTextWhenTheRowTakesItOnArgv()
    {
        Assert.Throws<InvalidOperationException>(
            () => HarnessCatalog.MaterializeChannelArgv("zcode", AgentChannels.Cli, " "));
    }
}
