# TinaChat 模块约定

**Last Updated:** 2026-09-20
**Last Updated By:** 智能体双向闭环：持久唤醒队列、执行结果回群、设置页群管理面；智能体侧工具面扩到 9 个，聊天室收为纯观察。修正操作数为 24。
**Last Verified Commit:** `a82ed34` 之后的工作树（唤醒闭环 + 智能体侧 list/decide/execute 工具，均未提交）
**Branch:** Everything-changed

## 位置与依赖

用户决定位置为 `TinadecCore/TinaChat`，覆盖此前讨论的根级独立目录方案。它是 Core 内部模块，模块 id `tina_chat`；未来独立交付为后续工作。当前 23 个 Core source project、14 个全量模块描述符。

本模块仅依赖 Abstractions/Persistence。DTO 放 Contracts，跨模块端口放 Abstractions；真实身份与运行组合放 Runtime，模型理解放 DmaEA，HTTP 放 AspNetCore。不要引用其他业务模块、MAF、Gateway 或 Desktop。

## 必须保留的规则

- 身份由经验证的租户主体控制；请求的 actor_id、职位或名字不是凭据。同一所有者能管理自己的多个参与者，目前尚无单独的运行实例凭据。
- 对话理解是可配置职责，不把 meeting 名字、某个全局 agent id 或管理员职责硬编码为唯一入口。
- 原文可见和整理材料可接收是不同权限。跨工作区必须同时满足会话与工作区策略；来源、引用、收件箱与模型输入使用同一授权判断。
- 消息和受众同事务落盘；客户端键绑定完整内容。禁止用内存队列替代持久收件箱，禁止把确认接收解释成理解或完成任务。
- 意图提案记录未核实陈述、约束、假设和问题；采纳是版本化操作，blocking_questions 未解决时不能执行。
- Core handoff session 绑定已采纳材料。普通交互、insert、历史查询、长期记忆和旧式 context patch 不得成为注入原话的旁路；恢复时重新核对绑定。
- 不直接消费其他模块 DbContext，不绕过已有模式/包禁用检查、工具审批、运行租约与检查点。
- 智能体工具面（`tina_chat_*` 六个 Core 虚拟工具）只是既有服务方法的另一层入口：受众、来源、保密、跨区与成员判定必须继续走同一套代码，禁止在工具里另写一份。会话发言身份由 `tina_chat_bind` 认领、每次调用重核归属；一个会话只绑一个身份。
- 这些工具不加审批门是有意的：授权来自“模式声明 ∩ 冻结清单 ∩ 实例 grant”，副作用面只有该参与者本可发出的通信记录。若将来给工具加工作区副作用，必须先回到审批门。
- 消息与它欠下的回合必须同事务落盘（`tina_chat_wakes`）；同一 (会话, 参与者, 原因) 未完成前只留一条待办并合并来源。不得用内存队列或 fire-and-forget 任务替代，冷却只推迟回合、绝不丢弃。
- 意图简报不再唤醒整理者，发送者不唤醒自己：这两条是防两个整理者互相作答的回路闸，移除前必须另设替代熔断。
- 后台回合以参与者所有者自身、经 Tenancy 核验的身份运行，绝不复用请求侧的环境主体；400/403/404/409 视为终止，模型故障与修订竞争退避重试。
- 执行结果回群以接收参与者身份发出，受众与来源沿用被执行的简报且不扩大，保密等级继承简报；parked（等待人工决定）不是终态，不得播报。

## 验证和边界

入口说明及当前未实现项见 README.md；规范定义见 `../../docs/tinadec-core-product-definition.zh-CN.md` §14.4。服务端测试在 `../tests/TinadecCore.Api.Tests/TinaChatTests.cs` 与 `TinaChatWakeSchemaReconciliationTests.cs`，必须检查真实模型请求内容，而不只检查 API 返回的消息列表。操作数是 24（20 通信/意图 + 4 观察，17 条路径），表是 11 张。设置页“智能体交流”承担群管理面；聊天室是纯观察位——无发送框（按定位不需要）也无任何写操作。简报的采纳/交接由智能体用 tina_chat_list_intents / tina_chat_decide_intent / tina_chat_execute_intent 完成（§14.4 的 owner/admin 是会话角色，不是人类专属）。PostgreSQL、真实外部模型、CLI/MCP、实时推送与独立凭据仍未测或未实现，不得写成已完成。Api 测试宿主的 `appsettings` 默认开启排空，因此任何新增的 TinaChat 用例都必须在夹具里显式设 `TinadecTinaChat:WakeDrainEnabled=false`，再手动驱动 `TinaChatWakeService.RunPassAsync`，否则后台 tick 会污染模型调用计数。

`TinaChatService.Observer.cs` 实现独立 `ITinaChatObserver`。管理员权限由 Runtime 的 `ITinaChatObserverAuthority` 适配真实 Tenancy 成员记录，不能信任 `actor_id`、human 类型或调用方的 Role 字符串。租户管理员本租户全量观察，工作区管理员仅管理范围；即使不入群也可读保密/限定受众原文和完整来源。观察不得调用普通成员的 ACK 或修改成员权限；打开会话记录审计。历史默认最后50条，before_sequence 向前翻页，after_sequence 跟随新消息，不能同时指定。测试覆盖跨租户拒绝、角色伪造、撤权、历史翻页和观察不改变 ACK。

修改契约时同步 Core OpenAPI、Gateway TinaChat 契约投影/外部快照和 Desktop 生成类型；具体命令见 README.md。
