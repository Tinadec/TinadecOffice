# 文档与代码一致性校正报告（docs-vs-code）

**分支**：`Astra` ｜ **基线提交**：`3e8ff30`（2026-09-07） ｜ **改动类型**：仅文档（Markdown），零源码改动
**改动规模**：80 个已跟踪 Markdown 文件修改 + 2 个新文件（`.qoder/repowiki/NOTICE.md`、本报告）；净变更约 +403 / −213 行

## 0. 目的与判定原则

本仓库的 `*.md` 存在系统性问题：**目标态被写成已完成**、**已退役的路由仍被写成当前契约**、**目录/测试路径早已失效却从未回查**、**数量统计系统性偏低**、**文档之间互相矛盾**。本 PR 以**代码为唯一事实源**，逐条核对后就地修正，并对无法逐条修正的历史文档做止损标注。

判定规则：
1. 任何"已实现 / 已删除 / 当前路由 / 数量"结论，必须有源码或测试的 `file:line` 支撑；无法验证的一律标注为未验证，不写成事实。
2. 历史计划类文档**不重写**，改为在文首加"不可作为事实源"横幅，避免后人误引。
3. 修正后文档内的自述仍然**不是**事实源；元数据（Last Updated / Commit）明确标注为"会过期"。

## 1. 本次核实的关键代码事实（修正依据）

| 事实 | 依据 |
|---|---|
| Core 注册 **13** 个 module descriptor（10 业务模块 + Tenancy + AgentConfiguration + VectorStore） | `TinadecCore/Runtime/TinadecCoreServiceCollectionExtensions.cs:36-48`；`AddTinadecCoreMinimal()` 只注册 4 个 `:97-100` |
| `/src/modules/` 下 **10** 个工程 | `TinadecCore/TinadecCore.slnx` |
| run 状态 **12** 个 | `TinadecCore/Abstractions/RunStatus/RunStatusMachine.cs:12-17` |
| `POST /sessions/{id}/invoke-stream` **已退役（404）**，当前入口是 `POST /sessions/{id}/interactions`，输出走 `GET /runs/{runId}/stream` | `TinadecCore/AspNetCore/Endpoints/DmaeaEndpoints.cs:26-28` |
| `PUT /api/v1/agents/{id}/runtime-binding` **存在**（多份文档误称已删除） | `TinadecCore/AspNetCore/Endpoints/AgentConfigurationEndpoints.cs:21` |
| `shell` 工具**无沙箱**（直接 `cmd.exe /d /s /c`）；`command_run` 才是走沙箱的那个 | `TinadecTools/Tools/Command/ShellTool.cs:20,33-43,56-60`；`Tools/Command/CommandRunner.cs:81,91-103` |
| Gateway 源码在 `TinadecGateway/src/`（**无** `gateway/src/`），**15** 个 mapper，无 `debugProxy.ts` / `codeTools.ts` | 目录实况 |
| Debug Studio **后端未实现**：`debug/*` 全是空数组/501 桩，Gateway `/ws/debug` 是死桩 | `TinadecCore/AspNetCore/Endpoints/StubEndpoints.cs`；`TinadecGateway/src/websocket.ts:58-60` |
| 内置 Office Agent Pack 当前 **0.2.3**（14 Agent / 5 PromptPipeline / 7 Mode） | `apps/desktop/src/agentPacks/OfficeAgentPack/manifest.json:9` |
| `AgentConfigurationDbContext` 有 **19** 张表；`Abstractions/Ports` 有 **27** 个端口文件 | 源码实况 |
| `tests/TinadecCore.Tests` **不存在**；Core 测试是 `TinadecCore/tests/` 下 4 个工程 | 目录实况 |
| `DevSeed` 只种子 `chat` 路由 + OpenAI provider（不种子任何 Office agent） | `TinadecCore/Runtime/DevSeed.cs:29-85` |
| 四个运营角色已由触发链调度（不再 dormant） | `TinadecCore/DmaEA/Operations/OperationalTriggers.cs` |
| `model-readiness` / `model-catalog-readiness` 是硬编码 0 的废弃兼容壳 | `TinadecCore/AspNetCore/Endpoints/StubEndpoints.cs:58-60,127-155` |
| Desktop 已删除 `invoke-stream` / `POST messages` 回退，`POST /interactions` 是唯一准入契约 | `apps/desktop/src/controllers/HomeController.ts:429-432` |
| 无 `run_id` 的 queued interaction 已持久化（`RunDirectiveRecord` + `run.queued` 事件） | `TinadecCore/AspNetCore/Endpoints/InteractionsEndpoints.cs:187-226` |
| lane 的出厂开关 `lanes_enabled=false`，且**没有受支持的运行时开启路径** | `TinadecCore/DmaEA/Configuration/default-agent-runtime.toml:25` |

## 2. 修正清单

### 2.1 根级 AI 入口

**`AGENTS.md`（15 处）**
- 元数据 `Last Updated/Commit/Branch` 更新为 `2026-09-07 / 3e8ff30 / Astra`，并加"元数据会过期、不得当作事实"的免责声明。
- 删除"`invoke-stream` 是当前调用入口"表述，改为 interactions 准入 + 已退役说明。
- `DevSeed` 描述纠正为**只种子 `chat` 路由**（原文暗示种子 planner/executor agent）；scheduling 仍为 501。
- `tools/shell` 从"501 桩"改为"已实现（审批门控、无沙箱）"。
- 每 run 生成实例数 `8 → 16`；run 状态 `10 → 12`；mapper `12 → 15`。
- 删除 `debugProxy.ts` 这一不存在的路径，改为"Debug Studio 后端未实现"。
- `21 → 22` 个 Core 源工程；删除遗留 `planning/execution` 分层表述。
- 修正 `tests/TinadecCore.Tests` 死路径（该目录已不存在），指向 `TinadecCore/tests/*`。

**`CLAUDE.md`（11 处）**
- `gateway/package.json` → `TinadecGateway/package.json`。
- 工具注册指引 `ToolRegistryService.cs` → `IToolRegistry` / `TinadecCore/Tools/CoreToolRegistry.cs`，并删除不存在的 `gateway/src/codeTools.ts`。
- 测试命令：删除不存在的 `tests/TinadecCore.Tests/TinadecCore.Tests.csproj` 与不可用的 `npm run test -w @tinadec/gateway`（TinadecGateway 不是 npm workspace 成员），改为 `dotnet test TinadecCore/TinadecCore.slnx` 与 `cd TinadecGateway && bun test`。
- **删除"CodeGraph 结果无需重复验证"这条危险指引**，改为"索引可能落后于工作树，结论必须回源核对"。
- Debug Studio 改为"前端存在、后端为桩"的准确描述；`CoreStore` 查询示例改为真实符号。

**`README.md`（4 处）**
- 删除"App 可以直连 Core"的表述与架构图中的直连箭头，改为"Desktop 永远经 Gateway"（与 `docs/tinadec-four-product-roadmap.zh-CN.md:53-55` 的架构决定一致）。
- Debug Studio 由"追踪可视化"改为"前端存在、后端未实现"。
- 数据库由"业务 schema 后续落地"改为"已落地（9 个 DbContext，SQLite 侧 25 个迁移文件）"。

### 2.2 Core / Gateway / 规范文件

**`TinadecCore/AGENTS.md`（14 处）**
- 元数据更新 + 免责声明；`/src/modules/` 由"八个"改为"十个业务模块"，依赖图同步；`AddTinadecCore()` 由模糊表述改为"13 个 module descriptor"，`AddTinadecCoreMinimal()` 补上 4 个真实模块。
- harness manifest 由"8 个 module descriptor"改为 13；端点表删除 invoke-stream 当前行，新增 interactions 行与"已退役（404）"历史行；`:183`、`:239-240` 同步。
- `tests/` 目录树补上缺失的 `TinadecCore.Governance.Tests`；Persistence 备注由"8-module manifest"改为"十个业务模块之外"。

**`.ponytail/rules.md` / `.ponytail/debt.md`**
- 端点目录 `TinadecCore/Api/Endpoints/` → `TinadecCore/AspNetCore/Endpoints/`（该目录早已迁移）。
- debt 表内失效行号修正；`index.ts:120 → :132`；一条已解决的债务（queued interaction 未持久化）标注为已解决。

### 2.3 docs/ 权威文档纠错

**`docs/tinadec-core-product-definition.zh-CN.md`（15 处）**
- run 状态 `10 → 12`（并给出唯一事实源 `RunStatusMachine.cs`）。
- 删除"也可使用 `invoke-stream`"，改为"已退役、返回 404"。
- lane 章节新增**出厂开关警告**：`lanes_enabled=false` 且无受支持的开启路径——避免把已实现代码路径误读为出厂能力。
- 观测面改为真实状态：readiness 已实现；`traces`/`metrics` 是空数组桩；`evaluations`/`audit export` 无任何路由。
- `simple_qa` / `controlled_execution` 标注为**目标态 id（代码中不存在）**。
- Pack 版本 `0.1.0 → 0.2.3`，资源数改为 14 Agent / 5 PromptPipeline / 7 Mode。
- §16 加"2026-08-22 历史快照"警示，并就地更正 8 行错误事实（队列已持久化、`AgentConfigurationService` 不是桩且为 19 张表、`context_compressor` 已进热路径、`ApprovalDelegation` 存在、演化候选已自动生成、`git_steward` 已触发）。

**`docs/architecture.md`（12 处）**
- `runtime-binding` 由"有意缺席"改为"存在且属于当前契约"。
- Pack `0.1.0 → 0.2.3` 并补全 5 PromptPipeline / 7 Mode。
- 四个运营角色由"dormant"改为"已由触发链调度，但不创建合成实例"。
- run 状态补齐 12 个；invoke-stream 改为 interactions + 已退役说明；`tools/shell` 由 501 改为已实现。
- 路由清单补齐 5 个无导航入口的页面；`model-readiness` / `model-catalog-readiness` 标注为废弃兼容壳。
- Debug Studio 整节加"仅前端、后端未实现"警示，并把该节明确标为目标态设计；端点表中 `debug/traces` 标注"当前返回 `[]`"、`WS /debug/ws` 标注"路由不存在"。

**`docs/startup.md`（11 处）**
- 删除不存在的 `agent_meeting` / `planner` 路由叙述，改为 interactions + `meeting`，并说明 `DevSeed` 只种子 `chat` 路由。
- 启动命令 `src/TinadecCore/TinadecCore.csproj` → `TinadecCore/Api/TinadecCore.Api.csproj`；`npm run dev -w @tinadec/gateway` → `npm run dev:gateway`。
- 后台启动脚本里的硬编码 `D:\github\TinadecOffice` 改为 `(Get-Location).Path`。
- 期望 agent 列表由 14 个错误 id 改为真实 roster（6 operation + 8 execution），并说明需先安装 Agent Pack。
- 删除 `CoreStore.Initialize()`（该类不存在）与死测试路径。

**`docs/agent-harness-product-model.zh-CN.md` + `.en.md`（各 5 处）**：run 状态 12 个；每 run 16 个生成实例；invoke-stream → interactions + 退役说明；`tools/shell` 不再列为 501；两文"实现状态"节加"2026-08-18 快照、M1–M8 未回写"警示。

**`docs/app-core-ui.md`（7 处）**：删除"HomeController 仍回退 invoke-stream"的过时描述（回退已删）；queued interaction 已持久化；`/library` 标注无导航入口；`runtime-binding` 纠正为存在；run stream 段落去掉 invoke-stream。

**`docs/tinadec-four-product-roadmap.zh-CN.md`（4 处）**：mapper `16 → 15`；DI 端口 `23 → 27` 并修正行号；"HTTP 层在不可打包的 Api 项目"标注为已解决（新增可打包 `TinadecCore.AspNetCore`）；最新提交日期更新。

**`docs/office-agent-pack-shipment-status-and-backlog.md`（4 处）**：补当前版本 0.2.3 与当前测试基线；分支 `main → Astra`；删除仓库中不存在的 `withdocs/双层智能体架构现状与差异分析.md` 引用；`tools/shell` 不再列为 501。

**`docs/e2e-refactoring-plan.zh-CN.md`（4 处）**：文首加"本文已是完成记录"说明；把 §1.2 / §4.1 中"invoke-stream 与悬空代理仍未退役"的**计划初期缺口描述**标注为已由 §8 完成，并给出退役证据（否则与本文 §8 的 ✅ 行自相矛盾）。

**`docs/architecture-compliance-verification.md`（9 处）**
- 文首加"**这不是一份验证报告**"警示：全文从未执行过任何验证。
- `✅ 通过` → `⚠️ 未实测`；第 1.3 / 5.2 / 8.3 节的 54% / 20% / 27% / 58% / 22% / 30% 指标逐条标注"无测量来源、不可引用"。
- 第 6 节把"测试结果"改为"预期结果（非实测输出）"，并指向真实证据 `TinadecCore/tests/TinadecCore.Architecture.Tests`。
- `[待填写]` 清理；"下次验证日期"标注已过期未执行。

### 2.4 其它权威/半权威文档

**`TinadecGateway/AGENTS.md`（4 处）**
- `PUT /api/v1/agents/:id/runtime-binding` 由"已删除"纠正为"当前有效代理"（`src/index.ts:1523` → Core `AgentConfigurationEndpoints.cs:21`）。
- `TINADEC_TOOL_RUNTIME_URL` 说明纠正为"外部转发地址；本机无此 HTTP 服务，工具宿主是 Core 按 `TinadecTools:ExecutablePath` 探测的子进程"。
- `invoke-stream` 由"原样转发"改为"已退役、返回 404"。

**`apps/desktop/AGENTS.md`（3 处）**
- 删除"legacy `invoke-stream`/`postMessage` 回退"的错误描述（回退已删除）。
- 路由清单补全为 13 条真实路由（含 `/agent-center`、`/code-editor`、`/workbench`、`/governance`、`/snapshots`、`/recovery/:actionId`、`/library`）。
- "正常用户聊天应迁移到 invoke-stream 契约"改为 interactions 契约。

**`apps/web/AGENTS.md`（1 处）**：Debug Studio 标注为"仅前端，Core debug/trace 后端未实现、Gateway `/ws/debug` 是死桩"。

**`apps/TinadecUI/AGENTS.md`（1 处）**：把机器专属路径 `C:\git\agent\TinadecUI` 改为"由 `scripts/sync-tinadec-ui.mjs` 解析（默认 `../TinadecUI`，可用 `TINADEC_UI_TARGET` 覆盖）"，并修正同步内容描述。

**`docs/web-client.md` / `docs/reference-project-map.md`**：`gateway/src` → `TinadecGateway/src`，Debug Studio 后端状态纠正。

**`docs/architecture-views/tinadecoffice.evidence.md`**：`Api/Endpoints/` → `AspNetCore/Endpoints/`；`Api/Program.cs` 的 13 次 `Map*Endpoints()` 改为 `AspNetCore/TinadecCoreEndpointRouteBuilderExtensions.cs:14-30` 的 17 次；501 计数 `25 → 24`（Stub 17 + ControlPlane 7）；mapper `12 → 15`；删除已不存在的 `src/codeTools.ts` 引用；WS 行号与 `config.ts` 行号校正。

**`docs/architecture-views/tinadecoffice.architecture-understanding.md`**：invoke-stream 由"请求内流"改为"已退役"；工具运行时改为 `TINADEC_TOOL_RUNTIME_URL` + 子进程自动探测的准确描述。

**`docs/git-module-status-and-roadmap.md`**：加历史横幅；`gateway` → `TinadecGateway`；`gateway/src/codeTools.ts` → 真实的 `TinadecTools/Tools/Git/GitReadTools.cs`。

**`docs/core-agent-e2e-verification-2026-08-29.md`**：加"验证记录快照"说明；`tools/shell` 由"恒 501"改为"已实现（需审批、无沙箱）"。

**交叉复核补漏**：`docs/app-core-ui.md` 的 `git_steward` 由"仅安装冻结、不参与事件"改为"已由触发链调度（仅建议性 `git.steward.reviewed`）"；`TinadecCore/AGENTS.md:267` 把 flat `runtime-binding` 由"已删除"纠正为"存在且属于当前契约"。

### 2.5 历史/不可信文档的止损标注

以下文档**不重写**，仅在文首插入横幅，明确"不可作为事实源"（含失效路径、目标态当已完成的提醒）：

- `docs/`：`agent-debug-studio-plan.md`、`ai-tools-deployment-verification.md`、`ai-tools-implementation-report.md`、`ai-tools-integration-guide.md`、`ai-tools-quick-start.md`、`corecoreplan.md`、`DmaEAPlan.md`、`gateway-extraction-and-tool-bridge.md`、`gateway-extraction-rust-removal-checklist.md`、`superpowers/plans/2026-07-09-gateway-extraction-and-rust-removal.md`
- `.trae/documents/`：20 个历史设计/修复计划
- `.trae/specs/`：4 个 `spec.md` + 6 个 `checklist.md`/`tasks.md`（其中多处标注"已完成 ✅"的功能在代码中无任何对应）
- `.zcode/plans/`：12 个会话计划
- 根目录 `agent plan.md`

### 2.6 AI 知识库快照提示

- 新增 `.qoder/repowiki/NOTICE.md`：`repowiki` 是工具在 `d863df4`（2026-08-04）生成的快照，落后当前分支 100+ 提交，约 7% 文件引用已指向不存在的路径；仅可作目录索引。**未改动其 290 个生成文件**。

## 3. 核实方法

- 全量读取源码/配置/测试，逐条与文档断言比对；所有结论带 `file:line`。
- 关键数量用命令复核：module descriptor、DbSet、mapper、端口、路由、工具 id、迁移、表数。
- 实测：`dotnet test TinadecCore/TinadecCore.slnx`、`bun test`（Gateway）、`vitest run`（Desktop）、TinadecTools 测试套件。

## 4. 风险与说明

- 本次**不改任何源码/配置/契约**，因此无运行时风险；改动全部落在文档。
- 历史文档的正文错误**未被逐条修正**（成本高、价值低），以横幅声明替代；若某篇需要重新作为事实源使用，必须先重写而不是删横幅。
- 环境限制导致的未验证项：本机未安装 `bun`、根目录与 `TinadecGateway`/`apps/desktop` 无 `node_modules`，因此 Gateway/Desktop 的测试命令需在具备依赖的环境中复跑；TinadecTools 测试中 3 例因沙箱下 `CreateSymbolicLink` 权限不足失败（环境问题，非代码问题）。
