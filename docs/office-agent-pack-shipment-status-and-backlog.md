# OfficeAgentPack 随装方案 — 本阶段收尾 + 后续待办

**提交:** `3d92404`（首版 0.1.0）/ `9b3d42b`（列表 500 修复）/ 本阶段（v0.2.0 七模式 + 发送框打通）
**分支:** `Astra`（本文写于 `main` 时代，HEAD 已为 `3e8ff30`）
**校验结果（2026-08-26 当时）:** Core Api 116/116、AgentFramework 56/56、Gateway 40/40、Desktop 276 pass + 14 skipped 全绿
**当前版本（2026-09-07 核对）:** Pack `0.2.3`（`apps/desktop/src/agentPacks/OfficeAgentPack/manifest.json:9`）；测试基线为 Core Api 194+1 known-flake、AgentFramework 100、Architecture 11、Governance 34、Gateway 44、Desktop 332

本文件记录该方案的落地状态，并把计划中**明确延期**的项登记为后续待办。与 `AGENTS.md`、`docs/tinadec-core-product-definition.zh-CN.md` 相互引用。

---

## 〇、阶段二：发送框模式 ↔ Pack 拓扑打通（2026-08-26）

- **Pack v0.2.0**：manifest 新增六个对话模式 `conversation.plan/spec/ask/vibe/auto/agent`（节点子集对齐 TOML profile，各带合法 worker 子集），共 7 Mode / 22 资源；digest `837497ea…7ee5`。
- **Pack v0.2.1（2026-08-31 双层权限收口）**：五个非 steward 治理角色 `tool_scope` 由 `["*"]` 收口为 `[]`，与 Core `CoreAuthorizationContextResolver` 新增的 operation 层零工具策略一致；digest `8110547a…962e`。`worker.general` 的通配按产品决定保留（`conversation.vibe` 唯一可写兜底 worker），且 `AgentInstanceService.SpawnAsync` 已禁止派生实例携带通配，通配不会再落到任何实例上。
- **拓扑显示修复**：`GET /api/v1/agent-modes/{id}` 对 published/managed 模式返回 published 投影 + `managed` 标记（此前 draft-only 守卫 409 导致画布空白）；Desktop 模式面板只读渲染 + 克隆入口。
- **agent_mode 打通**：`POST /interactions` 接受 `agent_mode`；解析顺序 = 显式 `mode_version_id` > workspace 已发布 `conversation.{slug}` > 会话默认；选中即持久化到 session 并以 conversation 应用语义 admission。Gateway `sessionMapper` 补转发 `mode_version_id` 等绑定字段，`interactionsMapper` 薄校验枚举。
- **回归测试**：装包后三列表端点 + published 拓扑读 + agent_mode 三级解析/未知拒绝/优先级。

---

## 一、本阶段收尾（已完成）

计划各章已全部落地并验证：

| 计划章节 | 落地状态 |
|----------|----------|
| **Pack 与公共契约** | `apps/desktop/src/agentPacks/OfficeAgentPack/` 静态 manifest（本节为 **0.1.0 首版**快照；当前已是 `0.2.3`，含 14 Agent + 5 PromptPipeline + 7 Mode + 推荐 defaults）；RFC 8785/JCS + SHA-256；Core 四端点 `GET/POST install-preview/PUT agent-packs*` |
| **Core 安装与版本治理** | `AgentPackService` + `AgentPackEndpoints`：SemVer / 引用图 / 双层拓扑 / 能力校验；单事务 Prompt→Agent→Mode→defaults；幂等 / hash 冲突 409 / `newer_installed` 防降级 / 默认值保护 / legacy DevSeed 语义采纳 / `managed_resource_read_only`；删除 DevSeed 14 个 Office 正式写入；收口重复 `/api/v1/agents`；SQLite + PostgreSQL 七表迁移 |
| **运行时闭环** | Mode 发布快照固定 agent_definition / version_id / hash / order / layer / config / effective tools / 模型策略 / PromptVersion；`FormalModeResolver` 只读快照；`FrozenRunConfiguration` 补齐 system prompt / 模型策略 / roster order / tools / prompt binding；统一 Prompt 装配路径；supervisor 真实可审计实例；planner 冻结 specialist roster；frozen execution roster 上稳定 fail-closed 的 worker 选择；`worker.assigned` 首次调用前持久化 + 重启恢复不重选 |
| **App / Gateway / 验证** | connected-epoch bootstrap；跳过子/pet/debug 窗口；BroadcastChannel + Web Locks 多标签协调；confirm 确认 owner/version/hash；拒绝生命周期内去重、下次启动再询；持久 Retry 且重试重新 preview；Agent Center 紧凑 Pack 状态条；General 移除 localStorage 伪 default topology，统一读 Core WorkspaceDefaults；Gateway 四路径薄代理 + Core/Gateway OpenAPI 与 Desktop typed client 同步 |
| **测试** | Core：manifest/引用/拓扑、owner 权限、workspace 隔离、首装 / 幂等 / hash 冲突 / 升级降级抑制 / 并发 / 事务回滚 / 默认值保护 / legacy adoption / managed 只读 / 双库迁移、正式快照、7 类 frozen worker 选择与 fail-closed、`OfficePack_UpgradeKeepsExistingSessionOnItsExactModeVersion`（升级后旧 session 保持旧 mode_version）；App/Gateway：初连/重连/确认/拒绝/重试/非 owner/子窗口/多标签/薄代理/OpenAPI 快照 |

**四条边界（计划明示的假设）已按声明实现，非缺口：**
- "随安装" = App 连接后用户确认安装到当前认证工作区，非 OS 安装器复制文件或直写 Core 库。
- App 卸载不删除 Core 中的 Pack；v1 不提供卸载 / 回滚 / 市场分发。
- 内容 hash 只证完整性，不提供发布者密码学身份证明。
- 四个运营辅助角色（`context_compressor` / `skill_recommender` / `evolution` / `git_steward`）安装后进入 frozen roster；2026-08-29 起经运营层触发链按需旁路激活（见 §2.2 状态更新）。

---

## 二、后续待办（本阶段明确延期 / 范围外）

> 以下均来自计划本身的"延期 / 不做"声明与当前源码中的明确缺口，属于下一阶段取用项，非本阶段缺陷。

### 2.1 Pack 生命周期能力（计划 v1 范围外）
- [ ] **数字签名与信任库** — v1 信任边界为 "当前工作区 owner 授权 + 用户确认 owner/version/hash + 审计"；数字签名与签名信任库延期。落地时需在 `AgentPackEnvelope` 增加签名载体、Core 引入信任库校验并把签名方身份纳入审计，同时重算 Envelope 契约。
- [ ] **卸载 / 回滚** — 目前 Pack 只装不卸；需要 workspace-scoped uninstall 与回滚到前一个已安装 version 的能力（含 managed 资源回收 / 默认值回退策略）。
- [ ] **市场分发** — 目前 Pack 由 App 构建期静态携带；市场分发需引入远程获取、内容信任链与版本分发端点。

### 2.2 运行时深化（"仅安装冻结"角色的后续触发链）
> **状态更新（2026-08-29）**：四条触发链已随运营层触发链落地（`[triggers]` 策略 + `DmaEA/Operations/OperationalTriggers.cs` 四锚点旁路分派；详见根 `AGENTS.md` 同日条目与 `docs/tinadec-core-product-definition.zh-CN.md` §6.4.1。注：早期版本此处引用的 `withdocs/双层智能体架构现状与差异分析.md` **在仓库中不存在**，已移除）。剩余子项如下。
- [x] **`context_compressor` 事件触发链** — 已实现：`task_closed`/run 收尾触发，token 阈值门 + ToolCallAware 守卫，`kind=compaction` CAS 补丁与 `context.compacted` 事件。剩余：压缩与监督轮次的联动策略仍为基础形态。
- [x] **`skill_recommender` 事件触发链** — 已实现：`task_graph_created`/`capability_missing` 触发，推荐写 `checkpoint.RecommendedCapabilities` 并注入重规划指令。剩余：`capability_missing` 尚不自动触发重规划（仅建议）。
- [~] **`evolution` 运行内评测闭环** — 部分实现：run 收尾由 `experience_curator` 主动策展记忆/智能体候选（受 `[memory]` 白名单约束）；`promote` 已解锁为消毒→发布不可变版本→回填；新增 `GET .../proposals/{id}/evaluation`（源 run 回放评测证据）。剩余：canary/灰度与激活阶段未实现。
- [~] **`git_steward` 事件触发链** — 部分实现：仅对触碰 `git_*` 工具的 run 发 `git.steward.reviewed` 建议事件（不执行 git）。剩余：与快照联动的变更范围审查与提交计划形成。

### 2.3 部署与多租户
- [ ] **Cloud 多租户恢复调度器** — Core 目前恢复扫描仅覆盖 `ITenantContextAccessor.Current`（单工作区）；云端多租户恢复仍需 tenant scheduler + 分布式 claim。Pack 安装的并发收敛目前依赖 Core 单点 revision / 唯一索引 / 幂等 receipt。
- [ ] **scheduling 仍为 501 桩**（`tools/shell` 已实现：TinadecTools 的 `shell` 工具，审批门控；注意它**没有沙箱**，直接执行 `cmd.exe /d /s /c`，有沙箱的是 `command_run`）— 与本方案无直接耦合，但后续 full-duplex 深化的调度路径待实现。

### 2.4 既有遗留（当前阶段已知、非本方案引入）
- **Desktop 14 个 skipped 测试** — `NotificationIslandHost` 为 Vue 3.6.0-rc.7 Transition + happy-dom 环境问题（rc.2→rc.7 升级后复测仍失败），当前已 `describe.skip`（非运行中失败）；待根 `vue` / `@vue/compiler-sfc` 升到 ≥3.6 stable 后解掉（见 `apps/desktop/AGENTS.md` NOTES）。
- **`ToolChainEndpointTests.WorkerWriteFile_…` 偶发 flake** — real-process 写盘测试在整包并发下偶发 approval decision `Conflict`；单测 / 套件单独重跑均 114/114 全绿，属已知资源争用 flake（见 `AGENTS.md` 终端纪律与 Core AGENTS 说明），后续可考虑与 real-process 测试隔离或串行化。

### 2.5 E2E 验证发现的后续事项（2026-08-29）
> 来源：`docs/core-agent-e2e-verification-2026-08-29.md`（WorkBuddy 会话的真实 HTTP 端到端验证）。
- [ ] **契约快照门禁缺口** — Gateway 契约快照落后 Core（`memory-items`×2、`approvals/{id}`、`recovery-decision` 未进快照），且 `check:drift` 只守 Gateway→Desktop 方向；建议补 Core→Gateway 快照门禁。
- [ ] **状态机锐边** — run 已处于 `awaiting_user` 时，引擎再次尝试转 `awaiting_approval` 会被 `RunStatusMachine` 拒绝（`StorageLifecycleService.cs` 不允许 `awaiting_user→awaiting_approval`），run 以 runtime error 失败；仅在高并发互扰场景触发，需明确合法转移或排队语义。
- **环境提示（非代码项）**：假模型 provider `bc12683f`（"Local Fake E2E"）仍指向已停止的本地假端点 `127.0.0.1:48799`，下次真实使用前需在模型中心重配；`data/tinadec.db` 留有验证 project/session 残留可在 UI 清理。

---

## 三、维护说明

- 本文件随本方案演进维护；完成任一后续待办时，勾选并在落点提交的 commit message 中引用。
- 完成项移入上方"本阶段收尾"表或直接删除，避免与 `AGENTS.md` / `docs/tinadec-core-product-definition.zh-CN.md` 的 CURRENT STATE 区重复。
- 与各级 `AGENTS.md` 的 `OfficeAgentPack` 条目保持一致；改动入口 / 契约 / 信任模型时同步刷新本文件与相关 AGENTS 元数据。