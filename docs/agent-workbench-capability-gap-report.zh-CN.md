# TinadecOffice 智能体工作台 — 能力差距报告（2026-09-22）

范围：`Everything-changed` 分支上从阶段 0（`a673112`，锁定绿色基线并收口 TinaChat 唤醒/交接环）起的 49 个提交（含上一批的 `296c377`、`efae106`），加本批——本批按 Conventional Commits 分两次落地：Core 那条读路径的修复，和它上面新长的用量面。对标对象：codex、opencode、hermes-agent、gemini-cli、cline、harness-mix、Claude Desktop 的公开形态。

## 判据（三个等级，不可混用）

- **A 已验证**：有自动化测试穿过这条链（含端到端 run 断言），且阶段末门禁按 `GATE_EXIT` 记账为绿。
- **B 半验证**：实现落地、且**至少一侧**被断言，但存在一条明确"没人看的那半边"——响应体没有 schema、真机观感没复看、或只由脚本化模型驱动。
- **C 缺口 / 未验证**：代码里没有；或有实现但链断在一个从不被读取的位置。

**本报告里"已验证"永远不等于"能在真实模型上跑通"。** 见 §4 第 1 条——这是全篇最重的一条，其他缺口都能排在它后面。

## 1. 结论一句话

工作台侧的功能面（编辑重发、附件、指令文件、技能、记忆评审、上下文可见性、**一次 run 的 token 用量**、逐文件评审与撤销、命令面板、终端、受治理的网络出口、50 个 provider 工具）**已成体系且有自动化证据**；**没有**的是三件不同性质的事：真实模型从未被测过（C）、语义检索/视觉输入这类模型能力面（C）、以及交付工程本身没有 CI（C）。

## 2. A 级：本轮或此前有测试穿过

| 能力 | 落点 | 证据锚点 |
| --- | --- | --- |
| 审批可信（冻结证据、脱敏后铸进事件、正文不外泄） | Core `Governance` + 桌面审批面板 | `ApprovalEvidenceProjectorTests.cs` 10 例——含 `SecretShapedKeysAreRedactedEntirely`、`WriteFileCallNamesThePath_ButNeverCarriesTheBody`、`DigestStaysWithinTheDurableColumnBudget`、`UnparseableOrNonObjectParametersDegradeInsteadOfThrowing`；桌面侧 `feat(app): render decision-grade evidence in the approval panel` |
| 会话内编辑并重发（消息级截断，行永不删除） | Core revert 端点 + 网关代理 + 桌面 composer | `836abe1`（`StorageApiTests.cs` 内 +97 行断 revert 语义；该路由在 `openapi.core.json` 里有 30 行新契约）+ `dc08182` 桌面接线 |
| 工具 id 对齐真实清单（图标/结果视图/写路径） | 桌面 `lib/toolPresentation.ts` | 清单是**量出来的**：`8c6edc2` 用 provider `#manifest` 实测 50 个 id，`toolPresentation.test.ts` 钉住计数并把任何解析成 `other` 的 id 判红 |
| 附件垂直切片（上传→绑定消息→模型可见→分页读取） | Core `Attachments` + `read_attachment` 虚拟工具 | `4ae7c62`/`9445933`，`AttachmentApiTests.cs` |
| `@` 提及补全工作区路径 | 桌面 composer | `ed4766d` |
| 斜杠命令与命令面板共用一张表 | `lib/appCommands.ts` 唯一 owner | `4292851`/`63cafac`/`b474263`，`appCommands.test.ts` 22 例 + 路由名存在性断言 |
| 一个窗口一条会话事件流 | 桌面流读取收敛 | `a2be1e5`/`499d7a8`（并删掉死 WebSocket 组合式与规则文件仍在推荐的传输接缝 `2eb4bbd`） |
| 项目指令文件进入上下文（优先级/截断/越界拒绝） | Core `Context` | `091d4bf`，`WorkspaceInstructionApiTests.cs` 走真 provider + 真文件系统 |
| 工作区技能渐进披露（索引上屏、正文留在磁盘） | Core `Skills` + `Context` | `9870854`，`WorkspaceSkillApiTests.cs` + `WorkspaceSkillPolicyTests.cs` 32 例 |
| run 被告知了什么：来源清单、逐条 token 价格、被预算裁掉的条目及原价 | `context.packed` → orchestration 投影 → 桌面 | `c4b1ac9`/`c0402e8` + 本批（`WorkspaceInstructionApiTests` 新增 2 例含负对照；`FullDuplexEndpointTests` 按索引断言行数/同序/求和） |
| 快照逐文件评审与单文件撤销（带 `expected_sha256`） | Core 三端点 + `SnapshotsPage.vue` | `957d061`/`3558181`，`WorkspaceFileReviewApiTests.cs` 16 例 |
| 记忆评审面：候选→裁决→晋升→检索→撤销，可筛且带依据/适用条件/失效条件 | Core 读写口 + 网关 2 条代理 + `MemoryPage.vue` | `6b80210`/`ea013bd`/`4f2f0aa`，`MemoryReviewApiTests.cs` + 网关 `runtimeProxy.test.ts` |
| 晋升记忆真的进 prompt、未评审候选真的不进 | `MemoryStore.RetrieveAsync` | `FullDuplexEndpointTests.InvokeStream_SendsPromotedMemoryAndKeepsAnUnreviewedCandidateOutOfThePrompt` |
| `web_fetch`：受治理的网络出口（SSRF connect 期防护、逐跳复验、一份失败只说一件事） | `TinadecTools/Tools/Web/` + `HumanOnlyTools` | `7bceecb`，`WebFetchTests.cs` 51 例（含"绝不向被拒地址开 socket"的生产 handler 用例）；把守卫短路后恰好 3 例红 |
| 终端：本地 node-pty + 智能体终端（`shell` 工具的 Core 会话，事件日志回放 + 审计输入） | 桌面 `useTerminal`/`useTerminalSource` | 存在且被 `useTerminal.test.ts`/`TerminalCallBlock.test.ts` 覆盖（本批复核其架构注释与 `shell` 工具在实测清单中的事实） |
| 一次 run 花了多少：按 `model × provider` 分组的 token 与调用数，读不到就明说读不到 | Core `model-invocations` 分页 → 网关透传 → `OrchestrationTab.vue` + `lib/modelUsage.ts` | 本批。Core `ModelInvocationQueryTests.cs` 7 例——其中 2 例**先在未修复的构建上跑红**（`from`/`to` 窗口、并列时间戳的游标），另有 1 例钉住 OpenAPI 里 token 三字段并禁止 `prompt_tokens`/`completion_tokens` 回来；桌面 `modelUsage.test.ts` 7 例 + `OrchestrationTab.test.ts` 14 例（新 7），断"分组求和 == 每行 `total_tokens` 之和""缺字段渲染成'未报告'而不是 0""翻页被截断时必须自称是下限"。**注意这一行的 A 级只覆盖"读得到、算得对、说得出边界"**：没有任何单价，所以它不是成本面板（见 §4 第 11 条） |
| 一次 5xx 会自己留下原因（进程内有界环，不上外线；测试主机读回） | Core `AspNetCore/ServerFailureJournal.cs` | `ServerFailureJournalTests.cs` 6 例：同一次响应**同时**断 body 不含异常类型名 + journal 含最内层消息；4xx 不进环；裸宿主（未 `AddTinadecCoreHttp`）仍能回 500；环形淘汰；`Describe()` 把"给修的人看的那一行"格式钉死（含外层/最内层两段）。消费面：`ServerFailureReports.AssertStatusAsync` 是这句话唯一的构造点，9 个断言站（FullDuplex/ToolChain/Unattended 三个 stream helper 的 admission·run-stream·replay）经它读回；另有一条经真 Core host 的读回断言（`Interactions_StaleContextRevision_Returns409` 断 `LastFailure()` 报"没有 5xx"）。**并且它已在真实红例上兑现过一次**：本批整解门禁的一条 `POST /interactions` 500 第一次带出 `server cause: … SQLite Error 5: 'unable to delete/modify collation sequence due to active statements'`（详见 §4.5 第 5 条）。自 host 走 `CliRuntimeTestServers` 既有做法 |
| MCP 人类侧读面：清单带出处、连不上的服务器仍在场、逐服务器给 schema | Core `AspNetCore/Endpoints/McpEndpoints.cs` + 网关两条内联代理 + `MarketDetailCard.vue` | 三侧各钉一遍：`McpInventoryApiTests.cs` 10 例（承重那条是 `Tools_AreNeverReportedMissing_WhileTheProviderCannotAnswer`——provider 起不来时**必须** 200 + `source:"tool_provider_unavailable"`，不能 404 让人以为服务器被删了）、网关 `mcpProxy.test.ts` 5 例（含"外部契约里只剩这两条 MCP 路由"与"`mcp_server_not_found` 不被压成 `conflict`"）、桌面 `MarketController.test.ts` 3 例（变异 `mcpReadSucceeded → true` 恰红 1 例） |
| 工具层：`mcp_list include_schema=false` / `mcp_search` 默认参数不再整次失败 | `TinadecTools/Tools/Mcp/{McpModels,McpClientPool}.cs` | `tests/TinadecTools.Tests/McpPassThroughTests.cs` 2 例，断的是**序列化之后**的线上形状（解析断 `input_schema` 为 JSON null）。修前修后各用本地 provider 靶子实测一次：修前 `{call_id:1,success:false,error:"Operation is not valid due to the current state of the object."}` |

## 3. B 级：实现落地，但有一半边没人看

- **状态码本身也是没契约的**。上一句还只说"响应体缺 schema"，实测更糟：Core 快照里 `POST /api/v1/sessions/{id}/interactions` 声明的是 **`200 OK`（且无 content）**，而 `InteractionsEndpoints.cs` 的四条出口全部 `Results.Created` 实际返回 **201**——224 个 2xx 响应声明里只有 **4 个**写了 201。也就是说契约不但没描述字段，还**写错了状态码**，任何按它生成的客户端（`generated/schema.d.ts`）拿到的是不存在的形状。桌面 `api.ts` 因此在两处各自手写这个 receipt（`api.ts:2559` 与 `api.ts:2183` 的内联声明），`turn_id` 只从 SSE 帧读、从不从 receipt 读——**三份互相不知道对方的存在**（任务 #23 收这条）。同理：receipt 有四种形状（admitted / `message_only` 无 `run_id` / steering / queued replay），一个"全字段可选"的 DTO 只会把"哪个键配哪个 `status`"这件事继续藏起来，所以要按 `status` 可辨识联合来写，而不是摊平。
- **大多数响应体根本没有契约**。实测两份 OpenAPI 快照：`TinadecCore/tests/__snapshots__/openapi.core.json` 220 个 operation **全部声明了 2xx 状态码，但只有 41 个（18.6%）带响应体 schema**；`TinadecGateway/tests/__snapshots__/openapi.external.json` 229 个里 49 个（21.4%）。两边各 +1 是本批的 `model-invocations`——也就是说**"给一个读路由补上类型"目前是唯一被实测推进过的进度**，其余 179/180 条仍然一测未动。也就是说 `npm run check:drift` 能护住的只有那一小截——`/api/v1/runs/{id}/orchestration`、`/api/v1/workspace-snapshots/*` 这类返回裸匿名对象的读路由在两份快照里都是 `content: none`，响应字段漂移完全看不见。**这是任务 #21 那类缺陷（桌面 DTO 声明 Core 从不发送的字段）能连着发生三次的结构性原因**，不是粗心：没有任何机器面会因为它写错字段而变红。当下夹住字段的是两头——Core 测试断铸造侧、页面测试断调用侧。补法是把读路由改成有类型的响应（现仓惯例是 `.Produces<T>(status)`：实测 65 处、集中在 4 个文件；`TypedResults`/`WithResponse<T>` 零使用），但那会连带改动 190 条路由的 OpenAPI 面貌，属于阶段 8 的重构窗口，不做零敲碎打（任务 #30）。补完还得和 `openapi.core.json`/`openapi.external.json` 两份快照、`schema.d.ts` 再生成、DTO、测试与中文产品定义**同一次改动**落地（`docs/architecture.md` 的 PUBLIC API VERSION RULE 要求同批），两条 `git diff --exit-code` 门禁会各自把关。
- **用量面的"另一半"仍然没契约**：本批给 Core 那条路由补上了类型，但网关按自己的透传惯例把数组元素写成 `t.Array(t.Unknown())`，所以重新生成的 `apps/desktop/src/generated/schema.d.ts` 拿到的是 `items: unknown[]`——**桌面那侧的字段类型仍然只存在于手写 `api.ts` 里**。这次推进的实质是：字段名第一次被钉在一个会自动变红的地方（Core 快照 + 一条读它的断言），而不是"生成的客户端终于知道有哪些字段"。同一句话也解释了 §4.7 第一条为什么还不能动。要收掉它得让网关给这个页面声明元素 schema，那属于 #30 的一部分。
- **桌面端全部界面证据都在 happy-dom 里**。没有一次真 Electron 像素级复看：命令面板首屏密度、`Context Packs` 那行新加的裁掉句（明显比 chip 长，会不会拉爆行高未知）、快照评审卡塞进密集卡片后的观感、**本批新增的 Model Usage 块（一行 chip 里塞"模型 · token 数 · 未报告数 · 调用数"，长模型名下的换行与截断没看过）**。类型与测试全绿不等于这些面能用。另外这个面沿用了 `OrchestrationTab.vue` 的既有做法——**整份文件是硬编码英文，一个 `t()` 都没有**，所以它不在 `i18nParity.test.ts` 的登记面里；这是 #10（可访问性/设计令牌/文案契约扩面）要一并收的历史账，不是本批新引入的偏离。
- **`web_fetch` 的礼貌性与缓存**：无 robots.txt、无 ETag/If-Modified-Since、无 HTTP 代理与自定义 CA（企业内网场景）；HTML→文本是手写扫描器，SPA 骨架可能取到空壳。工具描述里未承诺能渲染，因此不算撒谎，但算缺口。
- **上下文可视化只到"来源 + 价格 + 被裁"这一层**。装配之后的第二处裁剪（`Prompts/PromptsModuleRegistrar.cs` 的片段预算，结果写进 `PromptAssemblyResult.Warnings`）**至今没有读者**——模型最终真正读到的片段，与 `context.packed` 说的那份之间，还隔着一段无人观察的裁剪。

## 4. C 级：缺口，按"补它的价值 / 前置代价"排序

1. **真实模型从未进入任何测试**。全部端到端 run 用例由 `ScriptedChatClient` 驱动；`docs/core-agent-e2e-verification-2026-08-29.md` 那次"真实 HTTP 实测"用的也是**本地脚本化 OpenAI 端点**，且快照于 2026-08-29 的 `main`。因此"规划→工具→回馈→收敛"这条主链在真实模型下的行为（解析失败率、重试、token 计费、慢响应下的审批超时）**全部未验证**。前置：一条可显式跳过、有密钥才跑的 egress 冒烟用例 + 一个真跑一遍的记录，而不是把脚本化模型换掉当回归基线。
2. **语义检索没配**。记忆检索是"作用域过滤 + 关键词打分"，向量化路由未配置（`Memory`/`VectorStore`/`IProjectVectorDatabase` 有端口没有线路）。后果具体：**换了说法的同一条事实召不回来**，而"晋升记忆"的价值恰恰在于跨会话复述。同时 `applicability`/`expiry_condition` 只能给人看——`MemoryEntry` 只带 content，条件失效进不了模型。
3. **视觉/音频输入不做**。`CoreAttachmentReadTool.cs` 明写"本运行时不向任何 provider 发送 image 或 audio part"，非文本附件直接被拒。对标 Claude Desktop/Codex 的截图工作流，这是硬缺口，且它不在桌面端而在 provider 协议层。
4. **桌面端没有任何 CI 门禁**（任务 #22）。`npm run test`、`typecheck`、`vite build`、`check:drift` 全都要靠人（或我）在阶段末手跑；本批四个门禁就是这么跑的。任何"绿色"都是**报告**而非**约束**。动 CI 需要用户点头（本批未动）。
5. **Api.Tests 在高负载下有抖动族**（任务 #25）。**本批第一次拿到带原因的红**（正是这次诊断前置的目的）。整解门禁里：`ToolChainEndpointTests.AskMode_RunsOnNarrowedRoster_BrowserWorkerCompletesWithoutSupervisor` 红，消息现在是——

   > `Interaction admission returned 500 InternalServerError: {…,"detail":"An unexpected error occurred.",…} | server cause: POST /api/v1/sessions/{id}/interactions [internal_error] Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'unable to delete/modify collation sequence due to active statements'.`

   **机制坐实**：这条家族有两种措辞（`user-function` 与 `collation sequence`），都指向同一件事——Microsoft.Data.Sqlite 在**池化物理句柄 `Open()`** 时重注册 per-connection 对象，而该句柄上还有未走完的 reader。注册方**就是 EF Core 的 Sqlite 提供程序**：实测 `Microsoft.EntityFrameworkCore.Sqlite.dll` 10.0.10 里含 `CreateFunction` 与 `CreateCollation` 两处符号引用（这两个是 Microsoft.Data.Sqlite 的 API，EF 用它们挂上 `ef_*` 函数与排序规则），而全仓自身 `CreateFunction`/`CreateCollation`/`UseCollation` 零命中。
   **同时记一次自我纠正的往返**：本批中途我曾把"注册方是 EF"这句**作废**，依据是"EF 的 Sqlite 程序集里没有 `RegisterFunction` 符号"——那次作废是错的：`RegisterFunction` 是 EF Relational 层的模型 API 名，SQLite 提供程序落的是 `CreateFunction`/`CreateCollation`；真正的凭据是现场 journal 消息 + 按正确符号名重扫。教训写进本文件：**推翻一条旧结论需要与立论同等强度的证据，猜错符号名会把对的改成错的**。
   顺带量到并仍然成立的约束：全仓连接串只设 `DataSource`（`Persistence/ServiceCollectionExtensions.cs:102-107`，无 `Pooling`/`Cache`/`DefaultTimeout`/`Mode=Memory`）；每个 Api 测试类实例一份 temp 库文件，且 `xunit.runner.json` 关掉程序集与 collection 并行——**所以碰撞不是"多个测试宿主共享池"，而是同一个 host 内部的并发**（后台写者确实存在：`DmaEA/FullDuplexRunEngine.cs`、`Runtime/RecoveryCoordinator.cs`、`Runtime/TinaChatWakeService.cs`）；全仓也没有任何 `journal_mode`/`busy_timeout` 设置，只有迁移期 `PRAGMA foreign_keys`。
   **修法回到连接生命周期**，两条候选都还没验证：① 让池化 `Open()` 不再撞上仍带 active statement 的物理句柄（关池 / 收敛连接生命周期）；② 在读路径对 Error 5 做**定向**重试——它不是 busy/lock 码，但重试确实会再走一次注册，所以是候选修法而不是纯粹掩盖。**定向实验仍未做**（C 级）。诊断前置本身已落地并**已被这条真实红验证**：原因进 `AspNetCore/ServerFailureJournal`（只在 DI 可见，绝不上外线），9 个断言站经 `ServerFailureReports.AssertStatusAsync` 打印 problem body + `server cause: …`，另有 6 例 journal 测试与一条真 host 读回断言。

   **同日定向实验的增量（见 §5d，务必按它的可信度分级读）**：这一族**不止 SQLite**。同一棵树在负载下第一次拿到第二族的完整机理——`CliRuntimeTests.CliProcessManager_ReusesReachableServerUrlWithoutSpawning` 的可达性探测在 CPU 饥饿下判成"没在跑"，于是走到 `SpawnAsync` 去启动一个测试故意配的不存在二进制（`DmaEA/CliRuntime/CliProcessManager.cs:131`）。它与 SQLite 无关，形状相同：**时序敏感的守卫在负载下翻到失败分支，而失败分支的报错指向别处**。收口动作因此收窄成一条具体的：探测超时必须 report 成"我没等到（带超时值）"，而不是静默改走 spawn。同轮的 `GATE_EXIT=4`（连摘要都没打出来）**不记在产品头上**——同一任务的输出里有 `bash: fork: Resource temporarily unavailable` 与 `0xC000026B`，是我自己的负载循环耗尽了进程位；所以"负载下全量必崩"这句话本仓**没有**证据，能说的只有"这台机器在 40 轮 vitest 并发下无法干净跑完一次全量"。
6. **孤儿测试工程**：`tests/Tinadec.Contracts.Tests` 引用了不存在的 `../../src/TinadecCore/TinadecCore.csproj`，且**不在任何 `.slnx` 里**——它不红，因为没人跑它。删或修都要用户定（未动）。
7. **架构债**（任务 #9，本批**修正了其中一条的因果**）：
   - ~~Home / Workbench 两页职责重叠，合并~~ —— **这条提法是错的**，两处证据：①`docs/app-core-ui.md` §3.1 规定两页各司其职（Home=meeting 入口/项目/会话/消息/队列，Workbench=run/task graph/worker/supervision/context），L644-645 还是两条独立的验收项；②真正重复的是"同一个 orchestration 快照有两套类型两条读路径"，而 `pages/WorkbenchPage.vue:130` 那句 `as unknown as never` 不是偷懒：生成的 `OrchestrationSnapshot` 把 `nodes`/`context_packs`/`flows`/`step_results`/`agent_instances` 全声明成 **`unknown[]`**（实测 `src/generated/schema.d.ts`），而 `src/api.ts:1941` 手写的才是完整类型。**结论：这条债被 §3 第一条（任务 #30 响应体 schema）挡住**——现在删 cast 只会把 cast 挪进组件内部。顺序改为先 #30 再回来。Workbench 的 lineage + `RunLaneCanvas` 也没有 Home 等价物，合并会真丢功能。
   - `SettingsPage.vue` 的**第一层已经搬完（2026-09-22）**：agent-pack 生命周期（`checking|install|upgrade|deferred|conflict|error` + reload/toggle/adopt/uninstall）整体搬到 `src/settings/sections/AgentPacksPanel.vue`，页面从 **3731 → 3502 行**（−229）。回归网用的是仓库现成的三条，而不是新发明的断言：`SettingsPage.agentPack.test.ts` 4 例（克隆流、清单三态、卸载必须先确认、清单路由失败退化成空表）改动前后都绿——**"搬动没改变用户可见表面"这件事是它们证的，不是我说的**；`SettingsPage.smoke.test.ts` 的 import/usage 契约顺手扩到 4 个 Agent Center 面板，并把匹配从 `<Name />` 放宽成 `<Name`（带 props 的面板永远不会自闭合）；`i18nParity.test.ts` 登记新面板的 `t()` 引用。**仍留着**：Agent Center 五个子标签的本体、窗口与材质 chrome，以及 `clone`/`uninstalled` 之外那类需要页面状态的动作。
   - SQLite 打开路径没有任何重试或 `busy_timeout`（实测全仓零命中）。§4.5 第 5 条拿到现场原因后这条重新变成一个**候选修法**（Error 5 出在池化 `Open()` 重注册时，重试确实会再走一次注册），但仍未验证，也不能替代连接生命周期那条。lane 编排这条产品定义要不要保留，需要用户决策，未动。
8. **撤销记忆的理由无处可存**：`MemoryItemRecord` 没有 reason 列，`RevokeAsync(itemId, reason)` 收下就丢。桌面因此刻意**不收集**撤销理由——收一个会被扔掉的答案等于教用户输入噪音。要留这条审计得加列（本仓 schema 走 `DbContextMigrationParticipant` 自研路径，不是 EF Migrations）。
9. **`web_search` 没做**。它需要外部搜索 API 密钥与配额治理，本仓没有这个配置面；宁可登记为缺口也不做假实现。
10. **候选列表没有分页游标**：`limit` 有上界 500，但没有 next-cursor，翻 500 条以上只能靠更窄的筛选。列表每行还要读一次内容 blob，所以筛选必须在进库前做完。
11. **几类工作台常见能力本仓完全没有**，此前散在各阶段里没有汇总：生命周期 hooks/自定义命令、编辑器级诊断（LSP）接线、行内 tab 补全、跨会话全局搜索。这一条我**没有逐条核对参考项目的实现细节**，只断言"本仓没有"，不借用别人的功能清单当自己的需求。**其中的成本/用量面本批已经交付了一半**（见 §2 与 §3）：`model-invocations` 现在有一个"这一轮花了多少 token"的读面，但**仍然没有金额**——本仓没有任何单价表，也没有能放它的配置面，所以这块面板刻意只说 tokens，并写明了为什么。剩下的部分照旧是缺口：跨 run/跨会话的汇总、按 provider 的配额与上限、"这次改动值不值这么多"的比较。
12. **异常按消息字符串分类，会把基础设施故障伪装成产品错误**（本批现场撞到的）。`AspNetCore/TinadecCoreHttpExtensions.cs` 有一条 `InvalidOperationException when Message.Contains("model"|"Provider") => 400 model_not_configured`，而 EF 的"这条 LINQ 翻译不了"消息里天然带着实体类名——`DbSet<ModelInvocationRecord>` 里的 "Model" 就足够让一次**查询翻译失败**以"模型没配好"的身份返回 400。后果不是难看：调用方看到的是"去配一下模型"，真因是"这个过滤器在本仓的 SQLite 上从来不能用"，而 400 也**不会进上一批刚上线的 5xx journal**（`server cause: the handler recorded no 5xx`），于是新诊断面在这条路上恰好帮不上。本批只修了那一条路由，**没有动全局分类规则**（它服务所有路由，改法的影响面我看不到全）。要收的是：给"未映射的基础设施异常"一个不会被字符串劫持的落点，并让 4xx 的机器码能反查到自己那句 detail。另外同源的、已被本仓三处注释写明但**没有任何一条测试守着**的规则也一并登记在这里：SQLite 翻译不了 `DateTimeOffset` 列与参数的比较，任何新读路由直接写 `Where(x => x.SomethingAt >= value)` 都会变成上面这种 400。

13. **市场的写那一半仍然全是占位**（本轮把 MCP 的**读**那一半转成 A，读以外没动）：Core 的 `market/sources`（POST）、`market/sources/{id}/refresh`、`market/catalog/{id}`、`extensions/*` 依旧 501/占位，桌面 `MarketController.ensureBuiltInSources()` 开局仍 POST 两个 `tinadec://marketplace/*` 源并被拒——区别只剩"不再被 `catch {}` 咽掉"。要接的源已按实测形状定下：官方 MCP Registry `GET https://registry.modelcontextprotocol.io/v0/servers`（**免密钥可读**，顶层 `{servers, metadata:{nextCursor,count}}`，条目 `{server:{name,title,description,version,remotes[{type,url}],repository,packages}, _meta}`）、SKILL.md 仓库、本机 CLI 目录（复用已实现的 `model-providers/cli/discover` + `connect`）。用户已拍板安装边界＝**可拉包，但必须审批 + 固定版本**，落盘复用 `UserToolAction` + 受治理 `write_file`（自动带快照与逐文件撤销）。分阶段登记在任务 #36（只读耐久面）/#37（提案+审批安装）/#38（技能源）/#39（CLI 目录 + 页面重写）。
14. **MCP 服务器归属看不出来**（本轮新登记，挡住 #37 的一部分）：provider 读的 `mcp_servers.json` 里没有任何"这一条是哪次安装写的"信息，所以"这个扩展装了哪些服务器"无法回答。桌面此前按 `server.extension_id` 过滤，而该字段 Core 从不发 ⇒ 那个区块**在真机上永远不渲染**，只在预览画廊的假数据里"工作"过——本轮已把假数据改成真形状并删掉该区块。要恢复这个问法，得先给安装记录持久化归属。
15. **连不上的 MCP 服务器，给人看的是乱码**：清单里 `error` 字段带子进程 stderr 尾部，zh-CN 机器上非 ASCII 部分全成 `\ufffd`（实测：`'definitely-not-a-real-binary' \ufffd\ufffd\ufffd\ufffd…`）。解码发生在 ModelContextProtocol SDK 的 stderr 读取侧，不在本仓；结果是"为什么连不上"这句话对中文环境的使用者基本不可读。本轮只在界面如实显示原文，未伪造翻译。

## 5. 阶段末门禁实测（按批次记账，全部读日志里自己写的 `GATE_EXIT`/`EXIT`，不看管道退出码）

### 5f. 2026-09-23 第六批：市场面从"表存在但没人写"变成一条能读的路（#36 / M1）

- **这条比 §5e 那一类更糟**：MCP 至少是"投影没写"——`mcp_list` 早就把答案算好了。市场这两张表
  （`extension_sources` / `extension_catalog_entries`）自骨架起**零写入者、零读者**，所以 M1 不是补投影，
  是从 adapter 开始建。桌面还每次加载都 POST 两个 `tinadec://` 假源，501 被 `catch {}` 吞掉——
  这就是"预览画廊里满、真机上永远空"的完整成因链。
- **出口面是这条增量里唯一真正新的东西**：provider 侧新增预留控制工具 `#fetch`（复用 `WebFetchGuard`，
  不改其策略），Core 侧新增 `IMarketCatalogService` + 官方 MCP Registry adapter。两侧策略**故意分开放**：
  Core 决定"可以打哪个 URL"（https-only、无 userinfo/query/fragment、查询串由 Core 自己拼），
  provider 决定"这个地址能不能连"（connect 期按即将拨号的 IP 判定）。理由写在代码注释里：
  Core 再开一套 HTTP 栈，就是第二份需要同步、需要审计的策略。
- **三种结局只有一种能删行**是本批的语义核心。`fetched` / `blocked` / `unavailable` 之外还有一条
  `truncated_pages`（页数上限砍断 cursor 链）：此时**一行都不删**、`removed_rows: 0`，
  因为"没读完"不是"市场变小了"。`refused_rows` 单独存在：无 name 或无 version 的行不落库但必须报数。
- **一次为 M2 预埋的不变量**：`(SourceId, ExtensionId, Version)` upsert 保留原行 `Id`，
  `manifest_hash` 对键序不敏感（对内容敏感）。这两条现在没有消费者，M2 的"固定版本"一上来就靠它们。
- **搜索转义是量出来的**：去掉 `EscapeLike` 后 `q="%"` 从命中 1 行变 3 行，且只有那一条测试变红。
- **诚实缺口**：只有一个 adapter（`skill_repository`/`cli_runtime` 创建即 400，留给 M3/M4）；
  装不了任何东西（`/extensions/*` 七条仍 501，且新增一例断言它必须是 501 而不是空 200）；
  真实 registry 端到端一次没跑过（CI 不出网）；分页是 `offset`，并发刷新会让第二页漂移；
  `expires_at` 只上报没人读；删除源是硬删——M2 让安装引用 `catalog_id` 之后必须先加引用守卫。

- **两条实现限制，本轮新登记（不是缺口清单里的旧账）**：
  ① `reason` 直接把 provider/异常的 `Message` 原文外发给客户端——这与 `McpEndpoints` 的既有做法一致，
  但 `docs/security.md` 的"不把内部细节交给调用方"口径并没有一条测试守着这条新路由；
  ② `ListCatalogAsync` 对同一过滤条件**枚举两次**（一次取 `total_available`/`as_of`，一次取页），
  所以并发刷新能让"共 3 条"和"这一页 2 条"来自两个瞬间。它属于下面那条 offset 缺口的同一根因，
  换游标可以一起解决，本批不换是因为没有消费者在翻页（M4 才做分页 UI）。
- **阶段末门禁实测（顺序跑，逐个读日志自己写的退出码）**：Core `Api.Tests` `exit=0`、**486/486**（16m19s）；
  `TinadecTools.Tests` `exit=0`、**292/292**；Governance **44/44**、Architecture **17/17**、AgentFramework **329/329**；
  Gateway `bun test src` `exit=0`、**70/70**；Desktop `vitest run` `exit=0`（**738 通过 / 14 跳过**）、
  `typecheck` `exit=0`、`vite build` `exit=0`、electron `node --test` **24/24**；
  `check:drift` `exit=1`（按机制必然：它以 `git diff --exit-code` 收尾，本批契约是有意变更且未提交），
  其实质另量——连跑两次 `generate:client` 后 `schema.d.ts` sha1 恒为 `420a35ba…218576`。
  **顺带一条对 #25 的正面观测**：上一批同树顺序跑 457/457，这一批把三个门禁从并行改成顺序后，
  486 例里负载族一条没红——与 §5d 的 A/B 结论同向（并发放大），但**仍不构成根因证明**，
  因为本批同时新增了 29 例，样本不是同一份。

### 5e. 2026-09-23 第五批：MCP 人类侧第一次读到真东西（#35）

- **Core 全量** `dotnet test Api.Tests` → **`API_EXIT=0`、457/457、15m44s**（上一批 448 例 / 21m32s；多的 9 例正是本批新写的 `McpInventoryApiTests`）。`TinadecTools.Tests` **`TOOLS_EXIT=0`、284/284**；网关 `bun test src` **`BUN_EXIT=0`、63 pass / 0 fail**（+5 条）。
- **桌面**：`npx vitest run` **`VITEST_EXIT=0`、80 文件通过 / 1 跳过、736 例通过 / 14 跳过**（较上一批 +1 文件 / +3 例，正好是本批新增的 `MarketController.test.ts`）；`npm run typecheck` **`TSC_EXIT=0`**（仍是 0 错误，这条门禁从上一批起是硬门槛）；`vite build` **`BUILD_EXIT=0`**（1m36s）；electron `node --test` **`ELECTRON_EXIT=0`、24/24**（本批没动主进程，跑它是为了不拿"没跑"当"不会坏"）。文档定稿后再重跑两个读文档的快门禁：**`GOV_EXIT=0`（44/44）、`ARCH_EXIT=0`（17/17）**。`npm run check:drift` 提交前 **`DRIFT_EXIT=1`**——它以 `git diff --exit-code -- src/generated/` 收尾，未提交的有意契约变更必然让它红；其实质一半单独量过：连跑两次 `generate:client`（`GEN1_EXIT=0`、`GEN2_EXIT=0`），`schema.d.ts` 的 sha1 三次都是 `518c14ac06ce313c3c6cfc7c83991f6599295285` ⇒ **再生成幂等，除本批有意改动外没有二阶漂移**。
- **同一棵树上量到的负载对照（#25 的假设第一次有可读的两组数）**：并发跑两个全量 → `452/457`、47m40s；机器上只剩一个全量 → `457/457`、15m44s。差异里的两条 `ToolChainEndpointTests` 红（`FailedToolDispatch_IsFedBackToWorker_AndNotReDispatchedOnLaterTicks` 等不到第二个 pending approval；`WorkerWriteFile_ThroughInProcessFakeProvider_DispatchLoopCompletesWithoutTinadecTools` 状态不等于 `completed`）隔离复跑分别 **1/1 绿（51s / 33s）**。§5d 那组实验因为自家循环吃掉进程位而没有结论，这组有：**"并行门禁放大这一族"从假设升级为对照量到的事实**，且两组的红/绿集合都能念出来。根因仍待收，#25 不降级。
- **本批新增的测试第一次跑就是红的，红在我身上**：`McpInventoryApiTests.ServerPayload` 的 `github` 那一行少写一个 `}`，三条用例停在 `JsonReaderException: ']' is invalid without a matching open`。上一轮上下文压缩把这条线记成"只剩门禁"，而三条后台通知全都报 `exit code 0`——**如果信通知，我会把 9/9 写进这份报告**。自警一条：门禁的裁决只存在于日志自己那行 `已通过!/失败!` 里。
- **一条错假设换到一个出货面缺陷**：我原以为 `default(JsonElement)` 会序列化成 `null`，它其实抛 `InvalidOperationException`。这条假设先让我的假 provider 用例变红，我去量真实 provider 才发现 `mcp_list include_schema=false` 与 **`mcp_search` 的默认参数**下，任何一次非空命中都会整调用失败并返回 `{"success":false,"error":"Operation is not valid due to the current state of the object."}`——`mcp_search` 是模型可调的工具，等于**模型搜工具只要搜到东西就永远拿到一句废话**。修在工具层（`McpToolSummary.InputSchema` → `JsonElement?`），红→绿用同一条探针复跑证明（同一 `call_id` 从 `success:false` 变 `success:true`，线上带 `"input_schema":null`）。
- **两次变异对照都做了，各自的红集都点名了改动**：
  - **工具层（provider 侧那 2 例新用例）**：把两个文件一起改回修复前的形状（`McpToolSummary.InputSchema` 回到不可为 null 的 `JsonElement` + `McpClientPool` 回到 `: default`）后跑 `--filter FullyQualifiedName~McpPassThroughTests` → **`MUT_EXIT=1`、恰好红 2 例**，红的就是本批新增的 `McpList_WithoutSchemas_StillReachesTheWire` 与 `McpSearch_WithholdingSchemas_StillReachesTheWire`，异常正是产品当年回给调用方的那一句 `System.InvalidOperationException : Operation is not valid due to the current state of the object.`；其余 6 例仍绿。改回来后同一 filter **8/8**。顺带量到一件结构事实：只改回 `McpModels.cs` 而不改 `McpClientPool.cs` 会 `error CS0037`（无法把 null 赋给 `JsonElement`）——**这个修复本来就是一处类型 + 一处赋值两个文件绑在一起的，单独回滚其中一半编译不过**。
  - **桌面（`MarketController.test.ts` 3 例）**：把 `mcpReadSucceeded` 改成恒 `true` → 恰红 1 例（"provider 没答上来时不得读成'什么都没配'"那一条），复原后 3/3；细节登记在 `apps/desktop/AGENTS.md` 的 Marketplace 行。
- **断言写法本身也被测过一次**：`input_schema` 为 null 最初用字符串匹配 `"input_schema":null`，但源生成上下文写的是**缩进** JSON，匹配永远不成立 ⇒ 改成把那一行**解析**出来再断 `JsonValueKind.Null`。这条改动的价值不在"多两例"，而在上面那组变异对照真能红到点名的两例。
- **顺手修掉一条测试基建缺陷（单独一次提交）**：`TinadecTools.Tests` 的 3 条**链接遍历防护**用例在未提权、未开开发者模式的 Windows 上必然红——`CreateSymbolicLink` 抛的是 `IOException`（`ERROR_PRIVILEGE_NOT_HELD`，本地化消息"客户端没有所需的特权"），而它们只 catch `UnauthorizedAccessException`（`GitReadToolsTests` 那条根本没 catch）。新增 `tests/TinadecTools.Tests/LinkPrerequisite.cs`，按 Win32 错误码判定而**不匹配本地化文本**。**代价说清楚**：这 3 条现在会静默 return，也就是这台机器上"链接不能穿出工作区"从未被真正验证——登记为 #40，需要的是一个能建符号链接的 CI job 或 junction 变体。284/284 这句因此只覆盖"跑起来的用例"。
- **删幻影这件事在两侧各钉一颗钉子**：网关侧删掉 `src/mcp/mcpRoutes.ts`（`connect`/`disconnect`/`status`/`tools/{toolName}/call`）与 `index.ts` 的 `reload` 代理——**它们代理的 Core 路由从来不存在**（`tools/{toolName}/call` 即使存在也是一条绕过审批门直达 MCP 工具的口子），Core 侧只有一条 `reload` 桩、连同两条读桩一起处理。钉子分两颗：Core 一例断言这五条路径在 Core 全 404，网关一例断言同样的路径 404 **且 `forwarded === 0`**——返回 404 但仍然打到 Core，不叫删掉。
- **契约计量（`git diff --numstat`）**：`openapi.core.json` `+167/−12`、`openapi.external.json` `+74/−163`（删的比加的多，正是删除面应有的形状）、`schema.d.ts` `+25/−100`。
- 两处编辑事故按老规矩记在这里：往本文件与 `TinadecCore/AGENTS.md` 追加段落时各吃掉过一次紧邻的下一节标题（`### 5b` 与 `## CHAIN CLOSURE`），都是读回工具输出的 diff 才发现——**没有任何门禁会抓 markdown 的标题被吞**，所以这类改动必须读回接缝。

### 5c. 2026-09-22 第三批：`model-invocations` 读路径修复 + run 用量面上线

- **Core 全量** `dotnet test TinadecCore/TinadecCore.slnx` → **`GATE_EXIT=0`**：Governance 44/44、Architecture 17/17、AgentFramework 329/329、**Api 448/448（21m32s）**。这是这条工作线上**第一次不带抖动红的 Core 全量**（上一批 440/441、再上一批 434/435，红都在 #25 家族）。但**这次绿不能算 #25 好转**：本批全程顺序跑，没有任何并行门禁抢 CPU（上一批我并行了，那一轮 Api 36m40s），"负载放大这一族"的假设因此既没被否证也没被证实——#25 的定向实验仍未做，保持 C 级。
- **修复有牙的 A/B（先红后绿，顺序就是证据）**：同一 filter `FullyQualifiedName~ModelInvocationQueryTests` 在**未修复**的构建上 `GATE_EXIT=1`、**2 红 / 5 绿**，两条红（`Page_FiltersByStartedAtWindow`、`Page_CursorReturnsEveryRowExactlyOnce_WhenTimestampsTie`）都停在 400，detail 就是 `The LINQ expression … could not be translated`；修复后同一命令 `GATE_EXIT=0`、**7/7**。
- **文档定稿后重跑两个读文档的快门禁**：`GOV_EXIT=0`（44/44）、`ARCH_EXIT=0`（17/17）。
- **Gateway** `bun test src` → `BUN_EXIT=0`、**58 pass / 0 fail**（上一批 56；本批 +2：query/cursor 逐字转发、`invalid_query` 不被改写成 `conflict`）。
- **Desktop**：`npx vitest run` `VITEST_EXIT=0`（**79 文件通过 / 1 跳过、733 例通过 / 14 跳过**，比上一批 +1 文件 / +14 例，正好是 `modelUsage.test.ts` 7 例 + `OrchestrationTab.test.ts` 新 7 例），`npm run typecheck` `TSC_EXIT=0`，`npm run build` `BUILD_EXIT=0`，electron 侧 `node --test` `ELECTRON_EXIT=0`（24/24，本批没动主进程代码，跑它是为了不拿"没跑"当"不会坏"）。`npm run check:drift` 在提交前 `DRIFT_EXIT=1`——它以 `git diff --exit-code -- src/generated/` 收尾，未提交的有意契约变更必然让它红，所以这条读法是"还没提交"而不是"漂了"；它的实质一半单独量过：连跑两次 `generate:client`，`schema.d.ts` 的 sha1 前后都是 `deb0ece…8533`，**再生成是幂等的**，也就是说除本批有意新增的那 9 行之外没有任何二阶漂移。
- **契约覆盖率的变动是量出来的**：同一脚本改前/改后各跑一次——Core 由 220 个 operation 里 40 个带响应体变成 **41 个（18.6%）**，网关由 229 个里 48 个变成 **49 个（21.4%）**。同一批第一次出现"用一条 Core 测试去读 OpenAPI 文档里的字段名"这种用法（而不是只把快照当文本比对）。

### 5d. 2026-09-22 第四批：#25 的负载定向实验（结论：一半成立，一半是我的仪器坏了）

同一棵已提交树（`e474197`）上做 A/B：先顺序跑一次全量当对照（§5c 第一条，`GATE_EXIT=0`、Api 448/448、21m32s），再在同一条命令里"Api 全量 + 桌面 `npx vitest run` 循环"制造负载。**读数如下，但两条的可信度不同**：

- **成立的一半**：负载下 00:07:32 出现一条真实失败，而且**不是 SQLite Error 5 那一族**——`CliRuntimeTests.CliProcessManager_ReusesReachableServerUrlWithoutSpawning` 抛 `InvalidOperationException: CLI runtime 'claude-cli' binary_path 'C:\missing\claude.exe' could not be started … 系统找不到指定的文件`。用例名就是它的断言（"**不 spawn**，复用可达 URL"），而 `CliProcessManager.SpawnAsync`（`DmaEA/CliRuntime/CliProcessManager.cs:131`，经 `EnsureRunningAsync:66`）被走到了 ⇒ **可达性探测在负载下判为"没在跑"，于是去 spawn 一个测试故意配的不存在二进制**。这是与 Error 5 同形状、不同子系统的第二族：**一个时序敏感的探测在 CPU 饥饿下翻到失败分支**。上一批只把它当"负载敏感标签"，这一批第一次拿到了翻在哪一行。
- **不成立的一半（诚实撤回）**：这一轮 `GATE_EXIT=4`，日志里**没有测试摘要**——但原因不能记在产品头上。同一个后台任务的输出末尾是 `bash: fork: Resource temporarily unavailable` 与 `dofork: child -1 - forked process … died unexpectedly, exit code 0xC000026B, errno 11`：**是我的负载循环（每轮 npx 会派生一批子进程）把机器的进程位耗尽**，中止至少部分是仪器自身失效。所以本批**不能**说"负载下 Api 必崩"，只能说"负载下出现了一条机制明确的探测翻转，且我这台机器无法在 40 轮 vitest 并发下干净地跑完一次全量"。这条正是"读数一致时先怀疑量具"的又一次兑现：先查仪器，再下结论。
- 附带量到的负载刻度：`LOAD_ITER` 时间戳显示桌面 vitest 单轮从空闲的 ~47 s 涨到 20:27→20:29 的 ~2 min 以上，随后循环停在 20:29 再无写入（进程位耗尽）。
- **对 §6 次序的影响**：#22（桌面 CI）**继续排在 #25 之后**，而且这一批给了它一条新的具体理由：CI runner 若与别的构建并发，第一次失败可能是探测翻转或 runner 自身没进程位，而不是产品回归——那种红的失败集合是**不可知**的（连摘要都没有）。#25 剩下的定向工作因此收窄成一件事：**给 `CliProcessManager` 的可达性探测补一个能区分"没在跑"与"我没等到"的路径**（探测超时应当是失败并带上超时值，而不是静默去 spawn），这条改完，这一族在负载下的表现才会变成可读的断言而不是随机分支。

### 5b. 2026-09-22 第二批：5xx 自报原因 + `SettingsPage` 抽出 `AgentPacksPanel`

- **Core 全量** `dotnet test TinadecCore/TinadecCore.slnx` → `GATE_EXIT=1`：Architecture 17/17、Governance 44/44、AgentFramework 329/329 全绿；Api **440/441**，唯一红 `ToolChainEndpointTests.AskMode_RunsOnNarrowedRoster_BrowserWorkerCompletesWithoutSupervisor`，**隔离单跑 1/1 绿** → 仍是 #25 家族。区别是这次红消息自己带出了原因（§4.5 第 5 条），所以"每轮换签名、单跑全绿"从观察升级成了有因可循的记录。
- **诊断改动自己的测试**：本地一条 filter 里 **7/7 绿**（journal 6 例 + `Interactions_StaleContextRevision_Returns409` 那条经真 host 的读回断言）。
- **耗时不作可比读数**：本轮 Api 36m40s（上一批 16m11s），因为我把桌面门禁与它并行跑，抢占了 CPU。**下一批要么顺序跑，要么不拿这个时间当基线**；更要紧的是：并行负载本身可能就是这一族红的放大器，这还没被验证。
- **Desktop**：`vitest run` EXIT=0（78 文件通过 / 1 跳过、**719 例通过 / 14 跳过** —— 与上一批逐项相同，正是纯搬运应有的形状），electron `node --test` 24/24，`npm run typecheck` EXIT=0，`npm run build` EXIT=0，`npm run check:drift` EXIT=0。
- **Gateway**：本批未改码，仍复跑 `bun test src` → **56 pass / 0 fail**。
- **文档面在门禁之后又被改写过**（AGENTS/本文件的机制那条来回纠正了两次），所以 Governance 44/44 与 Architecture 17/17 在文档定稿后**重跑过一次**，看下面这条：

### 5a. 2026-09-22 第一批：上下文包标价与被裁来源上线

- **Core 全量** `dotnet test TinadecCore/TinadecCore.slnx` → `GATE_EXIT=1`：Architecture 17/17、Governance 44/44、AgentFramework 329/329 全绿；Api **434/435**，唯一红是 `ToolChainEndpointTests.VibePack_Run_WalksDeclaredEdges_ParksOnApproval_AndCompletes`（轮询 `GET /api/v1/approvals` 得到 500 `internal_error`），**隔离单跑 1/1 绿**。上一批同一位置的红是 `AgentCandidatePromotion_PublishesImmutableVersion` 的 `SQLite Error 5`，当时也是隔离 1/1 绿——**每轮换一条签名、单跑全绿**，因此归入任务 #25 的负载族而非本批回归；本批新增 Core 用例本地 19/19 绿。
- **Desktop**：`npm run test` EXIT=0（78 文件通过 / 1 跳过、**719 例通过 / 14 跳过**），`npm run typecheck` EXIT=0，`npm run build`（vite）EXIT=0，`npm run check:drift` EXIT=0。
- **`check:drift` 这次绿得有意义，也有局限**：它是从网关快照 `TinadecGateway/tests/__snapshots__/openapi.external.json` 重新生成 `src/generated/schema.d.ts` 后比对——本批 Core 改了 `/api/v1/runs/{id}/orchestration` 的响应字段而门禁毫无反应，**正好实测出 §3 第一条**：该路由在契约里没有响应 schema，代理透传的数组元素是 `t.Unknown()`，所以新字段既不需要重新生成、也不会被任何契约面保护。
- **Gateway**：本批未改代码，但仍复跑了一次 `bun test src` → **56 pass / 0 fail**（不改代码不代表不必看绿灯）。网关对本批唯一相关的事实是：`context_packs` 整体透传、数组元素 `t.Unknown()`，因此新字段自动穿过而不需要登记。

## 6. 如果要继续，次序建议

先做 1（真实模型冒烟 + 一份人读的实测记录），再做 4（桌面 CI 门禁，把"绿色"从报告变成约束），然后 5（把三族抖动的根因收掉，否则 CI 一起来就会立刻变红）——这三条是**互相解锁**的：没有 4，1 的结论无法持续；没有 5，4 会长期红灯从而被关掉。2/3 属于产品能力面，需要模型侧配置决策；7 属于重构窗口，需要一次大范围改动配可验证回归证据。

**本批之后要改两处排序判断**（都是证据变了，不是偏好变了）：

1. 第 5 条（#25）**成本降了**：原因现在会自己出现在红例消息里，剩下的是两条候选修法各做一次的定向实验，而不是从零猜机制。同时多了一条**未被验证但很可能的放大器**——我自己把桌面门禁与 Core 门禁并行跑，那一轮 Api 从 16m 涨到 36m40s。所以做 4（CI）之前应先把"门禁必须顺序跑"定成规则，否则 CI 会把这一族放大成常态。
2. ~~新增第 8 条候选：**成本/用量面（任务 #32）比原先估计的更便宜也更值钱**~~ —— **本批已兑现**（§2 新行 + §3 两条限制）。留在这里是因为它兑现了一件比"多一个面板"更值钱的事：**给一条从不被测的路由写第一条测试，当场测出它的三个过滤器是死的**（§4 第 12 条）。这直接支持 §6 的原有次序判断——#30（给读路由补响应体 schema）不该被当作"文档工程"往后推：本批就是先补了一条类型、再补了一条读它的断言，两者一起才让字段名和状态码变得可证。下一步次序建议改为：**按"哪个面即将被用户读到"来挑 #30 的路由清单**，而不是按文件顺序扫；候选仍然是 §4 第 12 条里那条规则可能藏身的其它日期形状读路由。
