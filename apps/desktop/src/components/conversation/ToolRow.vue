<script setup lang="ts">
import { Check, ChevronRight, SquareTerminal, X } from '@lucide/vue'
import { computed, ref, toRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useUie } from '@tinadec/ui'
import { api } from '@/api'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'
import { Spinner } from '@/components/ui/spinner'
import { homeController } from '@/controllers/HomeController'
import { useDelayedLoading } from '@/composables/useDelayedLoading'
import { useElapsed } from '@/composables/useElapsed'
import { useNotifications } from '@/composables/useNotifications'
import { useTerminal } from '@/composables/useTerminal'
import { createAgentTerminalSource } from '@/composables/useTerminalSource'
import { useToolPreview } from '@/composables/useToolPreview'
import { feedLocale, formatDuration } from '@/lib/conversation/format'
import type { ToolEntry } from '@/lib/conversation/reducer'
import { displayPath, entrySentence, verbKey, type VerbPhase } from '@/lib/conversation/toolSentence'
import ToolPreview from './ToolPreview.vue'

/**
 * One tool call as a sentence: "Ran npm test ✓ 1.2s". Opening it shows the redacted
 * arguments, the live shell tail from the event stream, the files it changed and Core's
 * bounded result preview — fetched only when opened and only once the call has finished.
 * A shell call is never rendered with an inline xterm here; "Open in terminal panel"
 * hands its session to the panel terminal instead.
 */
const props = defineProps<{ entry: ToolEntry; runId: string; workspaceRoot?: string | null }>()
const { t, locale } = useI18n()
const { notify } = useNotifications()
const { openAgentTerminal } = useTerminal()

const lang = computed(() => feedLocale(locale.value))
const sentence = computed(() => entrySentence(props.entry, props.workspaceRoot))
const phase = computed<VerbPhase>(() => {
  const { status, toolSuccess } = props.entry
  if (status === 'failed' || status === 'outcome_unknown' || status === 'cancelled' || toolSuccess === false) return 'failed'
  return status === 'completed' ? 'done' : 'active'
})
const running = computed(() => phase.value === 'active')
const showSpinner = useDelayedLoading(running)
const elapsed = useElapsed(toRef(() => props.entry.startedAt), toRef(() => props.entry.endedAt))
const subject = computed(() => {
  const value = sentence.value.subject ?? props.entry.toolId
  return sentence.value.location ? `${value} · ${sentence.value.location}` : value
})
const failure = computed(() => {
  if (phase.value !== 'failed') return ''
  if (props.entry.exitCode !== null && props.entry.exitCode !== 0) return t('conversation.tool.exitCode', { code: props.entry.exitCode })
  return props.entry.errorCategory ?? ''
})
const isShell = computed(() => sentence.value.verb === 'run')

const open = ref(false)
const { preview, loading, error, load } = useToolPreview(toRef(() => props.entry.id), toRef(() => props.entry.status))
const previewSpinner = useDelayedLoading(loading)
watch([open, () => props.entry.status], ([isOpen]) => { if (isOpen) void load() })

const tail = computed(() => {
  const lines = props.entry.outputPartial ? [...props.entry.output, props.entry.outputPartial] : props.entry.output
  return lines.slice(-20).join('\n')
})

function openInTerminal() {
  const sessionId = props.entry.terminalSessionId
  if (!sessionId) {
    notify.info(t('conversation.tool.terminalNotReady'))
    return
  }
  // The panel has no history of its own: seed it with the tail this row holds, then it follows live frames.
  const replay = [{
    v: '1.0', type: 'terminal.stdout', run_id: props.runId, session_id: homeController.currentSession.value?.id ?? null,
    request_id: '', trace_id: '', seq: 0, ts: props.entry.startedAt, capabilities: [],
    payload: { terminal_session_id: sessionId, execution_id: props.entry.id, data: tail.value ? `${tail.value}\n` : '' },
  }]
  openAgentTerminal({
    terminalSessionId: sessionId,
    runId: props.runId,
    command: props.entry.command ?? undefined,
    title: props.entry.command?.slice(0, 40) || t('conversation.tool.terminalTitle'),
    source: createAgentTerminalSource({
      terminalSessionId: sessionId,
      runId: props.runId,
      feed: { subscribe: (handler) => homeController.onEvent(handler), replay: () => replay },
      sendStdin: (id, data) => api.sendTerminalStdin(id, data),
      kill: (id) => api.killTerminalSession(id),
    }),
  })
  // A singleton set up by the layout engine; resolved at click time, absent on hosts without it.
  let uie: ReturnType<typeof useUie> | null
  try { uie = useUie() } catch { uie = null }
  if (!uie) {
    notify.info(t('conversation.tool.terminalOpenedInList'))
    return
  }
  uie.bus.dispatch({
    command: { type: 'openCard', scope: uie.scope.value, descriptorId: 'terminal', slotId: 'right', title: props.entry.command?.slice(0, 24) || t('conversation.tool.terminalTitle') },
    source: 'user',
    expectedRevision: uie.snapshot.value.revision,
  })
}
</script>

<template>
  <Collapsible v-model:open="open" class="conversation-tool" :data-phase="phase" data-testid="tool-row">
    <CollapsibleTrigger class="conversation-fold-trigger">
      <Spinner decorative v-if="showSpinner" class="conversation-glyph" />
      <X v-else-if="phase === 'failed'" class="conversation-glyph is-failed" aria-hidden="true" />
      <component :is="sentence.icon" v-else class="conversation-glyph" aria-hidden="true" />
      <span class="conversation-tool-sentence" data-testid="tool-sentence">{{ t(verbKey(sentence.verb, phase), { subject }) }}</span>
      <span class="conversation-fold-meta" :class="{ 'is-failed': phase === 'failed' }">
        <Check v-if="phase === 'done'" class="conversation-meta-glyph is-done" aria-hidden="true" />
        <template v-if="failure">{{ failure }}</template>
        <template v-if="elapsed !== null && (running || phase === 'done')">{{ formatDuration(elapsed, lang) }}</template>
      </span>
      <ChevronRight class="conversation-fold-chevron" aria-hidden="true" />
    </CollapsibleTrigger>
    <CollapsibleContent class="conversation-fold-content">
      <div class="conversation-tool-detail">
        <p v-if="entry.argumentsSummary" class="conversation-tool-args">{{ entry.argumentsSummary }}</p>
        <pre v-if="isShell && tail && (running || !preview?.preview)" class="conversation-code" data-testid="tool-live-tail">{{ tail }}</pre>
        <ul v-if="entry.changedPaths.length" class="conversation-tool-changes" role="list" :aria-label="t('conversation.tool.changedFiles')">
          <li v-for="path in entry.changedPaths" :key="path">{{ displayPath(path, workspaceRoot) }}</li>
        </ul>
        <p v-if="previewSpinner" class="conversation-preview-caption"><Spinner decorative class="conversation-glyph" /> {{ t('conversation.tool.preview.loading') }}</p>
        <p v-else-if="error" class="conversation-preview-caption">{{ t('conversation.tool.preview.failed') }}
          <button type="button" class="conversation-link-button" @click="load">{{ t('common.refresh') }}</button>
        </p>
        <ToolPreview v-if="preview" :preview="preview" :workspace-root="workspaceRoot" />
        <button v-if="isShell && entry.terminalSessionId" type="button" class="conversation-link-button" data-testid="open-in-terminal" @click="openInTerminal">
          <SquareTerminal class="conversation-meta-glyph" aria-hidden="true" /> {{ t('conversation.tool.openInTerminal') }}
        </button>
      </div>
    </CollapsibleContent>
  </Collapsible>
</template>

<style scoped>
.conversation-tool-sentence { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.conversation-tool-detail { display: flex; flex-direction: column; gap: 6px; padding: 2px 0 6px 20px; margin-left: 6px; border-left: 1px solid var(--border-card); }
.conversation-tool-args { margin: 0; font-size: 11px; color: var(--text-muted); overflow-wrap: anywhere; }
.conversation-tool-changes { margin: 0; padding: 0; list-style: none; font-size: 12px; color: var(--text-secondary); }
.conversation-preview-caption { display: flex; align-items: center; gap: 6px; margin: 0; font-size: 11px; color: var(--text-muted); }
</style>
