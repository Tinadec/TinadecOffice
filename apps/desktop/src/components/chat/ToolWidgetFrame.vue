<script setup lang="ts">
/**
 * A tool's own HTML view of its result, in a sandboxed frame.
 *
 * This is the least trusted thing the product renders: not our code, not the user's code, but
 * markup a tool (and through it, a model) produced. So the frame is the boundary, and every part
 * of it is deliberate:
 *
 *   - `sandbox="allow-scripts"` and **no** `allow-same-origin`. Both together would be no sandbox
 *     at all — the frame would share the host origin and could read the host DOM, storage and the
 *     `window.tinadec` bridge. With only `allow-scripts` it runs in an opaque origin: it can draw
 *     and post messages, and it can reach nothing of ours.
 *   - The document comes from `tinadec-widget://`, whose URL carries a one-time token and whose
 *     body is dropped the moment it is served. Nothing is read from disk and no path is involved.
 *   - The document-level CSP is attached by the main process when it serves the body, so the
 *     policy travels with the bytes instead of depending on this attribute.
 *
 * What the frame can still do is consume CPU and send `postMessage`. This component therefore
 * validates what it accepts: only `widget:height` from *this* frame's own `contentWindow`, and
 * only a finite number. A widget cannot make us render arbitrary UI, cannot set our state, and
 * cannot reach any other widget.
 *
 * A widget that never arrives, or that the host refuses, degrades to the plain text beside it —
 * never to an empty panel that looks like a tool which did nothing.
 */
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'

const props = defineProps<{
  /** The tool-authored HTML. Empty or refused bodies fall back to plain text. */
  html: string | null | undefined
  title: string
}>()

const { t } = useI18n()

const frame = ref<HTMLIFrameElement | null>(null)
/** Set once the host has handed back a URL, cleared as soon as the widget is spent. */
const token = ref<string | null>(null)
const url = ref<string | null>(null)
const failure = ref<string | null>(null)
/** The height the widget asked for, clamped so a hostile document cannot grow it without bound. */
const height = ref(320)
const MIN_HEIGHT = 120
const MAX_HEIGHT = 900

function clampHeight(value: number): number {
  return Math.min(MAX_HEIGHT, Math.max(MIN_HEIGHT, Math.round(value)))
}

/**
 * Only messages from the frame we mounted count, and only the one shape we act on. A widget is
 * untrusted input, so the check is on the sender first — anything else is discarded without
 * being read.
 */
function onMessage(event: MessageEvent) {
  const frameWindow = frame.value?.contentWindow
  if (!frameWindow || event.source !== frameWindow) return
  const data = event.data as { type?: unknown; height?: unknown } | null
  if (!data || data.type !== 'widget:height') return
  if (typeof data.height !== 'number' || !Number.isFinite(data.height)) return
  height.value = clampHeight(data.height)
}

/**
 * A registration is a main-process resource with a TTL, so every path that abandons one has to
 * give it back: the component can unmount while `register` is still in flight (the token does not
 * exist yet, so `onBeforeUnmount` cannot see it), and a changed `html` replaces the previous token
 * before anything releases it. Both leak a served-but-never-consumed document until it expires.
 */
let disposed = false
/** Bumped per mount call, so a superseded registration can tell that it lost the race. */
let generation = 0

function releaseToken(value: string | null) {
  const release = window.tinadec?.releaseToolWidget
  if (release && value) void release(value)
}

async function mount(html: string | null | undefined) {
  const register = window.tinadec?.registerToolWidget
  const mine = ++generation
  // Whatever the previous call registered is now superseded.
  releaseToken(token.value)
  token.value = null
  url.value = null
  if (!register || !html || !html.trim()) {
    failure.value = null
    return
  }
  try {
    const registered = await register(html)
    // Unmounted, or a newer html arrived while this was in flight: hand the token straight back
    // rather than adopting it, because nothing will ever render or release it.
    if (disposed || mine !== generation) { releaseToken(registered?.token ?? null); return }
    // Main refuses an unusable body rather than serving an empty frame; the caller's plain-text
    // fallback is then the whole view, which is honest about what the tool returned.
    if (!registered) {
      failure.value = t('widget.refused')
      return
    }
    token.value = registered.token
    url.value = registered.url
    failure.value = null
  } catch (cause) {
    if (disposed || mine !== generation) return
    failure.value = cause instanceof Error ? cause.message : String(cause)
  }
}

// The body is served once, so a changed widget is a new registration, not a reload.
watch(() => props.html, (next) => { void mount(next) }, { immediate: true })

onMounted(() => window.addEventListener('message', onMessage))
onBeforeUnmount(() => {
  disposed = true
  window.removeEventListener('message', onMessage)
  // A widget the frame never consumed is revoked, so an abandoned turn leaves nothing served.
  releaseToken(token.value)
  token.value = null
  url.value = null
})

const showFrame = computed(() => Boolean(url.value))
</script>

<template>
  <div class="tool-widget" data-testid="tool-widget">
    <p v-if="failure" class="tool-widget-note" role="alert">{{ failure }}</p>
    <!--
      `referrerpolicy` is set here as well as on the response: the frame must never leak the
      host's URL, and a widget has no legitimate need for it.
    -->
    <iframe
      v-if="showFrame"
      ref="frame"
      :src="url!"
      :title="title"
      class="tool-widget-frame"
      :style="{ height: `${height}px` }"
      sandbox="allow-scripts"
      referrerpolicy="no-referrer"
      loading="lazy"
      data-testid="tool-widget-frame"
    />
  </div>
</template>

<style scoped>
.tool-widget { min-width: 0; }

.tool-widget-note {
  margin: 4px 0;
  font-size: 11px;
  color: var(--text-chat-muted);
}

/* The frame's own background is transparent, so the surface behind it shows through; a widget
   is expected to paint only what it needs rather than being handed a card. */
.tool-widget-frame {
  display: block;
  width: 100%;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: transparent;
  color-scheme: light dark;
}
</style>
