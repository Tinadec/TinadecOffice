<script setup lang="ts">
import { computed } from 'vue'
import { ShieldAlert } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import { homeController } from '@/controllers/HomeController'
import { useNotifications } from '@/composables/useNotifications'
import { interfaceLocale } from '@/lib/interfaceLocale'
import type { SupervisionDecisionOption, SupervisionReview } from '@/lib/conversation/supervision'

/**
 * A run that supervision escalated to the user: why, and the decisions Core offers.
 * Shared by the space-mode TurnTimeline and the flat conversation feed so both answer
 * an escalation the same way.
 */
const props = defineProps<{ review?: SupervisionReview | null }>()
const { notify } = useNotifications()
const { t } = useI18n()

/** The escalation vocabulary is Core's; the label is the user's language. */
const optionLabel = computed<Record<SupervisionDecisionOption, string>>(() => ({
  continue: t('agent.supervisionContinue'),
  correct: t('agent.supervisionCorrect'),
  cancel: t('agent.supervisionCancel'),
}))

const brief = computed(() => props.review?.brief ?? null)

const summary = computed(() => {
  const value = brief.value
  if (!value) return []
  const lines = [t('conversation.supervision.brief.tasks', {
    completed: value.completedTasks,
    total: value.totalTasks,
  })]
  if (value.blockedTasks) lines.push(t('conversation.supervision.brief.blocked', { count: value.blockedTasks }))
  if (value.failedTasks) lines.push(t('conversation.supervision.brief.failed', { count: value.failedTasks }))
  if (value.pendingTasks) lines.push(t('conversation.supervision.brief.pending', { count: value.pendingTasks }))
  if (value.unmetCriteria) lines.push(t('conversation.supervision.brief.criteria', { count: value.unmetCriteria }))
  if (value.supervisorRequired && !value.supervisorAvailable) lines.push(t('conversation.supervision.brief.noSupervisor'))
  if (value.maxRevisionRounds > 0 && value.revisionRound >= value.maxRevisionRounds) {
    lines.push(t('conversation.supervision.brief.budget', {
      current: value.revisionRound,
      max: value.maxRevisionRounds,
    }))
  }
  return lines
})

const guidance = computed(() => {
  const value = brief.value
  if (!value) return null
  if (value.completedTasks === 0) return t('conversation.supervision.brief.recommendNoCompleted')
  if (value.unmetCriteria > 0) return t('conversation.supervision.brief.recommendCriteria')
  return t('conversation.supervision.brief.recommendPartial')
})

function optionHint(option: SupervisionDecisionOption): string {
  return t(`conversation.supervision.brief.option.${option}`)
}

async function decide(option: SupervisionDecisionOption) {
  const runId = props.review?.runId
  const sessionId = homeController.currentSession.value?.id
  if (!runId || !sessionId) return
  try {
    if (option === 'correct') {
      const content = window.prompt(t('agent.supervisionCorrectionPrompt'))
      if (!content?.trim()) return
      await api.createInteraction(sessionId, {
        content: content.trim(),
        client_message_id: crypto.randomUUID(),
        dispatch_mode: 'insert',
        target_run_id: runId,
        interface_locale: interfaceLocale(),
      })
    } else {
      await api.controlRun(runId, option === 'continue' ? 'resume' : 'cancel')
    }
  } catch (error) {
    notify.error(error, { title: t('agent.supervisionDecisionFailed') })
  }
}
</script>

<template>
  <div v-if="review?.reasons.length || review?.options.length || review?.brief" class="supervision-review" data-testid="supervision-gate">
    <div v-if="brief" class="supervision-brief" data-testid="supervision-brief">
      <div class="supervision-reasons-heading">
        <ShieldAlert :size="14" aria-hidden="true" />
        <strong>{{ t('conversation.supervision.brief.title') }}</strong>
      </div>
      <ul class="supervision-summary">
        <li v-for="line in summary" :key="line">{{ line }}</li>
      </ul>
      <p v-if="guidance" class="supervision-guidance">{{ guidance }}</p>
      <details v-if="review?.reasons.length" class="supervision-details">
        <summary>{{ t('conversation.supervision.brief.details') }}</summary>
        <span>{{ review.reasons.join(t('activity.reasonSeparator')) }}</span>
      </details>
    </div>
    <div v-else-if="review?.reasons.length" class="supervision-reasons" data-testid="supervision-reasons">
      <div class="supervision-reasons-heading">
        <ShieldAlert :size="14" aria-hidden="true" />
        <strong>{{ t('agent.supervisionWaiting') }}</strong>
      </div>
      <span>{{ review.reasons.join(t('activity.reasonSeparator')) }}</span>
    </div>
    <div v-if="review?.options.length" class="supervision-actions">
      <button v-for="option in review.options" :key="option" type="button"
        class="supervision-action" :data-option="option" data-testid="supervision-decision"
        @click="decide(option)">
        <span>{{ optionLabel[option] }}</span>
        <small>{{ optionHint(option) }}</small>
      </button>
    </div>
  </div>
</template>

<style scoped>
.supervision-review { display: flex; flex-direction: column; gap: 9px; margin-top: 4px; padding: 10px 12px; border: 1px solid color-mix(in srgb, var(--accent-warning) 38%, var(--border-muted)); border-radius: 8px; background: color-mix(in srgb, var(--accent-warning) 8%, var(--surface-raised)); }
.supervision-brief { display: flex; flex-direction: column; gap: 6px; color: var(--text-secondary); font-size: 12px; line-height: 1.45; }
.supervision-reasons { display: flex; flex-direction: column; gap: 3px; margin: 0; color: var(--text-secondary); font-size: 12px; line-height: 1.45; }
.supervision-reasons-heading { display: flex; align-items: center; gap: 6px; color: var(--accent-warning); }
.supervision-reasons strong { font-weight: 600; }
.supervision-summary { display: flex; flex-direction: column; gap: 2px; margin: 0; padding-left: 18px; font-variant-numeric: tabular-nums; }
.supervision-guidance { margin: 0; color: var(--text-primary); }
.supervision-details { color: var(--text-muted); }
.supervision-details summary { width: fit-content; cursor: pointer; border-radius: 4px; }
.supervision-details summary:focus-visible { outline: 1px solid hsl(var(--ring)); }
.supervision-details > span { display: block; margin-top: 4px; white-space: pre-wrap; overflow-wrap: anywhere; }
.supervision-actions { display: flex; gap: 6px; flex-wrap: wrap; }
.supervision-action { display: flex; flex-direction: column; align-items: flex-start; gap: 2px; min-height: 28px; max-width: 260px; padding: 5px 10px; border: 1px solid var(--border-muted); border-radius: 6px; color: var(--text-primary); background: var(--surface-raised); cursor: pointer; font-size: 12px; text-align: left; }
.supervision-action > span { font-weight: 500; white-space: nowrap; }
.supervision-action small { color: var(--text-muted); font-size: 10px; line-height: 1.35; }
.supervision-action:hover { border-color: var(--accent-primary); background: var(--surface-hover); }
.supervision-action:focus-visible { outline: 1px solid hsl(var(--ring)); border-color: var(--accent-primary); }
</style>
