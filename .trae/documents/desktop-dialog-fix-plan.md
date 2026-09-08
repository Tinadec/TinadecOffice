> **⚠️ 历史文档（不可作为事实源）**
> 本文是当时由 AI 生成/执行的计划或设计稿，**未随代码更新**：其中的文件路径、路由名、数量统计与"已完成"结论均可能已失效。以源码、测试与 `AGENTS.md` 的 M 段记录为准。归档核对时间：2026-09-07（commit 3e8ff30）。

# Desktop 新对话对话框 UI 问题分析与修复计划

## 问题概述

Desktop 应用中新对话的对话框（WelcomeScreen 组件）存在多个严重的 UI/UX 问题，核心表现为：**不该滚动的地方可以滚动，整体不像一个现代 AI 对话输入框**。

---

## 问题根因分析

### 问题 1：`.welcome-screen` 整体容器设置了 `overflow: auto`

**位置**: [styles.css:1495-1503](file:///d:/github/TinadecCode/apps/desktop/src/styles.css#L1495-L1503)

```css
.welcome-screen {
  display: flex;
  flex-direction: column;
  justify-content: flex-start;
  padding-left: 60px;
  padding-top: 28vh;
  height: 100%;
  overflow: auto;  /* ← 罪魁祸首！整个欢迎页面可以上下滚动 */
}
```

**影响**：整个 WelcomeScreen 页面可以上下滚动，导致：
- 选择项目的下拉菜单展开时，页面可以滚动
- 上传图片/文件的弹出菜单展开时，页面可以滚动
- 用户在输入框中操作时，页面可以意外滚动

**修复**：将 `overflow: auto` 改为 `overflow: hidden` 或 `overflow: clip`，因为欢迎页面内容不应超出视口。

---

### 问题 2：`.project-dropdown-menu` 设置了 `overflow-y: auto`

**位置**: [styles.css:1727-1741](file:///d:/github/TinadecCode/apps/desktop/src/styles.css#L1727-L1741)

```css
.project-dropdown-menu {
  position: absolute;
  top: calc(100% + 6px);
  left: 0;
  z-index: 50;
  display: flex;
  flex-direction: column;
  min-width: 200px;
  max-height: 280px;
  overflow-y: auto;  /* ← 项目下拉菜单可以滚动 */
  ...
}
```

**影响**：当项目数量较多时，下拉菜单本身可以滚动——这本身是合理的，但问题在于它和父容器的 `overflow: auto` 产生了**滚动穿透**。用户在项目下拉菜单上滚动时，滚动事件可能冒泡到父容器 `.welcome-screen`，导致整个页面也在滚动。

**修复**：
1. 使用 shadcn 的 `ScrollArea` 组件替代原生 `overflow-y: auto`，提供更好的滚动体验
2. 在下拉菜单打开时阻止父容器滚动

---

### 问题 3：`.plus-menu`（上传图片/文件菜单）没有 `overflow` 控制

**位置**: [styles.css:1588-1602](file:///d:/github/TinadecCode/apps/desktop/src/styles.css#L1588-L1602)

```css
.plus-menu {
  position: absolute;
  top: calc(100% + 6px);
  left: 0;
  z-index: 50;
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 6px;
  min-width: 140px;
  ...
}
```

**影响**：plus-menu 是绝对定位的弹出菜单，但因为父容器 `.welcome-screen` 有 `overflow: auto`，当菜单展开时，如果用户在菜单区域滚动鼠标，整个页面也会跟着滚动。这就是"上传图片和文件也可以上下滑动"的根本原因。

---

### 问题 4：对话框设计不符合现代 AI 对话输入框范式

当前 WelcomeScreen 的对话框结构：
- 顶部：标题 + 模式切换按钮
- 中间：一个扁平的输入框（`<input>` 单行），带 + 按钮和发送按钮
- 底部：工具栏（项目选择、Plan 按钮、模型选择）

**与现代 AI 对话框的差距**：

| 方面 | 当前实现 | 现代AI对话框（如 ChatGPT/Claude） |
|------|---------|----------------------------------|
| 输入框 | 单行 `<input>` | 多行 `<textarea>` 自动增高 |
| 附件预览 | 无 | 上传后显示缩略图/文件名 |
| 项目选择 | 下拉菜单在工具栏底部 | 顶部标签或独立选择器 |
| 整体布局 | 偏左 60px + 28vh padding | 居中，垂直居中 |
| 滚动行为 | 整体可滚动 | 固定不动，仅输入框内容可滚动 |
| 对话框圆角 | 16px | 通常更大（20-24px） |
| 输入区域高度 | 固定 40px | 自适应，最小 48-52px |

---

### 问题 5：滚动穿透（Scroll Chaining）

这是最核心的 bug。当 `.welcome-screen` 设置了 `overflow: auto` 时：
1. 任何子元素（包括绝对定位的弹出菜单）上的滚动事件都可能冒泡到父容器
2. 用户在下拉菜单或弹出菜单上滚动时，整个页面也在滚动
3. 这就是为什么"选择项目的对话框可以上下滑动"和"上传图片和文件也可以上下滑动"

---

## 修复计划

### Step 1: 修复 `.welcome-screen` 的滚动问题
- 将 `overflow: auto` 改为 `overflow: hidden`
- 确保欢迎页面内容不会超出视口

### Step 2: 修复项目下拉菜单的滚动穿透
- 使用 shadcn `ScrollArea` 组件包裹项目列表
- 在下拉菜单打开时，给 `.welcome-screen` 添加 `overflow: hidden`（如果 Step 1 还不够）
- 或者使用 `overscroll-behavior: contain` CSS 属性防止滚动穿透

### Step 3: 修复 plus-menu 的滚动穿透
- 同样添加 `overscroll-behavior: contain`
- 确保 plus-menu 展开时不会触发父容器滚动

### Step 4: 将输入框从单行 `<input>` 改为多行自适应 `<textarea>`
- 替换 `welcome-dialog-input` 为 textarea
- 实现自动增高逻辑（根据内容调整高度）
- 设置最大高度限制

### Step 5: 改善对话框整体布局
- 调整 padding，使对话框更居中
- 增大对话框圆角
- 优化工具栏的视觉层次
- 添加附件预览区域（上传图片/文件后显示）

### Step 6: 使用 shadcn 组件替换自定义实现
- 项目选择器：使用 `UiSelect` 或 `UiCommand` + `UiPopover`
- 弹出菜单：使用 `UiDropdownMenu`
- 滚动区域：使用 `UiScrollArea`

### Step 7: 添加 `overscroll-behavior: contain` 到所有弹出层
- `.project-dropdown-menu`
- `.plus-menu`
- 任何其他绝对定位的弹出层

---

## 涉及文件

| 文件 | 修改内容 |
|------|---------|
| `apps/desktop/src/styles.css` | 修复 `.welcome-screen` overflow、添加 `overscroll-behavior`、调整对话框样式 |
| `apps/desktop/src/components/WelcomeScreen.vue` | 重构对话框结构，input→textarea，集成 shadcn 组件 |

---

## 优先级

1. **P0 - 滚动穿透修复**：Step 1, 2, 3, 7（立即修复，这是最明显的 bug）
2. **P1 - 输入框改进**：Step 4（核心体验改进）
3. **P2 - 布局优化**：Step 5, 6（视觉和体验提升）
