# TinadecOffice Git 模块状态与路线图

**文档版本**：2.2  
**更新日期**：2026-06-26  
**覆盖范围**：`apps/desktop`、`gateway`、`src/TinadecCore` 三层 Git 相关实现

---

## 1. 已完成的功能和改进项

### 1.1 核心 Git 只读能力

| 功能 | 实现说明 | 关键文件 |
|---|---|---|
| 工作区状态展示 | 解析 `git status --porcelain=v1 --branch`，输出 branch、upstream、ahead/behind、文件状态 | `gateway/src/codeTools.ts` |
| Diff 预览 | 支持 working tree / staged / branch range 三段 diff，带文件元数据与截断控制 | `gateway/src/codeTools.ts` |
| 提交历史 | 结构化 `git log` 输出，支持日期分组与选中 commit 详情 | `apps/desktop/src/components/git/GitHistoryView.vue` |
| Worktree 列表 | `git worktree list --porcelain` 解析 | `gateway/src/codeTools.ts` |
| Push 就绪检查 | 检测 detached HEAD、no upstream、behind、uncommitted changes 等 blocker | `gateway/src/codeTools.ts` |
| 分支对比 | `diff_compare` action 支持任意两个 ref 的 diff | `gateway/src/codeTools.ts` |

### 1.2 变更操作（Approval-gated）

| 功能 | 实现说明 | 状态 |
|---|---|---|
| Stage / Unstage | `git add` / `git restore --staged`，路径限制在工作区内 | 可用 |
| Commit | 支持 `commit_staged_only` 与 `include_all` 两种模式 | 可用 |
| Push | 支持普通 push 与 `-u origin <branch>` 自动设置上游 | 可用 |
| Pull | `git pull [remote] [branch]`，支持 `--rebase` / `--ff-only` | 可用 |
| Checkout | `git checkout <branch>`，带分支名安全校验 | 可用 |
| Create Branch | `git checkout -b <name>`，自动检查分支是否已存在 | 可用 |
| Fetch | `git fetch --all --prune` 或指定 remote，返回 tracking 信息 | 可用 |
| Merge | `git merge [branch]`，支持 `--no-ff`/`--ff-only`/`--squash` | 可用 |
| Rebase | `git rebase [branch]`，支持 `--continue`/`--abort`/`--skip` | 可用 |
| Resolve Conflict | `git checkout --ours/--theirs/--both` + `git add` | 可用 |
| Delete Branch | `git branch -d/-D <branch>` | 可用 |
| Rename Branch | `git branch -m <new-name>` | 可用 |

### 1.3 分支管理

| 功能 | 实现说明 | 关键文件 |
|---|---|---|
| 真实分支列表 | `branch_list` action 通过 `git branch -a -vv --format=...` 输出所有本地/远程分支 | `gateway/src/codeTools.ts` |
| 远程分支展示 | `GitBranchView.vue` 按 `is_remote` 分组展示本地与远程分支 | `apps/desktop/src/components/git/GitBranchView.vue` |
| 上游跟踪 | 每个分支项显示 upstream 与 `↑ahead ↓behind` | `apps/desktop/src/components/git/GitBranchView.vue` |
| Fetch 同步 | 分支视图工具栏支持一键请求 fetch approval 并执行 | `apps/desktop/src/composables/useGitOperation.ts` |
| 分支删除 | `delete_branch` action，支持 `-d` / `-D` | `gateway/src/codeTools.ts` |
| 分支重命名 | `rename_branch` action，重命名当前分支 | `gateway/src/codeTools.ts` |
| Merge/Rebase 入口 | 分支视图右键菜单可直接发起 merge/rebase approval | `apps/desktop/src/components/git/GitBranchView.vue` |

### 1.4 安全与治理

- Core 拥有 approval 状态，Gateway 执行前查询 Core 校验 `approval_id` 与 `kind=git`。
- **Approval 上下文校验**：校验 `approval.cwd` 与 `request.cwd`、`approval.command` 与请求 action 是否匹配，不匹配返回 `approval:context-mismatch`。
- 路径安全：`normalizeGitPathspec` 使用 `:(literal)` 前缀并限制在 Git worktree 内。
- Ref/remote 名称安全校验：`isSafeGitRefName`。
- 命令超时保护：普通操作 10 秒；`push`/`pull`/`fetch`/`merge`/`rebase` 等长操作 60 秒。

### 1.5 UI 与 AI 雏形

- Git 面板三栏布局：Changes / History / Branches。
- 文件列表、状态徽章、diff 预览、commit message 编辑器。
- 冲突文件置顶高亮与横幅提示；支持 ours/theirs/both 一键解决。
- 一键“批准并执行”：stage / commit / push 等高频操作从 3 次点击降为 1 次。
- 分支视图操作菜单：delete / rename / merge / rebase。
- AI 提交信息生成：基于 diff 摘要 + 文件列表的 prompt，本地启发式 + 流式 AI，conventional commit 校验。
- AI 变更风险分析：新增 `useAiChangeAnalysis`，提供风险等级、影响面、关注点、测试建议，支持本地启发式 + 流式 AI。
- Diff Viewer 视图切换：支持 side-by-side 与 inline 模式切换；Monaco 提供语法高亮。

---

## 2. 尚未完成或待处理的功能和改进项

### 2.1 功能完整性

| 待处理项 | 问题描述 | 优先级 |
|---|---|---|
| `git reset` / `git revert` / `git cherry-pick` | 未实现 | P1 |
| `git stash` 管理 | 未实现 | P1 |
| `git tag` 管理 | 未实现 | P2 |
| `git submodule` | 未实现 | P2 |

### 2.2 分支管理

| 待处理项 | 问题描述 | 优先级 |
|---|---|---|
| Commit graph 可视化 | 仅线性列表，无 DAG 图 | P2 |
| 分支策略提示 | 根据工作流（main/trunk/feature）提示推荐 rebase/merge 策略 | P2 |
| 远程分支检出 | 远程分支点击后直接 `checkout -b <local> <remote>` | P2 |

### 2.3 冲突解决方案

| 待处理项 | 问题描述 | 优先级 |
|---|---|---|
| 冲突文件列表与状态 | 已检测 `UU/AA/DD` 等冲突码并高亮置顶 | 已完成 |
| 冲突解决 UI | 已提供 ours/theirs/both 按钮 | 已完成 |
| Rebase/Merge 冲突流 | 后端已返回冲突状态；UI 需进一步引导 `--continue` / `--abort` | P2 |
| 冲突文件 diff 高亮 | 冲突标记 `<<<<<<<` / `=======` / `>>>>>>>` 需高亮 | P2 |

### 2.4 AI 集成

| 待处理项 | 问题描述 | 优先级 |
|---|---|---|
| 基于 diff 的提交信息 | 已完成；prompt 包含最多 6K 字符的 diff 摘要 | 已完成 |
| 变更风险分析 | 已完成；新增 `useAiChangeAnalysis` 与 GitChangesView 分析面板 | 已完成 |
| 多语言提交信息 | 仅支持英文 | P2 |
| 提交信息风格选择 | 无简短/详细/技术风格选项 | P2 |
| 变更审查（code review）意见 | 未在 commit 前展示 AI 审查卡片 | P2 |

### 2.5 操作效率

| 待处理项 | 问题描述 | 优先级 |
|---|---|---|
| Approval 上下文校验 | 已校验 cwd/command 匹配 | 已完成 |
| 一键“批准并执行” | 已覆盖 stage / commit / push | 已完成 |
| 长操作进度反馈 | push/pull/fetch 大仓库无实时进度指示 | P2 |
| Commit 行为一致性 | `commit_staged_only` 模式已实现；默认行为已明确 | 已完成 |
| 快速操作 | 支持键盘快捷键：Ctrl+Enter 提交、Ctrl+Shift+P push | P2 |

### 2.6 UI 界面

| 待处理项 | 问题描述 | 优先级 |
|---|---|---|
| Hunk 级 stage/unstage | diff viewer 当前为文件级 stage；需精确解析 hunk 并调用 `git apply --cached` | P2 |
| Side-by-side diff | 已完成；DiffViewer 支持 side-by-side / inline 切换 | 已完成 |
| 语法高亮 | 已完成；Monaco diff editor 按文件路径自动检测语言 | 已完成 |
| 分支 ahead/behind 视觉提示 | 当前较简单 | P2 |
| 操作反馈 | 所有 git 操作完成后显示 inline 成功提示，失败时显示可折叠的详细输出 | 部分完成 |

---

## 3. 当前项目状态全面总结

`TinadecOffice` 的 Git 模块已经从 MVP 阶段迈入**“生产可用”阶段**。核心工作流 `status → diff → stage → commit → push → pull → branch → fetch → merge → rebase → resolve-conflict` 已全部在 Gateway 层实现，并通过 Core approval 机制保证安全；Desktop 层提供了三栏面板、真实分支列表、冲突解决、分支管理操作菜单、一键批准执行、AI 提交信息生成、AI 变更风险分析、diff 视图切换等交互体验。P0/P1 级别的功能完整性、分支管理、冲突解决、approval 上下文校验、一键执行效率、AI diff 上下文与风险分析已交付。

当前主要短板集中在**高级撤销操作（reset/revert/cherry-pick/stash）、commit graph 可视化、真正的 hunk 级 stage/unstage、分支 ahead/behind 视觉提示增强**等方面。后续补齐后，Git 模块将具备与 JetBrains / VS Code 基础 Git 能力相媲美的完整体验。

---

## 4. 改进实施计划与可衡量目标

### 4.1 功能完整性优化（第一阶段已完成）

**目标**：让核心变更操作全部可用。

| 任务 | 实施计划 | 可衡量目标 |
|---|---|---|
| pull 执行 | 实现 `executeGitPull` | 已完成；支持 rebase/ff_only |
| checkout 执行 | 实现 `executeGitCheckout` | 已完成；分支名安全校验 |
| create_branch 执行 | 实现 `executeGitCreateBranch` | 已完成；自动检查分支存在性 |
| fetch 执行 | 实现 `executeGitFetch` | 已完成；返回 tracking info |
| merge/rebase | 新增 `merge` / `rebase` action，支持 `--abort` / `--continue` | 已完成 |
| resolve_conflict | 新增 `resolve_conflict` action，支持 ours/theirs/both | 已完成 |
| delete/rename branch | 新增 `delete_branch` / `rename_branch` action | 已完成 |
| reset/revert/cherry-pick | 后续阶段补充 | 覆盖常见撤销操作 |

**验收指标**：
- 10 个核心变更操作端到端可用（已完成）。
- Gateway 端新增 merge/rebase/resolve_conflict 测试覆盖。

### 4.2 分支管理改进（第二阶段已完成）

**目标**：提供真实、完整、可交互的分支管理体验。

| 任务 | 实施计划 | 可衡量目标 |
|---|---|---|
| 真实分支列表 | 已完成；`branch_list` action 替换 worktree 推断 | 本地/远程分支准确展示 |
| 上游跟踪 | 已完成；展示 upstream 与 ahead/behind | 每个分支项显示 `↑N ↓M` |
| 分支删除 | 新增 `delete_branch` action，支持 `-D` / `-d` | 已完成 |
| 分支重命名 | 新增 `rename_branch` action | 已完成 |
| Merge/Rebase 入口 | 分支视图操作菜单直接发起 merge/rebase | 已完成 |
| 分支策略提示 | 根据工作流提示推荐 rebase/merge 策略 | 在分支视图显示策略建议 |
| Commit graph | 使用 canvas/SVG 绘制简化 DAG | History 视图可切换 graph 模式 |

**验收指标**：
- 分支列表加载时间 < 1 秒（普通仓库）。
- 支持本地分支增删改查、远程分支展示、fetch 同步、merge/rebase 发起。

### 4.3 冲突解决方案（第三阶段已完成）

**目标**：建立“检测 → 展示 → 解决 → 继续”的完整冲突处理闭环。

| 任务 | 实施计划 | 可衡量目标 |
|---|---|---|
| 冲突高亮 | 已完成；冲突文件置顶并高亮 | 冲突文件在文件列表顶部醒目展示 |
| 冲突解决 UI | 在 `GitChangesView.vue` 中为冲突文件提供 ours/theirs/both 按钮 | 已完成 |
| 解决后端 | 新增 `resolve_conflict` action：调用 `git checkout --ours/--theirs` 后 `git add` | 已完成 |
| Merge/Rebase 闭环 | 实现 `merge` / `rebase` action；冲突时返回 `conflict: true` 与冲突文件列表 | 已完成；UI 可继续后续操作 |
| 冲突标记高亮 | diff viewer 中 `<<<<<<<` / `=======` / `>>>>>>>` 高亮 | 后续补充 |

**验收指标**：
- 能完成一次产生冲突的 merge，并在 UI 中解决冲突后提交。
- 冲突场景有专门的 UI 状态与明确引导。

### 4.4 AI 集成增强（第四阶段，部分完成）

**目标**：让 AI 成为 Git 工作流中的有效助手，而不仅是提交信息生成器。

| 任务 | 实施计划 | 可衡量目标 |
|---|---|---|
| 基于 diff 的提交信息 | 在 prompt 中加入 working tree diff 摘要（最多 6K 字符） | 已完成 |
| 变更风险分析 | 新增 `useAiChangeAnalysis`，本地启发式 + 流式 AI 输出风险等级、影响面、关注点、测试建议 | 已完成；GitChangesView 已展示分析面板 |
| 变更审查 | 新增 `ai_review_changes` 接口，输出审查意见与改进建议 | 在 commit 前展示 AI 审查卡片 |
| 风格选择 | 提交信息生成支持 `style: concise/detailed/technical` | 用户可选择风格 |
| 缓存与取消 | 支持取消正在生成的请求，避免重复调用 | 已完成 AbortController |

**验收指标**：
- AI 提交信息生成包含 diff 上下文（已完成）。
- 新增变更风险分析功能入口（已完成）。
- 生成延迟 < 5 秒（本地模型路由）。

### 4.5 操作效率提升（第四阶段已完成）

**目标**：减少审批操作步骤，提升执行安全与反馈速度。

| 任务 | 实施计划 | 可衡量目标 |
|---|---|---|
| Approval 上下文校验 | 已完成；不匹配时返回 `approval:context-mismatch` | 拦截跨目录/跨 action 的误用 |
| 一键批准并执行 | 在 UI 上为 pending approval 增加“Approve & Execute”按钮；点击后顺序触发 approve + execute | 已覆盖 stage/commit/push |
| 长任务超时 | 对 push/pull/fetch/merge/rebase 增加超时阈值至 60 秒 | 大仓库同步不再 10 秒超时 |
| 长任务进度 | 后续通过 SSE/轮询返回进度行 | 后续补充 |
| 快速操作 | 支持键盘快捷键：Ctrl+Enter 提交、Ctrl+Shift+P push | 后续补充 |

**验收指标**：
- approval 上下文不匹配可被拦截（已完成）。
- 一键执行覆盖 stage/commit/push（已完成）。
- push/pull/fetch/merge/rebase 超时阈值 ≥ 60 秒（已完成）。

### 4.6 UI 界面改进（第四阶段，部分完成）

**目标**：提供更接近专业 IDE 的视觉与交互体验。

| 任务 | 实施计划 | 可衡量目标 |
|---|---|---|
| 冲突解决 UI | 冲突文件显示 ours/theirs/both 按钮与 approval 执行卡片 | 已完成 |
| 分支操作菜单 | 本地分支支持 delete/rename/merge/rebase | 已完成 |
| 一键按钮 | stage/commit/push 支持 Approve & Execute | 已完成 |
| 操作反馈 | 所有 git 操作完成后显示 inline 成功提示，失败时显示可折叠的详细输出 | feedback 已展示；失败详情待增强 |
| Side-by-side / inline diff | 在 `DiffViewer.vue` 中新增视图切换；Monaco 提供语法高亮 | 已完成 |
| Hunk 级 stage/unstage | 在 `diffUtils.ts` 中精确解析 hunk 范围，调用 `git apply --cached` 实现 hunk stage | 后续补充 |
| Commit graph | 使用 canvas 绘制简化 commit graph，展示分支/合并关系 | 后续补充 |

**验收指标**：
- 冲突文件可在 UI 中解决（已完成）。
- 分支支持 delete/rename/merge/rebase（已完成）。
- Diff viewer 支持 side-by-side / inline 切换与语法高亮（已完成）。
- Hunk 级 stage/unstage、History graph 模式后续补充。
- 所有变更操作有明确的视觉反馈。

---

## 5. 实施顺序建议

1. **第一阶段（已完成）**：功能完整性 P0 + Approval 上下文校验
2. **第二阶段（已完成）**：分支管理改进（真实分支列表 + fetch + delete/rename/merge/rebase）+ 冲突检测高亮
3. **第三阶段（已完成）**：冲突解决 UI + merge/rebase 闭环 + 一键批准并执行 + AI diff 上下文
4. **第四阶段（已完成）**：AI 变更风险分析 + UI 界面改进（side-by-side/inline diff 切换、语法高亮）
5. **第五阶段（后续）**：高级功能（reset/revert/cherry-pick、stash、tag、submodule、认证管理）与体验增强（真正 hunk 级 stage/unstage、commit graph、分支 ahead/behind 视觉提示）

---

## 6. 风险与注意事项

- **安全边界**：所有新增 git 变更操作必须保持 approval-gated，不能绕过 Core approval。
- **路径安全**：新增命令必须继续使用 `normalizeGitPathspec` 与 `isSafeGitRefName`。
- **状态权威**：Desktop 不直接调用 git，所有状态来自 Gateway/Core。
- **向后兼容**：`git_worktree_manager` 的现有 action 参数格式保持稳定，新增参数使用可选字段。
- **测试**：每次新增 action 必须在 Gateway 测试目录补充用例，涉及 UI 的变更补充 Vitest 测试。
