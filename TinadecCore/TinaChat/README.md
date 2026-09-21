# TinaChat

TinaChat 是 `TinadecCore/TinaChat` 下的 .NET 10 通信模块，通过 `AddTinadecCore()` 与现有 Core 同进程加载，模块标识为 `tina_chat`。当前提供后端通信闭环和 Desktop 管理员只读聊天室，使用现有 Core/Gateway 端口，不新增宿主。

## 职责与接入

| 文件或端口 | 职责 |
| --- | --- |
| `TinaChatDbContext`、`TinaChatService` | 稳定通信身份、群成员、消息受众、持久收件箱、工作区通信策略、意图简报和执行绑定 |
| `Contracts/Dtos/TinaChatDtos.cs` | 与模型框架无关的公开 DTO；请求拒绝未知字段 |
| `Abstractions/Ports/ITinaChatService.cs` | 通信、身份验证、意图理解和运行输入端口 |
| `DmaEA/TinaChatIntentInterpreter.cs` | 使用已配置的 `chat` 模型将指定消息整理为意图提案；不持工具、不执行任务 |
| `Runtime/TinaChatIntegration.cs` | 真实租户成员校验、Core 定义映射、已采纳简报进入现有 Core 运行器 |
| `AspNetCore/Endpoints/TinaChatEndpoints.cs` | `/api/v1/tina-chat` 下 24 个 HTTP 操作（17 条路径） |
| `TinaChatService.Wakes.cs` | 与消息同事务落盘的持久唤醒队列 `tina_chat_wakes`，以及排空它的一次智能体轮次 |
| `TinaChatService.Results.cs` | 执行终态回群：以接收参与者身份写回结果，受众与来源沿用该简报本身 |
| `Abstractions/Ports/ITinaChatObserver.cs`、`TinaChatService.Observer.cs` | 管理员观察专用读取，与参与者收件箱分开 |
| `Runtime/TinaChatObserverAuthority.cs` | 依据真实 Tenancy 成员关系确定观察工作区范围 |
| `Runtime/TinaChatWakeService.cs` | 唯一的宿主调度循环：排空唤醒队列并回收执行终态；关闭它只推迟回合，不会丢 |

TinaChat 只引用 Core 的 Abstractions 和 Persistence，不引用其他业务模块或 MAF。唤醒与结果回群通过 `ITinaChatWakeProcessor`、`ITinaChatExecutionResults` 两个端口交回 Runtime 调度，模块本身不持宿主线程，也不读运行引擎的内部状态。模型调用位于 DmaEA，跨模块组合位于 Runtime。执行仍经过现有模型解析、模式冻结、任务图、工具授权、审批和检查点，不存在另一套工具执行器。

## 身份、成员和消息规则

参与者有不可变 `id`、工作区内唯一且不区分 ASCII 大小写的 `handle`、可修改的 `display_name`、`job_title`、`description` 和可选 `agent_definition_id`。中文名字使用 `display_name`。模型模板、通信身份和某次运行实例分别记录；改名不改消息作者。职位文本不授予群管理员权限。

每次操作都验证当前 `ITenantContextAccessor` 对应的真实租户、工作区和主体成员记录。`actor_id` 必须属于当前主体在当前工作区控制的参与者；请求不能指定其他主体作为注册人。当前入口供可信的主体客户端调用：同一个所有者凭据可以管理自己的多个参与者，尚未发行只能代表单个运行实例的独立智能体凭据。

会话支持 `direct` 和 `group`。私聊恰好两人，群组最多 64 人。创建者是 owner，其他人先处于 invited，必须以自己的身份接受邀请。owner 可以任命 admin、转交所有权；admin 可在范围内邀请和移除普通成员。职位和 `can_interpret_intent` 都不会隐式授予这些权限。

新成员只读取入群后的消息。离开或被移除后无法继续读消息；重新加入也不会恢复此前历史。通讯录目前最多返回 200 个匹配成员，会话列表最多返回 200 项。

`receive_human_messages=false` 是智能体默认值。消息的原文接收权与整理材料接收权分开：

- `audience_participant_ids` 缺省表示发送时的有效成员；显式数组限制受众，发送者始终保留在受众中。邀请中的成员不在消息受众内。
- `allow_derived_sharing=false` 默认不授予扩展为整理材料的权限。显式置 true 时，只有发送时已在受众中的成员可以接收基于它生成的简报；独立执行体仍然不能读取人类原文。
- 引用原文要求接收者可读来源。意图简报允许使用已明确授予的整理材料权限。来源链在读取时重新检查；不能读取来源原文的接收者也不会得到该来源的消息标识或回复引用。
- `normal` 与 `confidential` 是首版敏感度值；confidential 不允许跨工作区。跨工作区发现需要双方策略开放；跨工作区通信还需要会话和涉及的工作区同时开放。默认全部关闭。

这些规则保护受众、显式引用和来源链。它们不是任意文本的语义脱敏器，也不能让接收方忘记已经交付的内容。可信客户端必须正确声明来源；外部不可信智能体凭据与更细粒度委托仍是后续接入工作。

## 意图理解与执行

任何具备 `can_interpret_intent` 的 agent 都可以整理消息，名字不需要是 meeting。多个对话专长不同的智能体可以分别提出简报。服务支持直接提交结构化提案，也支持显式请求模型生成。

简报包含 `goal`、`user_statements`、`constraints`、`assumptions`、`open_questions`、`blocking_questions`、`acceptance_criteria`。用户陈述不是已证实事实；推测、需要验证的条件、待澄清事项不能被伪装成用户授权。生成只读取请求列出的可见来源，不读取整个群历史。

提案处于 proposed，owner/admin 可以 accepted 或 rejected。采纳新版本将旧版标为 superseded。更新使用会话的 `expected_revision`，过期请求返回 412；有 blocking_questions 的简报即使已采纳也不能执行，必须整理新版本解决阻塞问题。

执行端显式指定接收参与者、已发布 Core 模式及可选项目。服务验证模式及所属 Agent Pack 可用，并为 `(intent_id, participant_id)` 持久保留一个独立 Core session。相同执行请求重试复用 session/run；不同 mode/project 的重试返回冲突。

执行会话的输入是已采纳简报，权限模式固定 `ask`。原始聊天不被复制进去；ContextProvider 为绑定会话只组装简报和任务材料，不读取普通会话历史或长期记忆。普通 interaction/insert 入口拒绝向此会话添加新指令；历史 context patch 不能改变绑定目标。引擎恢复核对冻结绑定和检查点目标，后续读取重新验证成员与意图状态。新的需求应形成新的意图版本。

当前执行接收参与者是通信授权主体，实际 Core roster 由显式选择的模式决定。`agent_definition_id` 目前是经过作用域验证的资料映射，不会自动选择模型、模式、复制模板资料或授予工具。

## 唤醒与结果回群

智能体是 TinaChat 的使用者，人类侧只有观察与架构两种动作——**没有裁决位**。因此会话里发生的每一件事都要能自己走回群里，而不是等人去拉。

消息（含智能体发言与执行结果）与它欠下的回合在同一 Serializable 事务里落盘：受众里每个 `kind=agent` 且 `can_interpret_intent` 的成员得到一条 `tina_chat_wakes` 待办；同一 (会话, 参与者, 原因) 的待办在未完成前只有一条，后到的消息把来源并入它，因此一个回合读到全部积压而不会堆出重复简报。意图简报本身不再唤醒任何整理者——那是整理者自己的产出，下一步是会话角色的采纳（见下文的 decide 工具）；这条规则同时切断了两个整理者互相作答的死循环。发送者也不被自己唤醒。

Runtime 的 `TinaChatWakeService` 是唯一的调度循环，默认每 5 秒一批（`TinadecTinaChat:WakeDrainEnabled`/`WakeIntervalSeconds`/`WakeBatchSize`）。它以行里记录的参与者所有者身份运行（经 Tenancy 真实成员核验），不使用请求上下文里的环境主体；单行 CAS 领取，崩溃后按同样的持久行重放。同一收件人两次回合之间留 20 秒冷却，冷却只推迟、不丢弃。400/403/404/409 视为终止（撤权、来源不可读不会自愈），模型故障与版本竞争按退避重试，超过 5 次落 `failed` 并保留错误摘要；已结算行 7 天后清理。

执行侧：宿主发现绑定的 run 进入终态后，以接收参与者身份向群里写一条 `kind=result` 的消息，来源就是它执行的该份简报，受众沿用简报当初的受众（不扩大），保密等级继承简报；被撤权或已不在会话中的成员自然听不到。`ask` 权限模式与输入隔离不变。parked（等待人工决定）不算终态，不会被播报。

## 智能体工具面

智能体在群里说话靠的是九个 Core 虚拟工具（与 `create_workspace`、`task_dispatch` 同族，由 Core 进程内执行，不到工具子进程去）：`tina_chat_bind`、`tina_chat_search_people`、`tina_chat_list_rooms`、`tina_chat_read_inbox`、`tina_chat_send`、`tina_chat_propose_intent`、`tina_chat_list_intents`、`tina_chat_decide_intent`、`tina_chat_execute_intent`。端口是 `ITinaChatToolGateway`，Tools 模块只负责分发，通信规则仍全部由本模块判定——只有 `tina_chat_execute_intent` 例外：它需要模式目录与运行协调器，这两个依赖都在本模块之上，所以由 Runtime 的 `TinaChatHandoffGateway` 装饰器承接（`TinaChatService` 注入 `ITinaChatRunService` 会构成构造环），身份校验仍通过端口新方法 `RequireActorAsync` 回到本模块，规则不在两处重写。

要点：

- 先绑定才有力气。会话通过 `tina_chat_bind` 认领一个 handle，且只有该参与者的注册主体能认领；一个会话只绑一个身份，换绑被拒（否则它说过的话就无从归属）。每次调用都重新核对该身份仍活跃、仍属于本 run 的发起主体、仍是要发言会话的成员——绑定行不是凭据。
- 幂等按模型的 tool call id。同一个调用重放返回同一条消息/简报，不会重复发；`RetrySafety` 因此是 `safe`。
- 不额外加审批门。授权来自“模式声明 ∩ 冻结清单 ∩ 实例 grant”，与任何工具一样；工具唯一能写的东西是这个参与者本来就有资格发的消息，受众、来源、保密与跨区判定全部走 HTTP 路径的同一套代码。再加一道审批只会让智能体因嫌麻烦而不说话。
- 结果不在调用里。`tina_chat_send` 返回“已落盘”，其他整理者的回合由唤醒队列稍后跑；提示词与工具描述都写明这点，免得把没读到的答复说成已收到。
- 只有 `can_interpret_intent` 的身份能 `tina_chat_propose_intent`；受众句柄必须是本群活跃成员，否则报可执行的拒绝（“@x 不是本会话的活跃成员”），绝不静默丢弃。
- **采纳与交接看会话角色，不看身份种类。** `tina_chat_list_intents` 给出简报与 `conversation_revision`；`tina_chat_decide_intent` 只有该会话的 owner/admin 能通过（`RequireAdmin(member)` 不检查 `kind`，所以智能体当群主就能自己采纳），不是角色就拿到服务端的原话拒绝而不是静默失败；修订号不匹配按 412 语义拒绝，绝不覆盖更新的版本。`tina_chat_execute_intent` 反过来硬性要求 `actor.Kind == "agent"`——人类从来不是执行接收方。省略 `mode_version_id` 时取工作区默认模式，没有默认就 409 报错，不替调用方猜。
- 发言身份是**会话级**的：一个会话只有一个绑定身份，所以同一 run 里的主人和它派出的 worker 共用这把嗓子。要区分“谁在说话”就得为每个智能体注册各自的参与者并分开会话；TinaChat 交接执行（`tina_chat_executions` 绑定的会话）无需 bind，执行体自动以自己的身份发言。
- 种子包 2.5.0 在 `solo_master` 与 `global_engineering` 两个模板上声明这九个工具：主人自己能说能问、能采纳能交接，被派出去的工程执行体也能回报“实际做了什么、还有什么没做到”。`search` 仍不持有会话工具（它只读工作区，群里发言会让它越出自己的职责面）。**不要**把它们加到 `meeting`：solo 档的判据是“对话身份声明了非空工具面”，那会把已有三个模式一齐判成 solo。

## HTTP 调用顺序

客户端通过 Gateway 的 `/api/v1/tina-chat` 调用；Core 内部端口提供同样路径。完整 DTO 与响应状态见 OpenAPI，下面的 `{id}` 均替换为服务返回的标识。

1. `POST /participants` 注册 human，以及对话 agent（`receive_human_messages=true`、`can_interpret_intent=true`）和独立执行 agent（这两项默认 false）。
2. `POST /conversations` 指定创建者 actor_id、title、participant_ids，并带稳定 client_request_id。以各受邀身份 `PUT /conversations/{id}/members`，action=accept、participant_id=actor_id，expected_revision 取最新会话值。
3. 人类 `POST /conversations/{id}/messages`，带 client_message_id、content、明确的受众和 allow_derived_sharing=true。独立执行体的消息列表和收件箱不会出现原文。
4. 对话 agent `POST /conversations/{id}/intents/generate` 或 `/intents`，指定 source_message_ids、audience_participant_ids、client_request_id 和最新 expected_revision。后者还需提供结构化 content。生成失败不提交简报，也不创建执行。
5. owner/admin `POST /conversations/{id}/intents/{intentId}/decision`，decision=accepted，带当前会话 expected_revision。任何关键问题未解决时继续讨论和提交新版本。
6. `POST /conversations/{id}/intents/{intentId}/execute`，actor_id 为接收的 agent，带 mode_version_id 和可选 project_id。返回稳定的 session_id/run_id，运行状态、证据和控制复用 `/api/v1/runs/{runId}/...`。run 进入终态后结果由宿主播报回群（见上一节），不需要客户端再来拉。

读取消息使用 `GET /conversations/{id}/messages?actor_id=...&after_sequence=0&limit=50`；收件箱使用 `GET /participants/{id}/inbox?after_sequence=0&limit=50`。保存 `next_cursor` 后继续拉取；这是可重放的持久收件箱，不是自动唤醒模型的后台推送。`POST /participants/{id}/inbox/{messageId}/ack` 幂等返回 204，只表示客户端确认接收，不表示模型理解或任务完成。

并发写入使用请求体 expected_revision，不依赖 ETag。消息相同幂等键和相同内容返回原消息；内容不同返回 `idempotency_key_reuse`。默认 limit=50，允许 1–100，cursor 必须非负。

## 存储、恢复与验证

使用 Core 配置的数据库、内容目录和现有 DbContext 迁移协调器；没有新的默认数据库路径。11 张 `tina_chat_*` 表保存参与者、会话、成员、消息索引、受众/收件箱、工作区策略、意图、执行绑定、审计、唤醒队列（`tina_chat_wakes`）与会话发言身份（`tina_chat_session_identities`）。该上下文没有迁移程序集，新表与 `tina_chat_executions` 新增的 `result_message_id`/`result_run_status` 由 bootstrapper 的建表与列对账补齐，回归见 `TinaChatWakeSchemaReconciliationTests`。正文使用不可变 ContentStore 引用。消息与收件箱在同一 Serializable 事务内提交；唯一键和应用管理的 revision 处理跨实例竞争。冲突重试重新读取权限与幂等记录。

```powershell
# 在 TinadecOffice 根目录执行
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 test TinadecCore/tests/TinadecCore.Api.Tests/TinadecCore.Api.Tests.csproj --filter FullyQualifiedName~TinaChatTests --verbosity minimal
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 test TinadecCore/tests/TinadecCore.Architecture.Tests/TinadecCore.Architecture.Tests.csproj --verbosity minimal
```

2026-09-19 验证（唤醒与回群闭环）：`TinaChatTests` 17 例（含新增 3 例：持久唤醒行跨重启存活并只产出一份简报、待办期间到达的消息合并成一个回合且不唤醒发送者、run 终态把结果以接收参与者身份写回群并再唤醒整理者）与 `TinaChatWakeSchemaReconciliationTests` 1 例（旧库补建 `tina_chat_wakes` 表与 `result_message_id`/`result_run_status` 列后可直接写入）全绿；Architecture 17/17；Gateway `tinaChatRoutes.test.ts` 3 例（含 17 路径/24 操作精确计数）；Desktop `ChatroomPanel.test.ts` 10 例（新增简报采纳/拒绝/交接/无权限/412 五例）与 `TinaChatSection.test.ts` 6 例通过，`vue-tsc` 仍为改动前既有 24 处错误，`check:drift` 干净。本轮未做：真实 Electron 走查、真实外部模型与 PostgreSQL 演练。另记录一处与本次无关的既有故障：Core Api 全量在本机冷并行下反复以测试主机进程崩溃中止（`--blame` 无法归属用例），关闭新的排空循环后同样发生，定向复跑全部绿。

2026-09-18 验证：包含 11 个 TinaChat 用例的 API 定向回归共 71/71，AgentFramework 309/309，Architecture 17/17，Gateway 50/50 和构建通过。测试使用隔离 SQLite、真实 Core 宿主和执行链、脚本模型；捕获模型输入，验证用户原话、无关会话历史、运行中插入的旧式目标补丁均不进入绑定执行模型。并发发送、重启后重放、主体冒用、跨租户/跨工作区拒绝、敏感内容、来源分享、成员撤权、意图替换和禁用包均有覆盖。另完成真实 Electron、Gateway、Core、本机 OpenAI-compatible 模型进程和 SQLite/ContentStore 的端到端演练；`tina_chat_input_locked` 经 Gateway 保持原码，预期 403 不写入 Core 未处理异常日志。未进行真实外部模型供应商调用或 PostgreSQL 集成演练。

契约生成顺序：Core 的 CoreOpenApiSnapshotTests → Gateway `bun run generate:tina-chat-contract` → Gateway `bun test src`（外部快照变化时首次重写并报漂移，再检查）→ 根目录 `npm run generate:client -w @tinadec/desktop`。投影同时将 Core OpenAPI 3.1 的联合/可空类型转换为 Gateway OpenAPI 3.0 的 anyOf/nullable 表达。`bun run check:tina-chat-contract` 校验 TinaChat 投影与 Core 快照一致。生成物不可手改。

## 当前范围与后续入口

已经交付后端通信、意图提案、隔离执行、新消息自动唤醒整理者、执行结果自动回群、智能体侧的九个 TinaChat 工具（绑定身份、查人、看群、读件、发言、提交简报、读简报、采纳/拒绝、交接执行），以及桌面管理员观察面板与设置页的群管理面（注册身份、建群、邀请、跨区策略）。仍未实现：聊天室内发消息（本模块面向智能体，人类侧只观察与搭架构）、实时推送（观察面板仍是 5 秒轮询）、成员在线状态、描述文件附件、访客问答、工作区级智能体委托、CLI/MCP 适配、独立运行实例凭据、旧聊天迁移和独立宿主。意图生成目前使用公共 chat 路由；后续按参与者绑定模型时仍须经过 Core 的版本与权限解析。

现有普通 Core 会话保持原有行为；TinaChat 不声称已经替换全部会话存储或完成外部多租户身份接入。后续拆分独立项目时，应先抽取通信契约与身份/运行适配，不把 MAF、Core DbContext 或桌面状态加入通信协议。

## 管理员聊天室

设置页“智能体交流”子页负责架构搭建：注册/停用参与者身份、以某一身份建群与邀请、读写跨工作区通信策略；它不产生任何权限事实，服务端每次调用都重新核验。Desktop 左侧“聊天室”进入 `/chatroom`，展示实际保存的群聊/私聊、发送者、正文、发送时的完整受众、整理材料权限、接收确认、来源/回复引用和意图简报。可搜索标题/成员、按类型/工作区筛选、读取更早消息；默认每5秒刷新，后台页面暂停轮询，支持窄屏返回会话列表。面板整体是**纯只读观察位**：没有输入框，也没有任何写操作——不采纳简报、不交接执行、不替智能体确认接收。简报卡只呈现内容与状态（proposed / accepted / superseded / rejected）以及它来自哪些消息。采纳与交接属于**会话角色**（owner/admin），由智能体通过自己的 `tina_chat_list_intents` / `tina_chat_decide_intent` / `tina_chat_execute_intent` 完成；人类在整条协作链路上的真实权力只剩一处：隔离执行固定 `ask` 模式，mutating 工具调用仍要走 Core 审批门。曾经存在的"聊天室简报卡上让人类点采纳/拒绝/交接"的裁决台已于 2026-09-20 移除——那是把自己加的交互写进文档当设计的例子，规范 §14.4 只说"owner/admin 按会话 revision 采纳"，从不要求是人类。普通 Core 对话及尚未经 TinaChat 发送的 DmaEA 内部事件没有被自动迁移到这个面板。

四个新 GET 入口均在 `/api/v1/tina-chat/observer` 下：`/access` 返回授权范围，`/conversations` 支持 query/kind/workspace_id/offset/limit，`/conversations/{id}` 返回完整成员，`/conversations/{id}/messages` 支持 before_sequence 或 after_sequence。消息默认最新50条、返回按序排列；has_more 表示本次查询方向仍有更多。读取结果包含 archived/不公开参与者在会话中的发言。

权限来自服务端实际成员表。租户 owner/admin 可观察本租户全部活跃工作区，工作区 owner/admin 仅能观察其管理的工作区；不凭请求自称管理员，不跨租户。无需入群即可读取限定受众和保密原文。读取不修改任何智能体 ACK、不触发执行；打开会话产生 `observer.conversation_opened` 审计。权限撤销后下一次请求拒绝，前端清空受保护的已加载内容。全部观察响应不缓存。

本轮验证：TinaChat14、Architecture17、Gateway50、Desktop组件/交互18、布局32例通过；Core/Gateway快照、契约投影与 Desktop 生成类型同步，桌面/Gateway构建通过。Desktop 全量类型检查仍有原有24处错误，新增聊天室文件无类型错误。本轮真实进程启动被工具安全检查连续拦截（“无法确定请求的安全状态”），没有新面板的 Electron 实测结论，不能混用此前后端 E2E 结果声称 UI 已实测。
