<script setup lang="ts">
import { computed } from 'vue'
import { CircleHelp, Crosshair, RotateCcw, ScanSearch, ShieldCheck, Wrench } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { CorrectionTrace } from '@/lib/conversation/correction'

const props = defineProps<{ trace: CorrectionTrace }>()
const { t } = useI18n()

const phaseIcon = computed(() => {
  if (props.trace.phase === 'INTERVENE') return Wrench
  if (props.trace.phase === 'CONVERGE') return ShieldCheck
  if (props.trace.phase === 'BACKTRACK') return RotateCcw
  return ScanSearch
})
const pendingQuestion = computed(() =>
  props.trace.openQuestions.find((question) => question.blocking && !question.answeredAt) ?? null)
const artifacts = computed(() => ({
  modified: props.trace.completion.modifiedArtifacts.length,
  verified: props.trace.completion.verifiedArtifacts.length,
}))
const effectiveStatus = computed(() => {
  if (props.trace.status === 'resolved' || props.trace.status === 'reopened') return props.trace.status
  if (props.trace.completion.status === 'ready') return 'ready'
  return props.trace.status
})
const phaseLabel = computed(() => t(`conversation.correction.phase.${props.trace.phase.toLowerCase()}`))
</script>

<template>
  <details class="conversation-correction" :open="effectiveStatus !== 'resolved'">
    <summary class="conversation-correction-summary">
      <component :is="phaseIcon" :size="13" aria-hidden="true" />
      <span>{{ phaseLabel }}</span>
      <span v-if="trace.summary" class="conversation-correction-muted">{{ trace.summary }}</span>
      <span class="conversation-correction-spacer" />
      <span class="conversation-correction-badge">
        {{ t(`conversation.correction.status.${effectiveStatus}`, { fallback: effectiveStatus }) }}
      </span>
    </summary>

    <div class="conversation-correction-body">
      <p v-if="trace.goal"><strong>{{ t('conversation.correction.goal') }}</strong>{{ trace.goal }}</p>
      <p v-if="trace.symptom"><strong>{{ t('conversation.correction.symptom') }}</strong>{{ trace.symptom }}</p>
      <p v-if="trace.doneWhen"><strong>{{ t('conversation.correction.doneWhen') }}</strong>{{ trace.doneWhen }}</p>
      <p v-if="trace.rootCause && trace.rootCause !== 'UNKNOWN'"><strong>{{ t('conversation.correction.rootCause') }}</strong>{{ trace.rootCause }}</p>

      <div class="conversation-correction-columns">
        <section>
          <h4>{{ t('conversation.correction.hypotheses') }}</h4>
          <p v-if="trace.hypotheses.length === 0" class="quiet">{{ t('conversation.correction.empty') }}</p>
          <ul v-else>
            <li v-for="hypothesis in trace.hypotheses" :key="hypothesis.id">
              <span class="conversation-correction-hypothesis">{{ hypothesis.id }} · {{ hypothesis.statement }}</span>
              <span class="quiet">{{ t(`conversation.correction.hypothesisStatus.${hypothesis.status}`) }}</span>
            </li>
          </ul>
        </section>
        <section>
          <h4>{{ t('conversation.correction.coverage') }}</h4>
          <p>{{ t('conversation.correction.coverageSummary', artifacts) }}</p>
          <p v-if="trace.completion.unresolvedReason" class="conversation-correction-blocked">{{ trace.completion.unresolvedReason }}</p>
        </section>
      </div>

      <section v-if="trace.evidence.length">
        <h4>{{ t('conversation.correction.evidence') }}</h4>
        <ul>
          <li v-for="evidence in trace.evidence.slice(-6)" :key="evidence.id">
            <Crosshair :size="11" aria-hidden="true" />
            <span>{{ evidence.summary }}</span>
          </li>
        </ul>
      </section>

      <section v-if="trace.interventions.length">
        <h4>{{ t('conversation.correction.interventions') }}</h4>
        <ul>
          <li v-for="intervention in trace.interventions.slice(-4)" :key="intervention.id">
            <Wrench :size="11" aria-hidden="true" />
            <span>{{ intervention.summary }}</span>
          </li>
        </ul>
      </section>

      <section v-if="trace.openQuestions.length">
        <h4>{{ t('conversation.correction.questions') }}</h4>
        <p v-for="question in trace.openQuestions" :key="question.id" class="conversation-correction-question">
          <CircleHelp :size="12" aria-hidden="true" />
          <span>{{ question.question }}</span>
          <span v-if="question.answeredAt" class="quiet">{{ question.answer }}</span>
        </p>
      </section>

      <p v-if="pendingQuestion" class="conversation-correction-blocked">
        {{ t('conversation.correction.waitingQuestion') }}
      </p>
    </div>
  </details>
</template>

<style scoped>
.conversation-correction { border-left: 2px solid var(--accent-info, #58a6ff); padding-left: 8px; color: var(--text-secondary); font-size: 12px; }
.conversation-correction-summary { display: flex; align-items: center; gap: 6px; cursor: pointer; list-style: none; min-width: 0; }
.conversation-correction-summary::-webkit-details-marker { display: none; }
.conversation-correction-summary > span { min-width: 0; }
.conversation-correction-muted { color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.conversation-correction-spacer { flex: 1; }
.conversation-correction-badge { border: 1px solid var(--border-subtle); border-radius: 4px; padding: 1px 4px; color: var(--text-muted); font-size: 10px; text-transform: uppercase; }
.conversation-correction-body { display: grid; gap: 6px; margin-top: 8px; }
.conversation-correction-body p { margin: 0; line-height: 1.45; overflow-wrap: anywhere; }
.conversation-correction-body strong { margin-right: 5px; color: var(--text-primary); font-weight: 600; }
.conversation-correction-columns { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 12px; }
.conversation-correction-body section { min-width: 0; }
.conversation-correction-body h4 { margin: 0 0 3px; color: var(--text-primary); font-size: 11px; font-weight: 600; }
.conversation-correction-body ul { display: grid; gap: 3px; margin: 0; padding: 0; list-style: none; }
.conversation-correction-body li { display: flex; gap: 5px; min-width: 0; line-height: 1.4; }
.conversation-correction-body li span { min-width: 0; overflow-wrap: anywhere; }
.conversation-correction-hypothesis { display: block; }
.conversation-correction-blocked { color: var(--accent-warning, #e3b341) !important; }
.conversation-correction-question { display: flex; align-items: flex-start; gap: 4px; }
@media (max-width: 640px) { .conversation-correction-columns { grid-template-columns: 1fr; } }
</style>
