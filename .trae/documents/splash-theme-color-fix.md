# 修复加载界面主题色跟随应用设置

## 摘要

修复 splash 加载界面显示错误绿色的问题。根因有二：(1) `index.html` 原生 splash 硬编码颜色，不跟随主题设置；(2) `useTheme.ts` 中 `'blue'` 主题的 `dark.accentBrand` 被错误配置为 `#39d353`（绿色），应为蓝色 `#58a6ff`（与同主题其他变量一致）。

## 当前状态分析

### Bug 1: index.html 原生 splash 硬编码颜色

[index.html](file:///c:/git/agent/TinadecOffice/apps/desktop/index.html) L8-22:
```html
<style>
  .splash-placeholder {
    background: #0a0e14; /* 硬编码暗色 */
  }
  .splash-placeholder svg {
    color: #2ec4b6; /* 硬编码青色 */
  }
</style>
```
- 在 HTML 解析时显示，此时 `main.ts` 尚未执行，CSS 变量未设置
- 无论用户选什么主题/强调色，都显示固定颜色
- `AppSplash.vue` 已正确用 `var(--accent-brand)`，但 Vue 挂载前的原生 splash 不跟随

### Bug 2: 'blue' 主题 accentBrand 配置错误

[useTheme.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/composables/useTheme.ts) L25-43:
```ts
{
  key: 'blue',
  dark: {
    accentPrimary: '#58a6ff',    // 蓝色 ✓
    accentBrand: '#39d353',      // 绿色 ✗ — 应为 #58a6ff
    textBrand: '#58a6ff',        // 蓝色 ✓
    borderInputFocus: '#58a6ff', // 蓝色 ✓
  },
  // light 配置正确，无需改动
}
```
- 默认强调色是 `'blue'`（[useTheme.ts L185](file:///c:/git/agent/TinadecOffice/apps/desktop/src/composables/useTheme.ts#L185)）
- `AppSplash.vue` 用 `var(--accent-brand)`，在默认 blue 主题下渲染为 `#39d353` 绿色
- 用户看到的"绿色"即此

### 主题应用时机（[main.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/main.ts) L13-16）
```ts
const { applyInitialTheme } = useTheme()
if (applyInitialTheme) applyInitialTheme()
app.mount('#app')
```
- `useTheme()` 在 `app.mount` 前调用，设置 `data-theme` 属性 + 通过 `style.setProperty` 覆盖 CSS 变量
- 但发生在 `<script type="module">` 执行时，比 index.html 原生 splash 显示晚
- 所以原生 splash 需要独立的早期主题应用逻辑

## 拟定改动

### 改动 1: 修复 'blue' 主题 accentBrand 配置

**文件**: [apps/desktop/src/composables/useTheme.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/composables/useTheme.ts#L32)

**改动**: 将 L32 的 `accentBrand: '#39d353'` 改为 `accentBrand: '#58a6ff'`

```ts
// 改前
dark: {
  accentPrimary: '#58a6ff',
  accentBrand: '#39d353',      // ← 绿色，bug
  textBrand: '#58a6ff',
  borderInputFocus: '#58a6ff',
  shadowFocus: '0 0 0 3px rgba(88, 166, 255, 0.3)',
},

// 改后
dark: {
  accentPrimary: '#58a6ff',
  accentBrand: '#58a6ff',      // ← 修复为蓝色，与同主题其他变量一致
  textBrand: '#58a6ff',
  borderInputFocus: '#58a6ff',
  shadowFocus: '0 0 0 3px rgba(88, 166, 255, 0.3)',
},
```

**理由**: `'blue'` 主题的 `accentPrimary`/`textBrand`/`borderInputFocus` 都是 `#58a6ff`，唯独 `accentBrand` 是 `#39d353` 绿色，明显是配置错误。`light` 配置（L37-41）的 `accentBrand: '#1f6feb'` 是合理的蓝色，无需改动。

### 改动 2: index.html 原生 splash 跟随主题

**文件**: [apps/desktop/index.html](file:///c:/git/agent/TinadecOffice/apps/desktop/index.html)

**改动**: 在 `<head>` 加内联 `<script>` 同步读取 localStorage 主题设置，设置 `data-theme` 属性 + `--accent-brand` CSS 变量；修改 splash CSS 用变量和属性选择器。

#### 2a: 新增内联脚本（在 `<style>` 之前）

```html
<script>
  // 早期主题应用 —— 让原生 splash 在 Vue 挂载前就跟随用户设置。
  // 逻辑与 useTheme.ts 的 applyTheme/applyAccentColor 一致；main.ts 执行后会幂等覆盖。
  (function () {
    var theme = localStorage.getItem('tinadec-theme') || 'dark';
    var accent = localStorage.getItem('tinadec-accent-color') || 'blue';
    if (theme === 'system') {
      theme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }
    document.documentElement.setAttribute('data-theme', theme);
    // accentBrand 颜色映射（与 useTheme.ts ACCENT_COLORS 一致）
    var brandColors = {
      blue:   { dark: '#58a6ff', light: '#1f6feb' },
      green:  { dark: '#3fb950', light: '#1a7f37' },
      purple: { dark: '#bc8cff', light: '#8250df' },
      orange: { dark: '#f0883e', light: '#bc4c00' },
      pink:   { dark: '#f778ba', light: '#bf3989' },
      red:    { dark: '#f85149', light: '#cf222e' },
      cyan:   { dark: '#56d4dd', light: '#087990' },
      yellow: { dark: '#d29922', light: '#9a6700' },
    };
    var c = (brandColors[accent] || brandColors.blue)[theme === 'dark' ? 'dark' : 'light'];
    document.documentElement.style.setProperty('--accent-brand', c);
  })();
</script>
```

**设计要点**:
- IIFE 同步执行，在 HTML 解析时立即设置 `data-theme` 和 `--accent-brand`
- 读取的 localStorage key 与 [useTheme.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/composables/useTheme.ts) L178/L185 完全一致（`tinadec-theme`、`tinadec-accent-color`）
- `system` 主题用 `matchMedia` 解析，与 [useTheme.ts L197](file:///c:/git/agent/TinadecOffice/apps/desktop/src/composables/useTheme.ts#L197) 一致
- 颜色映射表与 `ACCENT_COLORS` 的 `accentBrand` 字段一致（修复后的 blue 值 `#58a6ff`）
- `main.ts` 的 `applyInitialTheme()` 会幂等覆盖，无冲突

#### 2b: 修改 splash CSS（用变量 + 属性选择器替代硬编码）

```html
<style>
  .splash-placeholder {
    position: fixed;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    background: #0a0e14; /* 暗色默认；亮色由下方属性选择器覆盖 */
  }
  :root[data-theme="light"] .splash-placeholder {
    background: #ffffff;
  }
  .splash-placeholder svg {
    height: 88px;
    width: auto;
    color: var(--accent-brand, #2ec4b6); /* 跟随主题强调色，fallback 为青色 */
  }
</style>
```

**设计要点**:
- `background` 用属性选择器跟随 `data-theme`（暗色 `#0a0e14` / 亮色 `#ffffff`），与 [styles.css](file:///c:/git/agent/TinadecOffice/apps/desktop/src/styles.css) L116/L177 一致
- `color: var(--accent-brand, #2ec4b6)`：由内联脚本设置的 CSS 变量驱动，带 fallback 保底
- 不依赖 styles.css 加载时机（dev 模式下 CSS 注入可能晚于 splash 首次渲染）

## 假设与决策

| 决策 | 选择 | 理由 |
|------|------|------|
| 'blue' 主题 dark.accentBrand 修复值 | `#58a6ff` | 与同主题的 `accentPrimary`/`textBrand`/`borderInputFocus` 一致 |
| 原生 splash 主题应用方式 | 内联 IIFE 脚本 | HTML 解析时同步执行，早于 main.ts，确保 splash 首帧即跟随主题 |
| 颜色映射表 | 在 index.html 内联硬编码 | splash 场景无法引用 useTheme.ts 模块；DRY 妥协，但值与 ACCENT_COLORS 一致，变更时需同步 |
| splash 背景色实现 | 属性选择器 `:root[data-theme="light"]` | 不依赖 CSS 变量加载时机，最可靠 |
| AppSplash.vue | 不改 | 已正确用 `var(--accent-brand)`，bug 修复后自动显示正确颜色 |
| useTheme.test.ts | 不改 | 无现有测试覆盖 accentBrand 具体值；配置修复不改变测试行为 |

## 验证步骤

1. **默认主题验证（blue/dark）**:
   ```bash
   npm run dev
   ```
   - splash 应显示蓝色 logo（`#58a6ff`），不是绿色
   - 原生 splash → AppSplash 切换无颜色跳变

2. **切换强调色验证**:
   - 在设置页切换到 green/purple/orange 等强调色
   - 刷新应用，splash logo 颜色应跟随设置

3. **亮色主题验证**:
   - 在设置页切换到 light 主题
   - 刷新应用，splash 背景应为白色 `#ffffff`，logo 颜色跟随强调色

4. **system 主题验证**:
   - 设置为 system 主题
   - 修改系统深浅色模式
   - splash 应跟随系统主题

5. **回归测试**:
   ```bash
   npm --prefix apps/desktop test
   ```
   - 全部通过（118 tests）
