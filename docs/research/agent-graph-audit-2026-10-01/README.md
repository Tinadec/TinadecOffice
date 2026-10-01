# 图工程隔离复查探针

对应 [复查报告](../../agent-graph/review-2026-10-01.zh-CN.md)，基线 `f67ce4f`。

这些是诊断程序，不是把错误行为固定下来的回归断言。修复后输出应变化，应将相应场景移入正式测试。宿主使用现有 API 测试工厂和临时 SQLite；治理模型由可注入的假执行器代替，不使用外部模型。

从仓库根目录执行：

```powershell
powershell -NoProfile -File scripts/setup-dotnet-env.ps1 run --project docs/research/agent-graph-audit-2026-10-01/probes/AuditProbes.csproj
```

在已构建 Core 的工作树上，可避免重新构建其引用：

```powershell
powershell -NoProfile -File scripts/setup-dotnet-env.ps1 build docs/research/agent-graph-audit-2026-10-01/probes/AuditProbes.csproj -p:BuildProjectReferences=false
powershell -NoProfile -File scripts/setup-dotnet-env.ps1 run --no-build --project docs/research/agent-graph-audit-2026-10-01/probes/AuditProbes.csproj
```

程序打印以 `AUDIT ` 开头的 JSON。基线观察：

| 探针 | 基线输出 | 要验证的修复 |
|---|---|---|
| `spawnable_definition` | 提示词为空，模板类型没有该字段，职责描述仍在 | 通过正式冻结/模型输入测试证明职责指令被保留 |
| `same_run_overlapping_write_scope` | 两个不同任务均获准 | 任务级冲突与显式父子授权 |
| `run_filtered_topology` | 返回另一 run 的租约 | 完整数据对象可见性过滤 |
| `wake_after_transient_model_failure` / `wake_after_retry` | pending 来源为零，重试直接 done；执行器只调用一次 | 本次领取的输入在成功 ACK 前不丢失 |
| `orphaned_running_wake` | 旧 running 行一直 running | 领取期限、fencing 与崩溃重放 |

本轮完整日志及 TRX 在本地 `TestResults/architecture-audit-20261001/`。真实外部模型、PostgreSQL、多宿主和沙箱实机验收没有由这些探针覆盖。
