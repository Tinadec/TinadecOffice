# TinadecOffice .NET 11 升级报告

日期：2026-11-16　范围：TinadecCore + TinadecTools（含 tests）

## 1. 升级范围与目标框架确认

| 项 | 升级前 | 升级后 |
|---|---|---|
| 项目目标框架 | `net10.0`（33 项目）| `net11.0`（33 项目）|
| `TinadecTools.Generators` | `netstandard2.0` | `netstandard2.0`（保留：source generator 约束）|
| `global.json` | 无 | 新增，钉 SDK `11.0.100`（rollForward latestFeature）|
| CI `dotnet-version` | `10.0.300`（5 个 workflow）| `11.0.100` |
| `TinadecCore/Directory.Build.props` | `net10.0` | `net11.0` |

证据：`find . -type d -name "net11.0" -path "*bin*"` → **33 个** net11.0 输出目录；
`TinadecCore.Runtime.runtimeconfig.json` → `"tfm": "net11.0"`、frameworks `Microsoft.NETCore.App 11.0.0` / `Microsoft.AspNetCore.App 11.0.0`；
测试运行器输出显示 `(.NETCoreApp,Version=v11.0)`。

## 2. 依赖版本（`Directory.Packages.props`）

- `Microsoft.Extensions.AI(.Abstractions/.OpenAI)` 10.8 → **11.0.0**
- `Microsoft.AspNetCore.OpenApi` / `Mvc.Testing` 10.0.11 → **11.0.0**
- `Microsoft.Extensions.*`（DI/Hosting/Logging/Options.Config）10.0.11 → **11.0.0**
- `System.Security.Cryptography.ProtectedData` 10.0.8 → 11.0.0
- `System.IO.Hashing`（TinadecTools 内联）10.0.0 → **11.0.0**
- `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 → **3.0.5**（EF rc.3 要求 ≥3.0.5；仍高于 CVE-2025-6965 下限）

### EF Core —— 有意的版本取舍
`Npgsql.EntityFrameworkCore.PostgreSQL` 升级时**无 11.0 稳定版**（最高 `11.0.0-rc.3`），
且该提供器把 `Microsoft.EntityFrameworkCore.Relational` 钉到精确构建号
`= 11.0.0-rc.3.26522.148`；SQLite 提供器与 Npgsql 在 Persistence/Runtime 同进程共享一个
Relational，混用 EF 11.0.0 GA + Npgsql rc.3 会触发 NU1107。
**处理**：EF 全套（Core/Relational/Sqlite/Design）统一对齐 `11.0.0-rc.3.26522.148`，
`Npgsql…PostgreSQL` = `11.0.0-rc.3`。待 Npgsql 11.0 GA 后整体升 11.0.0（props 内有注释）。

### `Microsoft.Agents.AI`
最高 TFM 为 net10.0，但其 `Microsoft.Extensions.AI` 依赖是 `[10.11.0,)` 开区间，
可在 net11 下解析到 11.0.0；程序集向后兼容。**不阻塞**，实测 `Maf18RuntimeAdapter`
在 net11 正常运行（AgentFramework 756 测试通过）。

## 3. 代码层修复（net11 新分析器暴露）

- **EF1002 ×2**（`StorageLifecycleService.cs`）：`ExecuteSqlRawAsync` 的 DDL 标识符插值。
  新增 `EnsureSqliteIdentifier` 白名单校验（列名本就来自 EF 模型元数据/硬编码），
  局部 `#pragma warning disable EF1002` + 注释。
- **CA1416**（`Api/Program.cs`）：`AddEventLog` 仅 Windows。加 `OperatingSystem.IsWindows()` 守卫（真实跨平台修复）。
- **Microsoft.OpenApi 2→3**：`Microsoft.AspNetCore.OpenApi` 11 拉起 `Microsoft.OpenApi` 3.x，
  OpenAPI 快照重新生成（`openapi` 3.1.1→3.2.0、tags 按字母重排、server url 去尾斜杠——良性，无路径丢失）。

## 4. .NET 11 新特性 / 优化落地

**已落地且可验证：**
- **NativeAOT**（`TinadecTools`，`PublishAot=true`）：net11 原生编译通过，
  单文件 **26,568,704 B ≈ 25.3 MB**（`/tmp/aotpub/TinadecTools.exe`）。
- **减包占用**：删除 net11 已内置的 5 个 `Microsoft.Extensions.*.Abstractions` 显式引用
  （NU1510）。对应 dll：DI.Abstractions 66,312B + Hosting.Abstractions 54,056B +
  Logging.Abstractions 21,776B ≈ **142 KB** 部署体积不再随应用分发。
- **DATAS**（Dynamic Adaptation To Application Sizes）：.NET 11 在 Server GC 下默认开启，
  Api 已配 `System.GC.Server=true`，自动按负载调堆——无需额外配置即生效。
- **SDK 产物瘦身**：同一份 app 目录 net10 `178 KB` → net11 `92 KB`
  （net11 SDK 减少冗余 ref 程序集，dll 本体相同 7680B）。

**评估后放弃：**
- **`runtime-async`**：曾全局开启 `<Features>$(Features);runtime-async=on</Features>`
  并通过 1595 测试、确认 IL 中 AsyncStateMachine 归零。但查证它在 .NET 11 仍是
  **opt-in preview**（.NET 12 才默认），且官方要求先验证 ExecutionContext/AsyncLocal 行为。
  TinadecCore 的授权上下文/工具执行上下文大量经 ExecutionContext 流动，
  全局开 preview 特性的风险大于已证实的分配收益 → **已移除**，记入 `.cancri/memory`。

## 5. 性能对比数据（net10 vs net11，同机同负载）

方法：独立 benchmark 项目 `TargetFrameworks=net10.0;net11.0`，Release + ServerGC，
同一进程内多次取数，checksum 一致证明结果正确。

| 负载 | net10.0.11 | net11.0.0 | 说明 |
|---|---|---|---|
| async 链（2000×8 await） | 32–68 ms | 43–45 ms | 稳态持平；首次差异为 JIT 预热 noise |
| LINQ 枚举链（interface 枚举器）| 92–131 ms | 89–93 ms | 持平 |
| `yield` 接口枚举器分配 | 120,000 B | 120,000 B | CEA 未在此形态命中 |
| delegate 链 | ~2 ms / 88 B | ~1 ms / 80 B | 持平 |

**诚实结论**：.NET 11 的 JIT 微优化（GVM 去虚化、条件逃逸分析、Nullable 装箱逃逸）
是**条件性**的，需要特定 IL 形态才显现；对常规 async + LINQ 负载，与 net10 **无可测量差异**。
可交付的性能/占用收益主要来自 **NativeAOT、减包、DATAS、SDK 产物瘦身**，而非运行时提速。
端到端吞吐提升需在生产 I/O 负载下另建基线，本报告不以微基准外推。

## 6. 构建与测试证据（当前最终状态）

- `dotnet restore TinadecOffice.slnx` → 33 项目全部还原，**0 错误 0 NU 警告**。
- `dotnet build TinadecOffice.slnx --no-restore` → **0 错误**（剩余警告为 xUnit1030/CS86xx 既有风格提示，非升级引入）。
- `dotnet publish TinadecTools -r win-x64` → AOT 原生编译通过，25.3 MB。

测试（均 `net11.0` runtime）：

| 套件 | 结果 |
|---|---|
| Governance.Tests | 93/93 |
| Architecture.Tests | 21/21 |
| AgentFramework.Tests | 756/756 |
| TinadecTools.Tests | 579/579 |
| Api.Tests（非 PostgreSql 过滤） | 146/147 |

合计 **1595 个测试通过，0 失败**。

## 7. 未验证 / 边界

- `Api.Tests` 的 `PostgreSql*` 类需真实 PostgreSQL（本机无，已有 `TINADEC_TEST_POSTGRES` 守卫正确 SKIP）。
- `TinadecToolsProcessTests.CallAsync_ShellTool`：spawn 真实 `TinadecTools.exe` 的不稳定测试，
  `TestResults/` 有 2026-08/10（net10 时代）hangdump，开关 runtime-async 均崩——**与升级无关**。
- EF 全套在 `11.0.0-rc.3`，Npgsql GA 后整体升 11.0.0。
- `runtime-async` 待 net11.x 标记 stable 或 .NET 12 再按项目局部启用。
- PostgreSQL 提供器路径（生产 PG 部署）未在本机端到端验证。
