# 内置浏览器 Agent 化（Agent Browser）规范 — 2026-11-02

## 1. 目标

让 TinadecOffice 的内置浏览器从“人看的预览 iframe”变成“智能体可读、可操作、可被接管”的浏览器，同时坚持 .NET 技术栈、不引入第二套 Chromium。

- 浏览器引擎：Electron 自带 Chromium。
- 自动化语义（快照、ref、动作、等待、batch、错误分类）：全部在 .NET `TinadecTools`。
- Electron 只提供：窗口/标签生命周期、单个目标的 CDP 中继、硬停止事实（弹窗/权限/下载/文件选择/对话框）和用户接管。

## 2. 拓扑

```
Agent tool call (Core)
  → TinadecTools/Tools/Browser/*.cs
      → TINADEC_HOME/state/agent-browser/bridge.json {url, token}
      → 127.0.0.1 HTTP JSON（Bearer）
          → apps/desktop/electron/browserBridge.cjs
              → CDP 方法白名单 + 参数校验
              → apps/desktop/electron/agentBrowser.cjs
                  → WebContentsView（persist:tinadec-agent-browser）
```

- 第一阶段：**独立 Agent 浏览器窗口**（一 session 一窗口，窗口内多 `WebContentsView` 标签）。第二阶段再挂进右侧预览卡。
- 不使用全局 `--remote-debugging-port`；`webContents.debugger.sendCommand(method, params, sessionId?)` 只对已登记目标生效。
- 桥不提供语义命令：模型永远拿不到“执行任意 JS”的参数；`Runtime.evaluate`/`callFunctionOn` 只由工具内部固定脚本使用，且桥对 cookie/storage/网络 token 有额外拒绝。

## 3. 桥协议

- 文件：`<officeRoot>/state/agent-browser/bridge.json`，`{version, url, token, pid, created_at}`；启动写、退出删。**令牌不进环境变量**（工具子进程会继承 env）。
- HTTP：仅 `127.0.0.1`、随机端口、`Authorization: Bearer`、常量时间比较、拒绝带 `Origin` 的请求、请求体 ≤64 KiB。
- 操作：`cdp`、`tabs/open|list|select|close`、`screenshot`、`dialog`、`state`。
- CDP 允许：`Page.*`（不含 `addScriptToEvaluateOnNewDocument`/`setDocumentContent`/下载）、`DOM.*`、`Runtime.evaluate|callFunctionOn|releaseObject`、`Input.*`、`Accessibility.*`、`Target.getTargets|attachToTarget|detachFromTarget`。其余域一律拒绝。
- 结果：`{ok:true,data}` / `{ok:false,code,message,permanent}`；`cdp` 的 `data` 为 `{result, notices, notice_seq}`。
- 每 session 串行；notices 带单调 `seq`，工具只消费比上次新的通知，避免硬停止重复触发。

## 4. 工具面（14 个）

只读（`requires_approval=false`，risk low，mutates false，retry safe）：
- `browser_snapshot`：a11y 文本树 + `[ref=N]`；viewport/full 由实现约定，字符预算可配。
- `browser_tabs_list`
- `browser_wait`：页内轮询 text / url（`Runtime.callFunctionOn` 固定脚本）。
- `browser_screenshot`：桥落盘 PNG，只返回路径/字节数。

操作（`requires_approval=true`，risk high，mutates false，retry **unsafe**，人专属/委托阻断）：
- `browser_navigate`、`browser_history`
- `browser_click`、`browser_type`、`browser_press`、`browser_scroll`
- `browser_tab_open`、`browser_tab_select`、`browser_tab_close`
- `browser_batch`：1–20 个 click/type/press/scroll，一次审批，首个失败即停，最后返回新快照。

`browser_navigate` 首次调用打开独立窗口；`browser_snapshot` 在无标签时返回 `browser_not_started`。

## 5. 快照与 ref

- `Accessibility.getFullAXTree` → 过滤 ignored/匿名 generic → 缩进文本；交互角色分配 `[ref=N]`。
- ref 身份 = `backendDOMNodeId`（同一 tab）；同一快照内唯一。
- 文档身份 = `Page.getFrameTree` 的 `(frameId, loaderId)`；loaderId 变化时清空 ref 并重置计数。旧 ref 返回 `browser_ref_stale` / `browser_ref_unknown`（permanent），要求重新快照。
- 文本预算默认 24,000 字符（`browser.max_snapshot_chars` 2048–64,000）；超出截断并明确标记。
- 快照是模型的主视图；截图只给人看（模型没有图片通道）。

## 6. 动作语义

- ref → `DOM.scrollIntoViewIfNeeded` → `DOM.getBoxModel` → `DOM.resolveNode` → composed hit test（穿透 open shadow root/slot；检查 detached/disabled/zero-size/hidden/obscured）。
- 真实 `Input.dispatchMouseEvent` / `dispatchKeyEvent` / `insertText`；输入后回读，失败回退原生 value setter + input/change（固定脚本）。
- “已派发输入后不重试”：动作层没有重放循环；`RetrySafety=unsafe` 让 Core 也不把超时标成可重试。
- 等待在页内轮询，不在 .NET 忙等。

## 7. 安全与硬停止

- 独立 `persist:tinadec-agent-browser` 分区；`contextIsolation:true`、`sandbox:true`、`nodeIntegration:false`、无 preload、`webSecurity` 默认开；**不复用用户登录态**。
- 导航只 http/https、无 userinfo；允许 loopback（本地预览是主用例），默认阻止 RFC1918/链路本地/云元数据；`browser.allow_private_hosts` 可开私有网段（loopback 始终允许）。
- 权限请求一律 deny；`window.open` deny；`will-download` preventDefault；文件选择器拦截；JS 对话框记录并暂停 agent。
- 硬停止码：`browser_user_in_control`、`browser_dialog_open`、`browser_permission_denied`、`browser_download_started`、`browser_file_chooser_opened`、`browser_renderer_gone`；弹窗/导航拒绝是 notice，不阻断但可见。
- 用户接管：显式“停止/接管”按钮把控制态切到 `user`；所有 agent 操作返回硬停止（只读 `state/tabs/list` 仍可读）。第一阶段不自动检测用户输入。
- 审计：桥写 `state/agent-browser/audit.jsonl`（5 MiB 轮转），记录 op/method/code/url，不含参数与令牌。

## 8. 状态与重启

- 标签/窗口归 Electron；ref 属于 TinadecTools 进程，进程重启后失效（重新快照即可）。
- 应用重启重写 bridge.json；令牌变化由工具侧 401/连接失败重读一次处理。

## 9. 种子包

- `GraphSeedPack` 3.4.0：浏览器工具属于 **search 执行者**（`search.tool_scope`），不进入 `meeting`/`solo_master`——协调者的 AgentVersion 快照有 16 KiB 列预算，且协调者不应直接操作页面。Solo/Team/Graph/Workflow/Correction 的 search 可浏览；Plan/Spec 的 search 绑定把 14 个工具全部关掉。
- 浏览器 act 工具在 `ToolApprovalPolicy`/`AutoApproveOptions` 中是 human-only，并列入委托阻断；风险 high、非 workspace mutation。

## 10. 阶段 2（未实施）

把独立窗口的同一个 `WebContentsView` 挂进右侧预览卡，需要先验证：
- 从独立窗口 reparent 到主窗口 `contentView`，按卡片 rect 同步 bounds；
- 原生视图会盖住 renderer 的 `<dialog>`/teleport 弹层（命令面板、通知岛、右键菜单），必须 hide-on-overlay；
- 拖拽/缩放/分离窗口/圆角裁剪。

不通过则降级为 `capturePage` 镜像（卡片只显示画面 + “在浏览器窗口打开/接管”）。当前预览卡仍是 iframe；Agent 浏览器是独立窗口。

## 11. 明确不做（v1）

`browser_evaluate` / 任意 JS；复用登录态；iframe/OOPIF 深层 ref；下载/上传/文件选择器；实时视频帧；多窗口管理；Core 新 HTTP 端点；站点经验系统。

## 12. 关键文件

- `apps/desktop/electron/agentBrowser.cjs`、`browserBridge.cjs`、`agentBrowserPreload.cjs`、`agentBrowserStrings.cjs`
- `apps/desktop/agent-browser.html`、`apps/desktop/src/agentBrowserShell.ts`
- `TinadecTools/Tools/Browser/BrowserBridgeClient.cs`、`BrowserToolRuntime*.cs`、`BrowserSnapshotBuilder.cs`、`BrowserNavigationPolicy.cs`、`BrowserTools.cs`
- `TinadecCore/Abstractions/Ports/ToolApprovalPolicy.cs`、`ApprovalEvidenceProjector.cs`、`Governance/AutoApproveOptions.cs`、`Tools/ToolDispatcher.cs`
- `apps/desktop/src/lib/toolPresentation.ts`、`lib/conversation/toolSentence.ts`、`apps/desktop/src/agentPacks/GraphSeedPack/*`
