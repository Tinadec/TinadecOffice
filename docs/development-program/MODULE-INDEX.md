# 模块总索引

由模块STATUS/TODO生成；修改这些源文件后执行 `node docs/development-program/scripts/reindex.mjs` 刷新。模块目录是长期入口，完成情况只按证据记录，当前不计算完成百分比。

## App / 用户工作台

| 模块ID | 模块档案 | 初始/当前审计阶段 | 功能条目 | 已验收 | TODO |
| --- | --- | --- | --- | --- | --- |
| APP-DESKTOP | [Desktop / Electron 原生壳](02-modules/app/desktop-shell/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [2项](02-modules/app/desktop-shell/TODO.md) |
| APP-WEB | [Web / 浏览器平台适配](02-modules/app/web/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [2项](02-modules/app/web/TODO.md) |
| APP-RENDERER | [共享渲染层 / 路由与 API](02-modules/app/shared-renderer/README.md) | 初始源码清点；逐功能审计未完成 | 6 | 4 | [8项](02-modules/app/shared-renderer/TODO.md) |
| APP-HOME | [Home / 会话、对话与投递](02-modules/app/home/README.md) | 初始源码清点；逐功能审计未完成 | 8 | 3 | [9项](02-modules/app/home/TODO.md) |
| APP-CODE | [Code / 编程工作台](02-modules/app/code/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [2项](02-modules/app/code/TODO.md) |
| APP-DATA | [Workbench / 治理与数据页面](02-modules/app/data-pages/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [2项](02-modules/app/data-pages/TODO.md) |
| APP-SETTINGS | [Settings / 配置中心](02-modules/app/settings/README.md) | 初始源码清点；逐功能审计未完成 | 4 | 3 | [6项](02-modules/app/settings/TODO.md) |
| APP-MARKET | [Market / 市场](02-modules/app/market/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [2项](02-modules/app/market/TODO.md) |
| APP-DEBUG | [Debug Studio / 调试界面](02-modules/app/debug-studio/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 1 | [3项](02-modules/app/debug-studio/TODO.md) |
| APP-UIE-ENGINE | [TinadecUI / UIE Engine](02-modules/app/uie-engine/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 1 | [1项](02-modules/app/uie-engine/TODO.md) |
| APP-UIE-COMPONENTS | [TinadecUI / UIE Components](02-modules/app/uie-components/README.md) | 初始源码清点；逐功能审计未完成 | 4 | 2 | [3项](02-modules/app/uie-components/TODO.md) |
| APP-LOCAL-STATE | [Desktop / 偏好与布局持久化](02-modules/app/local-state/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [2项](02-modules/app/local-state/TODO.md) |
| APP-SERVICES | [Desktop / 本地服务管理](02-modules/app/local-services/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [3项](02-modules/app/local-services/TODO.md) |
| APP-PACKS | [App / AgentPack 内容与安装体验](02-modules/app/agent-packs/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 1 | [3项](02-modules/app/agent-packs/TODO.md) |

## Core / 24 个产品工程

| 模块ID | 模块档案 | 初始/当前审计阶段 | 功能条目 | 已验收 | TODO |
| --- | --- | --- | --- | --- | --- |
| CORE-API | [Api · 可执行宿主](02-modules/core/Api/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [1项](02-modules/core/Api/TODO.md) |
| CORE-HTTP | [AspNetCore · 可嵌入 HTTP 层](02-modules/core/AspNetCore/README.md) | 初始源码清点；逐功能审计未完成 | 4 | 0 | [2项](02-modules/core/AspNetCore/TODO.md) |
| CORE-RUNTIME | [Runtime · 唯一组合根](02-modules/core/Runtime/README.md) | 初始源码清点；逐功能审计未完成 | 5 | 0 | [2项](02-modules/core/Runtime/TODO.md) |
| CORE-DMAEA | [DmaEA / 双层调用与持久运行引擎](02-modules/core/DmaEA/README.md) | 初始源码清点；逐功能审计未完成 | 4 | 0 | [2项](02-modules/core/DmaEA/TODO.md) |
| CORE-AGENT-CONFIG | [AgentConfiguration](02-modules/core/AgentConfiguration/README.md) | 配置存储链路已拆分并完成 Windows/SQLite、Linux/真实 PostgreSQL 定向验证；包服务全量审计、macOS 和完整发布产物验收仍未完成 | 5 | 4 | [3项](02-modules/core/AgentConfiguration/TODO.md) |
| CORE-AGENT-GRAPH | [AgentGraph · 图与资源](02-modules/core/AgentGraph/README.md) | 初始源码清点；逐功能审计未完成 | 3 | 0 | [3项](02-modules/core/AgentGraph/TODO.md) |
| CORE-GOVERNANCE | [Governance · 授权与审批](02-modules/core/Governance/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [1项](02-modules/core/Governance/TODO.md) |
| CORE-TINACHAT | [TinaChat · 会话组织通信](02-modules/core/TinaChat/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [2项](02-modules/core/TinaChat/TODO.md) |
| CORE-MODELS | [Models · 模型与 Harness](02-modules/core/Models/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [2项](02-modules/core/Models/TODO.md) |
| CORE-CONTEXT | [Context · 本轮输入与补丁](02-modules/core/Context/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [2项](02-modules/core/Context/TODO.md) |
| CORE-PROMPTS | [Prompts · 提示词组装与版本](02-modules/core/Prompts/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [2项](02-modules/core/Prompts/TODO.md) |
| CORE-MEMORY | [Memory · 会话与长期记忆](02-modules/core/Memory/README.md) | 初始源码清点；逐功能审计未完成 | 4 | 1 | [1项](02-modules/core/Memory/TODO.md) |
| CORE-SKILLS | [Skills · 市场与集成配置](02-modules/core/Skills/README.md) | 初始源码清点；逐功能审计未完成 | 5 | 4 | [4项](02-modules/core/Skills/TODO.md) |
| CORE-TOOLS | [Tools · 工具治理与适配](02-modules/core/Tools/README.md) | 初始源码清点；逐功能审计未完成 | 4 | 1 | [3项](02-modules/core/Tools/TODO.md) |
| CORE-LIFECYCLE | [Lifecycle · 运行事实与恢复](02-modules/core/Lifecycle/README.md) | 初始源码清点；逐功能审计未完成 | 3 | 0 | [1项](02-modules/core/Lifecycle/TODO.md) |
| CORE-LOOP-GUARD | [LoopGuard · 防空转与预算](02-modules/core/LoopGuard/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [1项](02-modules/core/LoopGuard/TODO.md) |
| CORE-TENANCY | [Tenancy · 身份与隔离](02-modules/core/Tenancy/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [1项](02-modules/core/Tenancy/TODO.md) |
| CORE-VECTOR | [VectorStore · 检索底座](02-modules/core/VectorStore/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [1项](02-modules/core/VectorStore/TODO.md) |
| CORE-PERSISTENCE | [Persistence · 公共存储适配](02-modules/core/Persistence/README.md) | 初始源码清点；逐功能审计未完成 | 3 | 0 | [2项](02-modules/core/Persistence/TODO.md) |
| CORE-SQLITE | [Storage.Migrations.Sqlite](02-modules/core/Storage.Migrations.Sqlite/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [1项](02-modules/core/Storage.Migrations.Sqlite/TODO.md) |
| CORE-POSTGRES | [Storage.Migrations.PostgreSql](02-modules/core/Storage.Migrations.PostgreSql/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [1项](02-modules/core/Storage.Migrations.PostgreSql/TODO.md) |
| CORE-STRATEGIES | [Strategies · F# 纯策略](02-modules/core/Strategies/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [1项](02-modules/core/Strategies/TODO.md) |
| CORE-ABSTRACTIONS | [Abstractions · 跨模块端口](02-modules/core/Abstractions/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [1项](02-modules/core/Abstractions/TODO.md) |
| CORE-CONTRACTS | [Contracts · 对外契约类型](02-modules/core/Contracts/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [1项](02-modules/core/Contracts/TODO.md) |

## Gateway / 无状态传输门面

| 模块ID | 模块档案 | 初始/当前审计阶段 | 功能条目 | 已验收 | TODO |
| --- | --- | --- | --- | --- | --- |
| GW-HTTP | [Gateway / HTTP、认证与上下文](02-modules/gateway/http-auth/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 1 | [2项](02-modules/gateway/http-auth/TODO.md) |
| GW-SESSIONS | [Gateway / 项目、会话与运行控制](02-modules/gateway/session-control/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 1 | [2项](02-modules/gateway/session-control/TODO.md) |
| GW-CONFIG | [Gateway / 配置、市场、治理与组织代理](02-modules/gateway/configuration-organization/README.md) | 初始源码清点；逐功能审计未完成 | 3 | 1 | [3项](02-modules/gateway/configuration-organization/TODO.md) |
| GW-TOOLS | [Gateway / 用户工具传输与可选读面](02-modules/gateway/user-tool-transport/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [1项](02-modules/gateway/user-tool-transport/TODO.md) |
| GW-STREAMING | [Gateway / SSE、附件、日志与取消](02-modules/gateway/streaming/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [2项](02-modules/gateway/streaming/TODO.md) |
| GW-WS | [Gateway / WebSocket 范围与实现](02-modules/gateway/websocket/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [2项](02-modules/gateway/websocket/TODO.md) |

## Tools / 执行产品

| 模块ID | 模块档案 | 初始/当前审计阶段 | 功能条目 | 已验收 | TODO |
| --- | --- | --- | --- | --- | --- |
| TOOLS-PROTOCOL | [TinadecTools / 协议、manifest 与执行宿主](02-modules/tools/protocol-host/README.md) | 初始源码清点；逐功能审计未完成 | 4 | 1 | [3项](02-modules/tools/protocol-host/TODO.md) |
| TOOLS-FILES | [TinadecTools / 文件与搜索](02-modules/tools/files-search/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [3项](02-modules/tools/files-search/TODO.md) |
| TOOLS-GIT | [TinadecTools / Git](02-modules/tools/git/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [2项](02-modules/tools/git/TODO.md) |
| TOOLS-COMMAND | [TinadecTools / 命令、进程与终端](02-modules/tools/commands-terminals/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [2项](02-modules/tools/commands-terminals/TODO.md) |
| TOOLS-SANDBOX | [TinadecTools / 平台沙箱](02-modules/tools/sandbox/README.md) | 初始源码清点；逐功能审计未完成 | 3 | 0 | [3项](02-modules/tools/sandbox/TODO.md) |
| TOOLS-MCP | [TinadecTools / MCP 扩展](02-modules/tools/mcp/README.md) | 初始源码清点；逐功能审计未完成 | 3 | 1 | [3项](02-modules/tools/mcp/TODO.md) |
| TOOLS-WEB | [TinadecTools / 网络抓取](02-modules/tools/web-fetch/README.md) | 初始源码清点；逐功能审计未完成 | 2 | 0 | [2项](02-modules/tools/web-fetch/TODO.md) |
| TOOLS-GENERATOR | [TinadecTools.Generators / 构建期生成器](02-modules/tools/source-generator/README.md) | 初始源码清点；逐功能审计未完成 | 1 | 0 | [2项](02-modules/tools/source-generator/TODO.md) |

## 横切工程 / 数据、质量、交付

| 模块ID | 模块档案 | 初始/当前审计阶段 | 功能条目 | 已验收 | TODO |
| --- | --- | --- | --- | --- | --- |
| X-DATA | [数据、安全与持久化验收](02-modules/cross-cutting/data-security/README.md) | 初始源码清点；逐功能审计未完成 | 6 | 0 | [5项](02-modules/cross-cutting/data-security/TODO.md) |
| X-QUALITY | [质量、契约与验收门禁](02-modules/cross-cutting/quality-contracts/README.md) | 初始源码清点；逐功能审计未完成 | 3 | 0 | [2项](02-modules/cross-cutting/quality-contracts/TODO.md) |
| X-DELIVERY | [三平台交付、Manager 与更新](02-modules/cross-cutting/delivery-manager/README.md) | 初始源码清点；逐功能审计未完成 | 6 | 0 | [4项](02-modules/cross-cutting/delivery-manager/TODO.md) |

