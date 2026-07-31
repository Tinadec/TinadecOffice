# Web 客户端：可行性与实现路径

**状态：** 阶段 1 已落地（`apps/web/`），阶段 2 未开始，阶段 3 已明确推迟
**目标：** 在不修改 `apps/desktop/` 任何源文件的前提下，让浏览器复用桌面渲染层。

## 结论

可行，且成本远低于预期。整个 renderer 对 Electron 的耦合面只有一个对象：`window.tinadec`。
其余部分（Vue 3、vue-router hash 模式、Monaco、xterm.js、marked、Tailwind v4）全部是浏览器原生能力。

Gateway 也已经预留了云端形态：`TINADEC_GATEWAY_MODE=cloud` 支持 `0.0.0.0` 监听、JWT/API Key 认证、
租户头透传与 CORS 白名单（`TinadecGateway/src/config.ts`、`src/auth.ts`）。
Core 侧 `ITenantContextAccessor` 是既定的请求隔离端口。

换言之：Web 化在架构上已经被预留过，缺的是最后一步。

## 平台契约：`window.tinadec`

`apps/desktop/electron/preload.cjs` 通过 `contextBridge` 暴露 `window.tinadec`，
类型定义在 `apps/desktop/src/env.d.ts:105-197`。全仓 17 个渲染层文件引用它。

**核心思路：把这个 preload 契约本身当作平台抽象层。**
Web 端提供一份同形状的浏览器实现（shim），在桌面 `main.ts` 执行前装载，整个 UI 即可原样启动。

被否决的方案：抽取 `apps/shared-ui` 公共包。那需要移动 180+ 文件并重写全部 import，
直接违反「不影响 desktop」的约束，也违反 Ponytail 第 2 条（Reuse Check）。

### 能力分类

| 分组 | 调用点 | 是否已 `?.` 保护 | Web 语义 |
|------|--------|------------------|----------|
| 窗口装饰（min/max/close、broadcastTheme） | 7 | 是 | 空实现 |
| 可分离面板（detach/reattach/panel 事件） | 8 | 是 | `detachPanel` 返回 `null`，标签页保持内联 |
| 桌面宠物（`pets.*`） | 12 | **否** | 必须提供全部方法的安全空值，否则页面抛错 |
| 终端（`terminal.*`） | 10 | 是（`isTerminalAvailable()`） | 阶段 1 不定义，走既有降级分支 |
| Gateway URL / 文件对话框 / 重启 | 6 | 部分 | 同源 origin、返回 `null`、`location.reload()` |

`pets.*` 是唯一的硬性风险点：`SettingsPage.vue` 与 `DesktopPetPage.vue` 里的调用没有可选链，
shim 必须补齐每一个方法。

### 包结构

```
apps/web/
├── index.html                 # 复用 desktop 的早期主题脚本与 splash
├── package.json               # @tinadec/web
├── vite.config.ts             # alias '@' -> ../desktop/src；publicDir -> ../desktop/public
└── src/
    ├── main.ts                # import './platform/webShim' 然后 import '@/main'
    └── platform/webShim.ts    # window.tinadec 的浏览器实现
```

`main.ts` 的 import 顺序是有语义的：shim 必须先于桌面引导装载。

根 `package.json` 的 `workspaces` 已是 `apps/*`，新包自动纳入。
`dev:web` 作为独立脚本提供，**不**加入默认 `dev` 并发链，避免拖慢桌面开发循环。

### 硬性约束

- `apps/desktop/` 零修改。发现无法绕开的问题就上报，不要就地改。
- 不复制、不移动桌面源码，只做 alias。
- 不在桌面源码里加 `if (isWeb)` 分支。所有平台差异收敛到 `webShim.ts` 一个文件，
  否则 desktop 会被慢慢污染。

## 现状盘点

| 层 | Web 就绪度 | 说明 |
|---|---|---|
| Core (48731) | 高 | 无 Electron 依赖；`Tenancy` 模块已存在 |
| Gateway (48730) | 高 | cloud 模式已支持 0.0.0.0 + 认证 + 租户 |
| Desktop renderer | 中高 | 纯 Vue/Vite；依赖库全部面向浏览器 |
| Electron main | 不适用 | 窗口 / PTY / 宠物 / 文件对话框属于平台能力 |

## 阶段划分

### 阶段 1 — 只读 Web 版（低风险，可独立交付）

**可用：** 聊天、会话/项目、任务图、审批 UI、上下文包、事件 SSE、
Model/Agent Center、Market、设置（除宠物与 Gateway URL）、Debug Studio、Monaco 代码查看。

**不可用：** 终端、桌面宠物、分离窗口、本地目录选择。

已完成并验证：
1. `apps/web/` 包，`vite.config.ts` 把 `@` alias 到 `../desktop/src`，
   `publicDir` 指向 `../desktop/public`，端口 5174（5173 留给 desktop）。
   `server.fs.allow` 必须包含仓库根目录，否则 hoisted 的字体/Monaco 资源会被 Vite 拦截。
2. `webShim.ts` — 全部是空实现与安全空值返回。
3. `gatewayUrl` 返回 `window.location.origin`，配合 Vite dev proxy（`/api`、`/docs`、`/ws`）
   或生产反向代理走同源，从根本上绕开 CORS。

浏览器实测结果（Playwright，无后端运行）：
- `#/` 与 `#/settings` 均挂载成功，**0 console error**
- 宠物设置页降级为空列表，不崩
- Gateway 地址字段因 `managed: true` 呈只读，符合预期
- 对照实验：直接以浏览器加载 **未注入 shim** 的 desktop 源码，
  `SettingsPage.vue:168` 立刻抛 `Cannot read properties of undefined (reading 'pets')`。
  这反向证明了 shim 补齐 `pets.*` 全部方法是必需的，不是防御性冗余。
- `App.vue` 页面切换时偶发的 Vue `parentNode` transition 报错，
  在 desktop 基线（同样浏览器环境、未注入 shim）中同样出现，属既有问题，与 Web 化无关。

### 阶段 2 — 终端（有阻塞，未开始）

两个障碍：

1. **Tool Runtime 服务不存在。** `TinadecGateway/src/websocket.ts:53` 已声明 `/ws/terminal`
   指向 Tool Runtime (48732)，`config.ts:72` 也已配置 `TINADEC_TOOL_RUNTIME_URL`，
   但服务本身没有——`TinadecTools/` 是 .NET 控制台原型宿主（stdin/stdout），不是 HTTP/WS 服务。
2. **Gateway 的 WS 代理是死代码。** `index.ts:822-838` 的 `/ws/terminal` handler 订阅了一个
   Bun pub/sub topic，计算出 `targetUrl` 后直接 `void targetUrl`，从未连接目标。
   `websocket.ts:93-139` 的 `createWsProxyHandlers` 实现了真正的双向代理，但没有任何路由调用它。
   三个 WS 路由（terminal / debug / collaboration）全是无效桩。

**PTY 该放在哪里：新建最小 Tool Runtime 服务（Node.js，48732）。**

- 不放 Gateway：`TinadecGateway/AGENTS.md` 明确禁止 Gateway 执行 PTY/shell，
  这是支撑无状态横向扩展的架构约束，不是空话；将来加入 file/Git/MCP 工具时还得把 PTY 拆出来。
- 不放 Core：Core 是 .NET 状态权威，Windows 上没有成熟的 .NET PTY 库
  （现有 `TinadecTools/Runtime/TerminalRunner.cs` 是重定向 stdio 的非交互执行，不是 PTY）。
- **运行时选 Node.js 而非 Bun**：`node-pty` 是 N-API 原生模块，用到 conpty/ioctl 等
  较深的 N-API 面，Bun 1.2 的兼容性未经验证，故障会以「resize 失效 / 信号丢失 / 输出乱码」
  这类交互期才暴露的形式出现。Tool Runtime 是独立服务，运行时选择被 Gateway 代理隐藏，
  没有理由在这里冒险。

WS 协议直接由现有 IPC 面（`preload.cjs:42-61`）导出，一个 WS 连接对应一个 PTY：

| Desktop IPC | WS 帧 |
|-------------|-------|
| `terminal:create` + options | WS upgrade URL query 参数 |
| `terminal:write` | `{"type":"write","data":"..."}` |
| `terminal:resize` | `{"type":"resize","cols":N,"rows":N}` |
| `terminal:destroy` | `{"type":"destroy"}` |
| `terminal:data:{id}` | `{"type":"data","data":"..."}` |
| `terminal:exit:{id}` | `{"type":"exit","exitCode":N}` |
| `terminal:get-shells` | HTTP `GET /api/v1/shells` |

工作量：Tool Runtime 约 200 行，Gateway 代理修复约 15 行，shim `terminal.*` 约 120 行。
渲染层零改动。

**锁定风险：基本没有。** shim 的 `window.tinadec.terminal` 就是协议契约，
服务端实现（重构、容器化、换语言）都不影响它。

### 阶段 3 — 多用户 / 公网部署（真正的难点，与 Web UI 无关）

实际风险集中在这里，且全部是后端问题：

1. ~~**JWT 签名未验证**~~ — **已修复。** `auth.ts` 现使用 WebCrypto 做 HMAC-SHA256 验签，
   强制 `alg === 'HS256'`（拒绝 `alg: none` 与算法混淆），base64url 补齐 padding，
   并在云端模式下**未配置密钥时拒绝 Bearer token**（fail-closed，旧行为是直接信任解码结果）。
   `authenticate()` 改为 async，唯一调用点 `index.ts:141` 的 `onRequest` 已加 `async`/`await`。
   本地模式（`authConfig === undefined`）行为完全不变。
   实测：伪造签名 / `alg:none` / 未配密钥三种攻击均被拒绝，合法签名正常通过。
2. **Core 租户身份是开发桩** — `TinadecCore/Tenancy/DevelopmentTenantContextAccessor.cs:15`
   在未启用开发身份时直接抛异常，需要真实的外部身份适配器。
3. **工具层是单工作区模型** — `TinadecTools` 在进程启动时把当前目录快照为唯一根目录。
   多租户需要 per-tenant 工作区隔离与进程隔离，这是最大的一块新增工作。
4. **沙箱账户** — `command_run` 走本地 `TinadecSandbox` Windows 账户 + UAC，
   服务器多用户场景需要重新设计（容器 / 每租户 worker）。

## 判断

- 阶段 1 是真正的低垂果实：几乎纯增量，desktop 零回归风险，随时可以砍掉。
- 阶段 3 的工作量大于阶段 1 与 2 之和，且与「Web UI」无关，本质是把本地单用户 harness
  改造成多租户服务。
- **建议近期把 Web 版限定为「局域网 / 单用户远程访问」**，跳过阶段 3，
  只做认证修复（第 1 项）。这个组合的价值/成本比最高。
