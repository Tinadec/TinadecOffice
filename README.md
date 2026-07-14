# TinadecOffice

TinadecOffice 是一个 Windows 优先的智能体桌面工作台，基于通用智能体 harness 构建。它为 AI 辅助软件开发提供完整环境，支持审批门控的工具执行、双层智能体编排以及可分离面板 UI。

## 架构

TinadecOffice 采用三层架构，各层职责边界清晰：

| 层 | 技术栈 | 端口 | 职责 |
|---|--------|------|------|
| **Desktop** | Electron + Vue 3 + Vite + Tailwind CSS | 5173 (开发) | UI 呈现：聊天、任务图、执行分派、审批、Debug Studio |
| **Gateway** | Elysia TypeScript + Node.js | 48730 | 薄 BFF/代理层，位于 Desktop 与 Core 之间；Swagger 文档位于 `/docs` |
| **Core** | .NET 10 C# + ASP.NET Core | 48731 | 唯一状态权威：编排、工具注册、模型路由、SQLite 持久化、OpenTelemetry 追踪 |

**设计原则：**
- Core 是唯一状态权威 — Gateway 和 Desktop 不存储任何状态
- Desktop 只调用 Gateway，不直接调用 Core
- 所有写操作都必须经过审批门
- API 契约统一使用 `snake_case` 命名

## 快速开始

```powershell
npm install
npm run restore:dotnet
npm run dev
```

OpenAPI 文档：`http://127.0.0.1:48730/docs`

详细的启动流程和故障排查，请参阅 [docs/startup.md](docs/startup.md)。

## 项目结构

```
TinadecOffice/
├── apps/desktop/            # Electron + Vue 渲染器、Debug Studio、可分离面板
├── gateway/                 # Elysia BFF/代理
├── src/TinadecCore/         # .NET Core 运行时、状态权威、契约、追踪
├── src/TinadecTools/        # 原型工具宿主，含 AOT-safe JSON 和 MCP 透传
├── TinadecTools.Generators/ # [ToolFunction] 注册的源代码生成器
├── tests/                   # Core、契约和 TinadecTools 的 xUnit 测试
├── docs/                    # 产品模型、架构、安全、启动手册
└── TinadecOffice.slnx       # .NET 解决方案文件
```

## 核心特性

- **双层智能体编排** — 规划层（主动监督）和执行层（任务执行）协同工作
- **审批门控工具执行** — 所有写操作都需要用户明确批准
- **基于 Provider 的模型中心** — 支持 API 密钥、本地服务器和 CLI 模型访问，含健康监控
- **可分离面板窗口** — 将侧边栏面板拖出为独立的 Electron 窗口
- **Agent Debug Studio** — 内置追踪可视化和调试工具
- **MCP 协议支持** — Model Context Protocol 透传，可接入外部工具服务器
- **SQLite 持久化** — 项目、会话、消息、事件和审批全部本地持久化存储

## 常用命令

```bash
npm run dev                          # 同时启动 Core + Gateway + Desktop
npm run build                        # 构建所有工作区和 .NET 解决方案
npm test                             # 运行所有测试（工作区 + .NET）
npm run test -w @tinadec/gateway     # 仅 Gateway 测试
npm run test -w @tinadec/desktop     # 仅 Desktop 测试
```

直接运行 Core 测试：

```powershell
Remove-Item Env:Version -ErrorAction SilentlyContinue
Remove-Item Env:Ice-Version -ErrorAction SilentlyContinue
dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal
```

## 文档

| 文档 | 用途 |
|------|------|
| [产品模型](docs/agent-harness-product-model.zh-CN.md) | 层次边界和职责划分 |
| [架构](docs/architecture.md) | 技术架构、端口、事件形态 |
| [参考项目映射](docs/reference-project-map.md) | 同类项目参考与设计决策 |
| [启动手册](docs/startup.md) | 标准化启动流程和故障排查 |

## 许可证

GPL-3.0-or-later