# AI Agent 开发框架选型调研报告

> 日期：2026-09-10（发报告当天口径）
> 数据快照时间：2026-09-10 ~15:00 UTC（GitHub REST API 实测）
> 适用对象：Tinadec 产品族（TinadecCore / TinadecTool / TinadecGateway / TinadecApp）
> 方法：本地检出只读核验（`C:\git\agent` 下 14+ 仓库）+ GitHub API 精确拉取 + 官方文档（Learn/Docs）联网复核 + Tinadec 工作树源码（AGENTS.md / 产品定义 / 路线图 / OpenAPI 快照）对照
> 权威基线：`docs/tinadec-core-product-definition.zh-CN.md`、`docs/tinadec-core-reference-decisions.zh-CN.md`、`docs/tinadec-four-product-roadmap.zh-CN.md`、`TinadecCore/AGENTS.md`、`TinadecOffice/AGENTS.md`

---

## 0. 结论先行（给决策人的一页）

1. **继续以 Microsoft Agent Framework（MAF）为执行底座，不要切换框架。** MAF 在 2026-04 已 GA 1.0、2026-09-10 已演进到 Python `1.18.0` / .NET 高频迭代，是微软对 AutoGen + Semantic Kernel 的官方继任者；TinadecCore 锁定 `MAF 1.18.0` 的选择与上游方向一致。
2. **Tinadec 的差异化不应在“换编排内核”，而在“保留治理权威”。** MAF/LangGraph/OpenAI Agents SDK 只解决单次执行图、checkpoint、HITL 恢复；Tinadec 的 DmaEA 双层（`operation` 治理 + `execution` 执行）、冻结配置、PDP/租约/一次性审批、版本化 Agent/Mode/Prompt、Agent Pack、三类快照是主流框架默认不给的，需要自建并保留。
3. **Tinadec 当前最大缺口不是编排，而是“产品化四件套”：** 可观测/评测闭环、记忆与 embedding 生产链路、 scheduling 与多租户/身份/容器发布、SDK/市场生态。前三者决定能不能生产可用，最后一个决定能不能长出社区。
4. **建议路线：** Phase 0/1 先补契约即代码（OpenAPI codegen + drift 门禁）与 NuGet/容器发布；Phase 2 补 eval/canary、长期记忆、scheduling；Phase 3 再做市场与多租户。替换动作一律经 DmaEA 内部适配器 + 兼容测试门禁（产品定义 PD-12）。

---

## 1. Tinadec 基线冻结（2026-09-10 工作树）

### 1.1 版本与分支

| 组件 | 本次核验 |
|---|---|
| TinadecOffice | HEAD `baa9f22`（含自由对话/审批建区/migrate 复用 binder；上一个基线 `6e29e86` Astra） |
| TinadecCore（嵌套） | HEAD `e5b9b6d`（Api/data 忽略）/ 同步点 `1fdf555 sync from TinadecOffice@554822d`，另有三态生命周期 `60e2c67` |
| MAF 基线 | `1.18.0`（`Directory.Packages.props` 集中锁定：`Microsoft.Agents.AI[.Abstractions/.Workflows/.OpenAI]` 均为 1.18.0；ASP.NET Core OpenApi/Mvc.Testing 10.0.11， transitive `Microsoft.OpenApi 2.7.5`） |
| .NET | 10（`net10.0`，nullable + implicit usings） |
| 本地 MAF 检出 | `agent-framework@main@2c49f50cf`（2026-09-04），落后远端 HEAD `c457aca`（2026-09-10）约 6 天 |

### 1.2 已实现（有测试/可运行证据）

* 全双工 run 引擎：`POST /sessions/{id}/interactions` 准入（幂等 client-message-id、`context_revision`、排队/插入/并行）→ 治理协调 → 任务规划 → 执行 → 监督 → meeting 收尾；`GET /runs/{id}/stream` SSE replay/follow；run control（cancel/pause/resume）；租约 checkpoint 重启恢复；旧 `invoke-stream` 已退役 404。
* 版本化配置：Agent/Mode/Prompt draft + 不可变版本 + ETag/If-Match；ModeVersion 为 session/run 事实源；逐 Agent 模型策略收敛为 `inherit|route|fixed`；`model_routes` 有序候选 + `model_invocations` 归因日志；`model-resolution/preview`、`model-references`、`model-invocations` 已实现。
* 工具链 E2E：`TinadecToolsProcessManager`（每 workspace 一子进程、BOM-free UTF-8 行协议、v2 manifest）→ `ToolManifestSnapshotResolver` 冻结 → `ToolInvocationScopeResolver` 验 hash → `ToolDispatcher`（PDP/租约/一次性 ActionApproval）→ 批准唤醒 `engine.EnqueueAsync`；`tool-layer-readiness` 为真实 manifest 投影；`shell` 治理工具 + `#terminal` 控制工具 + `terminals/{id}/stdin|kill`。
* 权限收口（2026-08-31）：operation 层零工具（`operation_layer_cannot_invoke_tools`）+ `FreezeToolManifest` 仅并 execution agents + `SpawnAsync` 拒通配 `*` + 实例授权 ∩ 冻结清单（`IFrozenToolManifestCatalog`）。
* 无人值守（M5/M8）：`FrozenPermissionMode`（full-access/auto-approve/ask）+ lane 准入解冻 + `IToolApprovalAutoPolicy` + `park_expired` 升级 + per-lane planner；PDP 下游释放（`full_access_auto_grant`/`auto_policy_released`），auto 默认关、不碰 human-only、高风险不自动放行。
* 快照与治理：Workspace 快照（filesystem/Git，`session_metadata_snapshots`）、UserToolAction（manifest/参数/快照绑定、CAS、重启恢复）、Agent Pack（workspace-scoped preview/install/version/default-adoption/managed-read-only，RFC8785 digest，Office pack 0.2.4）。
* 传输：Core 48731（`snake_case` + RFC9457 ProblemDetails + `code`+`trace_id` + 内部 `/openapi/core.json`）；Gateway 48730（15 mapper、外部 `/docs/json` 快照、`X-Request-Id`、SSE cursor `Last-Event-ID→after_seq`）；Desktop 经 Gateway（永久唯一边界），`generate:client`（openapi-typescript@6 离线生成 `schema.d.ts`）+ `check:drift` 已部分启用。

### 1.3 明确缺口（源码自认，非推测）

* `scheduling` 写路径 `501`；`debug/*` 空数组/`501`、`WS /ws/debug` 死桩（Debug Studio 无真实数据）；`Gateway /ws/terminal` 桩。
* 无已配置 embedding 路由（`IEmbeddingProvider` 不可用即显式失败）；向量库仅基础能力（SQLite `sqlite-vec` per project / PG `pgvector`）。
* 演化：候选生成/晋升已通，但评测（evaluator/rubric/canary/灰度）缺；记忆：candidate-only review，无注入闭环与衰减治理。
* 上下文压缩事件驱动、restore-plan UX 未完成；workspace override/readiness 诊断未暴露。
* 交付：Contracts/Abstractions/Runtime/AspNetCore 可打包但未发布任何 NuGet feed；无容器镜像、无 OIDC、无多租户调度；无生成式 TS/Python/.NET Client SDK（`api.ts` 2200 行手写镜像渐进替换中）；市场/签名/卸载回滚缺。

---

## 2. 框架覆盖与“发报告当天”精确数据

### 2.1 口径说明

* `stars/forks/issues/pushed/default_branch/license/updated` 取自 `GET https://api.github.com/repos/{owner}/{repo}`（本次 2026-09-10 ~15:00 UTC 实测，非缓存）。
* `HEAD sha/date/msg` 取自 `GET /repos/{repo}/commits?per_page=1`；`release` 取自 `GET /repos/{repo}/releases/latest`。
* `52w_total/last4w` 取自 `GET /repos/{repo}/stats/participation`（202  computing 时记为不可用，大仓常见）。
* 时间为 API 返回的 UTC 时间；PowerShell 侧显示格式为 `yyyy/M/d HH:mm:ss`，日期均为 2026-09-10 前后。
* 两个易错点已核验：`sst/opencode` 已迁移/重定向到 `anomalyco/opencode`（同 id `975734319`，默认分支 `dev`）；本地 `t3code` 远端为 `pingdotgg/t3code`（T3 Chat 应用，非通用 agent 框架，引用时需与 Tinadec 旧文档中的 harness `t3code` 区分）。

### 2.2 主表（14 主框架，精确到当天）

| # | 框架 | 仓库 | stars | forks | open issues | pushed_at (UTC) | HEAD（当天） | latest release（当天） | license |
|---|---|---|---|---|---|---|---|---|---|
| 1 | Microsoft Agent Framework | `microsoft/agent-framework` | 13,452 | 2,293 | 602 | 2026-09-10 14:58 | `c457aca` 2026-09-10 `Python: reset $LASTEXITCODE…(#8206)` | `python-1.18.0` 2026-09-10 | MIT |
| 2 | Semantic Kernel | `microsoft/semantic-kernel` | 28,552 | 4,765 | 269 | 2026-09-09 06:03 | `3551171` 2026-09-09 `Python: Add User-Agent…(#13703)` | `dotnet-1.80.1` 2026-09-03 | MIT |
| 3 | AutoGen | `microsoft/autogen` | 60,918 | 9,205 | 1,065 | 2026-04-15 | `027ecf0` 2026-04-06 `Update maintenance mode banner(#7521)` | `python-v0.7.5` 2025-09-30 | docs CC-BY-4.0 / code MIT（双证） |
| 4 | LangChain | `langchain-ai/langchain` | 146,072 | 24,398 | 478 | 2026-09-10 13:27 | `443154d` 2026-09-10 `feat(huggingface)…` | `langchain-core==1.6.2` 2026-09-04 | MIT |
| 5 | LangGraph | `langchain-ai/langgraph` | 41,391 | 6,991 | 774 | 2026-09-09 07:22 | `e539ac1` 2026-09-09 `chore(deps)…(#8449)` | `sdk==0.4.4` 2026-08-27 | MIT |
| 6 | LlamaIndex | `run-llama/llama_index` | 52,118 | 8,107 | 749 | 2026-09-10 04:02 | `5be4479` 2026-09-08 `docs: remove dead badge…(#23005)` | `v0.14.24` 2026-08-19 | MIT |
| 7 | CrewAI | `crewAIInc/crewAI` | 58,333 | 8,399 | 749 | 2026-09-10 14:39 | `5704ea0` 2026-09-10 `fix(agents): keep null…(#6775)` | `1.15.21` 2026-09-09 | MIT |
| 8 | Dify | `langgenius/dify` | 155,328 | 24,528 | 1,061 | 2026-09-10 12:19 | `f0d69e7` 2026-09-10 `fix(web)…(#42145)` | `1.17.1` 2026-09-10 | Dify Open Source License（NOASSERTION，非 OSI） |
| 9 | n8n | `n8n-io/n8n` | 203,924 | 60,631 | 1,146 | 2026-09-10 14:59 | `85cfc26` 2026-09-10 `docs(API)…` | `n8n@2.38.6` 2026-09-10 | Sustainable Use License（fair-code，非 OSI） |
| 10 | Coze Studio | `coze-dev/coze-studio` | 21,572 | 3,115 | 568 | 2026-07-29 | `fefb05f` 2026-07-29 `fix(infra)…(#2723)` | `v0.5.1` 2026-02-05 | Apache-2.0 |
| 11 | OpenAI Agents SDK | `openai/openai-agents-python` | 29,326 | 4,691 | 44 | 2026-09-09 12:35 | `83c737f` 2026-09-09 `release: 0.22.2(#4935)` | `v0.22.2` 2026-09-09 | MIT |
| 12 | Codex CLI | `openai/codex` | 123,059 | 18,940 | 16,464 | 2026-09-10 14:22 | `9469737` 2026-09-10 `Add MIME-filtered resource…(#44548)` | `rust-v0.154.0` 2026-09-09 | Apache-2.0 |
| 13 | Gemini CLI | `google-gemini/gemini-cli` | 106,895 | 14,545 | 820 | 2026-09-10 11:42 | `ed2ac40` 2026-09-08 `fix(core): preserve Flash IDs…(#29252)`（本地检出 `85aca16` 同分支更新） | `v0.59.0` 2026-09-08 | Apache-2.0 |
| 14 | OpenCode | `anomalyco/opencode`（原 `sst/opencode` 已迁移） | 206,346 | 26,974 | 5,746 | 2026-09-10 13:47 | `b3f1a96` 2026-09-10 `fix(console)…(#48309)` | `v1.18.30` 2026-09-09 | MIT |

佐证/对照仓（Tinadec 参考决策用，同样当天拉取）：`traycerai/traycer` 1,450/193/197；`GaosCode/PlanWeave` 379/26/0；`xai-org/grok-build` 26,648/5,013/0；`QoderAI/better-harness` 2,244/180/8；`NousResearch/hermes-agent` 244,116/50,461/41,537（API 原值，量级异常高，报告中仅作原值引用，不做排名依据）；`pingdotgg/t3code` 22,309/5,532/1,619（实为 T3 Chat，见 2.1）。

活跃度（participation，52 周）：`agent-framework` 52w=2,728（近4周 55,54,45,121，明显活跃）；`dify` 52w=6,074（近4周 194,193,150,213，极活跃）；`semantic-kernel` 52w=329（近4周 3,4,8,8，低）；`langgraph` 52w=811（近4周 3,3,20,7）；`codex/gemini-cli/opencode/autogen` 当次返回 computing（202），记为不可用，不强行填数。

本地检出对照（证明“不是拿旧快照冒充当天”）：`agent-framework 2c49f50cf(09-04)` vs 远端 `c457aca(09-10)`；`langchain eef3eaeac8` vs `443154d`；`langgraph 81bf17b23` vs `e539ac1`；`dify dde1d500b5` vs `f0d69e7`；`codex 6af34540` vs `9469737`；`gemini-cli 85aca16` vs `ed2ac40`；`opencode 337fd14(dev fork)` vs `b3f1a96`。结论：本地检出普遍落后远端 0–6 天，能力判断以远端文档+当天 release 为准，本地仅作结构证据。

---

## 3. 能力边界矩阵（能否做）

> `●` 原生支持 / `◐` 需二次开发或企业版 / `○` 缺失或明确不做。Tinadec 列为本次工作树实测。

| 维度 | MAF 1.18 | SK | AutoGen | LangChain/LangGraph | LlamaIndex | CrewAI | Dify | n8n | Coze Studio | Agents SDK | Codex CLI | Gemini CLI | OpenCode | **Tinadec** |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 单智能体 loop | ● | ● | ● | ●/● | ● | ● | ● | ●(AI节点) | ● | ● | ● | ●(ReAct) | ●(build/plan) | ●（meeting/planner/worker） |
| 多智能体编排 | ●（Sequential/Concurrent/Handoff/GroupChat/Magentic，可 `as_agent` 嵌套） | ●（多 Agent 协作） | ●（AgentChat，维护模式） | ●（Graph/DeepAgents/Subgraph） | ◐（Workflows 事件驱动，可组多 Agent） | ●（Crews 自治 + Flows 事件控制） | ●（Agent 节点 + Workflow 画布） | ●（可视子工作流作工具） | ●（工作流/多 Agent 资源） | ●（Agents as tools/Handoffs） | ●（spawn/send/wait/list/interrupt 子代理） | ●（subagents/remote subagents） | ●（primary/subagent/task） | ●（DmaEA 双层 + spawn/lineage/budget） |
| 持久化/检查点/恢复 | ●（InMemory/File/Cosmos，superstep checkpoint + resume/rehydrate） | ◐（Thread/Memory，实验性） | ◐ | ●（checkpointer：Memory/Sqlite/Postgres；durability exit/async/sync；time-travel） | ◐（Context/State，偏 RAG 会话） | ●（Memory + checkpoint/async + Enterprise 重放） | ●（Home Snapshot/Workspace/Lease；run 分离） | ●（Execution history/版本回滚） | ●（发布版本/对话管理） | ●（Sessions/SQLite/SQLAlchemy/Encrypted + Temporal 长任务） | ●（JSONL rollout/不可变历史/thread 稳定 ID/fork） | ●（Checkpointing：shadow git + 对话快照 + `/restore`） | ●（session/revert/snapshot） | ●（run/task/event JSONL + 租约 checkpoint + SSE replay/follow；三类快照契约） |
| HITL/审批 | ●（ApprovalRequired/ Binding/ToolApprovalAgent + standing rules） | ◐ | ◐（Studio 有，维护中） | ●（interrupt/resume，`Command(resume)`） | ◐（HITL 节点示例） | ●（approval gates + RBAC + audit） | ●（HITL 结束 Agent run、暂停外层 Workflow） | ●（人工审批节点） | ●（发布审核/人工节点） | ●（tool approvals + guardrails + HITL 可恢复） | ●（UnlessTrusted/OnRequest/Granular/Never + 三态 Skip/NeedsApproval/Forbidden + sandbox×审批矩阵） | ●（Policy Engine：allow/ask/deny + priority + modes default/autoEdit/plan/yolo） | ●（allow/ask/deny + pattern + auto 模式） | ●（持久 Approval + PDP/租约/nonce + auto-policy/full-access/pre-auth；默认 ask，human-only 不自动放行） |
| 记忆 | ●（history/memory provider + Mem0/Cosmos 原生记忆博文 2026-09-04） | ◐（Mem0/Whiteboard，实验性） | ○ | ●（checkpointer 短期 + Store 长期跨 thread） | ●（Memory：token 限额 + Static/Fact/Vector 三 block + flush） | ●（统一 Memory：scope/c RecallFlow/多租户隔离） | ●（知识库/RAG/变量记忆） | ○（靠外接 DB/记忆节点） | ●（知识库/数据库/变量） | ●（Sessions 持久工作上下文） | ○（会话文件，无长期记忆治理） | ●（/memory + GEMINI.md 上下文层级） | ◐（compaction/title/summary Agent） | ◐（短期有；长期仅 candidate review，无 confidence/expires 治理与注入四分法） |
| RAG/知识 | ◐（靠 VectorStore 连接器） | ●（Azure AI Search/Qdrant 等 + search function 转工具） | ○ | ●（Retrieval 生态最全） | ●（最强：LlamaParse + 索引 + Workflows 编排） | ●（knowledge + RAG embedder 可配） | ●（RAG Pipeline：摄取/切分/检索/评测一体） | ○ | ●（知识库/插件/DB 一体） | ○ | ○ | ○（Google Search grounding） | ○ | ◐（VectorStore 基础：sqlite-vec/pgvector + chunk/retrieve；无生产级 RAG 管线） |
| 工具/MCP/A2A | ●（MCP hosting + A2A + Foundry Hosted 2 行部署） | ●（Plugin/OpenAPI/MCP） | ●（MCP Workbench，冻结） | ●（MCP 全系 + Agent Server 自动 checkpoint） | ●（工具 + MCP） | ●（MCP/A2A + Tool Repository） | ●（50+ 内置工具 + Marketplace Models/Tools/Data/Trigger/Agent Strategies） | ●（400–1500+ 集成节点 + LangChain AI 节点） | ●（插件/知识/DB + OpenAPI/Chat SDK） | ●（function/hosted/MCP + Sandbox/Computer） | ●（MCP + app-server + execpolicy + Guardian 预审） | ●（MCP 本地/远端 + OAuth + extensions） | ●（MCP 本地/远端 + OAuth + org 远端配置） | ◐（IToolProvider 通用契约已抽出，但仅 TinadecTools 一个实现；MCP 仅 Tools 侧 pass-through 原型） |
| 护栏/策略语言 | ●（middleware + security labels py 侧） | ◐ | ○ | ●（middleware：fallback/selection/editing/summarization/limit） | ○ | ●（guardrails + PII redaction hooks） | ◐ | ◐ | ◐ | ●（input/output/tool guardrails + tripwire + 并行/阻塞两模式） | ●（execpolicy DSL + Granular 位图 + ApprovalStore 缓存） | ●（TOML rule + priority 小数分层 + MCP 通配） | ●（V1 permission 对象 / V2 permissions 数组规则） | ●（PolicyBundle/交集求交 + 显式 Forbidden 短路；策略禁止不打扰用户） |
| 可观测/评测 | ●（OTel Agent/Workflow + DevUI + AF Labs gaia/tau2） | ◐ | ◐（Bench） | ●（LangSmith tracing/eval/prompts/deploy + Engine） | ◐ | ●（tracing + cost + eval + Arize/Galileo/Datadog） | ●（LLMOps：日志/标注/延迟/Template 市场） | ●（Insights/审计/SIEM 企业版） | ●（Coze Loop：prompt/评测/监控全生命周期） | ●（Tracing 内置默认开 + eval） | ●（rollout JSONL + review + cloud） | ●（telemetry + GitHub Action review/triage） | ◐（timeline/E2E timeline tests） | ○（OTel 非敏感结构已接；但 debug/* 桩、无 eval/canary、无成本账本） |
| 部署/运行形态 | ●（库 + Foundry Hosted + Durable/Functions 扩展） | ●（SDK 三语言） | ◐（冻结） | ●（SDK + Agent Server + 云） | ●（Py/TS + Cloud） | ●（OSS + Enterprise 云/控制台） | ●（Cloud/VPC/自托管 + publish App/API/MCP + difyctl） | ●（自托管/air-gapped/Cloud） | ●（开源引擎 + 商业版 + Chat SDK） | ●（SDK + Temporal + Voice/Realtime） | ●（CLI + IDE + Cloud + app-server） | ●（CLI + IDE + Cloud Shell + ACP） | ●（TUI/CLI/Web/serve + 无头服务 + SDK 生成客户端） | ◐（服务可独立跑；库可嵌入；但无 NuGet feed/容器/OIDC/多租户调度） |

关键定性（当天文档复核）：

* MAF 不是“SK 改名”：SK 与 AutoGen 官方 README 均已挂 `MAF is the enterprise-ready successor` + 迁移指南（SK→MAF、AutoGen→MAF）；AutoGen 明确 `maintenance mode，不收新 feature，仅修 bug/安全，社区维护`，最后实质提交停在 2026-04-06。新项目不应再选 AutoGen/SK。
* LangGraph 1.0（2025-10）+ MAF 1.0（2026-04）之后，`LangGraph vs MAF` 才是有效比较，`LangChain vs AutoGen` 已过时（LangChain 官方 2026-08-16 对比页原话）。
* Codex/Gemini 的审批不是简单 allow/ask/deny：Codex 有 sandbox×审批组合判定 + Granular 五开关 + execpolicy 前缀沉淀；Gemini 有小数优先级（Default 1.x < Extension 2.x < Workspace 3.x < User 4.x < Admin 5.x）+ modes + MCP 通配。Tinadec 已吸收这两条（位图 + Forbidden + 审批记忆化），是少数把 CLI 治理做成持久化状态机的。
* Agents SDK 的差异化是轻量 + guardrails 并行/阻塞 + tool guardrails + Sessions + Temporal 长任务 + Voice/Realtime/Sandbox agents，适合“代码即编排”，不适合要做版本化治理的平台。
* Dify/n8n/Coze 是平台型：Dify 强在 RAG+LLMOps+Marketplace+多发布形态；n8n 强在 1500+ 集成+自托管+审计，但 SUL 不是 OSI 开源；Coze 强在可视 Agent/Workflow/资源一体 + Loop 评测，但开源版更新慢（release 停在 2026-02-05，HEAD 停在 2026-07-29）。

---

## 4. 可扩展性矩阵（能不能长）

| 扩展点 | MAF | SK/AutoGen | LangChain/Graph | LlamaIndex | CrewAI | Dify/Coze/n8n | Agents SDK | CLI 系（Codex/Gemini/OpenCode） | **Tinadec** |
|---|---|---|---|---|---|---|---|---|---|
| 模型/Provider | ●（Foundry/Azure/OpenAI/GitHub Copilot SDK + 多 provider 示例；`IChatClient` 统一） | ●（OpenAI/Azure/HF/NVidia/Ollama 等） | ●（最广，100+；model fallback middleware） | ●（LLM 可插拔） | ●（多 LLM + 运行时切换评测） | ●（Dify 数十家 + 自托管；n8n 切换无需改架构；Coze 模型服务） | ●（Responses/Chat + 100+ LLM provider-agnostic） | ●（Codex 模型切换；Gemini 路由/回退；OpenCode provider 归一） | ●（多协议 openai-chat/responses/anthropic-messages + ACP/opencode CLI；per-agent inherit/route/fixed + override；缺 embedding 路由） |
| 工具/技能 | ●（Skills design 0037 + AgentSkillsProvider + middleware） | ●（Plugin/OpenAPI/MCP） | ●（tools + middleware + DeepAgents skills） | ●（FunctionAgent tools） | ●（tools + Tool Repository + skills） | ●（Dify Marketplace 六类；Coze 插件/工作流/知识；n8n 自定义节点/JS/Python） | ●（function/hosted/MCP + Agents as tools） | ●（MCP OAuth/远端组织配置；Codex execpolicy；Gemini extensions） | ◐（`[ToolFunction]` 生成器 + ToolHandlerBase；SKILL.md/SEP-2640 对齐中；Footprint Ladder 纪律缺） |
| 编排扩展 | ●（5 Builder 全编译为 Workflow + checkpoint/HITL 继承；declarative YAML） | ●（Process Framework） | ●（Graph/Functional `@step` 缓存/检查点） | ●（Workflows 事件/处理器/循环/并行） | ●（Crews 自治 + Flows start/listen/router + 状态持久/恢复） | ●（可视节点 + 循环/分支/子工作流） | ○（Python-first，不学新抽象） | ◐（Codex fork/subagents；Gemini hooks/todos；OpenCode agents/subagents 权限） | ●（TOML 基线 + Mode 拓扑 + Agent Pack 7 模式；但 workspace override/readiness 诊断未暴露） |
| 协议/发布 | ●（Hosting.OpenAI/A2A/hosting-mcp + hyperlight/monty 沙箱实验） | ◐ | ●（Agent Server/A2A） | ◐ | ●（REST/Webhook/Enterprise） | ●（Dify App/API/MCP 工具发布；n8n 模板 9000+；Coze OpenAPI/Chat SDK） | ●（Tracing/部署 + Temporal） | ●（app-server/MCP-server/exec-server/JSONL 事件） | ○（无 A2A/MCP 发布端；Hosting 仅 MAF 侧能力，未经适配器暴露） |
| 宿主/SDK | ●（.NET/Python/Go + DevUI） | ●（Py/.NET/Java） | ●（Py/JS + SDK） | ●（Py/TS） | ●（Py + CLI + Studio + Enterprise） | ●（API/SDK + CLI difyctl + Chat SDK） | ●（Py/JS + REPL/测试工具） | ●（CLI/TUI/IDE/Cloud） | ◐（`AddTinadecCore/AddTinadecCoreMinimal/AddTinadecCoreHttp/MapTinadecCore` 已就位；但无发布包/生成式客户端/CLI） |
| 贡献/二次开发门槛 | 低（Learn + Discord + office hours + 示例矩阵） | 中（迁移期） | 低（文档最全） | 低 | 低（docs/llms.txt + skills） | 低（Dify 模板市场可一键采用；n8n 节点文档 48h 同步） | 低（examples/docs 完整） | 低 | 高（中文基线唯一权威 + 无公开 feed + 手写 DTO 仍在迁移） |

Tinadec 可扩展性结论：**内核扩展点设计是对的（通用 IToolProvider + 可打包 AspNetCore + Agent Pack），但“只有一个实现 + 没有市场 + 没有生成客户端”**，导致扩展性停在代码层，没有变成生态层。

---

## 5. 社区支持矩阵（有没有人）

| 信号 | MAF | SK | AutoGen | LangChain | LangGraph | LlamaIndex | CrewAI | Dify | n8n | Coze Studio | Agents SDK | Codex | Gemini CLI | OpenCode |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| stars（当天） | 13,452 | 28,552 | 60,918 | 146,072 | 41,391 | 52,118 | 58,333 | 155,328 | 203,924 | 21,572 | 29,326 | 123,059 | 106,895 | 206,346 |
| forks（当天） | 2,293 | 4,765 | 9,205 | 24,398 | 6,991 | 8,107 | 8,399 | 24,528 | 60,631 | 3,115 | 4,691 | 18,940 | 14,545 | 26,974 |
| issues（当天） | 602 | 269 | 1,065 | 478 | 774 | 749 | 749 | 1,061 | 1,146 | 568 | 44 | 16,464 | 820 | 5,746 |
| 发布节奏（当天 latest） | python-1.18.0（当天） | dotnet-1.80.1（09-03） | python-v0.7.5（2025-09-30，冻结） | core 1.6.2（09-04） | sdk 0.4.4（08-27） | v0.14.24（08-19） | 1.15.21（09-09） | 1.17.1（当天） | 2.38.6（当天） | v0.5.1（02-05，慢） | v0.22.2（09-09） | rust-v0.154.0（09-09） | v0.59.0（09-08） | v1.18.30（09-09） |
| 文档/支持 | Learn + Discord + office hours + blog | Learn（迁移指南） | 维护模式公告 + 迁移指南 | LangChain docs + LangSmith | 同左 + persistence/durability 专页 | workflows/memory 专页 + 博客 | docs/llms.txt + Enterprise | 官网 + Marketplace + Creator Center + SOC2/ISO | docs + 论坛 + 模板 9000+ | 中英 README + 架构/开发指南 | developers.openai + tracing/eval | developers.openai/codex + changelog | geminicli.com + policy/checkpoint 专页 | opencode.ai docs + permissions/MCP 专页 |
| 背书 | Microsoft | Microsoft（继任为 MAF） | Microsoft Research（已交棒） | LangChain Inc | 同左 | LlamaIndex | CrewAI（Enterprise） | LangGenius（Cloud/企业） | n8n（Cloud/企业） | 字节（Coze 商业版） | OpenAI | OpenAI（ChatGPT 订阅含 Codex） | Google（Code Assist 配额） | 社区（原 sst，现 anomalyco） |
| License 风险 | MIT ✅ | MIT ✅ | 双证 ⚠️（留意 CC-BY-4.0 文档） | MIT ✅ | MIT ✅ | MIT ✅ | MIT ✅ | 非 OSI ⚠️（自有开源证） | 非 OSI ⚠️（SUL fair-code，不可转售托管） | Apache-2.0 ✅ | MIT ✅ | Apache-2.0 ✅ | Apache-2.0 ✅ | MIT ✅ |

解读：

* 生态断层最明显的是 **AutoGen（60k star 但已冻结）** 与 **SK（28k star 但交棒 MAF）**：star 高不代表可选，维护状态才是选型依据。
* Tinadec 若对标 n8n/Dify 的“自托管+商业化”路线，必须先想清楚 license：Tinadec 现状 `Core MIT / Gateway AGPL / Tools AGPL / Office GPL`，组合分发走最严格者（AGPL 第13条网络条款），与 MAF/MIT 生态混用时需在报告外单独做合规评审（本报告只提示，不给法律结论）。
* Tinadec 当前社区支持为 `○`：无公开 star/fork/issues 口径、无 Discord/论坛、无英文文档 parity、无公开 feed，唯一事实源是中文产品定义 + 工作树。这在选型报告里是减分项，但也是机会：先把 codegen/SDK/feed 做出来，社区才有载体。

---

## 6. Tinadec 缺少什么（四维全景，P0 最先补）

### 6.1 缺失能力全景（相对 14 框架）

| P | 缺口 | 主流对照 | Tinadec 现状证据 |
|---|---|---|---|
| P0 | embedding/检索生产链路 | LlamaIndex（Parse+索引+Workflows）、Dify（RAG Pipeline）、CrewAI（embedder 可配） | `IEmbeddingProvider` 无路由即显式不可用；无 chunk/召回评测 |
| P0 | scheduling/定时/触发 | n8n（trigger/定时）、Dify（Trigger 插件）、CrewAI（Triggers & Flows） | 写路径 `501` |
| P0 | 可观测与评测闭环 | LangSmith、CrewAI Control Plane、Coze Loop、MAF DevUI + gaia/tau2、Agents SDK tracing+eval | `debug/*` 桩 + 无 eval/rubric/canary/成本账本 |
| P0 | 发布与运行交付 | MAF Foundry Hosted、Dify Cloud/VPC/自托管、n8n air-gapped、OpenCode serve | 无 NuGet feed/容器/OIDC/多租户调度 |
| P1 | 长期记忆治理 | CrewAI（scope/RecallFlow）、LlamaIndex（三 block + flush）、LangGraph（Store 跨 thread） | 仅 candidate review；无 confidence/expires/InjectionMode 四分法 |
| P1 | 上下文压缩生产化 | MAF（Compaction 家族 + 原子工具组）、LangChain（summarization/context_editing）、Dify（compaction 先清工具结果） | ToolCallAware 守卫 + 触发器有，压缩内核仍 MAF 可替换未收口 |
| P1 | 多租户/企业管控 | CrewAI（RBAC/审计/Enterprise IAM）、n8n（RBAC/SSO/SIEM）、Gemini（enterprise policy） | `ITenantContextAccessor` dev 占位；云多租户恢复需分布式 claim（自认） |
| P2 | 跨设备同步/内容寻址/远端 Host | Traycer（协议版本/分片）、PlanWeave（executionEnvelope 内容寻址） | 参考决策已明确拒绝在 MVP 做（正确），但需在路线图保留位 |

### 6.2 可扩展性差距

* Provider：Tinadec 多协议已追平第一梯队，差的是“ embedding + 模型评测/切换”（CrewAI multi-LLM testing、LangChain fallback）。
* Tool：从“一个子进程实现”到“多个 Provider 实现”（MCP server/HTTP/远端工具宿主经同一 `IToolProvider` 接入，C3）。
* Skill/Plugin：从“能读 SKILL.md”到“Dify 六类 Marketplace + 版本/签名/卸载回滚 + 按会话折叠工具面”（hermes-agent 规则）。
* 协议：从“内部 SSE”到“可发布的 A2A/MCP 端点 + 生成式 SDK”（MAF Hosting.OpenAI/A2A、OpenCode httpapi-codegen）。
* 配置：从“TOML 基线 + Pack 安装”到“声明式 YAML + 策略优先级小数编码 + Always Allow 持久规则对象”（Gemini/Dify）。

### 6.3 社区与生态差距

* 无公开仓库口径（本报告所有 star/commit 都是别人的，Tinadec 是分母缺失）。
* 文档只有中文权威版是对的（避免双源），但缺英文术语表之外的最小英文面 + `llms.txt`/skill 化文档（CrewAI/LangChain 已标配，Agent 才能读文档）。
* 无 `check:generated` 之外的生态抓手：Marketplace/模板/插件中心、cookbooks、Discord/论坛、benchmark（gaia/tau2 这类 Labs 载荷）都没有。
* 好消息：`reference-decisions` + `four-product-roadmap` 的证据链质量高于多数中小框架，转成公开文档即是冷启动资产。

### 6.4 落地路线建议（与四产品路线图对齐）

* **Phase 0（立即可做）：** B2 codegen 引导（外部 OpenAPI → `schema.d.ts` + `check:drift` 入 CI）+ A0 双仓所有权拍板。本报告建议落盘路径即 `TinadecOffice/docs/ai-agent-framework-selection-report.zh-CN.md`，与 codegen 同属“契约即代码”主线。
* **Phase 1（地基）：** A1 打包布局无关化 → A2 内部 NuGet feed + CI 发布 → A3 `MapTinadecCore`（已部分完成，需补容器镜像）+ B2.5/B3 `api.ts` 渐进迁移。
* **Phase 2（能力）：** C1/C2 Tool Provider 适配器化 → embedding 路由 + scheduling 写路径 + 长期记忆（confidence/expires + 四分法注入）+ eval（LocalEvaluator/Rubric/ExpectedToolCall 对标 MAF）+ canary/灰度。
* **Phase 3（扩展）：** A4（OIDC/多租户/容器/readiness 诊断）+ C3（第二 Provider：MCP server 经同一契约接入）+ B4（Core HttpApi 直生成，减 Gateway 手写 mapper）+ 市场/签名/卸载回滚。
* **不变量（任何阶段不换）：** 权限不可扩大、配置可复现、事件可审计、外部副作用不重复；MAF 只经 DmaEA 适配器替换，需过兼容测试门禁。

---

## 7. 选型结论表（一句话给每个框架定性）

| 框架 | 结论 | 给 Tinadec 的借用点 |
|---|---|---|
| MAF | **保留为执行底座**，跟踪 1.18.x（需过适配器兼容测试） | Workflow/checkpoint、5 Builder、Compaction、Approval 链、OTel、Hosting/A2A/MCP 发布、Eval/Labs |
| SK / AutoGen | **不选**（官方继任即 MAF；AutoGen 维护模式，SK 交棒） | 只借迁移指南，不借新能力 |
| LangChain/Graph | **最强对照组**，不替换但对齐 durability/HITL/记忆/中间件 | durability 三档、pending writes、Store 跨 thread、middleware（fallback/selection/summarization/limit） |
| LlamaIndex | **RAG 专项对照** | Parse + Memory 三 block + Workflows 事件编排 |
| CrewAI | **企业化对照** | 统一 Memory + Control Plane（RBAC/审计/成本）+ Tool Repository + eval |
| Dify | **平台化对照** | Marketplace 六类 + RAG Pipeline + LLMOps + 多发布形态 + difyctl |
| n8n | **自动化对照，license 慎用** | 集成广度 + 自托管/审计，但 SUL 不可转售托管 |
| Coze Studio/Loop | **可视化对照** | 资源一体（插件/知识/DB）+ Loop 全生命周期评测监控 |
| Agents SDK | **轻量对照** | guardrails（并行/阻塞/tripwire）+ Sessions + Temporal + Sandbox/Realtime |
| Codex CLI | **治理细节金矿** | Granular 五开关 + 三态判定 + execpolicy 沉淀 + rollout 不可变历史 + Windows 沙箱 |
| Gemini CLI | **策略语言金矿** | 小数优先级 + modes + MCP 通配 + shadow-git 三元组恢复 + headless fail-closed |
| OpenCode | **Harness UX 金矿** | allow/ask/deny + pattern + auto 模式 + MCP OAuth + 生成客户端管线（httpapi-codegen） |
| Traycer/PlanWeave/grok/hermes/better-harness | **治理佐证**，不单独选型 | 角色≠权限、三类快照、显式任务图/review gate、会话级工具面、写前门禁 |

---

## 附录 A. 取数命令与可复现性

```powershell
# stars/forks/issues/pushed（发报告当天）
$repos = @("microsoft/agent-framework","microsoft/semantic-kernel","microsoft/autogen","langchain-ai/langchain","langchain-ai/langgraph","run-llama/llama_index","crewAIInc/crewAI","langgenius/dify","n8n-io/n8n","coze-dev/coze-studio","openai/openai-agents-python","openai/codex","google-gemini/gemini-cli","anomalyco/opencode")
foreach ($r in $repos) { (Invoke-RestMethod "https://api.github.com/repos/$r").stargazers_count }

# HEAD 与 release
Invoke-RestMethod "https://api.github.com/repos/microsoft/agent-framework/commits?per_page=1"
Invoke-RestMethod "https://api.github.com/repos/microsoft/agent-framework/releases/latest"

# 本地检出对照
git -C agent-framework log --oneline -1; git -C agent-framework branch --show-current
git -C TinadecOffice log --oneline -3
```

本地远端对照（当天实测）：`agent-framework→microsoft/agent-framework.git@main@2c49f50cf`；`langchain→langchain-ai/langchain.git@master@eef3eaeac8`；`langgraph→langchain-ai/langgraph.git@main@81bf17b23`；`dify→langgenius/dify.git@main@dde1d500b5`；`opencode→anomalyco/opencode.git@dev@337fd144d2`；`codex→openai/codex.git@main@6af345407d`；`gemini-cli→google-gemini/gemini-cli.git@main@85aca163f`；`traycer→traycerai/traycer.git@main@122ced28a`；`PlanWeave→GaosCode/PlanWeave.git@main@a5115d9d`；`grok-build→xai-org/grok-build.git@main@72a61251`；`better-harness→QoderAI/better-harness.git@main@7cd26e9`；`hermes-agent→NousResearch/hermes-agent.git@main@693641aa8b`。

## 附录 B. 报告局限

* GitHub API 有速率限制与 `stats/participation` 202 computing，大仓部分活跃度记为不可用，未强行估算。
* `NousResearch/hermes-agent` 的 244k star 为 API 原值，量级异常，仅原值引用，不参与排名。
* Tinadec 无公开仓库口径，社区对比为单向（别人有数，Tinadec 缺数），结论偏向“先补交付载体再谈社区”。
* License 仅做选型风险提示，不构成法律意见；Gateway/Tools AGPL 与对外托管的组合需另行合规评审。
