<script setup lang="ts">
import { Brain, CircleCheck, CircleX, Hourglass, ListChecks, Users } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { Spinner } from '@/components/ui/spinner'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import { feedLocale, formatDuration } from '@/lib/conversation/format'
import type { ActivityGroupNode, FeedNode } from '@/lib/conversation/grouping'
import { entrySentence, verbKey, type VerbPhase } from '@/lib/conversation/toolSentence'
import type { ToolEntry, WaitEntry } from '@/lib/conversation/reducer'
import TurnMarker from './TurnMarker.vue'

defineOptions({ name: 'TurnActivity' })

/**
 * The work area of a turn, one line per node (APP-HOME-108 P3 skeleton). Lines have no
 * border and no background; status is the glyph's colour alone. P4 replaces each line with
 * its full component (reasoning block, collapsible exploration group, tool previews).
 */
defineProps<{ nodes: FeedNode[]; workspaceRoot?: string | null }>()
const { t, locale } = useI18n()
const { displayName } = useAgentDirectory()

function phaseOf(entry: ToolEntry): VerbPhase {
  if (entry.status === 'failed' || entry.status === 'outcome_unknown' || entry.toolSuccess === false) return 'failed'
  return entry.status === 'completed' ? 'done' : 'active'
}

function toolLine(entry: ToolEntry, root?: string | null): string {
  const sentence = entrySentence(entry, root)
  const subject = sentence.location ? `${sentence.subject} · ${sentence.location}` : sentence.subject
  return t(verbKey(sentence.verb, phaseOf(entry)), { subject: subject ?? entry.toolId })
}

function groupLine(node: ActivityGroupNode): string {
  if (node.thinkingOnly) {
    return node.exploring ? t('conversation.reasoning.thinking') : t('conversation.reasoning.thoughtFor', { duration: formatDuration(node.thinkingMs, feedLocale(locale.value)) })
  }
  const parts = [
    node.counts.files && t('conversation.group.files', { count: node.counts.files }, node.counts.files),
    node.counts.searches && t('conversation.group.searches', { count: node.counts.searches }, node.counts.searches),
    node.counts.lists && t('conversation.group.lists', { count: node.counts.lists }, node.counts.lists),
  ].filter(Boolean)
  return `${t(node.exploring ? 'conversation.group.exploring' : 'conversation.group.explored')} ${parts.join(' · ')}`.trim()
}

function waitNames(entry: WaitEntry): string {
  return entry.awaited.map((item) => displayName(item.handle ?? item.taskKey)).filter(Boolean).join('、')
}
</script>

<template>
  <ul class="conversation-activity" role="list">
    <li v-for="node in nodes" :key="node.id" class="conversation-activity-row" :data-node="node.type">
      <template v-if="node.type === 'group'">
        <Spinner v-if="node.exploring" class="conversation-glyph" />
        <Brain v-else-if="node.thinkingOnly" class="conversation-glyph" aria-hidden="true" />
        <CircleCheck v-else class="conversation-glyph is-done" aria-hidden="true" />
        <span>{{ groupLine(node) }}</span>
      </template>
      <template v-else-if="node.type === 'reasoning'">
        <Spinner v-if="node.entry.status === 'running'" class="conversation-glyph" />
        <Brain v-else class="conversation-glyph" aria-hidden="true" />
        <span :class="{ shimmer: node.entry.status === 'running' }">
          {{ node.entry.status === 'running' ? t('conversation.reasoning.thinking') : t('conversation.reasoning.thoughtFor', { duration: formatDuration(Date.parse(node.entry.endedAt ?? node.entry.startedAt) - Date.parse(node.entry.startedAt), feedLocale(locale)) }) }}
        </span>
      </template>
      <template v-else-if="node.type === 'tool'">
        <Spinner v-if="phaseOf(node.entry) === 'active'" class="conversation-glyph" />
        <CircleX v-else-if="phaseOf(node.entry) === 'failed'" class="conversation-glyph is-failed" aria-hidden="true" />
        <component :is="entrySentence(node.entry, workspaceRoot).icon" v-else class="conversation-glyph" aria-hidden="true" />
        <span class="conversation-activity-text" data-testid="tool-row">{{ toolLine(node.entry, workspaceRoot) }}</span>
      </template>
      <template v-else-if="node.type === 'wait'">
        <Hourglass class="conversation-glyph" aria-hidden="true" />
        <span>{{ t(node.entry.status === 'waiting' ? 'conversation.wait.waiting' : 'conversation.wait.resolved', { names: waitNames(node.entry) }) }}</span>
      </template>
      <template v-else-if="node.type === 'plan'">
        <ListChecks class="conversation-glyph" aria-hidden="true" />
        <span>{{ t('conversation.plan.updated', { done: node.entry.steps.filter((s) => s.status === 'completed').length, total: node.entry.steps.length }) }}</span>
      </template>
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
            <TurnActivity v-if="agent.nodes.length" :nodes="agent.nodes" :workspace-root="workspaceRoot" class="conversation-agent-activity" />
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
.conversation-activity-text { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.conversation-agents { display: flex; flex-direction: column; gap: 4px; width: 100%; }
.conversation-agent { padding-left: 20px; border-left: 1px solid var(--border-card); }
.conversation-agent-name { color: var(--text-primary); }
.conversation-agent-status { font-size: 11px; color: var(--text-muted); }
.conversation-agent-status[data-status='failed'], .conversation-agent-status[data-status='blocked'] { color: var(--accent-danger); }
.conversation-agent-status[data-status='awaiting_approval'] { color: var(--accent-warning); }
.conversation-agent-title { margin: 0; font-size: 12px; color: var(--text-muted); }
</style>
