<script setup lang="ts">
import { computed } from 'vue'
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
</script>

<template>
  <div v-if="steps.length > 0 || calls.length > 0 || review" class="live-turn" data-testid="live-turn">
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

.live-turn-tools {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
</style>
