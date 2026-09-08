> **⚠️ 已归档规格（未实现或已过时）**
> 本文描述的功能多数**在代码中不存在**（例如 .trae/specs 中标注"已完成 ✅"的能力无任何源码/测试对应）。仅作历史记录，请勿据此判断当前能力。

# 修复 Electron 桌面应用启动 Spec

## 架构审查结论

经过对整个代码库的全面审查，**架构分层完全保留了设计理念**：

### Core 层（通用智能体运行时）✅ 完整保留
- `IToolRegistry` / `ICodeToolClient` / `IAgentWorkflowRuntime` / `IToolPermissionPolicy` 抽象接口齐全
- `AgentCatalog` 定义了双层智能体架构（planning 层 5 个 + execution 层 7 个），含进化候选
- `OrchestratorService` 正确编排 TaskGraph → Workflow → Tool Dispatch → StepResult → Event
- `ToolExecutionService` 实现审批门控：变更类工具必须走 approval 流
- `OpenAiCompatibleClient` 提供模型调用能力
- `EventHub` + `EventEnvelope` 事件体系完整
- `CoreStore` 是唯一状态源（SQLite），不依赖外部状态

### Code 层（编程领域层）✅ 正确分离
- Gateway (`apps/gateway`) 作为 Code 层 BFF，暴露 `/api/v1/code/tools/*/execute` 端点
- `codeTools.ts` 实现了 4 个编程工具 stub：search_files / sandbox_exec / apply_patch / review_format
- `tryExecuteNativeTool` 已预留 Codex Rust native binary 调用路径
- Code 不保存业务状态，不拥有任务图，不拥有 Agent 编排权

### 胶水层 ✅ 正确设计
- Core 通过 `CodeToolClient`（HttpClient）调用 Gateway 的 Code 工具端点
- Gateway 代理 Core 的所有 API，同时自己持有 Code 工具端点
- `native/` workspace 预留了 cdylib（Core 侧）和 napi-rs（Code 侧）胶水路径
- 架构边界测试 `ArchitectureBoundaryTests` 验证 Core 不依赖 Web/UI 框架

### 前端 ✅ 功能完整
- Vue 3 + Vue Router + vue-i18n + Tailwind CSS 架构合理
- AppHeader / AppSidebar / ChatPanel / WelcomeScreen / SettingsPage / MarketPage 等组件齐全
- AgentTopologyCanvas 实现了智能体拓扑可视化
- 双语支持（中/英）、暗色/亮色主题切换

## Why

当前 `npm run dev:desktop` 无法在 Windows 上启动 Electron 应用。原因是 `concurrently` 在 PowerShell 环境下无法正确 spawn `cmd.exe` 来执行子命令（`spawn cmd.exe ENOENT`），导致 Vite 和 Electron 都无法启动。用户要求以原生 Electron 桌面应用的形式运行，而不是在浏览器中看网页。

## What Changes

- 重写 `apps/desktop/package.json` 的 `dev` 脚本，使用 Node.js 脚本替代 `concurrently` + `cmd.exe` 的组合，确保跨平台兼容
- 创建 `apps/desktop/scripts/dev.mjs` 开发启动脚本，用 Node.js 原生 `child_process` 管理 Vite 和 Electron 进程
- 清理之前创建的临时批处理文件（`start-vite.bat`、`start-electron.bat`、`dev.bat`）
- 确保 Electron 主进程正确加载 Vite dev server URL 或构建产物

## Impact

- Affected specs: Electron 桌面应用启动流程
- Affected code: `apps/desktop/package.json`、新增 `apps/desktop/scripts/dev.mjs`、删除临时 `.bat` 文件

## ADDED Requirements

### Requirement: Electron 桌面应用开发模式启动

系统 SHALL 提供一个 `npm run dev:desktop` 命令，在 Windows 上正确启动 Vite 开发服务器和 Electron 进程。

#### Scenario: 开发模式启动成功
- **WHEN** 用户运行 `npm run dev:desktop`
- **THEN** Vite 开发服务器在 `http://127.0.0.1:5173` 启动
- **AND** 等待 Vite 就绪后，Electron 进程启动并加载 Vite dev server URL
- **AND** Electron 窗口以 1440x920 尺寸打开，标题为 "TinadecCode"

#### Scenario: Vite 就绪检测
- **WHEN** 开发脚本启动
- **THEN** 脚本 SHALL 通过 HTTP 请求检测 Vite 服务器是否就绪
- **AND** 最多等待 30 秒
- **AND** 如果超时则报错退出

#### Scenario: 进程生命周期管理
- **WHEN** Electron 进程退出
- **THEN** Vite 进程 SHALL 同时被终止
- **WHEN** 用户按 Ctrl+C
- **THEN** 所有子进程 SHALL 被清理

### Requirement: 清理临时文件

系统 SHALL 删除之前为解决启动问题而创建的临时批处理文件。

#### Scenario: 临时文件清理
- **WHEN** spec 实施完成
- **THEN** `start-vite.bat`、`start-electron.bat`、`dev.bat` 文件 SHALL 不存在
