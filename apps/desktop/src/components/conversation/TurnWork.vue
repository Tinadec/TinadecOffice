<script setup lang="ts">
import { ChevronRight } from '@lucide/vue'
import { computed, toRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'
import { Spinner } from '@/components/ui/spinner'
import { useAutoDisclosure } from '@/composables/useAutoDisclosure'
import { useJustSettled } from '@/composables/useJustSettled'
import { useElapsed } from '@/composables/useElapsed'
import { formatDuration } from '@/lib/conversation/format'
import type { FeedNode } from '@/lib/conversation/grouping'
import { latestReasoningTitle } from '@/lib/conversation/reasoning'
import type { FeedEntry, RunActivity, ToolEntry } from '@/lib/conversation/reducer'
import { entrySentence, verbKey } from '@/lib/conversation/toolSentence'
import TurnActivity from './TurnActivity.vue'

/**
 * A run's whole work area as one fold. While the run is live it is open, headed by a status
 * line — spinner, the step in progress, a timer — that is the turn's one polite live region.
 * When the run settles it folds to a summary ("Worked for 1m 20s · 6 files · 2 commands");
 * the reader can open or close it at any time and it then stays that way. Approvals are not
 * in here: the turn renders them outside every fold.
 */
const props = defineProps<{
  activity: RunActivity
  nodes: FeedNode[]
  live: boolean
  runId: string
  /** Bumped when the feed publishes; activity is mutable. */
  revision: number
  workspaceRoot?: string | null
}>()
const { t } = useI18n()

const { open } = useAutoDisclosure(toRef(() => props.live))
// The summary that replaces the status line sweeps once, only when this run finished in view.
const justSettled = useJustSettled(toRef(() => props.live))
const elapsed = useElapsed(toRef(() => props.activity.firstAt), toRef(() => (props.live ? null : props.activity.lastAt)))

function isLive(entry: FeedEntry): boolean {
  if (entry.kind === 'tool') return entry.status === 'requested' || entry.status === 'running'
  if (entry.kind === 'reasoning') return entry.status === 'running'
  return false
}

const status = computed(() => {
  void props.revision
  const current = [...props.activity.entries].reverse().find(isLive)
  if (current?.kind === 'tool') {
    const sentence = entrySentence(current, props.workspaceRoot)
    return t(verbKey(sentence.verb, 'active'), { subject: sentence.subject ?? current.toolId })
  }
  if (current?.kind === 'reasoning') return latestReasoningTitle(current.text)
    ?? t(current.output && !current.text ? 'conversation.reasoning.drafting' : 'conversation.reasoning.thinking')
  return t('conversation.reasoning.thinking')
})

const summary = computed(() => {
  void props.revision
  const tools = props.activity.entries.filter((entry): entry is ToolEntry => entry.kind === 'tool')
  const files = new Set(tools.filter((entry) => entrySentence(entry).quiet === 'read').map((entry) => entry.resourcePath ?? entry.id)).size
  const commands = tools.filter((entry) => entrySentence(entry).verb === 'run').length
  const edits = tools.filter((entry) => entrySentence(entry).verb === 'edit').length
  return [
    t('conversation.turn.workedFor', { duration: formatDuration(elapsed.value ?? 0, t) }),
    files && t('conversation.group.files', { count: files }, files),
    edits && t('conversation.turn.edits', { count: edits }, edits),
    commands && t('conversation.turn.commands', { count: commands }, commands),
  ].filter(Boolean).join(' · ')
})
</script>

<template>
  <Collapsible v-model:open="open" class="conversation-work" :data-live="live ? 'true' : undefined" data-testid="turn-work">
    <CollapsibleTrigger class="conversation-fold-trigger conversation-work-trigger">
      <template v-if="live">
        <Spinner decorative class="conversation-glyph" />
        <span class="shimmer" role="status" data-testid="turn-status">{{ status }}</span>
        <span v-if="elapsed !== null" class="conversation-fold-meta">{{ formatDuration(elapsed, t) }}</span>
      </template>
      <span v-else :class="{ 'shimmer-once': justSettled }" data-testid="turn-summary">{{ summary }}</span>
      <ChevronRight class="conversation-fold-chevron" aria-hidden="true" />
    </CollapsibleTrigger>
    <CollapsibleContent class="conversation-fold-content">
      <TurnActivity :nodes="nodes" :run-id="runId" :workspace-root="workspaceRoot" />
    </CollapsibleContent>
  </Collapsible>
</template>

<style scoped>
.conversation-work-trigger { color: var(--text-secondary); }
</style>
