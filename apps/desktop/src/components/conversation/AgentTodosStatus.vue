<script setup lang="ts">
import { ListChecks } from '@lucide/vue'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import type { PlanEntry } from '@/lib/conversation/reducer'
import { isLiveRunStatus, type TurnRunInput } from '@/lib/conversation/turns'
import AgentTodos from './AgentTodos.vue'

/**
 * A capsule above the composer while a run works from a plan: "Plan 2/5 · Writing tests".
 * It shows the newest live run's own plan (a sub-agent's only when the run has none) and
 * opens the full list in place. Nothing is shown once the run settles.
 */
const props = defineProps<{ feed: ConversationFeed; runs: readonly TurnRunInput[] }>()
const { t } = useI18n()
const open = ref(false)

const plan = computed<PlanEntry | null>(() => {
  void props.feed.revision.value
  for (const run of [...props.runs].reverse()) {
    if (!isLiveRunStatus(run.status)) continue
    const activity = props.feed.state.value.runs.get(run.id)
    if (!activity) continue
    const plans = activity.entries.filter((entry): entry is PlanEntry => entry.kind === 'plan' && entry.steps.length > 0)
    const own = plans.filter((entry) => !entry.taskId || activity.tasks.get(entry.taskId)?.isMain)
    const chosen = (own.length ? own : plans).sort((a, b) => a.at.localeCompare(b.at)).at(-1)
    if (chosen) return chosen
  }
  return null
})
const done = computed(() => plan.value?.steps.filter((step) => step.status === 'completed').length ?? 0)
const current = computed(() => plan.value?.steps.find((step) => step.status === 'in_progress')?.step ?? null)
</script>

<template>
  <div v-if="plan" class="conversation-todos-status" data-testid="todos-status">
    <div v-if="open" id="conversation-todos-status-list" class="conversation-card conversation-todos-status-list">
      <AgentTodos :entry="plan" />
    </div>
    <button
      type="button"
      class="conversation-todos-capsule"
      :aria-expanded="open"
      aria-controls="conversation-todos-status-list"
      data-testid="todos-capsule"
      @click="open = !open"
    >
      <ListChecks class="conversation-meta-glyph" aria-hidden="true" />
      <span>{{ t('conversation.plan.capsule', { done, total: plan.steps.length }) }}</span>
      <span v-if="current" class="conversation-todos-capsule-step">· {{ current }}</span>
    </button>
  </div>
</template>

<style scoped>
.conversation-todos-status { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; width: 100%; max-width: 852px; margin: 0 auto; padding: 0 16px 6px; }
:global(.chat-narrow) .conversation-todos-status { padding-inline: 10px; }
:global(.chat-ultra) .conversation-todos-status { padding-inline: 6px; }
.conversation-todos-status-list { width: 100%; max-height: 14rem; overflow: auto; }
.conversation-todos-capsule { display: inline-flex; align-items: center; gap: 6px; max-width: 100%; min-height: 24px; padding: 2px 10px; border: 1px solid var(--border-card); border-radius: 999px; color: var(--text-secondary); background: var(--surface-section); font-size: 11px; font-variant-numeric: tabular-nums; cursor: pointer; }
.conversation-todos-capsule:hover { background: var(--surface-hover); }
.conversation-todos-capsule:focus-visible { outline: 1px solid hsl(var(--ring)); }
.conversation-todos-capsule-step { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-primary); }
</style>
