# AI 工具快速启动指南

## 概述

本指南帮助 TinadecOffice 团队快速集成和使用 Ponytail 和 CodeGraph 两个 AI 编码辅助工具。

## 快速开始

### 1. 安装 CodeGraph（5 分钟）

```powershell
# 安装 CodeGraph CLI
npm i -g @colbymchenry/codegraph

# 进入项目目录
cd d:\github\agent\TinadecCode

# 初始化项目索引（首次需要几分钟）
codegraph init

# 配置 Claude 集成
codegraph install --target=claude

# 验证安装
codegraph status
```

### 2. 配置 Ponytail（2 分钟）

Ponytail 规则已经集成到项目的 `AGENTS.md` 和 `CLAUDE.md` 文件中，无需额外配置。

**验证方法**：
- 检查 `AGENTS.md` 中是否有 `## PONYTAIL CODING PRINCIPLES` 部分
- 检查 `CLAUDE.md` 中是否有 `## Ponytail Integration` 部分

### 3. 重启 AI 工具

重启 Claude Code 或其他 AI 工具，使配置生效。

---

## 日常使用

### Ponytail 使用场景

#### 场景 1：创建新组件
**传统方式**：
```vue
<template>
  <div class="date-picker-wrapper">
    <DatePicker 
      v-model="date"
      :format="dateFormat"
      :clearable="true"
      :placeholder="placeholder"
    />
  </div>
</template>

<script>
import DatePicker from '@vuepic/vue-datepicker'
import '@vuepic/vue-datepicker/dist/main.css'

export default {
  components: { DatePicker },
  data() {
    return {
      date: null,
      dateFormat: 'yyyy-MM-dd',
      placeholder: '选择日期'
    }
  }
}
</script>
```

**Ponytail 方式**：
```vue
<template>
  <input type="date" v-model="date" :placeholder="placeholder">
</template>

<script setup>
import { ref } from 'vue'

const date = ref(null)
const placeholder = '选择日期'
</script>
```

#### 场景 2：添加日志功能
**传统方式**：
```csharp
// 引入 Serilog
using Serilog;

public class MyService
{
    private readonly ILogger _logger;
    
    public MyService()
    {
        _logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();
    }
    
    public void DoWork()
    {
        _logger.Information("开始执行工作");
        // ...
    }
}
```

**Ponytail 方式**：
```csharp
// 使用 .NET 内置日志
using Microsoft.Extensions.Logging;

public class MyService
{
    private readonly ILogger<MyService> _logger;
    
    public MyService(ILogger<MyService> logger)
    {
        _logger = logger;
    }
    
    public void DoWork()
    {
        _logger.LogInformation("开始执行工作");
        // ...
    }
}
```

### CodeGraph 使用场景

#### 场景 1：理解跨层调用链

**问题**：Desktop 层的按钮点击后，如何追踪到 Core 层的处理逻辑？

**查询**：
```powershell
codegraph explore "How does the agent session creation flow from Desktop through Gateway to Core?"
```

**结果**：
1. Desktop 层：`apps/desktop/src/api.ts` → `createSession()`
2. Gateway 层：`gateway/src/index.ts` → 路由处理
3. Core 层：`src/TinadecCore/Services/OrchestratorService.cs` → 会话创建

#### 场景 2：分析工具注册机制

**查询**：
```powershell
codegraph explore "How does ToolRegistryService register new tools?"
```

**结果**：
- `IToolRegistry` 接口定义
- `ToolRegistryService` 实现
- `CodeCapabilityProvider` 注册示例

#### 场景 3：检查代码变更影响

**查询**：
```powershell
codegraph impact "CoreStore.cs"
```

**结果**：
- 受影响的文件列表
- 调用链分析
- 建议的测试范围

---

## 常用命令速查

### CodeGraph 命令

```powershell
# 初始化项目索引
codegraph init

# 查看索引状态
codegraph status

# 手动同步（通常自动同步）
codegraph sync

# 查询代码结构
codegraph explore "查询内容"

# 分析代码影响
codegraph impact "文件名"

# 查看调用者
codegraph callers "函数名"

# 查看被调用者
codegraph callees "函数名"
```

### Ponytail 原则速查

| 场景 | Ponytail 建议 |
|------|---------------|
| 日期选择器 | `<input type="date">` |
| HTTP 客户端 | 使用内置 `fetch` |
| 日志记录 | 使用框架内置日志 |
| 配置管理 | 使用环境变量 |
| 状态管理 | 使用框架内置方案 |
| 表单验证 | 使用 HTML5 原生验证 |

---

## 架构合规性检查清单

### 引入新工具前检查

- [ ] 工具是否符合三层架构原则？
- [ ] 工具应归属哪个架构层级？
- [ ] 是否会影响现有安全机制？
- [ ] 是否会绕过审批门机制？

### 代码生成后检查

**Ponytail 检查**：
- [ ] 代码行数是否显著减少？
- [ ] 是否删除了安全验证？（不应该）
- [ ] 是否删除了错误处理？（不应该）
- [ ] 是否使用了项目现有的服务/组件？

**CodeGraph 检查**：
- [ ] 是否理解了跨层调用关系？
- [ ] 是否分析了代码变更的影响范围？
- [ ] 是否验证了架构边界？

### 架构边界检查

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

---

## 故障排除

### CodeGraph 问题

**问题**：索引不完整
```powershell
# 重新索引
codegraph init --force

# 检查状态
codegraph status
```

**问题**：AI 工具无法连接
```powershell
# 重新安装集成
codegraph install --target=opencode --location=local --yes

# 重启 AI 工具
```

**问题**：查询结果不准确
```powershell
# 手动同步
codegraph sync

# 检查文件是否已保存
```

### Ponytail 问题

**问题**：AI 生成的代码过于简洁
**解决**：
1. 在 AGENTS.md 中强调安全规则
2. 添加示例说明何时不应简化
3. 定期审查 AI 生成的代码

**问题**：AI 不遵循三层架构边界
**解决**：
1. 在 CLAUDE.md 中明确架构约束
2. 添加架构违规的示例
3. 使用 CodeGraph 验证调用链

---

## 培训资源

### Ponytail 学习资源

1. **官方文档**：https://github.com/DietrichGebert/ponytail
2. **项目示例**：查看 `examples/` 目录
3. **团队培训**：每周代码审查会议

### CodeGraph 学习资源

1. **官方文档**：https://github.com/colbymchenry/codegraph
2. **常用查询**：参考 `ai-tools-integration-guide.md`
3. **团队培训**：每月技术分享会

---

## 常见问题

### Q1：Ponytail 会影响代码质量吗？
**A**：不会。Ponytail 强调"最小可行代码"，但绝不删除安全验证、错误处理或可访问性代码。

### Q2：CodeGraph 会泄露代码吗？
**A**：不会。CodeGraph 100% 本地运行，数据不离开你的机器，无需 API 密钥。

### Q3：这两个工具会冲突吗？
**A**：不会。Ponytail 专注于代码生成策略，CodeGraph 专注于代码理解能力，两者互补。

### Q4：如何评估工具效果？
**A**：
- Ponytail：统计代码行数减少比例
- CodeGraph：统计工具调用次数减少比例

---

## 联系支持

如有问题或建议，请联系：
- **技术负责人**：[待填写]
- **文档维护**：[待填写]
- **团队 Slack**：#tinadec-ai-tools

---

**文档版本**：1.0  
**创建日期**：2026-06-29  
**最后更新**：2026-06-29
