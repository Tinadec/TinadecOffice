---
kind: dependency_management
name: 多语言依赖管理：npm workspaces + .NET Central Package Management + Bun Lockfile
category: dependency_management
scope:
    - '**'
source_files:
    - package.json
    - .npmrc
    - TinadecCore/Directory.Packages.props
    - TinadecGateway/package.json
    - apps/desktop/package.json
    - apps/web/package.json
    - TinadecGateway/bun.lock
    - scripts/setup-dotnet-env.ps1
    - scripts/setup-shadcn-ai.mjs
---

TinadecOffice 是一个跨语言的 Agent 协作工作空间，涉及 .NET（C#/F#）、TypeScript/Node.js、Bun 等多个技术栈。其依赖管理采用分层策略，针对不同语言生态使用各自的最佳实践。

## 1. Node.js 生态：npm workspaces + lockfile

- **根级 package.json** 定义 npm workspaces，将 `apps/*` 下的子应用纳入统一编排，包含 desktop（Electron+Vue）和 web（纯 Vue）两个前端包。
- **各子模块独立 package.json**：`apps/desktop/package.json`、`apps/web/package.json`、`TinadecGateway/package.json` 分别声明自身依赖，通过 workspace 引用共享依赖。
- **锁文件策略**：
  - Gateway 使用 Bun，生成 `bun.lock` 锁定依赖版本。
  - 根目录存在 `package-lock.json`，由 npm 生成用于锁定 workspace 依赖。
  - 代码中显式识别多种 lockfile 格式（`package-lock.json`、`yarn.lock`、`pnpm-lock.yaml`、`bun.lockb`、`cargo.lock`、`poetry.lock`），表明项目对多包管理器兼容性的考量。
- **peerDependencies 处理**：`.npmrc` 启用 `legacy-peer-deps=true`，以绕过 `vite-plugin-vue-mcp@0.3.2` 与 Vite 7 的 peer 依赖冲突，并手动在 `apps/desktop/package.json` 中显式声明 React 相关依赖以确保运行时可用。

## 2. .NET 生态：Central Package Management (CPM)

- **集中版本管理**：`TinadecCore/Directory.Packages.props` 通过 `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>` 启用 CPM，所有 NuGet 包版本在此统一声明，避免各 csproj 重复声明版本。
- **传递依赖固定**：`<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>` 确保传递依赖也被锁定，提升构建可重现性。
- **关键依赖分组**：
  - Microsoft Agent Framework (MAF) 锁定到 1.13.0 稳定版
  - Microsoft.Extensions.AI 系列使用 10.8.0
  - EF Core 及其 SQLite/PostgreSQL 提供程序使用 10.0.x 系列
  - F# 核心库使用 9.0.303
- **解决方案编排**：通过 `.slnx` 文件（`TinadecOffice.slnx`、`TinadecCore/TinadecCore.slnx`）组织多个 .NET 项目，支持增量构建和测试。

## 3. 脚本化依赖安装与初始化

- **根级 scripts**：`scripts/setup-dotnet-env.ps1` 封装 .NET 环境设置，`scripts/setup-shadcn-ai.mjs` 处理 shadcn AI 工具初始化。
- **AI 工具链**：通过 `package.json` 中的 `ai:tools:*` 脚本集成 CodeGraph、Ponytail、shadcn-vue skill 等 AI 辅助工具，支持一键安装、验证和状态检查。
- **postinstall 钩子**：根 `package.json` 的 `postinstall` 自动执行 shadcn AI 初始化，确保开发环境一致性。

## 4. 私有仓库与镜像配置

- **.npmrc** 仅配置 `legacy-peer-deps=true`，未声明私有 registry，默认使用 npm 官方源。
- 未发现 NuGet.config 或私有 NuGet 源配置，推测依赖均从公共源获取。

## 5. 架构约定与约束

- **依赖隔离**：每个子模块（desktop、web、gateway、core）保持独立的依赖声明，通过 workspace 机制共享，避免全局污染。
- **版本锁定严格性**：.NET 侧通过 CPM + 传递依赖固定实现强锁定；Node.js 侧依赖 lockfile 保证可重现构建。
- **安全考虑**：SQLitePCLRaw.bundle_e_sqlite3 明确注释 pinned 到 3.0.3 以规避 CVE-2025-6965，体现对安全漏洞的主动防护。
- **多包管理器兼容**：代码中对多种 lockfile 格式的识别表明项目设计时考虑了不同开发者可能使用的包管理器偏好。