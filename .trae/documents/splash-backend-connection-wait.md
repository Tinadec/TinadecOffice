# Splash 等待后端连接实现计划

## 摘要

扩展现有 splash 机制：当 Vue 主界面加载完成后，splash 不立即消失，而是持续显示直到后端（Gateway 48730）健康检查通过。若 30 秒内后端仍不可达，直接进入主界面。视觉保持纯静态 logo（无文字/动画），与现有 splash 一致。

## 当前状态分析

### 现有 splash 实现（[apps/desktop/index.html](file:///c:/git/agent/TinadecOffice/apps/desktop/index.html)）
- `<div id="app">` 内有原生 HTML splash 占位层（居中 logo + `#0a0e14` 背景）
- `app.mount('#app')` 时 Vue 替换 `#app` 子内容，splash 自动消失
- **限制**：splash 寿命仅到 Vue 挂载，无法延长到后端连接成功

### 后端健康检查已具备（[apps/desktop/src/api.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/api.ts) L997-1002）
```ts
health: () => request<Record<string, unknown>>('/api/v1/health'),
```
- Gateway 的 `/api/v1/health` 会代理到 Core（[TinadecGateway/src/index.ts](file:///c:/git/agent/TinadecOffice/TinadecGateway/src/index.ts) L157-168），成功 = 整条链路通
- 失败时 `request()` 抛 `Cannot connect to backend (...)`（[api.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/api.ts) L956-960）

### Composable 单例模式（[apps/desktop/src/composables/useTheme.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/composables/useTheme.ts) L173-188）
- 模块级 `let stored: Ref<T> | null = null` + 懒初始化函数
- 多组件共享同一份状态
- 无 Pinia，无新依赖

### App.vue 结构（[apps/desktop/src/App.vue](file:///c:/git/agent/TinadecOffice/apps/desktop/src/App.vue)）
- `<template>` 渲染 `.background-layer`（固定背景层）+ `<RouterView>`（带过渡）
- 无现有连接/loading 状态管理

### 测试约定（[apps/desktop/src/composables/useBackground.test.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/composables/useBackground.test.ts)）
- vitest colocated `*.test.ts`
- 测纯函数，不挂 Vue 组件

## 设计方案

**混合方案**：保留 index.html 原生 splash 作为"Vue 加载前的第一帧"，Vue 挂载后由 `<AppSplash>` 组件接管渲染相同视觉，直到 `connectionState` 变为 `connected` 或 `timeout`。

### 加载时序（改动后）
```
1. index.html 原生 splash 显示（覆盖 Vite 模块加载 + Vue init）
2. Vue 挂载 → App.vue setup → useConnection() 初始化 → onMounted 触发 start()
3. App.vue 渲染 <AppSplash>（视觉与原生 splash 一致，用户无感知切换）
4. useConnection 每 1.5s 调用 api.health()：
   - 成功 → connectionState = 'connected' → AppSplash 消失，主界面呈现
   - 失败 → 继续轮询
5. 若 30s 内未成功 → connectionState = 'timeout' → AppSplash 消失，进入主界面
```

## 假设与决策

| 决策 | 选择 | 理由 |
|------|------|------|
| 视觉样式 | 纯静态 logo，无文字/动画 | 用户选择；与现有 splash 一致 |
| 超时行为 | 30s 后直接进主界面，无重试按钮 | 用户明确要求"就直接进入主界面" |
| 连接检测端点 | `api.health()`（Gateway 48730） | 已含 Core 下游检测；遵守"Desktop 只调 Gateway"硬约束 |
| 轮询间隔 | 1.5s | 平衡响应速度与请求量 |
| 状态机 | `connecting → connected \| timeout` | timeout 也显示主界面，仅状态值不同 |
| 配色 | `var(--bg-primary)` + `var(--accent-brand)` | 跟随主题；styles.css 已定义（L116/L164 暗色，L177/L225 亮色） |
| 单例 | 模块级 ref + `started` 标志 | 防止多次调用 start() 重复轮询；复用 useTheme 模式 |

## 文件结构

| 文件 | 操作 | 职责 |
|------|------|------|
| `apps/desktop/src/composables/useConnection.ts` | 新建 | 连接状态机：轮询 api.health()，30s 超时 |
| `apps/desktop/src/composables/useConnection.test.ts` | 新建 | 测试状态转换与超时 |
| `apps/desktop/src/components/AppSplash.vue` | 新建 | Vue 渲染的 splash，视觉同原生 splash |
| `apps/desktop/src/App.vue` | 修改 | 引入 useConnection + AppSplash，连接前显示 splash |
| `apps/desktop/index.html` | 不改 | 原生 splash 保留作为第一帧 |
| `apps/desktop/src/api.ts` | 不改 | api.health() 已存在 |
| `apps/desktop/src/i18n.ts` | 不改 | splash 无文字，无需 i18n |

## 任务分解

### Task 1: 创建 useConnection composable

**Files:**
- Create: `apps/desktop/src/composables/useConnection.ts`

- [ ] **Step 1: 创建 composable 文件**

```ts
// apps/desktop/src/composables/useConnection.ts
import { ref, type Ref } from 'vue'
import { api } from '@/api'

/**
 * Connection state machine for splash screen gating.
 * - 'connecting': splash visible, polling backend health
 * - 'connected': backend reachable, splash hidden
 * - 'timeout': 30s elapsed without backend, splash hidden (enter main UI anyway)
 */
export type ConnectionState = 'connecting' | 'connected' | 'timeout'

export const CONNECTION_TIMEOUT_MS = 30_000
export const CONNECTION_POLL_INTERVAL_MS = 1_500

let state: Ref<ConnectionState> | null = null
let timeoutHandle: ReturnType<typeof setTimeout> | null = null
let pollHandle: ReturnType<typeof setInterval> | null = null
let started = false

function getState(): Ref<ConnectionState> {
  if (!state) {
    state = ref<ConnectionState>('connecting')
  }
  return state
}

function clearTimers() {
  if (timeoutHandle !== null) {
    clearTimeout(timeoutHandle)
    timeoutHandle = null
  }
  if (pollHandle !== null) {
    clearInterval(pollHandle)
    pollHandle = null
  }
}

function markConnected() {
  if (state && state.value === 'connecting') {
    state.value = 'connected'
    clearTimers()
  }
}

async function probe(): Promise<boolean> {
  try {
    await api.health()
    return true
  } catch {
    return false
  }
}

export function useConnection() {
  const connectionState = getState()

  async function start() {
    if (started) return
    started = true

    // 30s timeout: if still 'connecting', transition to 'timeout'
    timeoutHandle = setTimeout(() => {
      if (state && state.value === 'connecting') {
        state.value = 'timeout'
        clearTimers()
      }
    }, CONNECTION_TIMEOUT_MS)

    // Immediate first probe (don't wait for first interval tick)
    if (await probe()) {
      markConnected()
      return
    }

    // Poll every CONNECTION_POLL_INTERVAL_MS until success or timeout
    pollHandle = setInterval(async () => {
      if (await probe()) {
        markConnected()
      }
    }, CONNECTION_POLL_INTERVAL_MS)
  }

  return { connectionState, start }
}

/** Test-only: reset singleton state between tests. */
export function __resetConnectionForTests() {
  clearTimers()
  if (state) state.value = 'connecting'
  started = false
}
```

**设计要点**：
- 模块级单例（`state`、`started`）——复用 [useTheme.ts](file:///c:/git/agent/TinadecOffice/apps/desktop/src/composables/useTheme.ts) L173-188 模式
- `started` 标志防止 `onMounted` 重复触发
- `markConnected()` 检查 `connecting` 状态，避免超时后误改
- `__resetConnectionForTests()` 仅供测试重置单例

---

### Task 2: 编写 useConnection 测试

**Files:**
- Create: `apps/desktop/src/composables/useConnection.test.ts`

- [ ] **Step 1: 创建测试文件**

```ts
// apps/desktop/src/composables/useConnection.test.ts
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'

vi.mock('@/api', () => ({
  api: {
    health: vi.fn(),
  },
}))

import { api } from '@/api'
import {
  useConnection,
  __resetConnectionForTests,
  CONNECTION_TIMEOUT_MS,
  CONNECTION_POLL_INTERVAL_MS,
} from './useConnection'

describe('useConnection', () => {
  beforeEach(() => {
    __resetConnectionForTests()
    vi.useFakeTimers()
    vi.clearAllMocks()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('starts in connecting state', () => {
    const { connectionState } = useConnection()
    expect(connectionState.value).toBe('connecting')
  })

  it('transitions to connected when first health probe succeeds', async () => {
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { connectionState, start } = useConnection()
    await start()
    expect(connectionState.value).toBe('connected')
  })

  it('stays connecting when first probe fails, then connected on retry', async () => {
    let calls = 0
    vi.mocked(api.health).mockImplementation(async () => {
      calls++
      if (calls < 2) throw new Error('Cannot connect to backend')
      return { status: 'ok' }
    })
    const { connectionState, start } = useConnection()
    await start()
    expect(connectionState.value).toBe('connecting')

    // Advance past one poll interval; flush the async probe
    await vi.advanceTimersByTimeAsync(CONNECTION_POLL_INTERVAL_MS)
    expect(connectionState.value).toBe('connected')
  })

  it('transitions to timeout after 30s of failed probes', async () => {
    vi.mocked(api.health).mockRejectedValue(new Error('Cannot connect to backend'))
    const { connectionState, start } = useConnection()
    await start()
    expect(connectionState.value).toBe('connecting')

    await vi.advanceTimersByTimeAsync(CONNECTION_TIMEOUT_MS)
    expect(connectionState.value).toBe('timeout')
  })

  it('start() is idempotent (calling twice does not restart polling)', async () => {
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { start } = useConnection()
    await start()
    // Second call should be a no-op (started flag guard)
    await start()
    expect(api.health).toHaveBeenCalledTimes(1)
  })

  it('timeout does not override connected state', async () => {
    vi.mocked(api.health).mockResolvedValue({ status: 'ok' })
    const { connectionState, start } = useConnection()
    await start()
    // Already connected; advancing past timeout should not change state
    await vi.advanceTimersByTimeAsync(CONNECTION_TIMEOUT_MS)
    expect(connectionState.value).toBe('connected')
  })
})
```

- [ ] **Step 2: 运行测试，确认通过**

Run: `npm --prefix apps/desktop test -- useConnection`
Expected: 6 tests passed

---

### Task 3: 创建 AppSplash 组件

**Files:**
- Create: `apps/desktop/src/components/AppSplash.vue`

- [ ] **Step 1: 创建组件文件**

```vue
<!-- apps/desktop/src/components/AppSplash.vue -->
<script setup lang="ts">
/**
 * AppSplash — Vue-rendered splash shown while waiting for the backend.
 *
 * Visual matches the native splash in index.html so the transition
 * from native (pre-Vue) → Vue splash is seamless. Uses CSS variables
 * so it follows the active theme (dark/light).
 *
 * Shown by App.vue when connectionState === 'connecting'.
 * Removed when connectionState becomes 'connected' or 'timeout'.
 */
</script>

<template>
  <div class="app-splash">
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 453.04 350" fill="currentColor" aria-hidden="true">
      <path d="M0,152.01929L0,231.13193C0,267.12656,23.88369,298.75122,58.504963,308.59903L182.44873,343.85413C211.25745,352.04858,241.78033,352.04858,270.58905,343.85413L394.53281,308.59903C429.15408,298.75122,453.03775,267.12656,453.03775,231.13193L453.03775,152.01929C453.03775,131.48445,440.98279,113.76516,423.5636,105.55234L394.22815,35.020775C385.58447,14.237647,359.06622,0.0035646637,328.98392,0L317.35669,0C304.81461,0,294.64725,7.3336515,294.64725,16.380161C294.64725,25.426661,304.81461,32.760319,317.35669,32.760319L328.98392,32.760319C339.01624,32.759811,347.86035,37.507561,350.73959,44.439369L374.11868,100.67502L78.907547,100.67502L102.29823,44.439369C105.17747,37.507561,114.02158,32.759811,124.05387,32.760319L135.68109,32.760319C148.22321,32.760319,158.39055,25.426661,158.39055,16.380161C158.39055,7.3336515,148.22321,0,135.68109,0L124.05387,0C93.971581,0.0035561048,67.45327,14.237639,58.809616,35.020775L29.474157,105.55235C12.054992,113.76519,0,131.48445,0,152.01929Z" />
    </svg>
  </div>
</template>

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
.app-splash svg {
  height: 88px;
  width: auto;
  color: var(--accent-brand);
}
</style>
```

**设计要点**：
- SVG path data 与 [index.html](file:///c:/git/agent/TinadecOffice/apps/desktop/index.html) 原生 splash、[BrandLogo.vue](file:///c:/git/agent/TinadecOffice/apps/desktop/src/components/BrandLogo.vue) L26 完全一致
- `var(--bg-primary)` / `var(--accent-brand)`：跟随主题（[styles.css](file:///c:/git/agent/TinadecOffice/apps/desktop/src/styles.css) L116/L164 暗色，L177/L225 亮色）
- `z-index: 9999`：确保覆盖在所有层之上
- 被 `html { border-radius: 12px; overflow: hidden }`（[styles.css](file:///c:/git/agent/TinadecOffice/apps/desktop/src/styles.css) L256-259）自动裁剪圆角

---

### Task 4: 修改 App.vue 集成 splash 与连接检查

**Files:**
- Modify: `apps/desktop/src/App.vue`

- [ ] **Step 1: 修改 `<script setup>`，引入 useConnection 和 AppSplash**

将现有 [App.vue](file:///c:/git/agent/TinadecOffice/apps/desktop/src/App.vue) L1-44 的 `<script setup>` 改为：

```ts
<script setup lang="ts">
import { RouterView } from 'vue-router'
import { ref, watch, onMounted } from 'vue'
import router from './router'
import { useBackground } from '@/composables/useBackground'
import { useConnection } from '@/composables/useConnection'
import AppSplash from '@/components/AppSplash.vue'

// ---- Background layer (global, outside page transitions) ----
const { settings: backgroundSettings, applyBackground } = useBackground()
watch(backgroundSettings, () => applyBackground(), { deep: true, immediate: true })

// ---- Backend connection gating ----
// Splash stays visible until backend connects or 30s timeout.
const { connectionState, start: startConnection } = useConnection()
onMounted(() => {
  startConnection()
})

// Track navigation direction for directional page transitions.
const transitionName = ref('page-slide-left')

const navOrder: Record<string, number> = {
  home: 0,
  market: 1,
  settings: 2,
  'debug-studio': 3,
  'code-editor': 4,
  'detached-panel': 5,
}

// Set transition direction before navigation completes so the
// <Transition> component picks up the correct name.
router.beforeEach((to, from, next) => {
  const toOrder = navOrder[String(to.name)] ?? 0
  const fromOrder = navOrder[String(from.name)] ?? 0
  transitionName.value = toOrder >= fromOrder ? 'page-slide-left' : 'page-slide-right'
  next()
})
</script>
```

**改动点**：
- 新增 `import { onMounted }`（L3）
- 新增 `import { useConnection }` 和 `import AppSplash`（L6-7）
- 新增连接检查块：`const { connectionState, start } = useConnection()` + `onMounted(start)`
- 其余逻辑不变

- [ ] **Step 2: 修改 `<template>`，连接前显示 AppSplash**

将现有 [App.vue](file:///c:/git/agent/TinadecOffice/apps/desktop/src/App.vue) L46-96 的 `<template>` 改为：

```html
<template>
  <!-- Splash: shown until backend connects or 30s timeout.
       Visual matches index.html native splash for seamless transition. -->
  <AppSplash v-if="connectionState === 'connecting'" />

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

**改动点**：
- 顶层加 `<AppSplash v-if="connectionState === 'connecting'" />`
- 原有内容用 `<template v-else>` 包裹
- background-layer 和 RouterView 内容完全不变

- [ ] **Step 3: 运行全部测试，确认无回归**

Run: `npm --prefix apps/desktop test`
Expected: 全部通过（现有 112 + 新增 6 = 118 tests）

---

### Task 5: 端到端验证

- [ ] **Step 1: 验证后端已启动场景（正常流程）**

```bash
npm run dev
```
- 启动后先看到居中 logo（原生 splash）
- Vue 挂载后 logo 持续显示（AppSplash 接管，无闪烁）
- 后端就绪后 logo 消失，主界面呈现
- 预期：splash 停留时间很短（后端通常秒级就绪）

- [ ] **Step 2: 验证后端未启动场景（超时流程）**

不启动 Gateway/Core，仅启动 Desktop：
```powershell
# 临时修改：在另一个终端不启动 gateway，只启 desktop
Remove-Item Env:VITE_DEV_SERVER_URL -ErrorAction SilentlyContinue
cd apps/desktop
# 先启 vite
npx vite --host 127.0.0.1 --port 5173
# 另一终端，不启 gateway
$env:VITE_DEV_SERVER_URL="http://127.0.0.1:5173"
npx electron .
```
- 应看到 splash 持续显示 30 秒
- 30 秒后 splash 消失，进入主界面（HomePage，可能显示空数据/错误状态）
- 期间无白屏、无闪烁

- [ ] **Step 3: 视觉一致性检查**

- 原生 splash → AppSplash 切换无视觉跳变（暗色主题下背景/颜色一致）
- 窗口圆角正常（splash 被裁剪，无溢出）
- logo 始终居中

- [ ] **Step 4: 提交**

```bash
git add apps/desktop/src/composables/useConnection.ts \
        apps/desktop/src/composables/useConnection.test.ts \
        apps/desktop/src/components/AppSplash.vue \
        apps/desktop/src/App.vue
git commit -m "feat(desktop): splash waits for backend connection with 30s timeout"
```

## 自检清单

- [x] **Spec 覆盖**："前端加载完成仍显示 splash" → Task 4 `v-if="connectionState === 'connecting'"`；"直至连接到后端" → Task 1 `markConnected()`；"30秒超时进主界面" → Task 1 `setTimeout` + Task 4 `v-else`
- [x] **无占位符**：所有代码块完整，无 TODO/TBD
- [x] **类型一致**：`ConnectionState`、`connectionState`、`start` 在 composable、组件、App.vue 中命名一致
- [x] **文件路径准确**：全部基于 Phase 1 探索的实际路径
- [x] **遵守约束**：Desktop 只调 Gateway（api.health 走 48730）、不引新依赖、不编辑产物目录
