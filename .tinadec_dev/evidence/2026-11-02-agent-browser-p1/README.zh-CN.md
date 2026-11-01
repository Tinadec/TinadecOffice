# P1：桥 + 独立浏览器窗口（2026-11-02）

## 实现

- `apps/desktop/electron/browserBridge.cjs`：127.0.0.1 随机端口 + 随机 Bearer 令牌；令牌写 `state/agent-browser/bridge.json`（0600，Windows 依赖用户 ACL）；CDP 方法白名单（Page/DOM/Runtime/Input/Accessibility/Target attach）；`Page.navigate` 只允许 http(s)、拒绝内嵌凭据与元数据地址；脚本体拒绝 cookie/storage/fetch 等 token；每 session 串行；`/tabs/*`、`/screenshot`、`/dialog`、`/state`；审计 JSONL（5 MB 轮转）。
- `apps/desktop/electron/agentBrowser.cjs`：一 session 一窗口、多 `WebContentsView` 标签、独立 `persist:tinadec-agent-browser` 分区；attach 单 tab debugger；控制态 `agent|user`；弹窗/权限/下载/文件选择器/JS 对话框记为硬停止 notices；截图落 `state/agent-browser/shots/<session>/`；shell 命令走 sender 校验。
- `apps/desktop/electron/agentBrowserPreload.cjs` + `apps/desktop/agent-browser.html` + `apps/desktop/src/agentBrowserShell.ts`：卫星窗口本地 shell，只暴露 `browserShell:state|command|onState` 三通道；五语标签在 `agentBrowserStrings.cjs`。
- `apps/desktop/electron/main.cjs`：启动受管服务前启动桥并写令牌；`before-quit` / `will-quit` 停桥并删令牌；主窗口真正关闭/`window-all-closed` 关闭 Agent 浏览器；`tinadec:interface-locale` 同步 shell 语言。
- `apps/desktop/vite.config.ts`：`rollupOptions.input` 增加 `agentBrowser` 第二入口；打包后由 `app://bundle/agent-browser.html` 承载。

## 单测（27/27）

`node --test electron/browserBridge.test.cjs electron/agentBrowser.test.cjs electron/agentBrowserStrings.test.cjs`

覆盖：令牌文件写/删、401/403/413、越界 op、方法白名单、`javascript:` 与元数据地址、脚本 cookie 访问、CDP 结果 + notices、用户接管硬停止、弹窗打开时短路、同 session 串行、标签生命周期、对话框、弹窗/权限 notices、截图落盘、shell sender 校验、二次 attach 映射、五语文案键与占位符一致。

## 真实 Electron 集成探针（10/10）

`probe.cjs`（真 Electron + 真 `WebContentsView` + 真桥 + 真 HTTP 页面）：

1. 桥启动并写令牌文件；
2. `tabs/open` 创建真实窗口并加载页面；
3. 通过桥轮询到 `document.readyState === complete`；
4. `Accessibility.getFullAXTree` 返回按钮 `backendDOMNodeId`；
5. `DOM.getBoxModel` 直接收 backendNodeId；
6. 桥转发 `Input.dispatchMouseEvent` 后页面文本变为 `clicked`；
7. `screenshot` 落盘（约 23 KB）；
8. `state` 报告 1 个活动标签、control=agent；
9. `Browser.getVersion` 被拒（`browser_method_denied`）；
10. `setControl('user')` 后 `cdp` 被硬停止（`browser_user_in_control`）。

## 关键实现事实

- **必须先加载文档再 attach debugger**：第一次探针在全新的空白 `WebContentsView` 上 attach + `Page.enable` 会永久挂起（10 秒桥超时）。改为 `loadURL(url ?? 'about:blank')` 后再 attach，整条链 10/10 通过。P0 探针也是"先加载、后 attach"的顺序，这条已写进代码注释。
- `WebContentsView.setVisible(false)` 能隐藏非活动标签（P0 证实），P1 用它做多标签。
- `win.showInactive()` 不抢焦点；P0 证实未聚焦与隐藏时输入仍生效。
- shell 页面是独立 Vite 入口，不把主 renderer 的完整 `tinadec` preload 暴露给承载远程页面的窗口。

## 未验证（留到 P5 / P6）

- 真实应用（`npm run dev` / 打包 `app://bundle`）里 shell 页面的实际观感与 IPC 往返。
- `TINADEC_DEV_CDP_PORT` 下外部客户端已 attach 同一目标的真实冲突（只测了二次 attach 错误字符串）。
- 多标签在真实窗口里的像素/焦点行为。
- P6 卡片挂载与 `WebContentsView` 覆盖 overlay 的问题。
