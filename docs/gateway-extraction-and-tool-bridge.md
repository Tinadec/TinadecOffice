# Gateway 抽取与 Rust 删除修复清单

**生成日期：** 2026-07-09
**范围：** 将 `apps/gateway` 含其内嵌工具整体抽出为顶层独立组件；删除 Rust native 层；gateway 内 native-backed 工具改为接驳 C# Tool layer（TinadecTools）stdio 实例。
**不做：** Tool layer 的技术栈/API/端口/工具实现/进程编排；Tool layer 如何向 Core 注册工具。

---

## 一、背景与目标架构

目标四组件关系：

```
Tinadec Core (AI Agents) ←→ Gateway ←→ Desktop (UI)
       ↑↓                        ↑↓
  Tool layer ←→ Things outside Tinadec
```

- Gateway 是纯 BFF 代理 + 内嵌工具宿主（本次随 gateway 一起抽出，保留原状）
- Tool layer（TinadecTools，C#）是 stdin/stdout 常驻进程，按 workspace 启动实例、设置 pwd
- Rust native 层整体删除

---

## 二、TinadecTools 协议与能力速览（接驳依据）

### 启动与调用
- 入口 [Program.cs](file:///workspace/TinadecTools/Program.cs)：`GeneratedToolRegistry.RegisterAll()` → stdin/stdout JSON 常驻循环
- 协议 [ToolCalling.cs](file:///workspace/TinadecTools/Abstractions/ToolCalling.cs)：
  - 请求 `ToolCallRequest<TParams>`：`tool_id`、`session_id`、`toolcall_id`、`approved`、`params`
  - 响应 `ToolCallResponse<T>`：`call_id`、`success`、`result`；错误 `ToolCallErrorResponse`：`call_id`、`success`、`error`
- 注册 [ToolRegistry.cs](file:///workspace/TinadecTools/Abstractions/ToolRegistry.cs)：静态 `[ToolFunction(id)]` + 有状态 `ToolHandlerBase<,>`；`RequiresApproval` 在注册层拦未批准调用
- **不感知 workspace**：路径解析基于进程 CWD（`Path.GetFullPath`），无 workspace root 参数 → 接驳时需按 workspace 启动实例并设置 CWD/pwd
- **有状态**：FileSlot 缓存（打开 FileStream + RW锁 + 行索引 + hash）、MCP 连接池 → 实例需常驻复用，勿每次调用重启

### 已实现工具（15 个）
| tool_id | 能力 | approval | 状态 |
|---|---|---|---|
| `read_file` | 文件读，带行 hash、行索引、缓存 | 否 | 有状态 |
| `replace_lines` | 行替换（per-line hash 锚校验） | 是 | 有状态 |
| `replace_bytes` | 字节替换（file_hash 校验） | 是 | 有状态 |
| `insert_bytes` / `insert_byte` | 字节插入 | 是 | 有状态 |
| `delete_bytes` | 字节删除 | 是 | 有状态 |
| `insert_line` / `delete_line` | 行插入/删除 | 是 | 有状态 |
| `file_search` | ripgrep 内容搜索（支持 glob/-F/-C/-i） | 否 | 无状态 |
| `command_run` | 任意命令执行（**非沙箱**） | 是 | 无状态 |
| `mcp_list` / `mcp_search` / `mcp_invoke` | MCP stdio 透传 | invoke 是 | 有状态(连接池) |
| `echo` / `stateful_echo` | demo | 否 | — |

### 不具备的能力（接驳时需注意）
无 git、无 scaffold/template、无 unified-diff `apply_patch`、无 `list_directory`、无 code_editor、无交互式 terminal PTY、无 `sandbox_exec`（有非沙箱 `command_run`）、无 `review_format`、无独立的 `glob_search`/`grep_content`（合并进 `file_search`）。

### ripgrep 依赖
[TinadecTools.csproj:40-45](file:///workspace/TinadecTools/TinadecTools.csproj#L40) 构建时条件拷贝 `..\native\rg\rg(.exe)`；运行时解析顺序 `TINADEC_TOOLS_RG_PATH` → bundle → 自动下载 GitHub releases（[RipgrepRunner.cs:57-65](file:///workspace/TinadecTools/Tools/Search/RipgrepRunner.cs#L57)）。
**影响**：删 `native/` 后 bundle 路径失效（Condition 不满足自动跳过，不报错），退化为运行时自动下载。建议把 rg 二进制目录迁到 TinadecTools 自有目录或依赖 env/自动下载（Tool layer 团队处理，本清单仅标注影响点）。

---

## 三、工具对照与接驳方案

### 3.1 gateway 8 个 native-backed 工具 → 接驳 Tool layer
Rust 删除后，这些工具的 `tryExecuteNativeTool` 执行链断裂。接驳方式：**接口/路由不动，executeCodeTool 实现从 spawn Rust 二进制改为调用按 workspace 缓存的 Tool layer stdio 实例**。

| gateway 工具 id | Tool layer tool_id | 接驳 | 说明 |
|---|---|---|---|
| `read_file` | `read_file` | 直接映射 | 参数适配（filepath/start_row/end_row） |
| `grep_content` | `file_search` | 直接映射 | rg 内容搜索 |
| `search_files` | `file_search` | 映射 | 模糊文件名搜索走 rg |
| `glob_search` | `file_search` | 映射（glob 参数） | rg -g 近似 glob |
| `sandbox_exec` | `command_run` | 映射 | 注意：Tool layer 非沙箱，语义降级 |
| `apply_patch` | `replace_bytes`/`replace_lines`/`insert_*`/`delete_*` | 需薄适配层 | Tool layer 是字节/行原语，无 unified-diff 解析；暂可降级 unavailable 或后续补 patch 解析 |
| `list_directory` | 无 | 暂 unavailable | 可用 `command_run` 跑 `ls` 组合，或 Tool layer 后补 |
| `review_format` | 无 | 暂 unavailable | 后续 Tool layer 补 |

### 3.2 gateway TS 实现的工具 → 保留原状
下列工具为纯 TS 实现，不依赖 Rust，删除 Rust 不影响其运行。本次随 gateway 整体抽出，保留原状。

| gateway 工具 | 保留理由 | 后续 |
|---|---|---|
| `git_worktree_manager`(~2000 行) | Tool layer 完全无 git；迁移成本高 | 标注后续 Tool layer 接管候选 |
| `code_editor`(文件读写) | Tool layer 有更强 FileRW，但 gateway 版较简单且不依赖 Rust | 标注可由 Tool layer read_file/replace_* 替代 |
| `terminal`(PTY) | Tool layer 无交互式 PTY | 保留 |
| `project_templates/scaffold` | Tool layer 无 | 保留 |
| `bash_environment`/`language_runtime_probe`/`debug_session` | 双方都是 stub | 保留 |

---

## 四、执行清单

### 阶段 0：基线验证（只读）
- [ ] `npm run build -w @tinadec/gateway && npm run test -w @tinadec/gateway`
- [ ] `dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal`（Windows PowerShell 先 `Remove-Item Env:Version/Ice-Version`）

### 阶段 1：删除 Rust native 层
**物理删除**
- [ ] 删除 `native/` 整个目录
- [ ] 删除 `.cargo/config.toml`

**脚本/配置清理**
- [ ] [package.json:15-16](file:///workspace/package.json#L15) — 删除 `build:native`、`build:native:release` 脚本（硬编码 D 盘 cargo 路径）
- [ ] [.gitignore:14,17-18](file:///workspace/.gitignore#L14) — 删除 `native/target/`、`native/codex-src/` 条目

**Core C# 清理**
- [ ] [DoctorService.cs:16-17](file:///workspace/src/TinadecCore/Services/DoctorService.cs#L16) — 删除 `cargo`、`rustc` 两个 probe

**Gateway 代码清理（native 执行路径）**
- [ ] [codeTools.ts:19](file:///workspace/apps/gateway/src/codeTools.ts#L19) — status 联合类型删除 `'native'`
- [ ] [codeTools.ts:54](file:///workspace/apps/gateway/src/codeTools.ts#L54) — `CodeToolSpec` 接口删除 `nativeBacked?: boolean`
- [ ] [codeTools.ts:193-250](file:///workspace/apps/gateway/src/codeTools.ts#L193-L250) — TOOL_SPECS 中 9 个 native-backed 工具的 `nativeBacked: true` 标记删除（spec 本身保留作 catalog）
- [ ] [codeTools.ts:447-452](file:///workspace/apps/gateway/src/codeTools.ts#L447-L452) — `executeCodeTool` 中 `if (spec.nativeBacked) { tryExecuteNativeTool }` 分支改为接驳 Tool layer（见阶段 2）
- [ ] [codeTools.ts:479](file:///workspace/apps/gateway/src/codeTools.ts#L479) — fallback `'native_runtime: pending'` 分支删除
- [ ] [codeTools.ts:3066-3162](file:///workspace/apps/gateway/src/codeTools.ts#L3066-L3162) — 删除 `tryExecuteNativeTool`、`nativeRuntimePath`、`resolveNativeBinary`（含 `TINADEC_CODE_NATIVE_BIN`、`../../..` 回 repo root、`native/target/` 查找）

**注意保留**
- [codeTools.ts:139](file:///workspace/apps/gateway/src/codeTools.ts#L139) `package_manager: 'cargo'` 的 rust-cli 项目模板**保留**（脚手架模板，不依赖本机 Rust toolchain）

### 阶段 2：Gateway 接驳 Tool layer stdio
**新增 Tool layer 进程管理器**（gateway 内，按 workspace 缓存实例）
- [ ] 新增 `apps/gateway/src/toolLayerBridge.ts`：
  - 按 workspace 路径 spawn TinadecTools 进程，设置 `cwd` = workspace
  - 缓存 workspace→进程实例映射（Tool layer 常驻、有状态，复用）
  - 提供 `callTool(workspace, toolId, params, approved)`：序列化 `ToolCallRequest<JsonElement>` → 写 stdin → 读 stdout 一行 → 反序列化 `ToolCallResponse<JsonElement>`
  - 进程生命周期：lazy spawn、退出时 dispose、异常重启
  - TinadecTools 二进制路径可配（env `TINADEC_TOOLS_BIN`），不硬编码 monorepo 相对路径

**改写 executeCodeTool 的 native-backed 分支**
- [ ] [codeTools.ts:447-452](file:///workspace/apps/gateway/src/codeTools.ts#L447-L452) — 从 `tryExecuteNativeTool` 改为调用 `toolLayerBridge.callTool(...)`，按上表映射 gateway 工具 id → Tool layer tool_id，参数适配转换
- [ ] 审批门保留：executeCodeTool 转发前仍跑 `codeToolRequiresApproval`/`codeToolApprovalBlockFor`（[codeTools.ts:327-439](file:///workspace/apps/gateway/src/codeTools.ts#L327-L439)）；Tool layer 侧 `RequiresApproval` 工具也校验 `approved` 字段（双保险）
- [ ] `list_directory`/`review_format`/`apply_patch`(unified-diff) 暂返回 unavailable 结构化错误，不阻塞 gateway

### 阶段 3：MCP 处理
- [ ] gateway `src/mcp/`（[mcpRoutes.ts](file:///workspace/apps/gateway/src/mcp/mcpRoutes.ts)、[McpConnectionManager.ts](file:///workspace/apps/gateway/src/mcp/McpConnectionManager.ts)、[McpClient.ts](file:///workspace/apps/gateway/src/mcp/McpClient.ts)）**保留原状**随 gateway 抽出（Tool layer 已有 mcp_list/search/invoke 但 mcp-passthrough 机制由 Tool layer 团队后续接入，本次不动 gateway MCP 实现）
- [ ] 标注：gateway MCP 与 Tool layer MCP 后续统一为 mcp-passthrough，本次不迁移

### 阶段 4：Gateway 物理迁移到顶层
- [ ] `git mv apps/gateway gateway`（保留 git 历史）
- [ ] 根 [package.json:7](file:///workspace/package.json#L7) — workspaces 改为 `["apps/*", "gateway"]`（保持单仓 dev 体验）
- [ ] 根 package.json 脚本：`dev:gateway`/`build`/`test`/`dev`(concurrently) 中 gateway 部分路径更新
- [ ] `cd gateway && npm install` 确认依赖独立可解析
- [ ] 确认 gateway 源码无其他 `apps/gateway` 或相对 repo root 硬编码路径（native 路径已在阶段 1 删除）

### 阶段 5：Desktop 最小改动
- [ ] [apps/desktop/src/components/PreviewBrowserPanel.vue:16](file:///workspace/apps/desktop/src/components/PreviewBrowserPanel.vue#L16) — 硬编码 `http://localhost:48730/docs` 改为 `window.tinadec?.gatewayUrl?.() + '/docs'`
- [ ] [apps/desktop/src/toolCatalog.ts:115](file:///workspace/apps/desktop/src/toolCatalog.ts#L115) — source precedence 中 `'codex-rust': 2` 处理（见阶段 6 Core 决策）
- [ ] Desktop 工具组件中 `source === 'codex-rust'` 标签：[ToolCatalogBrowser.vue:75,129](file:///workspace/apps/desktop/src/components/tools/ToolCatalogBrowser.vue#L75)、[ToolInvocationCard.vue:52](file:///workspace/apps/desktop/src/components/tools/ToolInvocationCard.vue#L52)、[ToolExecutionTimeline.vue:59](file:///workspace/apps/desktop/src/components/tools/ToolExecutionTimeline.vue#L59)、[ToolStatsDashboard.vue:128](file:///workspace/apps/desktop/src/components/tools/ToolStatsDashboard.vue#L128)、[SettingsPage.vue:353](file:///workspace/apps/desktop/src/pages/SettingsPage.vue#L353) — 视 Core 侧 source 名决策更新
- [ ] [apps/desktop/src/toolCatalog.test.ts](file:///workspace/apps/desktop/src/toolCatalog.test.ts) — codex-rust fixture 更新
- [ ] preload.cjs / api.ts / env.d.ts **不动**（Desktop 永远只调 Gateway，契约不变）

### 阶段 6：Core 侧 codex-rust 清理（仅清理 Rust 专属硬编码，不重设计 Tool layer 契约）
> 本阶段只删除因 Rust 移除而失效的硬编码；Core→Gateway 工具调用链（CodeToolClient/McpGatewayClient 指向 `TINADEC_GATEWAY_URL`）**保留不动**（工具仍嵌在 gateway 内执行）。

- [ ] [ToolRegistryService.cs:6-90](file:///workspace/src/TinadecCore/Services/ToolRegistryService.cs#L6-L90) — `CodexCapabilityProvider`（8 个 codex-rust source 工具描述符）：**保留**（工具描述仍有效，执行经 gateway），仅清理 Rust 相关文案；source 名 `codex-rust` 是否改名待 Tool layer 接驳后统一（本次可先保留避免大面积断言改动）
- [ ] [CodexRuntimeAdapters.cs:7-23](file:///workspace/src/TinadecCore/Services/CodexRuntimeAdapters.cs#L7-L23) — `CodexRuntimeKernelAdapter`：**保留**（描述能力，不依赖 Rust 二进制）
- [ ] [CodeToolClient.cs:15-17](file:///workspace/src/TinadecCore/Services/CodeToolClient.cs#L15) — source 校验 `codex-rust`/`code` **保留**（经 gateway 执行仍有效）
- [ ] [Program.cs:29](file:///workspace/src/TinadecCore/Program.cs#L29) — CORS 保留（无变化）
- [ ] [Program.cs:54-70](file:///workspace/src/TinadecCore/Program.cs#L54) — CodeToolClient/McpGatewayClient/McpToolInvocationAdapter HttpClient URL **保留指向 `TINADEC_GATEWAY_URL`**（工具仍嵌 gateway）
- [ ] [DoctorService.cs:16-17](file:///workspace/src/TinadecCore/Services/DoctorService.cs#L16) — 已在阶段 1 删除 cargo/rustc probe
- [ ] Core 测试中 codex-rust 断言**保留**（source 名未改）

> 若后续 Tool layer 接驳后决定统一 source 名，再单独立项改 Core/Desktop/Tests 的 `codex-rust` 字面量。本次不触发该连锁改动。

### 阶段 7：文档与根配置同步
- [ ] [CLAUDE.md](file:///workspace/CLAUDE.md) — 删除 Rust toolchain 前置要求、`native/Cargo.toml` 条目、`native/target/` 禁编目；文件图 `apps/gateway` → `gateway`；修正 stale `@tinadec/code`/`dev:code`
- [ ] [AGENTS.md](file:///workspace/AGENTS.md) — 删除 Native 层整节；STRUCTURE 树删 `native/`、`apps/gateway`→`gateway/`；WHERE-TO-LOOK/CODE MAP 路径更新；CONVENTIONS 删 native 约定；更新元数据 `Last Updated`/`Last Updated By`/`Branch`
- [ ] [docs/architecture.md:6](file:///workspace/docs/architecture.md#L6) — 删 native/glue 行
- [ ] [docs/startup.md:186-221](file:///workspace/docs/startup.md#L186-L221) — 删 Native Rust Glue 整节；修正 `@tinadec/code` 残留
- [ ] [docs/architecture-compliance-verification.md](file:///workspace/docs/architecture-compliance-verification.md)、[docs/ai-tools-*.md](file:///workspace/docs/ai-tools-integration-guide.md)、[docs/agent-harness-product-model.*.md](file:///workspace/docs/agent-harness-product-model.zh-CN.md) — native/Rust 层描述清理
- [ ] [README.md:8](file:///workspace/README.md#L8) — 删 native 行
- [ ] [.ponytail/rules.md:54](file:///workspace/.ponytail/rules.md#L54)、[.ponytail/config.json:37-44,51](file:///workspace/.ponytail/config.json#L37) — nativeLayer 配置节删除
- [ ] [scripts/test-ai-tools.ps1:215](file:///workspace/scripts/test-ai-tools.ps1#L215) — 删 native 路径检查
- [ ] [apps/gateway/AGENTS.md](file:///workspace/apps/gateway/AGENTS.md) → 迁为 `gateway/AGENTS.md`，更新目录位置、命令、删 native binary 段落、注明 code-tools native 分支改为接驳 Tool layer stdio

### 阶段 8：全量验证
- [ ] `cd gateway && npm run build && npm test` — gateway 独立可构建
- [ ] `dotnet test tests/TinadecCore.Tests -v minimal` — Core 测试通过
- [ ] `npm run dev` — Core/Gateway/Desktop 三进程启动
- [ ] 手动验证：gateway 接驳 Tool layer 后 `read_file`/`file_search` 类工具经 workspace 实例执行
- [ ] 手动验证：Desktop → Gateway 工具调用链（terminal、git、code_editor 保留原状的 TS 工具仍工作）
- [ ] Swagger `/docs` 可访问
- [ ] 全仓搜索确认 Rust 残留为零：`codex-rust|tinadec-code-native|codex-apply-patch|codex-exec-server|tinadec-core-native|TINADEC_CODE_NATIVE_BIN|nativeRuntimePath|resolveNativeBinary`（历史 spec `.trae/` 除外）

---

## 五、环境变量契约

| 环境变量 | 使用者 | 默认值 | 说明 |
|---|---|---|---|
| `TINADEC_GATEWAY_PORT` | Gateway 自身 | 48730 | Gateway 监听端口 |
| `TINADEC_GATEWAY_URL` | Desktop(preload)、Core(CORS、HttpClient) | `http://127.0.0.1:48730` | 访问 Gateway 完整 URL |
| `TINADEC_CORE_URL` | Gateway | `http://127.0.0.1:48731` | Gateway→Core 代理地址 |
| `TINADEC_TOOLS_BIN` | **新增** — Gateway | 无默认 | TinadecTools 可执行文件路径（gateway spawn Tool layer 实例用） |
| `TINADEC_TOOLS_RG_PATH` | Tool layer | 无 | ripgrep 路径（Tool layer 侧，gateway 不直接用） |
| ~~`TINADEC_CODE_NATIVE_BIN`~~ | **删除** | — | Rust binary 路径，不再需要 |

---

## 六、风险与注意点

1. **Tool layer 不感知 workspace**：所有路径基于进程 CWD。gateway 接驳时必须按 workspace 启动独立实例并设 `cwd`，否则多 workspace 路径串。实例需常驻复用（FileSlot/MCP 连接池有状态）。
2. **`apply_patch` 语义不等价**：Tool layer 是字节/行原语 + hash 锚，无 unified-diff 解析。接驳后该工具暂降级 unavailable，需后续补 patch 解析适配层。
3. **`sandbox_exec` 降级**：Tool layer `command_run` 非沙箱。接驳后 sandbox 语义丢失，需在 gateway 侧文档/响应中标注，或在 Tool layer 后续补沙箱。
4. **`list_directory`/`review_format` 无对应**：暂 unavailable，不阻塞 gateway 但 UI 需处理不可用态。
5. **ripgrep bundle 路径失效**：删 `native/` 后 TinadecTools.csproj 的 `..\native\rg\rg.exe` 条件拷贝失效（不报错，跳过），退化为运行时自动下载。Tool layer 团队需决定 rg 二进制归属。
6. **Core 侧 source 名 `codex-rust` 保留**：本次不触发 Core/Desktop/Tests 的 source 名连锁改动，待 Tool layer 接驳稳定后单独立项统一。
7. **gateway MCP 与 Tool layer MCP 暂并存**：本次不迁移 gateway `src/mcp/`，后续统一为 mcp-passthrough。
8. **git/code_editor/terminal/templates 随 gateway 保留**：这些 TS 工具不依赖 Rust，删除 Rust 不影响；但它们长期应归 Tool layer，本次不迁移以控制范围。

---

## 七、网关层临时补丁：list_directory 直调 ls（MVP 兜底，2 周替换）

### 背景
Tool layer（cjt 的 C# TinadecTools）目前**主要缺文件系统访问（list_directory）和文件 Review 工具（review_format）**。为保 MVP 半个月内跑通，在 Gateway 层打临时补丁：删 Rust 后 `list_directory` 的 `tryExecuteNativeTool` 链断裂，临时用系统 `ls` 命令兜底。

### 数据流向
上层（TS/C#）请求 → Gateway `executeCodeTool('list_directory')` → 执行 `ls` → 结果返回上层

### 安全加固（保命，必须做）
1. **命令白名单**：只允许 `ls`，禁止任何 shell 元字符。校验目标路径字符串，命中以下任一即拒绝并返回 failed：
   - `;` `&` `|` `$` `` ` `` `(` `)` `>` `<` `\n` `\r` 空格拼接的额外命令
   - 路径展开符 `*` `?` `[` `]`（由 gateway 自己调 Node `fs` 列目录，避免 shell 展开）
2. **路径限制**：强制限定在 workspace 根下。请求的 `path` 解析为绝对路径后必须以 `request.cwd`（workspace 根）为前缀，禁止 `..` 逃逸；禁止访问 `/etc`、`/usr`、`/`、`~`、项目根之外路径。
3. **不拼 shell**：用 Node `child_process.spawn('ls', ['-A', resolvedPath], { shell: false })`，参数走数组不拼字符串，从根上杜绝注入。
4. **输出截断**：`ls` 输出超限（如 >65536 字符或 >2000 项）截断并标 `truncated: true`。

### 补丁落点
[codeTools.ts](file:///workspace/apps/gateway/src/codeTools.ts) `executeCodeTool` 函数（[L441](file:///workspace/apps/gateway/src/codeTools.ts#L441)），在 native 分支之前为 `list_directory` 加临时分支：

```ts
// FIXME: MVP TEMPORARY HACK - REPLACE WITH SANDBOX LISTDIR AFTER 2 WEEKS
// Tool layer (cjt) 缺文件系统访问；Rust 删后 native 链断。临时直调 ls 兜底。
// 安全风险：暴露终端执行面。已加白名单+路径限制，但仍是技术债。
// 替换时机：cjt 的 C# 沙箱 listdir 就绪后删除本分支，改走 toolLayerBridge。
if (spec.id === 'list_directory') {
  return executeListDirectoryViaLs(spec, request);
}
```

新增 `executeListDirectoryViaLs()` 函数（codeTools.ts 内，风格对齐现有 `resultFor`/`failedResult`/`stringArg`）：
- 解析 `args.path`（默认 `.`）+ `args.show_hidden`（默认 false）
- 路径校验：相对 `request.cwd` 解析 → 拒 `..` 逃逸 → 拒 workspace 外 → 拒元字符
- `spawn('ls', flags, { shell: false, cwd: request.cwd })`，flags 仅允许 `-A`/无 flag
- 解析输出为 entries 数组（目录在前、文件在后），返回 `resultFor(spec, 'completed', ...)`
- 任何校验失败或执行异常返回 `failedResult(spec, ...)`，不抛出

### review_format 处理
`review_format` 是 Tool layer 第二个主要缺口。本次**不做网关兜底**（review 是格式化逻辑，不是文件系统访问，无安全风险但实现成本高于补丁范围），保留 stub 状态返回 `'stubbed'`，等 Tool layer 补 review_format 工具后接驳。

### 技术债跟踪
- 代码内 `// FIXME: MVP TEMPORARY HACK` 注释（上示）
- 本文档本节记录安全隐患与替换时机
- 替换条件：cjt 的 C# 沙箱 listdir 就绪 → 删 `executeListDirectoryViaLs` → `list_directory` 改走 `toolLayerBridge.callTool(workspace, 'list_directory', ...)`
- 半个月复盘时强制核对：本补丁是否已删，未删则升级为阻塞项

### 与清单其他阶段的关系
- 阶段 1 删 Rust 时，`list_directory` 的 nativeBacked 分支会断；本补丁**在删 Rust 同时落地**，避免 MVP 期 `list_directory` 直接 404
- 阶段 2 接驳 Tool layer 时，`list_directory` 分支优先改为 toolLayerBridge（若 cjt 已补）；若未补，保留本补丁
- 阶段 8 验证时确认 `list_directory` 经 ls 补丁可工作，且 `..` 逃逸/元字符注入被拒

---

## 八、不在本次范围

- Tool layer 技术栈/API/端口/工具实现/进程编排
- Tool layer 向 Core 注册工具的契约
- Core→Tool layer 直连（不经 gateway）的重构
- `codex-rust` source 名统一改名（待 Tool layer 接驳稳定后单独立项）
- gateway MCP 迁往 Tool layer
- git/code_editor/terminal/templates 从 gateway 迁往 Tool layer
- review_format 网关兜底实现（等 Tool layer 补）
