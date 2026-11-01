# P5：真实 TinadecTools → 桥 → 页面 端到端（2026-11-02）

运行：`npx electron .tinadec_dev/evidence/2026-11-02-agent-browser-p5/probe.cjs`（Windows 本机，Electron 43.3.0）。
原始输出：`probe2.out`。探针使用真实 Electron 主进程、真实 `WebContentsView`、真实 bridge、**真实 `TinadecTools.exe` 子进程**（`TINADEC_HOME` 指向同一临时 office root），以及本地 HTTP 夹具页。

## 全部通过（21 项）

| 断言 | 证据 |
|---|---|
| 工具进程 `#manifest` 含 browser 面 | 65 个工具 |
| `browser_navigate` 打开真实窗口并等待 document ready | `ok:true, ready:true` |
| snapshot 为按钮/输入框分配 ref | `button [ref=1]`、`textbox "Name " [ref=2]` |
| `browser_click` 真实鼠标事件更新页面 | snapshot 出现 `clicked` |
| `browser_type` 聚焦/输入/回读 | `value:"hello-e2e", verified:true` |
| snapshot 显示输入值 | `value="hello-e2e"` |
| `browser_wait` 观察到延迟 DOM | `ok:true, reason:"text"` |
| `browser_batch` 两步串行 + 最终快照 | `steps[0/1].ok:true`，快照 `value="batch-text"` |
| `browser_screenshot` 落盘 | PNG 约 46 KB |
| `browser_tabs_list` | 一个 active tab |
| `javascript:` 导航被拒 | `browser_navigation_blocked` |
| **open shadow root 按钮** 点击 | snapshot 出现 `shadow-clicked` |
| 弹窗被拒并作为 notice 返回 | `browser_popup_blocked` |
| 下载硬停止 | 下一命令 `browser_download_started`（`x.txt`） |
| 文件选择器硬停止 | `browser_file_chooser_opened`（`selectSingle`） |
| JS 对话框暂停 agent | `browser_dialog_open` |
| 处理对话框后继续 | 下一次 snapshot `ok:true` |
| 用户接管硬停止 | `browser_user_in_control` |
| 交还控制后恢复 | 下一次 snapshot `ok:true` |

## 本轮探针抓出的两个真缺陷（已修）

1. **快照把 `invalid:false` 误报成 `invalid`**：`BrowserSnapshotBuilder` 对 `invalid` 属性只区分 True 与 String，AX 树把有效控件报成字符串 `"false"`，于是每个按钮/输入框都带 `invalid`。改为：True 或非 `"false"` 的字符串才追加。
2. **下载通知可能赶不上点击回执**：`will-download` 是异步事件，点击工具读取 notices 时它可能还没到；同时 `DownloadItem.getWebContents()` 不一定能定位到发起 tab。现在：
   - 管理器在无法定位发起者且只有一个 session 时，把通知记到该 session；多 session 仍保持隔离。
   - 工具层在**下一次命令**的 `state` notices 中捕获它并硬停止（P5 已验证 `browser_download_started`）。
   - 文件选择器同理，在同一套 notices/seq 机制下工作。

## 边界（诚实）

- 探针的 shell 页面是 `about:blank`，**工具栏/接管按钮的真实 UI** 没有在此验证；P5 证明的是 agent 工具链与页面语义。
- JS 对话框由探针直接调用 `manager.handleDialog` 处理；v1 **没有暴露 `browser_dialog` 工具**，agent 停在硬停止，用户可在窗口里处理或由宿主代答。这是有意的最小安全面。
- 探针进程在 `app.exit` 处偶发不返回（Electron 子进程持有管道），已用 taskkill 清理；不影响 PASS 读数。P1 探针早前留下的孤儿进程也已清理。
- 没有经过 Core/Gateway/真实模型：P4 的 Core 侧由 GraphSeedPack 安装测试、审批/证据/重试测试覆盖；P5 验证的是工具 provider 到桥到页面。
- 多平台、打包 `app://bundle` shell 页面、真实工具栏 UI、P6 卡片挂载仍未验证。
