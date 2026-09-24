<script setup lang="ts">
import { computed, ref, useId } from 'vue'
import { useI18n } from 'vue-i18n'
import { CheckCircle2, XCircle, Loader2, Clock, ShieldAlert, ChevronRight } from '@lucide/vue'
import { UiCollapsible } from '@/components/ui'
import type { ToolCall } from '@/composables/useAgentActivity'
import TerminalCallBlock from './TerminalCallBlock.vue'

const props = defineProps<{ toolCall: ToolCall; runId?: string | null }>()
const emit = defineEmits<{ approve: [approvalId: string]; reject: [approvalId: string] }>()
const { t } = useI18n()
const expanded = ref(false)
const detailsId = useId()
const statusConfig = computed(() => {
  switch (props.toolCall.status) {
    case 'running': return { icon: Loader2, key: 'running' }
    case 'completed': return { icon: CheckCircle2, key: 'completed' }
    case 'failed': return { icon: XCircle, key: 'failed' }
    case 'waiting_approval': return { icon: ShieldAlert, key: 'waiting' }
    default: return { icon: Clock, key: 'pending' }
  }
})
const durationLabel = computed(() => props.toolCall.durationMs == null ? null
  : props.toolCall.durationMs < 1000 ? `${props.toolCall.durationMs}ms` : `${(props.toolCall.durationMs / 1000).toFixed(1)}s`)
const isRisky = computed(() => ['high', 'critical'].includes(props.toolCall.risk))
const isShellCall = computed(() => props.toolCall.toolId === 'shell')
</script>

<template>
  <article class="tool-call-card" :class="`tool-${statusConfig.key}`">
    <UiCollapsible v-model:open="expanded">
      <template #trigger>
        <button class="tool-call-head" type="button" :aria-expanded="expanded" :aria-controls="detailsId"
          :aria-label="t(expanded ? 'agent.collapseTool' : 'agent.expandTool', { tool: toolCall.toolName })">
          <ChevronRight :size="13" class="tool-call-toggle" :class="{ expanded }" />
          <component :is="statusConfig.icon" :size="13" class="tool-status-glyph"
            :class="{ 'tool-icon-spin': toolCall.status === 'running' }" />
          <strong>{{ toolCall.toolName }}</strong>
          <span v-if="isRisky" class="tool-call-risk-tag">{{ t('agent.highRisk') }}</span>
          <span class="tool-call-args">{{ toolCall.argsSummary }}</span>
          <span v-if="durationLabel" class="tool-call-duration">{{ durationLabel }}</span>
        </button>
      </template>
      <div :id="detailsId" class="tool-call-details">
        <pre v-if="toolCall.argsSummary">{{ toolCall.argsSummary }}</pre>
        <TerminalCallBlock v-if="isShellCall" :execution-id="toolCall.id" :command="toolCall.argsSummary"
          :run-id="runId ?? toolCall.runId" :status="toolCall.status" />
        <p v-if="toolCall.resultSummary && toolCall.resultSummary !== toolCall.argsSummary">{{ toolCall.resultSummary }}</p>
        <ul v-if="toolCall.evidence.length">
          <li v-for="(item, idx) in toolCall.evidence" :key="idx">{{ item }}</li>
        </ul>
      </div>
    </UiCollapsible>
    <!-- Folding output must never hide a pending decision. -->
    <div v-if="toolCall.status === 'waiting_approval' && toolCall.approvalId" class="tool-call-approval">
      <pre v-if="toolCall.argsSummary">{{ toolCall.argsSummary }}</pre>
      <span>{{ t('agent.toolApprovalRequired') }}</span>
      <div class="tool-call-approval-actions">
        <button class="tool-call-approve-btn" type="button" @click="emit('approve', toolCall.approvalId!)">
          <CheckCircle2 :size="12" />{{ t('agent.approve') }}
        </button>
        <button class="tool-call-reject-btn" type="button" @click="emit('reject', toolCall.approvalId!)">
          <XCircle :size="12" />{{ t('agent.reject') }}
        </button>
      </div>
    </div>
  </article>
</template>

<style scoped>
.tool-call-head { display: flex; align-items: center; gap: 6px; width: 100%; min-width: 0; padding: 4px; background: transparent; border: none; border-radius: 4px; color: var(--text-secondary); text-align: left; cursor: pointer; }
.tool-call-head:hover { background: var(--bg-hover); }
.tool-call-head:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 1px; }
.tool-call-head strong { font-size: 12px; font-weight: 500; flex-shrink: 0; }
.tool-call-toggle, .tool-status-glyph { flex-shrink: 0; color: var(--text-muted); }
.tool-call-toggle.expanded { transform: rotate(90deg); }
.tool-running .tool-status-glyph { color: var(--accent-primary); }
.tool-completed .tool-status-glyph { color: var(--accent-success); }
.tool-failed .tool-status-glyph { color: var(--accent-danger); }
.tool-waiting .tool-status-glyph, .tool-call-approval { color: var(--accent-warning); }
.tool-call-args { min-width: 0; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 11px; color: var(--text-chat-muted); }
.tool-call-duration { flex-shrink: 0; font-size: 10px; color: var(--text-muted); }
.tool-call-risk-tag { flex-shrink: 0; font-size: 10px; color: var(--accent-danger); font-weight: 600; }
.tool-call-details { margin: 4px 0 6px 25px; max-height: 18rem; overflow: auto; font-size: 12px; color: var(--text-secondary); }
.tool-call-details p, pre { margin: 4px 0; white-space: pre-wrap; overflow-wrap: anywhere; font-size: 12px; }
.tool-call-approval { margin: 4px 0 8px 25px; font-size: 12px; }
.tool-call-approval-actions { display: flex; gap: 8px; margin-top: 4px; }
.tool-call-approve-btn, .tool-call-reject-btn { display: inline-flex; align-items: center; gap: 4px; padding: 4px 8px; background: transparent; border: 1px solid var(--border-muted); border-radius: 4px; cursor: pointer; }
.tool-call-approve-btn { color: var(--accent-success); }
.tool-call-reject-btn { color: var(--accent-danger); }
.tool-icon-spin { animation: tool-spin 1s linear infinite; }
@keyframes tool-spin { to { transform: rotate(360deg); } }
@media (prefers-reduced-motion: reduce) { .tool-icon-spin { animation: none; } }
</style>
