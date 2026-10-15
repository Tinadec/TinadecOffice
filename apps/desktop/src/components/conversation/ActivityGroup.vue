<script setup lang="ts">
import { Brain, ChevronRight, CircleCheck, Hourglass } from '@lucide/vue'
import { computed, toRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'
import { Spinner } from '@/components/ui/spinner'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import { useAutoDisclosure } from '@/composables/useAutoDisclosure'
import { useDelayedLoading } from '@/composables/useDelayedLoading'
import { formatDuration } from '@/lib/conversation/format'
import type { ActivityGroupNode } from '@/lib/conversation/grouping'
import type { ReasoningEntry, ToolEntry, WaitEntry } from '@/lib/conversation/reducer'
import { entrySentence, verbKey } from '@/lib/conversation/toolSentence'
import ReasoningBlock from './ReasoningBlock.vue'

/**
 * Consecutive quiet steps — reads, searches, listings, thoughts, waits — as one line:
 * "Explored 6 files · 2 searches". Open while it is the live end of the turn, folded once
 * the turn moves on, and left as the reader set it after they touch it. Inside, Codex-style:
 * consecutive reads share one "Read a.ts, b.ts" line, each search and listing is one line.
 */
const props = defineProps<{ node: ActivityGroupNode; workspaceRoot?: string | null }>()
const { t } = useI18n()
const { displayName } = useAgentDirectory()

const { open } = useAutoDisclosure(toRef(() => props.node.exploring))
const showSpinner = useDelayedLoading(toRef(() => props.node.exploring))

const label = computed(() => {
  const { node } = props
  if (node.thinkingOnly) {
    return node.exploring ? t('conversation.reasoning.thinking') : t('conversation.reasoning.thoughtFor', { duration: formatDuration(node.thinkingMs, t) })
  }
  const parts = [
    node.counts.files && t('conversation.group.files', { count: node.counts.files }, node.counts.files),
    node.counts.searches && t('conversation.group.searches', { count: node.counts.searches }, node.counts.searches),
    node.counts.lists && t('conversation.group.lists', { count: node.counts.lists }, node.counts.lists),
  ].filter(Boolean)
  return [t(node.exploring ? 'conversation.group.exploring' : 'conversation.group.explored'), parts.join(' · ')].filter(Boolean).join(' ')
})

type Line =
  | { kind: 'reads'; id: string; subjects: string[]; live: boolean }
  | { kind: 'tool'; id: string; entry: ToolEntry }
  | { kind: 'reasoning'; id: string; entry: ReasoningEntry }
  | { kind: 'wait'; id: string; entry: WaitEntry }

const lines = computed<Line[]>(() => {
  const out: Line[] = []
  for (const entry of props.node.entries) {
    if (entry.kind === 'reasoning') out.push({ kind: 'reasoning', id: entry.id, entry })
    else if (entry.kind === 'wait') out.push({ kind: 'wait', id: entry.id, entry })
    else {
      const sentence = entrySentence(entry, props.workspaceRoot)
      const live = entry.status === 'requested' || entry.status === 'running'
      const previous = out.at(-1)
      if (sentence.quiet === 'read' && previous?.kind === 'reads') {
        previous.subjects.push(sentence.subject ?? entry.toolId)
        previous.live ||= live
      } else if (sentence.quiet === 'read') {
        out.push({ kind: 'reads', id: entry.id, subjects: [sentence.subject ?? entry.toolId], live })
      } else {
        out.push({ kind: 'tool', id: entry.id, entry })
      }
    }
  }
  return out
})

function toolText(entry: ToolEntry): string {
  const sentence = entrySentence(entry, props.workspaceRoot)
  const phase = entry.status === 'completed' ? 'done' : 'active'
  const subject = sentence.location ? `${sentence.subject} · ${sentence.location}` : sentence.subject
  return t(verbKey(sentence.verb, phase), { subject: subject ?? entry.toolId })
}
</script>

<template>
  <Collapsible v-model:open="open" class="conversation-group" data-testid="activity-group" :data-exploring="node.exploring ? 'true' : undefined">
    <CollapsibleTrigger class="conversation-fold-trigger">
      <Spinner decorative v-if="showSpinner" class="conversation-glyph" />
      <Brain v-else-if="node.thinkingOnly" class="conversation-glyph" aria-hidden="true" />
      <CircleCheck v-else class="conversation-glyph" aria-hidden="true" />
      <span :class="{ shimmer: node.exploring }" data-testid="group-label">{{ label }}</span>
      <ChevronRight class="conversation-fold-chevron" aria-hidden="true" />
    </CollapsibleTrigger>
    <CollapsibleContent class="conversation-fold-content">
      <ul class="conversation-group-lines" role="list">
        <li v-for="line in lines" :key="line.id" class="conversation-group-line">
          <ReasoningBlock v-if="line.kind === 'reasoning'" :entry="line.entry" />
          <span v-else-if="line.kind === 'reads'" :class="{ shimmer: line.live }">
            └ {{ t(verbKey('read', line.live ? 'active' : 'done'), { subject: line.subjects.join(', ') }) }}
          </span>
          <span v-else-if="line.kind === 'tool'">└ {{ toolText(line.entry) }}</span>
          <span v-else class="conversation-group-wait">
            <Hourglass class="conversation-meta-glyph" aria-hidden="true" />
            {{ t(line.entry.status === 'waiting' ? 'conversation.wait.waiting' : 'conversation.wait.resolved', { names: line.entry.awaited.map((item) => displayName(item.handle ?? item.taskKey)).join(t('conversation.listSeparator')) }) }}
          </span>
        </li>
      </ul>
    </CollapsibleContent>
  </Collapsible>
</template>

<style scoped>
.conversation-group-lines { display: flex; flex-direction: column; gap: 2px; margin: 0; padding: 0 0 4px 20px; list-style: none; color: var(--text-secondary); font-size: 12px; }
.conversation-group-line { min-width: 0; overflow-wrap: anywhere; }
.conversation-group-wait { display: inline-flex; align-items: center; gap: 4px; }
</style>
