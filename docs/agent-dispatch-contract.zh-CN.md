# 双层智能体派发契约：按职责点名（2026-09-27）

本文回答三个问题，并记录依据与落地位置：

1. 上层协调者派子智能体，按**职责**还是按**名字**？
2. 名字是**固定**的还是**随机**的？
3. 派出去之后，结果怎么回来？

## 结论

| 问题 | 结论 |
|---|---|
| 选谁 | **按职责选**：协调者读执行者名册里的职责与边界，显式点名一位执行者。Core 照办；按 `required_tools` 反推执行者只作兜底。 |
| 用什么标识 | **固定的角色 id**（agent slug，如 `search`、`global_engineering`），不用人名、不用随机 id。 |
| 同角色多实例 | Core 分配 run 内句柄 `<agent>#<n>`（如 `search#2`），确定性、可重放，事件与证据都用它指代执行者。 |
| 结果回传 | 现阶段为**异步入队 + 收尾汇总**：子任务在协调者当前步之后运行，结果以结构化证据（任务标题、执行者句柄、状态、逐条判据证据）交给最终作答的协调者。同 run 内追问/等待结果尚未提供（见「边界」）。 |

## 双层架构的中心

- **Core 是唯一权威**：名册在 run 准入时冻结（`FrozenRunConfigurationV1.DispatchRoster`），档位（声明边 / spawn 白名单 / solo）决定谁可被派；边权限、spawn 权、agent 预算、逐次写审批都在 Core 执行。协调者点名**不能**放宽模式允许的范围。
- **协调者（上层）**：拥有对话与最终答复；负责理解目标、按职责拆成可独立验收的任务、点名执行者、依据证据作答。声明图模式下它通过规划输出的任务数组派发（`assignee`）；solo 模式下它在自己的工具循环里用 `task_dispatch`（`agent`）派发。
- **执行者（下层）**：只看得到派给它的简报（看不到协调者的对话），最终回复就是交回的全部内容，并以 `TASK_OUTCOME` / `CRITERION_EVIDENCE` 协议逐条自证。

## 依据

### 外部资料

Claude Code subagents（`subagent_type` = 固定名 + `description` 驱动委派，运行时实例另有句柄）、OpenAI Agents SDK（handoff `transfer_to_<agent_name>` + `handoff_description`；agents-as-tools）、AutoGen SelectorGroupChat（`<name> : <description>` 名册）、Google ADK（按名字 transfer + 必须有清晰描述）、CrewAI（以角色为标识）一致：**固定标识 + 职责描述，由描述驱动选择；没有任何一家使用随机名**。工具选择研究（BiasBusters, arXiv:2510.00307）指出查询与工具元数据的语义对齐是选择的最强驱动。Anthropic 的多智能体研究系统经验：每个子智能体需要目标、输出格式、工具指引与明确边界，描述含糊会导致重复劳动。

### 派发采样

`docs/research/dispatch-sampling-2026-09-27/`（`build.py` 生成提示词，`score.py` 复算，`outputs/` 为模型原始输出）。

- 12 个真实请求 + 1 个实例追问场景，5 个职责有重叠的执行者（search / global_engineering / git_steward / reviewer / doc_writer）。
- 4 种契约：**A** 现行（无目标字段，Core 按工具最窄匹配反推）；**B** 固定角色 id + 职责描述；**C** 人名 + 职责描述；**D** 随机 id + 职责描述。
- 每种契约在 Haiku 与 Sonnet 上各采样一次；A 的得分按 Core 真实的 `SelectWorker` 规则复算。

| 契约 | Haiku 严格 / 宽松 | Sonnet 严格 / 宽松 | 实例追问 |
|---|---|---|---|
| A 现行（按工具反推） | 8/12 · 8/12 | 6/12 · 8/12 | 不支持 |
| B 角色 id + 职责 | 10/12 · 11/12 | 11/12 · 12/12 | 2/2 |
| C 人名 + 职责 | 11/12 · 11/12 | 11/12 · 12/12 | 2/2 |
| D 随机 id + 职责 | 12/12 · 12/12 | 11/12 · 12/12 | 2/2 |

读法：A 的失败主要是 **Core 误派**而不是模型写错——模型为「找引用」写了正确的 `read_file` + `file_search`，最窄匹配却选中恰好只持有这两件工具的 `reviewer`；「改代码注释」落到 `doc_writer`；跨两种职责的任务直接 `worker_unavailable`。有职责描述时，**点名风格对准确率无可测影响**，因此选角色 id：它对日志、UI、审批与人同样可读，描述被截断时仍有语义。样本量小（每组合一次采样），结论看方向，与外部资料一致。

## 落地位置

| 层 | 位置 |
|---|---|
| 冻结名册 | `DmaEA/FrozenRunConfiguration.cs`（`FrozenDispatchTarget`、`DispatchRoster`）；`DmaEA/FullDuplexRunCoordinator.cs` 准入时冻结；描述来自 AgentVersion 快照 `description`（`Runtime/FormalModeResolver.cs`） |
| 规划 | `DmaEA/PlanningAgent.cs`（名册一行一执行者、`assignee` 必填）；`PlannedTask.Assignee` → `DurableTaskNode.RequestedAgent`；`ValidateAndMaterializeGraph` 校验 |
| `task_dispatch` | `Tools/CoreTaskDispatchTool.cs`（`agent` 必填）；`Tools/ToolInvocationScopeResolver.cs` 读冻结名册；`Tools/ToolDispatcher.cs` 调用期校验，错误列出合法 id 与职责 |
| 选人 | `FullDuplexRunEngine.ResolveRequestedWorker`（点名优先，边/spawn/预算不变）；`NextWorkerHandle`；`worker.assigned` 带 `handle` |
| 等待与接续 | `task_wait`（`Tools/CoreTaskWaitTool.cs` 声明，`FullDuplexRunEngine.HandleTaskWaitAsync` 执行：驻留 → 子任务跑完 → 同一轮次恢复并拿到结果）；`task_dispatch.follow_up_of` → `FollowUpBrief` |
| 收尾 | `FullDuplexRunEngine.FormatTaskEvidenceForMeeting` |
| 种子包 | `apps/desktop/src/agentPacks/GraphSeedPack/manifest.json`（2.9.0 起职责式描述、管线提示词对齐 `assignee`/`agent`；2.10.0 `solo_master` 持 `task_wait`；2.11.0 `solo_master`/`global_engineering` 持 `plan_update`；2.12.0 新增只读执行者 `reviewer` 与 Plan / Review / Spec 三个模式，固定 id 变为 `search` / `global_engineering` / `reviewer`；2.13.0–2.16.0 组织/证据/worktree/环境工具；**3.0.0 对话身份合一：七模式全部由 `meeting` 对话**） |

## 边界（尚未解决）

- ~~同 run 内等待/追问~~（2026-09-27 已解决）：协调者先把能并行的子任务都派出去，再调 `task_wait`；它被驻留到这些子任务结束，然后在同一次调用的结果里拿到每个子任务的句柄、状态、摘要与逐条判据证据。追问用 `task_dispatch` 的 `follow_up_of` 指向上一个子任务，新执行者的简报里会带上前一个的结果。仍未做：向**正在运行**的子任务插话（只能在它结束后接续）。
- **名册规模**：每个模式 1–2 个执行者（Review 只有 `reviewer`，Plan 只有 `search`）；执行者增多时应保持一行一职责、边界写清，工具选择准确率随候选数增加会下降。
