namespace TinadecCore.Abstractions.Ports;

/// <summary>
/// Where the prompt text goes when Core opens a one-shot headless channel. This is not a style
/// choice: measured on this host, <c>claude -p &lt;prompt&gt; --input-format stream-json</c> exits 0
/// having printed <em>nothing</em> — with the streaming input format the argv prompt is ignored — so a
/// row that claims <see cref="Argv"/> here can report an empty answer as a successful turn.
/// </summary>
public static class HarnessPromptDeliveries
{
    /// <summary>Substituted into the argv template at <see cref="HarnessCatalog.PromptPlaceholder"/>.</summary>
    public const string Argv = "argv";

    /// <summary>Written to the child's standard input as plain text, then closed.</summary>
    public const string Stdin = "stdin";

    /// <summary>
    /// Written to standard input as one JSON user-message frame
    /// (<c>{"type":"user","message":{"role":"user","content":[{"type":"text",…}]}}</c>) — the shape the
    /// Claude-family harnesses require once <c>--input-format stream-json</c> is selected.
    /// </summary>
    public const string StdinMessage = "stdin-message";
}

/// <summary>
/// Which answer frame the harness actually emits on its headless channel. The vocabulary is a vendor
/// property, so it is declared in the table rather than inferred: a client that guesses a dialect
/// either loses the answer or, worse, files an empty stdout as a turn that succeeded.
/// <see cref="None"/> means "this build has not captured a completing turn from this harness", which
/// keeps the channel undrivable instead of offering a button that cannot answer.
/// </summary>
public enum HarnessHeadlessEnvelopes
{
    None = 0,

    /// <summary>
    /// Claude Code family: one frame per line, answer in the terminal <c>{"type":"result","subtype",
    /// "is_error","result","usage"}</c> frame, streamed text in <c>type:"assistant"</c> frames.
    /// Captured from claude-code 2.x and codebuddy on this host, both replying <c>PONG</c>.
    /// </summary>
    ClaudeResult,

    /// <summary>
    /// Codex CLI <c>exec --json</c>: JSONL <c>thread.started</c> / <c>turn.started</c> /
    /// <c>item.completed</c> / <c>turn.completed</c> / <c>turn.failed</c>, answer in
    /// <c>item.type=="agent_message"</c> → <c>item.text</c>. Event names read from the vendor's
    /// <c>codex-rs/exec/src/exec_events.rs</c>; a completing turn was not captured here because the
    /// configured provider answered 403.
    /// </summary>
    CodexJsonl,

    /// <summary>
    /// DeepSeek Harness: <c>{"type":"session"}</c>, <c>{"type":"status","phase"}</c> frames, then
    /// <c>{"type":"final","text"}</c>. Measured on this host — and it emits <c>final</c> with empty
    /// text while exiting non-zero when its upstream has no channel, so an empty final is a failure.
    /// </summary>
    DshFinal,

    /// <summary>
    /// ZCode <c>-p … --json</c>: not NDJSON at all — one indented JSON document on stdout with the
    /// answer in <c>response</c> and counts under <c>usage</c>. A line-based reader sees a document
    /// whose first line is <c>{</c> and returns nothing.
    /// </summary>
    ZcodeDocument
}

/// <summary>
/// One channel a harness can be driven over: the argv that selects it and the wire protocol Core
/// then speaks there. Argv entries are templates — <see cref="HarnessCatalog.PromptPlaceholder"/>
/// and <see cref="HarnessCatalog.PortPlaceholder"/> are substituted at spawn time and are never
/// stored in provider configuration.
/// </summary>
public sealed record HarnessChannelSpec(
    string Channel,
    string Protocol,
    IReadOnlyList<string> Argv,
    IReadOnlyList<string> MandatoryArgs,
    string PromptDelivery = HarnessPromptDeliveries.Argv,
    HarnessHeadlessEnvelopes Envelope = HarnessHeadlessEnvelopes.None);

/// <summary>
/// A harness that also exposes a long-lived local HTTP server instead of a stdio session. This is
/// not a fourth channel: it is reached over HTTP, so readiness is a URL probe rather than a
/// handshake. <c>opencode serve</c> is the one Core has always been able to reach.
/// </summary>
public sealed record HarnessHttpSpec(
    string Protocol,
    IReadOnlyList<string> Argv,
    int DefaultPort);

/// <summary>
/// How to ask a harness for its version. <see cref="NeverUse"/> lists argv that must not be probed
/// even though they look harmless — a probe that shells out to a vendor command with side effects
/// turns a discovery call into a network request or a hang.
/// </summary>
public sealed record HarnessVersionProbe(
    IReadOnlyList<string> Argv,
    int TimeoutMs,
    bool TolerateNonZeroExit,
    IReadOnlyList<string> NeverUse);

/// <summary>
/// Everything TinadecCore knows about one external coding agent: how to find its binary, which
/// channels it offers, what argv selects each channel, and the vendor behaviour a caller cannot
/// discover by probing.
/// </summary>
/// <param name="EnvOverrides">
/// Environment variable names that pin the executable explicitly, checked before any search path.
/// </param>
/// <param name="ExtraSearchRoots">
/// Directories searched <em>before</em> <c>PATH</c>, so a vendor's own bundled binary wins over a
/// possibly stale global copy. Entries may use the <c>{ProgramFiles}</c>, <c>{LocalAppData}</c>,
/// <c>{AppData}</c> and <c>{UserProfile}</c> tokens; they expand at resolution time, not here, so
/// tests can substitute the roots.
/// </param>
/// <param name="EnvVars">
/// Environment variable names the harness itself reads. Names only — Core never reads, logs, or
/// persists a value from any of them. An empty list means "not verified against this harness",
/// never "this harness needs no credentials".
/// </param>
/// <param name="ConfigHome">
/// The harness's own configuration directory, or <c>null</c> when it has not been verified on a
/// supported host. Recorded so discovery can show it, not so Core can read the directory.
/// </param>
public sealed record HarnessSpec(
    string Id,
    string DisplayName,
    string Vendor,
    string DocsUrl,
    IReadOnlyList<string> BinaryNames,
    IReadOnlyList<string> EnvOverrides,
    IReadOnlyList<string> ExtraSearchRoots,
    IReadOnlyList<HarnessChannelSpec> Channels,
    HarnessHttpSpec? HttpServer,
    IReadOnlyList<string> EnvVars,
    string? ConfigHome,
    HarnessVersionProbe VersionProbe,
    IReadOnlyList<string> KnownCaveats)
{
    /// <summary>
    /// Protocol to assume when a stored provider carries neither an explicit <c>protocol</c> nor a
    /// <c>channel</c>. Prefers the HTTP server shape because that is the one path predating the
    /// channel vocabulary; otherwise the first declared channel is the vendor's primary one.
    /// </summary>
    public string DefaultProtocol => HttpServer?.Protocol ?? Channels[0].Protocol;

    public HarnessChannelSpec? Channel(string? channel)
    {
        var normalized = AgentChannels.Normalize(channel);
        return normalized is null ? null : Channels.FirstOrDefault(candidate => candidate.Channel == normalized);
    }
}

/// <summary>
/// Static registry of the external coding agents TinadecOffice can drive. It lives in
/// <c>Abstractions/Ports</c> as a compiled table — the same shape as
/// <see cref="WorkspaceToolCatalog"/> and <see cref="CoreVirtualToolPolicy"/> — because it carries
/// executable behaviour (exact argv per channel, bundled-root fallbacks, forced non-interactive
/// flags, probe tolerances) that has to be unit-testable and compile-checked. A TOML or JSON
/// resource would need its own validator, hot-reload store and test surface, and a DB table would
/// make vendor facts per-workspace state that drifts; neither is what this is.
/// </summary>
public static class HarnessCatalog
{
    /// <summary>Substituted with the prompt text at spawn time on the <see cref="AgentChannels.Cli"/> channel.</summary>
    public const string PromptPlaceholder = "{prompt}";

    /// <summary>Substituted with the chosen port at spawn time for the local HTTP server shape.</summary>
    public const string PortPlaceholder = "{port}";

    private static readonly string[] NoArgs = [];

    public static IReadOnlyList<HarnessSpec> All { get; } =
    [
        new(
            Id: "opencode",
            DisplayName: "OpenCode",
            Vendor: "SST",
            DocsUrl: "https://opencode.ai/docs",
            BinaryNames: ["opencode"],
            EnvOverrides: ["TINADEC_OPENCODE_EXECUTABLE"],
            ExtraSearchRoots: NoArgs,
            Channels:
            [
                new(AgentChannels.Acp, ChatProtocols.Acp, ["acp"], NoArgs),
                new(AgentChannels.Cli, ChatProtocols.HeadlessCli, ["run", PromptPlaceholder], NoArgs,
                    HarnessPromptDeliveries.Argv, HarnessHeadlessEnvelopes.None),
                new(AgentChannels.Tui, ChatProtocols.Tui, NoArgs, NoArgs)
            ],
            // The one harness-shaped path that worked before the channel vocabulary existed. Kept as
            // the default protocol so pre-existing opencode providers keep resolving opencode-serve.
            HttpServer: new(ChatProtocols.OpencodeServe, ["serve", "--port", PortPlaceholder], 4096),
            EnvVars: NoArgs,
            ConfigHome: null,
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: false, NeverUse: NoArgs),
            KnownCaveats:
            [
                "`opencode run <prompt>` 的 argv 本机被接受（它走到模型调用才失败：`Cannot connect to API`），" +
                "但成功回合的输出帧从未被抓到，所以 headless 信封留 None——它已有两条被验证能答话的渠道（acp、opencode-serve）。"
            ]),

        new(
            Id: "cursor",
            DisplayName: "Cursor",
            Vendor: "Anysphere",
            DocsUrl: "https://cursor.com/docs/cli/installation",
            BinaryNames: ["cursor-agent"],
            EnvOverrides: ["TINADEC_CURSOR_EXECUTABLE"],
            ExtraSearchRoots: ["{LocalAppData}/cursor-agent", "{UserProfile}/.local/bin"],
            Channels: [new(AgentChannels.Acp, ChatProtocols.Acp, ["acp"], NoArgs)],
            HttpServer: null,
            EnvVars: NoArgs,
            ConfigHome: null,
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: false, NeverUse: NoArgs),
            KnownCaveats:
            [
                "ACP 入口是子命令 `cursor-agent acp`，不是往任意 CLI 上追加端口 flag。",
                "Windows 上 npm 全局的 cursor-agent 是 .cmd shim，真实入口是 versions/<版本>/node.exe + index.js 的间接调用。",
                "未在开发机上验证过实机回合：该 harness 未安装，此行的 argv 来自上游已验证适配表。"
            ]),

        new(
            Id: "codebuddy",
            DisplayName: "CodeBuddy",
            Vendor: "Tencent",
            DocsUrl: "https://www.codebuddy.ai/docs/cli/overview",
            BinaryNames: ["codebuddy"],
            EnvOverrides: ["TINADEC_CODEBUDDY_EXECUTABLE"],
            // WorkBuddy ships its own CLI; the global npm copy can be older, so the bundled root
            // is searched first. Verified present on this host.
            ExtraSearchRoots:
            [
                "{ProgramFiles}/WorkBuddy/resources/app.asar.unpacked/cli/bin",
                "{LocalAppData}/Programs/WorkBuddy/resources/app.asar.unpacked/cli/bin"
            ],
            Channels:
            [
                new(AgentChannels.Acp, ChatProtocols.Acp, ["--acp"], NoArgs),
                new(AgentChannels.Cli, ChatProtocols.HeadlessCli,
                    ["-p", "--output-format", "stream-json", "--input-format", "stream-json"],
                    ["-y"], HarnessPromptDeliveries.StdinMessage, HarnessHeadlessEnvelopes.ClaudeResult),
                new(AgentChannels.Tui, ChatProtocols.Tui, NoArgs, NoArgs)
            ],
            HttpServer: null,
            EnvVars: NoArgs,
            ConfigHome: null,
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: true, NeverUse: ["--help"]),
            KnownCaveats:
            [
                "`--help` 会先向 galileotelemetry.tencent.com POST 遥测再打印帮助；版本探针必须用 `--version`，并且容忍断网时的非零退出。",
                "非交互（`-p`）必须带 `-y`，否则停在权限提示上直到超时。",
                "它不在 PATH 上：权威二进制随 WorkBuddy 桌面端一起安装。",
                "随 WorkBuddy 装的那个入口是无扩展名的 `#!/usr/bin/env node` 脚本，Windows 上没有 .exe/.cmd 兄弟文件，" +
                "所以按本机实测它解析不到（`node <脚本>` 才跑得起来）；信封与 Claude Code 同族，实测回 `result:\"PONG\"`。"
            ]),

        new(
            Id: "dsh",
            DisplayName: "DeepSeek Harness",
            Vendor: "DeepSeek",
            DocsUrl: "https://github.com/deepseek-ai/deepseek-harness",
            BinaryNames: ["dsh"],
            EnvOverrides: ["TINADEC_DSH_EXECUTABLE"],
            ExtraSearchRoots: NoArgs,
            Channels:
            [
                new(AgentChannels.Acp, ChatProtocols.Acp, ["--profile", "acp"], NoArgs),
                new(AgentChannels.Cli, ChatProtocols.HeadlessCli, ["--profile", "headless", "--json", PromptPlaceholder], NoArgs,
                    HarnessPromptDeliveries.Argv, HarnessHeadlessEnvelopes.DshFinal),
                // Not `dsh tui`: measured on this host, `tui` is read as a profile name and dsh dies with
                // `profile "tui" does not exist; create it with 'dsh plugin --profile tui add <package>'`.
                // The terminal UI is the `dsh-tui` plugin profile, so this row only runs once that plugin
                // is installed (`dsh-tui` / `dst` launchers appear next to `dsh` on PATH).
                new(AgentChannels.Tui, ChatProtocols.Tui, ["--profile", "dsh-tui"], NoArgs)
            ],
            HttpServer: null,
            EnvVars: ["DSH_HOME", "DEEPSEEK_API_KEY"],
            ConfigHome: null,
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: false, NeverUse: NoArgs),
            KnownCaveats:
            [
                "已观测到它在最后一个 session/update 之前就返回 prompt 回执（工具与 MCP 回合附近）；" +
                "回合必须经过排空窗口才算结束，否则回复非确定性地被截断。",
                "上游没有可用通道时它仍然打出一帧 `{\"type\":\"final\",\"text\":\"\"}` 并以退出码 1 结束" +
                "（本机实测：`No available channel for model deepseek-flash`）；空 final 必须判失败，不能记成\"模型说了空话\"。",
                "TUI 是插件 profile `dsh-tui`，不是内置子命令；未装插件时这一渠道连进程都起不来。"
            ]),

        new(
            Id: "kimi-code",
            DisplayName: "Kimi Code",
            Vendor: "Moonshot AI",
            DocsUrl: "https://moonshotai.github.io/kimi-code/",
            BinaryNames: ["kimi"],
            EnvOverrides: ["TINADEC_KIMI_EXECUTABLE"],
            ExtraSearchRoots: ["{UserProfile}/.kimi-code/bin"],
            Channels:
            [
                new(AgentChannels.Acp, ChatProtocols.Acp, ["acp"], NoArgs),
                new(AgentChannels.Cli, ChatProtocols.HeadlessCli,
                    ["-p", PromptPlaceholder, "--output-format", "stream-json"], NoArgs,
                    HarnessPromptDeliveries.Argv, HarnessHeadlessEnvelopes.None),
                new(AgentChannels.Tui, ChatProtocols.Tui, NoArgs, NoArgs)
            ],
            HttpServer: null,
            EnvVars: NoArgs,
            ConfigHome: "{UserProfile}/.kimi-code",
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: false, NeverUse: NoArgs),
            KnownCaveats:
            [
                "它自己会派子智能体：带 _meta['codebuddy.ai/parentToolCallId'] 的 session/update 绝不能拼进父回合的答案。",
                "ACP 能力面（fork、usage）的已验证记录来自 0.26.0；后续版本必须在连接时重新探测，不能照抄能力位。",
                "headless 渠道的 argv 本机接受（先打 `{\"role\":\"meta\",\"type\":\"system.version\",\"version\":\"2.1.1\"}`），" +
                "但这台机器上它没登录：`No model configured. Run kimi and use /login`，所以答案帧从未被抓到——" +
                "这一行的信封故意留 None，宁可让渠道显示为不可驱动，也不按别的厂商的信封猜它的输出。"
            ]),

        new(
            Id: "claude-code",
            DisplayName: "Claude Code",
            Vendor: "Anthropic",
            DocsUrl: "https://docs.anthropic.com/en/docs/claude-code",
            BinaryNames: ["claude"],
            EnvOverrides: ["TINADEC_CLAUDE_EXECUTABLE"],
            ExtraSearchRoots: NoArgs,
            // No acp channel: deliberate. `claude --help` on this host mentions ACP zero times, so
            // the previous claude-cli -> acp mapping could only ever fail to connect.
            Channels:
            [
                new(AgentChannels.Cli, ChatProtocols.HeadlessCli,
                    ["-p", "--output-format", "stream-json", "--input-format", "stream-json", "--verbose"], NoArgs,
                    HarnessPromptDeliveries.StdinMessage, HarnessHeadlessEnvelopes.ClaudeResult),
                new(AgentChannels.Tui, ChatProtocols.Tui, NoArgs, NoArgs)
            ],
            HttpServer: null,
            EnvVars: NoArgs,
            ConfigHome: "{UserProfile}/.claude",
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: false, NeverUse: NoArgs),
            KnownCaveats:
            [
                "双向 stream-json 的 Agent SDK 会话是厂商原生协议，属于第二批；本轮只声明它确实可跑的一次性 headless 渠道。",
                "`--output-format stream-json` 配 `-p` 必须再加 `--verbose`，否则退出码 1 并打印 " +
                "\"When using --print, --output-format=stream-json requires --verbose\"（本机实测）。",
                "带 `--input-format stream-json` 时 argv 里的 prompt 会被忽略：`-p <prompt> …` 静默退出 0、stdout 一个字节都没有（本机实测）。" +
                "prompt 只能作为 stdin 上的 user 帧送达，这条 argv 因此没有 {prompt} 占位符。"
            ]),

        new(
            Id: "codex",
            DisplayName: "Codex CLI",
            Vendor: "OpenAI",
            DocsUrl: "https://developers.openai.com/codex/cli",
            BinaryNames: ["codex"],
            EnvOverrides: ["TINADEC_CODEX_EXECUTABLE"],
            ExtraSearchRoots: NoArgs,
            Channels:
            [
                new(AgentChannels.Cli, ChatProtocols.HeadlessCli,
                    ["exec", "--json", "--skip-git-repo-check", "-"], NoArgs,
                    HarnessPromptDeliveries.Stdin, HarnessHeadlessEnvelopes.CodexJsonl),
                new(AgentChannels.Tui, ChatProtocols.Tui, NoArgs, NoArgs)
            ],
            HttpServer: null,
            EnvVars: NoArgs,
            ConfigHome: "{UserProfile}/.codex",
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: false, NeverUse: NoArgs),
            KnownCaveats:
            [
                "原生协议是 `codex app-server`（stdio JSON-RPC），不是 ACP；`codex exec` 另支持 --output-schema FILE 与 -o FILE。第二批接入。",
                "Core 给它的 cwd 是受治理的暂存根目录而不是 git 仓库，少了 `--skip-git-repo-check` 就退出 1：" +
                "\"Not inside a trusted directory and --skip-git-repo-check was not specified\"（本机实测）。",
                "`-` 让 prompt 从 stdin 读，命令行里就不出现用户文本；不带 `--json` 时它把人读的运行头写到 stderr，不可解析。",
                "上游 403 时它先连发多帧 `{\"type\":\"error\",\"message\":\"Reconnecting…\"}` 再 `{\"type\":\"turn.failed\"}`；" +
                "重试噪声不是终态，回合结果以 turn.* 为准。"
            ]),

        new(
            Id: "zcode",
            DisplayName: "ZCode",
            Vendor: "Z.ai",
            DocsUrl: "https://zcode.z.ai/",
            BinaryNames: ["zcode"],
            EnvOverrides: ["TINADEC_ZCODE_EXECUTABLE"],
            ExtraSearchRoots: NoArgs,
            Channels:
            [
                new(AgentChannels.Cli, ChatProtocols.HeadlessCli, ["-p", PromptPlaceholder, "--json", "--mode", "build"], NoArgs,
                    HarnessPromptDeliveries.Argv, HarnessHeadlessEnvelopes.ZcodeDocument),
                new(AgentChannels.Tui, ChatProtocols.Tui, ["tui"], NoArgs)
            ],
            HttpServer: null,
            EnvVars: NoArgs,
            ConfigHome: null,
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: false, NeverUse: NoArgs),
            KnownCaveats:
            [
                "不提供 ACP：`zcode app-server --stdio` 是厂商私有协议，帧里没有 \"jsonrpc\" 键，需要 JSON-RPC 连接层支持省略该字段的模式。第二批接入。",
                "--mode 的取值有 build|edit|yolo；目录钉在 build，yolo 会绕过权限提示，不属于受治理的默认。",
                "`--json` 输出的是**一整个缩进 JSON 文档**（首行就是 `{`），不是每行一帧：按 NDJSON 读它只能拿到 `{`。" +
                "答案在 `response`，计数在 `usage.inputTokens/outputTokens/totalTokens`（本机实测回 `PONG`，19239/3/19242）。",
                "stderr 会打 `ZCode Built-in skipped (not-due)`，与回合成败无关，别当错误。"
            ]),

        new(
            Id: "agy",
            DisplayName: "Antigravity",
            Vendor: "Google",
            DocsUrl: "https://antigravity.google/docs",
            BinaryNames: ["agy", "antigravity"],
            EnvOverrides: ["TINADEC_AGY_EXECUTABLE", "AGY_CLI_PATH"],
            ExtraSearchRoots:
            [
                "{LocalAppData}/Programs/Antigravity/bin",
                "{LocalAppData}/Programs/Antigravity",
                "{UserProfile}/.antigravity/bin"
            ],
            Channels:
            [
                new(AgentChannels.Acp, ChatProtocols.Acp, ["acp"], NoArgs),
                new(AgentChannels.Tui, ChatProtocols.Tui, NoArgs, NoArgs)
            ],
            HttpServer: null,
            EnvVars: ["GEMINI_API_KEY"],
            ConfigHome: "{UserProfile}/.gemini/antigravity",
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: true, NeverUse: NoArgs),
            KnownCaveats:
            [
                "Antigravity CLI (`agy`) speaks stdio ACP via `agy acp`; custom flags can be overridden in launch_args.",
                "User configuration and skills live under ~/.gemini/antigravity."
            ]),

        new(
            Id: "gemini-cli",
            DisplayName: "Gemini CLI",
            Vendor: "Google",
            DocsUrl: "https://geminicli.com/docs/",
            BinaryNames: ["gemini"],
            EnvOverrides: ["TINADEC_GEMINI_EXECUTABLE"],
            ExtraSearchRoots: ["{AppData}/npm", "{UserProfile}/.npm-global/bin", "{UserProfile}/.bun/bin"],
            Channels:
            [
                new(AgentChannels.Acp, ChatProtocols.Acp, ["--experimental-acp"], NoArgs),
                new(AgentChannels.Tui, ChatProtocols.Tui, NoArgs, NoArgs)
            ],
            HttpServer: null,
            EnvVars: ["GEMINI_API_KEY", "GOOGLE_API_KEY"],
            ConfigHome: "{UserProfile}/.gemini",
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: false, NeverUse: NoArgs),
            KnownCaveats:
            [
                "ACP stdio mode uses `--experimental-acp` (Gemini CLI 0.2+); override launch_args if your build uses `--acp`.",
                "Config and MCP settings live under ~/.gemini."
            ]),

        new(
            Id: "qwen-code",
            DisplayName: "Qwen Code",
            Vendor: "Alibaba Qwen",
            DocsUrl: "https://qwenlm.github.io/qwen-code-docs/",
            BinaryNames: ["qwen"],
            EnvOverrides: ["TINADEC_QWEN_EXECUTABLE"],
            ExtraSearchRoots: ["{AppData}/npm", "{UserProfile}/.npm-global/bin", "{UserProfile}/.bun/bin"],
            Channels:
            [
                new(AgentChannels.Acp, ChatProtocols.Acp, ["--acp"], NoArgs),
                new(AgentChannels.Tui, ChatProtocols.Tui, NoArgs, NoArgs)
            ],
            HttpServer: null,
            EnvVars: ["DASHSCOPE_API_KEY", "OPENAI_API_KEY"],
            ConfigHome: "{UserProfile}/.qwen",
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: false, NeverUse: NoArgs),
            KnownCaveats:
            [
                "ACP stdio mode starts via `qwen --acp`.",
                "Config and skills live under ~/.qwen."
            ]),

        new(
            Id: "minimax-code",
            DisplayName: "MiniMax Code",
            Vendor: "MiniMax",
            DocsUrl: "https://platform.minimaxi.com/docs",
            BinaryNames: ["minimax", "minimax-code"],
            EnvOverrides: ["TINADEC_MINIMAX_EXECUTABLE"],
            ExtraSearchRoots: ["{AppData}/npm", "{UserProfile}/.npm-global/bin", "{UserProfile}/.bun/bin", "{UserProfile}/.local/bin"],
            Channels:
            [
                new(AgentChannels.Acp, ChatProtocols.Acp, ["--acp"], NoArgs),
                new(AgentChannels.Tui, ChatProtocols.Tui, NoArgs, NoArgs)
            ],
            HttpServer: null,
            EnvVars: ["MINIMAX_API_KEY"],
            ConfigHome: "{UserProfile}/.minimax",
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: true, NeverUse: NoArgs),
            KnownCaveats:
            [
                "If your MiniMax Code build uses custom ACP flags, configure launch_args or use custom-acp."
            ]),

        new(
            Id: "pi",
            DisplayName: "Pi Coding Agent",
            Vendor: "Pi",
            DocsUrl: "https://shittycodingagent.ai/",
            BinaryNames: ["pi"],
            EnvOverrides: ["TINADEC_PI_EXECUTABLE"],
            ExtraSearchRoots: ["{AppData}/npm", "{UserProfile}/.npm-global/bin", "{UserProfile}/.bun/bin", "{UserProfile}/.pi/bin"],
            Channels:
            [
                new(AgentChannels.Acp, ChatProtocols.Acp, ["--acp"], NoArgs),
                new(AgentChannels.Tui, ChatProtocols.Tui, NoArgs, NoArgs)
            ],
            HttpServer: null,
            EnvVars: ["ANTHROPIC_API_KEY", "OPENAI_API_KEY"],
            ConfigHome: "{UserProfile}/.pi",
            VersionProbe: new(["--version"], 15_000, TolerateNonZeroExit: true, NeverUse: NoArgs),
            KnownCaveats:
            [
                "Pi CLI supports ACP via `pi --acp`; set launch_args when using an external ACP adapter wrapper."
            ]),

        new(
            Id: "custom-acp",
            DisplayName: "Custom ACP Agent",
            Vendor: "Custom",
            DocsUrl: "https://agentclientprotocol.com/",
            BinaryNames: ["custom-acp"],
            EnvOverrides: ["TINADEC_CUSTOM_ACP_EXECUTABLE"],
            ExtraSearchRoots: NoArgs,
            Channels: [new(AgentChannels.Acp, ChatProtocols.Acp, NoArgs, NoArgs)],
            HttpServer: null,
            EnvVars: NoArgs,
            ConfigHome: null,
            VersionProbe: new(["--version"], 10_000, TolerateNonZeroExit: true, NeverUse: NoArgs),
            KnownCaveats:
            [
                "Generic stdio ACP driver: supply any compliant ACP executable in binary_path and optional CLI flags in launch_args."
            ])
    ];

    /// <summary>Finds a harness by catalog id. Deliberately does not alias the pre-channel driver
    /// strings (<c>claude-cli</c>, <c>codex-cli</c>, <c>cursor-acp</c>): an alias table would keep
    /// the fabricated driver-implies-protocol coupling alive, and those rows could never connect
    /// anyway. They render in the model center as an unrecognized harness needing manual editing.</summary>
    public static HarnessSpec? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var needle = id.Trim();
        return All.FirstOrDefault(spec => string.Equals(spec.Id, needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Protocol for a (harness, channel) pair. Returns <c>null</c> when the driver is not a catalog
    /// harness, and also when the harness does not declare the requested channel — a channel is a
    /// routing decision, so a typo'd <c>acp</c> must not silently resolve to a headless one-shot.
    /// A missing channel falls back to the harness's primary protocol, which is what pre-channel
    /// stored rows mean.
    /// </summary>
    public static string? ProtocolFor(string? driver, string? channel)
    {
        var spec = Find(driver);
        if (spec is null) return null;
        if (string.IsNullOrWhiteSpace(channel)) return spec.DefaultProtocol;
        return spec.Channel(channel)?.Protocol;
    }

    /// <summary>
    /// The single resolution order for a provider's wire protocol: the explicitly configured value
    /// wins, then the harness catalog's (harness, channel) pair, then the HTTP API driver map.
    /// Every read path that needs a protocol calls this instead of restating the chain, because a
    /// consumer that drops the catalog step silently falls back to <c>openai-chat</c> for a harness.
    /// </summary>
    public static string ResolveProtocol(string? configuredProtocol, string? driver, string? channel) =>
        ChatProtocols.Normalize(configuredProtocol ?? ProtocolFor(driver, channel) ?? ChatProtocols.InferFromDriver(driver));

    /// <summary>
    /// The argv that opens one channel of one harness. Callers must not fall back to stored
    /// <c>launch_args</c>: passing a free-text argument string to a process safely needs a shell
    /// parser, and a driver the catalog does not know has no verified invocation for this channel —
    /// guessing one turns a typo into spawning an unrelated command with the user's prompt appended.
    /// </summary>
    public static IReadOnlyList<string> ChannelArgv(string? driver, string channel) =>
        Find(driver)?.Channel(channel)?.Argv
        ?? throw new InvalidOperationException(
            $"Harness '{driver}' has no '{channel}' channel in the catalog, so Core will not guess the argv that starts one.");

    /// <summary>
    /// Resolves ACP launch argv: explicit operator-provided <paramref name="launchArgs"/> overrides
    /// the catalog template (allowing <c>custom-acp</c> or custom flags on any ACP harness);
    /// otherwise delegates to <see cref="ChannelArgv"/>.
    /// </summary>
    public static IReadOnlyList<string> ResolveAcpArgv(string? driver, string? launchArgs)
    {
        if (!string.IsNullOrWhiteSpace(launchArgs))
            return SplitCommandLineArgs(launchArgs);
        if (string.Equals(driver?.Trim(), "custom-acp", StringComparison.OrdinalIgnoreCase))
            return NoArgs;
        return ChannelArgv(driver, AgentChannels.Acp);
    }

    public static IReadOnlyList<string> SplitCommandLineArgs(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return NoArgs;
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var hasToken = false;

        foreach (var ch in commandLine)
        {
            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                hasToken = true;
                continue;
            }
            if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                hasToken = true;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inSingleQuote && !inDoubleQuote)
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }
            current.Append(ch);
            hasToken = true;
        }
        if (hasToken) args.Add(current.ToString());
        return args;
    }

    /// <summary>
    /// Where the prompt text goes for a (harness, channel) pair. Defaults to <see cref="HarnessPromptDeliveries.Argv"/>
    /// for rows that carry <see cref="PromptPlaceholder"/>, and is otherwise the row's declared value.
    /// </summary>
    public static string PromptDeliveryFor(string? driver, string channel) =>
        Find(driver)?.Channel(channel)?.PromptDelivery ?? HarnessPromptDeliveries.Argv;

    /// <summary>
    /// The argv Core actually passes for a turn on this channel: the template with the prompt
    /// substituted when (and only when) the row declares argv delivery, plus the vendor's mandatory
    /// non-interactive flags. Those flags are appended here rather than stored in the template so a
    /// configured provider cannot drop them — <c>codebuddy</c> without <c>-y</c> sits at a permission
    /// prompt until the turn times out.
    /// </summary>
    /// <remarks>
    /// The two consistency checks below are not defensive noise: each one corresponds to a way this
    /// build has measured a harness exiting 0 with no answer at all.
    /// </remarks>
    public static IReadOnlyList<string> MaterializeChannelArgv(string? driver, string channel, string? prompt)
    {
        var spec = Find(driver)?.Channel(channel)
            ?? throw new InvalidOperationException(
                $"Harness '{driver}' has no '{channel}' channel in the catalog, so Core will not guess the argv that starts one.");
        var carriesPlaceholder = spec.Argv.Contains(PromptPlaceholder, StringComparer.Ordinal);
        var delivery = spec.PromptDelivery;

        if (delivery == HarnessPromptDeliveries.Argv && !carriesPlaceholder)
            throw new InvalidOperationException(
                $"Harness '{driver}' declares argv prompt delivery for '{channel}' but its template has no {PromptPlaceholder}.");
        if (delivery != HarnessPromptDeliveries.Argv && carriesPlaceholder)
            throw new InvalidOperationException(
                $"Harness '{driver}' declares '{delivery}' prompt delivery for '{channel}' but its template still carries {PromptPlaceholder}.");

        if (delivery != HarnessPromptDeliveries.Argv)
        {
            // The template is still the whole command — `-p --output-format stream-json …` selects the
            // headless shape. Only the prompt text moves to stdin; spawning with no arguments would
            // start the vendor's interactive UI instead.
            return WithMandatoryArgs(spec, [.. spec.Argv]);
        }

        var substituted = new List<string>(spec.Argv.Count + spec.MandatoryArgs.Count);
        foreach (var entry in spec.Argv)
        {
            if (!entry.Contains(PromptPlaceholder, StringComparison.Ordinal))
            {
                substituted.Add(entry);
                continue;
            }

            if (string.IsNullOrWhiteSpace(prompt))
                throw new InvalidOperationException(
                    $"Harness '{driver}' takes its prompt on argv for '{channel}', but the turn had no prompt text to place.");

            // The parts are the text on either side of every placeholder, so joining with the prompt
            // substitutes each occurrence exactly once. Appending per part would double it.
            substituted.Add(string.Join(prompt, entry.Split(PromptPlaceholder, StringSplitOptions.None)));
        }
        return WithMandatoryArgs(spec, substituted);

        static IReadOnlyList<string> WithMandatoryArgs(HarnessChannelSpec spec, List<string> argv)
        {
            foreach (var mandatory in spec.MandatoryArgs)
            {
                if (!argv.Contains(mandatory, StringComparer.Ordinal)) argv.Add(mandatory);
            }
            return argv;
        }
    }

    /// <summary>
    /// True when this build has captured a completing headless turn from this harness, so the answer
    /// frame it parses is measured rather than assumed. Drivability on the one-shot channel is a
    /// property of the (harness, channel) pair — the envelope vocabulary is vendor-specific — so the
    /// model center must not offer <c>cli</c> for a harness whose frames this build has only seen fail.
    /// </summary>
    public static bool HasVerifiedHeadlessEnvelope(string? driver) =>
        Find(driver)?.Channel(AgentChannels.Cli)?.Envelope is not null and not HarnessHeadlessEnvelopes.None;
}
