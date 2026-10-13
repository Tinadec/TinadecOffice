<script setup lang="ts">
import { Bot, ChevronRight, Maximize2 } from '@lucide/vue'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useUie } from '@tinadec/ui'
import MarkdownRender from '@/components/MarkdownRender.vue'
import { Spinner } from '@/components/ui/spinner'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import { useDelayedLoading } from '@/composables/useDelayedLoading'
import { useNotifications } from '@/composables/useNotifications'
import type { SubAgentNode } from '@/lib/conversation/grouping'
import { selectSubAgent } from '@/lib/conversation/selection'
import { describeEntry } from './describeEntry'
import TurnActivity from './TurnActivity.vue'

/**
 * One sub-agent, compact: its handle and name, a status badge, the task it was given on an
 * L-shaped connector line, and what it is doing now (shimmering while it works). Clicking
 * the card opens its own activity and final summary in place; ⤢ opens the same agent in the
 * right-column "Sub-agents" card. Its approvals are not here — the turn lifts them out.
 */
const props = defineProps<{
  node: SubAgentNode
  runId: string
  workspaceRoot?: string | null
  /** The panel card shows the detail open and has no "open in panel" of its own. */
  expanded?: boolean
  inPanel?: boolean
}>()
const { t } = useI18n()
const { displayName } = useAgentDirectory()
const { notify } = useNotifications()

const open = ref(Boolean(props.expanded))
const agent = computed(() => props.node.agent)
const working = computed(() => props.node.status === 'running' || props.node.status === 'awaiting_approval')
const showSpinner = useDelayedLoading(working)
const name = computed(() => {
  const handle = agent.value.handle
  const display = displayName(agent.value.agentSlug)
  return handle && display && handle.split('#')[0] !== display ? `${handle} · ${display}` : displayName(handle ?? agent.value.agentSlug) || agent.value.taskKey || agent.value.taskId
})
const latest = computed(() => (working.value ? describeEntry(props.node.latest, t, props.workspaceRoot) : null))
const detailId = computed(() => `sub-agent-${agent.value.taskId}`)

function openInPanel() {
  selectSubAgent({ runId: props.runId, taskId: agent.value.taskId })
  let uie: ReturnType<typeof useUie> | null
  try { uie = useUie() } catch { uie = null }
  if (!uie) {
    notify.info(t('conversation.agents.openedInPanel'))
    return
  }
  uie.bus.dispatch({
    command: { type: 'openCard', scope: uie.scope.value, descriptorId: 'subAgents', slotId: 'right', title: t('conversation.agents.panelTitle') },
    source: 'user',
    expectedRevision: uie.snapshot.value.revision,
  })
}
</script>

<template>
  <article class="conversation-card conversation-sub-agent" :data-status="node.status" data-testid="sub-agent-card">
    <header class="conversation-sub-agent-head">
      <button
        type="button"
        class="conversation-sub-agent-toggle"
        :aria-expanded="open"
        :aria-controls="detailId"
        data-testid="sub-agent-toggle"
        @click="open = !open"
      >
        <Spinner decorative v-if="showSpinner" class="conversation-glyph" />
        <Bot v-else class="conversation-glyph" aria-hidden="true" />
        <span class="conversation-sub-agent-name" :class="{ shimmer: working }">{{ name }}</span>
        <span class="conversation-sub-agent-badge" :data-status="node.status">{{ t(`conversation.agents.status.${node.status}`) }}</span>
        <ChevronRight class="conversation-fold-chevron" :class="{ 'is-open': open }" aria-hidden="true" />
      </button>
      <button v-if="!inPanel" type="button" class="conversation-icon-button" :aria-label="t('conversation.agents.openInPanel')" :title="t('conversation.agents.openInPanel')" data-testid="sub-agent-open-panel" @click="openInPanel">
        <Maximize2 :size="12" aria-hidden="true" />
      </button>
    </header>
    <p v-if="agent.title" class="conversation-sub-agent-task"><span class="conversation-sub-agent-connector" aria-hidden="true">└</span>{{ agent.title }}</p>
    <p v-if="latest" class="conversation-sub-agent-latest shimmer" data-testid="sub-agent-latest">{{ latest }}</p>
    <div v-if="open" :id="detailId" class="conversation-sub-agent-detail">
      <TurnActivity v-if="node.nodes.length" :nodes="node.nodes" :run-id="runId" :workspace-root="workspaceRoot" />
      <div v-if="agent.summary" class="message-content assistant conversation-sub-agent-summary" data-testid="sub-agent-summary">
        <MarkdownRender :content="agent.summary" />
      </div>
    </div>
  </article>
</template>

<style scoped>
.conversation-sub-agent { display: flex; flex-direction: column; gap: 2px; padding: 6px 10px; }
.conversation-sub-agent[data-status='completed'] { opacity: 0.85; }
.conversation-sub-agent[data-status='cancelled'] { opacity: 0.6; }
.conversation-sub-agent-head { display: flex; align-items: center; gap: 4px; }
.conversation-sub-agent-toggle { display: flex; flex: 1; align-items: center; gap: 6px; min-width: 0; padding: 2px 0; border: 0; color: inherit; background: transparent; font: inherit; font-size: 12px; text-align: left; cursor: pointer; }
.conversation-sub-agent-toggle:focus-visible { outline: 1px solid hsl(var(--ring)); border-radius: 4px; }
.conversation-sub-agent-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-primary); }
.conversation-sub-agent-badge { flex-shrink: 0; font-size: 11px; color: var(--text-muted); }
.conversation-sub-agent-badge[data-status='running'] { color: var(--accent-primary); }
.conversation-sub-agent-badge[data-status='awaiting_approval'] { color: var(--accent-warning); }
.conversation-sub-agent-badge[data-status='failed'], .conversation-sub-agent-badge[data-status='blocked'] { color: var(--accent-danger); }
.conversation-sub-agent-badge[data-status='completed'] { color: var(--accent-success); }
.conversation-fold-chevron.is-open { transform: rotate(90deg); }
.conversation-sub-agent-task { display: flex; gap: 6px; margin: 0; padding-left: 7px; font-size: 12px; color: var(--text-secondary); overflow-wrap: anywhere; }
.conversation-sub-agent-connector { color: var(--text-muted); }
.conversation-sub-agent-latest { margin: 0; padding-left: 26px; font-size: 11px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.conversation-sub-agent-detail { display: flex; flex-direction: column; gap: 6px; margin-top: 4px; padding-top: 6px; border-top: 1px solid var(--border-card); }
.conversation-sub-agent-summary { font-size: 12px; }
</style>
