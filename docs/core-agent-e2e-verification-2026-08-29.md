# TinadecCore 运行时 Agent 健康度端到端验证报告

**日期**：2026-08-29 · **验证提交**：2613bfd (main) · **方式**：真实 HTTP 端到端实测（本地脚本化 OpenAI 端点驱动，零真实 API key）

---

## 一、总结论

**TinadecCore 的运行时 agent 主链路功能正常，agent 能正常在工作区工作** —— 已在真实 HTTP、真实 OpenAI SDK、真实 TinadecTools 子进程下完成端到端验证。过程中发现并修复了 **1 个真实 bug**（升级路径 schema 漂移），该 bug 曾导致审批决策 500、run 永久卡在 `awaiting_user`。

## 二、端到端验证记录（全链路通过）

| 步骤 | 结果 |
|---|---|
| `POST /projects`（path=沙箱目录） | 201，workspace root 绑定成功 |
| `POST /sessions`（不传 mode_version_id） | 201，bootstrap 的 WorkspaceDefaults 自动生效 |
| `POST /sessions/{id}/interactions`（agent_mode=agent） | 201，run 被 admission |
| 引擎推进（假模型 7 次调用） | planning 出任务图 → coordination → execution 返回 write_file tool_call |
| 审批门 | run 转 `awaiting_user`，pending approval 出现，**阻塞不放行**（符合设计） |
| `POST /approvals/{id}/decision` 放行 | granted → run 恢复执行 |
| **TinadecTools 子进程写文件** | **`hello-from-agent.txt` 真实落盘沙箱，内容与 tool_call 指定完全一致** |
| supervision → finalization | 假模型返回 pass → 会议收口 → run `completed` |
| 中途重启 Core | run 与 pending 审批完好保留，放行后正常恢复（**持久化恢复验证**） |

关键配置入口（已验证可用）：`PUT /api/v1/model-providers/{id}`（If-Match 为**纯数字** revision；`api_key` 写入 SecretStore；整个 body 存为 model-config，`base_url`/`protocol`/`model` 随之生效）。

## 三、发现并修复的真实 bug

**升级路径 schema 漂移**（修复已落地并通过回归测试）：

- **现象**：审批决策端点 500（`SQLite Error 1: table capability_leases has no column named nonce_secret_reference`），run 卡死在 `awaiting_user` 直到 30 分钟审批过期。
- **根因**：`GovernanceNonceMaterial` 迁移（202608220008，SQLite/PG 两侧）是**空操作**。governance 表由 bootstrap 创建、无迁移历史，因此 8/22 之前创建的库的 `capability_leases` 永远不会被补 `nonce_secret_reference` 列、遗留的 NOT NULL `nonce` 列（新模型已 NotMapped）也永远不会被删。lifecycle 侧 `approval_requests` 有真实迁移覆盖 —— 这种不对称正是漂移只发生在 governance 侧的原因。
- **修复**：`TinadecCore/Persistence/DbContextMigrationParticipant.cs` 的 `DbContextSchemaBootstrapper` 新增**列级调和**（`ReconcileModelColumnsAsync`）：对既有表比对模型列与实际列（SQLite `pragma_table_info` / PG `information_schema.columns`），缺失即 `ALTER TABLE ADD COLUMN`（必填列带类型零值默认），并定向、有守卫地 drop 遗留 `capability_leases.nonce`。幂等，双 provider 通用。
- **回归测试**：`TinadecCore/tests/TinadecCore.Governance.Tests/SchemaReconciliationTests.cs`（模拟旧 shape 库 → 跑 bootstrap → 断言列调和 + EF 插入 lease 成功）。
- **本机 DB 处理**：已手动补列/删列（与代码修复等价），下次启动起由代码自动维护。

## 四、测试基线

| 套件 | 结果 |
|---|---|
| Core 全量（4 项目 219 用例） | **隔离运行 100% 通过** |
| ToolChainEndpointTests（含真实子进程 write_file 落盘） | 2/2 绿 |
| FullDuplexEndpointTests | 25/25 绿 |
| Governance.Tests（含新增回归测试） | 13/13 绿 |
| Gateway `bun test src` | 44/44 绿 |
| 并行全量跑 | 有 flake（ToolChain/FullDuplex/CliRuntime/TinadecToolsProcess 互扰，所有失败项隔离复跑均绿） |

**并行 flake 中的已知锐边**（未修，建议关注）：run 已处于 `awaiting_user` 时，引擎再次尝试转 `awaiting_approval` 会被 `RunStatusMachine` 拒绝（`StorageLifecycleService.cs:1107` 不允许 `awaiting_user→awaiting_approval`），run 以 runtime error 失败。仅在高并发/互扰场景触发。

## 五、环境注意事项

1. **模型 provider 已被本次验证改写**：`bc12683f`（显示名 "Local Fake E2E"）现指向已停止的本地假服务端 `http://127.0.0.1:48799/v1`。下次真实使用前需在模型中心重新指向真实端点。
2. `data/tinadec.db` 中留有本次验证的 project（e2e-sandbox）/session 记录，可在 UI 中清理。
3. `TinadecTools:DefaultWorkspaceRoot` 保持 null 是安全的 —— workspace root 来自 project 的 `path` 字段；`tool-layer-readiness` 显示 tool_count=0 属预期 fail-closed 行为。
4. 已知桩（不影响主链路）：市场/扩展、MCP/ACP 路由、debug、model-settings、prompt 版本操作、`tools/shell` 恒 501 或空数组；`/api/v1/model-readiness` 恒 provider_count=0。
5. 契约快照落后 Core 5 个提交（memory-items×2、approvals/{id}、recovery-decision 未进 Gateway 快照），且 `check:drift` 只守 Gateway→Desktop 方向 —— 建议补 Core→Gateway 门禁。

## 六、本次变更清单（未提交）

- `TinadecCore/Persistence/DbContextMigrationParticipant.cs` —— schema 列级调和修复
- `TinadecCore/tests/TinadecCore.Governance.Tests/SchemaReconciliationTests.cs` —— 新增回归测试
- `AGENTS.md` —— 回写修复记录 + 过期信息更正（NotificationIslandHost 14 失败实际已 describe.skip）
- 验证用的沙箱目录、假模型脚本、临时日志已全部清理，git 工作区仅含上述三个有意变更
