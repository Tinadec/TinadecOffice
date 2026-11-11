<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { UieCanvas, type UiePageId } from '@tinadec/ui'
import AppHeader from '@/components/AppHeader.vue'
import { marketController } from '@/controllers/MarketController'
import { useUiePage } from '@/lib/uiEngine'

const route = useRoute()
/** `/market`, `/market/skills` and `/market/mcp` are three authored layouts over one surface. */
const pageId = computed<UiePageId>(() => (route.meta.uiePage as UiePageId | undefined) ?? 'market')
const marketKind = computed<'skill' | 'mcp-server' | null>(() =>
  (route.meta.marketKind as 'skill' | 'mcp-server' | undefined) ?? null,
)

const uie = useUiePage(pageId.value)
const emit = defineEmits<{ ready: [] }>()
const canvasReady = ref(false)
let alive = true

// The page owns the read and its lifecycle; the cards it mounts only render the state.
onMounted(async () => {
  await uie.ready
  if (!alive) return
  // The shared store may still hold another page's layout while disk hydration runs.
  // Select the restored layout before the first canvas/card mount.
  uie.showPage(pageId.value)
  marketController.setKind(marketKind.value)
  canvasReady.value = true
  marketController.start()
  await nextTick()
  if (alive) emit('ready')
})

// Vue reuses this component when moving between the three market routes, so the layout and the
// locked kind have to follow the route rather than the setup call.
watch([() => route.meta.uiePage, () => route.meta.marketKind], async () => {
  if (!alive) return
  uie.showPage(pageId.value)
  marketController.setKind(marketKind.value)
  await nextTick()
})

onBeforeUnmount(() => {
  alive = false
  marketController.stop()
})
</script>

<template>
  <main class="shell">
    <div class="top-drag-bar" />
    <AppHeader />
    <UieCanvas v-if="canvasReady" />
  </main>
</template>

<style scoped>
.shell {
  position: relative;
  height: 100vh;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  background: transparent;
}
</style>
