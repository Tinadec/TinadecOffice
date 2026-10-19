<script setup lang="ts">
import { Brain, ChevronRight, CircleHelp, Crosshair, ListChecks, PenLine, RotateCcw, ScanSearch, ShieldAlert, ShieldCheck, Wrench } from '@lucide/vue'
import { computed, toRef } from 'vue'
import { useI18n } from 'vue-i18n'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'
import { useAutoDisclosure } from '@/composables/useAutoDisclosure'
import { useJustSettled } from '@/composables/useJustSettled'
import { useElapsed } from '@/composables/useElapsed'
import { formatDuration } from '@/lib/conversation/format'
import type { FeedNode } from '@/lib/conversation/grouping'
import { latestReasoningTitle } from '@/lib/conversation/reasoning'
import type { FeedEntry, PlanEntry, ReasoningEntry, RunActivity, ToolEntry } from '@/lib/conversation/reducer'
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
  /**
   * Producing work right now: drives the spinner, the live step line and the timer. A run
   * parked on a person is NOT working — nothing is arriving, so claiming a step is in
   * progress would read as "still busy" forever.
   */
  working: boolean
  /**
   * Still in flight, parked or not: decides whether the fold starts open. Kept apart from
   * `working` because a parked run must stay open (the work, and any decision, is what the
   * reader came for) while it must not pretend to be running.
   */
  openByDefault: boolean
  runId: string
  /** Core's current run phase, used while the first durable step is still in flight. */
  runStatus: string
  /** Bumped when the feed publishes; activity is mutable. */
  revision: number
  workspaceRoot?: string | null
}>()
const { t } = useI18n()

const { open } = useAutoDisclosure(toRef(() => props.openByDefault))
// The summary that replaces the status line sweeps once, only when this run finished in view.
const justSettled = useJustSettled(toRef(() => props.openByDefault))
const elapsed = useElapsed(toRef(() => props.activity.firstAt), toRef(() => (props.openByDefault ? null : props.activity.lastAt)))

function isLive(entry: FeedEntry): boolean {
  if (entry.kind === 'tool') return entry.status === 'requested' || entry.status === 'running'
  if (entry.kind === 'reasoning') return entry.status === 'running'
  return false
}

type StatusKind = 'drafting' | 'planning' | 'thinking' | 'tool' | 'locating' | 'fixing' | 'verifying' | 'backtracking' | 'question'

interface StatusStep {
  kind: StatusKind
  label: string
}

const statusIcons = {
  drafting: PenLine,
  planning: ListChecks,
  thinking: Brain,
  tool: Wrench,
  locating: ScanSearch,
  fixing: Crosshair,
  verifying: ShieldCheck,
  backtracking: RotateCcw,
  question: CircleHelp,
} as const

function latest(entries: readonly FeedEntry[], kind: 'plan' | 'reasoning'): PlanEntry | ReasoningEntry | null {
  for (let i = entries.length - 1; i >= 0; i -= 1) {
    const entry = entries[i]!
    if (entry.kind === kind) return entry
  }
  return null
}

const statusStep = computed<StatusStep>(() => {
  void props.revision
  if (props.activity.correction) {
    const trace = props.activity.correction
    const pending = trace.openQuestions.find((question) => question.blocking && !question.answeredAt)
    if (pending) return { kind: 'question', label: pending.question }
    if (trace.phase === 'INTERVENE') return { kind: 'fixing', label: trace.summary || t('conversation.correction.phase.intervene') }
    if (trace.phase === 'CONVERGE') return { kind: 'verifying', label: trace.summary || t('conversation.correction.phase.converge') }
    if (trace.phase === 'BACKTRACK') return { kind: 'backtracking', label: trace.summary || t('conversation.correction.phase.backtrack') }
    return { kind: 'locating', label: trace.summary || t('conversation.correction.phase.observe') }
  }
  const current = [...props.activity.entries].reverse().find(isLive)
  if (current?.kind === 'tool') {
    const sentence = entrySentence(current, props.workspaceRoot)
    return { kind: 'tool', label: t(verbKey(sentence.verb, 'active'), { subject: sentence.subject ?? current.toolId }) }
  }
  if (current?.kind === 'reasoning') {
    const title = latestReasoningTitle(current.text)
    if (title) return { kind: 'thinking', label: title }
    return current.output && !current.text
      ? { kind: 'drafting', label: t('conversation.reasoning.drafting') }
      : { kind: 'thinking', label: t('conversation.reasoning.thinking') }
  }

  // The live event may already have settled, but the phase remains the best explanation
  // of what the run is doing while the next event is in flight. Keep the latest durable
  // step visible instead of falling back to a generic "thinking" label.
  const plan = latest(props.activity.entries, 'plan') as PlanEntry | null
  if (plan) {
    const active = plan.steps.find((step) => step.status === 'in_progress')
    return { kind: 'planning', label: active?.step ?? t('conversation.reasoning.planning') }
  }
  const reasoning = latest(props.activity.entries, 'reasoning') as ReasoningEntry | null
  if (reasoning) {
    const title = latestReasoningTitle(reasoning.text)
    if (title) return { kind: 'thinking', label: title }
    return reasoning.output && !reasoning.text
      ? { kind: 'drafting', label: t('conversation.reasoning.drafting') }
      : { kind: 'thinking', label: t('conversation.reasoning.thinking') }
  }
  if (props.runStatus === 'planning' || props.runStatus === 'understanding' || props.runStatus === 'replanning') {
    return { kind: 'planning', label: t('conversation.reasoning.planning') }
  }
  return { kind: 'thinking', label: t('conversation.reasoning.thinking') }
})

const statusIcon = computed(() => statusIcons[statusStep.value.kind])

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
  <Collapsible v-model:open="open" class="conversation-work" :data-live="working ? 'true' : undefined" data-testid="turn-work">
    <CollapsibleTrigger class="conversation-fold-trigger conversation-work-trigger">
      <template v-if="working">
        <component :is="statusIcon" class="conversation-glyph" data-testid="turn-status-icon" aria-hidden="true" />
        <span class="shimmer" role="status" data-testid="turn-status">{{ statusStep.label }}</span>
        <span v-if="elapsed !== null" class="conversation-fold-meta">{{ formatDuration(elapsed, t) }}</span>
      </template>
      <!-- Parked on a person: no spinner, no timer, just why it stopped. The gate below the
           fold carries the decision itself; this line only keeps the fold honest. -->
      <template v-else-if="openByDefault">
        <ShieldAlert class="conversation-glyph conversation-glyph--waiting" aria-hidden="true" />
        <span class="conversation-fold-waiting" role="status" data-testid="turn-waiting-fold">{{ t('conversation.turn.awaiting.generic') }}</span>
      </template>
      <template v-else-if="activity.correction">
        <component :is="statusIcon" class="conversation-glyph" data-testid="turn-correction-summary-icon" aria-hidden="true" />
        <span class="conversation-correction-summary" data-testid="turn-correction-summary">{{ statusStep.label }}</span>
        <span class="conversation-fold-meta">{{ summary }}</span>
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
.conversation-glyph--waiting { color: var(--accent-warning, #e3b341); }
.conversation-fold-waiting { color: var(--accent-warning, #e3b341); font-size: 12px; }
.conversation-correction-summary { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-secondary); font-size: 12px; }
</style>
