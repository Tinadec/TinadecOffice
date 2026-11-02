# 计划书⑦：PR 审查 — 本地 PAT 版（对应扩张调研 §3.3）

> 状态：草案 v1（2026-11-03），来源：`expansion-directions-research.zh-CN.md`（红海格局）+ 本仓代码实测。**未登记**，待批准。
> 判据沿用既有差距报告 A/B/C 三级；每批先红后绿+变异对照，收尾必须过 live 验收。

## 0. 结论一句话

PR 审查机器人的"发现"层已是红海（Copilot GA 2025-04、Cursor BugBot、CodeRabbit、@codex），本仓不比准确率，比**治理证据链**：审查结论带 file:line+可复跑依据+审计留痕（复用审批证据投影能力）——这是以上竞品都没有的。形态做**本地 PAT 版**：GitHub App/webhook 需要云接收端，推迟到计划书⑨评估。

## 1. 对标与现状

**竞品（网络检索，2026-11）**
- Codex：云侧 GitHub App，`@codex` 提及→👀→直接在 PR 发 review；2026-02/03 有触发可靠性 issue（#13701：10 分钟内 @3-4 次才触发）——**可靠性是云方案的真实痛点**，本地版天然免疫。
- Copilot Code Review（GA）、Cursor BugBot（只报真 bug，准确率 52%→76%）、CodeRabbit：形态同质化=mention/指派→diff→评论。
- Qoder：**没有**（IDE 仅 VS Code 标配 github-authentication，桌面 preload github 命中 0）——本方向无竞争压力。

**TinadecOffice 现状（2026-11-03 实测）**
- git 工具族在 TinadecTools（`GitCli`、`GitBranchTools.cs:44-56`、`GitWorktreeMutationTools.cs:42,96`），git 面板支持 push（`useGitOperation.ts:277`），AI commit message 走 `invokeStream`（`useAiCommitMessage.ts:317`）。
- `api.github.com` 全仓产品代码 **0 引用**（grep 证实，仅 AssemblyInfo/文档 URL 命中）——GitHub API 面从零起步。
- 治理网络出口现成：`web_fetch` 的 SSRF 防护+逐跳复验（`WebFetchGuard.cs`）——GitHub REST 调用应走同一治理出口，而不是工具进程里裸连。
- PAT 存放现成：SecretStore（Windows DPAPI/POSIX AES-GCM，`SecretStoreFactory.cs:27-40`）。
- 工作区指令现成：AGENTS.override.md>AGENTS.md>CLAUDE.md>CONTEXT.md（`WorkspaceInstructionPolicy.cs:29`）——审查准则文件直接复用这套优先级。
- 快照/评审语义现成：manifest(sha256)+`expected_sha256` 乐观守卫——审查结论的"依据"可锚定到具体 diff 行。

## 2. 目标 / 非目标

**目标**：①GitHub REST 最小面（PAT+治理出口）；②"审查"作为一等智能体模式：输入 PR→产出结构化审查（问题/file:line/严重度/依据）；③默认**本地评审卡**、不自动发帖；④手动确认后 PAT 回帖；⑤多 PR 队列。
**非目标**：不做 webhook/GitHub App（⑨ 评估）；不做自动 approve/自动合并（治理红线，永久非目标）；不做 PR 描述生成以外的写操作（issue 管理等）；不比拼"报得多"（对标 BugBot 只报真问题的取舍）。

## 3. 设计方案

### 3.1 P1：GitHub REST 最小面
- PAT 存 SecretStore（env 模式只读可选，DPAPI 默认）；仓库绑定=本地 git remote 解析（`cloud-tasks/src/env_detect.rs` 的 github remote 解析是现成参照，但本仓自实现最小版）。
- 端点走 Core，经治理网络出口（WebFetchGuard 语义扩展：GitHub API 域为显式白名单目标，出网仍逐次审批/规则审批）。
- 最小调用面：list PRs、PR diff、PR comments、post comment——四个，不多做。

### 3.2 P2：审查智能体模式
- 新对话模式 `review`（7 模式清单扩一档）：输入=PR 号；装配=PR diff+工作区指令（审查准则文件走 WorkspaceInstructionPolicy 优先级）+相关文件上下文（@ 提及/技能渐进披露复用）。
- 产出=结构化审查对象：`{file, line, severity, category, evidence(file:line+引用), suggestion}`——evidence 字段是治理差异化，铸造进事件留痕（审批证据投影同款纪律：脱敏、冻结、可追溯）。
- 质量门：审查在 ScriptedChatClient 下钉结构，真模型质量依赖**真实模型冒烟**（既有报告 §4.1）——本计划的前置。

### 3.3 P3/P4：评审卡与回帖
- 本地评审卡先行：与审批面板同级的证据呈现（逐条 file:line 可点跳转）。
- 回帖=一次治理写：卡片逐条勾选→合并为一条 GitHub comment→PAT 发帖（走 P1 治理出口，发帖动作逐次审批）。**永不自动发帖。**
- 多 PR 队列：复用计划书② 的 Quest 语义（每 PR 一个 Quest 级任务）——依赖②先行或降级为简单串行队列。

## 4. 分批落地与验收

| 批 | 内容 | 验收门禁（先红后绿/变异） |
|---|---|---|
| PR1 | REST 最小面（PAT/白名单域/四调用）+ 仓库绑定 | 出网走治理出口（绕过 WebFetchGuard→红）；PAT 不进日志/事件（变异：日志泄 PAT→红） |
| PR2 | review 模式+结构化审查+本地评审卡 | evidence 可锚定 file:line 且脱敏；结构在 Scripted 下钉死 |
| PR3 | 手动回帖+多 PR 队列 | live：对真实 PR 完成一次"审查→确认→回帖"；未确认绝不发帖（变异→红） |

## 5. 风险与开放问题

1. 真实模型未测（§4.1）——审查质量全押在真模型上，PR2 的 A 级只覆盖结构不覆盖质量，报告中要如实分级。
2. 私有仓 PAT 权限面：最小 scope（repo:read + PR comment write）；secret 轮换引导。
3. 开放问题：review 模式是否读工作区工作副本（可能未同步）——建议 v1 强制从 PR ref 拉取（fetch 到临时 worktree，正好复用计划书④的 `.tinadec/worktrees` 约定）。

## 6. 与其他计划书的关系

- 计划书④（Best-of-N）的 worktree 约定复用于 PR ref 拉取；计划书②（Quest）承载多 PR 队列；计划书⑨（GitHub App）是本计划的云扩展形态。
- 前置：live 真链路验收（用户验收标准既有口径）；差距报告 §4.1 的 egress 冒烟用例作顺手卫生项，不构成前置。
