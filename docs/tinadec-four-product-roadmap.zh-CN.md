# Tinadec 四产品全面推进 · 战略路线图

> 本文是规划文档，不含代码改动；逐阶段实施需各自立项并遵循各仓库既有门禁。
> 生成日期：2026-08-28。参考工作区先例：opencode、openchamber、Microsoft Agent Framework、deepseek-harness。

## Context

Tinadec 是四个独立版本化产品组成的家族：**TinadecCore**（.NET 10 / MAF 1.18 智能体编排运行时，唯一状态权威）、**TinadecGateway**（Elysia 薄代理 BFF）、**TinadecTools**（.NET 审批感知工具宿主）、**TinadecApps**（前端，desktop/web）。当前以 Desktop → Gateway → Core → TinadecTools 的 Windows 优先集成拓扑运行。

本路线图回答两个判断问题，并给出四产品的分阶段推进规划。

---

## 一、两个判断问题的结论

### Q1 · 前后端是否已完成分离？—— **已高度分离，仅剩一个结构性缺口**

| 维度 | 现状 | 证据 |
|---|---|---|
| 构建独立 | ✅ | 前端 `vite build`，无任何 .NET import，无构建期后端依赖 |
| 运行时契约通信 | ✅ | 全部经 HTTP/SSE/WS 走 Gateway；`api.ts:1645` `gatewayUrl ?? 'http://127.0.0.1:48730'` |
| Gateway 薄代理 | ✅ | `TinadecGateway/src` 16 个小型 mapper，无业务状态；旧 BFF 聚合已删（`model-center/overview` 返回 404） |
| 后端契约单一事实源 + CI 门禁 | ✅ | Gateway `/docs/json` + `openapi.snapshot.test.ts` 快照 drift 门（`git diff --exit-code`） |
| 渲染器与平台解耦 | ✅ | `apps/web` 经 `webShim.ts` 复用 desktop 渲染器 |
| **前端 DTO 消费端** | ❌ **唯一缺口** | `api.ts`（2200 行）+ `generated/client.ts`（占位）均为**手写镜像**；`generate:client`/`check:drift` 脚本已就位但**未启用**，注释仍引用 Core 内部 `.cs` 文件，存在知识耦合与漂移风险 |

> **「Desktop 直连 Core」不是缺口**：经确认，Desktop 属 TinadecApps 层、有意不直连 Core，Gateway 是永久边界。这与现状代码一致，应从"待补缺口"重新定性为"架构决定"（见第二部分）。

### Q2 · TinadecCore 能否像 MAF / Opencode SDK 一样独立运行？—— **服务已可独立；库嵌入设计就位、形态已近 MAF，但尚未成熟为可独立交付产品；不具备 Opencode SDK 形态**

| 维度 | 现状 | 证据 |
|---|---|---|
| 独立服务 | ✅ 已可 | `dotnet publish Api` 独立进程（端口 48731） |
| 库嵌入设计 | ✅ 就位 | `AddTinadecCore()` / `AddTinadecCoreMinimal()` 公开组合入口（`Runtime/TinadecCoreServiceCollectionExtensions.cs:30,83`）；`Abstractions/Ports` 23 个 DI 端口；Contracts/Abstractions/Runtime 及模块均 `IsPackable=true`+MIT |
| MAF 隔离 | ✅ 框架式引用 | 仅 `DmaEA` 引 4 个 `Microsoft.Agents.AI` 包；唯一接触点 `DmaEA/Maf18RuntimeAdapter.cs`；Architecture.Tests 强制 MAF/EF/ASP.NET 类型不外泄 |
| 零反向依赖 | ✅ | 对 Desktop/Gateway/Tools 无编译期引用；唯一耦合 `Tools/TinadecToolsProcessManager.cs` 拉子进程，缺失时降级不阻断启动 |
| **成熟度缺口** | ❌ | ①未发布任何 NuGet feed；②打包文档相对路径在**独立镜像**里损坏（详见第八节，权威源其实是通的）；③HTTP 端点层在**不可打包**的 Api 项目，嵌入者只拿 DI 服务、需自建 HTTP；④无容器镜像、无 OIDC 身份适配器、无多租户调度 |

**形态对标**：
- **对比原版 MAF**：已很接近——同为「NuGet 库 + `AddXxx()` 组合进宿主 + 附带可独立运行的服务」。差距仅在未发布、无 Hosting/可复用 HTTP 层。
- **对比 Opencode SDK**：**形态不同且缺失**——Opencode SDK 是从 OpenAPI 生成的 TypeScript 客户端，供各前端远程消费无头服务。TinadecCore 没有这样的生成式客户端库。**这条缺失正是把 Q1 缺口与 Q2 缺口串起来的主线**（见下）。

### 关键洞察 · 一条主线贯穿两问

**「契约即代码」的 codegen 管线**（后端 OpenAPI → 自动生成客户端 → 前端/宿主纯消费）同时补上 Q1 与 Q2 的缺口，且工作区有两条可直接对标的先例：
- `opencode/packages/httpapi-codegen`：从服务端 HttpApi 生成 `packages/client`，`check:generated` 用 `git diff --exit-code` 防漂移；依赖严格单向（客户端运行时绝不依赖 Core/Server）。
- `deepseek-harness/packages/typert + api`：类型图自动生成 RPC 协议网关。

---

## 二、本次确认的架构决定（需回写 AGENTS.md）

1. **Desktop 属 TinadecApps 层，不直连 Core。** Gateway（48730）是 Desktop 永久且唯一的传输边界。
   - 影响：Desktop「直连 Core 双轨」**不做**；`electron/serviceDiscovery.cjs` 里 Core 候选维持"仅信息展示"。
   - 冲突点需回写：根 `AGENTS.md` 现有「TinadecApp 可直连 Core，Gateway 可选」表述对 Desktop 场景被本决定覆盖（其它 App 形态仍可直连的产品契约保留，但 Desktop 明确不直连）。
2. **Gateway 上升为稳定的前端契约边界**：其外部 OpenAPI（`/docs/json`）是 Apps 消费的唯一契约源，这强化了 Gateway 的定位而非弱化。
3. **事实源已核实（2026-08-28）**：独立仓库 `TinadecCore`（`github.com/Tinadec/TinadecCore`）是 2026-08-23 从 `TinadecOffice` 抽取的开源镜像（`SYNC.md`：GPL→MIT 换证），但此后**已独立提交、与权威源分叉**（关键文件 `TinadecCoreServiceCollectionExtensions.cs` 内容已不同）。**权威且活跃的开发主场是 `TinadecOffice/TinadecCore`**（最新提交 2026-08-28，含模型/智能体控制面重构），且**当前没有自动化 Core 同步脚本**（仅 `sync-tinadec-ui.mjs`）。这是 A 线推进前必须先解决的所有权问题。

---

## 三、目标终态（四产品北极星）

| 产品 | 目标形态 | 对标 |
|---|---|---|
| **TinadecCore** | 无头、可独立运行的编排运行时，**双形态**：既作独立服务，又作可嵌入任意 .NET 宿主的 NuGet 库；Contracts/Abstractions/Runtime 稳定，HTTP 面可挂载 | opencode 无头服务 + MAF 可嵌入库 |
| **TinadecGateway** | 薄代理 + BFF，前端唯一边界；外部 OpenAPI 为前端契约单一事实源 | opencode serve 边界 |
| **TinadecTools** | 可插拔 Tool Provider；Core 经通用契约而非硬编码子进程交互 | dsh 能力插件 + 类型化 RPC |
| **TinadecApps** | 多宿主前端（desktop/web）消费**生成的** TS 客户端，无手写 DTO 镜像 | opencode TUI/Web 纯 SDK 消费 |

---

## 四、三条推进主线（按本次优先级排序）

### 主线 A · TinadecCore 可独立交付（最高优先）
- **A0（前置，新）解决双仓库所有权**：先拍板独立开源镜像 `TinadecCore` 的定位——是「权威源的一次性快照（需重建同步/重抽取）」还是「独立演进的开源主场（需把活跃开发迁过去）」。在此之前不做跨仓库的打包修改，避免加深分叉。
- **A1 打包路径布局无关化**：在**权威源**把打包文档放进 `TinadecCore/docs/` 并将各 `*.csproj` 的 `PackageReadmeFile`/`None Include` 从 `..\..\docs\` 改为 `..\docs\`，使嵌套与抽取两种布局都能 `dotnet pack`。（注意：权威源当前 `..\..\docs\` 已可解析到 `TinadecOffice/docs/`，打包本是通的；此步是为布局无关与未来抽取做准备，须待 A0 定夺后执行。）
- **A2 建发布管线**：建立内部 NuGet feed + CI 发布（Contracts/Abstractions/Runtime 为稳定包，模块为 Runtime 的实现依赖包，同 feed 发布）；对照 `docs/tinadec-core-packaging.zh-CN.md` 落地其"尚未发布"部分。
- **A3 HTTP 面可复用**：把当前只在不可打包 Api 里的端点映射抽为可挂载能力（如 `MapTinadecCore(this IEndpointRouteBuilder)` 扩展，或独立可打包 `TinadecCore.AspNetCore`），使嵌入宿主无需自建 HTTP。
- **A4（延伸）**：OIDC 外部身份适配器、多租户调度、容器镜像、readiness 诊断补齐。

### 主线 B · 契约即代码 codegen 管线（最高杠杆，串联前后端）
- **B1 契约事实源已就位**：Core 内部 `/openapi/core.json` + Gateway 外部 `/docs/json` + 快照门禁（维持现状，作为基线）。
- **B2 启用 codegen 引导（安全首步）**：用已提交的 `TinadecGateway/tests/__snapshots__/openapi.external.json` 作为离线输入生成 `src/generated/schema.d.ts`（无需启动 Gateway），接上 `check:drift`。**本阶段不全量替换** `api.ts` 2200 行（那是高风险大改），仅建立「生成类型 + 漂移门禁」管线。
- **B2.5（后续）渐进迁移**：按域逐段把 `api.ts` 的手写类型替换为 `schema.d.ts` 的 `components['schemas']`，最后收敛到 `openapi-fetch` 客户端。
- **B3 接入门禁**：`check:drift`（`git diff --exit-code -- src/generated/`）入 CI，契约漂移即失败。
- **B4（延伸，对标 opencode）**：演进为从 Core HttpApi 定义直接生成，减少 Gateway 手写 mapper 层，向「客户端绝不依赖服务端实现」的单向依赖收敛。

### 主线 C · Tools 通用 Tool Provider 契约
- **C1 抽象端口**：在 Core 侧抽出通用 Tool Provider 契约（manifest 冻结、调用、审批钩子、恢复语义），与「直连 TinadecTools 子进程」解耦（当前实现在 `Tools/TinadecToolsProcessManager.cs`）。
- **C2 适配器化**：将 TinadecTools 重构为该通用契约的**一个**实现（保留 manifest v2、BOM-free UTF-8 管道、审批门）。
- **C3（延伸）**：允许其它 Tool Provider（MCP server、其它工具宿主）经同一契约接入；MCP pass-through 已在 `TinadecTools/Tools/Mcp` 有原型。

---

## 五、阶段化路线图（时序与依赖）

```
Phase 0（立即可做 · 小而高价值 · 可并行）
  ├─ B2 启用 codegen 引导（离线生成 schema.d.ts + check:drift，不替换 api.ts）
  └─ A0 解决双仓库所有权（拍板镜像定位，A1 的前置）

Phase 1（地基）
  ├─ A1 打包路径布局无关化（待 A0 定夺）
  ├─ A2 NuGet feed + CI 发布
  ├─ A3 HTTP 可复用挂载层（MapTinadecCore）
  └─ B2.5/B3 api.ts 渐进迁移 + drift 门禁入 CI

Phase 2（能力）
  ├─ C1/C2 Tool Provider 契约 + TinadecTools 适配器化
  └─ B4 codegen 向 Core HttpApi 直生成演进

Phase 3（扩展）
  ├─ A4 身份/多租户/容器化
  └─ C3 多 Tool Provider 接入
```

**依赖关系**：A0→A1→A2→A3 为主链；B2 独立可先行；B2.5/B3 依赖 B2；C 线相对独立，可与 A/B 并行。

---

## 六、关键既有资产（执行时复用，勿重写）

| 用途 | 路径 |
|---|---|
| Core 组合入口 | `TinadecCore/Runtime/TinadecCoreServiceCollectionExtensions.cs`（`AddTinadecCore`/`AddTinadecCoreMinimal`） |
| 打包配置 | `TinadecCore/Directory.Build.props`、各模块 `*.csproj`（`IsPackable`/`PackageId`） |
| 打包文档 | `TinadecOffice/docs/tinadec-core-packaging.zh-CN.md`（权威源）；独立镜像在 `TinadecCore/docs/` |
| MAF 适配隔离 | `TinadecCore/DmaEA/Maf18RuntimeAdapter.cs` |
| 前端契约消费 | `apps/desktop/src/api.ts`、`apps/desktop/src/generated/client.ts` |
| codegen 脚本（已就位） | `apps/desktop/package.json` `generate:client` / `check:drift` |
| Gateway 契约源 | `TinadecGateway/src/index.ts`（`/docs/json`）、`src/coreClient.ts` |
| 契约快照门禁（可作离线输入） | `TinadecGateway/tests/__snapshots__/openapi.external.json`、Core `openapi.core.json` |
| 工具子进程管理（待适配器化） | `TinadecCore/Tools/TinadecToolsProcessManager.cs` |

## 七、验证方式（各主线）

- **A**：`dotnet pack` 在权威源全绿；新 NuGet 包可被干净宿主 `restore` 并 `AddTinadecCore()`（+ 未来 `MapTinadecCore()`）启动出 `/api/v1/health`。
- **B**：`schema.d.ts` 生成成功且 `npm run build`/`vitest` 不回归；`check:drift` 在人为改契约时失败。
- **C**：Core 在替换/移除 TinadecTools Provider 实现时启动不崩；接入第二个 Provider 跑通一次审批-调用闭环。

## 八、风险与边界

- **双仓库分叉（已核实，需先决）**：`TinadecOffice/TinadecCore`（权威、活跃、今日仍在提交）与独立开源镜像 `TinadecCore`（2026-08-23 抽取、此后独立提交、已分叉）并存，且无自动化 Core 同步。**打包在权威源本是通的**；路径损坏只出现在滞后镜像。推进 A 线前必须先定镜像定位（A0），否则会加深分叉。
- **AGENTS.md 回写**：本决定的"Desktop 不直连 Core"与既有"Gateway 可选"表述冲突，落地时需在同一变更中更新相关 `AGENTS.md` 元数据。
- **MAF 锁定**：`Microsoft.Agents.AI` 1.18.0 版本锁定，MAF 类型保持隔离在 `DmaEA` 适配器后，任何推进不得破坏该边界。
- **范围边界**：本路线图为规划文档，未包含任何代码改动；逐阶段实施需各自立项并遵循各仓库既有门禁（Core 的 `dotnet test`、Gateway/Apps 的快照与单测）。

---

## 九、2026-08-29 全面推进落账

本轮按「边开发边提交」完成，权威源保持 `TinadecOffice/TinadecCore`。A0 已拍板：独立镜像定位为**权威源的受控同步快照**（不再独立演进），自动化同步由 `scripts/sync-tinadec-core.mjs` 承担（幂等、文件级暂存、SYNC.md 记账）。

| 项 | 状态 | 落地 |
|---|---|---|
| A0 | ✅ | 镜像=受控快照；`scripts/sync-tinadec-core.mjs` 实跑（镜像已同步到 `07ec3b0`，`dotnet pack Contracts` 独立布局通过） |
| A1 | ✅ | `Contracts/Abstractions/Runtime`（+AspNetCore）readme 改 `..\docs\`；文档副本入 `TinadecCore/docs/`；嵌套与镜像双布局 pack 均通 |
| A2 | ✅（待密钥） | `.github/workflows/core-pack.yml`：restore→build→全量测试→解决方案级 pack→artifact；`v*` tag 触发 publish job（等 `NUGET_API_KEY` 配置，tag 注入 `-p:PackageVersion`） |
| A3 | ✅ | 可打包 `TinadecCore.AspNetCore`（plain SDK + `FrameworkReference`）：`AddTinadecCoreHttp()` / `UseTinadecCoreExceptionHandler()` / `MapTinadecCore()`；13 端点组 + health/manifest/readiness 从 Api 物理移入；Api 瘦身为组合宿主；OpenAPI 快照零漂移、198→199 测试全绿 |
| A4 | ✅（切片） | `TinadecCore/Api/Dockerfile`（sdk:10.0→aspnet:10.0，`/data` 卷，48731）+ core-pack.yml `docker-image` job（只构建不推送）；OIDC 适配器/多租户调度延后 |
| B2 | ✅ | `generate:client`（openapi-typescript@6，离线、字节确定）+ `check:drift`；`schema.d.ts` 入库 |
| B2.5 | ✅（首段） | Gateway 外部快照 23→41 命名 schema（`src/externalDtoOpenApi.ts`，`detail.responses` 文档级、不装运行时校验）；`generated/client.ts` 响应 DTO 全部改为 `components['schemas']` 别名（`AgentPackEnvelopeDto` 保持请求侧宽松）；`api.ts` 2200 行镜像的后续批次按同一模式进行 |
| B3 | ✅ | `.github/workflows/contracts-drift.yml`：Gateway 快照重写 diff 门 + Desktop `check:drift` 双 job |
| C1/C2 | ✅ | 通用端口 `IToolProvider` 已在 `Abstractions/Ports/IToolDispatcher.cs`，TinadecTools 是其适配器；新增进程内假 Provider E2E（替换 DI 注册后 审批→resume→dispatch 全闭环、零子进程、工作区无落盘）钉死契约解耦 |
| C3 | ⏳ | 端口已泛化；第二个真实 Provider 选型延后 |

### 延后清单（需外部资源或另行立项）
- **真实 NuGet feed 接入**：仓库 Secrets 配置 `NUGET_API_KEY`（可选变量 `NUGET_SOURCE` 指内部 feed），从 main 打 `v*` tag 即发布
- **容器 registry 推送**：待选定镜像仓库；工作流已备 build-only job
- **`api.ts` 2200 行全量迁移与 `openapi-fetch` 收敛**（B2.5 后续批次，模式已由 client.ts 建立）
- **B4**：从 Core HttpApi 直生成客户端，收敛 Gateway 手写 mapper 层
- **A4 延伸**：OIDC 外部身份适配器、云端多租户调度
- **C3**：第二个真实 Tool Provider（MCP server / 其它工具宿主）接入

### 验证基线（2026-08-29 总闸）
- Core：`TinadecCore.slnx` 199/199 全绿（Governance 12 + AgentFramework 55 + Architecture 11 + Api.Tests 121，含假 Provider E2E；Cli 探测/活跃运行上限两用例在全量并行下偶发抖动，隔离与复跑均绿）
- Gateway：`bun test` 44/44；外部快照 41 schema、166 路径
- Desktop：vitest 312 过 / 0 败（14 个 NotificationIslandHost 用例按既有环境问题跳过）；typecheck 仅余 6 个与契约面无关的既有 Monaco/vi.fn 错误
- `generate:client` 重跑字节级一致（sha256 相同）；`dotnet list package --vulnerable --include-transitive` 26 项目零暴露
