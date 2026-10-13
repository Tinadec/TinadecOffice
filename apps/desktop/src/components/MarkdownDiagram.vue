<script lang="ts">
interface MermaidApi {
  initialize: (config: Record<string, unknown>) => void
  parse: (text: string, options?: { suppressErrors?: boolean }) => Promise<unknown> | unknown
  render: (id: string, text: string) => Promise<{ svg: string }>
}

/*
 * Shared by every diagram on the page, so it lives outside the per-instance setup. Mermaid
 * scopes a diagram's styles and its temporary render node by the id it is given: two
 * diagrams with the same id drew into each other. Its configuration is global as well, and
 * render is not safe to run concurrently, so renders take turns.
 */
let loader: Promise<MermaidApi> | null = null
let diagramCounter = 0
let initialisedTheme: string | null = null
let renderQueue: Promise<unknown> = Promise.resolve()

/**
 * Mermaid is by far the heaviest dependency of a reply, so it is imported only
 * when a reply actually contains a diagram.
 */
function loadMermaid(): Promise<MermaidApi> {
  loader ??= import('mermaid').then(module => module.default as unknown as MermaidApi)
  return loader
}

function serialised<T>(task: () => Promise<T>): Promise<T> {
  const run = renderQueue.then(task, task)
  renderQueue = run.catch(() => undefined)
  return run
}

/** Narrowest a diagram may shrink to, as a share of the width mermaid laid it out at. */
const MIN_DIAGRAM_SCALE = 0.8

/** Mermaid writes the laid-out width as the svg's inline max-width; null when it is absent. */
export function diagramMinWidth(svg: string): string | null {
  const match = /<svg\b[^>]*\bstyle="[^"]*max-width:\s*([\d.]+)px/.exec(svg)
  const width = match ? Number(match[1]) : NaN
  return Number.isFinite(width) && width > 0 ? `${Math.round(width * MIN_DIAGRAM_SCALE)}px` : null
}

function nextDiagramId(): string {
  diagramCounter += 1
  return `md-diagram-${diagramCounter}`
}
</script>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { TriangleAlert } from '@lucide/vue'

const props = defineProps<{
  /** Raw mermaid source, taken from the already sanitised code fence. */
  code: string
}>()

const { t } = useI18n()

/** Past this size a diagram is more likely to hang the reply than to inform it. */
const MAX_DIAGRAM_CHARS = 20000
/** Streaming writes a fence in pieces; wait for it to settle before laying out. */
const SETTLE_MS = 250

const svg = ref('')
const minWidth = computed(() => diagramMinWidth(svg.value))
const failed = ref(false)
const container = ref<HTMLElement | null>(null)

let themeObserver: MutationObserver | null = null
let settleTimer: ReturnType<typeof setTimeout> | null = null
let renderSequence = 0
let firstPass = true

function cssVar(name: string, fallback: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim()
  return value || fallback
}

/**
 * Diagrams inherit the active theme through the same CSS tokens the rest of the
 * app uses, so they follow the accent colour instead of shipping a second theme.
 */
function themeVariables(): Record<string, string> {
  const light = document.documentElement.getAttribute('data-theme') === 'light'
  return {
    darkMode: String(!light),
    background: 'transparent',
    primaryColor: cssVar('--surface-raised', light ? '#eef2ef' : '#1a1f29'),
    primaryTextColor: cssVar('--text-primary', light ? '#1f2328' : '#c9d1d9'),
    primaryBorderColor: cssVar('--border-default', light ? '#d0d7de' : '#1a1f29'),
    secondaryColor: cssVar('--surface-section', 'transparent'),
    tertiaryColor: cssVar('--surface-chrome', 'transparent'),
    lineColor: cssVar('--text-muted', '#6e7681'),
    textColor: cssVar('--text-primary', light ? '#1f2328' : '#c9d1d9'),
    // The base theme paints edge labels on a saturated block; keep them on the card surface.
    edgeLabelBackground: cssVar('--surface-section', light ? '#ffffff' : '#161b22'),
    fontSize: '12px',
  }
}

async function draw() {
  const source = props.code.trim()
  if (!source || source.length > MAX_DIAGRAM_CHARS) {
    svg.value = ''
    failed.value = true
    return
  }
  const sequence = ++renderSequence
  try {
    const mermaid = await loadMermaid()
    if (sequence !== renderSequence) return
    const rendered = await serialised(async () => {
      if (sequence !== renderSequence) return null
      const theme = document.documentElement.getAttribute('data-theme') ?? 'dark'
      if (initialisedTheme !== theme) {
        mermaid.initialize({
          startOnLoad: false,
          securityLevel: 'strict',
          theme: 'base',
          themeVariables: themeVariables(),
          fontFamily: 'inherit',
        })
        initialisedTheme = theme
      }
      // `suppressErrors` keeps an unfinished fence from logging while it streams in.
      const parsed = await mermaid.parse(source, { suppressErrors: true })
      if (sequence !== renderSequence) return null
      if (parsed === false) throw new Error('invalid diagram')
      return mermaid.render(nextDiagramId(), source)
    })
    if (!rendered || sequence !== renderSequence) return
    svg.value = rendered.svg
    failed.value = false
  } catch {
    if (sequence !== renderSequence) return
    svg.value = ''
    // An unrenderable diagram falls back to its own source instead of vanishing.
    failed.value = true
  }
}

watch(() => props.code, () => {
  if (firstPass) {
    firstPass = false
    void draw()
    return
  }
  if (settleTimer) clearTimeout(settleTimer)
  settleTimer = setTimeout(() => { void draw() }, SETTLE_MS)
}, { immediate: true })

onMounted(() => {
  themeObserver = new MutationObserver(() => {
    if (settleTimer) clearTimeout(settleTimer)
    settleTimer = setTimeout(() => { void draw() }, SETTLE_MS)
  })
  themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
})

onBeforeUnmount(() => {
  renderSequence += 1
  themeObserver?.disconnect()
  if (settleTimer) clearTimeout(settleTimer)
})
</script>

<template>
  <div ref="container" class="markdown-diagram-body">
    <!-- Mermaid runs with securityLevel strict and the source came from the
         sanitised fence, so its own output is the one place v-html is allowed. -->
    <div
      v-if="svg"
      class="markdown-diagram-svg"
      :style="minWidth ? { '--diagram-min-width': minWidth } : undefined"
      tabindex="0"
      role="img"
      :aria-label="t('chat.markdownDiagram')"
      v-html="svg"
    />
    <template v-else>
      <pre class="markdown-diagram-fallback"><code>{{ code }}</code></pre>
      <p v-if="failed" class="markdown-diagram-note" role="status">
        <TriangleAlert :size="12" aria-hidden="true" />
        <span>{{ t('chat.markdownDiagramError') }}</span>
      </p>
    </template>
  </div>
</template>
