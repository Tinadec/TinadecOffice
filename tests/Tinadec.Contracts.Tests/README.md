# 遗留需求证据目录（Tinadec.Contracts.Tests）

本目录是旧 Contracts 的**需求证据文件**，仅作为重建设计输入保留：

- 不在活动解决方案 `TinadecOffice.slnx` 内；
- 依赖已删除的旧 `src/` 实现，**不可构建**；
- **不得作为验证锚点**——任何构建、测试或就绪验证都不应指向本目录。

## 替代验证路线

请使用 `TinadecOffice.slnx` `/tests/` 文件夹中的活动测试项目：

- `TinadecCore/tests/TinadecCore.Architecture.Tests/`
- `TinadecCore/tests/TinadecCore.AgentFramework.Tests/`
- `TinadecCore/tests/TinadecCore.Api.Tests/`
- `tests/TinadecTools.Tests/`

定位依据见根 `AGENTS.md` 的 CURRENT REBUILD STATE 与 AI READING ORDER 章节。
