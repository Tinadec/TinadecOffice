<script setup lang="ts">
import { computed, ref } from 'vue'
import { ListChecks } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import ThinkingProcess from './ThinkingProcess.vue'
import ToolCallCard from './ToolCallCard.vue'
import SupervisionGate from '../conversation/SupervisionGate.vue'
import type { TurnActivity } from '@/composables/useAgentActivity'
import { turnTimeline } from '@/lib/turnTimeline'

const props = defineProps<TurnActivity>()
const emit = defineEmits<{ approve: [id: string]; reject: [id: string] }>()
const { t } = useI18n()
const items = computed(() => turnTimeline(props.thinkingSteps ?? [], props.toolCalls ?? []))
const expanded = ref(false)
const pendingCalls = computed(() => (props.toolCalls ?? []).filter((call) => call.status === 'waiting_approval'))
const detailItems = computed(() => items.value.filter((item) => item.kind !== 'tool' || item.call.status !== 'waiting_approval'))
const isWorking = computed(() => (props.thinkingSteps ?? []).some((step) => step.status === 'running')
  || (props.toolCalls ?? []).some((call) => call.status === 'running' || call.status === 'pending'))
const activityState = computed(() => props.supervisionReview?.options.length
  ? t('agent.activityWaitingApproval')
  : pendingCalls.value.length
    ? t('agent.activityWaitingApproval')
    : (props.toolCalls ?? []).some((call) => call.status === 'running')
      ? t('agent.activityUsingTools')
      : isWorking.value ? t('agent.activityThinking') : '')

</script>

<template>
  <div class="turn-timeline">
    <div v-if="items.length" class="activity-summary" data-testid="activity-summary">
      <button class="activity-summary-trigger" type="button" :aria-expanded="expanded" data-testid="activity-toggle" @click="expanded = !expanded">
        <ListChecks :size="13" class="activity-summary-icon" aria-hidden="true" />
        <span class="activity-summary-chevron" :class="{ expanded }" aria-hidden="true">›</span>
        <span class="activity-summary-label">{{ t('agent.activity') }}</span>
        <span class="activity-summary-count">{{ t('agent.activityCount', { count: items.length }) }}</span>
        <span v-if="activityState" class="activity-summary-state">{{ activityState }}</span>
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
    <SupervisionGate :review="supervisionReview" />
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
.activity-summary-state { min-width: 0; margin-left: auto; overflow: hidden; color: var(--text-muted); font-size: 10px; text-overflow: ellipsis; white-space: nowrap; }
.activity-details { position: relative; display: flex; flex-direction: column; gap: 1px; margin-left: 8px; padding: 3px 0 5px 14px; }
.activity-details::before { position: absolute; top: 0; bottom: 5px; left: 3px; width: 1px; background: color-mix(in srgb, var(--border-muted) 72%, transparent); content: ''; }
@media (prefers-reduced-motion: reduce) { .activity-summary-chevron { transition: none; } }
</style>
