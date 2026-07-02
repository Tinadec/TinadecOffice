<script setup lang="ts">
import { RouterView } from 'vue-router'
import { ref, watch } from 'vue'
import router from './router'
import { useBackground } from '@/composables/useBackground'

// ---- Background layer (global, outside page transitions) ----
// The background layer is rendered here — outside the <Transition> — so
// it is never affected by page-transition transforms.  CSS `position: fixed`
// inside a transformed ancestor behaves like `position: absolute`, which
// would cause the background to slide along with the page.  By keeping it
// here, the background stays perfectly static during navigation.
const { settings: backgroundSettings, applyBackground } = useBackground()
watch(backgroundSettings, () => applyBackground(), { deep: true, immediate: true })

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
  <!-- Background Layer — rendered outside <Transition> so it never moves -->
  <div v-if="backgroundSettings.type !== 'none'" class="background-layer">
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
  </div>

  <RouterView v-slot="{ Component }">
    <Transition :name="transitionName" mode="out-in">
      <component :is="Component" />
    </Transition>
  </RouterView>
</template>
