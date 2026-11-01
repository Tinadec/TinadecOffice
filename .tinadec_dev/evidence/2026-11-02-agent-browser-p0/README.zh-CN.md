# P0：Electron/CDP 硬事实探针（2026-11-02）

运行：`npx electron .tinadec_dev/evidence/2026-11-02-agent-browser-p0/probe.cjs`（Windows 本机，Electron 43.3.0）。
原始输出：`probe-output.txt`；探针源码：`probe.cjs`（临时验证用，不进入产品代码）。

## 读数（17/17 通过）

| 断言 | 结果 | 备注 |
|---|---|---|
| `Accessibility.getFullAXTree` 暴露 `backendDOMNodeId` | 通过 | 15 个节点，按钮 `backendDOMNodeId=6` |
| `DOM.pushNodesByBackendIdsToFrontend` | 通过 | 必须先 `DOM.getDocument`，否则 `Document needs to be requested first` |
| `DOM.getBoxModel { backendNodeId }` | 通过 | **直接收 backendNodeId**，不必先 push |
| `DOM.scrollIntoViewIfNeeded { backendNodeId }` | 通过 | 直接收 backendNodeId |
| `DOM.resolveNode` + `Runtime.callFunctionOn` | 通过 | 固定脚本，返回 `BUTTON:Click me` |
| `Input.dispatchMouseEvent` 真实点击 | 通过 | 点击后页面文本变为 `clicked`；`devicePixelRatio=1.75`，坐标仍用 CSS px |
| 窗口未聚焦时点击 | 通过 | `document.hasFocus()=false` 仍生效 |
| 窗口隐藏时点击 | 通过 | `win.hide()` 后仍生效 |
| `Page.captureScreenshot` | 通过 | PNG 约 25 KB（base64） |
| `Page.javascriptDialogOpening` 事件 | 通过 | `alert` 事件带 message/frameId/url |
| `Page.handleJavaScriptDialog` | 通过 | 解除阻塞后页面继续执行 |
| `Page.getFrameTree` 的 `loaderId` | 通过 | 导航前后变化 |
| 导航后旧 `backendDOMNodeId` | 失效 | `resolveNode` 失败 → 用 loaderId 判失效 |
| `Page.setInterceptFileChooserDialog` + `Page.fileChooserOpened` | 通过 | `mode=selectSingle` |
| `View.setVisible` | 存在 | 多标签可用 |
| 二次 `debugger.attach` | 确定性报错 | `Debugger is already attached to the target` |
| `DOM.focus` + `Input.insertText` + 回读 | 通过 | 值 `hello-probe` |

## 对实现的影响

1. **每次动作前重新取几何**：第一次点击失败的原因不是输入不可用，而是隐藏时取 `getBoxModel`、显示后布局/DPR 变化导致坐标过期。动作链必须是 `scrollIntoViewIfNeeded → getBoxModel → hit test → Input`，不缓存跨布局变化的 rect。
2. **坐标用 CSS px**，不做 DPR 缩放；`Input.dispatchMouseEvent` 与 `getBoxModel` 同一坐标系。
3. **ref 失效用 loaderId 判定**，不能只依赖 `resolveNode` 失败；两者都记录。
4. **不需要抢焦点**：窗口可见但未聚焦、甚至隐藏时输入都生效；独立窗口用 `showInactive()` 不会打扰用户。
5. **`pushNodesByBackendIdsToFrontend` 不是必需**：`getBoxModel` / `scrollIntoViewIfNeeded` / `focus` 都直接收 backendNodeId；保留它只作为后备。
6. **弹窗/文件选择器都可拦截**：主进程订阅 debugger `message` 即可，不需要 WebSocket 事件通道；文件选择器点击会发 `Page.fileChooserOpened`。
7. **二次 attach 的错误形状固定**：映射为 `browser_debugger_unavailable`，不崩。
8. **`View.setVisible` 存在**：独立窗口内多标签可用 `WebContentsView` + `setVisible(false)` 实现。

## 未验证（留到后续）

- 跨进程 iframe / OOPIF 的 `Target.attachToTarget(sessionId)` 深层快照。
- 打包态 `app://bundle` 上的第二 HTML 入口（P1 验证）。
- `TINADEC_DEV_CDP_PORT` 打开时外部客户端已 attach 的真实冲突路径（探针只验证了二次 attach 的错误字符串）。
