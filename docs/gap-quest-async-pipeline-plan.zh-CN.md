# 计划书②：Quest — spec 提交后异步自主执行、阻塞才通知的任务管道

> 状态：草案 v1（2026-11-02），来源：Qoder/Codex 竞品对比（当日会话），**未登记**进 §11/backlog，待批准后登记。
> 判据沿用既有差距报告 A/B/C 三级；每批先红后绿+变异对照，收尾必须过 live 验收。

## 0. 结论一句话

对标 Qoder Quest Mode（提交 spec→拆步→长时自主执行→只在阻塞/需要决策时通知）。TinadecOffice 的地基（持久队列、崩溃恢复、审批门、事件流）已经齐到"只缺一层产品面"：缺的是**跨会话的任务实体、会话外调度器和完成通知**。本计划刻意不解 lane、不碰云端调度——那是另两笔账。

## 1. 背景与对标

- Qoder Quest：高层 spec 委托出去，Qoder 自主长时间工作，开发者只在被阻塞时被打断；Remote 版把执行丢到远程沙箱（远程部分不在本计划）。
- 本仓现状：会话忙时的 interaction 有**持久排队**（`RunDirectives` 表 kind=queued_interaction），但队列是**会话内、单层、无任务语义**的——没有一个"任务"实体能活过单个 run、活过会话、能在 Core 重启后继续被推进。
- backlog `office-agent-pack-shipment-status-and-backlog.md:60-61` 与 `AGENTS.md:445/449` 登记的 scheduling 501 桩，其"云端多租户恢复调度器"半边仍留 backlog；本计划把**单机单用户**那半边做掉，并明确收敛承诺范围。

## 2. 目标 / 非目标

**目标**
1. 用户提交 Quest（目标/spec + 验收描述），Core 落持久实体，自主推进为**一串 run**直至完成或阻塞。
2. 阻塞语义：审批请求/审批过期/`capability_missing` 等让 Quest 转入 `awaiting_user`，通知用户后暂停推进。
3. 完成与阻塞都产生通知（应用内通知岛 + OS 级系统通知，后者默认关）。
4. Core 重启后 Quest 由 RecoveryCoordinator 族机制续跑（不是重新开始）。
5. Quest 看板：Home 页任务区起步，看板页 v2。

**非目标**
- 不解 lane 运行时不可达问题（`RunFreezeGate.cs:85-87` 全量拒绝 lanes_enabled）——Quest v1 是串行推进的一串 run，不需要并行步。
- 不做云端/远程沙箱执行（backlog 云端调度器那半边不动）。
- 不做 `capability_missing` 自动重规划（`DmaEA/Operations/OperationalTriggers.cs:138-163` 现状"仅建议"保持，v1 阻塞上抛给人）。
- 不做定时触发/Automations（那是另一条竞品差距，另立）。

## 3. 现状与证据（2026-11-02 实测）

**可直接复用的既有机制**
1. **持久队列+幂等 replay**：撞 SESSION_BUSY 时持久化 `RunDirectives` 行（含完整 payload，`InteractionsEndpoints.cs:491-535`），幂等键 `session:{id}:queued:{client_message_id}` 可 replay（:427-441）；引擎 run 终态后 `ReleaseTerminalQueuedInteractionsAsync` 按序只放队头（`FullDuplexRunEngine.Lanes.cs:1002-1023`）。
2. **12 态状态机+崩溃恢复**：`Abstractions/RunStatus/RunStatusMachine.cs:12-17,25-45`，awaiting_* 为受保护决策态（RecoveryPolicy，重启不失败）；`Runtime/RecoveryCoordinator.cs:30-103` 启动两遍+15s 修复循环（补齐终态流、放行 queued，:149-170→`FullDuplexRunEngine.cs:291-307`）。
3. **传输层即通知层**：run 级 `lib/runStream.ts` + 会话级 `/api/v1/events` replay-then-follow、after_seq/Last-Event-ID、15s 心跳（`StorageEndpoints.cs:684,423-460`）——"完成/阻塞才通知"不需要新传输。
4. **审批门全链**：pending 过期→`approval.park_expired`→升级 awaiting_user（`Lifecycle/ToolApprovalCoordinator.cs:749-765`），binding mismatch 失败（:785-801），`AwaitingApprovalExpiryReview` 标志（`FullDuplexRunEngine.cs:7951`，图 tier 泊车置位 :2036-2046）；无人值守档 M5（产品定义 §10.5）：full-access/auto-approve 放行、其余挂起。
5. **通知岛+跨窗广播**：`components/NotificationIslandHost.vue`+`composables/useNotifications.ts`（status/sticky/transient+source 持久性），跨窗口 IPC broadcastStatusNotification（`electron/main.cjs:779-786`）。**无 OS 级通知**（main 未接 Electron Notification）。
6. checkpoint CAS：`WHERE CheckpointRevision==ExpectedRevision` 更新 CurrentCheckpointId，幂等防重（`Lifecycle/StorageLifecycleService.cs:788-870`；注意本计划写作时题给行号 451-521 已漂移）——Quest 的续跑锚点。

**缺失、必须新建**
- 跨会话任务级持久实体（Quest spec→验收→状态）与会话外调度器。
- Core→桌面 OS 通知通路。
- Quest 看板 UI（现仅 run 内任务图投影 `TaskGraphPanel`）。
- spec 模式产出物与 Quest 的衔接（spec 模式存在但产出不落任务实体）。

## 4. 设计方案

### 4.1 实体与状态
- 新表 `Quests`（对齐既有 DbContextMigrationParticipant 自研迁移路径）：Id、SessionId、SpecReference（ContentReference+Hash，复用既有引用纪律）、AcceptanceText、Status、CurrentRunId、CreatedFrom（manual|spec_mode）、幂等键。
- 状态词表**复用 12 态投影**，不发明第二套：Quest 无独立终态机——Quest 状态 = 其当前 run 状态 + "推进器视角"的 queued/running/awaiting_user/done/blocked。教训对齐：少造实体、少造词表（RunStatusMachine 的词表已经是跨线合约，Gateway 枚举从它生成）。
- 一个 Quest 内部是**一串 run**：首 run=spec→计划（plan 语义），后续 run 消费 checkpoint.Tasks 里的步骤继续推进；步骤粒度复用 checkpoint，不新建步骤表。

### 4.2 派发与推进
- `QuestScheduler`（BackgroundService，仿 RecoveryCoordinator 的 15s 循环）：扫 ready 的 Quest→以既有 queued 语义投 interaction（走 `QueueBehindActiveRun=true` + RunDirectives 管线，幂等键带 questId）→ run 终态回调推进下一 run。
- v1 调度策略：**每会话串行**（队头放行的既有规则不变），跨 Quest 并行数上限 = `SchedulingPolicy.MaxActiveRunsPerSession` 既有配置，不新增预算词表。
- 阻塞即停：run 转入 awaiting_*（含 park_expired 路径）时 Quest 停止派发并发出通知；用户在审批面板处理后续跑（审批全链原样复用，**Quest 默认 human-only 审批档，绝不因 Quest 模式自动放宽**——无人值守 M5 的 full-access/auto-approve 是用户既有选择，Quest 尊重它但不修改它）。

### 4.3 通知
- 应用内：复用通知岛，Quest 完成/阻塞各一个事件源（sticky）。
- OS 级：`electron/main.cjs` 接 Electron Notification API + IPC；设置页开关，**默认关**，OOBE 提及；Core 不发 OS 通知（职责边界：Core 只发事件，桌面决定呈现）。

### 4.4 spec 模式衔接
- spec 模式产出物提供"转为 Quest"动作：把 spec 文本+验收描述落 Quest.SpecReference，会话留在原地，Quest 派发从新 interaction 开始。
- 与"提交后离开"的 UX 匹配：Quest 建立后用户可关会话/关窗口；回来时 Home 任务区显示进度，run 级流式续传（after_seq）保证不丢帧。

## 5. 分批落地与验收

| 批 | 内容 | 验收门禁（先红后绿/变异） |
|---|---|---|
| Q1 | Quests 表+CRUD 端点+QuestScheduler 串行推进（复用 RunDirectives 管线） | e2e：spec→多 run→终态推进；Core 重启 mid-quest→RecoveryCoordinator 续跑不重来；变异：调度器跳过 awaiting_* 停推规则→恰红 |
| Q2 | 完成/阻塞事件 + 通知岛接入 + OS 通知（默认关） | 审批过期路径触发 Quest blocked 通知的 e2e（复用 park_expired 现成触发）；OS 通知变异测试在 main.cjs 层 |
| Q3 | Home 任务区 + Quest 看板 v1 + "转为 Quest" | i18n parity 显式白名单登记；多标签 BroadcastChannel 不互踩；live 验收（真实链路走一遍 spec→完成） |
| Q4（可选） | `capability_missing` 的 Quest 级策略（保持"仅建议"或升级为可配置停推） | 用户决策后另批 |

## 6. 风险与开放问题

1. **与 RunDirectives 语义冲突**：queued 幂等键现在是 `session:{id}:queued:{client_message_id}`，Quest 复用时要保证 Quest 派发的 interaction 与用户手发的不互相吞——Q1 的幂等键设计是第一个评审点。
2. **无人值守安全面**：Quest 拉长了"没人看着"的时间窗。默认 human-only + 不自动放宽审批档是底线；auto-approve 用户跑 Quest 属其既有授权，但 Quest 完成通知必须把"哪些工具被自动放行"汇总进通知体。
3. scheduling 501 的承诺收敛：本计划只承诺单机单用户；backlog 里"云端多租户恢复调度器"（`office-agent-pack-shipment-status-and-backlog.md:60-61`）保持登记，语义要在 backlog 里改写为"仅剩云端半边"，避免两处各说各话。
4. SQLite 既有约束（不能在 SQL 比较 DateTimeOffset、Error 5 抖动族 §4.5）：Quests 表排序在内存做；连接生命周期修复是它的前置但不在本计划内。

## 7. 与既有登记项的关系

- 消化 backlog scheduling 501 的单机半边（需回写 backlog 措辞）。
- 与 §11 既有登记项（AgentEditorDrawer 组件化、工具覆盖 UI、模型服务页 CLI/ACP、roster 单测）无依赖冲突，不抢占。
- 依赖顺序建议：真实模型冒烟（既有报告 §4.1）→ Q1 的真 provider 验收。
