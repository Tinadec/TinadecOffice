# TinadecOffice Claude Guidance

This file is the Claude-specific entry point for AI agents. It intentionally points back to the shared project memory so Claude, Codex, and other agents follow the same architecture rules.

## Project Context

TinadecOffice is a Windows-first intelligent agent desktop workbench built around a universal agent harness. It implements a three-layer architecture:

1. **Desktop Layer** (UI Presentation): Electron + Vue 3 + Vite, port 5173
2. **Gateway Layer** (BFF/API): Elysia TypeScript, port 48730  
3. **Core Layer** (Agent Orchestration): .NET 10 C#, port 48731

The project follows a clear separation of concerns where Core is the only state authority, Gateway is a thin proxy, and Desktop is UI-only.

## Development Environment Configuration

### System Requirements
- Windows 10/11 (23H2 or later)
- Node.js 18+ and npm
- .NET 10 SDK
- Git

### Dependency Installation
```powershell
# Install npm dependencies
npm install

# Restore .NET dependencies
npm run restore:dotnet
```

### Environment Variables
The local environment contains `Version=V7.24.42SP3` which breaks MSBuild version parsing. Root npm scripts automatically clear this variable for .NET processes. For direct PowerShell dotnet commands, clear it manually:
```powershell
Remove-Item Env:Version -ErrorAction SilentlyContinue
Remove-Item Env:Ice-Version -ErrorAction SilentlyContinue
```

### Port Configuration
- **Desktop (Vite)**: `http://127.0.0.1:5173`
- **Gateway (Elysia)**: `http://127.0.0.1:48730`
- **Core (.NET)**: `http://127.0.0.1:48731`

### Key Configuration Files
- `package.json`: npm workspace configuration
- `TinadecOffice.slnx`: .NET solution file
- `gateway/package.json`: Gateway dependencies
- `apps/desktop/package.json`: Desktop dependencies

## Architecture Patterns and Design Principles

### Layered Architecture Pattern
TinadecOffice follows a strict three-layer architecture:
1. **Presentation Layer** (Desktop): UI rendering and user interaction
2. **API Gateway Layer** (Gateway): Request routing and proxy
3. **Business Logic Layer** (Core): Agent orchestration and state management

### State Management Pattern
- **Single Source of Truth**: Core is the only state authority
- **Stateless Layers**: Gateway and Desktop must not store business state
- **Event Sourcing**: All state changes are recorded as events
- **Approval Gates**: Write operations require explicit approval

### Tool Execution Pattern
- **Tool Registration**: Tools are registered through `IToolRegistry` interface
- **Capability Discovery**: Tools declare their capabilities and risk levels
- **Policy Enforcement**: Core governs tool execution based on risk policy
- **Structured Results**: Tools return structured evidence, not raw output

### Approval Gate Mechanism
- **Read-only Tools**: Can be auto-executed by Core policy
- **Write Operations**: Must go through approval workflow
- **Risk Assessment**: Tools are classified by risk level (low, medium, high)
- **Human Checkpoint**: High-risk operations require human approval

### API Design Principles
- **snake_case Convention**: All API DTOs use snake_case naming
- **RESTful Design**: Standard HTTP methods and status codes
- **SSE Events**: Real-time updates via Server-Sent Events
- **WebSocket Support**: Debug Studio uses WebSocket for real-time data

## Common Development Tasks

### Adding a New Tool
1. **Define Tool Interface**: Create tool specification in Core's `ToolRegistryService.cs`
2. **Implement Tool Logic**: Add tool implementation in appropriate layer (Core or Gateway)
3. **Register Tool**: Register tool through `IToolRegistry` interface
4. **Add Tests**: Create unit tests for tool functionality
5. **Update Documentation**: Update relevant `AGENTS.md` files

### Modifying Core Layer
1. **Read Architecture Docs**: Understand Core's role as state authority
2. **Follow Interface Pattern**: Use interfaces like `IToolRegistry`, `IToolInvocationAdapter`
3. **Maintain Abstraction**: Keep Core generic and reusable
4. **Update Contracts**: Update `Tinadec.Contracts` if changing DTOs
5. **Run Tests**: Execute Core tests before committing

### Modifying Gateway Layer
1. **Keep Gateway Thin**: Gateway should only proxy requests, not implement business logic
2. **Use coreClient**: Use `coreClient.ts` for Core communication
3. **Maintain API Contracts**: Keep API endpoints consistent with Core
4. **Test Proxy Logic**: Verify request forwarding works correctly

### Modifying Desktop Layer
1. **UI Only**: Desktop should only handle UI rendering and user interaction
2. **Call Gateway**: Always call Gateway, never Core directly
3. **Update DTO Mirrors**: Keep `api.ts` in sync with Core contracts
4. **Test UI Components**: Use Vitest for component testing

### Testing and Debugging
1. **Run Full Test Suite**: `npm test`
2. **Run Core Tests**: `dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal`
3. **Run Gateway Tests**: `npm run test -w @tinadec/gateway`
4. **Run Desktop Tests**: `npm run test -w @tinadec/desktop`
5. **Debug Studio**: Use Agent Debug Studio for tracing and debugging

### Common Commands
```powershell
# Start development environment
npm run dev

# Build all components
npm run build

# Run all tests
npm test

# Clear environment variables for .NET
Remove-Item Env:Version -ErrorAction SilentlyContinue
Remove-Item Env:Ice-Version -ErrorAction SilentlyContinue
```

## Start Here

Before making architecture, feature, UI, or tool-layer changes, read in this order:

1. `AGENTS.md` - shared project memory, commands, conventions, and anti-patterns.
2. `docs/agent-harness-product-model.zh-CN.md` or `docs/agent-harness-product-model.en.md` - product model and layer boundaries.
3. `docs/architecture.md` - current technical architecture and runtime topology.
4. The nearest nested `AGENTS.md` for the area you are changing.
5. The source files and tests that prove current behavior.

For Tool-layer / Code-suite work, inspect:

- `src/TinadecCore/Services/ToolRegistryService.cs` - Core capability registration and approval posture.
- `gateway/src/codeTools.ts` - Code tool catalog, DTO mapping, and fallback data.
- `apps/desktop/src/toolCatalog.ts` and `apps/desktop/src/pages/SettingsPage.vue` - Desktop presentation of Tool-layer capabilities.

## Product Model

The main mental model is:

- `Core` is the universal agent orchestration model and reusable agent harness.
- `Tool layer` provides executable, approval-aware capabilities.
- `Code` is not a peer layer. It is a built-in code/project/developer-environment tool suite inside the Tool layer.
- `Desktop` is the UI presentation surface for Core state, Tool-layer capabilities, approvals, events, traces, and configuration.

## Boundary Rules

- Core owns sessions, messages, runs, task graphs, approvals, model routes, agent profiles, tool policy, events, traces, and durable state.
- Tool-layer implementations execute capabilities and return structured evidence; they do not own orchestration state or approval policy.
- Code tools such as project templates, bash-like environments, built-in debugging, code editing, and Git worktree management should be registered through the Tool layer and governed by Core.
- Public Tool-layer DTOs use snake_case at the API boundary, even when TypeScript internals use camelCase.
- Desktop must call Gateway, not Core directly.
- Gateway is a proxy and tool bridge. Do not add durable product state there.

## Editing Rules

- Keep `AGENTS.md` and this file aligned when changing the AI reading path or product-layer vocabulary.
- When changing code, configs, commands, ports, contracts, module boundaries, or build/test workflows, update the relevant `AGENTS.md` file in the same work item.
- Do not edit generated or artifact directories: `bin/`, `obj/`, `node_modules/`, `dist/`, `dist-electron/`, `.vite/`, `coverage/`, `output/`, `tmp/`.
- Preserve the Windows-first workflow. Direct .NET PowerShell commands should clear `Version` and `Ice-Version` first.

## Ponytail Integration

Claude MUST apply Ponytail principles when:
- Creating new components or services
- Refactoring existing code
- Adding new features
- Fixing bugs

**Architecture-Aware Rules**:
- Desktop层: 优先使用 Vue 3 组合式 API，避免 Options API
- Gateway层: 保持 Elysia 路由简洁，不添加中间件逻辑
- Core层: 使用 .NET 10 最小 API 模式，避免过度抽象

**Safety Checklist**:
- [ ] 安全验证代码未被删除
- [ ] 错误处理机制完整
- [ ] 审批门机制正常工作
- [ ] 敏感信息未暴露
- [ ] 权限控制有效

## CodeGraph Integration

Claude SHOULD use CodeGraph for:
- Understanding cross-layer call chains
- Analyzing code impact before changes
- Locating specific functions and their callers
- Understanding state management flows

**Query Examples**:
```powershell
# 查询特定函数的调用链
codegraph explore "How does ToolRegistryService register tools?"

# 查询跨层依赖
codegraph explore "What calls CoreStore from Gateway?"

# 查询状态管理
codegraph explore "How does session state flow through the layers?"

# 查询工具执行
codegraph explore "How does approval gate mechanism work?"
```

**Best Practices**:
1. 查询前先思考：明确要理解的问题，避免模糊查询
2. 信任结果：CodeGraph 返回的代码已经是最新索引，无需重复验证
3. 关注调用链：利用 CodeGraph 的调用路径分析功能
4. 检查影响范围：修改代码前，使用 impact 分析功能

**Installation**:
```powershell
# 安装 CodeGraph CLI
npm i -g @colbymchenry/codegraph

# 初始化项目索引
cd d:\github\agent\TinadecCode
codegraph init

# 配置 Claude 集成
codegraph install --target=claude
```

**Verification Commands**:
```powershell
# 验证 CodeGraph 状态
npm run ai:codegraph:status

# 验证 Ponytail 配置
npm run ai:ponytail:validate

# 检查所有 AI 工具状态
npm run ai:tools:check
```

**Architecture Compliance Checklist**:
Before making changes, verify:
- [ ] Desktop layer does not call Core directly
- [ ] Gateway layer remains a thin proxy
- [ ] Core layer maintains state authority
- [ ] No business logic in Gateway/Desktop
- [ ] All mutations go through approval gates

## Useful Commands

```powershell
npm run dev
npm run build
npm test
Remove-Item Env:Version -ErrorAction SilentlyContinue
Remove-Item Env:Ice-Version -ErrorAction SilentlyContinue
dotnet test tests/TinadecCore.Tests/TinadecCore.Tests.csproj -v minimal
```
