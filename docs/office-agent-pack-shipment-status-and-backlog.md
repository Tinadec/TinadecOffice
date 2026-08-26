# OfficeAgentPack 随装方案 — 本阶段收尾 + 后续待办

**提交:** `3d92404`
**分支:** `main`
**阶段:** OfficeAgentPack 随装方案（首版 0.1.0）第一轮收尾
**校验结果:** Core 114/114、Gateway 38/38、Desktop 276 pass + 14 skipped 全绿

本文件记录该方案在 `3d92404` 的落地状态，并把计划中**明确本期不做 / 延期**的项登记为后续待办，供后续阶段直接取用。与 `AGENTS.md`、`docs/tinadec-core-product-definition.zh-CN.md` 相互引用。

---

## 一、本阶段收尾（已完成）

计划各章已全部落地并验证：

| 计划章节 | 落地状态 |
|----------|----------|
| **Pack 与公共契约** | `apps/desktop/src/agentPacks/OfficeAgentPack/` 静态 manifest（`tinadec.office.agent-pack@0.1.0`，owner `tinadec.office`，digest `3e2fdbdb…630fe1e`，14 Agent + `baseline-prompt` + `default-mode` + 推荐 defaults）；RFC 8785/JCS + SHA-256；Core 四端点 `GET/POST install-preview/PUT agent-packs*` |
| **Core 安装与版本治理** | `AgentPackService` + `AgentPackEndpoints`：SemVer / 引用图 / 双层拓扑 / 能力校验；单事务 Prompt→Agent→Mode→defaults；幂等 / hash 冲突 409 / `newer_installed` 防降级 / 默认值保护 / legacy DevSeed 语义采纳 / `managed_resource_read_only`；删除 DevSeed 14 个 Office 正式写入；收口重复 `/api/v1/agents`；SQLite + PostgreSQL 七表迁移 |
| **运行时闭环** | Mode 发布快照固定 agent_definition / version_id / hash / order / layer / config / effective tools / 模型策略 / PromptVersion；`FormalModeResolver` 只读快照；`FrozenRunConfiguration` 补齐 system prompt / 模型策略 / roster order / tools / prompt binding；统一 Prompt 装配路径；supervisor 真实可审计实例；planner 冻结 specialist roster；frozen execution roster 上稳定 fail-closed 的 worker 选择；`worker.assigned` 首次调用前持久化 + 重启恢复不重选 |
| **App / Gateway / 验证** | connected-epoch bootstrap；跳过子/pet/debug 窗口；BroadcastChannel + Web Locks 多标签协调；confirm 确认 owner/version/hash；拒绝生命周期内去重、下次启动再询；持久 Retry 且重试重新 preview；Agent Center 紧凑 Pack 状态条；General 移除 localStorage 伪 default topology，统一读 Core WorkspaceDefaults；Gateway 四路径薄代理 + Core/Gateway OpenAPI 与 Desktop typed client 同步 |
| **测试** | Core：manifest/引用/拓扑、owner 权限、workspace 隔离、首装 / 幂等 / hash 冲突 / 升级降级抑制 / 并发 / 事务回滚 / 默认值保护 / legacy adoption / managed 只读 / 双库迁移、正式快照、7 类 frozen worker 选择与 fail-closed、`OfficePack_UpgradeKeepsExistingSessionOnItsExactModeVersion`（升级后旧 session 保持旧 mode_version）；App/Gateway：初连/重连/确认/拒绝/重试/非 owner/子窗口/多标签/薄代理/OpenAPI 快照 |

**四条边界（计划明示的假设）已按声明实现，非缺口：**
- "随安装" = App 连接后用户确认安装到当前认证工作区，非 OS 安装器复制文件或直写 Core 库。
- App 卸载不删除 Core 中的 Pack；v1 不提供卸载 / 回滚 / 市场分发。
- 内容 hash 只证完整性，不提供发布者密码学身份证明。
- 四个运营辅助角色（`context_compressor` / `skill_recommender` / `evolution` / `git_steward`）仅安装并进入 frozen roster，不实例化、不产生参与事件（已验证 `FullDuplexRunEngine` 不触达它们）。

---

## 二、后续待办（本阶段明确延期 / 范围外）

> 以下均来自计划本身的"延期 / 不做"声明与当前源码中的明确缺口，属于下一阶段取用项，非本阶段缺陷。

### 2.1 Pack 生命周期能力（计划 v1 范围外）
- [ ] **数字签名与信任库** — v1 信任边界为 "当前工作区 owner 授权 + 用户确认 owner/version/hash + 审计"；数字签名与签名信任库延期。落地时需在 `AgentPackEnvelope` 增加签名载体、Core 引入信任库校验并把签名方身份纳入审计，同时重算 Envelope 契约。
- [ ] **卸载 / 回滚** — 目前 Pack 只装不卸；需要 workspace-scoped uninstall 与回滚到前一个已安装 version 的能力（含 managed 资源回收 / 默认值回退策略）。
- [ ] **市场分发** — 目前 Pack 由 App 构建期静态携带；市场分发需引入远程获取、内容信任链与版本分发端点。

### 2.2 运行时深化（本阶段"仅安装冻结"角色的后续触发链）
- [ ] **`context_compressor` 事件触发链** — 目前仅冻结。需接入 run 内的上下文压缩触发（满足 token 阈值时），按快照精确 PromptVersion 装配并写 context patch。
- [ ] **`skill_recommender` 事件触发链** — 目前仅冻结。需在规划阶段按任务能力缺口调用，向 planner 投递技能/工具建议。
- [ ] **`evolution` 运行内评测闭环** — 目前候选生成入口存在（`agent-evolution/*` 已实现 generate/promote/reject），但 run 内由 `experience_curator` 主动提议与 canary 评测未接通。
- [ ] **`git_steward` 事件触发链** — 目前仅冻结。需在 run 涉及 Git 变更时按快照参与变更范围审查与提交计划形成。

### 2.3 部署与多租户
- [ ] **Cloud 多租户恢复调度器** — Core 目前恢复扫描仅覆盖 `ITenantContextAccessor.Current`（单工作区）；云端多租户恢复仍需 tenant scheduler + 分布式 claim。Pack 安装的并发收敛目前依赖 Core 单点 revision / 唯一索引 / 幂等 receipt。
- [ ] **scheduling 与 `tools/shell` 仍为 501 桩** — 与本方案无直接耦合，但后续 full-duplex 深化的调度 / shell 执行路径待实现。

### 2.4 既有遗留（当前阶段已知、非本方案引入）
- **Desktop 14 个 skipped 测试** — `NotificationIslandHost` 为 Vue 3.6.0-rc.2 Transition + happy-dom 环境问题的既存失败；待根 `vue` / `@vue/compiler-sfc` 升到 ≥3.6 stable 后解掉（见 `apps/desktop/AGENTS.md` NOTES）。
- **`ToolChainEndpointTests.WorkerWriteFile_…` 偶发 flake** — real-process 写盘测试在整包并发下偶发 approval decision `Conflict`；单测 / 套件单独重跑均 114/114 全绿，属已知资源争用 flake（见 `AGENTS.md` 终端纪律与 Core AGENTS 说明），后续可考虑与 real-process 测试隔离或串行化。

---

## 三、维护说明

- 本文件随本方案演进维护；完成任一后续待办时，勾选并在落点提交的 commit message 中引用。
- 完成项移入上方"本阶段收尾"表或直接删除，避免与 `AGENTS.md` / `docs/tinadec-core-product-definition.zh-CN.md` 的 CURRENT STATE 区重复。
- 与各级 `AGENTS.md` 的 `OfficeAgentPack` 条目保持一致；改动入口 / 契约 / 信任模型时同步刷新本文件与相关 AGENTS 元数据。