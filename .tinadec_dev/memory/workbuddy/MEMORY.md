# TinadecOffice 项目长期笔记

## 终端能力架构（2026-08-31 定案）
- 双通道：用户终端 = Electron 直连 PTY（node-pty，`electron/terminalManager.cjs`）；agent 终端 = tool-core-gateway 路径（TinadTools `shell` 工具 → TinadCore 治理 dispatch → run 事件日志 → Desktop 回放+follow）。
- 管道协议 v2 有非请求响应的事件行 `{"kind":"event","call_id":N,...}`，call_id<=0 是广播（长驻会话输出/退出），由 `TinadecToolsProcessManager` 分流、`TerminalSessionEventBridge` 按 terminal_session_id 归属回写 run。
- 面板复用 terminal tab（AGENT 徽标来源标记）；对话内 shell 调用渲染 `TerminalCallBlock.vue`，跳转用 `openAgentTerminal`（幂等，`agent:<sessionId>` 实例 id）。
- 已知后续项：无 ConPTY（仅管道重定向）；长驻升级目前只认显式 `long_lived:true` 参数（自动升级未做）；Gateway `/ws/terminal` 桩未启用（SSE 已够用）。

## 桌面端窗口与本地资源（2026-10-08 定案）
- 打包态渲染层**不再用 `loadFile`**：由注册为 standard+secure 的 `app://bundle` 承载 `dist/`（`electron/appBundle.cjs`，含路径穿越防护/MIME/`/`→`index.html`）。四个窗口（main/panel/pet/debug-studio）的 `webSecurity` 已恢复默认，**不要再加回 `webSecurity:false`**——它会让预览面板里的异源 iframe 读到 `contextBridge`（含终端 IPC）。
- 用户本地媒体（背景图/视频）走 `tinadec-media://local/<base64url path>`（`electron/localMedia.cjs`）：只服务图片/视频扩展、支持 Range、**不发 CORS 头**。渲染层编码在 `useBackground.normalizeFileSource`（与主进程解码有跨语言往返用例），`read-image-data-url` 经 `sourceToMediaPath` 兼容三种形式。**网关 CORS 必须放行 `app://bundle`**，否则打包态请求被拒。
- 终端 IPC 不可再放开：`terminal:create` 只认 shell 目录内的条目、argv 取目录值、id 服务端生成；write/resize/destroy/snapshot 校验 sender 归属。
- 外链统一走 `electron/externalLinks.cjs`（`setWindowOpenHandler` + `will-navigate`，仅 http/https 交给系统浏览器）。

## 侧栏辅助控件的显隐（2026-10-21 定案）
- **`.sidebar-extra` 会吃掉同特异性的「默认隐藏」**：它只声明 `opacity: 1`（一个类，0-1-0），却写在 `styles.css` 后段，所以 `.workspace-section-add { opacity: 0 }` 这类基础规则被它整条盖掉，控件变成常显。隐藏态必须写成 `.workspace-section .workspace-section-add`（0-2-0）或更高，别靠书写顺序。
- 悬停区域：`.workspace-section`（标题 + `#sidebar-workspaces` 滚动区）是「新建工作区」的唯一显隐范围；`.workspace-section` 接管 `flex: 1/min-height: 0`，`.sidebar-list` 保留滚动端口（列表折叠时底部区域仍被钉住）。隐藏态配 `pointer-events: none`，避免隐形可点热区。
- 验证 CSS 级联**不必起 Electron**：happy-dom 注入整份 `styles.css` + 最小 DOM，`getComputedStyle` 会按真实级联求值（`:hover` 不求值），可对照修复前后。单文件 vitest 本机需 `--pool=threads`：默认 forks worker 报 `Timeout waiting for worker to respond`，且 CLI 不认 `--poolOptions.*`。

## 侧栏区块与排序（2026-10-21 定案）
- 工作区列表里的**最近对话 / 最近活动 / 自由对话 / 项目分组是同级兄弟区块**，不是"工作区整块 + 三个开关"。先后存 `tinadec.sidebar.workspaces.v1` 的 `sectionOrder`（默认 `recent, activity, free, projects`，即上线时的渲染顺序）；齿轮面板的每一行就是其中一块，右侧把手是拖拽源、整行是落点，`Alt+↑/↓` 等价。
- **关掉的块保留槽位**：`listItems` = `sectionOrder` × 四个开关，重开回原位。渲染分支必须写 `item.kind === 'workspace'` 而不是 `v-else`，否则模板里联合类型收窄不到 `item.workspace`；`v-for` 与 `v-if` 不能同挂一个节点。
- **自由对话不参与 `order`**（`reorderableWorkspace` 守卫）：它靠区块顺序定位，右键上移/下移走 `stepSection('free')`；项目行之间仍用 `order`，`reconcile` 仍以自由对话为锚插入新工作区。选项行用 `div` 不用 `<label>`，把手 `@click.stop`。
- 活动视图的状态**只归并 Core 的 12 态词表**（`TinadecGateway/src/contracts/runStatuses.ts` 是唯一出处，禁止自造 running/ready），词表外与请求失败都报 unknown，不伪装成"无任务"；无跨会话活动端点，只读列表渲染的 5 个会话 + `updated_at` 复用 + 60s TTL。

## 依赖行为判据（别照抄 issue/文档的结论）
- **elysia 1.4.29 会自己钳制 2xx**：`dist/compose.js` 的路由 catch 里 `if(!set.status||set.status<300)set.status=error?.status||500`。所以"路由暂存 201 后出错 → 响应 201"这类指控**默认先复现再采信**；但 4xx/5xx 会被保留。
- 网关上游不可达必须回 **502**（`src/upstreamFailure.ts`），且**不能**走 `toProblemDetails`（`normalizeCode` 会把 code 抹成 `conflict`，丢掉 `CORE_UNREACHABLE` 指纹）。`TINADEC_GATEWAY_TIMEOUT_MS` 至今无人消费。
- 桌面端 `package.json` 的 `test` 脚本是**显式文件清单**：新增 electron 测试文件必须同时加进那一行，否则静默不跑。

## 发布说明由 AI 手写（2026-10-20 定案）
- **每次发版，AI 要自己写更新说明，不能只依赖 `--generate-notes`**。用户的原话：`--generate-notes` 出来的是 `Fix/model center unify` 这种 PR 标题，解释不了任何事，也不回答"我为什么要升级"。
- 正文主体写在 `docs/releases/<tag>.md`（带 v 的文件名，如 `v0.3.0.md`），结构固定：`## What's new`（3–6 条粗体小标题，讲用户得到什么）+ `## What's changed`（新能力 / 修复 / 性能 / 升级须知）。
- 规程全文见 `docs/release-notes.zh-CN.md`。取材顺序：`git log --oneline <prev>..<tag> --no-merges` → `git diff --shortstat` → `gh pr list --state merged` → **关键改动读实现**。数字只取可复验来源，不许估算。
- 排版顺序是刻意的：**头图 → What's new → What's changed → 折叠的 Full commit log → 折叠的 Installation → 折叠的 Known boundaries**。安装表与平台边界每版重复、对老用户是噪音，必须收进 `<details>`。
- 拼装是**单一实现** `scripts/release-body.mjs`：CI（`desktop-release.yml` 的 `Compose release notes`）与本地补写都调它。`node scripts/release-body.mjs v0.3.0 --update` = 重新拼装并 `gh release edit`（只改正文，不动 tag 与资产）；`--out <file>` 只预览。
- 缺 `docs/releases/<tag>.md` 时脚本**不失败**（发版不该被文案卡住），只升 `::warning::`，并在 body 留 `<!-- release-notes: ... is missing -->` 便于事后 grep。
- 两个坑：`gh release create` 的 `--notes-file` 与 `--generate-notes` **互斥**；generate-notes 的 body **自带 `## What's Changed` 标题**，拼接时不要重复加。
- **发版前先看 main 最后一次 `Desktop release` 是不是绿的**。`create-version.yml` 只校验版本号与 tag 是否已存在，**不跑测试**；tag 构建的红灯完全继承 main 的状态，而 main 的 push 构建失败不会通知任何人。v0.4.1 首次 tag 就是被 f7d7aa82/b97834ad 遗留的三处过时断言打红（详见 `2026-10-28.md`）。查法：`gh run list --workflow=desktop-release.yml --limit 3`。
- **tag 已推送但 release 未创建时，删 tag 重建是安全的**：build job 红则 release job 因 `needs` 不跑，`gh release view <tag>` 报 not found 即可确认。删远端 tag 后重新 dispatch `create-version.yml`，它会从 main HEAD 重新打 tag。不要为此升版本号。
- 版本号要改**两处**：`apps/desktop/package.json` 与 `package-lock.json` 中 `apps/desktop` 包的 version。预览正文时 tag 还不存在，`release-body.mjs` 要用 `--auto-notes <假文件>` 跳过 `generate-notes` 的网络调用。

## 环境硬约束
- **TinadTools 宿主禁用反射序列化**（`JsonSerializerIsReflectionEnabledByDefault=false`）：动态 JSON payload 必须用 `Utf8JsonWriter` 手写或 source-gen context（`ShellToolJsonContext` 等），否则运行时 InvalidOperationException。
- **TinadTools 协议冒烟**：`scripts/terminal_protocol_smoke.py`（驱动真实子进程验证 one-shot/长驻/控制/事件顺序）。
- **TinadecTools.Tests 7 个失败为环境性**：Windows 符号链接创建无权限（CreateSymbolicLink 激活上下文错误），GitReadTools/GitLogTools 的 link-traversal 用例，与功能改动无关。
- **openapi 快照自愈流程**（Core 与 Gateway 均已改为"先写后断言"）：跑测试 → 快照自动再生成 → `git diff --exit-code` 把关 → 有意变更则提交快照。
- **本仓库 .git 曾于 2026-08-31 严重损坏**（.pack 全丢、refs 丢失），已从 origin (github.com/Tinadec/TinadecOffice) 完整重建；修复流程见 `.workbuddy/memory/2026-08-31.md`。对 .git 的批量写操作（stash/rebase 等）建议谨慎、必要时脱离沙箱执行。
