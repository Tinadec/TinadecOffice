# 内置浏览器 Agent 化 — 实施报告（2026-11-02）

## 1. 结论

第一阶段（独立 Agent 浏览器窗口）已完成并通过真实链路验证；第二阶段（挂进右侧预览卡）尚未实施，预览卡仍是原来的 iframe。

Agent 现在可以：

- 打开一个独立的 Agent 浏览器窗口并导航；
- 读取紧凑 a11y 快照与 `[ref=N]` 句柄；
- 真实点击、输入（回读校验）、按键、滚动、前进/后退/刷新、多标签；
- 用 `browser_batch` 一次审批执行多步；
- 等待文本/URL 出现；
- 截图落盘；
- 在用户接管、JS 对话框、权限提示、下载、文件选择器时收到稳定硬停止码。

## 2. 落地内容

### 2.1 Electron / 桌面

- `apps/desktop/electron/agentBrowser.cjs`：一 session 一窗口、多 `WebContentsView` 标签、独立 profile、debugger attach、控制态、notices（带 seq）、截图落盘、shell 命令与 sender 校验、下载/权限/文件选择/对话框记录。
- `apps/desktop/electron/browserBridge.cjs`：loopback HTTP + 随机 Bearer 令牌（写入 `state/agent-browser/bridge.json`，退出删除）、CDP 方法白名单、参数校验、导航 scheme/元数据检查、脚本 token 拒绝、每 session 串行、审计 JSONL。
- `apps/desktop/agent-browser.html` + `src/agentBrowserShell.ts` + `agentBrowserPreload.cjs` + `agentBrowserStrings.cjs`：独立窗口工具栏（地址、前进后退刷新、标签选择、Agent/User 控制徽标、接管/交还、外部打开、关闭），五语标签。
- `main.cjs`：启动服务前启桥并写令牌；`before-quit`/`will-quit` 停桥删令牌；主窗口真正关闭/`window-all-closed` 关闭 Agent 浏览器；`tinadec:interface-locale` 同步 shell 语言；`vite.config.ts` 增加 `agent-browser.html` 第二入口。

### 2.2 TinadecTools（.NET）

- `BrowserBridgeClient`：读 bridge.json、Bearer、401/连接失败重读、超时/业务码分类。
- `BrowserSnapshotBuilder`：AX 树压缩、ref 复用、预算截断、状态属性；`invalid:false` 修复。
- `BrowserNavigationPolicy`：http/https、userinfo、loopback/私有网段/链路本地/云元数据。
- `BrowserToolRuntime`（partial）：session/标签/ref 状态、快照、导航、等待、截图；动作层 click/type/press/scroll/history/tabs/batch；composed hit test、真实输入、输入回读与原生 setter 回退、loaderId 失效、notice seq 消费、`RetrySafety` 语义。
- `BrowserTools`：14 个工具（4 只读 + navigate + 9 操作），手写 schema 与模型说明；read 工具不审批，操作/导航/batch 审批门 + risk high + retry unsafe。

### 2.3 Core / 治理

- `ToolApprovalPolicy.DefaultHumanOnlyTools` / `DefaultDelegationBlockedTools` 增加全部浏览器操作 id；`AutoApproveOptions.HumanOnlyTools` 同步。
- `ToolDispatcher` 的 retryable 增加 `RetrySafety == "safe"`，避免非幂等只读调用被建议重试（新增回归）。
- `ApprovalEvidenceProjector` 增加 `browser_*` 分支：人类可读摘要（页面/ref/N 步），typed text 与嵌套 batch text 只保留字符数与短哈希。
- `ToolSettingsSchema` 增加 `browser` 段（enabled、max_snapshot_chars、action_timeout_ms、wait_timeout_ms、allow_private_hosts）；工具运行时读取快照预算、动作/等待超时与 allow_private_hosts。
- `GraphSeedPack` 3.4.0：浏览器工具进入 `search` 执行者；Plan/Spec 的 search 绑定关闭 14 个浏览器工具；meeting/solo_master 保持原工具面。digest 更新为 `de9924d3…`。
- Core API 假 manifest 补齐 14 个浏览器 id，供 spawnable 天花板求交。

### 2.4 桌面呈现

- `PROVIDER_TOOL_IDS` 从实测 `#manifest` 更新为 **65** 个（新增 14 个 browser + 补上此前漏登记的真实 `delete_file`），`manifest_hash` 更新为 `001ffc36…`。
- 新 `browser` kind + `Globe` 图标；`browse` 动词；五语 `conversation.tool.browse.*`。
- `toolSentence` 对 browser 用 URL/`ref:N` 或动作名作为 subject。

## 3. 验证

| 范围 | 结果 |
|---|---|
| P0 Electron/CDP 探针 | 17/17（AX/backendDOMNodeId/getBoxModel/scroll/resolve/click/screenshot/dialog/loaderId/文件选择器/insertText/二次 attach） |
| P1 桥/管理器/文案单测 | 27/27（令牌、401/403/413、方法白名单、JS/元数据、控制态、对话框、notices、串行、标签、截图、sender、二次 attach） |
| P1 真 Electron 集成探针 | 10/10（真 WebContentsView + 真桥 + 真页面） |
| P2/P3 浏览器单测 | 67/67（客户端 10、快照 5、导航 10、运行时/动作/batch 42） |
| TinadecTools 全量 | **493/494**；唯一失败 `ShellToolTests.RealCmd_BackgroundChildCannotHoldTheResultPipeOpen` 隔离复跑 **1/1** 通过，属既有真实 cmd 夹时序，非本批模块 |
| Core ApprovalEvidenceProjector | 23/23 |
| Core AutoApprove 策略 | 52/52 |
| Core GraphSeedPack（closure/config/install） | 9/9 |
| Core retryable 语义（safe/unsafe 只读） | 2/2 |
| Desktop typecheck | exit 0 |
| Desktop 定向 vitest（toolPresentation/toolSentence/i18n/GraphSeedPack） | 47/47 |
| **P5 真工具进程 → 桥 → 页面** | **21/21**：导航、snapshot ref、click、type+回读、wait、batch、screenshot、tabs、javascript 拒绝、open shadow DOM 点击、popup 通知、download 硬停止、file chooser 硬停止、dialog 暂停/恢复、用户接管/交还 |

桌面生产构建已落定：`npm run build -w @tinadec/desktop` **exit 0**，`vue-tsc --noEmit` 随构建一起通过，Rollup `✓ built in 2m 30s`、6826 modules；产物含 `dist/agent-browser.html`（3.43 kB）与 `dist/assets/agentBrowser-DXiPp34A.js`（3.15 kB），证明第二 HTML 入口在打包路径下能生成。日志：`.tinadec_dev/evidence/2026-11-02-agent-browser-p5/desktop-build.log`。

## 4. 本轮抓出并修掉的真缺陷

1. **先 attach 后加载会挂死**：全新空白 `WebContentsView` 上 `Page.enable` 永久挂起；改为先 `loadURL(url ?? 'about:blank')` 再 attach（P1 首跑 10 秒超时定位）。
2. **快照把 `invalid:false` 标成 invalid**：AX 树对有效控件返回字符串 `"false"`，旧逻辑只看“是字符串”；现在 True 或非 `"false"` 才标。
3. **下载通知赶不上点击回执**：`will-download` 异步；且 `DownloadItem.getWebContents()` 不一定定位发起 tab。管理器在无法定位且只有一个 session 时回退到该 session；工具在下一命令的 notices 中捕获并硬停止。文件选择器同理。
4. **`DOM.resolveNode` 的 objectId 在 `object.objectId`，`Runtime.evaluate` 在 `result.objectId`**：动作层曾统一用 evaluate 的解析器，导致所有动作 `browser_ref_stale`；已分开。
5. **meeting 快照超 16 KiB 列**：把浏览器工具加进 meeting 后 `agent_versions.SnapshotJson` 校验失败；按协调者/执行者架构改为 **search 执行者**持有浏览器，meeting/solo_master 保持原工具面。
6. **桌面 `PROVIDER_TOOL_IDS` 漏了真实 `delete_file`**：本次实测 65 个工具时发现并补上；列表从 50 → 65。

## 5. 诚实边界

- **P6 卡片挂载未做**：`PreviewBrowserPanel.vue` 仍是 iframe；Agent 浏览器是独立窗口。卡片挂载需要 `WebContentsView` reparent/overlay spike，原生视图会盖住命令面板/通知岛等 renderer 弹层。
- **shell 工具栏真实 UI 未实机走查**：P5 的 shell 是 `about:blank`；地址栏/接管按钮/标签下拉只在 Vite dev 入口返回 200，未在 Electron 窗口里点击验证。生产 `app://bundle` 的 `agent-browser.html` 未验证。
- **P5 未经过 Core/Gateway/真实模型**：它直接驱动真实 TinadecTools 子进程；Core 侧由 GraphSeedPack 安装、审批证据、auto-approve、retry 测试覆盖。
- **iframe/OOPIF 深层 ref 未做**：v1 只保证主框架；同进程 iframe 的内容不在快照内。open shadow root 已实测。
- **没有 `browser_dialog` 工具**：JS 对话框让 agent 硬停止，由用户在窗口里处理或宿主代答；这是有意的安全面。
- **下载/上传/文件选择器不支持**，只硬停止；截图只给人看（模型无图片通道）。
- **Linux/macOS、PostgreSQL、打包安装版未验证**；生产 `vite build` 已 exit 0 并产出 shell 入口，但 `app://bundle` 服务的实际加载未在安装版走查。
- **全量 Desktop vitest 未跑**：只跑与本批相关的 4 个套件（47/47）；仓库既有基线有已知 flake。
- 探针进程在 `app.exit` 处偶发不返回（Electron 子进程持有管道），已用 taskkill 清理；不影响 PASS 读数。

## 6. 下一步

1. **P6 spike**：把同一个 `WebContentsView` reparent 到主窗口，按卡片 rect 同步 bounds，验证命令面板/通知岛/拖拽/分离窗口；失败则降级 `capturePage` 镜像。
2. 真实应用 `npm run dev` / 打包 `app://bundle` 里走一遍 shell 工具栏、接管按钮、标签切换。
3. 若希望 Solo 主人**直接**浏览而不是派发 search，需要先解决 `agent_versions.SnapshotJson` 16 KiB 列（提升/拆列 + PG 迁移）；本批刻意没有改 Core 列预算。
4. 三平台 CI、PostgreSQL、真实模型端到端、安装版 `app://bundle` 走查。
5. 可选：审计日志 UI、站点经验（复用 Memory/Skills）、iframe/OOPIF、`browser_dialog` 专用工具。
