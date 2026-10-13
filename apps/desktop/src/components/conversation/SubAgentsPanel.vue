<script setup lang="ts">
import { Bot } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import { groupRun, type SubAgentNode } from '@/lib/conversation/grouping'
import { conversationSelection, selectSubAgent } from '@/lib/conversation/selection'
import { isLiveRunStatus, type TurnRunInput } from '@/lib/conversation/turns'
import SubAgentCard from './SubAgentCard.vue'

/**
 * The right-column "Sub-agents" card: every sub-agent of the session, working ones first,
 * and the selected one's whole activity. It reads the same feed as the conversation, so the
 * agent opened from the feed is the agent shown here.
 */
const props = defineProps<{
  feed: ConversationFeed
  runs: readonly TurnRunInput[]
  workspaceRoot?: string | null
}>()
const { t } = useI18n()
const { displayName } = useAgentDirectory()

interface RosterItem { runId: string; node: SubAgentNode; order: number }

const roster = computed<RosterItem[]>(() => {
  void props.feed.revision.value
  const items: RosterItem[] = []
  props.runs.forEach((run, runIndex) => {
    const activity = props.feed.state.value.runs.get(run.id)
    if (!activity) return
    groupRun(activity, isLiveRunStatus(run.status)).agents.forEach((node, index) => {
      items.push({ runId: run.id, node, order: runIndex * 1000 + index })
    })
  })
  const working = (item: RosterItem) => item.node.status === 'running' || item.node.status === 'awaiting_approval'
  return items.sort((a, b) => Number(working(b)) - Number(working(a)) || a.order - b.order)
})

const selected = computed(() => {
  const choice = conversationSelection.value
  return roster.value.find((item) => item.runId === choice?.runId && item.node.agent.taskId === choice?.taskId) ?? roster.value[0] ?? null
})

function choose(item: RosterItem) {
  selectSubAgent({ runId: item.runId, taskId: item.node.agent.taskId })
}
</script>

<template>
  <div class="conversation-sub-agents" data-testid="sub-agents-panel">
    <p v-if="roster.length === 0" class="conversation-sub-agents-empty">
      <Bot class="conversation-glyph" aria-hidden="true" />{{ t('conversation.agents.none') }}
    </p>
    <template v-else>
      <ul class="conversation-sub-agents-roster" role="listbox" :aria-label="t('conversation.agents.panelTitle')">
        <li
          v-for="item in roster"
          :key="`${item.runId}:${item.node.agent.taskId}`"
          role="option"
          tabindex="0"
          class="conversation-sub-agents-item"
          :aria-selected="selected?.node.agent.taskId === item.node.agent.taskId && selected?.runId === item.runId"
          data-testid="sub-agents-roster-item"
          @click="choose(item)"
          @keydown.enter.prevent="choose(item)"
          @keydown.space.prevent="choose(item)"
        >
          <span class="conversation-sub-agents-name">{{ displayName(item.node.agent.handle ?? item.node.agent.agentSlug) }}</span>
          <span class="conversation-sub-agent-badge" :data-status="item.node.status">{{ t(`conversation.agents.status.${item.node.status}`) }}</span>
        </li>
      </ul>
      <SubAgentCard
        v-if="selected"
        :key="`${selected.runId}:${selected.node.agent.taskId}`"
        :node="selected.node"
        :run-id="selected.runId"
        :workspace-root="workspaceRoot"
        expanded
        in-panel
        data-testid="sub-agents-detail"
      />
    </template>
  </div>
</template>

<style scoped>
.conversation-sub-agents { display: flex; flex-direction: column; gap: 10px; height: 100%; padding: 10px; overflow: auto; }
.conversation-sub-agents-empty { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--text-muted); font-size: 12px; }
.conversation-sub-agents-roster { display: flex; flex-direction: column; gap: 2px; margin: 0; padding: 0; list-style: none; }
.conversation-sub-agents-item { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 4px 8px; border-radius: 6px; font-size: 12px; cursor: pointer; }
.conversation-sub-agents-item:hover { background: var(--surface-hover); }
.conversation-sub-agents-item[aria-selected='true'] { background: var(--surface-selected); }
.conversation-sub-agents-item:focus-visible { outline: 1px solid hsl(var(--ring)); }
.conversation-sub-agents-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-primary); }
.conversation-sub-agent-badge { font-size: 11px; color: var(--text-muted); }
.conversation-sub-agent-badge[data-status='running'] { color: var(--accent-primary); }
.conversation-sub-agent-badge[data-status='awaiting_approval'] { color: var(--accent-warning); }
.conversation-sub-agent-badge[data-status='failed'], .conversation-sub-agent-badge[data-status='blocked'] { color: var(--accent-danger); }
</style>
