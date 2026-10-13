<script setup lang="ts">
import { Users } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { SubAgentNode } from '@/lib/conversation/grouping'
import SubAgentCard from './SubAgentCard.vue'

/**
 * Consecutive dispatches as one group: "2 agents working…" while two or more run, then one
 * compact card per sub-agent, in dispatch order.
 */
const props = defineProps<{ agents: SubAgentNode[]; runId: string; workspaceRoot?: string | null }>()
const { t } = useI18n()
const working = computed(() => props.agents.filter((agent) => agent.status === 'running' || agent.status === 'awaiting_approval').length)
</script>

<template>
  <div class="conversation-agent-group" data-testid="agent-group">
    <p v-if="working >= 2" class="conversation-agent-group-head" data-testid="agent-group-head">
      <Users class="conversation-glyph" aria-hidden="true" />
      <span class="shimmer">{{ t('conversation.agents.working', { count: working }, working) }}</span>
    </p>
    <SubAgentCard v-for="agent in agents" :key="agent.agent.taskId" :node="agent" :run-id="runId" :workspace-root="workspaceRoot" />
  </div>
</template>

<style scoped>
.conversation-agent-group { display: flex; flex-direction: column; gap: 6px; width: 100%; }
.conversation-agent-group-head { display: flex; align-items: center; gap: 6px; margin: 0; font-size: 12px; color: var(--text-secondary); }
</style>
