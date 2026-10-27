# 性能问题分析（2026-10-27）

分支 `main` @ `a33b959d` + 未提交工作树。纯分析，**未改任何产品代码**。

范围：前两份报告（[2026-10-18 全链路](2026-10-18-performance.zh-CN.md)、[2026-10-25 设置页冷启动](2026-10-25-settings-cold-start.zh-CN.md)）已修掉挂载 N+1、Vite 扫描、窗口等后端、i18n 拆包、会话列表 N+1、设置页七次读。本轮只看它们没覆盖的方向：**运行期热路径**（run 进行中、会话打开时）、**随使用量增长的查询**、**常驻后台循环**、渲染层流式渲染。

数据来源：本机两个真实项目作用域 `C:\git\agent\TinadecOffice\.tinadec`、`C:\git\agent\RouterAPI\.tinadec`，以 SQLite `mode=ro` 只读打开。探针脚本在 [`evidence/2026-10-27-perf-analysis/`](../evidence/2026-10-27-perf-analysis/)，可复现。

> **读数边界**：本机 Core/Gateway 未运行；启动 Core 会对真实作用域执行恢复修复（有副作用），所以**没有实测端点墙钟时间与 Core 空闲 CPU**。下面的毫秒数是用 Python `sqlite3` + 原生文件 I/O 复现同一访问模式得到的**下限**（EF Core 物化、SSE 写出、Gateway 转发都还要另加）。

---

## 一、结论（按用户可感知影响排序）

| # | 问题 | 触发场景 | 量级（本机真实数据） | 增长方式 |
|---|---|---|---|---|
| 1 | **会话刷新每次两遍全会话事件重放，且每条事件单独打开一次文件** | run 进行中每批实时事件后 250 ms；打开会话 | 1,295 条事件的会话：一次重放 **0.69–1.33 s**；每次刷新做两次 | 随会话事件数线性；run 期间连续触发 |
| 2 | **检查点内嵌原始工具结果，每次保存整份重写** | 每个工具回合、每次阶段迁移 | 单个 run：**85 份检查点 / 129.6 MB**，最后一份 2.2 MB（96% 是 `toolTurns`） | 每次保存 ∝ 已累积结果，总写入近似平方 |
| 3 | **SSE 跟随每 200 ms 全量扫会话索引；无会话时扫整个作用域** | 任何打开的窗口；欢迎页/新对话时为未过滤流 | 未过滤流初始重放 5,197 条 **3.9 s**；每 tick 扫全表 ≥33 ms ×5 次/秒 | 随作用域事件总数线性，常驻 |
| 4 | **所有已登记项目在启动时串行挂载、永不卸载，每个都跑一整套后台循环** | Core 启动；`GET /projects` | 每个作用域挂载约 2.4 s（10-18 报告），串行累加；每作用域引擎每 2 s 拉全部无租约 run（含历史终态） | 随登记项目数 × run 历史 |
| 5 | **流式回答每次更新都整篇重新 parse + 消毒 + 高亮** | 模型输出流式期间 | 12.6K 字符回答：末段每次更新 **20–37 ms**（不含 DOMPurify），全程累计 **331–454 ms**，是单次解析的 19–50 倍 | 随回答长度平方 |
| 6 | Vite 未忽略 `apps/desktop/.runtime-cache`（**1.3 GB / 5,275 文件**） | `npm run dev` | 与 10-18 的 `release/`/`runtime/` 同一类根因 | 随打包次数 |
| 7–11 | 低优先级：工人回合重解析全部历史结果、主进程同步日志、恢复巡检、`handleSessionEvent` 每条事件重建数组 | 见第三节 | — | — |

**1、2、3 互相叠加**：run 一开始产生事件 → 渲染层每 250 ms 触发一轮六个读 → 其中两个读各做一次全会话逐行开文件重放、另一个反序列化 2 MB 检查点 → 刷新串行、跑完立刻再跑 → Core 在整个 run 期间持续忙于"重读历史"，消息/审批/工具时间线的刷新落后数秒。同时 SSE 跟随还在每 200 ms 扫一次索引。

---

## 二、逐项证据与建议

### 1. 会话刷新：每轮两次全会话事件重放（最高优先级）

**调用链**

- 渲染层：`handleSessionEvent` 收到 `message./approval./tool./run./task/supervision./context./step.` 任一前缀的事件 → `scheduleSessionRefresh()`（250 ms 合并、一次只跑一个、跑完有积压再跑一轮）→ `readMessagesAndApprovals()` 并行六个读（[HomeController.ts:507-516](../../apps/desktop/src/controllers/HomeController.ts:507)）。run 进行中事件源源不断，这个刷新实际上是**连续运转**的。
- 六个读里的两个在 Core 侧做全会话重放：
  - `GET /sessions/{id}/orchestration`：[DmaeaEndpoints.cs:123](../../TinadecCore/AspNetCore/Endpoints/DmaeaEndpoints.cs:123) `ReplayEventsAsync(sessionGuid, 0)`，只为了从中挑出 `task.assigned/step.result.created/worker.*` 几种事件（:171、:201）；同一请求还在 :128 反序列化整份最新检查点（见第 2 条，可达 2 MB）。
  - `GET /sessions/{id}/tool-executions`：渲染层不传 `run_id`（注释说要统计整个会话），于是走 [DmaeaEndpoints.cs:262](../../TinadecCore/AspNetCore/Endpoints/DmaeaEndpoints.cs:262) 的全会话重放。
- 重放本身：[StorageLifecycleService.cs:483-495](../../TinadecCore/Lifecycle/StorageLifecycleService.cs:483) 把会话的全部索引行读出来，[MaterializeAsync :542-579](../../TinadecCore/Lifecycle/StorageLifecycleService.cs:542) **对每一行 new 一个 `FileStream`**、Seek、读、反序列化。

**实测（RouterAPI 作用域，最大会话 1,295 条事件）**

| 访问模式 | 耗时 |
|---|---|
| 每行打开一次文件（现状） | 692–1,334 ms |
| 每个 run 文件只打开一次（同样的行、同样的解析） | **40 ms**（约 17×） |

Windows 上单次打开文件约 0.5–1 ms（含 Defender 扫描），这是开销的主体，不是 JSON 解析。

**建议**（由易到难，可分步落地）

1. `MaterializeAsync` 按 `RunId` 分组，每个 run 文件只打开一次（行已按时间排序，分组后再按原序合并）——纯内部改动，所有重放调用点同时受益。
2. 给 `ReplayEventsAsync` 增加 `eventTypes` 过滤，在**索引查询里**按 `EventType` 过滤（`event_index` 已有该列），orchestration 只取那 5 种、工具时间线只取 `tool.*`，大部分行根本不用读文件。
3. orchestration 的 `step_results` 目前收的是**整个会话所有 run** 的 step result，却都标成当前 run 的 `run_id`（:205）——按当前 run 过滤既是正确性修正也是性能修正（`ReplayRunEventsAsync` 已存在）。
4. 中期：工具时间线改为增量（客户端带上次的游标），或在渲染层只在 `tool.*` 事件时刷新工具时间线、而不是任何事件都刷新六个读。

### 2. 检查点膨胀：工具结果原样嵌入，每次保存整份重写

**事实**

- 工具调用成功后 `pendingTurn.ResultJson = dispatch.Result?.GetRawText()`（[FullDuplexRunEngine.cs:2139-2141](../../TinadecCore/DmaEA/FullDuplexRunEngine.cs:2139)），原始结果**不截断、不引用化**地进入 `DurableTaskNode.ToolTurns`。
- `SaveCheckpointAsync` 每次都 `JsonSerializer.Serialize(checkpoint)` 整份（[:5661-5664](../../TinadecCore/DmaEA/FullDuplexRunEngine.cs:5661)），然后 SHA-256、写入内容库（`LocalFileContentStore` 用 `FileOptions.WriteThrough`，[LocalFileContentStore.cs:27](../../TinadecCore/Persistence/LocalFileContentStore.cs:27)）、新增一行 `run_checkpoints`。引擎（含 Lanes 分部）里有 60 余处保存调用。
- 旧修订**从不回收**：全仓只有卸载 Agent Pack 时 `ExecuteDelete`（[AgentPackResourceStore.cs:95](../../TinadecCore/Runtime/AgentPackResourceStore.cs:95)）。

**实测（TinadecOffice 作用域）**

| | |
|---|---|
| 作用域内容库 | 133 MB，其中检查点 129.8 MB |
| 最重的 run `8803ACFE…` | 85 份检查点，合计 **129.6 MB** |
| 增长轨迹 | rev 1→15：1→54 KB；rev 22 起跳到 **1.9 MB**（单个工具结果约 1.8 MB 进入转录），此后每份都 ≥1.9 MB |
| 最后一份 2.2 MB 的构成 | `tasks[].toolTurns` 2,075 KB（96%） |

RouterAPI：30 个 run、466 份检查点 19.5 MB，单 run 最多 60 份 / 5 MB。

**连带成本**：每次唤醒 run 读一次（`TryReadCheckpointAsync`）、每次会话刷新的 orchestration 读一次并反序列化、会话跨作用域迁移时整份复制（`SessionScopeTransferService` 把 `run_checkpoint` 列为结构化内容）。

**建议**

1. 工具结果写入内容库（`IContentStore` 已有、内容寻址），`WorkerToolTurn` 只存 `reference/hash/length` + 一段有界预览；需要喂给模型时再按引用读。这一步把检查点从 MB 级拉回 KB 级，85 次保存的总写入从 130 MB 降到个位数 MB。
2. run 进入终态后只保留最后一份（或最后 N 份）检查点修订，回收其余内容对象。是否需要保留中间修订作为审计证据需要产品确认——事件日志本来就是审计介质。
3. `WriteThrough` 是有意的持久化选择，不建议去掉；把写入量降下来才是正解。

### 3. SSE 跟随：每 200 ms 全量扫索引；无会话时是整个作用域

**事实**

- `/api/v1/events` 每 200 ms 调 `FollowEventsAsync`（[StorageEndpoints.cs:434](../../TinadecCore/AspNetCore/Endpoints/StorageEndpoints.cs:434)、:657）。该函数把**会话全部索引行**（瘦投影）读回内存，再在内存里按时间戳过滤——注释写明原因：EF Core SQLite 不能翻译 `DateTimeOffset` 比较（[StorageLifecycleService.cs:533-539](../../TinadecCore/Lifecycle/StorageLifecycleService.cs:533)）。注释称"不再全量重扫会话日志"只对了一半：文件不重读了，索引仍是全量。
- `sessionId == null` 时上述两步作用于**整个作用域**的 `event_index`。渲染层在没有选中会话时（欢迎页、新对话前）会 `followSession(null)`（[HomeController.ts:1592](../../apps/desktop/src/controllers/HomeController.ts:1592)、[sessionEventBus.ts:81](../../apps/desktop/src/lib/sessionEventBus.ts:81)），连接的正是这个未过滤流；它的初始重放会把整个作用域的历史逐行开文件重放一遍并推给渲染层，`handleSessionEvent` 再逐条处理。
- 初始重放永远从 0 开始（[StorageEndpoints.cs:405](../../TinadecCore/AspNetCore/Endpoints/StorageEndpoints.cs:405)），`Last-Event-ID` 只在内存里过滤——因为序号是 per-run 的，SQL 用不上游标。所以**每次 EventSource 重连（网关重启、网络抖动、窗口刷新）都是一次全会话重放**。
- 每个窗口（主窗口 + 每个分离面板）各一个连接，各自 200 ms 轮询。

**实测（RouterAPI，下限）**

| | 行数 | 耗时 |
|---|---|---|
| 未过滤流一次跟随 tick（全表瘦投影） | 5,197 | 33 ms |
| 最大会话一次跟随 tick | 1,295 | 22 ms |
| 未过滤流初始重放（逐行开文件） | 5,197 | **3,924 ms** |
| 同上，每 run 只开一次文件 | 5,197 | 264 ms |

每秒 5 次 tick → 一个空闲窗口就让 Core 持续占用约 10–17% 的单核（下限，EF 物化更贵），并随事件总数线性增长。

**建议**

1. 渲染层：没有选中会话时**不建立事件流**（或改为只订阅轻量的会话列表变化）。欢迎页并不消费全作用域历史。
2. 跟随查询改用可在 SQL 里比较的水位：`event_index` 增加 `timestamp_unix_ms`（引擎的租约已经这样做了：`LeaseExpiresUnixMilliseconds`），或者用 SQLite `rowid`/全局自增列作为跟随游标——注释里已写了这条升级路径。
3. 初始重放带上 per-run 最大序号（客户端已经有 `lastSequenceByRun` 的语义），或至少先应用第 1 条的"每 run 只开一次文件"。

### 4. 所有项目作用域常驻，各跑一整套后台循环

**事实**

- 启动时 `StorageScopeRecoveryWorker` **串行**挂载所有已登记项目（[StorageScopeRecoveryWorker.cs:12-18](../../TinadecCore/Runtime/StorageScopeRecoveryWorker.cs:12)）；`GET /api/v1/projects`（无存储头）同样**串行** `AcquireAsync` 每个作用域（[StorageEndpoints.cs:26-30](../../TinadecCore/AspNetCore/Endpoints/StorageEndpoints.cs:26)）。每次挂载含一次全量事件索引重扫（10-18 报告：约 2.4 s/作用域）。
- 挂载后启动该作用域服务图里的全部 `IHostedService`（[StorageScopeRegistry.cs:485-489](../../TinadecCore/Runtime/StorageScopeRegistry.cs:485)）：全双工引擎（100 ms 主循环 + 2 s 恢复扫描）、`RecoveryCoordinator`（15 s）、`AutonomousExplorationCoordinatorService`（500 ms）、`ApprovalGateService`、`TinaChatWakeService`、`EvidenceIndexService`、工具进程管理等。注册表里**没有空闲卸载**。
- 引擎每 2 s 的恢复扫描 `ListLeaseEligibleRunsAsync` 把所有"无租约或租约过期"的 run 读进内存，再在内存里排除终态（[StorageLifecycleService.cs:652-667](../../TinadecCore/Lifecycle/StorageLifecycleService.cs:652)）。已完成的 run 没有租约，所以这实际上是**每 2 s 拉一次全部历史 run**。注释把原因归于 `DateTimeOffset`，但 `Status` 是字符串，完全可以在 SQL 里过滤。
- `RecoveryCoordinator` 每 15 s 列出全部 cancelled/failed run 的 id，再到 `run_stream` 里 `Contains` 查一遍（[StorageLifecycleService.cs:591-629](../../TinadecCore/Lifecycle/StorageLifecycleService.cs:591)）。

**建议**

1. `ListLeaseEligibleRunsAsync` 把终态与 `awaiting_*` 排除放进 SQL `Where`（一行改动）；admission grace 也可按已有的 unix-ms 模式落列。
2. 挂载改为有界并行（`GET /projects` 与恢复 worker），总耗时从 Σ 降为 max。
3. 中期：按需挂载 + 空闲卸载（没有活跃 run、没有租约持有者的作用域在 N 分钟后停掉后台循环），或把后台循环提升为宿主级、跨作用域轮询一次。

### 5. 流式回答整篇重渲染

**事实**：`MarkdownRender` 的 `blocks` computed 在 `content` 每次变化时对**整篇**执行 `marked.parse` → `DOMPurify.sanitize` → 标题锚点/脚注/文档引用改写 → 对每个代码块 `highlightCode`（[MarkdownRender.vue:222-264](../../apps/desktop/src/components/MarkdownRender.vue:222)）。对话层按帧合并事件（`useConversationFeed`，设计合理），Core 每 256 字符或 100 ms 刷新一次输出（`ModelOutputStream.cs:45`），所以流式期间约每 100 ms 整篇重算一次。v-html 按块比较字符串，已完成块的 DOM 不会被替换——开销在主线程的解析/高亮，不在 DOM。

**实测**（[stream_markdown.mjs](../evidence/2026-10-27-perf-analysis/stream_markdown.mjs)：用仓库里的 `marked` + `highlight.js`，正文为真实源码拼成的 12.6K 字符回答，按 256 字符递增模拟；**不含 DOMPurify**，真实开销更高；两次运行之间有 JIT 波动）

| | 读数 |
|---|---|
| 单次整篇解析 | 6.6–24.5 ms |
| 末段每次更新 | **20.3–37.5 ms**（超过两帧） |
| 一次流式累计 | 331–454 ms（单次的 19–50 倍） |

**建议**：流式期间只重新解析"最后一个未闭合块"之后的尾部（前面已闭合的块结果可缓存）；或按代码块内容缓存高亮结果（已闭合代码块的文本不再变化）。最小改动是流式期间把重算节流到 ~150 ms 并跳过高亮，落定后再完整渲染一次。

### 6. `.runtime-cache` 不在 Vite 忽略列表里

`apps/desktop/.runtime-cache`（`stage-runtime.mjs` 的 NuGet/打包缓存）实测 **1.3 GB / 5,275 个文件**，而 [vite.config.ts:44](../../apps/desktop/vite.config.ts:44) 只忽略了 `release/`、`runtime/`、`dist/`。Vite 默认不忽略点目录，所以 chokidar 会爬它——这正是 10-18 报告里"真正的收益来源是 `watch.ignored`"的同一类问题。**未单独测量**它对冷启动的影响；修法是一行：把 `'**/.runtime-cache/**'` 加进 `ignored`。

---

## 三、低优先级（记录在案，当前数据量下影响小）

7. **工人每回合重解析全部历史工具结果**：`BuildConversationAsync` 对每个历史回合调用 `ModelFacingResult`，对 `ResultJson` 做一次完整 `JsonDocument.Parse` + 重写（[ExecutionAgent.cs:205-209](../../TinadecCore/DmaEA/ExecutionAgent.cs:205)、:264），之后才按消息条数压缩。转录 2 MB 时每回合多一次 2 MB 解析。第 2 条的引用化会顺带解决。
8. **Electron 主进程同步写日志**：每个子进程 stdout 数据块都在主进程上同步 `existsSync/statSync/appendFileSync`，并且**每次写都递归遍历整个日志目录逐文件 `statSync`** 来执行总预算（[logSink.cjs:50-64](../../apps/desktop/electron/logSink.cjs:50)，调用点 [serviceManager.cjs:188-189](../../apps/desktop/electron/serviceManager.cjs:188)）。当前 Core 的 EF 日志级别是 Warning、日志目录只有 4 个文件，所以代价小；一旦打开详细日志会直接阻塞主进程（终端 PTY 输出、所有 IPC 都在主进程）。建议预算检查改为按字节计数节流（例如每写入 1 MB 或每 5 s 检查一次）。
9. **`RecoveryCoordinator` 15 s 巡检**：见第 4 条，列表随历史增长；可改为只看最近 N 天或带"已修复"标记。
10. **`handleSessionEvent` 每条事件重建数组**：每条事件都 `new Map(events)` + 排序 + 截断 80 + 重新赋值一个深响应式 `ref`（[HomeController.ts:1457-1460](../../apps/desktop/src/controllers/HomeController.ts:1457)）；未过滤流的初始重放会执行几千次。改 `shallowRef` 并在重放期间跳过即可。**顺带发现的正确性问题**：它以 `seq` 为键，而序号是 per-run 的，不同 run 的同序号事件会互相覆盖。
11. **UIE 布局 blob 无界增长**：`sessionBySessionId` 为每个空间会话保留一份快照且从不清理，每次保存整份重写（已有防抖，影响小）。

## 四、已登记、本轮未重新分析

来自 10-18 报告"没做的"清单，仍然成立：挂载时事件索引全量重扫（可改增量 offset）、`/api/v1/agents` p50 314 ms、`ListAgents` 读全部 succeeded `model_invocations`、Core 6.8 s 宿主冷启动地板、`release/` 与 `runtime/` 放在 `apps/desktop` 下的布局问题、首屏重组件异步边界。

## 五、建议的落地顺序

| 顺序 | 改动 | 预期收益 | 风险/工作量 |
|---|---|---|---|
| 1 | `MaterializeAsync` 每 run 只开一次文件 | 所有重放 ~15×（实测 17×/15×） | 低，纯内部 |
| 2 | 重放按 `EventType` 在索引里过滤；orchestration 按当前 run 过滤 | 会话刷新从"读整个会话"变为"读几十行" | 低；需补 step_results 的 run 归属测试 |
| 3 | `ListLeaseEligibleRunsAsync` 在 SQL 里排除终态 | 每作用域每 2 s 不再拉全部历史 | 一行，低 |
| 4 | 无会话时不开事件流 | 欢迎页的 Core 常驻负载归零 | 低；需确认未过滤流没有其他消费者依赖 |
| 5 | `.runtime-cache` 加入 `watch.ignored` | dev 冷启动（未量化） | 一行 |
| 6 | 工具结果引用化进内容库 | 检查点 MB→KB，run 期间写入降一到两个数量级 | 中；涉及检查点 schema 与恢复路径 |
| 7 | 跟随水位改为可在 SQL 比较的列 | 跟随 tick 从 O(会话) 到 O(新增) | 中；需迁移 |
| 8 | 流式 Markdown 只重算尾部 | 长回答流式不卡顿 | 中 |
| 9 | 作用域有界并行挂载 / 空闲卸载 | 启动与常驻负载 | 中到高 |

每一步落地后都应当用同样的探针复测，并在真实 Core 上补测端点墙钟时间——本报告没有做这一步。
