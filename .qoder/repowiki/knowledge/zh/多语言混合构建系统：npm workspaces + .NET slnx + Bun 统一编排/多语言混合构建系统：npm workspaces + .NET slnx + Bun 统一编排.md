---
kind: build_system
name: 多语言混合构建系统：npm workspaces + .NET slnx + Bun 统一编排
category: build_system
scope:
    - '**'
source_files:
    - package.json
    - TinadecOffice.slnx
    - TinadecCore/Directory.Build.props
    - TinadecCore/Directory.Packages.props
    - scripts/setup-dotnet-env.ps1
    - .github/workflows/core-storage-postgres.yml
    - TinadecGateway/package.json
    - apps/desktop/package.json
    - apps/web/package.json
---

## 1. 使用的系统与工具

本项目采用**多语言混合构建**，通过 npm workspaces 与 .NET Solution Extensions (.slnx) 统一编排以下子系统：
- **.NET (C# + F#)**：基于 .NET 10.0（net10.0），使用 Microsoft Agent Framework (MAF, 1.13.0)、EF Core 10、ASP.NET Core 10、xUnit 测试框架。
- **Bun/Elysia Gateway**：TinadecGateway 使用 Bun 作为运行时与包管理器，Elysia 作为 Web 框架，TypeScript 编写。
- **Electron Desktop + Vite/Vue 3**：apps/desktop 为 Electron 桌面应用，Vite 构建 Vue 3 前端；apps/web 为纯 Web 版本。
- **CI/CD**：GitHub Actions 仅包含 PostgreSQL 存储集成测试工作流。

## 2. 关键文件与位置

- **根级编排**：`package.json`（npm workspaces 定义、脚本入口）、`TinadecOffice.slnx`（.NET 项目集合）
- **.NET 全局属性**：`TinadecCore/Directory.Build.props`（TargetFramework、Version、Nullable 等）、`TinadecCore/Directory.Packages.props`（集中式 NuGet 包版本管理）
- **环境准备脚本**：`scripts/setup-dotnet-env.ps1`（清理冲突环境变量、验证 dotnet SDK 可用性）
- **子模块配置**：`TinadecGateway/package.json`、`apps/desktop/package.json`、`apps/web/package.json`
- **CI 流水线**：`.github/workflows/core-storage-postgres.yml`

## 3. 架构与约定

### 3.1 分层解决方案结构（.slnx）
`slnx` 将 .NET 项目按职责分组：
- `/tools/`：代码生成器与工具运行时
- `/TinadecCore/foundation/`：契约、抽象、持久化、向量存储、F# 策略内核
- `/TinadecCore/modules/`：业务模块（DmaEA、Models、Context、Prompts、Memory、Skills、LoopGuard、Lifecycle）
- `/TinadecCore/host/`：运行时宿主与 API 端点
- `/tests/`：架构测试、Agent Framework 测试、API 测试、Tools 测试

### 3.2 npm workspaces 编排
根 `package.json` 通过 `workspaces: ["apps/*"]` 聚合桌面与 Web 前端，并通过 `concurrently` 并行启动 Core API、Gateway 和 Desktop 三个服务进行本地开发。

### 3.3 构建流程
- **开发模式**：`npm run dev` 并行启动 Core（dotnet run）、Gateway（bun dev）、Desktop（vite + electron）
- **构建模式**：`npm run build` 依次执行：workspace 构建 → Gateway bun 构建 → .NET slnx 构建（跳过 restore）
- **测试模式**：`npm run test` 依次执行：workspace 测试 → Gateway bun 测试 → .NET slnx 测试（跳过 build）

### 3.4 依赖版本管理
- **.NET 集中式包管理**：通过 `Directory.Packages.props` 统一管理所有 NuGet 包版本，启用 `CentralPackageTransitivePinningEnabled` 锁定传递依赖。
- **Node.js 依赖**：各子模块独立维护 `package.json`，根目录仅协调 workspace 脚本。

### 3.5 版本策略
- 所有 .NET 项目默认 `Version=0.1.0`、`IsPackable=false`（不打包为 NuGet 包）
- Node.js 子模块各自维护版本号（gateway 0.2.0，desktop/web 0.1.0）
- 目标框架统一为 `net10.0`，LangVersion 为 latest

## 4. 约定与约束

### 已观察到的约定
- **.NET 项目必须位于 slnx 中声明的路径下**，否则不会被识别
- **所有 .NET 项目继承 Directory.Build.props**，无需重复声明 TargetFramework、Nullable 等属性
- **NuGet 包版本必须在 Directory.Packages.props 中声明**，项目文件中只能引用包名不能指定版本
- **PowerShell 脚本通过 setup-dotnet-env.ps1 调用 dotnet**，自动清理 Version/Ice-Version 环境变量以避免进程冲突
- **GitHub Actions 仅运行 PostgreSQL 集成测试**，通过 TINADEC_TEST_POSTGRES 环境变量控制测试开关

### 约束与限制
- **.NET SDK 必须为 net10.0**（setup-dotnet-env.ps1 会检查 PATH 中是否可用）
- **Windows 平台需要 PowerShell 执行策略允许脚本运行**（脚本使用 -ExecutionPolicy Bypass）
- **遗留测试项目（TinadecCore.Tests、Tinadec.Contracts.Tests）不可构建**，属于历史证据目录
- **CI 流水线未覆盖前端构建与测试**，仅验证 Core 的 PostgreSQL 存储层
- **无 Dockerfile / Makefile / 跨编译配置**，构建完全依赖本地环境与 npm/dotnet CLI

### 缺失的构建能力
- 无容器化构建（Dockerfile）
- 无跨平台构建脚本（Linux/macOS 上 PowerShell 脚本不可用）
- 无统一的发布流水线（release flow）
- 无代码质量门禁（lint、security scan 等）
