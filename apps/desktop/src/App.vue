<script setup lang="ts">
import { RouterView } from 'vue-router'
import { ref, computed, watch, onMounted } from 'vue'
import router from './router'
import { useBackground } from '@/composables/useBackground'
import { useConnection } from '@/composables/useConnection'
import AppSplash from '@/components/AppSplash.vue'

// ---- Background layer (global, outside page transitions) ----
// The background layer is ALWAYS rendered here — outside the <Transition> —
// so it is never affected by page-transition transforms.  CSS `position: fixed`
// inside a transformed ancestor behaves like `position: absolute`, which
// would cause the background to slide along with the page.  By keeping it
// here, the background stays perfectly static during navigation.
//
// Even when type === 'none' (no custom background), the layer is still
// rendered with the theme's --bg-primary colour.  This ensures the window
// has a stable, non-animated bottom layer at all times.  Page containers
// (.shell, .settings-page, .workspace) are always transparent so this
// layer shows through.
const { settings: backgroundSettings, applyBackground } = useBackground()
watch(backgroundSettings, () => applyBackground(), { deep: true, immediate: true })

// ---- Backend connection gating ----
// Splash stays visible until backend connects or 30s timeout.
// 子窗口（?splash=0，如 Debug Studio / Detached Panel）跳过 splash + main-rise 动画：
// 它们复用主窗口已建立的后端连接，不应重播首次启动序列。
const isChildWindow = new URLSearchParams(window.location.search).get('splash') === '0'
const { connectionState, start: startConnection } = useConnection()
const isConnecting = computed(() => !isChildWindow && connectionState.value === 'connecting')
onMounted(() => {
  startConnection()
})

// Track navigation direction for directional page transitions.
// Settings is "deeper" than home, so navigating to settings slides left,
// and returning slides right — following wayfinding design principles.
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
</template>
