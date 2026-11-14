# 存储写事务重试与工具边界整改 · 收尾报告

日期：2026-11-14
分支：`main`（本地，未推送），`origin/main..HEAD` 共 **29** 个提交
产物输出：本轮全部验证都改用**仓库内默认产物路径**（见「方法论教训」）

## 一、本轮新修的真实缺陷（4 个）

| 提交 | 缺陷 | 修复前 | 修复后 |
|---|---|---|---|
| `d9f40465` | `WorkspaceStoragePolicy.IsInternal` 包含方向写反：普通布局下把 `.tinadec/{data,state,logs,cache,temp,packages,worktrees}` 当成项目内容放行 | 工具侧 3 红 / 578 | **579/579** |
| `ebbadd41` | schema 协调器 catalog 在删列后未失效，对同一列重复 `DROP COLUMN "nonce"` | 92/93 | **93/93** |
| `d4d45093` | 转移 fixture 缺 `GlobalAgentConfiguration` 注册，转移停在 `copying` | 1/5 | **5/5** |
| `4aa826ad` | 附件断言要求把图片称作 "not text"，而图片已有专用视觉通路文案 | 0/1 | **39/39** |

四条都做了变异验证：把修复改回旧行为，对应用例变红。

## 二、契约更正型提交（2 个）

| 提交 | 内容 | 验证 |
|---|---|---|
| `d16b2256` | `/migrate` 已由同步 200+会话体改为异步 202+receipt；旧断言不可能通过 | 2/2；变异成 `Results.Ok` 后红 |
| `995c1843` | 审批消费事务移入 `WriteInTransactionAsync`（每尝试新建上下文） | 守卫断言尝试次数恰为 3；去重试变异红；回归 35/35 |

## 三、关键判断修正（留档，避免后人重走）

1. **`/migrate` 404 不是 `storage_scope_not_found`。** 实测响应体是
   `"detail":"No endpoint matches this request."`：该路由的映射包在
   `StorageEndpoints.cs:272-274` 的守卫里，而测试宿主
   `IsolatedApiFactory.UsesManagedStorage => false`，路由根本不存在。
   先前基于推断的结论是错的。

2. **`TryStartCore` 的「语义阻塞」是我的误判。** `Never replay a potentially
   mutating call automatically` 约束的是「把已消费的执行重置回可启动状态」，
   不是 DB 写重试。且 `ToolApprovalCoordinator` 全类不执行任何工具
   （搜不到 dispatch/invoke），重试不可能重放工具。真正的约束是技术性的：
   `WriteInTransactionAsync` 要求委托每次自建上下文。

3. **基线对比必须用仓库内产物路径。** 见下节。

## 四、方法论教训

1. **`--artifacts-path` 指到仓库外会造出假失败。**
   `GraphSeedPackClosureTests.FindGraphSeedManifestPath()` 从
   `AppContext.BaseDirectory` 逐级向上找
   `apps/desktop/src/agentPacks/GraphSeedPack/manifest.json`；产物在
   `C:\git\agent\.p1verify\...` 时永远走不到仓库根。
   更危险的是**基线对比会同样失败**，于是把自造假失败误判成既有失败。
   本轮因此一度错判 2 条。

2. **改工具层必须跑 `tests/TinadecTools.Tests`。** Core 侧切片不覆盖工具自身
   单元测试，本轮因此把一个方向写反的修复当成绿的，直到补跑才发现。

3. **不要并发对同一输出目录编译与跑测试。** 本轮一次全量跑批与一次编译撞车，
   编译因 DLL 被占用而失败，跑批结果也不可信，只能停掉重跑。

4. `Copy-Item` / `git checkout` 恢复被变异文件会保留旧时间戳，MSBuild 可能跳过
   重编，导致「恢复后复跑」仍在跑变异版。用编辑器恢复 + `--no-incremental`。

5. 断言要放在探针**之后**，否则探针输出被前一条断言遮蔽。

6. `Select-String -LiteralPath` 在部分文件上静默返回空；改用 `read` 或 `rg`。

## 五、验证覆盖（仓库内默认产物路径）

| 套件 | 结果 |
|---|---|
| `tests/TinadecTools.Tests` | **579/579** |
| `TinadecCore.Architecture.Tests` | **21/21** |
| `TinadecCore.Governance.Tests` | **93/93** |
| `SessionScopeTransferTests` | **5/5** |
| `AttachmentApiTests` | **39/39**（trx 计数） |
| 审批切片（生命周期修复+审批流+审批窗口+审批读重试） | **35/35** |
| 回归切片（审批流+工具根+写重试+用户动作） | **39/39**（trx 计数） |
| `GraphSeedPackClosureTests` | **通过** |

## 六、仍未处理

1. `TinadecToolsProcessTests.CallAsync_ShellTool_ResultUsesSnakeCaseWireKeys`：
   `shell` 调用 120s 超时。同类其余 7 条通过，`origin/main` 上同样红。
2. `TinadecCore.AgentFramework.Tests` 间歇性失败 4–5 条，两次跑失败集合不同
   （基线 752/756、本分支 751/756），两侧都红，根因未定。
3. `tests/Tinadec.Contracts.Tests` 引用不存在的 `src/TinadecCore`，自初始提交
   即损坏，属遗留项目。
