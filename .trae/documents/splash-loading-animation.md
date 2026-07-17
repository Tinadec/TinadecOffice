# Splash Loading Animation Improvement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 TinadecOffice 桌面端正式加入 splash 加载动画：logo 呼吸脉动 + 退出时往上飞出窗口，主界面（浮岛式）由下往上平滑滑入，全程遵守 `prefers-reduced-motion`。

**Architecture:** 复用现有三段式 splash 链路（HTML 占位 → AppSplash.vue → 健康检查门控）。仅在渲染层增加 CSS 动画与 Vue `<Transition>` 协调：AppSplash 内部加 logo 呼吸动画；App.vue 用两个并行 `<Transition>`（splash-exit + main-rise）实现「logo 向上飞出 + 主界面由下往上滑入」的协调过渡；background-layer 保持原位不动（受 `transform: none !important` 保护，且必须放在任何 transformed ancestor 之外）。

**Tech Stack:** Vue 3 `<script setup>` + `<Transition>` / CSS keyframes + `cubic-bezier` 缓动 / Tailwind v4 CSS 变量（`--bg-primary`、`--accent-brand`）/ `prefers-reduced-motion` 媒体查询。

---

## Current State Analysis（来自 Phase 1 探索）

### 当前 splash 实现摘要

| 维度 | 现状 |
|---|---|
| Electron 主窗口 | `apps/desktop/electron/main.cjs` `createWindow()`：`frame:false`、`transparent:true`（默认）、`show:false`、`ready-to-show` 触发 `win.show()`。**没有独立 splash 窗口**。 |
| HTML 占位 splash | `apps/desktop/index.html` 第 33–60 行：`.splash-placeholder` 居中显示内联 SVG logo（88px，`fill="currentColor"`，颜色 = `--accent-brand`）。**无动画**。Vue 挂载 `#app` 时自动替换。 |
| Vue splash 组件 | `apps/desktop/src/components/AppSplash.vue`：`position:fixed; inset:0; background:var(--bg-primary); z-index:9999`，居中显示与 HTML 占位**完全相同**的 SVG path。**无动画**、无 spinner、无 keyframes。 |
| 切换机制 | `apps/desktop/src/App.vue` 第 58 行 `<AppSplash v-if="connectionState === 'connecting'" />` 与第 61 行 `<template v-else>` 主 UI 瞬时切换。**没有 `<Transition>` 包裹**。 |
| 连接状态机 | `apps/desktop/src/composables/useConnection.ts`：`'connecting' | 'connected' | 'timeout'`，30s 超时兜底放行。 |
| Logo 资产 | 同一段 SVG path（viewBox `0 0 453.04 350`）在 4 处复用：`public/tinadec-logo.svg`、`index.html` 内联、`AppSplash.vue` 内联、`src/components/BrandLogo.vue`。 |
| 现有动画约定 | 全局 `styles.css`：`@keyframes pulse`、`page-slide-left/right`（enter 0.28s `cubic-bezier(0.16, 1, 0.3, 1)`、leave 0.2s `cubic-bezier(0.4, 0, 1, 1)`）、`.background-layer` 强制 `transform:none !important; transition:none !important; animation:none !important;`。`settings.css` 已有 `prefers-reduced-motion` 禁用动画的先例。 |
| Web 设计准则约束 | 仅动画 `transform`/`opacity`；显式列举过渡属性（禁 `transition: all`）；必须 `prefers-reduced-motion` 兜底；SVG transform 用 `<g>` 包裹 + `transform-box: fill-box; transform-origin: center`。 |

### 关键约束（来自代码注释）

`App.vue` 第 9–20 行明确说明：background-layer 必须永远在 `<Transition>` 之外，因为 transformed ancestor 会让 `position: fixed` 退化为 `position: absolute`，导致背景随页面滑动。**主界面入场动画绝不能包裹 background-layer**。

---

## Proposed Changes

### 动画设计总览

```
t=0          HTML splash 占位显示（index.html 内联 SVG，无动画 → 加 200ms fade-in）
             ↓ Vue 挂载 #app，HTML splash 被替换
             AppSplash 接管（视觉无缝：仍为居中 logo + --bg-primary 背景）
             ↓ logo 进入「呼吸脉动」idle 动画（2.4s ease-in-out infinite，scale 1↔1.04 + opacity 0.9↔1）
             ↓ useConnection 每 1.5s 轮询 /api/v1/health
t=connected  connectionState='connected'
             ↓ 两个并行 <Transition> 同时触发：
               ① splash-exit-leave：logo translateY(0 → -100vh) + 容器 opacity 1→0（600ms cubic-bezier(0.4, 0, 0.2, 1)）
               ② main-rise-enter：主内容 translateY(60px → 0) + opacity 0→1（500ms cubic-bezier(0.16, 1, 0.3, 1)）
             background-layer 瞬时显示（无动画，z-index 在 splash 之下）
t=+600ms     splash 完全卸载，主内容就位
t=30s 超时   同样的过渡（timeout 也触发 v-if 翻转）
```

**关键决策**：
1. **AppSplash 不加入场动画**——避免与 HTML splash 占位交接时闪现。仅加 idle 呼吸 + exit 飞出。
2. **HTML splash 加 200ms fade-in**——首屏油漆感更柔和。
3. **logo 飞出用 translateY(-100vh)**——明确"移出窗口"语义，远大于 logo 自身高度（88px），视觉上"飞向天空"。
4. **主界面仅包裹 RouterView 内容**——background-layer 保留在 `<Transition>` 之外，永远不动。
5. **`appear` 修饰符**——确保首次进入主 UI 时也触发 main-rise 动画（不只是后续切换）。
6. **缓动曲线复用现有项目约定**——enter 用 `cubic-bezier(0.16, 1, 0.3, 1)`，leave 用 `cubic-bezier(0.4, 0, 0.2, 1)`。

### 文件清单

| 文件 | 改动类型 | 职责 |
|---|---|---|
| `apps/desktop/src/components/AppSplash.vue` | 修改 | 模板加内层 `.app-splash__logo` 包裹 SVG；scoped CSS 加 `@keyframes splash-breathe` 呼吸动画 |
| `apps/desktop/src/App.vue` | 修改 | 模板：用 `<Transition name="splash-exit">` 包裹 `<AppSplash>`；用 `<Transition name="main-rise" appear>` 包裹新增的 `.main-content` 容器（仅含 RouterView）；background-layer 移出 `<template v-else>`，改用 `v-if="!isConnecting"` 独立渲染 |
| `apps/desktop/src/styles.css` | 修改 | 全局加 `.splash-exit-leave-active` / `-leave-to` / `.main-rise-enter-active` / `-enter-from` / `-enter-to` 过渡类；加 `@media (prefers-reduced-motion: reduce)` 兜底块禁用 transform |
| `apps/desktop/index.html` | 修改 | `.splash-placeholder` 加 `animation: splash-fade-in 200ms ease-out`；`<style>` 内加 `@keyframes splash-fade-in` |

---

## Assumptions & Decisions

1. **"浮岛式设计"** 理解为现有主 UI 已具备的视觉风格（面板带 `--shadow-panel`/`--shadow-elevated`、与 background-layer 分离），本次仅做**入场动画**，不重新设计主 UI 视觉样式。如需新增浮岛视觉风格，应另起 plan。
2. **"logo 往上移出窗口"** = logo 在 splash 退出阶段 translateY 负方向移动至视口外（`-100vh`），非关闭窗口。
3. **测试策略**：UI 动画任务以**视觉验证 + DevTools Rendering 面板模拟 `prefers-reduced-motion`** 为主；辅以现有 `npm --prefix apps/desktop test` 确保无回归。不为本任务新增自动化测试（动画时序难自动化断言）。
4. **时序约定**：splash-exit 600ms、main-rise 500ms，并行触发；总过渡时长 ≈ 600ms。
5. **背景层不变**：严格遵守 `App.vue` 注释要求，background-layer 永远在 transformed ancestor 之外。
6. **CRLF/LF**：当前仓库 `settingsCssContract.test.ts` 已知存在 CRLF/LF 不匹配（来自 memory topics 2026-07-17），不在本任务范围内，不修复。

---

## Task 1: 为 AppSplash.vue 添加 logo 呼吸动画

**Files:**
- Modify: `apps/desktop/src/components/AppSplash.vue`（全文重写 `<template>` 与 `<style>`）

**为什么**：当前 splash 完全静态，用户要求"正式加入动画"。呼吸脉动是项目里已有的 loading 视觉语言（`@keyframes pulse`、`agent-pulse` 等），复用此约定让 splash "活起来"，但不喧宾夺主。

- [ ] **Step 1: 修改 AppSplash.vue 模板，给 SVG 加内层包裹**

修改 `apps/desktop/src/components/AppSplash.vue` 第 14–20 行的 `<template>` 块，将 `<svg>` 用 `<div class="app-splash__logo">` 包裹（便于独立施加 transform，避免与容器 fade 冲突）：

```vue
<template>
  <div class="app-splash">
    <div class="app-splash__logo">
      <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 453.04 350" fill="currentColor" aria-hidden="true">
        <path d="M0,152.01929L0,231.13193C0,267.12656,23.88369,298.75122,58.504963,308.59903L182.44873,343.85413C211.25745,352.04858,241.78033,352.04858,270.58905,343.85413L394.53281,308.59903C429.15408,298.75122,453.03775,267.12656,453.03775,231.13193L453.03775,152.01929C453.03775,131.48445,440.98279,113.76516,423.5636,105.55234L394.22815,35.020775C385.58447,14.237647,359.06622,0.0035646637,328.98392,0L317.35669,0C304.81461,0,294.64725,7.3336515,294.64725,16.380161C294.64725,25.426661,304.81461,32.760319,317.35669,32.760319L328.98392,32.760319C339.01624,32.759811,347.86035,37.507561,350.73959,44.439369L374.11868,100.67502L78.907547,100.67502L102.29823,44.439369C105.17747,37.507561,114.02158,32.759811,124.05387,32.760319L135.68109,32.760319C148.22321,32.760319,158.39055,25.426661,158.39055,16.380161C158.39055,7.3336515,148.22321,0,135.68109,0L124.05387,0C93.971581,0.0035561048,67.45327,14.237639,58.809616,35.020775L29.474157,105.55235C12.054992,113.76519,0,131.48445,0,152.01929Z" />
      </svg>
    </div>
  </div>
</template>
```

- [ ] **Step 2: 重写 AppSplash.vue 的 `<style scoped>` 块，加呼吸动画**

替换 `apps/desktop/src/components/AppSplash.vue` 第 22–36 行的 `<style scoped>` 块为：

```vue
<style scoped>
.app-splash {
  position: fixed;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--bg-primary);
  z-index: 9999;
}
.app-splash__logo {
  /* 内层包裹：让 logo 独立接受 transform，
     与外层容器的 fade 过渡不互相干扰。 */
  color: var(--accent-brand);
  animation: splash-breathe 2.4s ease-in-out infinite;
  will-change: transform, opacity;
}
.app-splash__logo svg {
  height: 88px;
  width: auto;
  display: block;
}
@keyframes splash-breathe {
  0%, 100% {
    transform: scale(1);
    opacity: 1;
  }
  50% {
    transform: scale(1.04);
    opacity: 0.9;
  }
}
</style>
```

**说明**：
- `color: var(--accent-brand)` 从 `.app-splash svg` 移到 `.app-splash__logo`，SVG `fill="currentColor"` 仍生效。
- `will-change: transform, opacity` 提示合成器，符合 Web Interface Guidelines「仅动画 transform/opacity」要求。
- `infinite` 循环 + `ease-in-out` 让脉动自然。
- 2.4s 节奏比项目里 1.5s 的 `pulse` 更舒缓，适合 splash 等待场景。

- [ ] **Step 3: 启动 dev 模式，肉眼验证 splash 呼吸动画**

运行：
```bash
npm --prefix apps/desktop run dev
```

预期：
- 应用启动后看到居中 Tinadec logo。
- logo 以 2.4s 节奏轻微放大缩小 + 透明度脉动。
- 颜色跟随主题强调色（默认蓝/teal）。
- 背景为 `--bg-primary`（暗色 `#0a0e14`）。

- [ ] **Step 4: 暂不提交，继续 Task 2**

---

## Task 2: 在 App.vue 中包裹 splash-exit Transition

**Files:**
- Modify: `apps/desktop/src/App.vue`（第 55–61 行模板结构）

**为什么**：当前 `<AppSplash v-if="...">` 瞬时卸载，没有过渡。用户要求"logo 往上移出窗口"，必须用 `<Transition>` 的 leave 钩子触发 `translateY(-100vh)`。Transition CSS 类放在全局 `styles.css`（Task 3 一起加），本任务只改 App.vue 模板。

- [ ] **Step 1: 修改 App.vue 模板，用 `<Transition name="splash-exit">` 包裹 AppSplash**

修改 `apps/desktop/src/App.vue` 第 55–61 行。把：

```vue
<template>
  <!-- Splash: shown until backend connects or 30s timeout.
       Visual matches index.html native splash for seamless transition. -->
  <AppSplash v-if="connectionState === 'connecting'" />

  <!-- Main UI: rendered once connection succeeds or timeout falls through. -->
  <template v-else>
```

替换为：

```vue
<template>
  <!-- Splash: shown until backend connects or 30s timeout.
       Visual matches index.html native splash for seamless transition.
       splash-exit Transition: logo slides up out of window + container fades. -->
  <Transition name="splash-exit">
    <AppSplash v-if="connectionState === 'connecting'" />
  </Transition>

  <!-- Main UI: rendered once connection succeeds or timeout falls through. -->
  <template v-else>
```

- [ ] **Step 2: 暂不验证（CSS 类尚未添加，此时切换会瞬时跳变）。继续 Task 3**

---

## Task 3: 在 styles.css 添加 splash-exit 过渡类 + prefers-reduced-motion 兜底

**Files:**
- Modify: `apps/desktop/src/styles.css`（在文件末尾追加新块）

**为什么**：Vue `<Transition name="splash-exit">` 会给离开中的 `<AppSplash>` 根元素（`.app-splash`）添加 `.splash-exit-leave-active` / `.splash-exit-leave-to` 类。这些类必须放在全局样式（非 scoped），因为 Transition 由父组件 `App.vue` 触发。

- [ ] **Step 1: 在 styles.css 末尾追加 splash-exit 过渡类**

在 `apps/desktop/src/styles.css` 文件**末尾**追加：

```css

/* ============================================================
   Splash → Main UI transition (splash-exit + main-rise)
   ============================================================
   - splash-exit: logo translateY(-100vh) + container opacity fade
   - main-rise:   main content translateY(60px → 0) + opacity fade
   - background-layer is intentionally NOT wrapped (stays static).
   - Honors prefers-reduced-motion (see bottom of this block).
   ============================================================ */

.splash-exit-leave-active {
  /* Explicit properties — never `transition: all` (Web Interface Guidelines). */
  transition:
    transform 600ms cubic-bezier(0.4, 0, 0.2, 1),
    opacity 400ms ease-out;
}
.splash-exit-leave-to {
  /* Move the entire splash up by 100vh so the logo clearly exits the window.
     -100vh >> logo height (88px), giving a clean "fly out the top" feel. */
  transform: translateY(-100vh);
  opacity: 0;
}

.main-rise-enter-active {
  transition:
    transform 500ms cubic-bezier(0.16, 1, 0.3, 1),
    opacity 500ms ease-out;
}
.main-rise-enter-from {
  transform: translateY(60px);
  opacity: 0;
}
.main-rise-enter-to {
  transform: translateY(0);
  opacity: 1;
}

/* Reduced motion: keep the transition functional but disable transforms.
   Splash still fades out, main UI still fades in — just no slide. */
@media (prefers-reduced-motion: reduce) {
  .splash-exit-leave-active {
    transition: opacity 200ms ease-out;
  }
  .splash-exit-leave-to {
    transform: none;
    opacity: 0;
  }
  .main-rise-enter-active {
    transition: opacity 200ms ease-out;
  }
  .main-rise-enter-from,
  .main-rise-enter-to {
    transform: none;
  }
}
```

**说明**：
- `transform-origin` 不显式设置（默认 `50% 50%`，对纯 translateY 平移无影响）。
- `will-change` 已在 AppSplash.vue scoped 样式里设置；这里 Transition 类不加 `will-change`，避免 leave 结束后残留合成层。
- `prefers-reduced-motion` 仅禁用 `transform`，保留 `opacity` 过渡（不出现硬切）。

- [ ] **Step 2: 启动 dev 模式，验证 splash 退出时 logo 向上飞出**

运行：
```bash
npm --prefix apps/desktop run dev
```

预期：
- splash 显示，logo 呼吸（来自 Task 1）。
- 当 Gateway 健康检查通过（或手动启动 Gateway 后），splash 开始离开：
  - logo 向上飞出窗口顶部（约 600ms）。
  - splash 容器整体淡出（约 400ms）。
- 主 UI 此时**还是瞬时出现**（main-rise Transition 尚未在 App.vue 中包裹）——这是 Task 4 的工作。
- 任务暂时只看到 splash 退出动画，主 UI 仍瞬时渲染。

- [ ] **Step 3: 验证 prefers-reduced-motion 兜底**

在 DevTools (`Ctrl+Shift+I`) → Rendering 面板 → 勾选 `Emulate CSS media feature prefers-reduced-motion: reduce`。重新加载窗口（`Ctrl+R`），等待 splash 退出。

预期：
- splash 不再向上滑动，仅淡出（200ms）。
- 无 transform 位移。

- [ ] **Step 4: 暂不提交，继续 Task 4**

---

## Task 4: 在 App.vue 中包裹 main-rise Transition

**Files:**
- Modify: `apps/desktop/src/App.vue`（第 60–111 行模板结构）

**为什么**：用户要求"浮岛式设计的主界面由下往上平滑移动"。主界面 = `RouterView` 内容（sidebar、chat 等，已带 `--shadow-panel` 浮岛视觉）。必须用 `<Transition name="main-rise" appear>` 包裹，让首次进入主 UI 时触发 `translateY(60px → 0)`。

**关键约束**：`background-layer` 必须保留在所有 `<Transition>` 之外（受 `transform: none !important` 保护且 transformed ancestor 会破坏其 `position: fixed`）。所以要把 `background-layer` 从 `<template v-else>` 内部移出来，独立用 `v-if="!isConnecting"` 渲染。

- [ ] **Step 1: 在 App.vue `<script setup>` 中添加 isConnecting 计算属性**

修改 `apps/desktop/src/App.vue` 第 1–7 行的 import 与第 26–29 行的 setup 块。把：

```vue
<script setup lang="ts">
import { RouterView } from 'vue-router'
import { ref, watch, onMounted } from 'vue'
import router from './router'
import { useBackground } from '@/composables/useBackground'
import { useConnection } from '@/composables/useConnection'
import AppSplash from '@/components/AppSplash.vue'
```

改为（增加 `computed` 导入）：

```vue
<script setup lang="ts">
import { RouterView } from 'vue-router'
import { ref, computed, watch, onMounted } from 'vue'
import router from './router'
import { useBackground } from '@/composables/useBackground'
import { useConnection } from '@/composables/useConnection'
import AppSplash from '@/components/AppSplash.vue'
```

然后修改第 26–29 行的连接 setup 块，把：

```vue
// ---- Backend connection gating ----
// Splash stays visible until backend connects or 30s timeout.
const { connectionState, start: startConnection } = useConnection()
onMounted(() => {
  startConnection()
})
```

改为（增加 `isConnecting` computed）：

```vue
// ---- Backend connection gating ----
// Splash stays visible until backend connects or 30s timeout.
const { connectionState, start: startConnection } = useConnection()
const isConnecting = computed(() => connectionState.value === 'connecting')
onMounted(() => {
  startConnection()
})
```

- [ ] **Step 2: 重写 App.vue 模板，分离 background-layer 与 main-content**

修改 `apps/desktop/src/App.vue` 第 55–111 行的整个 `<template>` 块。把：

```vue
<template>
  <!-- Splash: shown until backend connects or 30s timeout.
       Visual matches index.html native splash for seamless transition.
       splash-exit Transition: logo slides up out of window + container fades. -->
  <Transition name="splash-exit">
    <AppSplash v-if="connectionState === 'connecting'" />
  </Transition>

  <!-- Main UI: rendered once connection succeeds or timeout falls through. -->
  <template v-else>
    <!-- Background Layer — ALWAYS rendered, outside <Transition>, never moves.
         When type === 'none' it shows the theme's --bg-primary colour.
         This div is the stable, static foundation of the entire window. -->
    <div class="background-layer" :class="{ 'background-layer--none': backgroundSettings.type === 'none' }">
      <!-- Image Background -->
      <div
        v-if="backgroundSettings.type === 'image'"
        class="background-image"
        :style="{
          backgroundImage: backgroundSettings.source ? `url('${backgroundSettings.source}')` : 'none',
          backgroundSize: backgroundSettings.size,
          backgroundPosition: backgroundSettings.position,
          backgroundRepeat: backgroundSettings.repeat,
          opacity: backgroundSettings.opacity / 100,
          filter: backgroundSettings.blur > 0 ? `blur(${backgroundSettings.blur}px)` : 'none',
        }"
      />
      <!-- Video Background -->
      <video
        v-else-if="backgroundSettings.type === 'video' && backgroundSettings.source"
        class="background-video"
        :src="backgroundSettings.source"
        autoplay
        loop
        muted
        :style="{
          opacity: backgroundSettings.opacity / 100,
          filter: backgroundSettings.blur > 0 ? `blur(${backgroundSettings.blur}px)` : 'none',
        }"
      />
      <!-- HTML Background -->
      <div
        v-else-if="backgroundSettings.type === 'html' && backgroundSettings.source"
        class="background-html"
        v-html="backgroundSettings.source"
        :style="{
          opacity: backgroundSettings.opacity / 100,
          filter: backgroundSettings.blur > 0 ? `blur(${backgroundSettings.blur}px)` : 'none',
        }"
      />
      <!-- When type === 'none', the layer is empty but still has --bg-primary
           from .background-layer--none CSS class. -->
    </div>

    <RouterView v-slot="{ Component }">
      <Transition :name="transitionName" mode="out-in">
        <component :is="Component" />
      </Transition>
    </RouterView>
  </template>
</template>
```

替换为：

```vue
<template>
  <!-- Splash: shown until backend connects or 30s timeout.
       Visual matches index.html native splash for seamless transition.
       splash-exit Transition: logo slides up out of window + container fades. -->
  <Transition name="splash-exit">
    <AppSplash v-if="isConnecting" />
  </Transition>

  <!-- Background Layer — rendered as soon as splash dismisses.
       INTENTIONALLY OUTSIDE any <Transition> / transformed ancestor:
       CSS position:fixed degrades to absolute inside a transformed parent,
       which would make the background slide with the page (see comment below).
       This div is the stable, static foundation of the entire window. -->
  <div
    v-if="!isConnecting"
    class="background-layer"
    :class="{ 'background-layer--none': backgroundSettings.type === 'none' }"
  >
    <!-- Image Background -->
    <div
      v-if="backgroundSettings.type === 'image'"
      class="background-image"
      :style="{
        backgroundImage: backgroundSettings.source ? `url('${backgroundSettings.source}')` : 'none',
        backgroundSize: backgroundSettings.size,
        backgroundPosition: backgroundSettings.position,
        backgroundRepeat: backgroundSettings.repeat,
        opacity: backgroundSettings.opacity / 100,
        filter: backgroundSettings.blur > 0 ? `blur(${backgroundSettings.blur}px)` : 'none',
      }"
    />
    <!-- Video Background -->
    <video
      v-else-if="backgroundSettings.type === 'video' && backgroundSettings.source"
      class="background-video"
      :src="backgroundSettings.source"
      autoplay
      loop
      muted
      :style="{
        opacity: backgroundSettings.opacity / 100,
        filter: backgroundSettings.blur > 0 ? `blur(${backgroundSettings.blur}px)` : 'none',
      }"
    />
    <!-- HTML Background -->
    <div
      v-else-if="backgroundSettings.type === 'html' && backgroundSettings.source"
      class="background-html"
      v-html="backgroundSettings.source"
      :style="{
        opacity: backgroundSettings.opacity / 100,
        filter: backgroundSettings.blur > 0 ? `blur(${backgroundSettings.blur}px)` : 'none',
      }"
    />
    <!-- When type === 'none', the layer is empty but still has --bg-primary
         from .background-layer--none CSS class. -->
  </div>

  <!-- Main content shell — wrapped in <Transition name="main-rise" appear>
       so the floating-island UI slides up from below on first render.
       background-layer is deliberately outside this wrapper (see above). -->
  <Transition name="main-rise" appear>
    <div v-if="!isConnecting" class="main-content">
      <RouterView v-slot="{ Component }">
        <Transition :name="transitionName" mode="out-in">
          <component :is="Component" />
        </Transition>
      </RouterView>
    </div>
  </Transition>
</template>
```

**说明**：
- `<template v-else>` 被拆成两个独立的 `v-if="!isConnecting"` 块（background-layer + main-content），两者同时挂载/卸载，但只有 main-content 被 Transition 包裹。
- 内层 `<RouterView>` 的 `<Transition :name="transitionName" mode="out-in">` 保持不变——它处理**路由切换**过渡；外层 `main-rise` 只在 splash→main 首次切换时触发一次。两者嵌套不冲突。
- `appear` 修饰符确保首次渲染也走 enter 动画（虽然这里是 v-if 控制而非路由首屏，但加上更稳妥）。
- 注意：原模板里 `<template v-else>` 的所有子节点现在都被显式 `v-if` 替代，Vue 不会抱怨——因为外层 `<Transition>` 内部就是 `<div v-if="!isConnecting">` 单根元素。

- [ ] **Step 3: 验证主界面由下往上滑入**

运行：
```bash
npm --prefix apps/desktop run dev
```

预期：
- splash 显示，logo 呼吸。
- splash 退出时（健康检查通过或 30s 超时）：
  - logo 向上飞出窗口顶（600ms）。
  - splash 容器淡出（400ms）。
  - **主 UI 内容（sidebar + chat 等）从下方 60px 处平滑滑入到位**（500ms）。
  - background-layer 瞬时显示（无动画），位于主内容之下、splash 之上（splash z-index 9999 仍在最上层直到淡出）。
- 整个过渡感觉连贯："logo 飞向天空，主界面从地面升起"。

- [ ] **Step 4: 验证路由切换过渡未被破坏**

应用启动后，点击 sidebar 在 Home ↔ Settings ↔ Market 间切换。

预期：
- 路由切换仍有原有的 `page-slide-left` / `page-slide-right` 横向滑动过渡（0.28s）。
- main-rise 动画**不应**在路由切换时重复触发（它只在 splash→main 首次进入时触发一次）。

- [ ] **Step 5: 暂不提交，继续 Task 5**

---

## Task 5: 为 index.html 的 HTML splash 占位添加 fade-in

**Files:**
- Modify: `apps/desktop/index.html`（第 33–51 行 `<style>` 块）

**为什么**：Electron 首次加载 `index.html` 到 Vue 挂载之间有一段短暂时间（通常 100–300ms），此时显示 HTML splash 占位。当前占位瞬时出现，加 200ms fade-in 让首次油漆更柔和。Vue 挂载后会替换占位，AppSplash 接管并播放呼吸动画——视觉上从"淡入"过渡到"呼吸"，自然衔接。

- [ ] **Step 1: 修改 index.html 第 33–51 行的 `<style>` 块**

修改 `apps/desktop/index.html` 第 33–51 行。把：

```html
    <style>
      /* Splash 占位层 —— Vue 挂载 #app 时自动替换此内容 */
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

替换为：

```html
    <style>
      /* Splash 占位层 —— Vue 挂载 #app 时自动替换此内容 */
      .splash-placeholder {
        position: fixed;
        inset: 0;
        display: flex;
        align-items: center;
        justify-content: center;
        background: #0a0e14; /* 暗色默认；亮色由下方属性选择器覆盖 */
        animation: splash-fade-in 200ms ease-out;
      }
      :root[data-theme="light"] .splash-placeholder {
        background: #ffffff;
      }
      .splash-placeholder svg {
        height: 88px;
        width: auto;
        color: var(--accent-brand, #2ec4b6); /* 跟随主题强调色，fallback 为青色 */
      }
      @keyframes splash-fade-in {
        from { opacity: 0; }
        to   { opacity: 1; }
      }
      @media (prefers-reduced-motion: reduce) {
        .splash-placeholder {
          animation: none;
        }
      }
    </style>
```

**说明**：
- 仅 fade-in（opacity），无 transform——符合 Web Interface Guidelines「仅动画 transform/opacity」。
- `prefers-reduced-motion: reduce` 时直接 `animation: none`，瞬时显示。
- 200ms 与项目里 0.2s leave 过渡约定一致。
- HTML splash 与 Vue AppSplash 视觉完全一致（同 SVG path、同背景色、同 logo 颜色），所以 fade-in 结束后 AppSplash 接管时无视觉跳变。

- [ ] **Step 2: 重启 dev 模式（或硬刷新），验证 HTML splash 淡入**

完全退出 Electron（不仅是 `Ctrl+R`，要杀进程后重启），让 `index.html` 重新解析。

运行：
```bash
npm --prefix apps/desktop run dev
```

预期：
- 窗口出现的瞬间，HTML splash 在 200ms 内从透明淡入到完全显示。
- 随后 Vue 挂载，AppSplash 接管，logo 开始呼吸（Task 1）。
- 整个启动序列：淡入 → 呼吸 → （连接成功）logo 飞出 + 主界面升起。

- [ ] **Step 3: 暂不提交，继续 Task 6**

---

## Task 6: 综合验证 + Web Interface Guidelines 自检 + 提交

**Files:**
- Read-only verification across all modified files.

- [ ] **Step 1: 完整启动序列验证**

完全退出 Electron 后重启：

```bash
npm --prefix apps/desktop run dev
```

观察完整序列：
1. **t≈0**：窗口出现，HTML splash 在 200ms 内淡入（logo 居中，背景 `--bg-primary`）。
2. **t≈100–300ms**：Vue 挂载，AppSplash 接管（视觉无跳变），logo 开始 2.4s 呼吸脉动。
3. **t=连接成功**（启动 Gateway 或等 30s 超时）：
   - logo 向上飞出窗口顶部（600ms `cubic-bezier(0.4, 0, 0.2, 1)`）。
   - splash 容器淡出（400ms ease-out）。
   - 主 UI 内容从下方 60px 平滑滑入到位（500ms `cubic-bezier(0.16, 1, 0.3, 1)`）。
   - background-layer 瞬时显示，保持静止。
4. **路由切换**：Home ↔ Settings ↔ Market，原有 `page-slide-left/right` 横向过渡保留，main-rise 不重复触发。

- [ ] **Step 2: DevTools 模拟 prefers-reduced-motion 验证**

在 Electron 窗口按 `Ctrl+Shift+I` 打开 DevTools → Rendering 面板 → 勾选 `Emulate CSS media feature prefers-reduced-motion: reduce`。硬刷新 `Ctrl+R`。

预期：
- HTML splash：无 fade-in，瞬时显示。
- AppSplash logo：无呼吸动画，静态显示。
- splash 退出：仅 200ms opacity 淡出，无 translateY。
- 主 UI 入场：仅 200ms opacity 淡入，无 translateY。
- 整个序列仍可工作，无功能缺失。

- [ ] **Step 3: 运行现有测试套件，确保无回归**

运行：
```bash
npm --prefix apps/desktop test
```

预期：所有现有测试通过。重点关注是否有 CSS 契约测试（如 `settingsCssContract.test.ts`）引用了被修改的样式块——本任务仅在 `styles.css` 末尾追加新块，未改动既有规则，应无影响。如失败，根据报错定位并修复。

- [ ] **Step 4: Web Interface Guidelines 自检（人工）**

按 fetched guidelines 逐项核对修改的文件：

| 规则 | 检查点 | 结果 |
|---|---|---|
| Animation | `prefers-reduced-motion` 兜底 | ✅ Task 3 + Task 5 均加 |
| Animation | 仅动画 `transform` / `opacity` | ✅ 未动画其他属性 |
| Animation | 显式列举 transition 属性，非 `transition: all` | ✅ Task 3 CSS 显式列 transform/opacity |
| Animation | 动画可中断 | ✅ `<Transition>` 默认可被打断 |
| Accessibility | 装饰性 SVG `aria-hidden="true"` | ✅ AppSplash.vue 已有（未改动） |
| Anti-patterns | 无 `transition: all` | ✅ |
| Anti-patterns | 无 `outline-none` 无替代 | ✅ 未涉及 |
| Anti-patterns | 无 `<div onClick>` | ✅ 仅模板结构改动 |
| Images | `<img>` 有 width/height | ✅ 用 SVG viewBox，无 `<img>` |

- [ ] **Step 5: 用 web-design-guidelines skill 复核（可选）**

如需更严格审查，可调用 web-design-guidelines skill 复核以下文件：
- `apps/desktop/src/components/AppSplash.vue`
- `apps/desktop/src/App.vue`
- `apps/desktop/src/styles.css`（仅新增块）
- `apps/desktop/index.html`

如有 findings，逐条修复后重新执行 Step 1–3。

- [ ] **Step 6: 提交所有改动**

运行：
```bash
cd c:\git\agent\TinadecOffice
git status
git diff --stat
```

确认改动文件清单：
- `apps/desktop/src/components/AppSplash.vue`
- `apps/desktop/src/App.vue`
- `apps/desktop/src/styles.css`
- `apps/desktop/index.html`

提交：
```bash
git add apps/desktop/src/components/AppSplash.vue apps/desktop/src/App.vue apps/desktop/src/styles.css apps/desktop/index.html
git commit -m "feat(desktop): polish splash loading with logo breathe + slide-up transition

- AppSplash: add idle logo breathing pulse (2.4s ease-in-out infinite)
- App.vue: wrap splash in <Transition name=splash-exit>, wrap main content
  in <Transition name=main-rise appear>; keep background-layer outside any
  transformed ancestor (preserves position:fixed per existing contract)
- styles.css: add splash-exit + main-rise transition classes with explicit
  transform/opacity properties (no transition:all); add prefers-reduced-motion
  fallback that disables transforms but keeps opacity fades
- index.html: add 200ms splash-fade-in for first paint; disabled under
  prefers-reduced-motion

Coordinated transition: logo flies up out of window (-100vh) while main UI
rises from below (translateY 60px -> 0), background stays static."
```

---

## Verification Summary

| 验证项 | 命令/操作 | 预期 |
|---|---|---|
| splash 呼吸 | `npm --prefix apps/desktop run dev` | logo 2.4s 脉动 |
| logo 飞出 | 同上，等连接成功 | 600ms translateY(-100vh) + 淡出 |
| 主界面升起 | 同上 | 500ms translateY(60→0) + 淡入 |
| 背景不动 | 同上 | background-layer 静止 |
| 路由切换不破坏 | 切换 Home/Settings | page-slide-left/right 保留 |
| reduced-motion | DevTools Rendering 模拟 | 仅淡入淡出，无 transform |
| 现有测试 | `npm --prefix apps/desktop test` | 全部通过 |

---

## Self-Review Checklist（writing-plans skill 要求）

1. **Spec coverage**：
   - "改进 splash 首页加载动画，正式加入动画" → Task 1（呼吸）+ Task 5（HTML fade-in）
   - "浮岛式设计的主界面由下往上平滑移动" → Task 4（main-rise Transition）
   - "logo 也往上移出窗口" → Task 2 + Task 3（splash-exit Transition + translateY(-100vh)）
   - Web Interface Guidelines 合规 → Task 3 + Task 5 + Task 6 Step 4

2. **Placeholder scan**：无 TBD/TODO/「类似 Task N」/「添加适当错误处理」等占位符。

3. **Type consistency**：
   - `isConnecting` 在 Task 4 Step 1 定义为 `computed(() => connectionState.value === 'connecting')`，在 Step 2 模板中作为 `v-if="isConnecting"` / `v-if="!isConnecting"` 使用——一致。
   - `Transition name="splash-exit"` 与 Task 3 中 `.splash-exit-leave-active` 类名一致。
   - `Transition name="main-rise"` 与 Task 3 中 `.main-rise-enter-active` / `-enter-from` / `-enter-to` 类名一致。
   - `@keyframes splash-breathe`（Task 1）与 `animation: splash-breathe 2.4s ...`（Task 1）一致。
   - `@keyframes splash-fade-in`（Task 5）与 `animation: splash-fade-in 200ms ...`（Task 5）一致。

4. **风险点**：
   - Vue `<Transition>` 嵌套（外层 main-rise + 内层 page-slide）——Vue 3 支持，且内层有 `mode="out-in"`，不冲突。
   - background-layer 从 `<template v-else>` 内移到独立 `v-if="!isConnecting"`——逻辑等价（同时挂载/卸载），仅是结构变化。
   - `appear` 修饰符——确保首次渲染走 enter 动画。
