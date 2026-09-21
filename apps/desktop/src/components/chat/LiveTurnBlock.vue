<script setup lang="ts">
import { computed } from 'vue'
import ThinkingProcess from './ThinkingProcess.vue'
import ToolCallCard from './ToolCallCard.vue'
import type { ThinkingStep, ToolCall } from '@/composables/useAgentActivity'

/**
 * The turn currently in flight. A run produces thinking steps and tool calls
 * long before its assistant message exists, so this activity has no message id
 * to attach to — rendering it only via a message anchor makes the whole turn
 * invisible while it is running, approvals included.
 */
const props = defineProps<{
  thinkingSteps?: ThinkingStep[]
  toolCalls?: ToolCall[]
}>()

const emit = defineEmits<{
  approve: [approvalId: string]
  reject: [approvalId: string]
}>()

const steps = computed(() => props.thinkingSteps ?? [])
const calls = computed(() => props.toolCalls ?? [])
</script>

<template>
  <div v-if="steps.length > 0 || calls.length > 0" class="live-turn" data-testid="live-turn">
    <ThinkingProcess v-if="steps.length > 0" :steps="steps" />
    <div v-if="calls.length > 0" class="live-turn-tools">
      <ToolCallCard
        v-for="call in calls"
        :key="call.id"
        :tool-call="call"
        @approve="emit('approve', $event)"
        @reject="emit('reject', $event)"
      />
    </div>
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
