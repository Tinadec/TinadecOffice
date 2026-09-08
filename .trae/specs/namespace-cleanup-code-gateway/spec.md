> **⚠️ 已归档规格（未实现或已过时）**
> 本文描述的功能多数**在代码中不存在**（例如 .trae/specs 中标注"已完成 ✅"的能力无任何源码/测试对应）。仅作历史记录，请勿据此判断当前能力。

# 命名空间清理：Code 是 Code，Gateway 是 Gateway

## Why

当前 Gateway 包被命名为 `@tinadec/code`，导致 Code 层（编程领域工具）和 Gateway 层（BFF 代理）的边界模糊。根 `package.json` 的脚本也用 `dev:code` 来启动 Gateway，进一步加剧了命名混乱。需要一轮命名空间清理，让每一层归属明确。

## 当前命名问题清单

### 问题 1：Gateway 包名错误
- `apps/gateway/package.json` → `"name": "@tinadec/code"` ❌
- 应为 `"@tinadec/gateway"` — Gateway 是 BFF 代理层，不是 Code 层

### 问题 2：根 package.json 脚本命名混乱
- `"dev:code": "npm run dev -w @tinadec/code"` ❌
- 应为 `"dev:gateway": "npm run dev -w @tinadec/gateway"`
- `concurrently` 的 `-n core,code,desktop` 应改为 `-n core,gateway,desktop`

### 问题 3：Gateway 内 codeTools.ts 的定位
- `apps/gateway/src/codeTools.ts` — 文件名和导出函数名（`executeCodeTool`, `listCodeToolIds`）本身是正确的
- Gateway 作为 BFF，持有 Code 工具的路由和 native spawn 逻辑，这是合理的
- 但 stub 消息中的 `"Code-layer file search stub is wired"` 等措辞暗示这些是 Code 层的实现，而实际上 Gateway 只是代理层
- **不需要重命名文件**，但 stub 消息措辞应更精确

### 不需要改动的部分（已确认命名正确）
- `CodeToolClient.cs` / `ICodeToolClient` — Core 层调用 Code 工具的客户端，命名正确
- `CodeToolExecuteRequest` / `CodeToolExecuteResultDto` — DTO 描述 Code 工具协议，命名正确
- `ToolRegistryService.cs` 中 `Source = "code"` — 工具来源标记，正确
- `/api/v1/code/tools/:toolId/execute` — API 路由，正确
- `TINADEC_CODE_NATIVE_BIN` — 环境变量，正确
- `tinadec-code-native` — Rust binary 名称，正确
- 根 `"name": "tinadec-code"` — 项目整体名称，保持不变

## What Changes

- `apps/gateway/package.json` 的 `name` 从 `@tinadec/code` 改为 `@tinadec/gateway`
- 根 `package.json` 的 `dev:code` 脚本改为 `dev:gateway`，workspace 引用从 `@tinadec/code` 改为 `@tinadec/gateway`
- 根 `package.json` 的 `concurrently` 标签从 `core,code,desktop` 改为 `core,gateway,desktop`
- `apps/gateway/src/codeTools.ts` 中 stub 消息措辞更新，明确 Gateway 是代理而非实现

## Impact

- Affected specs: 包名、npm workspace 引用
- Affected code: `apps/gateway/package.json`、根 `package.json`、`apps/gateway/src/codeTools.ts`
- **BREAKING**: npm workspace 名称变更，所有 `npm run ... -w @tinadec/code` 引用需更新

## ADDED Requirements

### Requirement: Gateway 包名反映其角色

Gateway 的 npm 包名 SHALL 为 `@tinadec/gateway`，而非 `@tinadec/code`。

#### Scenario: 包名正确
- **WHEN** 查看 `apps/gateway/package.json` 的 `name` 字段
- **THEN** 值为 `"@tinadec/gateway"`

### Requirement: 根脚本命名与实际角色一致

根 `package.json` 的 dev 脚本 SHALL 使用 `dev:gateway` 而非 `dev:code`。

#### Scenario: 脚本命名
- **WHEN** 执行 `npm run dev:gateway`
- **THEN** 启动 Gateway BFF 服务

### Requirement: stub 消息明确 Gateway 代理角色

Gateway 的 Code 工具 stub 消息 SHALL 明确 Gateway 是代理层，而非 Code 层实现。

#### Scenario: stub 消息措辞
- **WHEN** native binary 不可用，Gateway 返回 stub 响应
- **THEN** summary 消息表述为 "Gateway proxy: ..." 而非 "Code-layer ..."
