# 计划书①：Repo Wiki — 自动生成、持续同步的项目知识库

> 状态：草案 v1（2026-11-02），来源：Qoder/Codex 竞品对比（当日会话），**未登记**进 §11/backlog，待批准后登记。
> 判据沿用 `agent-workbench-capability-gap-report.zh-CN.md` §判据 的 A/B/C 三级；每批验收门禁先红后绿、附变异对照，收尾必须过 live 验收（用户验收标准=真实可用）。

## 0. 结论一句话

对标 Qoder Repo Wiki（自动生成+持续同步项目文档），并利用 Qoder 自己的短板反超：Qoder 的 wiki 存在外部、AI 在工作区里用不到（其官方论坛的公开功能请求），本计划的 wiki 落在**工作区内 `.tinadec/wiki/`**、以渐进披露方式直接进上下文装配——是"能被自己的智能体读到的 wiki"。

## 1. 背景与对标

- Qoder：Repo Wiki 自动生成项目结构/实现文档并持续同步；但其存外部、智能体在 workspace 内读不到。Quest/Spec 也依赖对代码库的深度理解。
- Codex：无对应物，靠 agentic 搜索现查；深度代码上下文同样依赖语义检索层。
- 本仓既有差距报告 §4.2（语义检索没配：`IProjectVectorDatabase` 有端口没线路、"换了说法的事实召不回来"）是本计划的第一前置；Repo Wiki 与记忆语义召回共用这一层地基。

## 2. 目标 / 非目标

**目标**
1. 工作区可一键生成模块级 wiki 文档（落 `.tinadec/wiki/`，人可读可改）。
2. 文件变更后增量同步（watcher + 内容哈希），不整库重扫。
3. wiki 内容经向量化检索进入 context 装配（新来源 `workspace_wiki`，渐进披露：索引上屏、正文按需）。
4. 生成/写盘全程治理：落盘位置由 Core 决定、写前可审、路径白名单内。

**非目标**
- 不做 UI 层全局代码语义搜索（另立）。
- 不做多仓库/跨仓 wiki。
- 不做 wiki 的自动 PR/外发。
- 不解记忆检索的语义化（`MemoryStore.RetrieveAsync` 换嵌入打分另行登记，本计划只供给它向量基建）。

## 3. 现状与证据（2026-11-02 实测）

**已有、可直接复用**
- 向量端口：`Persistence/IProjectVectorDatabase.cs:4-56`（Upsert/Search/DeleteSource + ProjectVectorRecord/Match 全字段）；抽象 `Abstractions/Ports/IVectorStore.cs:4`。生产侧唯一消费者是 `AgentGraph/EvidenceArchiveService.cs:152,362`（get 侧，失败静默返 0）——写侧/建索引入口为零。
- **嵌入 provider 已全通**：`IEmbeddingProvider`（`Abstractions/Ports/IModelProvider.cs:22`），`Models/ModelsModuleRegistrar.cs:272` 解析 purpose=embedding 的路由→OpenAI 兼容端点，缺路由返回结构化 unavailable 而非造假向量。缺的只是把 VectorStore 模块接上它。
- 后台任务基建成熟：`Runtime/TinaChatWakeService.cs:23`（BackgroundService+间隔队列）、`Runtime/RecoveryCoordinator.cs:30` 等一族，WikiSyncWorker 可挂同类。
- 上下文装配可插来源：11 类来源齐全（`Context/ContextModuleRegistrar.cs`：intent :82、structured_session_state :108、task_context :122、session_history :180、reviewed_memory :196、run_steering :298、run_recorded_facts :319、session_compaction :366、workspace_instructions :424、workspace_skills :539/560、session_attachments :714）；`context.packed` 发出于 `DmaEA/FullDuplexRunEngine.cs:1368`（payload 带 sources/source_tokens/dropped_sources）。
- 渐进披露契约有现成样板：技能索引（`Abstractions/Ports/WorkspaceSkillPolicy.cs:47`，SkillRoots=[.tinadec/skills]，MaxSkills=40、MaxIndexChars=8000）——wiki 索引照抄这套"先声明后加载"。
- 治理写盘样板：市场安装的"冻结字节+落盘位置由 Core 决定+审批后写"（`Skills/MarketInstallService.cs`）。
- 参照存货：仓内已有 Qoder 生成的 wiki（`.tinadec_dev/wiki/qoder/zh/content/**`），可作格式与质量基准。

**缺失、必须新建**
- VectorStore 模块注册为 NotConfigured（`VectorStore/VectorStoreModuleRegistrar.cs:24`）——接线是本计划第一块硬骨头。
- **无工作区文件监听基建**：唯一 FileSystemWatcher 是 runtime.toml 热重载（`DmaEA/AgentRuntimeConfiguration.cs:349-379`）；大仓扫描无现成护栏（只有点状上限：32 源文件夹、列目录分页 `TinadecTools/Tools/FileRW/FileSystemTools.cs:106-121`、拖拽 200 文件/64MiB）。
- wiki 实体、生成器、UI 全新。

## 4. 设计方案

### 4.1 落点与格式
- 目录：`<workspace>/.tinadec/wiki/`，与 skills 同族（复用 WorkspaceSkillPolicy 的路径组合纪律：相对路径拼接必须等于加载器会读的那层）。
- 条目 = markdown（frontmatter：module/title/source_globs/content_hash/updated_at）+ 人类可直接编辑；外部编辑经哈希检测进入"重新生成/保留"裁决，不静默覆盖。
- 索引 = 内存态 + 持久化到向量库（source=`wiki:<条目id>`），上下文侧只见条目清单（标题+摘要+来源 glob），正文按需 `read_file`——与技能渐进披露同形。

### 4.2 三条子链
- **A 生成**：`WikiSyncWorker`（BackgroundService）扫描工作区→按目录/模块聚类→逐模块调 chat provider 生成→治理写盘（复用 MarketInstallService 的冻结字节路径：生成内容先入库、落盘位置由 Core 拼、写前入审批队列，除非用户在设置里给 wiki 开 auto-approve——默认逐次审）。
- **B 检索**：接线 VectorStore 模块（`VectorStoreModuleRegistrar.cs:24` 的 NotConfigured 位）→ 嵌入走 `IEmbeddingProvider`（purpose=embedding 路由）→ 本地持久化向量表（SQLite 存 float[]，余弦在内存算——**SQLite 不能在 SQL 里比较/排序，且新表不做 DateTimeOffset 列比较**，全仓既有约束，`Memory/MemoryModuleRegistrar.cs:92-94` 等多处为证）。
- **C 同步**：Core 侧新建工作区 watcher（防抖 ≥2s + 单轮变更预算 + 大仓退化为手动"重新扫描"按钮）；增量以 content_hash（sha256，复用 ContentStore 惯例）判定，只重生成变脏模块。

### 4.3 上下文接入
- context 装配新增第 12 类来源 `workspace_wiki`：预算内先给条目清单（≤8000 字符，对齐技能索引上限），命中检索的条目正文作为附加来源进装箱（`Prompts/PromptsModuleRegistrar.cs:76` budget=8192 逐段装箱、放不下即 warning 的既有机制直接吃掉它）。
- `context.packed` 的 sources 列表自然带上它——上下文可见性面（"run 被告知了什么"）无需改协议。

### 4.4 UI 与契约
- 桌面：WikiPage（与 MemoryPage 姊妹，`apps/desktop/src/router.ts:59-64` 加路由，入口进命令面板 `lib/appCommands.ts`）；生成任务进度走既有通知岛。
- Gateway：`TinadecGateway/src/index.ts` 照 memory 代理惯例（:1299-1325）加纯代理路由；**OpenAPI 双快照同批更新**（`CoreOpenApiSnapshotTests.cs:8-14` + gateway `openapi.snapshot.test.ts`，`git diff --exit-code` 门禁）。
- i18n：新说话页面必须手工加进 `apps/desktop/src/locales/i18nParity.test.ts:68-130` 的显式白名单（注意：该白名单是显式 glob，不是自动全扫——新增页面不登记就不会有死键告警）。

## 5. 分批落地与验收

| 批 | 内容 | 验收门禁（先红后绿/变异） |
|---|---|---|
| W1 | VectorStore 接线（嵌入路由 E2E 一条真路由）+ 向量表 + `.tinadec/wiki/` 契约（条目/哈希/路径纪律） | 缺 embedding 路由时结构化 unavailable（不造假向量）先红后绿；变异：把余弦改 0 恰红检索用例 |
| W2 | 生成器单模块闭环（扫描→聚类→生成→治理写盘）+ 手动触发端点 | 治理写盘变异：绕过冻结字节直写 → 恰红；真 provider 冒烟（可跳过、有密钥才跑，§4.1 同款）+ live 验收 |
| W3 | watcher 持续同步 + content_hash 增量 | e2e：改一个文件→只有对应条目重生成；防抖合并变异 |
| W4 | `workspace_wiki` 进上下文装配 + WikiPage UI | context.packed sources 含新来源且被预算裁剪时 dropped_sources 说得出；i18n parity 登记；双快照同批 |

## 6. 风险与开放问题

1. **真实模型未测**（既有报告 §4.1）压在本计划头上：生成质量、聚类合理性在 ScriptedChatClient 下验证不了——W2 的真 provider 冒烟是硬门禁。
2. 大仓扫描成本：无现成护栏，W3 必须先给预算护栏再开自动同步。
3. Windows watcher 抖动/网络盘：退化为手动重扫是明确兜底，不算失败态。
4. 开放问题：wiki 是否允许引用具体 file:line（会腐烂更快但更有用）——建议 v1 允许并在 frontmatter 标注 source_globs 供同步检测。

## 7. 与既有登记项的关系

- 完成即兑现既有报告 §4.2 的一半（向量线路 + wiki 场景）；记忆检索换嵌入打分登记为独立后续项。
- 依赖顺序建议：真实模型冒烟（§4.1）→ W1。W1-W4 不动 §11 现有登记项（AgentEditorDrawer/工具覆盖 UI/CLI-ACP/roster 单测），不抢占。
