<script setup lang="ts">
import { Hourglass, Users } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import type { FeedNode } from '@/lib/conversation/grouping'
import type { WaitEntry } from '@/lib/conversation/reducer'
import TurnMarker from './TurnMarker.vue'
import ActivityGroup from './ActivityGroup.vue'
import ReasoningBlock from './ReasoningBlock.vue'
import ToolRow from './ToolRow.vue'
import AgentTodos from './AgentTodos.vue'

defineOptions({ name: 'TurnActivity' })

/**
 * The work area of a run: reasoning blocks, folded exploration groups, one line per loud
 * tool call, plans, waits, sub-agents and system lines. Rows have no border and no
 * background; status is carried by the glyph. Sub-agents get their card in P5.
 */
defineProps<{ nodes: FeedNode[]; runId: string; workspaceRoot?: string | null }>()
const { t } = useI18n()
const { displayName } = useAgentDirectory()

function waitNames(entry: WaitEntry): string {
  return entry.awaited.map((item) => displayName(item.handle ?? item.taskKey)).filter(Boolean).join('、')
}
</script>

<template>
  <ul class="conversation-activity" role="list">
    <!-- A run failure is shown by the turn, outside this fold. -->
    <li v-for="node in nodes.filter((item) => item.type !== 'marker' || item.entry.type !== 'run_failed')" :key="node.id" class="conversation-activity-row" :data-node="node.type">
      <ActivityGroup v-if="node.type === 'group'" :node="node" :workspace-root="workspaceRoot" />
      <ReasoningBlock v-else-if="node.type === 'reasoning'" :entry="node.entry" />
      <ToolRow v-else-if="node.type === 'tool'" :entry="node.entry" :run-id="runId" :workspace-root="workspaceRoot" />
      <template v-else-if="node.type === 'wait'">
        <Hourglass class="conversation-glyph" aria-hidden="true" />
        <span>{{ t(node.entry.status === 'waiting' ? 'conversation.wait.waiting' : 'conversation.wait.resolved', { names: waitNames(node.entry) }) }}</span>
      </template>
      <AgentTodos v-else-if="node.type === 'plan'" :entry="node.entry" />
      <template v-else-if="node.type === 'agents'">
        <div class="conversation-agents">
          <div class="conversation-activity-row">
            <Users class="conversation-glyph" aria-hidden="true" />
            <span>{{ t('conversation.agents.working', { count: node.agents.length }, node.agents.length) }}</span>
          </div>
          <div v-for="agent in node.agents" :key="agent.agent.taskId" class="conversation-agent" data-testid="sub-agent">
            <div class="conversation-activity-row">
              <span class="conversation-agent-name">{{ displayName(agent.agent.handle ?? agent.agent.agentSlug) }}</span>
              <span class="conversation-agent-status" :data-status="agent.status">{{ t(`conversation.agents.status.${agent.status}`) }}</span>
            </div>
            <p v-if="agent.agent.title" class="conversation-agent-title">{{ agent.agent.title }}</p>
            <TurnActivity v-if="agent.nodes.length" :nodes="agent.nodes" :run-id="runId" :workspace-root="workspaceRoot" class="conversation-agent-activity" />
          </div>
        </div>
      </template>
      <template v-else-if="node.type === 'marker'">
        <TurnMarker :type="node.entry.type" :data="node.entry.data" />
      </template>
      <template v-else-if="node.type === 'supervision'">
        <span>{{ t(node.entry.decision === 'revise' ? 'conversation.supervision.revise' : 'conversation.supervision.escalate') }}</span>
      </template>
    </li>
  </ul>
</template>

<style scoped>
.conversation-activity { display: flex; flex-direction: column; gap: 2px; margin: 0; padding: 0; list-style: none; font-size: 12px; color: var(--text-secondary); }
.conversation-activity-row { display: flex; align-items: center; gap: 6px; min-height: 22px; min-width: 0; }
.conversation-activity-row > [data-slot='collapsible'], .conversation-activity-row > .conversation-todos, .conversation-activity-row > .conversation-agents { flex: 1; min-width: 0; }
.conversation-agents { display: flex; flex-direction: column; gap: 4px; width: 100%; }
.conversation-agent { padding-left: 20px; border-left: 1px solid var(--border-card); }
.conversation-agent-name { color: var(--text-primary); }
.conversation-agent-status { font-size: 11px; color: var(--text-muted); }
.conversation-agent-status[data-status='failed'], .conversation-agent-status[data-status='blocked'] { color: var(--accent-danger); }
.conversation-agent-status[data-status='awaiting_approval'] { color: var(--accent-warning); }
.conversation-agent-title { margin: 0; font-size: 12px; color: var(--text-muted); }
</style>
