<script setup lang="ts">
import { ChevronRight, Maximize2 } from '@lucide/vue'
import { computed, toRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { useUie } from '@tinadec/ui'
import MarkdownRender from '@/components/MarkdownRender.vue'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { instanceKeyOf } from '@/lib/avatarAssignment'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import { useAutoDisclosure } from '@/composables/useAutoDisclosure'
import { useDelayedLoading } from '@/composables/useDelayedLoading'
import { useElapsed } from '@/composables/useElapsed'
import { useNotifications } from '@/composables/useNotifications'
import { formatDuration } from '@/lib/conversation/format'
import type { SubAgentNode } from '@/lib/conversation/grouping'
import { selectSubAgent } from '@/lib/conversation/selection'
import { conversationFlowRules } from '@/lib/conversationFlowStyle'
import { describeEntry } from './describeEntry'
import TurnActivity from './TurnActivity.vue'

/**
 * One sub-agent, compact: its handle and name, how long it has been going, the task it was
 * given on an L-shaped connector line, what it is doing now (shimmering while it works) and,
 * in the fuller transcript shapes, the first line of what it reported back.
 *
 * How much of that a row says is the reader's choice (Settings → General): `name` is the
 * handle alone, `task` adds the task and the time, `result` adds the result. The same choice
 * decides whether the card opens its own activity in place or only in the right-column card;
 * its approvals are never here — the turn lifts them out.
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
const { nameFor } = useAgentDirectory()
const { notify } = useNotifications()

const rules = conversationFlowRules
const agent = computed(() => props.node.agent)
const working = computed(() => props.node.status === 'running' || props.node.status === 'awaiting_approval')
const showSpinner = useDelayedLoading(working)
const name = computed(() => {
  const handle = agent.value.handle
  const display = nameFor(agent.value.agentSlug)
  return handle && display && handle.split('#')[0] !== display ? `${handle} · ${display}` : nameFor(handle ?? agent.value.agentSlug) || agent.value.taskKey || agent.value.taskId
})
const latest = computed(() => (working.value ? describeEntry(props.node.latest, t, props.workspaceRoot) : null))

const elapsed = useElapsed(toRef(() => agent.value.startedAt), toRef(() => (working.value ? null : agent.value.endedAt)))
/** The task line and the time belong to every shape that says more than the handle. */
const detailed = computed(() => rules.value.subAgent !== 'name')
/**
 * The face is a left rail, not an icon in the title row. A full-body sprite at 18px was an
 * unreadable blob, so it grows with the card's density: a minimal row keeps a small mark, a
 * detailed row gives the character the height of the name, task and current-step lines.
 */
const faceSize = computed(() => (detailed.value ? 34 : 22))
const duration = computed(() => (detailed.value && elapsed.value !== null ? formatDuration(elapsed.value, t) : null))
/** A finished agent's own words, cut to the one line a row can hold. */
const result = computed(() => {
  if (rules.value.subAgent !== 'result' || working.value) return null
  const summary = agent.value.summary
  return summary ? firstLine(summary) : null
})

/** The first non-empty line, without the markdown marks that read as noise at one line wide. */
function firstLine(text: string): string {
  const line = text.split('\n').find((part) => part.trim()) ?? ''
  return line.replace(/^\s*(?:[#>]+\s*|[-*+]\s+)/, '').replace(/[*`_]+/g, '').trim()
}

/**
 * In place when the shape allows it; the panel card is always expandable because it *is* the
 * detail, and the fuller shapes let the running agent open itself so its steps are visible
 * without a click.
 */
const expandable = computed(() => props.expanded === true || props.inPanel === true || rules.value.subAgentDetail === 'inline')
const open = useAutoDisclosure(toRef(() => props.expanded === true || (rules.value.subAgentOpen === 'running' && working.value))).open

const detailId = computed(() => `sub-agent-${agent.value.taskId}`)

function toggle() {
  if (!expandable.value) return
  open.value = !open.value
}

function openInPanel() {
  selectSubAgent({ runId: props.runId, taskId: agent.value.taskId })
  let uie: ReturnType<typeof useUie> | null
  try { uie = useUie() } catch { uie = null }
  if (!uie) {
    notify.info(t('conversation.agents.openedInPanel'))
    return
  }
  uie.bus.dispatch({
    // Sub-agents are a section of the agent activity card (the one place that lists agents).
    command: { type: 'openCard', scope: uie.scope.value, descriptorId: 'agent', slotId: 'right', title: t('context.homeAgent') },
    source: 'user',
    expectedRevision: uie.snapshot.value.revision,
  })
}
</script>

<template>
  <article class="conversation-card conversation-sub-agent" :data-status="node.status" data-testid="sub-agent-card">
    <div class="conversation-sub-agent-row">
      <span class="conversation-sub-agent-face" :class="{ spinning: showSpinner }">
        <AgentPetAvatar
          :instance-key="instanceKeyOf(runId, agent.taskId)"
          :agent-id="agent.agentSlug"
          :size="faceSize"
          :alt="name"
        />
      </span>
      <div class="conversation-sub-agent-body">
        <header class="conversation-sub-agent-head">
          <!-- A row that cannot open anything in place is a line of text, not a button that does nothing. -->
          <component
            :is="expandable ? 'button' : 'div'"
            :type="expandable ? 'button' : undefined"
            class="conversation-sub-agent-toggle"
            :aria-expanded="expandable ? open : undefined"
            :aria-controls="expandable ? detailId : undefined"
            data-testid="sub-agent-toggle"
            @click="toggle"
          >
            <span class="conversation-sub-agent-name" :class="{ shimmer: working }">{{ name }}</span>
            <span v-if="duration" class="conversation-sub-agent-time">{{ duration }}</span>
            <span class="conversation-sub-agent-badge" :data-status="node.status">{{ t(`conversation.agents.status.${node.status}`) }}</span>
            <ChevronRight v-if="expandable" class="conversation-fold-chevron" :class="{ 'is-open': open }" aria-hidden="true" />
          </component>
          <button v-if="!inPanel" type="button" class="conversation-icon-button" :aria-label="t('conversation.agents.openInPanel')" :title="t('conversation.agents.openInPanel')" data-testid="sub-agent-open-panel" @click="openInPanel">
            <Maximize2 :size="12" aria-hidden="true" />
          </button>
        </header>
        <p v-if="detailed && agent.title" class="conversation-sub-agent-task"><span class="conversation-sub-agent-connector" aria-hidden="true">└</span>{{ agent.title }}</p>
        <p v-if="latest" class="conversation-sub-agent-latest shimmer" data-testid="sub-agent-latest">{{ latest }}</p>
        <p v-else-if="result" class="conversation-sub-agent-result" data-testid="sub-agent-result">{{ result }}</p>
      </div>
    </div>
    <div v-if="open" :id="detailId" class="conversation-sub-agent-detail" :class="{ 'is-capped': !inPanel }" data-testid="sub-agent-detail">
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
/* The face is a left rail so the character can be tall enough to read, not a 18px title icon. */
.conversation-sub-agent-row { display: flex; align-items: flex-start; gap: 8px; }
.conversation-sub-agent-body { display: flex; flex: 1; flex-direction: column; gap: 2px; min-width: 0; }
.conversation-sub-agent-head { display: flex; align-items: center; gap: 4px; }
/* While the agent works it breathes instead of spinning. */
.conversation-sub-agent-face { display: inline-flex; flex: none; padding-top: 1px; }
.conversation-sub-agent-face.spinning { animation: sub-agent-face-pulse 1.4s ease-in-out infinite; }
@keyframes sub-agent-face-pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.55; } }
@media (prefers-reduced-motion: reduce) { .conversation-sub-agent-face.spinning { animation: none; } }
.conversation-sub-agent-toggle { display: flex; flex: 1; align-items: center; gap: 6px; min-width: 0; padding: 2px 0; border: 0; color: inherit; background: transparent; font: inherit; font-size: 12px; text-align: left; cursor: pointer; }
div.conversation-sub-agent-toggle { cursor: default; }
.conversation-sub-agent-toggle:focus-visible { outline: 1px solid hsl(var(--ring)); border-radius: 4px; }
.conversation-sub-agent-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-primary); }
.conversation-sub-agent-time { flex-shrink: 0; white-space: nowrap; font-size: 11px; color: var(--text-muted); font-variant-numeric: tabular-nums; }
.conversation-sub-agent-badge { flex-shrink: 0; white-space: nowrap; font-size: 11px; color: var(--text-muted); }
.conversation-sub-agent-badge[data-status='running'] { color: var(--accent-primary); }
.conversation-sub-agent-badge[data-status='awaiting_approval'] { color: var(--accent-warning); }
.conversation-sub-agent-badge[data-status='failed'], .conversation-sub-agent-badge[data-status='blocked'] { color: var(--accent-danger); }
.conversation-sub-agent-badge[data-status='completed'] { color: var(--accent-success); }
.conversation-fold-chevron.is-open { transform: rotate(90deg); }
.conversation-sub-agent-task { display: flex; gap: 6px; margin: 0; font-size: 12px; color: var(--text-secondary); overflow-wrap: anywhere; }
.conversation-sub-agent-connector { color: var(--text-muted); }
.conversation-sub-agent-latest { margin: 0; padding-left: 14px; font-size: 11px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.conversation-sub-agent-result { margin: 0; padding-left: 14px; font-size: 11px; color: var(--text-secondary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.conversation-sub-agent-detail { display: flex; flex-direction: column; gap: 6px; margin-top: 4px; padding-top: 6px; border-top: 1px solid var(--border-card); }
/*
 * In the flow, a sub-agent's own steps stop at 20rem and scroll inside. Without the cap one
 * thorough agent pushes its answer three screens down; `overscroll-behavior: contain` keeps a
 * wheel gesture from chaining into the message scroller once it reaches the end either way.
 * The panel card is already a scroll container, so it never caps.
 */
.conversation-sub-agent-detail.is-capped { max-height: 20rem; overflow-y: auto; overscroll-behavior: contain; }
.conversation-sub-agent-summary { font-size: 12px; }
</style>
