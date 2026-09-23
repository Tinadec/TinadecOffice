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
| 工作区技能渐进披露（索引上屏、正文留在磁盘）＋ 技能可以被市场装、可以被自己的文件关掉 | Core `Skills` + `Context` | `9870854`，跨层 #38（M3）补 `disabled:` 停用权威与路径组合规则：两个文件合计 **44 例**（`--filter ~WorkspaceSkill` `SKILL_EXIT=0`，静态核过 11 + 33 = 44；本批 +12＝停用键 11 例 ＋ 相对/绝对路径 1 例） |
| run 被告知了什么：来源清单、逐条 token 价格、被预算裁掉的条目及原价 | `context.packed` → orchestration 投影 → 桌面 | `c4b1ac9`/`c0402e8` + 本批（`WorkspaceInstructionApiTests` 新增 2 例含负对照；`FullDuplexEndpointTests` 按索引断言行数/同序/求和） |
| 快照逐文件评审与单文件撤销（带 `expected_sha256`） | Core 三端点 + `SnapshotsPage.vue` | `957d061`/`3558181`，`WorkspaceFileReviewApiTests.cs` 16 例 |
| 记忆评审面：候选→裁决→晋升→检索→撤销，可筛且带依据/适用条件/失效条件 | Core 读写口 + 网关 2 条代理 + `MemoryPage.vue` | `6b80210`/`ea013bd`/`4f2f0aa`，`MemoryReviewApiTests.cs` + 网关 `runtimeProxy.test.ts` |
| 晋升记忆真的进 prompt、未评审候选真的不进 | `MemoryStore.RetrieveAsync` | `FullDuplexEndpointTests.InvokeStream_SendsPromotedMemoryAndKeepsAnUnreviewedCandidateOutOfThePrompt` |
| `web_fetch`：受治理的网络出口（SSRF connect 期防护、逐跳复验、一份失败只说一件事） | `TinadecTools/Tools/Web/` + `HumanOnlyTools` | `7bceecb`，`WebFetchTests.cs` 51 例（含"绝不向被拒地址开 socket"的生产 handler 用例）；把守卫短路后恰好 3 例红 |
| 终端：本地 node-pty + 智能体终端（`shell` 工具的 Core 会话，事件日志回放 + 审计输入） | 桌面 `useTerminal`/`useTerminalSource` | 存在且被 `useTerminal.test.ts`/`TerminalCallBlock.test.ts` 覆盖（本批复核其架构注释与 `shell` 工具在实测清单中的事实） |
| 一次 run 花了多少：按 `model × provider` 分组的 token 与调用数，读不到就明说读不到 | Core `model-invocations` 分页 → 网关透传 → `OrchestrationTab.vue` + `lib/modelUsage.ts` | 本批。Core `ModelInvocationQueryTests.cs` 7 例——其中 2 例**先在未修复的构建上跑红**（`from`/`to` 窗口、并列时间戳的游标），另有 1 例钉住 OpenAPI 里 token 三字段并禁止 `prompt_tokens`/`completion_tokens` 回来；桌面 `modelUsage.test.ts` 7 例 + `OrchestrationTab.test.ts` 14 例（新 7），断"分组求和 == 每行 `total_tokens` 之和""缺字段渲染成'未报告'而不是 0""翻页被截断时必须自称是下限"。**注意这一行的 A 级只覆盖"读得到、算得对、说得出边界"**：没有任何单价，所以它不是成本面板（见 §4 第 11 条） |
| 一次 5xx 会自己留下原因（进程内有界环，不上外线；测试主机读回） | Core `AspNetCore/ServerFailureJournal.cs` | `ServerFailureJournalTests.cs` 6 例：同一次响应**同时**断 body 不含异常类型名 + journal 含最内层消息；4xx 不进环；裸宿主（未 `AddTinadecCoreHttp`）仍能回 500；环形淘汰；`Describe()` 把"给修的人看的那一行"格式钉死（含外层/最内层两段）。消费面：`ServerFailureReports.AssertStatusAsync` 是这句话唯一的构造点，9 个断言站（FullDuplex/ToolChain/Unattended 三个 stream helper 的 admission·run-stream·replay）经它读回；另有一条经真 Core host 的读回断言（`Interactions_StaleContextRevision_Returns409` 断 `LastFailure()` 报"没有 5xx"）。**并且它已在真实红例上兑现过一次**：本批整解门禁的一条 `POST /interactions` 500 第一次带出 `server cause: … SQLite Error 5: 'unable to delete/modify collation sequence due to active statements'`（详见 §4.5 第 5 条）。自 host 走 `CliRuntimeTestServers` 既有做法 |
| MCP 人类侧读面：清单带出处、连不上的服务器仍在场、逐服务器给 schema | Core `AspNetCore/Endpoints/McpEndpoints.cs` + 网关两条内联代理 + `MarketDetailCard.vue` | 三侧各钉一遍：`McpInventoryApiTests.cs` 10 例（承重那条是 `Tools_AreNeverReportedMissing_WhileTheProviderCannotAnswer`——provider 起不来时**必须** 200 + `source:"tool_provider_unavailable"`，不能 404 让人以为服务器被删了）、网关 `mcpProxy.test.ts` 5 例（含"外部契约里只剩这两条 MCP 路由"与"`mcp_server_not_found` 不被压成 `conflict`"）、桌面 `MarketController.test.ts` 3 例（变异 `mcpReadSucceeded → true` 恰红 1 例） |
| 技能市场源与技能安装：listing 只给名字，地址与字节由 Core 决定（预览取一次、审批后落盘、无卸载只有停用开关） | Core `Skills/SkillRepositorySource.cs` + `MarketFetch.cs`/`MarketListing.cs` + `MarketInstallService` 的 plan 分叉 | 本批。`MarketCatalogApiTests` 50 → **62 例**（新增 12 个方法 / 12 例，含 4 例"装了也不会播"的 Theory；定向跑 `TEST_EXIT=0`、62/62）。承重四条：`PreviewingASkillFetchesTheDocumentOnce_AndWritesWhereTheLoaderLooks` 断被拨的第二个 URL **恰是 Core 拼出来的** `…/catalog/pdf-forms/SKILL.md`、listing 自带的 `evil.example.com` 一次都没被拨、且 `mcp_list` 从未被调用（技能目标不来自 provider）；`ApplyingASkillProposalQueuesTheFrozenBytes_AndGoesBackToTheNetworkNever` 在 apply 前清空 URL 台账，断**零次出网**；`ASkillDocumentThatWouldNotAdvertiseItselfIsRefused` 四例（目录名不符 / 缺 description / 无 frontmatter / 自我 `disabled:`）都在任何写面之前 409；`RemovingASkillIsRefusedWithTheSwitchThatActuallyExists` 断拒绝语里同时有"没有 delete"、`disabled: true` 和那个文件路径。工具层前置改动（`write_file` 创建工作区内缺失父目录）有 3 条腿的独立回归，承重那条是"工作区外那个目录至今不存在"。技能路径组合另有 `WorkspaceSkillPolicyTests` 一条直测（相对/绝对两条拼法必须等于加载器会读的那一层）。**这一行只覆盖"提案与校验语义"**：见 §5h 缺口①——索引格式是本仓自定契约，从未对真实仓库拨过一次 |
| 一次被批准的安装在真子进程里真的落盘：写进 provider 自己报的那个文件，再被同一个 provider 读回来 | Core `Skills/MarketInstallService.cs` × 真 `TinadecTools.exe`（测试侧只伪造 `#fetch`） | 本批（#39 验收项③ / M4a）。两条 `[RequiresTinadecToolsFact]`：`AnApprovedServerInstallLandsInTheFileTheProviderItselfReads`（预览→apply→权限请求→审批→**磁盘上出现 `mcp_servers.json`**，其 `command/args` 带着固定版本 `@ac/live-server@2.3.4`，`id` 与提案的 `server_id` 同一个；再预览拿到 `replaces_command = "npx -y @ac/live-server@2.3.4"`——这个串只能来自一次真 `read_file`，`-y` 只在磁盘的 args 里；`expected_file_hash` 与测试**自己直连子进程**量到的 `file_hash` 逐字相等；卸载走同一条链后 `servers` 为空、台账行消失）、`AnApprovedSkillInstallCreatesTheFolderTheLoaderLooksIn`（`skills/` 事先不存在 → 批准后 `skills/pdf-forms/SKILL.md` 带 frontmatter 落盘）。变异对照：把工具层"创建工作区内缺失父目录"改回旧的抛错 → 技能那一例立刻红（`Could not find a part of the path …`），恢复后绿。**仍未验证**：`#fetch` 是本批唯一伪造的一环（工具的 SSRF 闸按设计拒绝 loopback，本地假服务器这条路被安全策略堵死），所以"对真 registry/真技能仓库拨一次"仍然没做过 |
| 市场页不再"装死"：二次进入必定重读、被批准的安装自己追到终态；kind 与源类型上标签；i18n 门真的扫到卡片 | 桌面 `MarketController.ts` + `MarketPage.vue` + `i18nParity.test.ts`（改用 `node:fs` 扫 `apps/TinadecUI/src/components/cards/**`） | 本批（#45 / M4b-1）。`MarketController.test.ts` **11 → 17** 例：二次 `start()` 必重读（旧代码被模块级 `started` latch 吞掉）、未落定台账每 12s 追读且落定即退役、隐藏标签页跳过这一拍、`sourceKindLabel` 不把 `mcp_registry` 原样上屏（只有本构建没见过的 kind 才落回原词）。**三组变异对照各红各的那条**：latch 装回 → 只红"二次进入"；从 `loadAll` 摘掉武装 → 只红两条"轮询确实在动"（第三条"已落定就不该有定时器"两边都绿，它是反向对照不是证据）；`sourceKindLabel` 换成 `return kind` → 只红源类型标签例（`expected 'mcp_registry' not to be 'mcp_registry'`）。门自身的牙齿用注入变异验过（卡片里塞裸中文 + 一个假键 → 两条各自红），跑完按字节还原并 diff 校对 |
| 市场安装：一条提案把"装一个 MCP 服务器"变成一次可审的写盘（冻结字节 + 摘要绑定 + 人工审批 + 版本固定） | Core `Skills/MarketInstallService.cs` + 网关四条纯代理 + `MarketDetailCard.vue` | 本批。Core `MarketCatalogApiTests` 里 15 个新增方法 / **21 例**（含 7 例"包名不能安全出现在命令行"的 Theory；类内总数 29→50，全解 486→507 与此同数。承重三条：`ApplyingAProposalQueuesOneGovernedWrite_AndWritesNothingItself` 断 apply 之后目标文件**仍不存在**且动作停在 `requires_approval`；`AProviderWhoseConfigLivesOutsideTheProjectIsRefused_NotWrittenSomewherePlausible` 断路径来自 provider 而非猜测；`AProposalWhoseFrozenBytesMovedIsRefused_NotAppliedOnTheReviewersBehalf` 篡改存储字节后断 412 + `write_file` 从未到达 provider）。变异对照：把 digest 守卫改成常量假 → **恰红 1 例**且红例名就是这一条，其余 49 例仍绿。网关 `marketProxy.test.ts` 9 例（含"外部契约里的 market 路径集合与 Core 实现的**恰好**一致"的 deep-equal，和"412 过期提案不被压成 conflict"）。桌面 `MarketController.test.ts` 8 例装/卸用例（含断 `decideApproval` **从未被调用**——页面不再替用户做决定）。**这一行的 A 级覆盖"提案语义与治理路径"**；"批准之后真的写出字节"那一半由上一行（真子进程端到端）在本批补上，仍未跑过的只剩**对真实 registry 外网的一次刷新**（CI 不出网），且固定的是包宿主的版本**名字**、不是内容摘要也没有签名校验（见 §4 与 `docs/security.md`） |
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

13. **市场还没接的源与没跑过的外网**（本条原写于 M1 之前，那时"写那一半全是占位"；#37 与 #38 已经把读、提案、审批、落盘两条 kind 都做完，所以这里改成登记**真正剩下的**）：
    ① `cli_runtime` 仍无 adapter（M4）——创建即 400 `unsupported_market_source_kind`，本机 CLI 目录要复用的 `model-providers/cli/discover` + `connect` 已存在但没接进市场；
    ② 两种源都**从未对真实外网跑过一次**：CI 不出网，官方 MCP Registry 与任何技能仓库的读取全部由进程内 fake provider 驱动；`mcp_registry` 的形状是照真实 `/v0/servers` 答案抄的，而 `skill_repository` 的索引格式 `{skills:[{name,description,version,url}]}` 是 **Core 自己定的契约**，没有公开生态在发布它，桌面选择器会把它显示成裸字符串 `skill_repository`——三者都必须写清楚而不是含糊过去；
    ③ 只支持**单文件技能**（只有 `SKILL.md`，没有 `references/` 之类随附资产），且布局必须是 `<索引目录>/<name>/SKILL.md`——布局不同的仓库本 adapter 读不到，这是"Core 只拨源自己那一个 origin"换来的代价；
    ④ 技能没有卸载面（工具层无 delete/rename），能用的开关是文档自己的 `disabled: true`；
    ⑤ 市场页面接线与游标分页属 M4：`src/pages/MarketPage.vue` 仍是一层 `UieCanvas` 壳，出货面在 `apps/TinadecUI/src/components/cards/market/*`。
    安装边界由用户拍板且未放宽：**可拉包，但必须审批 + 固定版本**，落盘只走 `UserToolAction` + 受治理 `write_file`（自带快照与逐文件撤销）。分阶段登记在任务 #36（只读耐久面）/#37（提案+审批安装）/#38（技能源）/#39（CLI 目录 + 页面重写）。
14. **MCP 服务器归属看不出来**（本轮新登记，挡住 #37 的一部分）：provider 读的 `mcp_servers.json` 里没有任何"这一条是哪次安装写的"信息，所以"这个扩展装了哪些服务器"无法回答。桌面此前按 `server.extension_id` 过滤，而该字段 Core 从不发 ⇒ 那个区块**在真机上永远不渲染**，只在预览画廊的假数据里"工作"过——本轮已把假数据改成真形状并删掉该区块。要恢复这个问法，得先给安装记录持久化归属。
15. **连不上的 MCP 服务器，给人看的是乱码**：清单里 `error` 字段带子进程 stderr 尾部，zh-CN 机器上非 ASCII 部分全成 `\ufffd`（实测：`'definitely-not-a-real-binary' \ufffd\ufffd\ufffd\ufffd…`）。解码发生在 ModelContextProtocol SDK 的 stderr 读取侧，不在本仓；结果是"为什么连不上"这句话对中文环境的使用者基本不可读。本轮只在界面如实显示原文，未伪造翻译。
16. **答案不是逐 token 流出来的，思维链根本不回传（本批按用户提问实测登记，任务 #44）**。分三层看：
    - 传输层是 A 级的：`GET /api/v1/runs/{id}/invoke-stream` 走持久 journal，`id: seq` ＋ `Last-Event-ID` 断线续传 ＋ 空闲 heartbeat（`TinadecCore/AspNetCore/Endpoints/DmaeaEndpoints.cs:40-88`），桌面 `lib/runStream.ts`/`sessionEventBus.ts` 消费。
    - 过程事件也是真的在流：run 期间持续追加 `ack`/`partial`/`steering`/`context_conflict`/`done`，活动面板与"思考过程"条靠它动。
    - **正文只有一条 delta**：全仓产出面向用户 `delta` 的地方只有一处——`DmaEA/FullDuplexRunEngine.cs:4006-4009`，值是收尾时整段 `meetingResponse`，而且幂等键固定 `…:meeting:delta:0`（结构上每 turn 就容许一条）。TinaChat 同样整条落库（`TinaChat/TinaChatService.Messages.cs:148`）。唯一带增量语义的是 ACP 外部子 agent 的 `message.delta`（`DmaEA/CliRuntime/AcpChatClient.cs:77`），那是壳层转发不是自家引擎。
    - 思维链是**反向处理**的：`DmaEA/ModelOutputText.cs:24-55` 主动把内联 `<think>`/`<thinking>` 从面向用户的文本里删掉（因为 M.E.AI 丢 `reasoning_content`，见同文件 :13），Core 只留 `reasoning_tokens` 计数（`Abstractions/Ports/IModelProvider.cs:62`）。全仓 `reasoning_effort`/thinking 强度开关 **0 处**——既不能调，也不能给人看。
    - 一个命名陷阱：`components/chat/ThinkingProcess.vue` 显示的不是思维链而是步骤列表（`ThinkingStep` 来自 `useAgentActivity`），标题"已思考 · N 步"是硬编码中文（`ThinkingProcess.vue:106`），两种语言包都没有它。
17. ~~`check:drift` 之外还有一道门覆盖不到卡片，缺键因此能出货~~ —— **本批（#45 / M4b-1）已修，而修的时候查出这道门比本条记的更空**。原判断成立：parity 门（`apps/desktop/src/locales/i18nParity.test.ts` 的 `panelSources`）用 `import.meta.glob`，走不出 `apps/desktop` 这个 root，而 `@tinadec/ui` 是 vite alias，所以 `apps/TinadecUI/src/components/cards/**`（出货的市场面就在那里）一条都没进门；后果正是本条记的那两处——`MarketDetailCard.vue:159` 引用两个包里都不存在的 `market.required`/`market.secret`。**修的是引用方向**：双语里一直躺着从没被引用过的 `envRequired`/`envSecret`，所以改卡片指向它们，而不是再补两个键把重复留在包里。本批另外量到三条，都比"少两个键"更值得记：① 那条"模板里不得有裸 CJK"的规则用 `indexOf('<template>')` 定位，而卡片一律写 `<template vapor>` ——**它对每一个带 vapor 编译标记的文件恒空转**，等于这条规则从没看过卡片；现改 `<template[^>]*>`，并把截取范围收在根 `</template>`（`<style>` 里的中文注释不再算证据）。② 卡片**不套**"至少引用一个 `t()` 键"的断言：15 张里 11 张是桌面面板的薄壳、自身零标签，套上必红（同 `ChatPanel.vue` 不登记的既有理由）——代价见 ③。③ 新门额外断言"扫描结果数 > 0"，否则一条走错路径的 `fs` 扫描会交出一份全绿。**仍开着**：11 张零引用卡片是否**全部**只是薄壳没逐张核对过，所以若它们自带英文字面量，本门看不见；任务 #10（可访问性与设计令牌契约扩面）未做；`apps/TinadecUI/**` 现在进门了但仍在桌面 CI 之外（§4 第 4 条）。

18. **"把 CLI 运行时目录接进市场"这一条按原设想是形状错误（M4 计划项①，本批实测推翻）**。原设计是：第三种 `kind=cli_runtime` 的源，复用已实现的 `model-providers/cli/discover` ＋ `connect`。实测三件事互相不容：
    - **它不是 adapter，是 Core 自己伸手**。`ControlPlaneService` 的发现面读的是 `KnownClis` 这张**四行固定表**（`:155-165`，只有 claude / codex / cursor-agent / opencode 四个可执行名），解析靠 `File.Exists` 逐目录试、Windows 分支再试 `.exe/.cmd/.bat`（`:243-261`）、目录来自 `%PATH%` 加五个用户级默认安装位置（`:302-323`），探活是 Core 自己 `Process.Start` 跑 `--version` 只看 3 秒内退出码是否为 0（`:267-300`）；构造函注入的是 `ICliProcessManager`，**没有 `IToolProvider`**。而 `docs/security.md:49-51` 给市场定的边界是"不出新网、读面只走 provider 预留的 `#fetch`"。把这条塞进市场＝给市场开一条 Core 本地读盘＋起进程的旁路，正是那句规则要挡的。
    - **它的"安装"不是一次 `write_file`**。`connect` 改的是三样东西：一个常驻子进程（`CliProcessManager.cs:61-68` 按 `driver|binary|args` 复用）、一个 loopback 监听端口（`:222-229`，还可能从 stdout 抓 token）、以及 provider 表的一行不可变版本记录（`ControlPlaneService.cs:345-362`）。安装面只认两种 plan（配置写 / 技能写），第三条只能被 `market_install_not_expressible` 点名拒绝。
    - **本机这一半已经在界面上有了**：`SettingsPage.vue:1066-1090` 就是"发现→逐个连接"的入口（按钮在 `:2346`），所以需求并没有落空，缺的是市场页与它之间的指引。
    据此 M4 该项改为**不减需求、换实现形状**：市场页把"本机运行时"作为**由既有路由驱动的非商品区块**呈现（不是 `extension_catalog_entries` 里的行，不参与安装/台账），并补上它真正的契约缺口——`/model-providers/cli/discover` 与 `connect` 在 `openapi.core.json:7082/7094` 只声明 `"200": OK` 无 content，桌面因此在 `api.ts:943-964` 手写了 `CliDiscoveryResultDto`（#21/#30 同族）。若将来真要"外部 CLI 目录"，它得是**一个可信 URL 发布的索引**走 `#fetch`，且行一律 `installable=false` ＋ `install_blocker` 说明"装一个 CLI 是包管理器的事、连接它改的不是工作区文件"——那是新功能，不是这条的替身。
19. 台账唯一键实测比任务 #43 记的更严一点：`IntegrationDbContext.cs:32` 是 `(tenant_id, project_id, server_id)` 唯一，`Kind` 有列但**不在键里**；`MarketInstallService.RecordApplyAsync:512-516` 命中同键时是**就地改写** `Kind/Version/CatalogId/ConfigPath/ManifestHash/InstallActionId`（`:550-558`），所以跨 kind 撞名不是"多一行"而是"把已装的那条换了身份"，而且同一个 else 分支还会把 `UninstallActionId` 置回 null（**一条正在等人批的卸载会被静默抹掉**）；同时那个 slug 也是配置文件里的 JSON 键（`:683/:691`）——台账撞与配置条目撞是同一件事。`ServerIdFor` 在 `:854-859` 有一句自我辩护（"两个源折到同一个 id 不会静默，提案会说出它替换的是哪一条"），**这句对配置文件成立、对台账不成立**：配置那一格确实被 `replaces_command` 摊开给人看，台账那一行却是被换身份（连 `Kind` 一起）外加待批卸载被抹。所以 #43 不能拿那条注释当"已处理"；兜底常量 `"market-server"` 更是任何被截到空的名字都会撞的同一个键。#43 必须在接第四种可安装 kind（或任何新命名规则）之前做掉。

20. **一次受治理的用户动作被批之后，没有任何事件面可以观察到它（本批实测，决定 M4b 的修法形状）**。`ControlPlaneService.cs:697-709` 的 `user_tool` 分支在 `_approvals.DecideAsync` ＋ `_userActions.ResumeAsync` 之后**直接 return**，全程没有 `AppendEventAsync`；仓库里唯一的 `approval.decided` 写点在 `:716-719`，而它被 `if (decision.RunId is { } runId)` 门住——**只有属于某次 run 的执行才会进事件 journal**。`Runtime/UserToolActionService.cs` 里 `AppendEvent` 出现 0 次（实测计数）。后果不只在市场页：任何显示"待批 / 已批 / 已落盘"的界面都拿不到推送，`GET /api/v1/events`（`api.ts:2891-2909`，租户/工作区作用域）对它永远沉默。因此桌面市场面板的正确修法是**先重新读一次**（`MarketController.ts:208` 的 `loadInstallations`），并且要先拆掉 `MarketController.ts:334-339` 那个模块级 `started` 短路——它让 `/market` 二次进入时连一次重读都不做（这才是"永远显示 Queued"的根因，比"没轮询"更前置）。轮询本身有两条现成先例可抄：`ChatroomPanel.vue:87-93`（5s，按 `active` ＋ `document.visibilityState` 双重门）与 `GovernanceBoardPage.vue:34/110-116`（12s 同门），且 `HomeController.ts:619-634` 已经确立"事件只当失效信号、真值仍走 HTTP 读"的本仓规矩。**给 user_tool 审批补一条耐久事件**是另一件更大且正当的跨层改动（涉及 journal 语义与 SSE 契约），登记为独立工组而不是塞进市场批次。

## 5. 阶段末门禁实测（按批次记账，全部读日志里自己写的 `GATE_EXIT`/`EXIT`，不看管道退出码）
### 5j. 2026-09-23 第十批：市场页不再装死——重读的所有权、审批落地的追读，和一道对 `<template vapor>` 空转的门（#45 / M4b-1，桌面-only）

定位：M4 的桌面半边。上一批（#39 验收项③ / M4a）证明"批准真的会落盘"，这一批证明"落盘之后界面会不会变脸"，并顺手把 §4 第 17 条那道门修到能扫卡片。

- **第一因不是"没轮询"，是模块级 `let started` 把重读吞了**（本批最有价值的一条定位）。`MarketController.start()` 短路一次，而它只被三张卡片的 `onMounted` 调用；实测本应用没有 `keep-alive`／`onActivated`，所以离开 `/market` 再进来必定重新挂载——latch 让第二次连一次读都不发。也就是说"批完永远显示 Queued"里有一半根本不是刷新问题，是**这个页面在一个应用生命周期里只读过一次**。修法是把所有权交给路由：`MarketPage.vue` `onMounted → start()`、`onUnmounted → stop()`，三张卡片不再各自触发读取（顺带消掉旧设计里"一次挂载读三遍"）。`src/debug/preview/RealMarketPage.vue` 挂的就是真 `MarketPage.vue`，所以 Debug Studio 预览面不需要第二份接线。
- **审批落地这件事没有任何信号可订，只能读**。`ControlPlaneService.cs:697-709` 的 `user_tool` 分支在 append 之前 return，全仓 `UserToolActionService` 的 `AppendEvent` 计数 0（§4 第 20 条），所以"订阅总线"这个最自然的修法被否——订阅等于订一片沉默。落地形状抄本仓既有的 `stores/userAction.ts`：定时器**由数据武装、由数据退役**（只有 `action_status` 非空且未到终态的行才算未落定；空值按 Core 的语义就是"没有动作在跑"，不武装），节奏 12s 取自 `GovernanceBoardPage.vue`，放弃年龄 10min 取自 userAction 的 `POLL_MAX_AGE_MS`，标签页隐藏时跳过这一拍（`ChatroomPanel.vue` 的双门）。后台那一拍**故意不弹错**：不是用户要的读，每 12s 一张错误卡比显示旧状态更糟；代价（失败不可见、靠下一拍重试、10min 后彻底停）写在函数注释里而不是藏进 catch。
- **两条 label 规则一起落地，并且"未知 kind 原样落回"是刻意的**：`sourceKindLabel`/`catalogKindLabel` 收在 `MarketController.ts`（单一 owner，筛选栏与行徽标共用），M3 留下的"源类型显示成裸 `skill_repository`"就此关掉；空徽标会被读成"这行没有类型"，而事实是"这个构建没翻译过它"，所以未识别的词落回原样。断言写成"不等于线格式、也不含点号键名"而不是等于某句译文——测试默认语言是 `zh-CN`，写死译文下次改措辞就自己烂掉。
- **门扩到卡片时查出这条门比自己记的更空**（详见 §4 第 17 条改写）：`import.meta.glob` 越不过 `@tinadec/ui` 的 alias，改用 `node:fs` 递归读卡片目录；而原来那条"模板不得有裸 CJK"用 `indexOf('<template>')` 定位，卡片一律写 `<template vapor>` ——**它从没看过任何一张卡片**。新门另加一条"扫描结果数 > 0"，因为走错路径的扫描只会交出全绿。
- **一次修掉两类死引用**：`market.required`/`market.secret`（卡片要、双语没有）与 `envRequired`/`envSecret`（双语有、没人读）是同一对拼写漂移，改引用方向即可，不新增键。`ThinkingProcess.vue` 的"已思考 · N 步"是全仓唯一一处裸中文出货面，一并迁到 `agent.thoughtSteps`；同文件 `toLocaleTimeString('zh-CN', …)` 去掉硬编码语言（英文界面下时间格式此前也跟着中文走）。
- **mock 遮蔽的两条真分支**：`mockApi.ts` 的 `refreshExtensionSource` 恒返 `outcome:'fetched'` → 面板里"刷新被拦但目录还站着"那条 throw 在预览中永远点不到；`listMarketCatalog` 忽略全部入参 → 搜索框与类型筛选在预览里"看着是通的"。前者改为按 fixture 行的 `last_error` 返 `blocked`+`reason`，后者按 `kind`/`source_id`/`q` 真过滤。
- 门禁实测（顺序跑，读日志自己写的退出码）：`npm test` **NPMTEST_EXIT=0**（vitest **80 文件通过 / 1 跳过**；electron `node --test` **pass 24 / fail 0**）；`npm run typecheck`（vue-tsc）**TYPECHECK_EXIT=0**；`npm run check:drift` **DRIFT_EXIT=0**——本增量不碰任何路由，`src/generated/schema.d.ts` 实测 0 行改动，因此网关 `bun test` 与 Core 四套未跑（无 Core/Gateway 改动，`git diff --numstat` 只剩桌面与文档）。
- 本批**没有产品代码之外的新能力面**：Core/Gateway/Tools 0 改动；三组变异对照与门自身的注入对照见 §2 新行的证据锚点列。
- 诚实边界：真 Electron 里"停在 `/market` 不动、去治理页批准 → 药丸在下一拍变脸"没有人肉走过，全部断言来自控制器层的假 api；**目录本身仍不自动重读**（只有台账重读；进页面与刷新按钮才读目录），所以"市场新行自己冒出来"不属本批；预览 fixture 里没有任何 `skill` 行，按 Skill 筛选在预览中只会得到空态——正确但没有正例；`mockApi` 那份过滤是渲染层本地实现，与 Core 的 SQL 语义不是同一份代码。


### 5i. 2026-09-23 第九批：把审批真的批下去，让真 provider 真的写（#39 验收项③ / M4a）

- **这一批没有新增产品能力，它新增的是一条回归**。M2/M3 把安装做成了"冻结提案 → 人工审批 → 受治理落盘"，而最后那一环**从来没有测试走完过**：`MarketCatalogApiTests` 里甚至有一条专门断言文件不存在（那是当时的正确断言）。所以"点了装就真的装上"这句话在此刻之前只有推理支持，没有回归支持。
- **测试侧只伪造 `#fetch`，其余全给真子进程**。共享假 provider 加一个 `Real` 后端：`mcp_list`/`read_file`/`write_file`/清单都转发给真的 `TinadecTools.exe`（沿用既有 `[RequiresTinadecToolsFact]` 约定，二进制不在时自己跳过而不是假装通过）。`#fetch` 留在假的一侧不是偷懒：工具的 SSRF 闸**按设计**拒绝 loopback 与私网地址（`TinadecTools/Tools/Web/WebFetchGuard.cs:130-185`），本地起一台假市场服务器这条路被安全策略堵死，绕开它等于为了测试削弱闸门。转发放在"每次调用必须带超时"那两条断言之前，否则受治理动作那次调用（本来就不带 Core 侧超时）会先把测试打死。
- **两条真进程用例覆盖三件事**：
  `AnApprovedServerInstallLandsInTheFileTheProviderItselfReads` —— 预览的 `target_path` 与子进程 `mcp_list` 报的路径同一条；批准后磁盘上出现 `mcp_servers.json`，条目 `id` 等于提案的 `server_id`、命令行带着固定版本；再预览得到 `replaces_command = "npx -y @ac/live-server@2.3.4"`，而 `-y` 只存在于磁盘的 args 数组里，所以这句话只能来自一次真 `read_file`；`expected_file_hash` 与测试**自己直连子进程**量到的 `file_hash` 逐字相等（哈希长什么样是工具的事，测试不复制它的算法）；卸载走同一条链之后 `servers` 为空、台账行消失。
  `AnApprovedSkillInstallCreatesTheFolderTheLoaderLooksIn` —— 先断 `skills/` 不存在，批准之后 `skills/pdf-forms/SKILL.md` 带着 frontmatter 出现在磁盘上，再预览得到"已经存在"的警告与真实 hash。这一例是 M3 那条工具层改动（`write_file` 创建工作区内缺失的父目录）第一次在真进程里被验收。
- **变异对照做了，而且红得有名有姓**：把 `Directory.CreateDirectory(parent)` 换回旧的 `throw DirectoryNotFoundException` → 技能那一例立刻红 `Could not find a part of the path …\workspace\skills\pdf-forms\SKILL.md`（`PROBE_EXIT=1`），恢复后定向 2/2 绿。这条实验证明两件事：测试不是自我实现的期望，且"一道审批换一个变更"确实依赖工具层那一半。
- **第一轮两条都红，但红的是测试的想当然，不是产品**：① 我按假 provider 的 `sha256:existing` 形状去断真 hash 的前缀（真 provider 的取值根本不是那个形状——这是"读被测试对象自己决定的值"时又抄了同表的旧毛病）；② `replaces_command` 我写成 `npx @ac/live-server@2.3.4`，漏了 `-y`，而漏的那一项恰好是"这行是从磁盘拼出来的"的证据。两条都按实测改断言，**产品代码一行未动**。
- **阶段末门禁实测（顺序跑，读日志自己写的退出码）——这一批的整解是红的，如实记**：`BUILD_EXIT=0`；`GATE_EXIT=1`，`Api.Tests` **532/533**（29m23s）红的一条是 `TinadecToolsProcessTests.CallAsync_ShellTool_ResultUsesSnakeCaseWireKeys`，消息 `timeout: Tool call timed out after 120s.`；`Governance` **44/44**、`Architecture` **17/17**、`AgentFramework` **329/329** 全绿。两条真进程用例**在全量里也是过的**（类内 62 → 64，`--no-build` 定向 2/2 `TEST_EXIT=0`）。
  同树两个对照实验给这条红定性：① 单跑该类 **6/6 通过**（28.7s，`ISOLATE_EXIT=0`）；② 把该类与本批新写的 64 条市场用例**放同一进程并发跑 70/70 通过**（2m30s，`PAIR_EXIT=0`）——所以"我加的两条子进程测试把邻居饿死"这条**被证伪**，红只在**全量**条件出现。已登记为 #25 的第三个症状族（前两个是 `SQLite Error 5` 与审批轮询 500 掩码；这次是**真子进程 120s 不回话**），不是本批的回归、也不是本批能修的。
  同时记一条本批确实贡献了负载的事实、不推卸：Api.Tests 全量耗时从上一批的 17m25s 涨到 29m23s，本批净增约 50 秒真子进程工作（两条各 20-25s）——但耗时涨幅远大于此，且本批两条定向跑都快，所以把整机负载归给本批是夸大；这台机器上是否有第二个进程共享这一段运行**未测量**（启动前查过 `dotnet`/`testhost` 为零，运行中没有复查）。
  未跑到的一律不写成通过：本批没有产品代码改动（工具层那次变异实验已还原、`git diff --numstat` 只剩测试与文档），所以**网关 / 桌面 / `check:drift` 三项本批未跑**，契约面因此是"按改动面推断为无变化"，不是"实测 0 行"。
- **§5h 缺口⑥ 的测量结果**：`installations.action_status` 是每次列表**现读** `user_tool_actions` 得来的（`MarketInstallService.CurrentActionStatusAsync`），所以"批准完了"这件事在 Core 这一侧不会腐烂；台账的 `state` 才是写一次不再动的字段，而它的词表只有 `installing|removing`，靠 `removing` + `completed` 时删行收尾。剩下的真缺口在桌面：市场面板不重读安装台账，而且比"没轮询"更前置——`MarketController.ts:334-339` 的模块级 `started` 短路让**二次进入 `/market` 连一次重读都不做**（路由里没有 `keep-alive`，实测全仓 `onActivated` 零出现），修它与把卡片纳入双语/裸中文门一起做（报告 §4 第 17、20 条，任务 #45）。
- 已知缺口（诚实）：① 真外网的一次刷新仍然没跑过（CI 不出网）；② 测试装的包名是 fixture，不会真的 `npx` 拉包，`mcp_list` 在装完之后不再被调用（所以本批证明的是"写与读同一个文件、同一套解析"，不是"那个包能起一个服务器"）；③ 真子进程会占住工作目录，临时根目录删除改成 5×500ms 重试（沿用 `ToolChainEndpointTests` 既有先例）——这是一次真实的资源生命周期，不是测试噪音；④ #43（台账 `(project, id)` 跨 kind 唯一性）与 CLI 运行时目录、游标分页、MarketPage 真数据仍在本任务剩余部分。


### 5h. 2026-09-23 第八批：市场第二种源——一份技能文档，地址由 Core 拼，字节只取一次（#38 / M3）

- **这一批的新风险不是"写盘"，是"写了盘之后模型会读它"**。M2 装的服务器在被启动之前是惰性的；`skills/<name>/SKILL.md` 一落地就进入上下文：名字与描述每轮都上屏，正文被模型自己打开。所以本批的测试重心从"路径与命令行能不能被污染"挪到"装进去的东西会不会真的被播发、以及人审的是不是就是落盘的那一份"。
- **两种源，一套刷新词汇**。`MarketListing`/`MarketEntry`（一次刷新的结果）与 `MarketFetch`（`#fetch` 传输）从 registry adapter 里抽出来共用，`MarketCatalogService.ReadAsync` 成为唯一按 `source.Kind` 选 adapter 的位置；`AdapterKinds` 从一种变两种，`supported_kinds` 由 Core 报出、桌面选择器照它渲染（不在客户端写死清单）。顺带改掉一处旧口径：没有 adapter 的 kind 以前只回一句不落库，现在同样写进源行 `last_error`——"这个源读不了"必须对下一个读者仍然可见。
- **listing 拿不到"拨哪儿"的权利**。索引只贡献 `name`/`description`/`version`/`homepage`；技能正文地址是 `<索引所在目录>/<name>/SKILL.md`，其中 `<name>` 必须先过 `WorkspaceSkillPolicy.ValidateName`，行才允许落库。于是市场每一次出网的 origin 仍然是注册源时人写下的那一个，路径段里不可能出现分隔符、`..` 或编码把戏；`url` 字段作为展示文本存下、永不拨号（回归直接断言被拨的两个 URL 里没有 `evil.example.com`）。
- **正文只在预览时取一次，装之前先按加载器的规则复检**。`PlanSkillInstallAsync`：`#fetch` → 大小闸（`MaxSkillBodyBytes` 就是 `MaxFileBytes`，太大就根本不会被冻结）→ `WorkspaceSkillPolicy.TryRead`（名字必须等于目录名、描述长度、不能已被自己的 `disabled:` 关掉）→ 目标绝对路径 + 现存字节 hash 一起冻进提案。apply 不再出网，回归把这一点钉成 `Provider.Urls` 在 apply 之后为空。四种"装了也不会播"的形状（目录名不符 / 缺描述 / 无 frontmatter / 自我停用）各一条 Theory。
- **替换要看着旧字节被替换**。目标已存在时提案带上它当前的 `expected_file_hash`，`warnings[]` 点名"这个文件已经在了"，写盘条件因此只能是"覆盖我刚看过的那一份"；读不到就退化成"只创建"，与 M2 同一条 fail-safe。
- **卸载对技能不存在**。工具层没有 delete/rename，能用的开关是文档自己的 `disabled: true`（本批前置改动把这条权威做进了 `WorkspaceSkillPolicy`，fail-closed：出现键即视为关，只有 `false/no/off/0` 算开）。`uninstall-preview` 对 `kind=skill` 一律 409，拒绝语同时说出文件路径与那个开关——把"做不到"和"做错了"分成两句话。
- **另一处前置改动在工具层**：`write_file` 现在创建工作区内的缺失父目录（不曾有技能的工作区也没有 `skills/`），工具清单摘要随之改变，桌面三处登记面（`toolPresentation.ts` 头注、其测试里的 `MEASURED_MANIFEST`、`apps/desktop/AGENTS.md`）同批改到实测值。工作区外、符号链接、父目录是文件这三条仍拒绝，回归断"工作区外那个目录至今仍然存在"。
- **`warnings[]` 第一行按 kind 分叉**：包给 `VersionPinNote`（版本名不是内容摘要），技能给 `ContentPinNote`（审的就是写的，源之后改了也不影响这一次）。把包的警告印在技能提案上是一句假话——技能的字节不会变。
- 已知缺口（诚实）：① 索引格式 `{skills:[{name,description,version,url}]}` 是 **Core 自己定的契约**，没有公开生态在发布它，也**从未对任何真实仓库拨过一次**（CI 不出网，全部由进程内 fake provider 驱动）；② 只支持**单文件技能**，带 `references/` 资产的目录装不了，且仓库布局必须是 `<索引目录>/<name>/SKILL.md`；③ 目录分页仍是 `offset`，技能源没有游标，M4 一并处理；④ 桌面没有"添加技能源"的入口级验证——`MarketPage.vue` 仍是一层 `UieCanvas` 壳，源类型选项渲染裸字符串 `skill_repository`；⑤ 台账身份是 `(project_id, server_id)`，跨 kind 同名会互相顶掉记录（今天两种源的命名规则不可能相等，已登记为任务 #43，M4 接第三种 kind 时做掉）；⑥ 审批通过到真正落盘之间市场侧仍不确认（治理层是唯一写者），"已安装"的可观察性仍取决于 `action_status` 的刷新时机。



- **阶段末门禁实测（全部顺序跑，逐个读日志里自己写的退出码）**：Core 整解 `GATE_EXIT=0`、`BUILD_EXIT=0`——
  `Api.Tests` **531/531**（17m25s）、`Governance` **44/44**、`Architecture` **17/17**、`AgentFramework` **329/329**；
  定向两条分别是 `MarketCatalogApiTests` **62/62**（`TEST_EXIT=0`，本批 50→62）与 `--filter ~WorkspaceSkill` **44/44**
  （`SKILL_EXIT=0`；两个文件静态相加正好 11 + 33 = 44，算式闭合）。
  `TinadecTools.Tests` `TOOLS_EXIT=0`、**293/293**（38s，父目录那条新回归在列）。
  Gateway `bun test src` `BUN_EXIT=0`、**72 pass / 0 fail**（11 文件；本批只把"无 adapter 的 kind"那条示例换成仍然无 adapter 的 `cli_runtime`，测试条数不变）。
  Desktop `npm test` `TEST_EXIT=0`（**80 文件通过 / 1 跳过，746 例通过 / 14 跳过** ＋ electron `pass 24 / fail 0`）、
  `npm run typecheck` `TSC_EXIT=0`、`npm run build` `BUILD_EXIT=0`、`npm run check:drift` `DRIFT_EXIT=0`。
  **本批三份契约产物 0 行改动**（`openapi.core.json`/`openapi.external.json`/`schema.d.ts` 逐条 `git diff --numstat` 为空）：
  技能安装走的是 M2 已有的 `install-preview`/`apply` 两条路由，多出来的只有 `kind` 的取值、`supported_kinds` 的第二项和几句新拒绝语——
  这正是"没有新契约面"的证据，而不是"契约忘了更新"。
- **§5g 的缺口②（"只有 `mcp-server` 一种 kind 可装"）与 §5f 的"只有一个 adapter"本批已兑现**，那两段作为批次账原文保留，以本条为准。



### 5g. 2026-09-23 第七批：市场第一次能"装"东西，装的却是一份提案（#37 / M2，含 #41 #42 前置守卫）

- **这一批的不变量是"没有任何一次市场调用能碰磁盘或起进程"**。可达的唯一变更是一条被审批门住的
  `write_file` 用户动作：Core 把预览算出的字节冻结入库（含 digest 与到期时间），apply 只把**已冻结的
  那一份**交给 `IUserToolActionService`，之后落不落盘由治理层决定。`ApplyingAProposal…_WritesNothingItself`
  直接断 apply 返回之后目标文件**仍不存在**、且动作停在 `requires_approval`。
- **两个门的顺序是量出来的，不是我推的**。一条全新的写请求先停在 `awaiting_user`（权限请求），
  人在权限那一步放行之后才出现带冻结字节的 `awaiting_approval` 信封——这条是我第一版的断言写错
  （断 `awaiting_approval` 结果查不到行）之后读 `UserToolActionService` 与 `ApprovalFlowTests` 才改对的，
  DTO 文档现在两个门都点名。
- **目标路径来自 provider，不来自猜测**：`mcp_list → config_path` 落在项目根之外就
  `market_install_target_unresolved` 拒绝——"写到一个像样的地方"会产生一条没有任何服务器会读的配置，
  而这条错误正是本面最容易藏起来的失败。配置读不到时退化成 **create，绝不 overwrite**
  （`file_hash` 缺省即"只在文件不存在时创建"），所以一个 Core 没读到的文件不会被一份基于猜测的提案抹掉。
- **提案字节不可后改**：apply 在读取状态、也在走"重复点击回放"分支**之前**先复核 digest，不匹配即
  412 `market_install_proposal_stale` 并把行写成 `stale`（重试仍然 refused，不会变成可应用）。
  变异对照：守卫改成常量假 → **恰红 1 例**，红例名 `AProposalWhoseFrozenBytesMovedIsRefused_NotAppliedOnTheReviewersBehalf`，
  其余 49 例仍绿；恢复后定向 50/50。这条测试直接改存储里的字节，因为**没有任何公开路由能构造出这个条件**——
  这本身就是"提案一经发出即不可变"的证据。
- **一次仪器错误，记下来**：为跑上面这个对照写的 `mute/restore` 双向脚本把两个方向的替换对写反了，
  于是"变异跑"其实又是一次原树跑（50/50 绿 = 无效读数），而末尾那步 `restore` 反过来把守卫**改成禁用状态
  留在了树里**。是下一步为别的目的写的 grep 撞见的。教训与 §5d 那条同源：**推翻/建立一条读数需要与立论
  同等强度的证据**；A/B 脚本必须把"文件此刻处于哪个条件"的探针写进日志并在不符时中止，不能指望
  `restore` 那步会发现反向。
- **#41 / #42 的前置守卫随本批落地**：被安装引用的目录行不再被刷新/删除销毁（`retained_rows` +
  `market_source_in_use`，出处得以留存）；registry 包字段拼写按 `IMarketCatalogService` 校正；
  `reason` 的外泄口径写进 `docs/security.md`（"操作为何停下"可以外发，上游进程吐出的原文不可以）。
  offset 分页竞态按计划留给 M4（#39），本批不换游标：没有消费者在翻页。
- **桌面这一侧主要是"删决定"**：控制器不再代替用户调用 `decideApproval`（测试断它**从未被调用**）；
  "当前工作区"收回 `homeController.selectedProjectId` 唯一 owner，删掉跨模块 `watch`（那正是
  `ChatPanel.test.ts` 被打断的原因），改成派生的 `activeProposal`；`tinadec://` 幻影源与 Core 从不发的
  字段区块一并删。
- **诚实缺口（这一批留下的，不是上一批的旧账）**：
  ① 真实 registry 的一次端到端安装**从未跑过**（CI 不出网，全部用例由进程内 fake provider 驱动）；
  ② 只有 `mcp-server` 一种 kind 可装，`skill_repository`/`cli_runtime` 仍 400（M3/M4）；
  ③ 固定的是包宿主给某版本的**名字**，不是内容摘要、不校验签名——这句已进提案 `warnings[]`，
  因为看不见"保证的边界"的审批不算知情审批；
  ④ env 描述只带变量名，值仍在 `ISecretStore`，所以"需要密钥的服务器"装完可能起不来——以警告告知，
  但**没有引导配置的界面**；
  ⑤ 审批通过到真正落盘之间市场侧不再确认（治理层是唯一写者，这是设计），代价是"已安装"的可观察性
  完全取决于 `GET /market/installations` 的 `action_status` 刷新时机，M4 接分页时一并处理；
  ⑥ 台账只在 `Removing` 且动作 `completed` 时把行摘掉，写盘**失败**的补偿面（重试/回滚）没有，
  失败只以状态呈现。

- **阶段末门禁实测（全部顺序跑，逐个读日志里自己写的退出码）**：Core 整解 `GATE_EXIT=0`——
  `Api.Tests` **507/507**（23m27s）、`Governance` **44/44**、`Architecture` **17/17**、
  `AgentFramework` **329/329**，其中 `CoreOpenApiSnapshotTests` 在快照再生**之后**的整解里转绿（第一次单独跑按该测试的既有设计必红 1 例：先抄基线→再刷文件→断漂移）；
  `TinadecTools.Tests` `TOOLS_EXIT=0`、**292/292**（1m45s，本批未改工具层，跑它是为了钉住"提案要写的目标仍在原位"）；
  Gateway `bun test src` `BUN_EXIT=0`、**72 pass / 0 fail**（11 文件）；
  Desktop `npx vitest run` `VITEST_EXIT=0`（**80 文件通过 / 1 跳过，746 例通过 / 14 跳过**）、
  `vue-tsc --noEmit` `TSC_EXIT=0`、`npx vite build` `BUILD_EXIT=0`、electron `node --test` `ELECTRON_EXIT=0` **24/24**、
  `generate:client` `GEN_EXIT=0`。
  三份契约同批再生并核对只含本批内容：`openapi.core.json` **+416/−0**（四条 install 路由 + 五个组件，逐条 grep 核过）、
  `openapi.external.json` +319/−1、`schema.d.ts` +128/0。
  `check:drift` 提交前 `exit=1`（该门以 `git diff --exit-code` 收尾，未提交的有意契约变更必然让它红），
  **提交后复跑 `DRIFT_EXIT=0`**——再生成的 `schema.d.ts` 与已提交字节逐字节相同，所以"三份契约自洽"这句
  现在是量出来的而不是推出来的。

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
3. **第 16、17 条是用户提问问出来的，不是我审计出来的**（本批实录，值得记进方法）：我此前九批的自查清单里从来没有"答案是不是逐 token 出来的""思维链去哪儿了"这两问，于是 §2/§3 把"流式"当既有事实写了好几遍（SSE、断线续传、heartbeat 都真），却没人指出**正文只有一条 delta**。次序据此调整：M4 剩余面（#39）先收口，#44 紧随其后，两者互不遮蔽——#44 的前置只有一条：delta 分片要落进持久 journal，写压力必须按时间片合并（§4 第 5 条那族 SQLite 抖动就是每事件一写的放大器）。同时 17 提醒一件更一般的事：**门禁扫不到的目录等于没有门禁**，`apps/TinadecUI/**` 今天既不在 i18n parity 里、也不在桌面 CI 里（§4 第 4 条），所以做 #22 时应把这两个面一起纳入，否则 CI 上线了、卡片照旧漏检。
4. **第 17 条已在本批关掉，它顺手改了一条 §6 原有的判断**（#45 / M4b-1）：`apps/TinadecUI/**` 现在确实在 i18n 门里了（`node:fs` 递归扫卡片目录），所以第 3 条里"CI 上线了、卡片照旧漏检"这半个担心只剩**CI** 那一半——做 #22 时不必再为卡片新写扫描器，只要把已有的 `i18nParity.test.ts` 跑起来即可。同批量到的一条更一般的事实值得留在次序里：**一条用 `indexOf('<template>')` 定位的规则，对全仓统一使用 `<template vapor>` 的目录是恒空转的**——它显示为绿、实际什么都没看，而这道门已经存在了很久。因此 #22 落地时给每条"门"配一次注入对照（本批用了三次：假键、裸中文、把 latch 装回），否则"门在"与"门有效"仍是两件事。M4 剩余次序不变：先 #43（台账跨 kind 撞名，任何新可安装 kind 之前），再游标分页（顺带收掉 `MarketCatalogService` 的 total/page 竞态），CLI 运行时按 §4 第 18 条改判后的形状做（非商品区块 + 把 `cli/discover`/`cli/connect` 补进契约），然后才轮到 #44。
