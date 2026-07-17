# Splash 加载动画实现计划

## 摘要

在 `apps/desktop/index.html` 中注入一个极简的 splash 占位层：居中显示 Tinadec 品牌 logo（内联 SVG），背景色与暗色主题 `--bg-primary` 一致。Vue `app.mount('#app')` 挂载时自动替换该占位层，无需任何显式切换逻辑或新增文件。

## 当前状态分析

### 现有加载流程（`apps/desktop/electron/main.cjs` L33-82）
```
app.whenReady() → createWindow()
  → BrowserWindow({ show: false, ... })
  → win.loadURL/loadFile(index.html)
  → win.once('ready-to-show', () => win.show())
```
窗口在 `show: false` 状态下创建，渲染进程完成首帧后 `ready-to-show` 触发才 `win.show()`。

### 白屏根因（`apps/desktop/index.html`）
```html
<div id="app"></div>  <!-- 空 div，Vue 挂载前无任何内容 -->
```
`ready-to-show` 触发时 HTML 已解析但 Vue 尚未挂载，用户看到空白窗口。

### 关键发现
1. **`app.mount('#app')` 替换 `#app` 的子内容**（Vue 3 行为）——占位层放在 `#app` 内部会被自动替换，无需手动清理。
2. **`ready-to-show` 在 HTML 解析后触发**——此时 splash 占位已在 DOM 中，`win.show()` 时用户立即看到 logo。
3. **背景色无缝衔接**：App.vue 的 `.background-layer` 使用 `--bg-primary: #0a0e14`（暗色主题，`styles.css` L116），splash 用同色避免切换闪烁。
4. **圆角自动裁剪**：`styles.css` L256-259 `html { border-radius: 12px; overflow: hidden }` 会裁剪 `position: fixed` 的 splash 层，无需额外处理。
5. **品牌色已确立**：`--accent-brand: #2ec4b6`（青色，`styles.css` L164），与 `favicon.svg` 一致。
6. **Logo SVG path 已有**：`apps/desktop/public/tinadec-logo.svg` 和 `BrandLogo.vue` 使用相同 path data，可直接内联。

### 不需要改动的文件
- **`main.cjs`**：`show: false` + `ready-to-show` → `win.show()` 模式已经正确，splash 在窗口显示时就能被看到。
- **`main.ts`**：`app.mount('#app')` 自动替换占位层，无需改动。
- **`App.vue`**：`.background-layer` 背景色与 splash 一致，无冲突。
- **`styles.css`**：`html { border-radius; overflow: hidden }` 自动裁剪 splash 层。
- **无需新增文件**：不创建 splash.html、splash.cjs 或任何新模块。

## 拟定改动

### 唯一改动文件：`apps/desktop/index.html`

**目标**：在 `<head>` 加内联 splash 样式，在 `<div id="app">` 内加 splash 占位层（含内联 SVG logo）。

#### 改动 1：`<head>` 内新增内联 `<style>`

在 `</title>` 之后、`</head>` 之前插入：

```html
<style>
  /* Splash 占位层 —— Vue 挂载 #app 时自动替换此内容 */
  .splash-placeholder {
    position: fixed;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    background: #0a0e14; /* 匹配暗色主题 --bg-primary (styles.css L116) */
  }
  .splash-placeholder svg {
    height: 88px;
    width: auto;
    color: #2ec4b6; /* 品牌强调色 --accent-brand (styles.css L164) */
  }
</style>
```

**设计决策**：
- `position: fixed; inset: 0`：覆盖整个视口，被 `html { border-radius: 12px; overflow: hidden }` 自动裁剪圆角。
- `background: #0a0e14`：与 `--bg-primary`（暗色主题）一致，Vue 挂载后 `.background-layer` 同色，无闪烁。暗色是默认主题（`useTheme.ts`），覆盖最常见场景。
- `height: 88px`：logo 高度，宽度按内置比例自动计算（453.04/350 ≈ 1.294，宽约 114px）。在最小窗口尺寸 1120×720 中视觉比例合适。
- `color: #2ec4b6`：SVG 用 `fill="currentColor"` 继承此色，与 favicon/品牌色一致。
- 内联样式不依赖 styles.css 或 Vite，HTML 解析即生效。

#### 改动 2：`<div id="app">` 内注入 splash 占位层

将：
```html
<div id="app"></div>
```
改为：
```html
<div id="app">
  <div class="splash-placeholder">
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 453.04 350" fill="currentColor" aria-hidden="true">
      <path d="M0,152.01929L0,231.13193C0,267.12656,23.88369,298.75122,58.504963,308.59903L182.44873,343.85413C211.25745,352.04858,241.78033,352.04858,270.58905,343.85413L394.53281,308.59903C429.15408,298.75122,453.03775,267.12656,453.03775,231.13193L453.03775,152.01929C453.03775,131.48445,440.98279,113.76516,423.5636,105.55234L394.22815,35.020775C385.58447,14.237647,359.06622,0.0035646637,328.98392,0L317.35669,0C304.81461,0,294.64725,7.3336515,294.64725,16.380161C294.64725,25.426661,304.81461,32.760319,317.35669,32.760319L328.98392,32.760319C339.01624,32.759811,347.86035,37.507561,350.73959,44.439369L374.11868,100.67502L78.907547,100.67502L102.29823,44.439369C105.17747,37.507561,114.02158,32.759811,124.05387,32.760319L135.68109,32.760319C148.22321,32.760319,158.39055,25.426661,158.39055,16.380161C158.39055,7.3336515,148.22321,0,135.68109,0L124.05387,0C93.971581,0.0035561048,67.45327,14.237639,58.809616,35.020775L29.474157,105.55235C12.054992,113.76519,0,131.48445,0,152.01929Z" />
    </svg>
  </div>
</div>
```

**设计决策**：
- SVG path data 直接取自 `apps/desktop/public/tinadec-logo.svg`（与 `BrandLogo.vue` L26 完全一致）。
- `fill="currentColor"` 继承 `.splash-placeholder svg` 的 `color: #2ec4b6`。
- `aria-hidden="true"`：logo 是装饰性，无需屏幕阅读器朗读。
- `viewBox="0 0 453.04 350"`：保持原始比例。
- **Vue `app.mount('#app')` 会替换 `#app` 的全部子内容**，splash 占位层在 Vue 挂载时自动消失，无需手动移除。

## 加载时序（改动后）

```
1. app.whenReady() → createWindow()
2. BrowserWindow({ show: false }) 创建
3. loadURL(loadFile) 加载 index.html
4. HTML 解析 → splash 占位层进入 DOM → 内联 <style> 生效
5. <script type="module" src="/src/main.ts"> 加载
6. main.ts 执行 → import './styles.css' → html { border-radius; overflow } 生效
7. ready-to-show 触发 → win.show() → 用户看到居中 logo（splash 可见）
8. Vue 创建 app → app.mount('#app') → #app 子内容被替换 → splash 消失
9. App.vue .background-layer 渲染 --bg-primary (#0a0e14) → 背景连续无闪烁
10. RouterView 加载 HomePage → 主界面呈现
```

splash 覆盖步骤 7-8 之间的等待期（JS bundle 解析 + Vue 初始化），这正是用户感知的"后台加载主页面"阶段。

## 假设与决策

| 决策 | 选择 | 理由 |
|------|------|------|
| 实现方式 | 主窗口内 loading 占位 | 用户选择；改动最小，仅一个文件 |
| 视觉样式 | 仅静态 logo，无动画/进度条 | 用户选择极简；贴合 Ponytail 原则 |
| 背景色 | `#0a0e14`（暗色 `--bg-primary`） | 与 Vue 挂载后背景一致，无闪烁；暗色是默认主题 |
| Logo 色 | `#2ec4b6`（`--accent-brand`） | 与 favicon/品牌色一致 |
| Logo 尺寸 | 高 88px | 在最小窗口 1120×720 中视觉比例合适 |
| 切换方式 | Vue mount 自动替换（硬切） | 背景色一致，硬切不突兀；无需额外动画代码 |
| 主题适配 | 仅适配暗色（默认） | splash 在 JS 加载前显示，无法读取 localStorage 主题；暗色是默认值，覆盖最常见场景。若用户设了亮色主题，Vue 挂载时会有短暂暗→亮过渡，可接受 |
| 圆角处理 | 不额外处理 | `html { border-radius: 12px; overflow: hidden }` 自动裁剪 splash 层 |

## 验证步骤

1. **Dev 模式验证**：
   ```bash
   npm run dev
   ```
   - 启动后应先看到居中青色 logo on `#0a0e14` 背景
   - 等待几秒后 logo 消失，主界面（HomePage）呈现
   - 背景从 splash 到主界面无白屏闪烁

2. **Prod 模式验证**：
   ```bash
   npm run build
   npx electron .
   ```
   - 同样验证 splash → 主界面过渡

3. **视觉检查**：
   - logo 在窗口正中央（水平 + 垂直居中）
   - logo 颜色为青色 `#2ec4b6`
   - 背景为深色 `#0a0e14`
   - 窗口圆角正常（splash 层被裁剪，无溢出）

4. **回归检查**：
   - 主界面功能不受影响（路由、面板窗口、终端等正常）
   - `npm --prefix apps/desktop test` 测试通过（无 index.html 相关测试，预期无影响）

5. **透明窗口降级检查**：
   - 设置 `TINADEC_DISABLE_TRANSPARENCY=1` 启动，splash 仍正常显示
