<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import TurnTimeline from './TurnTimeline.vue'
import { homeController } from '@/controllers/HomeController'
import type { SupervisionReview, ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

/**
 * The turn currently in flight. A run produces thinking steps and tool calls
 * long before its assistant message exists, so this activity has no message id
 * to attach to — rendering it only via a message anchor makes the whole turn
 * invisible while it is running, approvals included.
 */
const props = defineProps<{
  thinkingSteps?: ThinkingStep[]
  toolCalls?: ToolCall[]
  runId?: string
  /** Passed by the owner that resolved this run's activity. The controller lookup
      stays as a fallback for callers that only know the run id. */
  supervisionReview?: SupervisionReview | null
}>()
const { t } = useI18n()

const emit = defineEmits<{
  approve: [approvalId: string]
  reject: [approvalId: string]
}>()

const steps = computed(() => props.thinkingSteps ?? [])
const calls = computed(() => props.toolCalls ?? [])
const review = computed(() => {
  if (props.supervisionReview) return props.supervisionReview
  if (!props.runId) return null
  return homeController.agentTurnActivities.value[props.runId]?.supervisionReview ?? null
})
const activityLabel = computed(() => review.value ? t('chat.waitingForApproval') : calls.value.length > 0 ? t('chat.usingTools') : t('chat.thinking'))
</script>

<template>
  <div v-if="steps.length > 0 || calls.length > 0 || review" class="live-turn" data-testid="live-turn" aria-live="polite">
    <div class="live-turn-status"><span class="live-turn-pulse" aria-hidden="true" /><span>{{ activityLabel }}</span></div>
    <TurnTimeline :run-id="runId" :thinking-steps="steps" :tool-calls="calls"
      :supervision-review="review"
      @approve="emit('approve', $event)" @reject="emit('reject', $event)" />
  </div>
</template>

<style scoped>
/* Flat disclosure row, matching the assistant message language: no border, no
   card background, no status pills — status is carried by glyph colour alone. */
.live-turn {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-width: 100%;
  padding: 4px 0;
}
.live-turn-status { display: inline-flex; align-items: center; gap: 6px; min-height: 20px; color: var(--text-muted); font-size: 11px; }
.live-turn-pulse { width: 6px; height: 6px; border-radius: 50%; background: var(--accent-primary); animation: live-turn-pulse 1.4s ease-in-out infinite; }
@keyframes live-turn-pulse { 0%, 100% { opacity: .35; transform: scale(.85); } 50% { opacity: 1; transform: scale(1); } }
@media (prefers-reduced-motion: reduce) { .live-turn-pulse { animation: none; opacity: 1; } }

.live-turn-tools {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
</style>
