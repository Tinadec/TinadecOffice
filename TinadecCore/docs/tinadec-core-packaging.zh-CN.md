# TinadecCore 打包与独立部署

本文记录当前工作树的交付边界，不代表已经发布到 NuGet 源或容器仓库。

## NuGet 包

稳定公共包的打包入口是：

- `TinadecCore.Contracts`：HTTP DTO、事件 envelope 和 provider-neutral 数据类型；不引用 MAF、ASP.NET 或 F#。
- `TinadecCore.Abstractions`：Core 端口和模块注册抽象；不暴露 MAF 类型。
- `TinadecCore.Runtime`：完整 Core 组合入口，MAF 1.18 仅通过 DmaEA 适配层接入。

三个项目显式启用 `IsPackable=true`，是唯一面向宿主承诺的包面。`TinadecCore` 全部模块（含 Contracts/Abstractions/Runtime 及内部实现包）均以 `MIT` 开源（`TinadecCore/LICENSE`，`Copyright (c) 2026 Lincube`，`PackageLicenseExpression=MIT` 集中于 `TinadecCore/Directory.Build.props`）。Runtime 依赖的 Core 内部模块也启用实现包，以便 NuGet 正确还原；这些包不属于稳定业务 API，应与 Runtime 一起从同一 feed 发布。测试项目和 API 项目继续不可打包。包版本沿用 `TinadecCore/Directory.Build.props` 的 `PackageVersion`，发布前应由 CI 注入正式 SemVer。

```powershell
dotnet pack TinadecCore/Contracts/TinadecCore.Contracts.csproj -c Release --no-restore
dotnet pack TinadecCore/Abstractions/TinadecCore.Abstractions.csproj -c Release --no-restore
dotnet pack TinadecCore/Runtime/TinadecCore.Runtime.csproj -c Release --no-restore
```

发布 Runtime 前需要先将 `TinadecCore.slnx` 中的实现包一起推送到同一内部源；只推送三个公开包会留下不可还原的内部 ProjectReference。CI 应使用统一版本执行整套解决方案的 `dotnet pack`，再选择性推送公开包和实现包。

`TinadecCore.Runtime` 的依赖包由 NuGet 根据项目引用生成；MAF 包只能作为运行适配依赖，不能进入 Contracts、事件或稳定 HTTP DTO。

## API 独立服务

API 是可执行交付物，不是稳定 Core 契约包：

```powershell
dotnet publish TinadecCore/Api/TinadecCore.Api.csproj -c Release --no-restore -o artifacts/tinadec-core-api
dotnet artifacts/tinadec-core-api/TinadecCore.Api.dll --urls http://127.0.0.1:48731
```

SQLite 适合本地部署；自托管云部署通过配置 PostgreSQL、ContentStore 和外部身份适配器完成。当前仓库尚未承诺容器镜像、NuGet feed、OIDC 适配器或生成的 TypeScript/.NET Client SDK，这些仍属于 Phase 1 后续交付。

## 边界约束

- `TinadecTools.Generators` 继续是 TinadecTool 的 Analyzer/Source Generator，不参与 Core 打包，也不成为运行时依赖。
- Gateway 只代理 Core API；它不读取 Core 数据库，也不负责 Core 包或工具授权。
- `OfficeAgentPack` 是 TinadecOffice/TinadecApp 的构建产物，不进入 Core NuGet 或 `TinadecCore.Api` publish 目录。Core 包只携带通用 Agent Pack 契约、安装服务、持久化迁移和无 App 专业知识的 TOML fallback。
- Desktop 与 Web 从同一 renderer 静态导入 `apps/desktop/src/agentPacks/OfficeAgentPack/manifest.json`，生产构建必须包含 Pack ID 和固定 digest；不得在运行时依赖机器路径读取 manifest。
- App 连接后把 bundled manifest 经公开 `/api/v1/agent-packs/*` 契约提交到当前工作区；不得通过复制文件到 Core 安装目录或直接写 Core 数据库完成“安装”。SQLite/PostgreSQL 的 Pack 表迁移属于 Core Runtime/API 交付物。
- MAF `1.18.0` 的版本兼容性由 `DmaEA/Maf18RuntimeAdapter.cs` 和兼容测试门禁；升级不得改变 Contracts 包、事件或配置契约。
