<script setup lang="ts">
import { RotateCcw } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { approvalSummary } from '@/userToolAction'
import type { ApprovalDto } from '@/api'
import type { SupervisionReview } from '@/composables/useAgentActivity'
import { groupRun, isCompactAnswer, type GroupedRun } from '@/lib/conversation/grouping'
import type { RunActivity, ToolEntry } from '@/lib/conversation/reducer'
import { isLiveRunStatus, type ConversationTurn } from '@/lib/conversation/turns'
import { elapsedMs } from '@/lib/conversation/format'
import UserMessage from './UserMessage.vue'
import TurnWork from './TurnWork.vue'
import ApprovalCard from './ApprovalCard.vue'
import AssistantAnswer from './AssistantAnswer.vue'
import TurnMarker from './TurnMarker.vue'
import SupervisionGate from './SupervisionGate.vue'

/**
 * One turn: the user's message, then for each run its work, the approvals it is waiting
 * on (never folded), any supervision escalation, and finally the answer.
 */
const props = defineProps<{
  turn: ConversationTurn
  /** Bumped when the feed publishes; run activity is mutable, so this is the change signal. */
  revision: number
  approvals: readonly ApprovalDto[]
  supervisionReviews?: Readonly<Record<string, SupervisionReview | null | undefined>>
  workspaceRoot?: string | null
  last?: boolean
}>()
const emit = defineEmits<{ edit: [payload: { id: string; content: string }] }>()
const { t, te } = useI18n()

interface RunView {
  id: string
  status: string
  live: boolean
  activity: RunActivity | null
  grouped: GroupedRun | null
  /** Approvals Core lists for this run that the feed has not seen (yet): still shown. */
  orphanApprovals: ApprovalDto[]
  streamingText: string | null
}

const approvalsById = computed(() => new Map(props.approvals.map((approval) => [approval.id, approval])))

const runs = computed<RunView[]>(() => {
  void props.revision
  return props.turn.runs.map(({ run, activity, streamingText }) => {
    const grouped = activity ? groupRun(activity, isLiveRunStatus(run.status)) : null
    const known = new Set(grouped?.approvals.map((approval) => approval.entry.approvalId).filter(Boolean))
    const orphanApprovals = props.approvals.filter((approval) =>
      approval.run_id === run.id && approval.status === 'pending' && !known.has(approval.id))
    return { id: run.id, status: run.status, live: isLiveRunStatus(run.status), activity, grouped, orphanApprovals, streamingText }
  })
})

/** A Core approval row with no feed entry, shown through the same card. */
function placeholderEntry(approval: ApprovalDto): ToolEntry {
  return {
    kind: 'tool', id: `approval:${approval.id}`, seq: 0, taskId: null, agentInstanceId: null,
    toolId: approval.tool_id ?? 'tool', status: 'awaiting_approval', risk: approval.risk ?? null,
    requiresApproval: true, mutatesWorkspace: false, approvalId: approval.id,
    argumentsSummary: approvalSummary(approval, { t, te }), resourcePath: approval.resource_path ?? null,
    command: approval.command ?? null, cwd: approval.cwd ?? null, query: null,
    toolSuccess: null, errorCategory: null, exitCode: null, timedOut: false,
    output: [], outputPartial: '', outputDropped: 0, terminalSessionId: null, changedPaths: [], startedAt: approval.created_at, endedAt: null,
  }
}

const answers = computed(() => props.turn.answers.filter((message) => message.role === 'assistant'))
/**
 * The same span the folded work summary shows (first to last activity of the turn's runs), so
 * "Worked for" means one number in both places; message timestamps only when nothing ran.
 */
const workedMs = computed(() => {
  void props.revision
  if (!props.turn.endedAt) return null
  const starts = props.turn.runs.map(({ activity }) => activity?.firstAt).filter((at): at is string => !!at).sort()
  const ends = props.turn.runs.map(({ activity }) => activity?.lastAt).filter((at): at is string => !!at).sort()
  return starts.length && ends.length ? elapsedMs(starts[0], ends.at(-1)) : elapsedMs(props.turn.startedAt, props.turn.endedAt)
})
/**
 * A failed or stopped last turn offers to try again: the original words go back into the
 * composer (edit-and-resend's revert, never an automatic resend). Only the last turn — an
 * older one would cut the history after it — and only once Core holds the message.
 */
const canRetry = computed(() => Boolean(props.last && props.turn.user && !props.turn.user.id.startsWith('pending-')
  && (props.turn.phase === 'failed' || props.turn.phase === 'cancelled')))
function retry() {
  const user = props.turn.user
  if (user) emit('edit', { id: user.id, content: user.content })
}
const toolCount = computed(() => {
  void props.revision
  return props.turn.runs.reduce((sum, { activity }) => sum + (activity?.entries.filter((entry) => entry.kind === 'tool').length ?? 0), 0)
})
</script>

<template>
  <div class="conversation-turn" :data-phase="turn.phase" data-testid="conversation-turn">
    <UserMessage v-if="turn.user" :message="turn.user" @edit="emit('edit', $event)" />
    <p v-if="turn.phase === 'sending'" class="conversation-sending" role="status">{{ t('conversation.turn.sending') }}</p>

    <template v-for="run in runs" :key="run.id">
      <TurnWork
        v-if="run.activity && run.grouped?.nodes.length"
        :activity="run.activity"
        :nodes="run.grouped.nodes"
        :live="run.live"
        :run-id="run.id"
        :revision="revision"
        :workspace-root="workspaceRoot"
      />
      <div v-if="run.grouped?.approvals.length || run.orphanApprovals.length" class="conversation-approvals" aria-live="polite">
        <ApprovalCard
          v-for="pending in run.grouped?.approvals ?? []"
          :key="pending.entry.id"
          :entry="pending.entry"
          :requester="pending.requester"
          :approval="pending.entry.approvalId ? approvalsById.get(pending.entry.approvalId) : null"
          :run-id="run.id"
          :workspace-root="workspaceRoot"
        />
        <ApprovalCard
          v-for="approval in run.orphanApprovals"
          :key="approval.id"
          :entry="placeholderEntry(approval)"
          :approval="approval"
          :run-id="run.id"
          :workspace-root="workspaceRoot"
        />
      </div>
      <SupervisionGate :review="supervisionReviews?.[run.id]" />
      <!-- Why a run stopped is never folded away with its work. -->
      <TurnMarker v-if="run.activity?.failure" type="run_failed" :data="{ category: run.activity.failure.category, message: run.activity.failure.message }" />
      <TurnMarker v-if="run.status === 'cancelled'" type="run_cancelled" />
      <AssistantAnswer v-if="run.streamingText && !answers.some((answer) => answer.run_id === run.id)" :content="run.streamingText" streaming />
    </template>

    <AssistantAnswer
      v-for="(answer, index) in answers"
      :key="answer.id"
      :content="answer.content"
      :created-at="answer.created_at"
      :worked-ms="index === answers.length - 1 ? workedMs : null"
      :tool-count="index === answers.length - 1 ? toolCount : 0"
      :last="last && index === answers.length - 1"
      :compact="isCompactAnswer(answer.content, index, answers.length)"
    />
    <div v-if="canRetry" class="conversation-retry">
      <button type="button" class="conversation-text-button" :title="t('conversation.turn.retryHint')" data-testid="turn-retry" @click="retry">
        <RotateCcw :size="12" aria-hidden="true" />{{ t('conversation.turn.retry') }}
      </button>
    </div>
  </div>
</template>

<style scoped>
.conversation-turn { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
.conversation-approvals { display: flex; flex-direction: column; gap: 8px; }
.conversation-retry { display: flex; }
.conversation-retry .conversation-text-button { display: inline-flex; align-items: center; gap: 4px; }
.conversation-sending { margin: 0; align-self: flex-end; font-size: 11px; color: var(--text-muted); }
</style>
