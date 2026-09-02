# M8 收尾计划：无人值守泳道真实链路 E2E、文档同步、全量回归与提交

## 概要

交接状态：M8①–⑤ 已完成且测试全绿（解冻变更泳道、lane\_key 管道、auto\_policy 端口、park\_expired 消费、每泳道 planner）。剩余三件事：

1. **M8⑥**：修复并跑通 `UnattendedLaneEndToEndTests`（已起稿、未编译、未跑通，已知 7 个待修问题）。
2. **M8⑦**：文档同步（产品定义文档 §6.4.1/§10.5 + 两份 AGENTS.md 追加 M8 记录）。
3. **全量回归 + 提交**（按 M5/M6 惯例拆两个提交：代码一个、docs 一个）。

## 现状分析（已核实）

- 测试文件已存在：[UnattendedLaneEndToEndTests.cs](file:///c:/git/agent/TinadecOffice/TinadecCore/tests/TinadecCore.Api.Tests/UnattendedLaneEndToEndTests.cs)，三个 `[RequiresTinadecToolsFact]` 用例（full-access / pre-auth / auto\_policy），脚本客户端按任务标题路由 worker 工具调用（`运行测试`→shell、`提交变更`→git\_commit），断言真实落盘（feature.txt 存在、`git log` 恰好 1 个提交、subject 为 "M8 unattended commit"）。

- 工作区 git status：M8 改动全部未提交（17 个修改 + 4 个新文件），另有与本任务无关的 `apps/desktop/*` 与 `.claude/launch.json` 改动（**不得顺带提交**）。HEAD = `8d54a15`。

- 已核实的支撑事实：

  - `FullDuplexFactory` 注入 runtime toml 的方式：写 toml 到临时文件 + `TinadecAgent:ProfileConfigPath`（[FullDuplexEndpointTests.cs:1892-1897](file:///c:/git/agent/TinadecOffice/TinadecCore/tests/TinadecCore.Api.Tests/FullDuplexEndpointTests.cs#L1892-L1897)）；完整 baseline toml 模板见 `RuntimeTomlWith`（[805-815 行](file:///c:/git/agent/TinadecOffice/TinadecCore/tests/TinadecCore.Api.Tests/FullDuplexEndpointTests.cs#L805-L815)），`[orchestration]` 段见 `LanesEnabledToml()`（[1241-1244 行](file:///c:/git/agent/TinadecOffice/TinadecCore/tests/TinadecCore.Api.Tests/FullDuplexEndpointTests.cs#L1241-L1244)）。

  - 默认 toml `lanes_enabled = false`（default-agent-runtime.toml:25），**必须**自定义 toml 才能开泳道；`max_agents_per_run` 已是 16。

  - 门控评审的 instructions 同时含「任务规划智能体」与「门控评审」（[FullDuplexRunEngine.cs:1034](file:///c:/git/agent/TinadecOffice/TinadecCore/DmaEA/FullDuplexRunEngine.cs#L1034)）——但当前草稿的 `LaneOpenLine` **不带 waits**，lane 不会进 gate\_review，无需 gate 分支（详见决策 D1）。

  - lane planner instructions 末尾追加 `Lane: {lane_key}`（[FullDuplexRunEngine.cs:3146-3150](file:///c:/git/agent/TinadecOffice/TinadecCore/DmaEA/FullDuplexRunEngine.cs#L3146-L3150)），脚本客户端按此路由到 `LanePlan`——已可用。

  - 三条放行路都已接线：`ToolApprovalCoordinator.TryMintPreAuthorizedApprovalAsync`（full-access → pre\_authorized → auto\_policy，[ToolApprovalCoordinator.cs:295-448](file:///c:/git/agent/TinadecOffice/TinadecCore/Lifecycle/ToolApprovalCoordinator.cs#L295-L448)），`approval.pre_authorized_minted` 事件带 `source`（[ToolDispatcher.cs:167-170](file:///c:/git/agent/TinadecOffice/TinadecCore/Tools/ToolDispatcher.cs#L167-L170)），auto\_policy 附带 `approval.auto_decided`。

  - 工具契约：`shell` = `{command}`，risk "high"（ShellTool.cs:36,41）；`git_commit` = `{message, include_all, confirm_commit}`，`RequiresApproval=true` → risk "high"（GitCommitTool.cs:37）。

  - 预授权 grant 匹配：运行级 grant（LaneKey null）覆盖所有 lane，lane 绑定 grant 精确匹配且优先（ToolApprovalCoordinator.cs:337-341）。

  - `permission_mode: "full-access"` 是合法冻结模式（FrozenRunConfiguration.cs:418-425）。

  - `JsonCanonicalizer`（internal，AgentConfiguration 的 InternalsVisibleTo 覆盖 Api.Tests）、`TestModelSecretStore`、`RequiresTinadecToolsFact`（探测测试输出目录的 TinadecTools.exe）均存在可用。

  - TinadecTools.exe 由 Api.Tests 的条件 ProjectReference 复制到测试输出；进程管理器探测 `AppContext.BaseDirectory`（TinadecToolsProcessManager.cs:64）。

  - 工具链 E2E 的成熟参照：`ToolChainEndpointTests.WorkerWriteFile_RequestsApproval_ApprovedThenExecuted_RunCompletes`（同工厂形态、真进程、真审批恢复）。

  - OpenAPI 漂移已由 Api 套件内 `CoreOpenApiSnapshotTests` 把关；M8② 的 `LaneKey`/`ParkExpired` 均 `[JsonIgnore]`，理论零漂移，无需另跑 npm drift（交接里的 `npm --prefix TinadecGateway run check:drift` 不存在，正确位置是 apps/desktop，且与 Core DTO 无关）。

  - 已知抖动：`Supervision_ReviseThenPass_ReExecutesTasks`（单独跑通过）；全量并行跑时 ToolChain/FullDuplex/CliRuntime/TinadecToolsProcess 存在跨类干扰史——失败用例先单独复跑再定性。

## 改动方案

### 一、M8⑥ 修复并跑通 E2E（唯一代码改动文件：`TinadecCore/tests/TinadecCore.Api.Tests/UnattendedLaneEndToEndTests.cs`）

**修复 1 —— 工厂注入 runtime toml（交接问题 1）**

在测试类加常量（镜像 `RuntimeTomlWith("enabled = false\n…")` + orchestration 段）：

```csharp
private const string UnattendedRuntimeToml =
    "schema_version = 1\n\n"
    + "[spawn]\nmax_depth = 2\nmax_agents_per_run = 16\nmax_parallel_workers = 4\n\n"
    + "[scheduling]\nmax_active_runs_per_session = 2\nworker_retry_limit = 2\npreserve_partial_results = true\n\n"
    + "[supervision]\nrequired_before_final = true\nmax_revision_rounds = 2\n\n"
    + "[context]\ndefault_token_budget = 8192\nrecent_message_limit = 24\noptimistic_revision = true\n\n"
    + "[memory]\ncandidate_only = true\nretrieval_limit = 8\n"
    + "allowed_scopes = [\"principal\", \"workspace\", \"project\", \"agent\"]\n"
    + "allowed_kinds = [\"fact\", \"preference\", \"decision\", \"success_pattern\", \"failure_pattern\", \"task_template\", \"supervision_rule\"]\n\n"
    + "[tools]\nprovider = \"tinadec-tools-process\"\nmutation_requires_approval = true\nserialize_workspace_writes = true\ndefault_timeout_seconds = 120\nmax_tool_rounds = 4\n\n"
    + "[triggers]\nenabled = false\ncontext_token_threshold = 0\ncompress_on_task_closed = false\nrecommend_on_task_created = false\ncurate_on_run_closed = false\ngit_steward_on_run_closed = false\n\n"
    + "[orchestration]\nlanes_enabled = true\nmax_lanes_per_run = 4\nmax_tasks_per_lane = 6\n";
```

`UnattendedFactory.ConfigureAppConfiguration` 中，构造 `values` 前写文件并加键：

```csharp
var tomlPath = Path.Combine(_root, "unattended-agent-runtime.toml");
File.WriteAllText(tomlPath, UnattendedRuntimeToml, Encoding.UTF8);
// values 里追加：
["TinadecAgent:ProfileConfigPath"] = tomlPath
```

（`_root` 每个测试实例唯一，无跨用例覆盖问题。）

**修复 2 —— meeting 首轮流 LANE\_OPEN（交接问题 2）**

删除 `_meetingOnce` 字段，改用一次性标志：meeting 分支（含 `LaneOpenLine` 的说明 turn）首轮返回 `LaneOpenLine`，之后返回 `"全部完成。"`（run 收尾的 meeting 终结）：

```csharp
private int _meetingOnceUsed;
// meeting 分支内：
var first = Interlocked.Exchange(ref _meetingOnceUsed, 1) == 0;
return new ChatResponse(new ChatMessage(ChatRole.Assistant, first ? LaneOpenLine : "全部完成。"));
```

（时序已核实：clarification turn 是第一次 meeting 调用，run 终结是第二次——与 M4 测试 `WhenMeetingOnce` 同构。）

**修复 3 —— 删完成桩（交接问题 3）**

- 删除 `FinishReason`（恒返 "completed" 的假断言）。

- `AwaitCompletionAsync` 改签名 `Task AwaitCompletionAsync(HttpClient client, Guid runId)`，去掉 `script` 参数与返回值，内部轮询 + `Assert.True(status == "completed", …)` 保持。

- 三个用例删掉 `var chunks = …` 与 `Assert.Equal("completed", FinishReason(chunks))`，直接 `await AwaitCompletionAsync(client, runId);`。

**修复 4/5 —— 门控：采用简化路线（交接问题 4、5，见决策 D1）**

- 保持草稿现状：`LaneOpenLine` **不带 waits** → lane 开启即派发，不进 gate\_review，**不加** gate 分支。

- MainPlan 保持单任务「开发功能」（worker 门控时序已保证指令在 main 执行期入队，消费在引擎下一 tick，M4 的 `MeetingDirective_LaneOpenConsumedByTargetRun` 已证明该时序可靠）。

**修复 6 —— 清理死代码（交接问题 6）**

- 删 `WhenSupervisor` 方法与 `_supervisorVerdicts` 队列；监督分支内联默认裁决 `{"decision":"pass","reasons":[],"revise_task_indexes":[]}`。

- `ReplayEventsAsync` 去掉 `script` 参数（调用点 `script: null!` 一并去掉）。

- `StartUnattendedRunAsync` 返回元组缩为 `(HttpClient Client, TaskCompletionSource WorkerGate, Guid RunId, string Workspace)`（去掉外部不用的 `script`、`workerStarted`），三个用例同步解构。

- `ActiveInvoke`/`StreamInvokeAsync`/`StartStreamingInvoke` 保持（clarification 断言要用）。

**修复 7 —— 编译 + 跑通（交接问题 7，预计有迭代）**

```powershell
# 1) 编译整个 Core 子解决方案（含 Api.Tests 及其条件引用的 TinadecTools）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 build TinadecCore/TinadecCore.slnx
# 2) 只跑新 E2E（3 个用例）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 test TinadecCore/tests/TinadecCore.Api.Tests/TinadecCore.Api.Tests.csproj --no-build --filter "FullyQualifiedName~UnattendedLaneEndToEndTests"
```

若失败按报错迭代修（交接 7 项是已知集，不排除运行期再暴露时序/选路问题；失败时先看 `orchestration` 投影与事件流定位是哪一环）。修完必须保持 `FullDuplexEndpointTests`、`ToolChainEndpointTests`、`PreAuthorizationMintTests`、`ApprovalWindowTests` 既有用例零回归（全量回归覆盖）。

### 二、M8⑦ 文档同步

**1.** **[docs/tinadec-core-product-definition.zh-CN.md](file:///c:/git/agent/TinadecOffice/docs/tinadec-core-product-definition.zh-CN.md)**

- §6.4.1（现 354-361 行）：在「可观测投影」条目之后、「lane 结构是引擎内部调度状态…」之前插入两条：

  - **泳道规划智能体实例**：goal-only `LANE_OPEN`（只带 `goal`，`tasks` 可选）把 lane 打开在 `planning` 相位，由该 lane 专属任务规划实例派生任务图（`LanePlannerIds` 映射、指令携带 `Lane: {lane_key}`、事件 `orchestration.lane_planning` → `orchestration.lane_planned` 含 `planner_instance_id`/`task_count`）；任务预算取 per-lane 冻结上限，run 级 spawn 预算 8→16；门控评审路由到该 lane 自己的规划实例（只能收紧）。

  - **变更泳道授权条件**：声明变更工具的 lane 准入须带 `pre_authorization` 引用或冻结为 `full-access`；执行期放行仅三条路（full-access 自动铸造 / 预授权 grant——运行级覆盖全 lane、lane 绑定精确匹配优先 / 策略自动批准），均未命中则泊车 `approval.park_expired` 并冻结 lane 升级 `awaiting_user`；lane 工具执行携带内部 `lane_key`（不进 HTTP DTO）。

- §10.5（现 634-657 行）：

  - 改写 656 行「策略自动批准尚未接入 Lifecycle 工具审批链」为已闭环：`IToolApprovalAutoPolicy` 端口（Abstractions）+ Governance 纯规则实现（`ToolApprovalAutoPolicy`/`AutoApprovePolicyRules`），协调器在 full-access 与预授权未命中时调用，`approval.auto_decided` 留痕；预算按调用方各自决策记录计数（Governance 权限路 / Lifecycle 工具路两套独立、不跨库共享），耗尽升级不拒绝。

  - 「残余风险」追加四条诚实缺口：① 真实模型（非 scripted）全链路 live smoke 未做（E2E 为 scripted 模型 + 真实 TinadecTools 子进程，需配 API key 另跑）；② RiskRank 取保守路径——共享规则对 `elevated` 按不可识别处理（永不自动批准）、权限请求通路 RiskRank 未改（零行为变化），方案中「统一采用工具审批链版本」未做；③ `HumanOnlyTools` 含 `command_run` 而实际命令工具 id 是 `shell`（语义未改，记录观察）；④ 默认 `AutoApproveRiskMax=medium` 不放行 high 风险变更工具（shell/git\_commit），无人值守提交需显式抬到 high（E2E 已验证必须显式配置）。

**2. AGENTS.md（根目录 + TinadecCore/，按各自文件头部 AI MAINTENANCE PROTOCOL）**

- `TinadecCore/AGENTS.md`：CURRENT STATE 列表 M6 条目后追加 M8 条目（落点 + 测试计数 + 诚实缺口，内容同上四条 + 21 文件落点概述 + 三 E2E 用例名）；`NOT IMPLEMENTED THIS ROUND` 补「真实模型 live smoke（scripted E2E 已覆盖治理链路）」。元数据：`Last Updated: 2026-09-03`、`Last Updated By: Trae (GLM-5.3) — M8 unattended lane closure`、`Last Verified Commit: <提交①的短哈希>`、`Branch: main`。

- 根 `AGENTS.md`：CURRENT REBUILD STATE 的 M6 条目后追加较短的 M8 条目（跨引 TinadecCore/AGENTS.md），元数据同步更新（同上）。

### 三、全量回归 + 提交

**回归（提交前必跑）**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-dotnet-env.ps1 test TinadecCore/TinadecCore.slnx --no-build
```

三套件（AgentFramework / Api / Governance）全绿为过；`Supervision_ReviseThenPass_ReExecutesTasks` 为已知抖动（单独复跑通过即不算回归）；新 E2E 缺 TinadecTools.exe 的环境靠 `[RequiresTinadecToolsFact]` 干净跳过。记录最终测试计数写进 AGENTS.md。

**提交（两个，沿用 M5/M6 惯例：代码一个、docs 一个，解决“Verified Commit 哈希先有鸡还是先有蛋”）**

- 提交①（代码 + 测试，含 M8①–⑤ 全部未提交改动 + E2E 测试文件）：

  ```
  M8: unattended lane闭环（解冻变更泳道/lane_key/auto_policy/park_expired/per-lane planner）

  - ① 变更泳道解冻：FrozenPermissionMode 进 OrchestrationDirectiveRules，preauthorization_unavailable 无条件拒绝删除
  - ② lane_key 管道：ToolExecutionRecord.LaneKey + EF 配置；ToolDispatchRequestDto.LaneKey（内部）；grant 匹配放宽为运行级或泳道精确匹配（泳道优先）；审批事件带 lane_key
  - ③ auto_policy 端口：IToolApprovalAutoPolicy（Abstractions）+ ToolApprovalAutoPolicy/AutoApprovePolicyRules（纯规则）；协调器第三通路；approval.auto_decided
  - ④ park_expired 消费：ParkExpired 标志 + 引擎 EscalateLaneAsync 冻结泳道
  - ⑤ 每泳道 planner：goal-only 指令（tasks 可选）+ planning 相位 + 泳道专属规划实例 + lane_planning/lane_planned 事件 + 预算 8→16
  - ⑥ 真实链路 E2E：UnattendedLaneEndToEndTests 三用例（full-access/pre-auth/auto_policy）经真实 TinadecTools 子进程 + 真实临时 git 仓提交

  诚实缺口：真实模型（非 scripted）live smoke 未做；RiskRank 取保守路径（elevated 永不自动批准）；Governance/Lifecycle 两套 auto_policy 预算独立不跨库。
  ```

  文件清单（`git add` 逐个列出，**不含** `apps/desktop/*`、`.claude/launch.json`）：TinadecCore 下 17 个修改 + 4 个新文件（`IToolApprovalAutoPolicy.cs`、`AutoApprovePolicyRules.cs`、`ToolApprovalAutoPolicy.cs`、`UnattendedLaneEndToEndTests.cs`）。

- 提交②（docs）：`docs/tinadec-core-product-definition.zh-CN.md`、根 `AGENTS.md`、`TinadecCore/AGENTS.md`；元数据里 `Last Verified Commit` 填提交①的短哈希；提交信息风格沿用 `docs: M8 收口 — …`。

## 假设与决策

- **D1（门控简化）**：lane 不带 waits、不加 gate 分支、MainPlan 保持单任务。依据：交接明确“可接受”；gate 路由已由 M8⑤ 的 `GoalOnlyLane_GateReviewRoutesToItsOwnPlannerInstance` 覆盖；E2E 的目的是验证三条放行路驱动真实工具，门控与之正交。带 waits 版本需要双串行主任务 + 监督裁决证据链，脆且与目标无关。

- **D2（提交拆分）**：代码与 docs 分两个提交（repo 惯例：M5 `8d0394a`+`facceb7`、M6 `994a1a6`+`0965e58`），并使 AGENTS.md 的 `Last Verified Commit` 能如实指向已验证的代码提交。

- **D3（OpenAPI drift）**：不跑 npm drift 检查——Core 快照已由 `CoreOpenApiSnapshotTests` 在 Api 套件内把关，且 M8② DTO 均 `[JsonIgnore]`；交接给的 `npm --prefix TinadecGateway run check:drift` 命令本身不存在（该 script 在 apps/desktop，且校验的是 Gateway 快照）。

- **D4**：不改 M8①–⑤ 任何已验证代码；若 E2E 暴露产品代码缺陷，先评估最小修复并在 AGENTS.md 记录，不顺手重构。

- 前提：本机 monorepo 布局（TinadecTools.exe 可达）、`git` 可用、测试环境 Windows（shell 走 `cmd.exe /c`）。

## 验证步骤

1. `build TinadecCore/TinadecCore.slnx` 零错误。
2. `--filter "FullyQualifiedName~UnattendedLaneEndToEndTests"`：3/3 通过（真实子进程写入 feature.txt + 真实 git 提交 "M8 unattended commit"、`rev-list --count HEAD`==1；full-access 无 `approval.auto_decided`；auto\_policy 有）。
3. 全量 `test TinadecCore/TinadecCore.slnx --no-build`：三套件全绿（允许已知抖动单独复跑定性），记录计数。
4. `git status` 确认两个提交只含计划内文件，`apps/desktop/*` 与 `.claude/launch.json` 保持未提交。
5. `git show --stat` 复核提交①文件清单 = 21 个 M8 文件；提交② = 3 个文档文件。

