<script setup lang="ts">
import { computed } from 'vue'
import { ShieldAlert } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import { homeController } from '@/controllers/HomeController'
import { useNotifications } from '@/composables/useNotifications'
import type { SupervisionDecisionOption, SupervisionReview } from '@/composables/useAgentActivity'

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
      })
    } else {
      // The escalation options are Core's vocabulary (continue/correct/cancel); the
      // run-control endpoint only accepts pause|resume|cancel. Sending "continue"
      // verbatim was rejected as INVALID_RUN_CONTROL, so the button did nothing.
      await api.controlRun(runId, option === 'continue' ? 'resume' : 'cancel')
    }
  } catch (error) {
    notify.error(error, { title: t('agent.supervisionDecisionFailed') })
  }
}
</script>

<template>
  <div v-if="review?.reasons.length || review?.options.length" class="supervision-review" data-testid="supervision-gate">
    <div v-if="review?.reasons.length" class="supervision-reasons" data-testid="supervision-reasons">
      <div class="supervision-reasons-heading"><ShieldAlert :size="14" aria-hidden="true" /><strong>{{ t('agent.supervisionWaiting') }}</strong></div>
      <span>{{ review.reasons.join('；') }}</span>
    </div>
    <div v-if="review?.options.length" class="supervision-actions">
      <button v-for="option in review.options" :key="option" type="button"
        class="supervision-action" :data-option="option" data-testid="supervision-decision"
        @click="decide(option)">
        {{ optionLabel[option] }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.supervision-review { display: flex; flex-direction: column; gap: 9px; margin-top: 4px; padding: 10px 12px; border: 1px solid color-mix(in srgb, var(--accent-warning) 38%, var(--border-muted)); border-radius: 8px; background: color-mix(in srgb, var(--accent-warning) 8%, var(--surface-raised)); }
.supervision-reasons { display: flex; flex-direction: column; gap: 3px; margin: 0; color: var(--text-secondary); font-size: 12px; line-height: 1.45; }
.supervision-reasons-heading { display: flex; align-items: center; gap: 6px; color: var(--accent-warning); }
.supervision-reasons strong { font-weight: 600; }
.supervision-actions { display: flex; gap: 6px; flex-wrap: wrap; }
.supervision-action { min-height: 26px; padding: 4px 10px; border: 1px solid var(--border-muted); border-radius: 6px; color: var(--text-primary); background: var(--surface-raised); cursor: pointer; font-size: 12px; }
.supervision-action:hover { border-color: var(--accent-primary); background: var(--surface-hover); }
</style>
