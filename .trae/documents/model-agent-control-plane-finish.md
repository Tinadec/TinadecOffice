> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# TinadecOffice 模型与智能体控制面重构 — 收尾计划

## 直接回答：没有做完

对照原计划的五个阶段，当前状态是**编码主体已完成约 80%，但术语清理未完成，最终验证从未执行**。最后一轮可确认的测试基线仍是 codex 中断前的 Core 110 passed / 6 failed；此后所有改动（含 Gateway 清理、Desktop 接入、Core 测试重写）都没有再跑过任何测试或 build。

## 当前状态（2026-08-27 本次只读验证）

### 已完成（静态可证）

| 原计划阶段 | 状态 | 证据 |
| --- | --- | --- |
| 阶段1 Desktop 编译修复 | 基本完成 | `mockApi.ts:264-273` `saveModelRoute` 已用 `{candidates:[{provider_instance_id, model, position}]}`；`mockData.ts` 已有 `mockAgentDirectory()` |
| 阶段2 Desktop 正式接入 | 主体完成 | `SettingsPage.vue:282` `agentDirectory = ref<AgentDirectoryItemDto[]>`，`:1042-1043` 目录+页内转换双轨；`AgentModesPanel.vue` 已保存 `model_strategy_override`（inherit/route/fixed）；`RuntimeInstancesPanel.vue:135-146` 已显示 source_definition/frozen_version/recent_actual_model/fallback_summary；`api.ts:1896-1911` preview/references/invocations 三 API 已接 |
| 阶段3 Gateway 清理 | **已完成** | `TinadecGateway/src` 无 `application-modes`/`agents/catalog`/`meeting_provider_id` 匹配；新代理 `index.ts:1096-1119`（model-resolution/preview、model-references、model-invocations）；`sessionMapper.ts` 已更新；`runtimeProxy.test.ts` 已重写；`openapi.external.json` 已重新生成；`AGENTS.md` 已修改 |
| 阶段4 Core 测试重写 + snapshot | 已做但未验证 | `TinadecCore/tests` 无旧术语；`openapi.core.json` 已重新生成（326 行变更）；AgentPack/FullDuplex/ToolChain 测试文件均已修改 |
| 阶段5 最终验证 | **未执行** | 无任何本机测试/build 记录 |

### 未完成项（本计划要做的）

1. **Core 术语残留（8 处非迁移）**
   - `AgentInstanceService.cs:118,120,202` + `DmaeaEndpoints.cs:483`：`AgentProfilePromotionDisabledException`
   - `ILongTermMemoryService.cs:21`、`MemoryModuleRegistrar.cs:190,231`、`MemoryDbContext.cs:160,161`：`AgentProfileId`
   - `TinadecCore/AGENTS.md`：229 行 "agent profile"、234 行 `AgentProfileRecord.Layer == "planning"`、238 行 "Gateway sessionMapper 转发 meeting_provider_id" 均为过时描述，且缺 2026-08-27 模型策略重构条目
2. **Desktop 术语残留（实质约 20 处）**
   - `api.ts:866` `AgentProfileDto` 前端投影类型（`:844` AgentCenterAgentDto extends 它、`:953` Legacy 注释、`:2105` promoteAgentCandidate 返回类型）
   - `api.ts:1721` `getAgentCatalog()` 死代码 —— 调用已被删除的 `/api/v1/agents/catalog`，全仓无调用者
   - `SettingsPage.vue` 13 处、`AgentTopologyCanvas.vue` 3 处、`mockData.ts:20,1210,1211,1220` 引用 `AgentProfileDto`
   - `generated/client.ts:51` 仍含 `meeting_provider_id`（生成文件未重新生成；`package.json` 有 `generate:client`/`check:drift` 脚本）
3. **promoteAgentCandidate 返回类型错位**：Core `EvolutionEndpoints.cs:76` promote 实际返回 proposal（`promoted_agent_id` 字段），Desktop `api.ts:2105` 标注为 `AgentProfileDto`，需对齐
4. **全部测试/build 未验证**：Core 6 个失败测试是否真修复（Pack 目录 28、slug conflict、workspace_defaults 重复插入、TOML 驱动断言、Core snapshot、ToolChain probe.txt）不得而知
5. **SQLite DateTimeOffset 数据库排序遗漏检查**未做（阶段4.3）

### 关键架构事实（执行时需要）

- SettingsPage 的 `agents.value = directory.map(...)` 是**页内 View Model 投影**（新目录 DTO → 旧扁平形状），不是 API 兼容层；API 契约已切换为 `listAgents(): AgentDirectoryItemDto[]`。清理方式是重命名/收编前端投影类型，不是恢复旧 API。
- SettingsPage 中的 `'cli'`/`'acp'`（372-373、2175、2286 行）是 Model Center 的 provider connection_kind 分类（CLI/ACP provider instance tab），**不是旧策略种类**，属于合法保留，不要误删。
- Memory 的 `AgentProfileId` 是 memory 记录关联 agent 的持久化字段属性名；DB 列名（小写 snake_case）不在 rg 验证模式的匹配范围（模式大小写敏感，仅 `AgentProfile` PascalCase）。
- Git：39 个已修改文件 + 新文件全部未提交；Mimosa 门禁默认不提交，本计划只改代码不提交。

## 剩余工作步骤

### 步骤 1：Core 术语收尾

1. `AgentProfilePromotionDisabledException` → `AgentPromotionDisabledException`：
   - `TinadecCore/DmaEA/AgentInstanceService.cs:118-124` 类定义重命名（含两个构造函数）
   - `TinadecCore/Api/Endpoints/DmaeaEndpoints.cs:483` catch 类型同步
2. Memory `AgentProfileId` → `AgentId`：
   - 先读 `MemoryDbContext.cs:155-170`、`MemoryModuleRegistrar.cs:180-240`、`ILongTermMemoryService.cs` 确认是实体属性/接口参数/调用点
   - C# 标识符统一改名；若 `AgentProfileId` 是 EF 实体属性，用显式 `HasColumnName` 保留现有列名（避免 Memory 数据迁移；小写列名不触发验证标准）
3. 更新 `TinadecCore/AGENTS.md`：
   - 修正 229/234/238 行过时表述（agent profile → agent directory；AgentProfileRecord.Layer → 目录/resolver 语义；删除 "Gateway sessionMapper 转发 meeting_provider_id" 改为 meeting_model_override）
   - CURRENT STATE 追加一条 2026-08-27 条目：模型策略 `inherit|route|fixed`、有序 Route candidates、`IAgentModelResolver`、`model_invocations` 归因、`meeting_model_override`、Agent 目录（bootstrap|pack|custom|missing_reference）、`RetireAgentProfiles` 迁移、新端点（agents 目录/缺失引用/模型预览/反向引用/调用记录）
4. SQLite DateTimeOffset 检查：在 `TinadecCore` 源码中 grep `DateTimeOffset` + `OrderBy`，确认无遗漏的 DB 侧排序（既有修复模式：先 `ToListAsync()` 再内存排序）。

### 步骤 2：Desktop 旧类型清理

1. `api.ts`：
   - 删除 `getAgentCatalog()`（:1721 附近，死代码）
   - `AgentProfileDto` 投影类型重命名为 `AgentViewDto`（或并入 `AgentCenterAgentDto`，以改动最小者为准），同步 `:844`、`:953` 注释
   - `promoteAgentCandidate`（:2105）返回类型改为 Core 实际返回的 proposal 形状（对照 `EvolutionEndpoints.cs` `ToProposal`：`promoted_agent_id` 等字段；必要时在 api.ts 补 `AgentEvolutionProposalDto` 类型）
2. `SettingsPage.vue`（13 处）、`AgentTopologyCanvas.vue`（3 处）、`mockData.ts`（4 处）：同步重命名引用；`cloneAgentProfile`/`saveAgentProfile` 等函数名同步去 "Profile" 化（如 `cloneAgent`/`saveAgent`）
3. 重新生成 `generated/client.ts`：
   - 后台启动 Core（48731）→ Gateway（48730），`npm --prefix apps/desktop run generate:client`，确认 `client.ts` 中 `meeting_provider_id` 消失，然后停掉两个服务
   - 注意终端纪律：长时间命令后台启动；构建/测试通过 `scripts/setup-dotnet-env.ps1` 包装

### 步骤 3：最终验证（按序执行）

```powershell
# 先停 dev server 防止 dll 锁（48731 / 48730 / 5173）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 build TinadecCore/TinadecCore.slnx --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 test TinadecCore/tests/TinadecCore.Api.Tests/TinadecCore.Api.Tests.csproj --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 test TinadecCore/tests/TinadecCore.AgentFramework.Tests/TinadecCore.AgentFramework.Tests.csproj --no-restore
npm --prefix TinadecGateway test
npm --prefix apps/desktop test
npm --prefix apps/desktop run build
```

### 步骤 4：修复验证中暴露的问题

- Core Api.Tests 若仍有失败，按原计划阶段4.1 清单处理：Pack 安装后 Agent 数量（14 → 新目录 28）、Pack/custom 同 slug conflict 旧断言、bootstrap 自动创建 workspace_defaults 重复插入、`ApplicationModes_AndAgentModes_AreTomlDriven` 旧 TOML 假设、Core OpenAPI snapshot 漂移（重生成）、ToolChain `probe.txt`（调查 worker 是否实际调用、新模型 resolver/fallback 是否吞掉工具调用）
- Gateway / Desktop 测试失败：对照新契约修正（Gateway 快照如需重生成，启动 Core+Gateway 后按其测试脚本流程处理）
- Desktop build 失败：修复类型/模板编译错误
- 必要时后台启动 dev server 做宽/窄窗口 Settings 实际渲染检查，完成后停止

## 验证标准（全部满足才算完成）

1. Core / Gateway / Desktop 全部测试通过（Desktop 允许既有 14 个 NotificationIslandHost 环境性 skip）
2. OpenAPI snapshot（Core + Gateway）重新生成并通过校验；`generated/client.ts` 与 Gateway 实际契约一致
3. `rg "AgentProfile|parent_select|provider_auto|meeting_provider_id|agents/catalog"` 在源代码（排除 `**/Migrations/**`、`**/obj/**`、`**/bin/**`、`TinadecCore/Api/data`、`.workbuddy`、`.zcode`、`.mimosa`、`src/generated`）中无残留；`src/generated` 单独通过重新生成后无 `meeting_provider_id`
4. Desktop `npm run build` 成功

## 约束

- Core 是唯一事实源；Gateway 只做代理；Desktop 不推导绑定
- 不考虑历史与老版本，不保留任何兼容
- 不修改无关未跟踪数据：`.workbuddy`、`.zcode`、`TinadecCore/Api/data`、`TinadecGateway/.mimosa`、`.trae-tmp-analyze.ps1`
- 保留用户已有修改，不回退、不 reset、不 force push；Mimosa git 门禁：默认不提交（除非用户要求）
- 构建前停止 dev server（Core 48731 / Gateway 48730 / Vite 5173 端口）
- 长时间命令后台启动；构建/测试通过 `scripts/setup-dotnet-env.ps1` 包装

## 风险

- Core 6 个失败测试的重写质量未知，可能需要 1-2 轮修复迭代（尤其 ToolChain probe.txt 涉及真实子进程与全双工链路）
- SettingsPage.vue 约 3000 行、13 处类型引用的重命名可能牵出模板编译错误，需 Desktop build 兜底
- `generated/client.ts` 重新生成需要本地起 Core+Gateway，注意先停后启、用完即停
