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
| `AspNetCore/Endpoints/TinaChatEndpoints.cs` | `/api/v1/tina-chat` 下 25 个 HTTP 操作 |
| `Abstractions/Ports/ITinaChatObserver.cs`、`TinaChatService.Observer.cs` | 管理员观察专用读取，与参与者收件箱分开 |
| `Runtime/TinaChatObserverAuthority.cs` | 依据真实 Tenancy 成员关系确定观察工作区范围 |

TinaChat 只引用 Core 的 Abstractions 和 Persistence，不引用其他业务模块或 MAF。模型调用位于 DmaEA，跨模块组合位于 Runtime。执行仍经过现有模型解析、模式冻结、任务图、工具授权、审批和检查点，不存在另一套工具执行器。

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

## HTTP 调用顺序

客户端通过 Gateway 的 `/api/v1/tina-chat` 调用；Core 内部端口提供同样路径。完整 DTO 与响应状态见 OpenAPI，下面的 `{id}` 均替换为服务返回的标识。

1. `POST /participants` 注册 human，以及对话 agent（`receive_human_messages=true`、`can_interpret_intent=true`）和独立执行 agent（这两项默认 false）。
2. `POST /conversations` 指定创建者 actor_id、title、participant_ids，并带稳定 client_request_id。以各受邀身份 `PUT /conversations/{id}/members`，action=accept、participant_id=actor_id，expected_revision 取最新会话值。
3. 人类 `POST /conversations/{id}/messages`，带 client_message_id、content、明确的受众和 allow_derived_sharing=true。独立执行体的消息列表和收件箱不会出现原文。
4. 对话 agent `POST /conversations/{id}/intents/generate` 或 `/intents`，指定 source_message_ids、audience_participant_ids、client_request_id 和最新 expected_revision。后者还需提供结构化 content。生成失败不提交简报，也不创建执行。
5. owner/admin `POST /conversations/{id}/intents/{intentId}/decision`，decision=accepted，带当前会话 expected_revision。任何关键问题未解决时继续讨论和提交新版本。
6. `POST /conversations/{id}/intents/{intentId}/execute`，actor_id 为接收的 agent，带 mode_version_id 和可选 project_id。返回稳定的 session_id/run_id，运行状态、证据和控制复用 `/api/v1/runs/{runId}/...`。执行结果目前不会自动写成群消息。

读取消息使用 `GET /conversations/{id}/messages?actor_id=...&after_sequence=0&limit=50`；收件箱使用 `GET /participants/{id}/inbox?after_sequence=0&limit=50`。保存 `next_cursor` 后继续拉取；这是可重放的持久收件箱，不是自动唤醒模型的后台推送。`POST /participants/{id}/inbox/{messageId}/ack` 幂等返回 204，只表示客户端确认接收，不表示模型理解或任务完成。

并发写入使用请求体 expected_revision，不依赖 ETag。消息相同幂等键和相同内容返回原消息；内容不同返回 `idempotency_key_reuse`。默认 limit=50，允许 1–100，cursor 必须非负。

## 存储、恢复与验证

使用 Core 配置的数据库、内容目录和现有 DbContext 迁移协调器；没有新的默认数据库路径。9 张 `tina_chat_*` 表保存参与者、会话、成员、消息索引、受众/收件箱、工作区策略、意图、执行绑定和审计。正文使用不可变 ContentStore 引用。消息与收件箱在同一 Serializable 事务内提交；唯一键和应用管理的 revision 处理跨实例竞争。冲突重试重新读取权限与幂等记录。

```powershell
# 在 TinadecOffice 根目录执行
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 test TinadecCore/tests/TinadecCore.Api.Tests/TinadecCore.Api.Tests.csproj --filter FullyQualifiedName~TinaChatTests --verbosity minimal
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 test TinadecCore/tests/TinadecCore.Architecture.Tests/TinadecCore.Architecture.Tests.csproj --verbosity minimal
```

2026-09-18 验证：包含 11 个 TinaChat 用例的 API 定向回归共 71/71，AgentFramework 309/309，Architecture 17/17，Gateway 50/50 和构建通过。测试使用隔离 SQLite、真实 Core 宿主和执行链、脚本模型；捕获模型输入，验证用户原话、无关会话历史、运行中插入的旧式目标补丁均不进入绑定执行模型。并发发送、重启后重放、主体冒用、跨租户/跨工作区拒绝、敏感内容、来源分享、成员撤权、意图替换和禁用包均有覆盖。另完成真实 Electron、Gateway、Core、本机 OpenAI-compatible 模型进程和 SQLite/ContentStore 的端到端演练；`tina_chat_input_locked` 经 Gateway 保持原码，预期 403 不写入 Core 未处理异常日志。未进行真实外部模型供应商调用或 PostgreSQL 集成演练。

契约生成顺序：Core 的 CoreOpenApiSnapshotTests → Gateway `bun run generate:tina-chat-contract` → Gateway `bun test src`（外部快照变化时首次重写并报漂移，再检查）→ 根目录 `npm run generate:client -w @tinadec/desktop`。投影同时将 Core OpenAPI 3.1 的联合/可空类型转换为 Gateway OpenAPI 3.0 的 anyOf/nullable 表达。`bun run check:tina-chat-contract` 校验 TinaChat 投影与 Core 快照一致。生成物不可手改。

## 当前范围与后续入口

已经交付后端通信、意图提案、隔离执行和桌面管理员观察面板。消息发送/群管理 UI、自动唤醒/实时推送、运行结果自动回群、成员在线状态、描述文件附件、访客问答、工作区级智能体委托、CLI/MCP 适配、独立凭据发行、旧聊天迁移和独立宿主尚未实现。意图生成目前使用公共 chat 路由；后续按参与者绑定模型时仍须经过 Core 的版本与权限解析。

现有普通 Core 会话保持原有行为；TinaChat 不声称已经替换全部会话存储或完成外部多租户身份接入。后续拆分独立项目时，应先抽取通信契约与身份/运行适配，不把 MAF、Core DbContext 或桌面状态加入通信协议。

## 管理员聊天室

Desktop 左侧“聊天室”进入 `/chatroom`，展示实际保存的群聊/私聊、发送者、正文、发送时的完整受众、整理材料权限、接收确认、来源/回复引用和意图简报。可搜索标题/成员、按类型/工作区筛选、读取更早消息；默认每5秒刷新，后台页面暂停轮询，支持窄屏返回会话列表。普通 Core 对话及尚未经 TinaChat 发送的 DmaEA 内部事件没有被自动迁移到这个面板。

四个新 GET 入口均在 `/api/v1/tina-chat/observer` 下：`/access` 返回授权范围，`/conversations` 支持 query/kind/workspace_id/offset/limit，`/conversations/{id}` 返回完整成员，`/conversations/{id}/messages` 支持 before_sequence 或 after_sequence。消息默认最新50条、返回按序排列；has_more 表示本次查询方向仍有更多。读取结果包含 archived/不公开参与者在会话中的发言。

权限来自服务端实际成员表。租户 owner/admin 可观察本租户全部活跃工作区，工作区 owner/admin 仅能观察其管理的工作区；不凭请求自称管理员，不跨租户。无需入群即可读取限定受众和保密原文。读取不修改任何智能体 ACK、不触发执行；打开会话产生 `observer.conversation_opened` 审计。权限撤销后下一次请求拒绝，前端清空受保护的已加载内容。全部观察响应不缓存。

本轮验证：TinaChat14、Architecture17、Gateway50、Desktop组件/交互18、布局32例通过；Core/Gateway快照、契约投影与 Desktop 生成类型同步，桌面/Gateway构建通过。Desktop 全量类型检查仍有原有24处错误，新增聊天室文件无类型错误。本轮真实进程启动被工具安全检查连续拦截（“无法确定请求的安全状态”），没有新面板的 Electron 实测结论，不能混用此前后端 E2E 结果声称 UI 已实测。
