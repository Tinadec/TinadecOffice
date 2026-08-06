<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { Loader2, Minus, PanelRightOpen, Square, X } from '@lucide/vue'
import { useTheme } from '@/composables/useTheme'
import { getWorkbenchCardDescriptor } from '@/workbench/registry'
import { createHomeWorkbenchController, provideHomeWorkbench } from '@/workbench/homeController'
import { provideWorkbenchCard } from '@/workbench/cardContext'
import type { PersistedCardInstance, WorkbenchPageId } from '@/workbench/types'

const route = useRoute()
const { applyInitialTheme, theme, accentColor } = useTheme()
const home = createHomeWorkbenchController()
provideHomeWorkbench(home)

const loading = ref(true)
const error = ref('')
const pageId = ref<WorkbenchPageId>('home')
const title = ref('Detached module')
const card = ref<PersistedCardInstance>({ id: 'detached:loading', type: '', state: {} })
const descriptor = computed(() => getWorkbenchCardDescriptor(card.value.type))
const windowId = computed(() => String(route.query.windowId ?? ''))

provideWorkbenchCard({
  card,
  instanceId: `detached:${windowId.value}`,
  pageId: pageId.value,
  updateState,
})

async function loadCard(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const response = await window.tinadec.getDetachedWorkbenchCard?.()
    const payload = response?.workbench
    if (!payload?.card) throw new Error('Detached card state is unavailable.')
    card.value = payload.card
    pageId.value = payload.pageId
    title.value = payload.title
    if (!getWorkbenchCardDescriptor(payload.card.type)) throw new Error(`Unknown Workbench card: ${payload.card.type}`)
    if (payload.card.type.startsWith('home.')) {
      home.configureInitialSelection(payload.card.state)
      home.start()
    }
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

function updateState(state: Record<string, unknown>): void {
  card.value = { ...card.value, state: structuredClone(state) }
  void window.tinadec.updateDetachedWorkbenchCardState?.(card.value.state)
}

function reattach(): void {
  if (window.tinadec.reattachWorkbenchCard) {
    void window.tinadec.reattachWorkbenchCard(card.value.state)
    return
  }
  void window.tinadec.reattachPanel(card.value.id, card.value.type, title.value, card.value.state)
}

function minimizeWindow(): void { window.tinadec.minimizeWindow() }
function maximizeWindow(): void { window.tinadec.maximizeWindow() }
function closeWindow(): void { window.tinadec.closeWindow() }

let removeThemeListener: (() => void) | undefined
onMounted(async () => {
  applyInitialTheme?.()
  await loadCard()
  removeThemeListener = window.tinadec.onPanelThemeChanged?.((data) => {
    if (data.theme) theme.value = data.theme as 'dark' | 'light' | 'system'
    if (data.accentColor) accentColor.value = data.accentColor
  }) ?? undefined
})

onBeforeUnmount(() => {
  removeThemeListener?.()
  home.stop()
})
</script>

<template>
  <main class="detached-panel-shell">
    <header class="detached-titlebar">
      <div class="detached-titlebar-drag" />
      <span class="detached-title">{{ title }}</span>
      <div class="detached-titlebar-actions">
        <button title="Reattach to main window" @click="reattach"><PanelRightOpen /></button>
        <button title="Minimize" @click="minimizeWindow"><Minus /></button>
        <button title="Maximize" @click="maximizeWindow"><Square /></button>
        <button class="close" title="Close" @click="closeWindow"><X /></button>
      </div>
    </header>

    <section class="detached-panel-content">
      <div v-if="loading" class="detached-state"><Loader2 class="spinner" /><span>Loading module...</span></div>
      <div v-else-if="error" class="detached-state error"><strong>Unable to open module</strong><span>{{ error }}</span><button @click="loadCard">Retry</button></div>
      <component v-else-if="descriptor" :is="descriptor.component" />
    </section>
  </main>
</template>

<style scoped>
.detached-panel-shell { display: flex; flex-direction: column; width: 100%; height: 100vh; overflow: hidden; color: var(--text-primary); background: var(--bg-primary); }
.detached-titlebar { position: relative; display: flex; align-items: center; height: 36px; flex: 0 0 36px; padding-left: 12px; border-bottom: 1px solid var(--border-muted); background: var(--surface-chrome); }
.detached-titlebar-drag { position: absolute; inset: 0; -webkit-app-region: drag; }
.detached-title { z-index: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 12px; font-weight: 600; pointer-events: none; }
.detached-titlebar-actions { z-index: 2; display: flex; height: 100%; margin-left: auto; -webkit-app-region: no-drag; }
.detached-titlebar-actions button { display: grid; place-items: center; width: 38px; border: 0; color: var(--text-secondary); background: transparent; }
.detached-titlebar-actions button:hover { color: var(--text-primary); background: var(--surface-hover); }
.detached-titlebar-actions button.close:hover { color: white; background: #c42b1c; }
.detached-titlebar-actions svg { width: 14px; height: 14px; }
.detached-panel-content { flex: 1; min-width: 0; min-height: 0; overflow: hidden; }
.detached-state { display: grid; place-content: center; justify-items: center; gap: 10px; height: 100%; color: var(--text-muted); }
.detached-state svg { width: 24px; height: 24px; }
.detached-state.error strong { color: var(--text-primary); }
.detached-state.error button { padding: 6px 12px; border: 1px solid var(--border-default); border-radius: 6px; color: var(--text-primary); background: var(--surface-button); }
.spinner { animation: spin 800ms linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
</style>
