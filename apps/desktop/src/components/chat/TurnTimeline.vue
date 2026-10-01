<script setup lang="ts">
import { computed, ref } from 'vue'
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
const expanded = ref(false)
const pendingCalls = computed(() => (props.toolCalls ?? []).filter((call) => call.status === 'waiting_approval'))
const detailItems = computed(() => items.value.filter((item) => item.kind !== 'tool' || item.call.status !== 'waiting_approval'))
const activitySummary = computed(() => t('agent.activitySteps', { count: items.value.length }))

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
    <div v-if="items.length" class="activity-summary" data-testid="activity-summary">
      <button class="activity-summary-trigger" type="button" :aria-expanded="expanded" data-testid="activity-toggle" @click="expanded = !expanded">
        <span class="activity-summary-chevron" :class="{ expanded }" aria-hidden="true">›</span>
        <span class="activity-summary-label">{{ activitySummary }}</span>
        <span v-if="pendingCalls.length" class="activity-summary-attention">{{ t('agent.toolApprovalRequired') }}</span>
      </button>
      <div v-if="expanded" class="activity-details" data-testid="activity-details">
        <template v-for="item in detailItems" :key="item.id">
          <ThinkingProcess v-if="item.kind === 'thinking'" :steps="item.steps" />
          <ToolCallCard v-else :tool-call="item.call" :run-id="runId"
            @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
        </template>
      </div>
    </div>
    <template v-for="call in pendingCalls" :key="`attention-${call.id}`">
      <ToolCallCard :tool-call="call" :run-id="runId"
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
.activity-summary { min-width: 0; }
.activity-summary-trigger { display: flex; align-items: center; gap: 6px; width: 100%; min-height: 24px; padding: 2px 4px; border: 0; border-radius: 5px; color: var(--text-muted); background: transparent; font-size: 11px; text-align: left; cursor: pointer; }
.activity-summary-trigger:hover, .activity-summary-trigger:focus-visible { color: var(--text-primary); background: var(--bg-hover); }
.activity-summary-trigger:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 1px; }
.activity-summary-chevron { display: inline-block; width: 12px; color: var(--text-muted); font-size: 18px; line-height: 12px; transition: transform .15s ease; }
.activity-summary-chevron.expanded { transform: rotate(90deg); }
.activity-summary-label { font-weight: 500; }
.activity-summary-attention { margin-left: auto; color: var(--accent-warning); font-size: 10px; }
.activity-details { padding-left: 14px; }
.supervision-review { display: flex; flex-direction: column; gap: 6px; padding: 4px 8px; }
.supervision-reasons { margin: 0; color: var(--text-secondary); font-size: 12px; line-height: 1.5; }
.supervision-actions { display: flex; gap: 8px; flex-wrap: wrap; }
.supervision-action { border: none; background: transparent; color: var(--text-primary); cursor: pointer; font-size: 12px; padding: 2px 0; }
.supervision-action:hover { color: var(--accent); }
@media (prefers-reduced-motion: reduce) { .activity-summary-chevron { transition: none; } }
</style>
