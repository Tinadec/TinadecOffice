# PR #14 设置系统重构——合并前修复计划

## 摘要

基于对 PR #14（`setting/multi` 分支，HEAD = `bf44b90`）的代码审查与实地核实，本计划处理三项合并前工作：

1. **必须修复**：移除 `styles.css` 与 `settings/settings.css` 开头的 UTF-8 BOM（PR 新引入）。
2. **扩展 CSS 审计**：审查报告中关于 CSS 提取范围的具体论据不准确（`.chat-active-panel` 等类名仍在 `styles.css`，未移入 `settings.css`），但实地核查发现两文件间存在 **30 个重叠类选择器**，存在真实的优先级漂移风险，需逐一审计并可能扩展契约测试。
3. **次要改进**：为 `createAsyncSettingsComponent.ts` 的 `onError` 添加意图注释；在验证步骤中加入干净构建确认。

---

## 当前状态分析

### 已确认的事实

| 项 | 核实结果 |
|---|---|
| `styles.css` BOM | 基线提交 `e733325` 无 BOM（首字节 `40 69 6D` = `@im`）；当前 HEAD 有 BOM（`EF BB BF 40`）。**PR 引入。** |
| `settings.css` BOM | 新文件，首字节 `EF BB BF 2F` = BOM + `/`（`/*`）。**PR 引入。** |
| `styles.css` 行数 | 当前 5102 行（审查报告称"约 560 行"不准确）；基线约 8190 字节级行数，PR 净减约 3090 行。 |
| `.chat-active-panel` 等 | 仍在 `styles.css`（5 处），**不在** `settings.css`（0 处）。审查报告此项论据有误。 |
| `settingsCssContract.test.ts` | 已存在，验证 `settings.css` 含设置相关选择器、`styles.css` 仍含共享布局选择器（`.shell`、`.sidebar`、`.conversation` 等）。 |
| `onError` 回调 | `createAsyncSettingsComponent.ts:34-36` 始终调用 `fail()`，无注释说明意图。 |
| `SettingsPage.vue` CSS 导入 | 第 1249 行 `import '../settings/settings.css'` 位于 `<script setup>` 末尾。 |
| `BrandLogo` 尺寸 | `AppSidebar.vue:112` 为 `:size="14"`（原 18）。本次计划不改。 |
| `vite.config.ts` | `test.css: true`（正确，组件测试需要 CSS 处理）。 |

### 重叠类选择器（审计目标）

下列 30 个类名同时在 `styles.css` 与 `settings.css` 中出现，存在优先级漂移风险：

```
.active  .add  .agent-card  .agent-card-main  .agent-card-more
.agent-column  .agent-config-section  .agent-detail-head  .agent-detail-panel
.agent-topology-section  .catalog-readiness-row  .compact  .git-manager-grid
.git-manager-summary  .git-manager-tool-row  .modal-actions
.model-readiness-metrics  .model-readiness-routes  .model-section-header
.ok  .primary-button  .provider-brand-icon  .provider-detail-logo
.provider-readiness-detail  .ready  .risky  .secondary-button
.settings-page  .vue  .warn
```

其中 `.settings-page` 同时出现在两文件中是可预期的（设置页本身使用），但 `.agent-card`、`.primary-button`、`.git-manager-*`、`.model-readiness-*` 等若在非设置页面也使用，则需确认两文件中的定义是否一致或是否应只保留在一处。

---

## 拟议变更

### 变更 1：移除 `styles.css` 的 BOM

**文件**：[apps/desktop/src/styles.css](file:///c:/git/agent/TinadecOffice/apps/desktop/src/styles.css)

**做法**：以 UTF-8（无 BOM）重写文件。用 .NET API 读取全部字节、跳过前 3 字节（`EF BB BF`）、写回剩余字节。不改动任何 CSS 内容。

**为什么**：BOM 会使部分 CSS 解析器将首条 `@import "@fontsource-variable/geist";` 视为无效规则而静默忽略，导致字体不加载。审查报告此项判断正确。

**如何做**（执行阶段）：使用 `[System.IO.File]::ReadAllBytes` → `Skip(3)` → `WriteAllBytes`，或用 Edit 工具删除 BOM 行后重写第一行。验证首字节为 `40 69 6D`（`@im`）。

### 变更 2：移除 `settings/settings.css` 的 BOM

**文件**：[apps/desktop/src/settings/settings.css](file:///c:/git/agent/TinadecOffice/apps/desktop/src/settings/settings.css)

**做法**：同变更 1，以 UTF-8（无 BOM）重写。验证首字节为 `2F 2A`（`/*`）。

**为什么**：同变更 1。新文件不应带 BOM，且审查报告未提及此文件但问题相同。

### 变更 3：扩展 CSS 审计——逐一核对 30 个重叠类名

**涉及文件**：
- [apps/desktop/src/settings/settings.css](file:///c:/git/agent/TinadecOffice/apps/desktop/src/settings/settings.css)（读）
- [apps/desktop/src/styles.css](file:///c:/git/agent/TinadecOffice/apps/desktop/src/styles.css)（读）
- [apps/desktop/src/settings/settingsCssContract.test.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/settings/settingsCssContract.test.ts)（可能扩展）
- `apps/desktop/src/**/*.vue`（读，核对类名使用位置）

**做法**（执行阶段）：

1. **提取重叠类的定义**：对 30 个重叠类，分别在 `styles.css` 与 `settings.css` 中提取其规则块，比对是否：
   - 完全相同（冗余但无害，可从 `settings.css` 删除重复定义）
   - 不同（真实的优先级漂移风险，需判断哪个文件应保留权威定义）
2. **核对使用位置**：对每个重叠类，用 Grep 在 `apps/desktop/src/**/*.vue` 中搜索其使用，判断是否在非设置页面使用：
   - 若类名仅在设置页面使用 → `settings.css` 中的定义权威，`styles.css` 中的重复定义可删
   - 若类名在非设置页面也使用 → `styles.css` 中的定义必须保留，`settings.css` 中的重复定义需判断是否应删或保持一致
3. **扩展契约测试**（视审计结果）：若发现需要持续守护的规则，在 `settingsCssContract.test.ts` 中新增断言，例如：
   - 声明 `settings.css` 不应重新定义 `styles.css` 中已定义的非设置类（白名单除外）
   - 或声明两文件中重叠类的定义必须一致
4. **清理冗余定义**：对审计中确认的冗余或漂移定义，从 `settings.css` 中删除重复规则（保守原则：只删确认安全的重复，不重排样式结构）

**为什么**：审查报告的 CSS 范围论据虽不准确，但 30 个重叠类名是真实风险点。扩展审计将"需要验证"转化为"已验证并守护"。

### 变更 4：为 `onError` 添加意图注释

**文件**：[apps/desktop/src/settings/createAsyncSettingsComponent.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/settings/createAsyncSettingsComponent.ts) 第 34-36 行

**做法**：在 `onError` 回调上方添加单行注释：

```ts
// 错误恢复由 SettingsModuleBoundary 通过 boundaryKey 变更重新挂载处理，异步组件内部不重试。
onError(error, _retry, fail, _attempts) {
  fail()
},
```

**为什么**：审查建议。明确设计意图，避免后续维护者误以为缺少重试逻辑是缺陷。

---

## 假设与决策

1. **BOM 移除方式**：使用 .NET 字节级 API（`ReadAllBytes` → `Skip(3)` → `WriteAllBytes`），确保不引入新编码问题。不用 PowerShell `Out-File`（默认加 BOM）。
2. **CSS 审计范围**：仅审计 `apps/desktop/src` 下的 `.vue` 和 `.css` 文件，不涉及 `node_modules` 或构建产物。
3. **契约测试扩展**：仅在审计发现需要持续守护的规则时才扩展测试，不预置假设性断言（遵循 YAGNI）。
4. **不改 `BrandLogo` 尺寸**：审查仅建议"确认"，未要求改回；视觉确认在验证步骤中处理。
5. **不改 `SettingsPage.vue` 的 CSS 导入位置**：Vite 的 CSS 代码分割会在组件加载时通过 `<link>` 注入，审查的"确认"需求在验证步骤中处理。
6. **不改 `vite.config.ts`**：`css: true` 是正确的，无需修改。
7. **语言**：代码注释用中文（与项目现有注释风格一致，如 `settings.css` 头部注释）。

---

## 验证步骤

### 步骤 1：BOM 移除验证

```powershell
# 两个文件首字节应分别为 @im 和 /*
$styles = [System.IO.File]::ReadAllBytes("apps/desktop/src/styles.css")
$settings = [System.IO.File]::ReadAllBytes("apps/desktop/src/settings/settings.css")
"styles.css first 3 bytes: {0:X2} {1:X2} {2:X2}" -f $styles[0],$styles[1],$styles[2]   # 期望 40 69 6D
"settings.css first 3 bytes: {0:X2} {1:X2} {2:X2}" -f $settings[0],$settings[1],$settings[2]  # 期望 2F 2A 20
```

### 步骤 2：CSS 契约测试通过

```bash
npm --prefix apps/desktop test -- --run src/settings/settingsCssContract.test.ts
```

若变更 3 扩展了测试，新增断言也应通过。

### 步骤 3：全部桌面端测试通过

```bash
npm --prefix apps/desktop test
```

确认 3 个新测试文件（`SettingsModuleBoundary.test.ts`、`createAsyncSettingsComponent.test.ts`、`useSettingsNavigation.test.ts`）及 `settingsRegistry.test.ts`、`settingsCssContract.test.ts` 全部通过。

### 步骤 4：干净构建验证（审查建议）

```bash
# 清理构建产物后重新构建
npm --prefix apps/desktop run build
```

确认：
- 构建无错误
- `settings.css` 作为独立 chunk 生成（检查 `dist/assets/` 下是否有 settings 相关 CSS chunk）
- `styles.css` 缩减后不影响主聊天界面（构建产物中 `@import` 字体规则生效）

### 步骤 5：CSS 审计结果记录

在执行阶段的最终回复中，列出 30 个重叠类名的审计结论表格：类名 | `styles.css` 定义 | `settings.css` 定义 | 是否一致 | 非设置页面是否使用 | 处置。

---

## 执行顺序

1. 变更 1 + 变更 2（BOM 移除，并行，互不依赖）
2. 变更 3（CSS 审计——先提取定义、核对使用位置，再决定是否清理冗余/扩展测试）
3. 变更 4（onError 注释，独立）
4. 验证步骤 1-5
