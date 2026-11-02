# 扩张方向调研：电脑控制 / 团队协作 / GitHub 应用 / PR 审查 / 远程设备

> 调研日期：2026-11-03。来源：①本地逆向 Qoder 桌面 App 0.5.1（`C:\git\agent\Qoder-fuckali\analysis\09-*`，buildTime 2026-10-30，**一手证据**）；②本地 Codex 克隆（`C:\git\agent\codex\codex-rs` 模块面）；③网络检索（PR 审查机器人横向、CUA 格局）；④本仓路线图/产品定义/既有差距报告。
> 性质：方向调研，非计划书。每条方向给"做/收窄做/暂不做"建议；立项再出计划书。

## 0. 一句话结论

五个方向里，**电脑控制（浏览器先行）和"另一台机器连本机 Core"式的远程是值得做的**——前者与本仓治理 DNA 天然契合、竞品在 CUA 治理上全是空白；后者几乎零架构改动。**PR 审查做本地 PAT 版**（GitHub App 版需云服务端，推迟）。**人类团队协作与 GitHub App 暂不做**——它们都要求云侧服务端事实与多租户，与本仓"本地优先、Core 唯一状态权威、SQLite 单机"的现行形态根本冲突。

## 1. 对比总表（一手证据优先）

| 方向 | Qoder（逆向 0.5.1） | Codex（本地克隆+公开资料） | Copilot/Cursor/Claude | TinadecOffice |
|---|---|---|---|---|
| 电脑控制 | **有，三层**：内置插件 `qoder-computer-use`；扩展目录 `computer-control`（computer-use / record-and-replay / picture-in-picture-host）；浏览器控制 = Chrome 扩展 + native-messaging-host + `@ali/qoder-browser-use-sdk` + `browser-session:*` 20 频道 + **`browser-internet-policy:*` 3 频道（治理）**；`data-qoder-gui-id` 远程点击锚点 | **无**（codex-rs 无 computer-use/browser 模块；`ext/web-search` 仅搜索。ChatGPT 桌面端的 `@Computer`/`@Chrome` 是另一产品线） | Claude：Computer Use + Claude Code 经 MCP 接 CUA；Cursor：无 | 无；但 MCP 全链、审批、沙箱、Win32 P/Invoke 封装全在 |
| 人类团队协作 | **完整云端对象模型**：`/api/v1/collaboration/*`（projects/issues/discussions/files/team-profiles/agent-memberships/invitations）+ collab-cli 双可执行（`qc-cli-readonly-v1`，CLI 不持有长期凭据）+ 企业管控缓存表 `collaboration_governance` | 无人类协作面；`collaboration-mode-templates`/`agent-roles`/`message-board` 是**多智能体编排**模板，不是人类团队；云任务共享走 ChatGPT 账号 | Copilot 有组织策略面；Claude Code 无原生 | 无；产品定义里"协作"全指智能体间编排（`tinadec-core-product-definition.zh-CN.md:290,334`） |
| 智能体团队（附带发现） | **squad 语义**：云端注册 Agent 账号（agent_profiles/agent_runtimes/registration outbox）、Leader/Member（agent_teams）、仅 Leader 可委派、监督边界原子转移（`chat_session_input_provenance.terminal_callback_boundary`） | 多智能体编排模板（同上） | — | DmaEA 图编排+spawn 权威（本地治理版，无云端账号体系） |
| GitHub 应用 | **无**：IDE 仅 VS Code 标配 `github`/`github-authentication` 内置扩展，无 PR 扩展；桌面 App preload 711 频道 github 命中 **0** | **有**（云侧）：`@codex` 提及→👀→直接在 PR 发 review（官方文档+社区确认，2026-02 有触发可靠性 issue）；`cloud-tasks/src/env_detect.rs:58-63` 识别 github remote | Copilot 原生 | 无 GitHub API 面（本地 git 面板+AI commit message 而已） |
| PR 审查 | 无 | 有（@codex review） | **红海**：Copilot Code Review（GA 2025-04）、Cursor BugBot（只报真 bug、准确率 52%→76%）、CodeRabbit、Claude Review | 无 |
| 其他设备远程控制 | **最重实现**：peer-device 双角色（Controller/Worker），LAN+Relay 双传输，运行时自签证书+SHA-256 指纹 pin，配对码+**持久授权表 peer_grants**（重启后仍可连的保障），影子会话 adopt_sessions，输入权威序 `peer_ingress_timeline`，访问策略 `peer_access_policies`，账号绑定（个人=同账号、团队=同组织）；另有 remote-ssh 17 频道（含 thin-fallback 瘦客户端）+ WSL 注册；0.4.4 remote-control 是其单向前身 | 无设备互控；cloud-tasks=云端沙箱任务 | — | 无；但 `EnvironmentKind` 枚举含 cloud/remote（仅类型）；拓扑天然多机（Desktop→Gateway→Core 全 HTTP） |
| 云自动化（附带发现） | `cloud-automation:*` 7 频道+缓存表 | Automations（2026 新增） | — | scheduling 501 桩（计划书②） |
| Quest 式通知（附带发现） | local-inbox（`user_input_required/execution_failed`，`waiting_user\|blocked`） | — | — | 计划书② Q2 对标项 |

## 2. 关键事实展开

### 2.1 电脑控制：Qoder 是"AI IDE 做 CUA"的唯一样本，且治理面有真设计
- 分层拆解：**浏览器层**（扩展+SDK+20 频道会话管理）先行、**桌面层**（插件+录制回放+PiP 宿主）并列、**远程点击**（`data-qoder-gui-id` 埋点，服务 peer-device）叠加——三件事共用 GUI 锚定。
- `browser-internet-policy:*` 3 频道说明 Qoder 给浏览器控制配了出口策略位——这是全部竞品 CUA 里少见的治理痕迹。
- Codex 反例证明：**终端系智能体全部没做 CUA**；做 CUA 的是"助手系"（ChatGPT/Claude）。AI IDE 系只有 Qoder。
- 本仓硬前置：`CoreAttachmentReadTool.cs` 明写不向 provider 发 image part（既有报告 §4.3）——**没有视觉输入就没有桌面 CUA**；浏览器层可走 DOM/a11y 锚定弱一档起步（Qoder 的 gui-id 正是此路）。

### 2.2 远程设备：Qoder 用 34 个新频道+10 个新资源目录做到的（0.4.4→0.5.1 单版本增量）
- 身份与信任模型可抄作业：设备身份=运行时自签证书、fingerprint256 pin、**授权持久化在 peer_grants 而非配对码**（迁移注释原文："peer_grants 这张表才是『重启后原主控端仍能连上』的保障"）、机密只进 safeStorage 不进库。
- 降级链完整：Relay 不可用→配对码回落仅 LAN；SSH 连不上→thin-fallback 瘦客户端。
- 本仓结构性优势：Core 本来就是独立 HTTP 服务（48731）——"另一台机器的 Desktop 连本机 Core"是**配置问题**，不是架构问题。缺的只有：TLS、设备身份、准入治理（哪个设备能动哪个工作区，对标 peer_access_policies）。

### 2.3 团队协作：两家殊途同归"要云"
- Qoder 为此建了整套服务端对象模型（projects/issues/discussions）+ 云 Agent 账号体系 + 企业管控位；Codex 走 ChatGPT 账号+云任务。**没有一家做了本地 P2P 的人类团队协作。**
- 本仓对照：OIDC 适配器/多租户调度在路线图 A4 登记为"延后"（`tinadec-four-product-roadmap.zh-CN.md:79,172`），容器镜像已有切片（:160）。SQLite 单机、无服务器——团队协作的一切前置都不在。

### 2.4 PR 审查：入口价值高，但红海且同质化
- 全部竞品的 PR 审查形态一致：mention/指派触发→看 diff→按仓库指引（AGENTS.md 类）→在 PR 上发评论。差异只剩准确率与工作流摩擦。
- 本仓若做，差异化不在"再做一个 review bot"，而在**治理证据**：审查结论带 file:line+可复跑的证据链（本仓审批证据投影的既有能力），这是 Copilot/BugBot 都没有的。

## 3. 建议（按档排序）

**第一档——建议立项**
1. **远程多机（收窄版 peer-device）**：第一批只做"设备 A 的 Desktop 连设备 B 的 Core"（token/证书身份+准入策略+审计），零架构改动；SSH 主机管理第二批；Relay 公网中继**不做**（需云）。前置：无硬前置，token 机制可借 A4 的 OIDC 切片顺手做。理由：本仓 HTTP 拓扑天然多机，性价比全表最高；且它是"真实可用"验收里最有感的形态。
2. **电脑控制（浏览器先行）**：第一批经 **MCP 接现成 CUA/browser 服务器**试水（本仓 MCP 全链现成，工具面零改动，逐次审批照用）；第二批自建浏览器控制（扩展+CDP+出口策略，对标 browser-internet-policy）；桌面 CUA 与视觉输入绑定推进（§4.3 修掉才开）。理由：治理 DNA 是差异化——竞品 CUA 全是信任模型；依赖链清楚。

**第二档——收窄后做**
3. **PR 审查（本地 PAT 版）**：本地拉 PR→review 智能体模式→评论回帖，带证据链。不做 GitHub App 版（需云 webhook 接收端）。前置：真实模型冒烟（review 质量在 ScriptedChatClient 下验证不了，既有报告 §4.1）。

**第三档——暂不做，登记即可**
4. **人类团队协作**：跟随 A4（OIDC/多租户）进入"云产品阶段"再议；本仓当前形态下做=先造服务器，不建议。
5. **GitHub App**：同上，且 Qoder 一手证据表明**对标产品也没做**——不构成竞争压力，暂缓无风险。

## 4. 依赖链与成本提醒

- 视觉输入（§4.3）→ 桌面 CUA；MCP 路可绕开视觉输入先走浏览器层。
- A4（OIDC/多租户）→ 远程多机的身份面、团队协作的一切。
- 计划书②（Quest/调度）→ 云自动化对标项。
- 成本基准：Qoder 0.5.1 单版本为设备互连+团队加了 34 频道、10 个新资源目录、18 个新依赖（`analysis/09-* §1`）——这是团队级投入；本仓必须按 §3 的切片做，第一批每一项都应能独立过 live 验收。

## 5. 附：本调研未覆盖/待跟进

- Qoder IDE 侧（vscode-source）未深挖，本篇以桌面 App 0.5.1 为准。
- Codex GitHub 集成的服务端形态（App 权限范围）未取一手证据，引用的是官方文档与社区 issue。
- Claude Code 2026 年的团队/企业面未深查（本篇仅作横向参照）。
