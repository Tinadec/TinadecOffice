<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import { homeController } from '@/controllers/HomeController'
import { useNotifications } from '@/composables/useNotifications'
import ThinkingProcess from './ThinkingProcess.vue'
import ToolCallCard from './ToolCallCard.vue'
import type { SupervisionDecisionOption, TurnActivity } from '@/composables/useAgentActivity'
import { turnTimeline } from '@/lib/turnTimeline'

const props = defineProps<TurnActivity>()
const emit = defineEmits<{ approve: [id: string]; reject: [id: string] }>()
const { notify } = useNotifications()
const { t } = useI18n()
const items = computed(() => turnTimeline(props.thinkingSteps ?? [], props.toolCalls ?? []))

/** The escalation vocabulary is Core's; the label is the user's language. */
const optionLabel = computed<Record<SupervisionDecisionOption, string>>(() => ({
  continue: t('agent.supervisionContinue'),
  correct: t('agent.supervisionCorrect'),
  cancel: t('agent.supervisionCancel'),
}))

async function decideSupervision(option: SupervisionDecisionOption) {
  const runId = props.supervisionReview?.runId
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
  <div class="turn-timeline">
    <template v-for="item in items" :key="item.id">
      <ThinkingProcess v-if="item.kind === 'thinking'" :steps="item.steps" />
      <ToolCallCard v-else :tool-call="item.call" :run-id="runId"
        @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
    </template>
    <div v-if="supervisionReview?.reasons.length || supervisionReview?.options.length" class="supervision-review">
      <p v-if="supervisionReview?.reasons.length" class="supervision-reasons">
        <span data-testid="supervision-reasons">
          <strong>{{ t('agent.supervisionWaiting') }}</strong>{{ supervisionReview.reasons.join('；') }}
        </span>
      </p>
      <div v-if="supervisionReview?.options.length" class="supervision-actions">
        <button v-for="option in supervisionReview.options" :key="option" type="button"
          class="supervision-action" :data-option="option" data-testid="supervision-decision"
          @click="decideSupervision(option)">
          {{ optionLabel[option] }}
        </button>
      </div>
    </div>
  </div>
</template>

<style scoped>
.turn-timeline { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
.supervision-review { display: flex; flex-direction: column; gap: 6px; padding: 4px 8px; }
.supervision-reasons { margin: 0; color: var(--text-secondary); font-size: 12px; line-height: 1.5; }
.supervision-actions { display: flex; gap: 8px; flex-wrap: wrap; }
.supervision-action { border: none; background: transparent; color: var(--text-primary); cursor: pointer; font-size: 12px; padding: 2px 0; }
.supervision-action:hover { color: var(--accent); }
</style>
