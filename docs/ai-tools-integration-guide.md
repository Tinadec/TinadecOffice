# TinadecOffice AI 工具集成指南

## 概述

本文档详细说明如何将 Ponytail 和 CodeGraph 两个 AI 编码辅助工具集成到 TinadecOffice 项目的开发流程中。这两个工具分别从**代码生成策略**和**代码理解能力**两个维度提升 AI 编码效率。

## 项目技术栈分析

### TinadecOffice 四层架构
- **Desktop层**: Electron + Vue 3 + Vite + Tailwind CSS (端口 5173)
- **Gateway层**: Elysia TypeScript + Node.js (端口 48730)
- **Core层**: .NET 10 C# + ASP.NET Core (端口 48731)
- **Native层**: Rust workspace

### 支持的编程语言
- TypeScript/JavaScript (Desktop, Gateway)
- C# (Core)
- Rust (Native)
- Vue 3 (Desktop UI)

### 现有 AI 工具支持
- Claude Code (CLAUDE.md)
- Codex (AGENTS.md)
- 其他 AI 代理通过统一的 AGENTS.md 配置

---

## 第一部分：Ponytail 引入方案

### 1.1 Ponytail 核心价值

**Ponytail** 是一个 AI 编码助手规则集，通过"懒惰的资深开发者"思维模式：
- 减少 54% 的代码量（平均）
- 降低 20% 的 API 调用成本
- 提高 27% 的开发速度
- 保持 100% 的安全性

### 1.2 集成策略

#### 方案 A：规则文件集成（推荐）

将 Ponytail 的核心规则集成到现有的 AGENTS.md 和 CLAUDE.md 文件中。

**优势**：
- 无需额外安装
- 与现有 AI 工具配置统一
- 符合项目架构原则

**实施步骤**：

1. **在 AGENTS.md 中添加 Ponytail 规则部分**

```markdown
## PONYTAIL CODING PRINCIPLES

Before writing code, AI agents MUST follow this decision ladder:

1. **YAGNI Check**: Does this need to exist? → No: skip it
2. **Reuse Check**: Already in this codebase? → Reuse it, don't rewrite
3. **Stdlib Check**: Stdlib does it? → Use it
4. **Platform Check**: Native platform feature? → Use it
5. **Dependency Check**: Installed dependency? → Use it
6. **One-liner Check**: One line? → One line
7. **Minimum Viable**: Only then: the minimum that works

**Safety Rules**:
- NEVER remove validation, error handling, security, or accessibility code
- NEVER compromise security guards for brevity
- ALWAYS maintain existing safety patterns

**TinadecOffice Specific Applications**:
- Core层: 优先使用现有的服务接口，避免重复实现
- Gateway层: 保持薄代理模式，不添加业务逻辑
- Desktop层: 复用现有组件，避免过度封装
- Native层: 利用 Codex 现有能力，不重新实现底层工具
```

2. **在 CLAUDE.md 中添加 Claude 特定配置**

```markdown
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
- Native层: 复用 Codex 原语，不重新实现文件操作
```

#### 方案 B：插件集成（可选）

如果使用支持插件的 AI 工具（如 OpenCode），可以通过插件形式集成。

**OpenCode 配置**：
```json
{
  "plugin": ["@dietrichgebert/ponytail"]
}
```

### 1.3 使用场景

#### 场景 1：新功能开发
**问题**：需要添加日期选择器组件
**Ponytail 方案**：
```vue
<!-- 优先使用原生 HTML5 日期输入 -->
<input type="date" v-model="selectedDate">

<!-- 而不是安装第三方日期选择器库 -->
<!-- <DatePicker v-model="selectedDate" /> -->
```

#### 场景 2：Core 层服务开发
**问题**：需要添加日志记录功能
**Ponytail 方案**：
```csharp
// 优先使用 .NET 内置日志
ILogger<MyService> _logger;

// 而不是引入第三方日志库
// Serilog, NLog 等
```

#### 场景 3：Gateway 层路由开发
**问题**：需要添加请求验证
**Ponytail 方案**：
```typescript
// 优先使用 Elysia 内置验证
app.post('/api/v1/endpoint', {
  body: t.Object({
    name: t.String()
  })
}, (context) => {
  // 处理逻辑
})

// 而不是引入额外的验证库
// Joi, Yup, Zod 等
```

### 1.4 对四层架构的影响

**正面影响**：
- **Desktop层**: 减少不必要的组件依赖，保持 UI 层轻量
- **Gateway层**: 强化薄代理模式，避免业务逻辑泄漏
- **Core层**: 促进服务复用，减少重复实现
- **Native层**: 鼓励使用 Codex 现有能力

**需要避免的陷阱**：
- 不要为了简洁而删除安全验证代码
- 不要跳过错误处理机制
- 不要忽略可访问性要求

### 1.5 验证清单

在应用 Ponytail 原则后，检查以下项目：

- [ ] 代码行数是否显著减少？
- [ ] 是否删除了安全验证？（不应该）
- [ ] 是否删除了错误处理？（不应该）
- [ ] 是否使用了项目现有的服务/组件？
- [ ] 是否遵循了四层架构边界？

---

## 第二部分：CodeGraph 引入方案

### 2.1 CodeGraph 核心价值

**CodeGraph** 是一个预索引的代码知识图谱工具：
- 减少 58% 的工具调用
- 提高 22% 的响应速度
- 支持 20+ 种编程语言
- 100% 本地运行，无需 API 密钥

### 2.2 安装与配置

#### Windows 安装

**方法 1：PowerShell 安装脚本**
```powershell
irm https://raw.githubusercontent.com/colbymchenry/codegraph/main/install.ps1 | iex
```

**方法 2：npm 安装**
```powershell
npm i -g @colbymchenry/codegraph
```

#### 项目初始化

```powershell
cd d:\github\agent\TinadecCode
codegraph init
```

#### AI 工具集成

**Claude Code 集成**：
```powershell
codegraph install --target=claude
```

**OpenCode 集成**：
```powershell
codegraph install --target=opencode
```

**通用配置（手动）**：

在项目的 MCP 配置文件中添加：
```json
{
  "mcpServers": {
    "codegraph": {
      "type": "stdio",
      "command": "codegraph",
      "args": ["serve", "--mcp"]
    }
  }
}
```

### 2.3 使用场景

#### 场景 1：理解跨层调用链

**问题**：Desktop 层的某个按钮点击后，如何追踪到 Core 层的处理逻辑？

**CodeGraph 查询**：
```
How does the agent session creation flow from Desktop through Gateway to Core?
```

**返回结果**：
- Desktop 层：`apps/desktop/src/api.ts` 中的 `createSession()` 函数
- Gateway 层：`apps/gateway/src/index.ts` 中的路由处理
- Core 层：`src/TinadecCore/Services/OrchestratorService.cs` 中的会话创建逻辑

#### 场景 2：分析工具注册机制

**问题**：新工具如何注册到 Core 层的工具注册表？

**CodeGraph 查询**：
```
How does ToolRegistryService register new tools and what interfaces are involved?
```

**返回结果**：
- `IToolRegistry` 接口定义
- `ToolRegistryService` 实现
- `CodeCapabilityProvider` 注册示例

#### 场景 3：追踪状态管理

**问题**：Core 层的 CoreStore 如何管理会话状态？

**CodeGraph 查询**：
```
How does CoreStore manage session state and what are the key state transitions?
```

**返回结果**：
- SQLite schema 定义
- 状态转换逻辑
- 事件发布机制

### 2.4 语言支持矩阵

针对 TinadecOffice 的技术栈：

| 语言 | 支持状态 | 主要应用层 |
|------|----------|------------|
| TypeScript | ✅ 完全支持 | Desktop, Gateway |
| C# | ✅ 完全支持 | Core |
| Rust | ✅ 完全支持 | Native |
| Vue | ✅ 完全支持 | Desktop UI |

### 2.5 自动同步配置

CodeGraph 默认启用自动同步，配置文件更改时自动更新图谱。

**自定义配置**（可选）：

在项目根目录创建 `codegraph.json`：
```json
{
  "extensions": {
    ".vue": "vue"
  },
  "exclude": [
    "node_modules/",
    "dist/",
    "bin/",
    "obj/",
    "native/target/"
  ]
}
```

### 2.6 对四层架构的影响

**架构理解增强**：
- **Desktop层**: 理解 Vue 组件与 API 客户端的交互
- **Gateway层**: 追踪请求代理到 Core 的完整流程
- **Core层**: 分析服务间的依赖关系和状态管理
- **Native层**: 理解 Codex 原语的调用链

**开发效率提升**：
- 减少手动文件搜索时间
- 快速定位跨层调用关系
- 精准理解代码变更的影响范围

### 2.7 使用规范

#### 最佳实践

1. **查询前先思考**：明确要理解的问题，避免模糊查询
2. **信任结果**：CodeGraph 返回的代码已经是最新索引，无需重复验证
3. **关注调用链**：利用 CodeGraph 的调用路径分析功能
4. **检查影响范围**：修改代码前，使用 impact 分析功能

#### 避免的误区

- 不要完全依赖 CodeGraph，仍需阅读关键源代码
- 不要在 CodeGraph 索引未更新时信任过时信息
- 不要忽略架构边界，即使 CodeGraph 显示了跨层调用

---

## 第三部分：集成实施计划

### 3.1 阶段一：Ponytail 集成（1-2 天）

**目标**：将 Ponytail 规则集成到现有 AI 配置文件

**任务清单**：
1. [ ] 更新 AGENTS.md，添加 Ponytail 规则部分
2. [ ] 更新 CLAUDE.md，添加 Claude 特定配置
3. [ ] 创建 Ponytail 使用示例文档
4. [ ] 团队培训和规则宣导

**验收标准**：
- AI 代理能够识别并应用 Ponytail 原则
- 代码生成符合简洁性要求
- 不影响现有安全性和错误处理

### 3.2 阶段二：CodeGraph 集成（2-3 天）

**目标**：安装并配置 CodeGraph，建立代码知识图谱

**任务清单**：
1. [ ] 安装 CodeGraph CLI
2. [ ] 初始化项目索引
3. [ ] 配置 AI 工具集成
4. [ ] 验证语言支持（TypeScript, C#, Rust, Vue）
5. [ ] 创建常用查询示例
6. [ ] 团队培训和使用指导

**验收标准**：
- CodeGraph 能够正确索引 TinadecOffice 代码库
- AI 工具能够使用 CodeGraph 查询
- 团队成员能够使用常用查询

### 3.3 阶段三：优化与调整（持续）

**目标**：根据使用反馈优化配置

**任务清单**：
1. [ ] 收集使用反馈
2. [ ] 调整 Ponytail 规则细节
3. [ ] 优化 CodeGraph 查询模板
4. [ ] 更新文档和培训材料

---

## 第四部分：架构合规性检查

### 4.1 四层架构边界检查

引入新工具时，必须确保：

**Desktop层**：
- [ ] 不直接调用 Core 层
- [ ] 不存储业务状态
- [ ] 只通过 Gateway 通信

**Gateway层**：
- [ ] 保持薄代理模式
- [ ] 不实现业务逻辑
- [ ] 只代理请求到 Core

**Core层**：
- [ ] 保持唯一状态权威
- [ ] 通过接口治理工具
- [ ] 不硬编码工具逻辑

**Native层**：
- [ ] 作为 Tool layer 的底层能力提供者
- [ ] 通过稳定适配器与 Core 交互

### 4.2 工具集成规范

**新工具引入流程**：
1. 评估工具是否符合四层架构原则
2. 确定工具应归属的架构层级
3. 设计符合分层规范的集成方案
4. 实施并验证架构合规性
5. 更新相关文档和 AGENTS.md

**禁止的行为**：
- ❌ Desktop 直接调用 Native 层
- ❌ Gateway 存储业务状态
- ❌ Core 硬编码工具逻辑
- ❌ Native 层绕过 Core 审批

### 4.3 安全性检查清单

引入 Ponytail 和 CodeGraph 后，检查：

- [ ] 安全验证代码未被删除
- [ ] 错误处理机制完整
- [ ] 审批门机制正常工作
- [ ] 敏感信息未暴露
- [ ] 权限控制有效

---

## 第五部分：故障排除

### 5.1 Ponytail 相关问题

**问题**：AI 生成的代码过于简洁，缺少必要的错误处理
**解决**：
1. 在 AGENTS.md 中强调安全规则
2. 添加示例说明何时不应简化
3. 定期审查 AI 生成的代码

**问题**：AI 不遵循四层架构边界
**解决**：
1. 在 CLAUDE.md 中明确架构约束
2. 添加架构违规的示例
3. 使用 CodeGraph 验证调用链

### 5.2 CodeGraph 相关问题

**问题**：CodeGraph 索引不完整
**解决**：
```powershell
# 重新索引项目
codegraph init --force

# 检查索引状态
codegraph status
```

**问题**：AI 工具无法连接 CodeGraph
**解决**：
```powershell
# 重新安装集成
codegraph install --target=claude

# 重启 AI 工具
```

**问题**：查询结果不准确
**解决**：
1. 检查文件是否已保存
2. 等待自动同步完成（默认 2 秒）
3. 手动同步：`codegraph sync`

---

## 第六部分：监控与评估

### 6.1 效果评估指标

**Ponytail 效果评估**：
- 代码行数减少比例
- 开发时间缩短比例
- 代码质量评分
- 安全性检查通过率

**CodeGraph 效果评估**：
- 工具调用次数减少比例
- 代码理解时间缩短比例
- 跨层调用定位准确率
- 开发效率提升比例

### 6.2 定期审查

**每周审查**：
- AI 生成代码的质量检查
- 架构合规性验证
- 工具使用情况统计

**每月审查**：
- Ponytail 规则效果评估
- CodeGraph 索引准确性验证
- 团队使用反馈收集

---

## 附录

### A. 常用 CodeGraph 查询示例

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

### B. Ponytail 规则速查表

| 场景 | Ponytail 建议 |
|------|---------------|
| 日期选择器 | `<input type="date">` |
| HTTP 客户端 | 使用内置 `fetch` |
| 日志记录 | 使用框架内置日志 |
| 配置管理 | 使用环境变量 |
| 状态管理 | 使用框架内置方案 |

### C. 相关文档链接

- [TinadecOffice 架构文档](architecture.md)
- [Agent Harness 产品模型](agent-harness-product-model.zh-CN.md)
- [启动运行手册](startup.md)
- [参考项目映射](reference-project-map.md)

---

**文档版本**: 1.0  
**创建日期**: 2026-06-29  
**维护者**: TinadecOffice 开发团队  
**最后更新**: 2026-06-29
