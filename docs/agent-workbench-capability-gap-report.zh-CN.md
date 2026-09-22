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
| 审批可信（冻结证据、脱敏后铸进事件） | Core `Governance` + 桌面审批面板 | `feat(core): freeze redacted approval evidence at mint` / `feat(app): render decision-grade evidence in the approval panel` |
| 会话内编辑并重发（消息级截断） | Core revert + 桌面 composer | `feat(core): let a conversation be cut at one of its own messages`、`dc08182` |
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

## 3. B 级：实现落地，但有一半边没人看

- **代理路由的响应体没有契约保护**。网关外部 OpenAPI 一变，`npm run check:drift` 会要求重新生成 `src/generated/schema.d.ts`（本批之前它确实抓到过三条快照路径）；但 `/api/v1/workspace-snapshots/*` 与 `/api/v1/runs/{id}/orchestration` 在快照里 `responses.200.content: never`——**响应字段漂移看不见**，桌面 `api.ts` 里这些类型是手写的。夹住它们的是两头：Core 测试 + 页面调用参数断言。这也是任务 #21 那类缺陷（DTO 声明 Core 从不发送的字段）能发生三次的结构性原因。
- **桌面端全部界面证据都在 happy-dom 里**。没有一次真 Electron 像素级复看：命令面板首屏密度、`Context Packs` 那行新加的裁掉句（明显比 chip 长，会不会拉爆行高未知）、快照评审卡塞进密集卡片后的观感。类型与测试全绿不等于这些面能用。
- **`web_fetch` 的礼貌性与缓存**：无 robots.txt、无 ETag/If-Modified-Since、无 HTTP 代理与自定义 CA（企业内网场景）；HTML→文本是手写扫描器，SPA 骨架可能取到空壳。工具描述里未承诺能渲染，因此不算撒谎，但算缺口。
- **上下文可视化只到"来源 + 价格 + 被裁"这一层**。装配之后的第二处裁剪（`Prompts/PromptsModuleRegistrar.cs` 的片段预算，结果写进 `PromptAssemblyResult.Warnings`）**至今没有读者**——模型最终真正读到的片段，与 `context.packed` 说的那份之间，还隔着一段无人观察的裁剪。

## 4. C 级：缺口，按"补它的价值 / 前置代价"排序

1. **真实模型从未进入任何测试**。全部端到端 run 用例由 `ScriptedChatClient` 驱动；`docs/core-agent-e2e-verification-2026-08-29.md` 那次"真实 HTTP 实测"用的也是**本地脚本化 OpenAI 端点**，且快照于 2026-08-29 的 `main`。因此"规划→工具→回馈→收敛"这条主链在真实模型下的行为（解析失败率、重试、token 计费、慢响应下的审批超时）**全部未验证**。前置：一条可显式跳过、有密钥才跑的 egress 冒烟用例 + 一个真跑一遍的记录，而不是把脚本化模型换掉当回归基线。
2. **语义检索没配**。记忆检索是"作用域过滤 + 关键词打分"，向量化路由未配置（`Memory`/`VectorStore`/`IProjectVectorDatabase` 有端口没有线路）。后果具体：**换了说法的同一条事实召不回来**，而"晋升记忆"的价值恰恰在于跨会话复述。同时 `applicability`/`expiry_condition` 只能给人看——`MemoryEntry` 只带 content，条件失效进不了模型。
3. **视觉/音频输入不做**。`CoreAttachmentReadTool.cs` 明写"本运行时不向任何 provider 发送 image 或 audio part"，非文本附件直接被拒。对标 Claude Desktop/Codex 的截图工作流，这是硬缺口，且它不在桌面端而在 provider 协议层。
4. **桌面端没有任何 CI 门禁**（任务 #22）。`npm run test`、`typecheck`、`vite build`、`check:drift` 全都要靠人（或我）在阶段末手跑；本批四个门禁就是这么跑的。任何"绿色"都是**报告**而非**约束**。动 CI 需要用户点头（本批未动）。
5. **Api.Tests 在高负载下有抖动族**（任务 #25）。本批阶段末全量：Architecture 17/17、Governance 44/44、AgentFramework 329/329、Api 433 例中 1 红——`FullDuplexEndpointTests.AgentCandidatePromotion_PublishesImmutableVersion` 报 `SQLite Error 5 'unable to delete/modify user-function due to active statements'`（读 SSE 流时），**单跑该例 1/1 绿**。同族另有 graph-collapse 与 run 卡在 reviewing 两种签名。真实成因（连接池上的 `Open()`/流读竞态）未收口。
6. **孤儿测试工程**：`tests/Tinadec.Contracts.Tests` 引用了不存在的 `../../src/TinadecCore/TinadecCore.csproj`，且**不在任何 `.slnx` 里**——它不红，因为没人跑它。删或修都要用户定（未动）。
7. **架构债**（任务 #9）：Home / Workbench 两页职责重叠、`SettingsPage.vue` 体量、SQLite 打开缺少统一重试、lane 编排这条产品定义要不要保留尚未决。都是能挡住下一次大改的债，不是装饰问题。
8. **撤销记忆的理由无处可存**：`MemoryItemRecord` 没有 reason 列，`RevokeAsync(itemId, reason)` 收下就丢。桌面因此刻意**不收集**撤销理由——收一个会被扔掉的答案等于教用户输入噪音。要留这条审计得加列（本仓 schema 走 `DbContextMigrationParticipant` 自研路径，不是 EF Migrations）。
9. **`web_search` 没做**。它需要外部搜索 API 密钥与配额治理，本仓没有这个配置面；宁可登记为缺口也不做假实现。
10. **候选列表没有分页游标**：`limit` 有上界 500，但没有 next-cursor，翻 500 条以上只能靠更窄的筛选。列表每行还要读一次内容 blob，所以筛选必须在进库前做完。
11. **几类工作台常见能力本仓完全没有**，此前散在各阶段里没有汇总：生命周期 hooks/自定义命令、编辑器级诊断（LSP）接线、行内 tab 补全、跨会话全局搜索、成本/配额面板（`ModelUsage` 已在 Core 侧记账并进 `DmaeaEndpoints`，但桌面没有一个"这一轮花了多少"的汇总面）。这一条我**没有逐条核对参考项目的实现细节**，只断言"本仓没有"，不借用别人的功能清单当自己的需求。

## 5. 阶段末门禁实测（本批，全部按 `GATE_EXIT` 记账，不看管道退出码）

- **Core 全量** `dotnet test TinadecCore/TinadecCore.slnx` → `GATE_EXIT=1`：Architecture 17/17、Governance 44/44、AgentFramework 329/329 全绿；Api **434/435**，唯一红是 `ToolChainEndpointTests.VibePack_Run_WalksDeclaredEdges_ParksOnApproval_AndCompletes`（轮询 `GET /api/v1/approvals` 得到 500 `internal_error`），**隔离单跑 1/1 绿**。上一批同一位置的红是 `AgentCandidatePromotion_PublishesImmutableVersion` 的 `SQLite Error 5`，当时也是隔离 1/1 绿——**每轮换一条签名、单跑全绿**，因此归入任务 #25 的负载族而非本批回归；本批新增 Core 用例本地 19/19 绿。
- **Desktop**：`npm run test` EXIT=0（78 文件通过 / 1 跳过、**719 例通过 / 14 跳过**），`npm run typecheck` EXIT=0，`npm run build`（vite）EXIT=0，`npm run check:drift` EXIT=0。
- **`check:drift` 这次绿得有意义，也有局限**：它是从网关快照 `TinadecGateway/tests/__snapshots__/openapi.external.json` 重新生成 `src/generated/schema.d.ts` 后比对——本批 Core 改了 `/api/v1/runs/{id}/orchestration` 的响应字段而门禁毫无反应，**正好实测出 §3 第一条**：该路由在契约里没有响应 schema，代理透传的数组元素是 `t.Unknown()`，所以新字段既不需要重新生成、也不会被任何契约面保护。
- **Gateway**：本批未改代码，但仍复跑了一次 `bun test src` → **56 pass / 0 fail**（不改代码不代表不必看绿灯）。网关对本批唯一相关的事实是：`context_packs` 整体透传、数组元素 `t.Unknown()`，因此新字段自动穿过而不需要登记。

## 6. 如果要继续，次序建议

先做 1（真实模型冒烟 + 一份人读的实测记录），再做 4（桌面 CI 门禁，把"绿色"从报告变成约束），然后 5（把三族抖动的根因收掉，否则 CI 一起来就会立刻变红）——这三条是**互相解锁**的：没有 4，1 的结论无法持续；没有 5，4 会长期红灯从而被关掉。2/3 属于产品能力面，需要模型侧配置决策；7 属于重构窗口，需要一次大范围改动配可验证回归证据。
