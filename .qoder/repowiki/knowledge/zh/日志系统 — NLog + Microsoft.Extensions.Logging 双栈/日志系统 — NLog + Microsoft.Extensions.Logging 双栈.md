---
kind: logging_system
name: 日志系统 — NLog + Microsoft.Extensions.Logging 双栈
category: logging_system
scope:
    - '**'
source_files:
    - TinadecTools/Nlog.config
    - TinadecTools/Program.cs
    - TinadecCore/Persistence/DatabaseReadiness.cs
    - TinadecCore/Api/Program.cs
    - TinadecGateway/src/index.ts
---

## 1. 使用的框架与工具
- **TinadecTools（工具运行时）**：使用 **NLog** 作为日志框架，通过 `Nlog.config` 配置控制台与文件输出。
- **TinadecCore（核心框架 API）**：使用 **.NET 内置的 `Microsoft.Extensions.Logging`**（ILogger），由 ASP.NET Core 默认提供，未引入第三方日志库。
- **TinadecGateway（Bun/Elysia 网关）**：仅使用 Node.js 原生 `console.log` 进行启动信息输出，无结构化日志框架。

## 2. 关键文件与位置
- `TinadecTools/Nlog.config` — NLog 配置文件，定义 Console 与 File 两个目标及规则。
- `TinadecTools/Program.cs` — 工具运行时入口，通过 `LogManager.GetCurrentClassLogger()` 获取 NLog Logger。
- `TinadecCore/Persistence/DatabaseReadiness.cs` — 使用 `ILogger<DatabaseReadiness>` 记录数据库就绪探测警告。
- `TinadecCore/Api/Program.cs` — ASP.NET Core 应用入口，依赖 DI 注入 ILogger，未自定义日志配置。
- `TinadecGateway/src/index.ts` — 仅用 `console.log` 打印监听端口等启动信息。

## 3. 架构与约定
- **双栈并存**：工具层（TinadecTools）采用 NLog，核心服务（TinadecCore）采用 Microsoft.Extensions.Logging，两者互不共享，各自独立配置。
- **NLog 输出格式**：统一采用 `${longdate}|${level:uppercase=true}|${logger}|${message} ${exception:format=tostring}` 布局，包含时间、级别、日志源、消息与异常堆栈。
- **分级输出策略**：
  - 控制台：Debug 及以上级别写入 stderr。
  - 文件：Info 及以上级别写入 `logs/{shortdate}.log`，超过 10MB 自动归档到 `logs/archive/`，最多保留 30 个历史文件。
  - Microsoft.* 命名空间日志在控制台仅输出 Warn 及以上，且为 final 规则（不再继续传播）。
- **结构化字段**：NLog 调用中通过占位符 `{id}`、`{type}`、`{path}` 等传递上下文参数；ILogger 调用中使用 C# 插值字符串 `{Provider}` 传递参数。
- **异常处理模式**：错误路径统一通过 `logger.Warn(ex, ...)` 或 `_logger.LogWarning(ex, ...)` 记录异常对象，确保堆栈信息完整。

## 4. 约定与约束
- **工具层必须使用 NLog**：所有工具实现类（如 FileReader、Writer、McpInvokeTool 等）均通过 `LogManager.GetCurrentClassLogger()` 获取静态 Logger 实例。
- **核心层必须使用 ILogger<T>**：通过构造函数注入 `ILogger<T>`，遵循 .NET 标准依赖注入模式。
- **禁止直接使用 console.log（除 Gateway 启动信息外）**：后端代码不使用 `Console.WriteLine` 作为日志手段，统一走日志框架。
- **日志级别约定**：调试信息使用 Debug，业务警告使用 Warn/Error，正常流程使用 Info；Microsoft 内部库日志被降级过滤。
- **文件日志轮转策略固定**：单文件最大 10MB，归档文件按日期命名，最多保留 30 份，避免磁盘占用无限增长。
- **Gateway 层例外**：TypeScript 网关仅使用 `console.log` 输出启动信息，无结构化日志能力，属于轻量级 BFF 的定位使然。