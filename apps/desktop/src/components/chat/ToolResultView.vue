<script setup lang="ts">
/**
 * What a tool actually produced.
 *
 * The tool's full result has always been stored (Core writes `ResultJson` into the content
 * store and keeps a reference on the row) but no route returned it, so the transcript showed
 * a one-line `summary` and nothing else — a reader could not see the file that was read, the
 * command's output, or the search hits.
 *
 * The body arrives as text. This renders it by *shape*: a JSON object with recognisable keys
 * becomes rows, a diff-shaped payload goes to the diff viewer, and anything else falls back to
 * preformatted text rather than pretending to understand it. It never renders HTML or executes
 * anything from the payload — the model and the tools do not get to produce markup here.
 */
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import type { ToolExecutionResultDto } from '@/api'
import DiffViewer from '@/components/git/DiffViewer.vue'
import ToolWidgetFrame from './ToolWidgetFrame.vue'

const props = defineProps<{
  executionId: string
  toolId: string
  /**
   * Whether a tool-authored HTML view may be rendered. Only the "information" conversation
   * template turns this on: it is the tier where the agent is asked to present and ask for
   * information, and where the reader has opted into that. Every other template shows the
   * structured result and never mounts a frame.
   */
  allowWidget?: boolean
}>()
const { t } = useI18n()

const result = ref<ToolExecutionResultDto | null>(null)
const failure = ref<string | null>(null)
const loading = ref(false)

/**
 * Aborted on every re-run, so a slow response for a previous execution cannot land on the current
 * one. `api.getToolExecutionResult` takes the signal; an aborted request rejects and is ignored.
 */
let inFlight: AbortController | null = null
async function load() {
  inFlight?.abort()
  if (!props.executionId) { result.value = null; return }
  const controller = new AbortController()
  inFlight = controller
  loading.value = true
  try {
    const loaded = await api.getToolExecutionResult(props.executionId, controller.signal)
    if (controller.signal.aborted) return
    result.value = loaded
    failure.value = null
  } catch (cause) {
    // A cancellation is not a failure to show: the newer request owns the state now.
    if (controller.signal.aborted) return
    failure.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    if (inFlight === controller) { inFlight = null; loading.value = false }
  }
}

watch(() => props.executionId, load, { immediate: true })
onBeforeUnmount(() => inFlight?.abort())

/** Parsed payload, or null when the body is not JSON. */
const parsed = computed<unknown>(() => {
  const body = result.value?.body
  if (!body) return null
  try { return JSON.parse(body) } catch { return null }
})

const isJson = computed(() => parsed.value !== null)

/**
 * Rows for an object payload. Values are shown as-is; nested structures are re-serialised so
 * the row stays one line. Ordered as the tool emitted them, which is the tool's own priority.
 */
const rows = computed<Array<{ key: string; value: string }>>(() => {
  const value = parsed.value
  if (!value || typeof value !== 'object' || Array.isArray(value)) return []
  return Object.entries(value as Record<string, unknown>).map(([key, entry]) => ({
    key,
    value: typeof entry === 'string' ? entry : JSON.stringify(entry),
  }))
})

/** Array payloads (search hits, file lists) read better as a plain list. */
const listValues = computed<string[]>(() => {
  const value = parsed.value
  if (!Array.isArray(value)) return []
  return value.map((entry) => (typeof entry === 'string' ? entry : JSON.stringify(entry)))
})

/**
 * A diff, when the payload carries one. The key names below are the ones the tool layer
 * actually emits for the patch-carrying tools; anything else stays plain text.
 */
const diffText = computed<string | null>(() => {
  const value = parsed.value
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null
  const record = value as Record<string, unknown>
  for (const key of ['patch', 'diff', 'diff_text', 'unified_diff']) {
    const entry = record[key]
    if (typeof entry === 'string' && /^(@@|diff --git|--- )/m.test(entry)) return entry
  }
  return null
})

const rawText = computed(() => result.value?.body ?? '')
const truncatedLabel = computed(() => result.value?.truncated
  ? t('agent.resultTruncated', { returned: result.value.returned_length, total: result.value.content_length })
  : null)
</script>

<template>
  <div class="tool-result" data-testid="tool-result">
    <p v-if="loading" class="tool-result-note">{{ t('common.loading') }}</p>
    <p v-else-if="failure" class="tool-result-note is-error" role="alert">{{ failure }}</p>
    <template v-else-if="result">
      <p v-if="truncatedLabel" class="tool-result-note is-warning">{{ truncatedLabel }}</p>
      <p v-if="!result.body" class="tool-result-note">{{ t('agent.resultEmpty') }}</p>
      <!-- The tool's own view. Rendered before the generic shapes so a tool that authored HTML
           decides how its result reads, and only when the reader asked for that tier. -->
      <ToolWidgetFrame
        v-if="allowWidget && result.widget_html"
        :html="result.widget_html"
        :title="toolId"
      />
      <DiffViewer v-else-if="diffText" :diff-text="diffText" :file-path="result.tool_id" />
      <ul v-else-if="listValues.length" class="tool-result-list">
        <li v-for="(item, index) in listValues" :key="index">{{ item }}</li>
      </ul>
      <dl v-else-if="rows.length" class="tool-result-rows">
        <template v-for="row in rows" :key="row.key">
          <dt>{{ row.key }}</dt>
          <dd>{{ row.value }}</dd>
        </template>
      </dl>
      <pre v-else class="tool-result-raw">{{ rawText }}</pre>
    </template>
  </div>
</template>

<style scoped>
.tool-result { min-width: 0; }

.tool-result-note { margin: 4px 0; font-size: 11px; color: var(--text-chat-muted); }
.tool-result-note.is-error { color: var(--accent-danger); }
.tool-result-note.is-warning { color: var(--accent-warning); }

.tool-result-rows {
  display: grid;
  grid-template-columns: minmax(4rem, auto) 1fr;
  gap: 2px 10px;
  margin: 4px 0;
  font-size: 11px;
}

.tool-result-rows dt { color: var(--text-muted); }
.tool-result-rows dd {
  margin: 0;
  min-width: 0;
  overflow-wrap: anywhere;
  color: var(--text-secondary);
  white-space: pre-wrap;
}

.tool-result-list {
  list-style: none;
  margin: 4px 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-size: 11px;
  color: var(--text-secondary);
}

.tool-result-list li { overflow-wrap: anywhere; }

.tool-result-raw {
  margin: 4px 0;
  max-height: 18rem;
  overflow: auto;
  font-size: 11px;
  line-height: 1.45;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  color: var(--text-secondary);
}
</style>
