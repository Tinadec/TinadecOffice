<script setup lang="ts">
import { Hourglass } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import type { FeedNode } from '@/lib/conversation/grouping'
import type { WaitEntry } from '@/lib/conversation/reducer'
import TurnMarker from './TurnMarker.vue'
import ActivityGroup from './ActivityGroup.vue'
import ReasoningBlock from './ReasoningBlock.vue'
import ToolRow from './ToolRow.vue'
import AgentTodos from './AgentTodos.vue'
import AgentGroup from './AgentGroup.vue'

defineOptions({ name: 'TurnActivity' })

/**
 * The work area of a run: reasoning blocks, folded exploration groups, one line per loud
 * tool call, plans, waits, sub-agents and system lines. Rows have no border and no
 * background; status is carried by the glyph. Parallel dispatches render as an AgentGroup.
 */
defineProps<{ nodes: FeedNode[]; runId: string; workspaceRoot?: string | null }>()
const { t } = useI18n()
const { displayName } = useAgentDirectory()

function waitNames(entry: WaitEntry): string {
  return entry.awaited.map((item) => displayName(item.handle ?? item.taskKey)).filter(Boolean).join(t('conversation.listSeparator'))
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
        <span>{{ node.entry.status === 'waiting' ? t('conversation.wait.waiting', { names: waitNames(node.entry) }) : t('conversation.wait.resolved', { names: waitNames(node.entry) }) }}</span>
      </template>
      <AgentTodos v-else-if="node.type === 'plan'" :entry="node.entry" />
      <AgentGroup v-else-if="node.type === 'agents'" :agents="node.agents" :run-id="runId" :workspace-root="workspaceRoot" />
      <template v-else-if="node.type === 'marker'">
        <TurnMarker :type="node.entry.type" :data="node.entry.data" />
      </template>
      <template v-else-if="node.type === 'supervision'">
        <span>{{ node.entry.decision === 'revise' ? t('conversation.supervision.revise') : t('conversation.supervision.escalate') }}</span>
      </template>
    </li>
  </ul>
</template>

<style scoped>
.conversation-activity { display: flex; flex-direction: column; gap: 2px; margin: 0; padding: 0; list-style: none; font-size: 12px; color: var(--text-secondary); }
.conversation-activity-row { display: flex; align-items: center; gap: 6px; min-height: 22px; min-width: 0; }
.conversation-activity-row > [data-slot='collapsible'], .conversation-activity-row > .conversation-todos, .conversation-activity-row > .conversation-agent-group { flex: 1; min-width: 0; }
</style>
