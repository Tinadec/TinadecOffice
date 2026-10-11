<script setup lang="ts">
/**
 * One subagent's own block: who it is, what state it is in, and the work it did.
 *
 * The turn's own activity is the spine of the transcript; a subagent is a thing the lead
 * agent dispatched, so it reads as a nested block rather than as more of the same list. Two
 * parallel instances of one role are two cards, because Core gives each a handle (`search#1`,
 * `search#2`) for exactly that reason.
 */
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronRight, Loader2, CheckCircle2, XCircle } from '@lucide/vue'
import ThinkingProcess from './ThinkingProcess.vue'
import ToolCallCard from './ToolCallCard.vue'
import { groupStatus, groupTargets, type SubAgentGroup } from '@/lib/subAgents'

const props = defineProps<{
  group: SubAgentGroup
  defaultOpen?: boolean
  /** Forwarded to the calls this instance made, exactly as the turn forwards them to its own. */
  richViews?: boolean
  allowWidget?: boolean
}>()
const emit = defineEmits<{ approve: [id: string]; reject: [id: string] }>()
const { t } = useI18n()

const expanded = ref(props.defaultOpen ?? true)
const status = computed(() => groupStatus(props.group))
const targets = computed(() => groupTargets(props.group.steps))
const statusIcon = computed(() => status.value === 'running' ? Loader2 : status.value === 'failed' ? XCircle : CheckCircle2)

/** The card's role line: the slug when Core named one, otherwise the handle speaks for itself. */
const roleLabel = computed(() => props.group.slug ?? null)
</script>

<template>
  <article class="subagent-card" :class="`subagent-${status}`" :data-testid="'subagent-card'" :data-handle="group.handle">
    <button
      type="button"
      class="subagent-head"
      :aria-expanded="expanded"
      :aria-label="t(expanded ? 'agent.collapseSubAgent' : 'agent.expandSubAgent', { handle: group.handle })"
      @click="expanded = !expanded"
    >
      <ChevronRight :size="12" class="subagent-chevron" :class="{ expanded }" />
      <component :is="statusIcon" :size="12" class="subagent-status" :class="{ 'is-spinning': status === 'running' }" />
      <span class="subagent-handle">{{ group.handle }}</span>
      <span v-if="roleLabel" class="subagent-role">{{ roleLabel }}</span>
      <span class="subagent-state">{{ t(`agent.subAgentState.${status}`) }}</span>
    </button>
    <div v-if="expanded" class="subagent-body">
      <p v-if="targets.length" class="subagent-targets">{{ targets.join(' · ') }}</p>
      <ThinkingProcess :steps="group.steps" />
      <!-- The calls this instance made, which is what makes the card a record of its work rather
           than only of its thoughts. Pending approvals are deliberately not here — they stay on the
           turn's spine so the decision is never behind a fold. -->
      <ToolCallCard v-for="call in group.calls" :key="call.id" :tool-call="call" :run-id="group.key"
        :rich-views="richViews" :allow-widget="allowWidget"
        @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
    </div>
  </article>
</template>

<style scoped>
/* A nested block, not a card: the transcript has no card language for the assistant's own
   message, so a subagent gets a left rule and a tint rather than a floating surface. */
.subagent-card {
  min-width: 0;
  margin: 2px 0 3px;
  padding-left: 8px;
  border-left: 2px solid color-mix(in srgb, var(--border-muted) 80%, transparent);
}

.subagent-card.subagent-running { border-left-color: color-mix(in srgb, var(--accent-primary) 55%, transparent); }
.subagent-card.subagent-failed { border-left-color: color-mix(in srgb, var(--accent-danger) 55%, transparent); }

.subagent-head {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  min-width: 0;
  min-height: 26px;
  padding: 3px 4px;
  border: 0;
  border-radius: 6px;
  color: var(--text-secondary);
  background: transparent;
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.subagent-head:hover { background: var(--bg-hover); }
.subagent-head:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 1px; }

.subagent-chevron { flex-shrink: 0; color: var(--text-muted); transition: transform 0.15s ease; }
.subagent-chevron.expanded { transform: rotate(90deg); }
.subagent-status { flex-shrink: 0; color: var(--text-muted); }
.subagent-running .subagent-status { color: var(--accent-primary); }
.subagent-failed .subagent-status { color: var(--accent-danger); }
.subagent-done .subagent-status { color: var(--accent-success); }
.subagent-handle { flex-shrink: 0; font-weight: 600; color: var(--text-primary); }
.subagent-role { flex-shrink: 0; color: var(--text-chat-muted); }
.subagent-state { margin-left: auto; flex-shrink: 0; font-size: 10px; color: var(--text-muted); }
.subagent-body { min-width: 0; padding: 2px 0 4px 4px; }
.subagent-targets {
  margin: 2px 0 4px;
  overflow: hidden;
  color: var(--text-chat-muted);
  font-size: 11px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.is-spinning { animation: subagent-spin 1s linear infinite; }
@keyframes subagent-spin { to { transform: rotate(360deg); } }

@media (prefers-reduced-motion: reduce) {
  .subagent-chevron { transition: none; }
  .is-spinning { animation: none; }
}
</style>
