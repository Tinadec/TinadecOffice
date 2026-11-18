# TinadecTools / MCP 扩展：TODO

模块ID：`TOOLS-MCP` · 初始基线：2026-10-05，b6115e6 + 当前工作树。

本文件是该模块任务的唯一编辑入口；总TODO由本文件生成。任务ID长期稳定，完成或取消后保留记录；每项任务记录工作范围与验收条件。

状态：待核查 / 未开始 / 待方案 / 进行中 / 阻塞 / 待验收 / 已完成 / 不做 / 已被替代。优先级是初始建议，可在逐模块分析后调整。

<a id="tools-mcp-001"></a>

### TOOLS-MCP-001 完成 TinadecTools / MCP 扩展 的逐功能审计与模块图精化

- 类型：核查
- 状态：待核查
- 优先级：P2
- 主责模块：TOOLS-MCP
- 前置依赖：未细化；开工前在此列出实际Task ID，不能把全部关联模块当硬依赖
- 关联功能：待逐功能拆分后绑定本模块Feature ID
- 完成证据：未产生；本轮仅建立任务与源码基线

**问题与目的**

已有职责投影和初始源码事实，还没有把每个用户/调用场景逐项拆分并完成实现、测试和运行证据对账。

**验收条件**

- [ ] 拆分STATUS.md中的聚合能力，每个功能分配稳定feature id、明确输入/输出、失败与权限边界。
- [ ] 逐条定位实际实现、活动测试和历史报告；把源码可见、历史验证与本轮验收分别记录。
- [ ] 至少明确成功、错误/取消、权限和持久化/恢复场景中哪些适用；未适用的写出理由。
- [ ] 精化ARCHITECTURE.md中的调用/数据流；每个有向关系提供源码依据，职责关联不冒充编译依赖。
- [ ] 将确认缺口登记独立TODO，写明目标行为、范围、前置依赖和可执行验收条件；无证据的保持待核查。

**初始证据**

- [TinadecTools/Tools/Mcp/McpClientPool.cs:63](../../../../../TinadecTools/Tools/Mcp/McpClientPool.cs#L63)

<a id="tools-mcp-101"></a>

### TOOLS-MCP-101 用真实 stdio MCP server 验收目录与调用生命周期

- 类型：验收
- 状态：待核查
- 优先级：P2
- 主责模块：TOOLS-MCP
- 前置依赖：未细化；开工前在此列出实际Task ID，不能把全部关联模块当硬依赖
- 关联功能：待逐功能拆分后绑定本模块Feature ID
- 完成证据：未产生；本轮仅建立任务与源码基线

**问题与目的**

mock pass-through 测试存在，实际配置、外部进程生命周期与调用审批需当前证据。

**验收条件**

- [ ] 真实 server 的 list/search/schema 与 invoke 可对应，未知 server/工具给出可理解错误。
- [ ] 分别验证批准、拒绝、server 报错、调用取消、server crash 和重新连接。
- [ ] Tools 退出后核查 MCP 子进程和连接清理，记录外部工具副作用边界。

**初始证据**

- [TinadecTools/Tools/Mcp/McpClientPool.cs](../../../../../TinadecTools/Tools/Mcp/McpClientPool.cs)
- [TinadecTools/Tools/Mcp/McpServerRepository.cs](../../../../../TinadecTools/Tools/Mcp/McpServerRepository.cs)
- [tests/TinadecTools.Tests/McpPassThroughTests.cs](../../../../../tests/TinadecTools.Tests/McpPassThroughTests.cs)

<a id="tools-mcp-102"></a>

### TOOLS-MCP-102 按 Core 资源版本绑定 MCP 连接

- 类型：实现
- 状态：已完成
- 优先级：P1
- 主责模块：TOOLS-MCP
- 前置依赖：CORE-TOOLS-101
- 关联功能：TOOLS-MCP-F003
- 范围与验收：托管上下文绝不回退文件；指纹包含资源ID、版本及环境，新版本开启新连接，旧运行与活跃调用保留旧连接直至回收；同名显式资源准确调用；管理连接测试可诊断；市场安装审批后真正可调用、卸载进同一资源库。
- 完成证据：[专项报告](../../../../../.tinadec_dev/reports/2026-10-08-tools-settings.zh-CN.md)、Provider完整389、精确MCP定向及真实Desktop/市场Node stdio调用。未把本地真实进程夹具当作外部生产集成验收，既有TOOLS-MCP-101保持独立范围。

**验收条件**

- [x] 完成任务列出的实现范围及条件保存、冻结配置和资源隔离回归。
- [x] 取得真实 Windows Core/Desktop/Tools 证据；PostgreSQL 及 Linux/macOS 边界单独记录。
- [x] 同步模块说明、API/客户端及共享实施报告，不将专项完成扩大为整体模块验收。

**2026-11-11 补充**：`mcp_*` 已授予 `meeting`/`solo_master`，`npx`/`uvx` 安装入口与项目级 MCP 清单已接；运行上下文新增 `mcp_servers` 提示。HTTP/SSE 传输已实现（`202611110001_McpTransport` + `HttpClientTransport`），真实第三方 server 仍未验收；证据见[扩展层报告](../../../../../.tinadec_dev/reports/2026-11-11-extension-layer.zh-CN.md)。
