<script setup lang="ts">
/**
 * TerminalView — Individual xterm.js terminal renderer.
 *
 * Creates an xterm.js Terminal instance, attaches it to a container element,
 * and wires up the data flow between xterm and the Electron terminal backend.
 *
 * Features:
 * - Auto-fit to container size via ResizeObserver
 * - Web links addon for clickable URLs
 * - Theme adaptation from CSS variables
 * - Focus management
 * - Proper cleanup on unmount
 */
import { onMounted, onUnmounted, ref, watch } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import { WebLinksAddon } from '@xterm/addon-web-links'
import '@xterm/xterm/css/xterm.css'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useTerminal } from '@/composables/useTerminal'
import { codeFont, codeFontStack } from '@/lib/codeFont'

const props = defineProps<{
  /** Terminal instance ID from the backend */
  terminalId: string
}>()

const emit = defineEmits<{
  'exited': [terminalId: string]
}>()

const {
  attachTerminal,
  detachTerminal,
  fitTerminal,
  focusTerminal,
  getTerminal,
} = useTerminal()

const { panelStyle } = usePanelStyles()
const containerRef = ref<HTMLElement | null>(null)
let term: Terminal | null = null
let themeSyncObserver: MutationObserver | null = null
// xterm measures glyphs itself, so the code font reaches it as an option, not through CSS.
watch(codeFont, (id) => { if (term) term.options.fontFamily = codeFontStack(id) })
let fitAddon: FitAddon | null = null
let resizeObserver: ResizeObserver | null = null

function syncTerminalSurface(): void {
  if (!term) return
  const isTransparent = panelStyle.value.effect !== 'opaque'
  const root = typeof document !== 'undefined' ? document.documentElement : null
  const opaqueBg = root
    ? getComputedStyle(root).getPropertyValue('--bg-primary').trim() || (root.getAttribute('data-theme') === 'dark' ? '#09090b' : '#ffffff')
    : '#09090b'
  const nextBg = isTransparent ? 'rgba(0, 0, 0, 0)' : opaqueBg
  const currentTheme = term.options.theme ?? {}
  if (currentTheme.background !== nextBg) {
    term.options.theme = {
      ...currentTheme,
      background: nextBg,
    }
  }
}

watch(() => panelStyle.value.effect, () => {
  syncTerminalSurface()
})

// ---- xterm.js options ----

function createTerminal(): Terminal {
  return new Terminal({
    allowTransparency: true,
    cursorBlink: true,
    cursorStyle: 'bar',
    fontFamily: codeFontStack(codeFont.value),
    fontSize: 13,
    lineHeight: 1.2,
    letterSpacing: 0,
    scrollback: 10000,
    allowProposedApi: true,
    macOptionIsMeta: true,
    rightClickSelectsWord: true,
    theme: undefined, // Will be set by attachTerminal
  })
}

// ---- Lifecycle ----

onMounted(() => {
  if (!containerRef.value) return

  // Create xterm.js Terminal instance
  term = createTerminal()
  fitAddon = new FitAddon()

  // Load addons
  term.loadAddon(fitAddon)
  term.loadAddon(new WebLinksAddon())

  // Attach to the terminal backend
  attachTerminal(props.terminalId, containerRef.value, term, fitAddon)
  syncTerminalSurface()

  if (typeof MutationObserver !== 'undefined' && typeof document !== 'undefined') {
    themeSyncObserver = new MutationObserver(() => {
      queueMicrotask(syncTerminalSurface)
    })
    themeSyncObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme', 'data-global-panel-effect', 'style'],
    })
  }

  // Set up ResizeObserver for auto-fitting. Goes through `fitTerminal` so the
  // zero-size guard applies: this host is `display:none` whenever its card is not
  // the active tab, and fitting against that would shrink the PTY to 2×1.
  resizeObserver = new ResizeObserver(() => fitTerminal(props.terminalId))
  resizeObserver.observe(containerRef.value)

  // Focus the terminal after a short delay to ensure it's ready
  setTimeout(() => {
    focusTerminal(props.terminalId)
  }, 100)
})

onUnmounted(() => {
  if (themeSyncObserver) {
    themeSyncObserver.disconnect()
    themeSyncObserver = null
  }

  // Clean up ResizeObserver
  if (resizeObserver) {
    resizeObserver.disconnect()
    resizeObserver = null
  }

  // Unmounting means this view went away, not that the user asked to kill the
  // shell: closing a tab, collapsing the feature column, a route change and a
  // detach all unmount here. Detach the listeners and xterm and leave the process
  // running; explicit close/restart/Ctrl+W still reach closeTerminal().
  detachTerminal(props.terminalId, term)

  term = null
  fitAddon = null
})

// ---- Watch for terminal exit ----

watch(
  () => getTerminal(props.terminalId)?.exited,
  (exited) => {
    if (exited) {
      emit('exited', props.terminalId)
    }
  },
)

// ---- Public methods (exposed via template ref) ----

defineExpose({
  fit: () => fitTerminal(props.terminalId),
  focus: () => focusTerminal(props.terminalId),
})
</script>

<template>
  <div class="terminal-view">
    <div ref="containerRef" class="terminal-container" />
  </div>
</template>

<style scoped>
.terminal-view {
  width: 100%;
  height: 100%;
  overflow: hidden;
  background: var(--surface-canvas, var(--bg-primary));
  padding: 4px 6px;
  box-sizing: border-box;
}

.terminal-container {
  width: 100%;
  height: 100%;
  overflow: hidden;
}

/* Override xterm.js default styles to match the app theme & panel material */
.terminal-view :deep(.xterm) {
  padding: 0;
}

.terminal-view :deep(.xterm-viewport) {
  background-color: transparent !important;
  overflow-y: auto !important;
}

.terminal-view :deep(.xterm-screen) {
  background-color: transparent !important;
}

.terminal-view :deep(.xterm-rows) {
  font-family: var(--font-mono) !important;
}

/* Thin scrollbar for terminal */
.terminal-view :deep(.xterm-viewport::-webkit-scrollbar) {
  width: 6px;
}

.terminal-view :deep(.xterm-viewport::-webkit-scrollbar-thumb) {
  background: rgba(125, 133, 144, 0.3);
  border-radius: 3px;
}

.terminal-view :deep(.xterm-viewport::-webkit-scrollbar-thumb:hover) {
  background: rgba(125, 133, 144, 0.5);
}

.terminal-view :deep(.xterm-viewport::-webkit-scrollbar-track) {
  background: transparent;
}
</style>
