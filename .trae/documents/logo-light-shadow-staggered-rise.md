# Logo 光影呼吸 + 主界面阶梯式升起 实现计划

## Summary

修复当前 App.vue 中未解决的 git 冲突标记（导致构建失败），恢复用户希望保留的 splash-exit + main-rise + 子窗口跳过过渡，并将之前的 logo scale 呼吸动画改为**光影变化**（drop-shadow 光晕脉动），同时把 main-rise 主界面升起改为**从左至右阶梯式**：侧边栏先升起，主面板后升起，右侧 ContextPanel 最后升起。

## Current State Analysis（来自 Phase 1 探索）

### 当前实际状态（commit `2cf5265 feat添加了logo`）

| 文件 | 当前状态 |
|---|---|
| `apps/desktop/src/App.vue` | **含未解决 git 冲突标记**（`<<<<<<< HEAD` 第 30、67 行；`>>>>>>> parent of 9ad7986` 第 33、174 行）。HEAD 侧保留 `isChildWindow` + `isConnecting`（但 `computed` 未导入，编译会失败）+ 分离的 background-layer + main-rise Transition；parent 侧是原版 `<template v-else>` + 内联 background-layer + 直接 RouterView。 |
| `apps/desktop/src/components/AppSplash.vue` | 已被回退为**纯静态**：无 `.app-splash__logo` 包裹、无 `splash-breathe` 动画；仅居中显示 SVG（`fill="currentColor"`，`color: var(--accent-brand)`，`height: 88px`）。 |
| `apps/desktop/src/styles.css` | `splash-exit-leave-*` / `main-rise-enter-*` 过渡类**已被全部删除**（grep 无匹配）。`@keyframes pulse`（1.5s）等现有动画仍保留。 |
| `apps/desktop/index.html` | `.splash-placeholder` 的 `animation: splash-fade-in` **已删除**；`data-skip-splash` 属性逻辑保留（无害但无用）。 |
| `apps/desktop/electron/debug-studio.cjs` / `panelWindow.cjs` | `?splash=0` query **仍保留**（commit 6262caa 未被回退）。 |

### 用户需求澄清（Phase 2 已确认）

1. **光影变化时机**：等待期间持续呼吸（idle 动画，类似我之前的 splash-breathe，但用光影而非 scale）。
2. **冲突处理**：保留我之前的过渡（splash-exit + main-rise + 子窗口跳过），清理冲突标记。
3. **新增要求**：主界面阶梯式升起——从左至右，侧边栏先升，然后右边主面板，实现阶梯依次升起。

### 主界面布局结构（来自 HomePage.vue 第 322–385 行）

```
<main class="shell">
  <div class="top-drag-bar" />
  <AppHeader />                    <!-- 顶部 header -->
  <section class="workspace">      <!-- flex: 1 -->
    <ChatPanel />                  <!-- 中间主面板（DOM 第 1 位） -->
    <AppSidebar />                 <!-- 侧边栏，根元素 class="sidebar"（absolute, left:8px, width:260px） -->
    <ContextPanel />               <!-- 右侧上下文面板（DOM 第 3 位） -->
  </section>
</main>
```

`.sidebar` 的 CSS 在 `styles.css` 第 1631 行定义（`position: absolute; top: 8px; left: 8px; bottom: 8px; width: 260px; border-radius: 12px; box-shadow: var(--shadow-panel)`）。

### 关键约束

1. **main-rise Transition 只触发一次**：`<Transition name="main-rise" appear>` 包裹 `<div v-if="!isConnecting">`，`isConnecting` 连接成功后永远为 false，所以 enter 钩子只在 splash→main 首次切换时触发——路由切换不会重播 staggered 升起。
2. **background-layer 必须在 `<Transition>` 之外**：保留现有分离结构（`App.vue` 注释明确说明）。
3. **Web Interface Guidelines**：仅动画 `transform` / `opacity`（光影 filter 是用户明确要求的破例）；显式列举 transition 属性；`prefers-reduced-motion` 兜底。
4. **现有 `@keyframes pulse`**（1.5s ease-in-out infinite，opacity 1↔0.4）可参考节奏，但光影效果用 filter 更合适。

## Proposed Changes

### 动画设计总览

```
t=0          HTML splash 占位显示（瞬时，无 fade-in——已被回退，本计划不恢复）
             ↓ Vue 挂载 #app
             AppSplash 接管
             ↓ logo 进入「光影呼吸」idle 动画（2.8s ease-in-out infinite）
               drop-shadow(0 0 6px brand) ↔ drop-shadow(0 0 18px brand) + opacity 0.85↔1
             ↓ useConnection 每 1.5s 轮询 /api/v1/health
t=connected  connectionState='connected' → isConnecting=false
             ↓ 两个并行 <Transition> 同时触发：
               ① splash-exit-leave：logo translateY(0 → -100vh) + 容器 opacity 1→0（600ms）
               ② main-rise-enter：.main-content 容器瞬时显示（无整体动画）
                  内部 staggered 升起：
                    - .sidebar：       delay 0ms   → translateY(40px→0) + opacity 0→1（500ms）
                    - ChatPanel 根：   delay 120ms → translateY(40px→0) + opacity 0→1（500ms）
                    - ContextPanel 根：delay 240ms → translateY(40px→0) + opacity 0→1（500ms）
                  AppHeader：          delay -60ms（同步或略早，整体感）
             background-layer 瞬时显示，保持静止
t=+740ms     全部就位
```

**关键决策**：
1. **光影用 `filter: drop-shadow`**：SVG `fill="currentColor"` 配合 `filter: drop-shadow(0 0 Npx var(--accent-brand))` 产生光晕，模糊半径 6px↔18px 脉动。
2. **配合 opacity 微调**：0.85↔1，让光影变化更明显（纯 filter 变化可能不够明显）。
3. **2.8s 节奏**：比之前 scale 版本的 2.4s 更慢，光影呼吸应更舒缓。
4. **main-rise 容器不再整体 translateY**：改为容器内子元素 staggered 升起；容器本身瞬时显示（避免与子元素动画叠加冲突）。
5. **staggered 顺序**：sidebar（左）→ ChatPanel（中）→ ContextPanel（右），符合用户"从左至右"要求。
6. **AppHeader 同步显示**：顶部 header 不参与 staggered（视觉上 header 是"框"，应先就位）。

### 文件清单

| 文件 | 改动类型 | 职责 |
|---|---|---|
| `apps/desktop/src/App.vue` | 修改 | 清理 git 冲突标记，保留 HEAD 侧；恢复 `computed` 导入 |
| `apps/desktop/src/components/AppSplash.vue` | 修改 | 加 `.app-splash__logo` 包裹 + `splash-glow` 光影呼吸动画（filter drop-shadow + opacity，无 scale） |
| `apps/desktop/src/styles.css` | 修改 | 恢复 `splash-exit-leave-*`；重写 `main-rise-enter-*` 为容器瞬时 + 子元素 staggered 升起；加 `@keyframes rise-up`；`prefers-reduced-motion` 兜底 |
| `apps/desktop/index.html` | 可选修改 | 恢复 `splash-fade-in` 200ms（非必须，HTML splash 时间极短） |

## Assumptions & Decisions

1. **光影效果形式**：`filter: drop-shadow(0 0 Npx var(--accent-brand))` 模糊半径脉动 + `opacity` 微调。不用 `box-shadow`（SVG 无 box）、不用 `text-shadow`（SVG 非 text）。
2. **不恢复 index.html 的 splash-fade-in**：HTML splash 占位时间极短（100–300ms），且用户回退时已移除；本计划聚焦 Vue splash 的光影呼吸，避免范围蔓延。如需恢复，另起任务。
3. **staggered 升起选择器**：`.main-rise-enter-active .sidebar`、`.main-rise-enter-active .workspace > *`。`ChatPanel` / `ContextPanel` 的根元素 class 需在执行时确认（预期 `.chat-panel` / `.context-panel`，但用 `.workspace > *` 通配更稳妥，按 DOM 顺序设 delay 不可行——DOM 顺序是 Chat→Sidebar→Context，与视觉左右顺序不符，所以必须用具体 class）。
4. **AppHeader 不参与 staggered**：header 是顶部"框架"，应瞬时或最先就位，不参与"从左至右升起"序列。
5. **测试策略**：视觉验证 + `npm --prefix apps/desktop test` 无回归。不新增自动化测试。
6. **git 冲突清理方向**：保留 HEAD 侧（含 `isChildWindow` / `isConnecting` / 分离 background-layer / main-rise Transition），删除 parent 侧。需补 `computed` 导入（HEAD 侧用 `isConnecting = computed(...)` 但 import 行被冲突影响）。

---

## Task 1: 清理 App.vue git 冲突标记，恢复 computed 导入

**Files:**
- Modify: `apps/desktop/src/App.vue`

**为什么**：当前 App.vue 含 6 个冲突标记行（第 30/33/67/174 行的 `<<<<<<<`/`=======`/`>>>>>>>`），无法编译。用户确认保留 HEAD 侧（我之前的过渡逻辑），删除 parent 侧。HEAD 侧用了 `isConnecting = computed(...)` 但 `computed` 未在 import 行中（被冲突影响），需补齐。

- [ ] **Step 1: 修复 import 行，补 `computed`**

修改 `apps/desktop/src/App.vue` 第 3 行。把：

```vue
import { ref, watch, onMounted } from 'vue'
```

改为：

```vue
import { ref, computed, watch, onMounted } from 'vue'
```

- [ ] **Step 2: 清理第一处冲突（script 内，第 30–33 行）**

删除第 30–33 行的冲突标记，保留 HEAD 侧 `isConnecting` 定义。把：

```vue
const { connectionState, start: startConnection } = useConnection()
<<<<<<< HEAD
const isConnecting = computed(() => !isChildWindow && connectionState.value === 'connecting')
=======
>>>>>>> parent of 9ad7986 (feat(desktop): polish splash loading with logo breathe and slide-up transition)
onMounted(() => {
```

改为：

```vue
const { connectionState, start: startConnection } = useConnection()
const isConnecting = computed(() => !isChildWindow && connectionState.value === 'connecting')
onMounted(() => {
```

- [ ] **Step 3: 清理第二处冲突（template 内，第 67–174 行），保留 HEAD 侧**

删除第 67–174 行的冲突标记与 parent 侧内容，保留 HEAD 侧的分离 background-layer + main-rise Transition 结构。把第 67–174 行整段（从 `<<<<<<< HEAD` 到 `>>>>>>> parent of 9ad7986 ...`）替换为 HEAD 侧的干净版本（不含冲突标记）：

```vue
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
       子窗口（isChildWindow）用不存在的 'no-transition' name 禁用入场动画，瞬时显示。
       background-layer is deliberately outside this wrapper (see above). -->
  <Transition :name="isChildWindow ? 'no-transition' : 'main-rise'" appear>
    <div v-if="!isConnecting" class="main-content">
      <RouterView v-slot="{ Component }">
        <Transition :name="transitionName" mode="out-in">
          <component :is="Component" />
        </Transition>
      </RouterView>
    </div>
  </Transition>
```

**说明**：保留 `<AppSplash v-if="connectionState === 'connecting'" />`（第 65 行，无 Transition 包裹）——**此处需修正**：原 HEAD 侧用 `<Transition name="splash-exit">` 包裹 AppSplash，但当前文件第 65 行是裸 `<AppSplash>`。本步骤同步恢复 splash-exit 包裹。

修正第 62–65 行，把：

```vue
<template>
  <!-- Splash: shown until backend connects or 30s timeout.
       Visual matches index.html native splash for seamless transition. -->
  <AppSplash v-if="connectionState === 'connecting'" />
```

改为：

```vue
<template>
  <!-- Splash: shown until backend connects or 30s timeout.
       Visual matches index.html native splash for seamless transition.
       splash-exit Transition: logo slides up out of window + container fades. -->
  <Transition name="splash-exit">
    <AppSplash v-if="connectionState === 'connecting'" />
  </Transition>
```

注意：`<AppSplash v-if="connectionState === 'connecting'">` 这里用 `connectionState` 而非 `isConnecting` 是有意的——splash-exit Transition 只在主窗口触发，子窗口（`isChildWindow`）的 `isConnecting` 永远 false 会让 AppSplash 不渲染，无需 `connectionState` 直接判断。但为一致性，可改为 `v-if="isConnecting"`。**决定**：改为 `v-if="isConnecting"`，与 background-layer / main-content 一致。

最终第 65 行应为：

```vue
    <AppSplash v-if="isConnecting" />
```

- [ ] **Step 4: 验证 App.vue 无冲突标记且语法正确**

运行：
```bash
npm --prefix apps/desktop run build 2>&1 | Select-Object -Last 20
```

预期：构建成功，无 "Git conflict marker" 或 "computed is not defined" 错误。若失败，检查是否有遗漏的冲突标记或 import 缺失。

- [ ] **Step 5: 暂不提交，继续 Task 2**

---

## Task 2: 在 AppSplash.vue 添加 logo 光影呼吸动画

**Files:**
- Modify: `apps/desktop/src/components/AppSplash.vue`

**为什么**：用户明确要求"logo 显示光影的变化，而不是大小的变化"。当前 AppSplash 是纯静态。用 `filter: drop-shadow` 让 SVG logo 产生光晕，模糊半径 6px↔18px 脉动 + opacity 0.85↔1，形成"光影呼吸"。2.8s 节奏比之前的 2.4s scale 版本更舒缓。

- [ ] **Step 1: 修改 AppSplash.vue 模板，给 SVG 加内层包裹**

修改 `apps/desktop/src/components/AppSplash.vue` 第 14–20 行的 `<template>` 块，将 `<svg>` 用 `<div class="app-splash__logo">` 包裹（让光影 filter 独立施加，不与容器 fade 冲突）：

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

- [ ] **Step 2: 重写 AppSplash.vue 的 `<style scoped>` 块，加光影呼吸动画**

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
  /* 内层包裹：让 logo 独立接受 filter 光影，
     与外层容器的 fade 过渡不互相干扰。
     光影呼吸：drop-shadow 模糊半径脉动 + opacity 微调，
     不使用 scale（用户明确要求光影而非大小变化）。 */
  color: var(--accent-brand);
  animation: splash-glow 2.8s ease-in-out infinite;
  will-change: filter, opacity;
}
.app-splash__logo svg {
  height: 88px;
  width: auto;
  display: block;
}
@keyframes splash-glow {
  0%, 100% {
    /* 光晕收束：模糊半径小，光晕紧贴 logo */
    filter: drop-shadow(0 0 6px var(--accent-brand));
    opacity: 0.85;
  }
  50% {
    /* 光晕扩散：模糊半径大，光晕外溢 */
    filter: drop-shadow(0 0 18px var(--accent-brand));
    opacity: 1;
  }
}
</style>
```

**说明**：
- `filter: drop-shadow` 对 SVG `<path>` 有效（不同于 `box-shadow` 对 SVG 无效）。
- `var(--accent-brand)` 让光晕颜色跟随主题强调色（8 种可选）。
- 模糊半径 6px↔18px 是明显的视觉差异，光晕"呼吸"可见。
- opacity 0.85↔1 配合，让光影变化更立体（纯 filter 变化在浅色背景下可能不够明显）。
- `will-change: filter, opacity` 提示合成器。
- **无 scale**：严格遵守用户"不是大小的变化"要求。
- 2.8s 节奏比之前 scale 版本（2.4s）更舒缓，适合光影呼吸。

- [ ] **Step 3: 验证光影呼吸动画**

运行：
```bash
npm --prefix apps/desktop run dev
```

预期：
- splash 显示，logo 居中。
- logo 周围有 `--accent-brand` 颜色的光晕，以 2.8s 节奏收束（6px）↔扩散（18px）。
- logo 透明度 0.85↔1 微弱脉动。
- logo 大小**不变**。
- 切换主题强调色（Settings），光晕颜色跟随变化。

- [ ] **Step 4: 暂不提交，继续 Task 3**

---

## Task 3: 在 styles.css 恢复并重写过渡类（splash-exit + main-rise staggered）

**Files:**
- Modify: `apps/desktop/src/styles.css`（在文件末尾追加新块）

**为什么**：当前 styles.css 的 `splash-exit-leave-*` / `main-rise-enter-*` 已被回退删除，需恢复。同时按用户要求重写 main-rise 为**容器瞬时显示 + 内部子元素 staggered 升起**（sidebar → ChatPanel → ContextPanel），而非整体 translateY。

- [ ] **Step 1: 在 styles.css 末尾追加过渡类**

在 `apps/desktop/src/styles.css` 文件**末尾**追加：

```css

/* ============================================================
   Splash → Main UI transition (splash-exit + main-rise staggered)
   ============================================================
   - splash-exit: logo translateY(-100vh) + container opacity fade
   - main-rise:   容器瞬时显示，内部子元素 staggered 升起
                  （sidebar → ChatPanel → ContextPanel，从左至右）
   - background-layer is intentionally NOT wrapped (stays static).
   - Honors prefers-reduced-motion (see bottom of this block).
   ============================================================ */

/* --- splash 退出：logo 向上飞出窗口 + 容器淡出 --- */
.splash-exit-leave-active {
  transition:
    transform 600ms cubic-bezier(0.4, 0, 0.2, 1),
    opacity 400ms ease-out;
}
.splash-exit-leave-to {
  transform: translateY(-100vh);
  opacity: 0;
}

/* --- main-rise：容器瞬时显示，内部 staggered 升起 --- */
/* 容器本身无 transition（避免与子元素动画冲突） */
.main-rise-enter-active {
  /* 无整体 transition；子元素用 animation 实现 staggered */
}
.main-rise-enter-from {
  /* 容器起始无变化，子元素各自控制起始状态 */
}
.main-rise-enter-to {
  /* 容器终态无变化 */
}

/* staggered 子元素升起动画（仅在 main-rise-enter-active 期间触发） */
@keyframes rise-up {
  from {
    transform: translateY(40px);
    opacity: 0;
  }
  to {
    transform: translateY(0);
    opacity: 1;
  }
}

/* AppHeader：顶部框架，最先就位（不参与从左至右序列，但需淡入） */
.main-rise-enter-active .app-header {
  animation: rise-up 400ms cubic-bezier(0.16, 1, 0.3, 1) both;
  animation-delay: 0ms;
}

/* 侧边栏：从左至右第 1 位，delay 0ms */
.main-rise-enter-active .sidebar {
  animation: rise-up 500ms cubic-bezier(0.16, 1, 0.3, 1) both;
  animation-delay: 80ms;
}

/* ChatPanel：中间主面板，第 2 位，delay 200ms */
/* ChatPanel 根元素 class 需确认；常见为 .chat-panel。
   执行时如不同，调整为实际 class。备选：.workspace > :nth-child(1) */
.main-rise-enter-active .chat-panel {
  animation: rise-up 500ms cubic-bezier(0.16, 1, 0.3, 1) both;
  animation-delay: 200ms;
}

/* ContextPanel：右侧面板，第 3 位，delay 320ms */
/* ContextPanel 根元素 class 需确认；常见为 .context-panel。
   执行时如不同，调整为实际 class。备选：.workspace > :nth-child(3) */
.main-rise-enter-active .context-panel {
  animation: rise-up 500ms cubic-bezier(0.16, 1, 0.3, 1) both;
  animation-delay: 320ms;
}

/* Reduced motion: 禁用 transform 与 filter，仅保留 opacity 淡入淡出 */
@media (prefers-reduced-motion: reduce) {
  .splash-exit-leave-active {
    transition: opacity 200ms ease-out;
  }
  .splash-exit-leave-to {
    transform: none;
    opacity: 0;
  }
  .main-rise-enter-active .app-header,
  .main-rise-enter-active .sidebar,
  .main-rise-enter-active .chat-panel,
  .main-rise-enter-active .context-panel {
    animation: none;
  }
}
```

**说明**：
- `.main-rise-enter-active` 容器本身无 transition——避免容器 translateY 与子元素 animation 叠加冲突。
- 子元素用 `animation: rise-up ... both` + 不同 `animation-delay` 实现 staggered。
- `both` 保留终态（forwards）+ 起始状态（backwards），避免动画前后闪烁。
- 顺序：AppHeader(0ms) → sidebar(80ms) → ChatPanel(200ms) → ContextPanel(320ms)。
  - AppHeader 略早，作为"框架"先就位。
  - sidebar→ChatPanel→ContextPanel 间隔 120ms，形成"从左至右"阶梯感。
- 总时长：320ms + 500ms = 820ms，比之前整体 main-rise(500ms) 略长，但阶梯感更强。
- `prefers-reduced-motion` 禁用所有 animation 与 transform，splash 仍淡出（200ms）。
- **ChatPanel / ContextPanel 的根 class 需在执行时确认**（plan 中标注，Task 4 验证）。

- [ ] **Step 2: 确认 ChatPanel 与 ContextPanel 的根元素 class**

运行 Grep 查找：
- `apps/desktop/src/components/ChatPanel.vue` 模板根元素的 class
- `apps/desktop/src/components/ContextPanel.vue` 模板根元素的 class
- `apps/desktop/src/components/AppHeader.vue` 模板根元素的 class

如 class 与 plan 中假设（`.chat-panel` / `.context-panel` / `.app-header`）不符，调整 Task 3 Step 1 中的 CSS 选择器为实际 class。

- [ ] **Step 3: 验证 splash-exit + main-rise staggered 过渡**

运行：
```bash
npm --prefix apps/desktop run dev
```

预期：
- splash 显示，logo 光影呼吸（Task 2）。
- 连接成功后：
  - splash 整体向上飞出（600ms translateY -100vh）+ 淡出（400ms）。
  - 主界面容器瞬时显示。
  - **AppHeader 先淡入升起**（0ms delay，400ms）。
  - **sidebar 从下方 40px 升起**（80ms delay，500ms）。
  - **ChatPanel 从下方 40px 升起**（200ms delay，500ms）。
  - **ContextPanel 从下方 40px 升起**（320ms delay，500ms）。
  - 整体感觉：从左至右阶梯式升起。
- background-layer 瞬时显示，静止不动。

- [ ] **Step 4: 验证 prefers-reduced-motion 兜底**

DevTools (`Ctrl+Shift+I`) → Rendering 面板 → 勾选 `Emulate CSS media feature prefers-reduced-motion: reduce`。硬刷新 `Ctrl+R`，等 splash 退出。

预期：
- splash logo 无光影呼吸（filter 动画被禁用——需在 AppSplash.vue 也加 reduced-motion 兜底，见 Task 4）。
- splash 退出：仅 200ms opacity 淡出，无 translateY。
- 主界面：所有子元素无 animation，瞬时显示。

- [ ] **Step 5: 暂不提交，继续 Task 4**

---

## Task 4: 在 AppSplash.vue 添加 prefers-reduced-motion 兜底（禁用光影动画）

**Files:**
- Modify: `apps/desktop/src/components/AppSplash.vue`

**为什么**：Task 2 的 `splash-glow` 动画在 `prefers-reduced-motion: reduce` 时仍会播放 filter 动画，违反 Web Interface Guidelines。需在 scoped 样式中加媒体查询禁用。

- [ ] **Step 1: 在 AppSplash.vue `<style scoped>` 末尾追加 reduced-motion 兜底**

在 `apps/desktop/src/components/AppSplash.vue` 的 `<style scoped>` 块末尾（`@keyframes splash-glow` 之后、`</style>` 之前）追加：

```vue
@media (prefers-reduced-motion: reduce) {
  .app-splash__logo {
    animation: none;
    /* 静态显示 logo，保留基础光晕（非动画） */
    filter: drop-shadow(0 0 8px var(--accent-brand));
    opacity: 1;
  }
}
```

**说明**：
- `animation: none` 禁用 `splash-glow` 循环。
- 保留静态 `filter: drop-shadow(0 0 8px ...)` 作为基础光晕（8px 是 6px↔18px 的中间值），让 logo 在 reduced-motion 下仍有光影美感，但不脉动。
- `opacity: 1` 确保完全可见。

- [ ] **Step 2: 验证 reduced-motion 下 splash logo 静态光影**

DevTools → Rendering → 勾选 `prefers-reduced-motion: reduce`，刷新窗口。

预期：
- logo 周围有静态光晕（8px drop-shadow），不脉动。
- logo 大小、透明度均不变。
- splash 退出时无 translateY（Task 3 兜底生效）。

- [ ] **Step 3: 暂不提交，继续 Task 5**

---

## Task 5: 综合验证 + 测试 + 提交

**Files:**
- Read-only verification across all modified files.

- [ ] **Step 1: 完整启动序列验证**

完全退出 Electron 后重启：
```bash
npm --prefix apps/desktop run dev
```

观察完整序列：
1. **t≈0**：窗口出现，HTML splash 瞬时显示（无 fade-in，已被回退）。
2. **Vue 挂载**：AppSplash 接管，logo 开始 2.8s **光影呼吸**（drop-shadow 6px↔18px + opacity 0.85↔1，大小不变）。
3. **连接成功**：
   - splash 整体向上飞出（600ms）+ 淡出（400ms）。
   - 主界面容器瞬时显示。
   - AppHeader 升起（0ms）。
   - sidebar 升起（80ms，从左第 1）。
   - ChatPanel 升起（200ms，中间第 2）。
   - ContextPanel 升起（320ms，右侧第 3）。
   - 整体阶梯式从左至右。
   - background-layer 静止。
4. **路由切换**：Home ↔ Settings，原有 `page-slide-left/right` 保留；staggered 升起**不重播**（main-rise 只触发一次）。
5. **子窗口**（Debug Studio / Detached Panel）：无 splash、无 staggered 升起，瞬时显示。

- [ ] **Step 2: 验证 git 冲突标记已完全清除**

运行：
```bash
git grep -n "<<<<<<<\|>>>>>>>\|=======" -- apps/desktop/src/App.vue
```

预期：无输出（无冲突标记残留）。

- [ ] **Step 3: 运行现有测试套件**

```bash
npm --prefix apps/desktop test
```

预期：全部通过。重点关注 CSS 契约测试（如 `settingsCssContract.test.ts`）——本任务仅在 `styles.css` 末尾追加新块，未改动既有规则。

- [ ] **Step 4: DevTools 模拟 prefers-reduced-motion 完整验证**

DevTools → Rendering → 勾选 `prefers-reduced-motion: reduce`。硬刷新。

预期：
- HTML splash：瞬时显示。
- AppSplash logo：静态光晕（8px drop-shadow），不脉动。
- splash 退出：仅 200ms opacity 淡出。
- 主界面：所有子元素无 animation，瞬时显示。
- 功能完整，无缺失。

- [ ] **Step 5: Web Interface Guidelines 自检**

| 规则 | 检查点 | 结果 |
|---|---|---|
| Animation | `prefers-reduced-motion` 兜底 | ✅ Task 3 + Task 4 均加 |
| Animation | 仅动画 `transform` / `opacity`（光影 filter 是用户明确要求破例） | ✅ |
| Animation | 显式列举 transition 属性，非 `transition: all` | ✅ Task 3 CSS 显式列 |
| Animation | 动画可中断 | ✅ animation 默认可被打断 |
| Accessibility | 装饰性 SVG `aria-hidden="true"` | ✅ AppSplash.vue 已有 |
| Anti-patterns | 无 `transition: all` | ✅ |
| Anti-patterns | 无 `outline-none` 无替代 | ✅ 未涉及 |

- [ ] **Step 6: 提交所有改动**

确认改动文件清单：
- `apps/desktop/src/App.vue`（清理冲突 + 恢复 splash-exit 包裹）
- `apps/desktop/src/components/AppSplash.vue`（光影呼吸 + reduced-motion 兜底）
- `apps/desktop/src/styles.css`（恢复 splash-exit + 重写 main-rise staggered + rise-up keyframes）

提交（用临时文件避免 PowerShell 转义问题）：

写入 `.git/COMMIT_MSG_TMP.txt`：
```
feat(desktop): logo light-shadow breathe + staggered main UI rise

- AppSplash: replace scale-based breathe with light-shadow pulse
  (filter drop-shadow 6px<->18px + opacity 0.85<->1, 2.8s, no scale)
- App.vue: clean up git conflict markers; restore splash-exit Transition
  wrapper around AppSplash; keep isChildWindow skip logic
- styles.css: restore splash-exit-leave classes; rewrite main-rise to
  staggered child rise-up (AppHeader -> sidebar -> ChatPanel -> ContextPanel,
  left-to-right cascade 0/80/200/320ms delays); add rise-up keyframes;
  prefers-reduced-motion fallback disables transforms and filter animation

Main window: full sequence (logo glow breathe -> fly out -> staggered rise).
Child windows: skip animation entirely (?splash=0).
```

运行：
```bash
git add apps/desktop/src/App.vue apps/desktop/src/components/AppSplash.vue apps/desktop/src/styles.css
git commit -F .git/COMMIT_MSG_TMP.txt
Remove-Item .git/COMMIT_MSG_TMP.txt
git log -1 --stat
```

---

## Verification Summary

| 验证项 | 命令/操作 | 预期 |
|---|---|---|
| git 冲突清除 | `git grep -n "<<<<<<<\|>>>>>>>\|=======" -- apps/desktop/src/App.vue` | 无输出 |
| 构建通过 | `npm --prefix apps/desktop run build` | 成功 |
| 光影呼吸 | `npm --prefix apps/desktop run dev` | logo 2.8s 光晕脉动，大小不变 |
| splash 飞出 | 同上，等连接成功 | 600ms translateY(-100vh) + 淡出 |
| 阶梯式升起 | 同上 | AppHeader→sidebar→ChatPanel→ContextPanel 依次 |
| 背景不动 | 同上 | background-layer 静止 |
| 路由切换不破坏 | 切换 Home/Settings | page-slide 保留，staggered 不重播 |
| 子窗口跳过 | 打开 Debug Studio | 无动画，瞬时显示 |
| reduced-motion | DevTools Rendering 模拟 | 仅淡入淡出，无 transform/filter 动画 |
| 现有测试 | `npm --prefix apps/desktop test` | 全部通过 |

---

## Self-Review Checklist

1. **Spec coverage**：
   - "logo 显示光影的变化，而不是大小的变化" → Task 2（drop-shadow 光影呼吸，无 scale）
   - "保留我之前的过渡" → Task 1（清理冲突，保留 HEAD 侧）+ Task 3（恢复 splash-exit + main-rise）
   - "主界面从左至右阶梯式升起" → Task 3（staggered rise-up：sidebar→ChatPanel→ContextPanel）
   - Web Interface Guidelines 合规 → Task 3 + Task 4（reduced-motion 兜底）

2. **Placeholder scan**：
   - "ChatPanel / ContextPanel 的根 class 需确认"——这是执行时验证步骤（Task 3 Step 2），非占位符；plan 提供了备选 `.workspace > :nth-child(N)`。
   - 无 TBD/TODO/「类似 Task N」/「添加适当错误处理」等占位符。

3. **Type consistency**：
   - `isConnecting` 在 Task 1 定义为 `computed(() => !isChildWindow && connectionState.value === 'connecting')`，在模板中作为 `v-if="isConnecting"` / `v-if="!isConnecting"` 使用——一致。
   - `Transition name="splash-exit"` 与 Task 3 中 `.splash-exit-leave-active` 类名一致。
   - `Transition name="main-rise"` 与 Task 3 中 `.main-rise-enter-active` 类名一致。
   - `@keyframes splash-glow`（Task 2）与 `animation: splash-glow 2.8s ...`（Task 2）一致。
   - `@keyframes rise-up`（Task 3）与 `animation: rise-up 500ms ...`（Task 3）一致。
   - `.app-splash__logo` class（Task 2 模板）与 Task 2 style + Task 4 reduced-motion 选择器一致。

4. **风险点**：
   - ChatPanel/ContextPanel 根 class 可能不是 `.chat-panel`/`.context-panel`——Task 3 Step 2 提供了确认 + 备选方案。
   - main-rise 容器无 transition 可能导致"硬切"感——但子元素 staggered 升起会掩盖容器瞬时显示的突兀感。
   - filter 动画性能——`will-change: filter` 已加，且只动画一个 SVG path（88px），性能开销可接受。
