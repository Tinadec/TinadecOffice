# 计划书④：Best-of-N — N 个隔离尝试并行、diff 对比、择优合入

> 状态：草案 v1（2026-11-02），来源：Qoder/Codex 竞品对比（当日会话），**未登记**进 §11/backlog，待批准后登记。
> 判据沿用既有差距报告 A/B/C 三级；每批先红后绿+变异对照，收尾必须过 live 验收。

## 0. 结论一句话

对标 OpenAI Codex 的并行尝试（同一任务 N 路并行跑在隔离沙箱，diff 对比后择优合入）。本仓已有三块关键积木：`dispatch_mode=parallel` 真多 run 准入、`git_worktree_create/remove` 工具（限 `.tinadec/worktrees`）、快照逐文件评审协议（`expected_sha256`）——但**所有 run 共享同一个项目根目录**这一个结构性事实，让 N 个尝试今天在文件层互相踩踏、diff 无法按 run 归属。本计划的核心就是把"一个尝试=一个隔离目录+run 级快照归属"的地基打出来。

## 1. 背景与对标

- Codex：并行分支尝试（git worktree 工作流）→ 多 attempt 并存 → review 后应用最优结果。
- 本仓现状：parallel dispatch 已是**真多 run 并行**（`InteractionsEndpoints.cs:419` admissionStatus="assigned"，仅受 `MaxActiveRunsPerSession` 限流，`FullDuplexRunCoordinator.cs:210-215`，超限 409 不排队）——但那是"N 个 run 抢同一个可写世界"，不是"N 个隔离尝试"。
- 竞品对比初版称"Best-of-N 无任何积木"不准确：积木比预想多，缺的是把它们钉在一起的绑定层。

## 2. 现状与证据（2026-11-02 实测）

**可直接复用**
1. parallel 多 run 准入 + `MaxActiveRunsPerSession` 预算（`InteractionsEndpoints.cs:58-59,419`；`FullDuplexRunCoordinator.cs:210-215`）。
2. `git_worktree_create/remove` 工具已存在且**路径已约定** `.tinadec/worktrees`（`TinadecTools/Tools/Git/GitWorktreeMutationTools.cs:42,96`）；branch 工具族（`GitBranchTools.cs:44-56`）。
3. 快照评审协议：三端点+文件级 `.../files`、`.../files/diff`、`.../files/restore`（`WorkspaceSnapshotEndpoints.cs:14,58,75,99,137`），manifest(sha256)+小文件全文入 ContentStore（`WorkspaceSnapshotService.cs:261-281`），单文件撤销带 `expected_sha256` 乐观守卫（:425-434）。
4. 两方 diff 底座：`gitDiffCompare(base_ref, head_ref)`（`apps/desktop/src/api.ts:3633`）+ `CommitCompare.vue`；WorkbenchPage 已挂 TaskGraphPanel/RunLaneCanvas/lineage（`WorkbenchPage.vue:9-10,41-79`）。
5. 审批链一键批准惯例（`useGitOperation.ts:1133-1150`）。

**结构性障碍（本计划要拆的那堵墙）**
- run 的工作区绑定直接绑 `project.RootPath`（`WorkspaceBinding.cs:26-31`）；TinadecTools 进程按 `StorageId+WorkspaceRoots` 哈希复用（`TinadecToolsProcessManager.cs:163`）——N 个 run 共享一个工具进程、一个根目录。
- 快照端点全挂 **projectId 而非 runId**（`WorkspaceSnapshotEndpoints.cs:15,39`）——两个并行 run 的写入落在同一条快照流，无法按 run 归属。
- 写冲突唯一防线是 `write_file` 的 file_hash 乐观锁（`FileSystemTools.cs:176-179`）：后写者被拒——是碰撞检测，不是隔离。
- lane 机制运行时不可达（`FullDuplexRunEngine.Lanes.cs:14-17`、`RunFreezeGate.cs:85-87` `graph_tier_lanes_unsupported`）——本计划绕开它（不依赖 lane）。

## 3. 目标 / 非目标

**目标**
1. 一次"Best-of-N 请求"=创建 N 个 git worktree→每 attempt 一个 run、各自绑定 worktree 根→并行执行→各自产出可归属的快照/diff。
2. 评审 UI：N 路 diff 并排（相对 merge-base），择优合入。
3. 合入 v1 语义：整 attempt 合入 + 逐文件剔除（不做跨 attempt 拼接）。
4. 未选 attempt 的确定性清理（worktree remove+快照退役），主工作区全程不被尝试污染。
5. 快照端点增加 run/attempt 维度（向后兼容）。

**非目标**
- 不解 lane/graph 解冻（`graph_tier_lanes_unsupported`）——尝试并行用多 run，不用泳道。
- 不做跨 attempt 变更子集拼接（v2 再议）。
- 不做无 git 仓库的目录复制版 fallback：**v1 明确 Best-of-N 仅对 git 工作区开放**，非 git 工作区入口禁用并给结构化原因（成本与一致性理由，宁可功能面窄）。
- 不做模型自动评分择优（依赖真实模型验证，v1 人工择优；v4 可选批再议）。

## 4. 设计方案

### 4.1 尝试隔离：run → worktree 绑定
- `WorkspaceBinding` 扩展：run 准入时可指定 attempt 根（`<workspace>/.tinadec/worktrees/<attemptId>`，正好落在既有工具的路径约定内）；工具层 `write_file`/列目录的根解析跟随绑定——run 的全部写面被钉进 worktree。
- TinadecTools 进程键加 attempt 维度（`TinadecToolsProcessManager.cs:163` 的 `StorageId+WorkspaceRoots` 哈希扩为含 attempt 根），避免 N 个尝试共享一个工具进程的状态串扰。
- 读面：主工作区只读、worktree 可写——ACL/路径纪律复用既有 EnsureNoLinkTraversal 与工作区外拒绝规则。

### 4.2 编排器
- `BestOfNOrchestrator`（Core 侧）：受理请求(N≤MaxActiveRunsPerSession)→逐 attempt：`git_worktree_create`（走会话工具进程，审批链原样）→admission 派发 run（同一 spec/验收文本）→收割终态。
- attempt 生命周期状态进既有投影（session projection 已带 run 元数据，`WorkspaceSnapshotEndpoints.cs:176-183` 同域），不建新状态机——BestOfN 请求是 run 的编组，不是第三层实体。
- 失败语义：单 attempt 失败不取消兄弟 attempt；全部失败/全部成功才进评审态。

### 4.3 快照与 diff 归属
- `workspace-snapshots` 端点族加可选 `runId`/`attemptId` 查询维度（挂 projectId 的既有路由向后兼容，新增过滤参数）；attempt run 的快照行带归属，主工作区快照流不受污染。
- diff 基准统一为 merge-base(主分支, attempt 分支)——`gitDiffCompare` 两方底座直接吃这个 ref 对。

### 4.4 择优合入
- v1：整 attempt 合入 = 选中 attempt 的变更文件集按快照 restore 语义写回主工作区（带 `expected_sha256` 逐文件守卫，主工作区冲突文件被拒并列出——不静默覆盖）；"剔除某文件"=从变更集排除后同链路执行。
- 合入本身走治理写（审批卡片列文件清单），复用 git 工具族而非绕过它。
- 清理：未选 attempt 走 `git_worktree_remove`（已有工具）+快照退役；评审放弃=全量清理。

### 4.5 UI
- WorkbenchPage 新容器 `BestOfNPanel`：N 列 diff 并排 + 每列 attempt 摘要（终态、token 用量——`model-invocations` 读面直接复用）+择优/剔除/放弃按钮。
- 多标签协调：现有 BroadcastChannel（`graphSeedPackBootstrap.ts:69-94`，state_request/handled 握手形状）保证两窗不开同一 attempt 的评审态。

## 5. 分批落地与验收

| 批 | 内容 | 验收门禁（先红后绿/变异） |
|---|---|---|
| B1 | 地基：run 级 worktree 绑定 + 工具进程键扩展 + 快照 runId 维度 | 先红后绿的天然用例：现状下两 run 并行写同路径后者被 hash 拒（红）→隔离后各自成功（绿）；变异：把绑定回退到 RootPath→恰红 |
| B2 | BestOfNOrchestrator（建树→派发→收割）+ 端点 + 双 OpenAPI 快照同批 | e2e：N=2 spec→两 attempt 终态→快照可按 attempt 读；单 attempt 失败不传染；live 验收 |
| B3 | N 路对比 UI + 择优合入/剔除/放弃/清理 | 合入变异：篡改主工作区文件后合入→expected_sha256 拒绝并列表；放弃后 worktree 与快照确实退役（残留检测用例） |
| B4（可选） | 跨 attempt 拼接、模型按验收文本评分辅助 | 独立评审；依赖真实模型冒烟结论 |

## 6. 风险与开放问题

1. **大仓 worktree 成本**：worktree 共享 .git 对象但检出整树，Windows 路径长度（`.tinadec/worktrees/<id>/` 嵌套加深）要防 MAX_PATH——attemptId 用短随机段（对齐 `TinaSbx_<sha256_12>` 的命名惯例）。
2. 每工作区一工具进程的假设被打破：进程键扩展要回归验证既有复用行为（同 attempt 的调用仍复用同进程）。
3. git 边缘（LFS、子模块、sparse-checkout 的仓库）：v1 检测到即拒绝并给结构化原因，不硬扛。
4. token 成本翻 N 倍：BestOfNPanel 摘要必须展示每 attempt 用量（复用"缺字段渲染成'未报告'而不是 0"的既有纪律），让用户在按下"开 N 路"前看得到代价。
5. 开放问题：N 的默认值与上限是否并入 manifest/包络体系（DmaEA 资源包络先例）——倾向 v1 只用 `MaxActiveRunsPerSession`，v2 再并入。

## 7. 与既有登记项的关系

- B1 的"run 级绑定"对 Quest（计划书②）同样有用（Quest 多阶段若要隔离写面），两者共用地基、互不阻塞。
- 与 git 模块路线的关系：`docs/git-module-status-and-roadmap.md` 自标"⚠️历史文档，不可作为事实源"——本计划不引用其能力表，以代码为准；建议该文档随 B1 同批刷新或标注退役。
- 与 §11 登记项无依赖冲突。
