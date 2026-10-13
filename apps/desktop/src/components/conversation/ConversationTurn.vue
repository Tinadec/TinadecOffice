<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ApprovalDto } from '@/api'
import type { SupervisionReview } from '@/composables/useAgentActivity'
import { groupRun, type GroupedRun } from '@/lib/conversation/grouping'
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
const { t } = useI18n()

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
    argumentsSummary: approval.summary, resourcePath: approval.resource_path ?? null,
    command: approval.command ?? null, cwd: approval.cwd ?? null, query: null,
    toolSuccess: null, errorCategory: null, exitCode: null, timedOut: false,
    output: [], outputPartial: '', outputDropped: 0, terminalSessionId: null, changedPaths: [], startedAt: approval.created_at, endedAt: null,
  }
}

const answers = computed(() => props.turn.answers.filter((message) => message.role === 'assistant'))
const workedMs = computed(() => (props.turn.endedAt ? elapsedMs(props.turn.startedAt, props.turn.endedAt) : null))
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
    />
  </div>
</template>

<style scoped>
.conversation-turn { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
.conversation-approvals { display: flex; flex-direction: column; gap: 8px; }
.conversation-sending { margin: 0; align-self: flex-end; font-size: 11px; color: var(--text-muted); }
</style>
