# TinadecOffice 智能体工作台 — 能力差距报告（2026-09-22）

范围：`Everything-changed` 分支上从阶段 0（`a673112`，锁定绿色基线并收口 TinaChat 唤醒/交接环）起的 45 个提交，加本批第 46 个。对标对象：codex、opencode、hermes-agent、gemini-cli、cline、harness-mix、Claude Desktop 的公开形态。

## 判据（三个等级，不可混用）

- **A 已验证**：有自动化测试穿过这条链（含端到端 run 断言），且阶段末门禁按 `GATE_EXIT` 记账为绿。
- **B 半验证**：实现落地、且**至少一侧**被断言，但存在一条明确"没人看的那半边"——响应体没有 schema、真机观感没复看、或只由脚本化模型驱动。
- **C 缺口 / 未验证**：代码里没有；或有实现但链断在一个从不被读取的位置。

**本报告里"已验证"永远不等于"能在真实模型上跑通"。** 见 §4 第 1 条——这是全篇最重的一条，其他缺口都能排在它后面。

## 1. 结论一句话

工作台侧的功能面（编辑重发、附件、指令文件、技能、记忆评审、上下文可见性、逐文件评审与撤销、命令面板、终端、受治理的网络出口、50 个 provider 工具）**已成体系且有自动化证据**；**没有**的是三件不同性质的事：真实模型从未被测过（C）、语义检索/视觉输入这类模型能力面（C）、以及交付工程本身没有 CI（C）。

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
| 一次 5xx 会自己留下原因（进程内有界环，不上外线；测试主机读回） | Core `AspNetCore/ServerFailureJournal.cs` | `ServerFailureJournalTests.cs` 6 例：同一次响应**同时**断 body 不含异常类型名 + journal 含最内层消息；4xx 不进环；裸宿主（未 `AddTinadecCoreHttp`）仍能回 500；环形淘汰；`Describe()` 把"给修的人看的那一行"格式钉死（含外层/最内层两段）。消费面：`ServerFailureReports.AssertStatusAsync` 是这句话唯一的构造点，9 个断言站（FullDuplex/ToolChain/Unattended 三个 stream helper 的 admission·run-stream·replay）经它读回；另有一条经真 Core host 的读回断言（`Interactions_StaleContextRevision_Returns409` 断 `LastFailure()` 报"没有 5xx"）。**并且它已在真实红例上兑现过一次**：本批整解门禁的一条 `POST /interactions` 500 第一次带出 `server cause: … SQLite Error 5: 'unable to delete/modify collation sequence due to active statements'`（详见 §4.5 第 5 条）。自 host 走 `CliRuntimeTestServers` 既有做法 |

## 3. B 级：实现落地，但有一半边没人看

- **状态码本身也是没契约的**。上一句还只说"响应体缺 schema"，实测更糟：Core 快照里 `POST /api/v1/sessions/{id}/interactions` 声明的是 **`200 OK`（且无 content）**，而 `InteractionsEndpoints.cs` 的四条出口全部 `Results.Created` 实际返回 **201**——224 个 2xx 响应声明里只有 **4 个**写了 201。也就是说契约不但没描述字段，还**写错了状态码**，任何按它生成的客户端（`generated/schema.d.ts`）拿到的是不存在的形状。桌面 `api.ts` 因此在两处各自手写这个 receipt（`api.ts:2559` 与 `api.ts:2183` 的内联声明），`turn_id` 只从 SSE 帧读、从不从 receipt 读——**三份互相不知道对方的存在**（任务 #23 收这条）。同理：receipt 有四种形状（admitted / `message_only` 无 `run_id` / steering / queued replay），一个"全字段可选"的 DTO 只会把"哪个键配哪个 `status`"这件事继续藏起来，所以要按 `status` 可辨识联合来写，而不是摊平。
- **大多数响应体根本没有契约**。实测两份 OpenAPI 快照：`TinadecCore/tests/__snapshots__/openapi.core.json` 220 个 operation **全部声明了 2xx 状态码，但只有 40 个（18%）带响应体 schema**；`TinadecGateway/tests/__snapshots__/openapi.external.json` 229 个里 48 个（21%）。也就是说 `npm run check:drift` 能护住的只有那一小截——`/api/v1/runs/{id}/orchestration`、`/api/v1/workspace-snapshots/*` 这类返回裸匿名对象的读路由在两份快照里都是 `content: none`，响应字段漂移完全看不见。**这是任务 #21 那类缺陷（桌面 DTO 声明 Core 从不发送的字段）能连着发生三次的结构性原因**，不是粗心：没有任何机器面会因为它写错字段而变红。当下夹住字段的是两头——Core 测试断铸造侧、页面测试断调用侧。补法是把读路由改成有类型的响应（现仓惯例是 `.Produces<T>(status)`：实测 65 处、集中在 4 个文件；`TypedResults`/`WithResponse<T>` 零使用），但那会连带改动 190 条路由的 OpenAPI 面貌，属于阶段 8 的重构窗口，不做零敲碎打（任务 #30）。补完还得和 `openapi.core.json`/`openapi.external.json` 两份快照、`schema.d.ts` 再生成、DTO、测试与中文产品定义**同一次改动**落地（`docs/architecture.md` 的 PUBLIC API VERSION RULE 要求同批），两条 `git diff --exit-code` 门禁会各自把关。
- **桌面端全部界面证据都在 happy-dom 里**。没有一次真 Electron 像素级复看：命令面板首屏密度、`Context Packs` 那行新加的裁掉句（明显比 chip 长，会不会拉爆行高未知）、快照评审卡塞进密集卡片后的观感。类型与测试全绿不等于这些面能用。
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
6. **孤儿测试工程**：`tests/Tinadec.Contracts.Tests` 引用了不存在的 `../../src/TinadecCore/TinadecCore.csproj`，且**不在任何 `.slnx` 里**——它不红，因为没人跑它。删或修都要用户定（未动）。
7. **架构债**（任务 #9，本批**修正了其中一条的因果**）：
   - ~~Home / Workbench 两页职责重叠，合并~~ —— **这条提法是错的**，两处证据：①`docs/app-core-ui.md` §3.1 规定两页各司其职（Home=meeting 入口/项目/会话/消息/队列，Workbench=run/task graph/worker/supervision/context），L644-645 还是两条独立的验收项；②真正重复的是"同一个 orchestration 快照有两套类型两条读路径"，而 `pages/WorkbenchPage.vue:130` 那句 `as unknown as never` 不是偷懒：生成的 `OrchestrationSnapshot` 把 `nodes`/`context_packs`/`flows`/`step_results`/`agent_instances` 全声明成 **`unknown[]`**（实测 `src/generated/schema.d.ts`），而 `src/api.ts:1941` 手写的才是完整类型。**结论：这条债被 §3 第一条（任务 #30 响应体 schema）挡住**——现在删 cast 只会把 cast 挪进组件内部。顺序改为先 #30 再回来。Workbench 的 lineage + `RunLaneCanvas` 也没有 Home 等价物，合并会真丢功能。
   - `SettingsPage.vue` 的**第一层已经搬完（2026-09-22）**：agent-pack 生命周期（`checking|install|upgrade|deferred|conflict|error` + reload/toggle/adopt/uninstall）整体搬到 `src/settings/sections/AgentPacksPanel.vue`，页面从 **3731 → 3502 行**（−229）。回归网用的是仓库现成的三条，而不是新发明的断言：`SettingsPage.agentPack.test.ts` 4 例（克隆流、清单三态、卸载必须先确认、清单路由失败退化成空表）改动前后都绿——**"搬动没改变用户可见表面"这件事是它们证的，不是我说的**；`SettingsPage.smoke.test.ts` 的 import/usage 契约顺手扩到 4 个 Agent Center 面板，并把匹配从 `<Name />` 放宽成 `<Name`（带 props 的面板永远不会自闭合）；`i18nParity.test.ts` 登记新面板的 `t()` 引用。**仍留着**：Agent Center 五个子标签的本体、窗口与材质 chrome，以及 `clone`/`uninstalled` 之外那类需要页面状态的动作。
   - SQLite 打开路径没有任何重试或 `busy_timeout`（实测全仓零命中）。§4.5 第 5 条拿到现场原因后这条重新变成一个**候选修法**（Error 5 出在池化 `Open()` 重注册时，重试确实会再走一次注册），但仍未验证，也不能替代连接生命周期那条。lane 编排这条产品定义要不要保留，需要用户决策，未动。
8. **撤销记忆的理由无处可存**：`MemoryItemRecord` 没有 reason 列，`RevokeAsync(itemId, reason)` 收下就丢。桌面因此刻意**不收集**撤销理由——收一个会被扔掉的答案等于教用户输入噪音。要留这条审计得加列（本仓 schema 走 `DbContextMigrationParticipant` 自研路径，不是 EF Migrations）。
9. **`web_search` 没做**。它需要外部搜索 API 密钥与配额治理，本仓没有这个配置面；宁可登记为缺口也不做假实现。
10. **候选列表没有分页游标**：`limit` 有上界 500，但没有 next-cursor，翻 500 条以上只能靠更窄的筛选。列表每行还要读一次内容 blob，所以筛选必须在进库前做完。
11. **几类工作台常见能力本仓完全没有**，此前散在各阶段里没有汇总：生命周期 hooks/自定义命令、编辑器级诊断（LSP）接线、行内 tab 补全、跨会话全局搜索、成本/配额面板（`ModelUsage` 已在 Core 侧记账并进 `DmaeaEndpoints`，但桌面没有一个"这一轮花了多少"的汇总面）。这一条我**没有逐条核对参考项目的实现细节**，只断言"本仓没有"，不借用别人的功能清单当自己的需求。

## 5. 阶段末门禁实测（按批次记账，全部读日志里自己写的 `GATE_EXIT`/`EXIT`，不看管道退出码）

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
2. 新增第 8 条候选：**成本/用量面（任务 #32）比原先估计的更便宜也更值钱**——Core 已记 `model-invocations` 且带分页游标、也已在网关外部契约里，桌面**零消费者**。它不需要新依赖、不需要外部密钥，是这批之后最容易兑现的"对标 Codex/Claude Desktop"能力；唯一要克制的是**没有单价配置面，所以不许把它做成"金额"面板**。
