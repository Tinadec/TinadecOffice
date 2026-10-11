<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ListChecks, ShieldAlert } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import { homeController } from '@/controllers/HomeController'
import { useNotifications } from '@/composables/useNotifications'
import ThinkingProcess from './ThinkingProcess.vue'
import ToolCallCard from './ToolCallCard.vue'
import ToolGroupSummary from './ToolGroupSummary.vue'
import SubAgentCard from './SubAgentCard.vue'
import TurnMarker from './TurnMarker.vue'
import type { SupervisionDecisionOption, TurnActivity } from '@/composables/useAgentActivity'
import { attentionRows, detailRows, markerRows, turnRows } from '@/lib/turnTimeline'
import { subAgentGroups } from '@/lib/subAgents'

const props = defineProps<TurnActivity & {
  /**
   * False while the run is still working. The trail opens while the agent works and folds
   * to a summary once it answers — but only until the reader touches it, after which their
   * choice stands and the turn stops moving under them.
   */
  settled?: boolean
  /** False under the "concise" template; withholds the rich tool-result views. */
  richToolViews?: boolean
  /** True only under the "information" template; reaches the sandboxed widget frame. */
  allowToolWidget?: boolean
}>()
const emit = defineEmits<{ approve: [id: string]; reject: [id: string] }>()
const { notify } = useNotifications()
const { t } = useI18n()

/**
 * Which steps belong to a dispatched subagent rather than to the turn itself. Only work Core
 * explicitly attributed to an instance is lifted out; the rest stays on the turn's spine.
 */
const split = computed(() => subAgentGroups(props.thinkingSteps ?? [], props.toolCalls ?? []))
// The spine, not the full list: an attributed plan or thought is drawn inside its subagent's card,
// and feeding it to the turn's own rows as well put the same step on screen twice.
const rows = computed(() => turnRows(split.value.spine, split.value.ownCalls))
const details = computed(() => detailRows(rows.value))
const pending = computed(() => attentionRows(rows.value))
const markers = computed(() => markerRows(rows.value))
const subAgents = computed(() => split.value.groups)

/**
 * `null` means "follow the turn's own state". Once the reader opens or closes it themselves
 * the choice is pinned, so a long turn does not snap shut under someone who is reading it.
 */
const manual = ref<boolean | null>(null)
const expanded = computed(() => manual.value ?? !props.settled)
// A new turn is a new thing to look at; carrying the previous one's pinned choice over would
// open or close a turn the reader never touched.
watch(() => props.runId, () => { manual.value = null })

const pendingCount = computed(() => pending.value.length)
const failedCount = computed(() => (props.toolCalls ?? []).filter((call) => call.status === 'failed').length)
// A pending call is lifted out of the fold wherever it sits, including one a subagent made: the
// decision is the reader's to make regardless of which instance asked for it.
const isWorking = computed(() => (props.thinkingSteps ?? []).some((step) => step.status === 'running')
  || (props.toolCalls ?? []).some((call) => call.status === 'running' || call.status === 'pending'))

const activityState = computed(() => pendingCount.value
  ? t('agent.activityWaitingApproval')
  : (props.toolCalls ?? []).some((call) => call.status === 'running')
    ? t('agent.activityUsingTools')
    : isWorking.value ? t('agent.activityThinking') : '')

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
    <!-- `details.length || subAgents.length`, not just `details`: once attributed steps move into a
         subagent card, a turn whose only activity is that subagent has no details of its own and
         would otherwise render nothing at all. -->
    <div v-if="details.length || subAgents.length" class="activity-summary" data-testid="activity-summary">
      <button class="activity-summary-trigger" type="button" :aria-expanded="expanded" data-testid="activity-toggle" @click="manual = !expanded">
        <ListChecks :size="13" class="activity-summary-icon" aria-hidden="true" />
        <span class="activity-summary-chevron" :class="{ expanded }" aria-hidden="true">›</span>
        <span class="activity-summary-label">{{ t('agent.activity') }}</span>
        <span class="activity-summary-count">{{ t('agent.activityCount', { count: details.length + subAgents.length }) }}</span>
        <span v-if="failedCount" class="activity-summary-failed">{{ t('agent.activityFailed', { count: failedCount }) }}</span>
        <span v-if="activityState" class="activity-summary-state">{{ activityState }}</span>
      </button>
      <!-- Rows stay mounted while collapsed so they remain findable and the height
           transition has something to animate; `inert` keeps them out of the tab order
           and off the pointer, which a zero-height region would otherwise still capture. -->
      <div class="activity-collapse" :class="{ open: expanded }" :inert="!expanded" data-testid="activity-details">
        <div>
          <div class="activity-details">
            <template v-for="row in details" :key="row.id">
              <ThinkingProcess v-if="row.kind === 'thinking'" :steps="row.steps" />
              <ToolGroupSummary v-else-if="row.kind === 'toolGroup'" :calls="row.calls" />
              <ToolCallCard v-else-if="row.kind === 'tool'" :tool-call="row.call" :run-id="runId"
                :rich-views="richToolViews" :allow-widget="allowToolWidget"
                @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
            </template>
            <!-- A dispatched instance keeps its own block, after the turn's own rows. -->
            <SubAgentCard v-for="group in subAgents" :key="group.key" :group="group"
              :rich-views="richToolViews" :allow-widget="allowToolWidget"
              @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
          </div>
        </div>
      </div>
    </div>

    <!-- Lifecycle notes stay visible: a run that failed or was cancelled is why the turn
         ended, and a fold must not be the thing that hides it. -->
    <TurnMarker v-for="row in markers" :key="`marker-${row.id}`" :severity="row.step.severity">
      <strong v-if="row.step.title">{{ row.step.title }}</strong>
      <template v-if="row.step.title && row.step.description"> · </template>
      {{ row.step.description }}
    </TurnMarker>

    <!-- A decision is never behind a fold. This is the one region that must stay in view
         however the turn is collapsed, so it renders outside the collapsible element. -->
    <template v-for="row in pending" :key="`attention-${row.id}`">
      <ToolCallCard :tool-call="row.call" :run-id="runId" :rich-views="richToolViews" :allow-widget="allowToolWidget"
        @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
    </template>
    <div v-if="supervisionReview?.reasons.length || supervisionReview?.options.length" class="supervision-review">
      <div v-if="supervisionReview?.reasons.length" class="supervision-reasons" data-testid="supervision-reasons">
        <div class="supervision-reasons-heading"><ShieldAlert :size="14" aria-hidden="true" /><strong>{{ t('agent.supervisionWaiting') }}</strong></div>
        <span>{{ supervisionReview.reasons.join('；') }}</span>
      </div>
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
.activity-summary { min-width: 0; overflow: hidden; }
.activity-summary-trigger { display: flex; align-items: center; gap: 6px; width: 100%; min-height: 28px; padding: 4px 2px; border: 0; border-radius: 6px; color: var(--text-secondary); background: transparent; font-size: 11px; text-align: left; cursor: pointer; }
.activity-summary-trigger:hover, .activity-summary-trigger:focus-visible { color: var(--text-primary); background: var(--surface-hover); }
.activity-summary-trigger:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 1px; }
.activity-summary-chevron { display: inline-block; width: 12px; color: var(--text-muted); font-size: 18px; line-height: 12px; transition: transform .15s ease; }
.activity-summary-chevron.expanded { transform: rotate(90deg); }
.activity-summary-icon { flex: 0 0 auto; color: var(--accent-primary); }
.activity-summary-label { font-weight: 500; }
.activity-summary-count { color: var(--text-muted); font-variant-numeric: tabular-nums; }
.activity-summary-failed { color: var(--accent-danger); font-variant-numeric: tabular-nums; }
.activity-summary-state { min-width: 0; margin-left: auto; overflow: hidden; color: var(--text-muted); font-size: 10px; text-overflow: ellipsis; white-space: nowrap; }

/* Borderless expand/collapse: animate grid-template-rows 0fr↔1fr rather than max-height. */
.activity-collapse { display: grid; grid-template-rows: 0fr; opacity: 0; transition: grid-template-rows 0.2s cubic-bezier(0.19, 1, 0.22, 1), opacity 0.2s cubic-bezier(0.19, 1, 0.22, 1); }
.activity-collapse.open { grid-template-rows: 1fr; opacity: 1; }
.activity-collapse > * { overflow: hidden; min-height: 0; min-width: 0; align-self: start; }
.activity-details { position: relative; display: flex; flex-direction: column; gap: 1px; margin-left: 8px; padding: 3px 0 5px 14px; }
.activity-details::before { position: absolute; top: 0; bottom: 5px; left: 3px; width: 1px; background: color-mix(in srgb, var(--border-muted) 72%, transparent); content: ''; }

.supervision-review { display: flex; flex-direction: column; gap: 9px; margin-top: 4px; padding: 10px 12px; border: 1px solid color-mix(in srgb, var(--accent-warning) 38%, var(--border-muted)); border-radius: 8px; background: color-mix(in srgb, var(--accent-warning) 8%, var(--surface-raised)); }
.supervision-reasons { display: flex; flex-direction: column; gap: 3px; margin: 0; color: var(--text-secondary); font-size: 12px; line-height: 1.45; }
.supervision-reasons-heading { display: flex; align-items: center; gap: 6px; color: var(--accent-warning); }
.supervision-reasons strong { font-weight: 600; }
.supervision-actions { display: flex; gap: 6px; flex-wrap: wrap; }
.supervision-action { min-height: 26px; padding: 4px 10px; border: 1px solid var(--border-muted); border-radius: 6px; color: var(--text-primary); background: var(--surface-raised); cursor: pointer; font-size: 12px; }
.supervision-action:hover { border-color: var(--accent-primary); background: var(--surface-hover); }

@media (prefers-reduced-motion: reduce) {
  .activity-summary-chevron { transition: none; }
  .activity-collapse { transition: none; }
}
</style>
