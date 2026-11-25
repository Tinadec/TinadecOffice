<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  Activity,
  ArrowUpRight,
  ChevronDown,
  ChevronRight,
  Clock,
  GitCommitHorizontal,
  Loader2,
  MessageSquare,
  Network,
  Users,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { formatDuration as formatElapsed } from '@/lib/conversation/format'
import type { AgentActivity, AgentState, ProgressEvent, ThinkingStep } from '@/composables/useAgentActivity'
import type { OrchestrationSnapshotDto } from '../api'
import { focusDockCard } from '@/lib/homeDockSync'
import TeammateSteerPopover, { type TeammateSteerTarget } from './teammate/TeammateSteerPopover.vue'
import SubAgentsPanel from './conversation/SubAgentsPanel.vue'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import type { TurnRunInput } from '@/lib/conversation/turns'

const { t, te, locale } = useI18n()
const isZh = computed(() => String(locale.value || '').toLowerCase().startsWith('zh'))

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? String(t(key, params)) : String(t(key))
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

const props = defineProps<{
  activity: AgentActivity
  agentStates: Record<string, AgentState>
  thinkingSteps: ThinkingStep[]
  progressEvents: ProgressEvent[]
  orchestration: OrchestrationSnapshotDto | null
  feed?: ConversationFeed
  runs?: readonly TurnRunInput[]
  workspaceRoot?: string | null
}>()

const governanceCollapsed = ref(false)
const executionCollapsed = ref(false)
const subAgentsCollapsed = ref(false)
const subAgentCount = ref(0)

const steerTarget = ref<TeammateSteerTarget | null>(null)
const steerOpen = ref(false)
const pausedOverrides = ref<Record<string, boolean>>({})

const effectiveRunId = computed<string | null>(
  () => props.activity.runId ?? props.orchestration?.run?.id ?? null,
)

const statusConfig = computed(() => {
  switch (props.activity.status) {
    case 'thinking':
      return { label: t('agent.thinking'), tone: 'running' as const }
    case 'working':
      return { label: t('agent.working'), tone: 'running' as const }
    case 'waiting_approval':
      return { label: t('agent.waitingApproval'), tone: 'waiting' as const }
    case 'completed':
      return { label: t('agent.completed'), tone: 'completed' as const }
    case 'error':
      return { label: t('agent.error'), tone: 'error' as const }
    default:
      return { label: t('agent.idle'), tone: 'idle' as const }
  }
})

const elapsedMs = computed(() => {
  const startRaw = props.activity.runStartedAt ?? props.orchestration?.run?.created_at ?? null
  if (!startRaw) return null
  const start = Date.parse(startRaw)
  if (!Number.isFinite(start)) return null
  const endRaw = props.activity.lastUpdated ?? props.orchestration?.run?.updated_at ?? null
  const end = endRaw && Number.isFinite(Date.parse(endRaw)) ? Date.parse(endRaw) : Date.now()
  return Math.max(0, end - start)
})

function formatTabularDuration(ms: number | null): string | null {
  if (ms === null || !Number.isFinite(ms) || ms < 0) return null
  if (ms < 1000) return `${Math.round(ms)}ms`
  const sec = ms / 1000
  if (sec < 60) return `${sec.toFixed(1)}s`
  return formatElapsed(ms, t)
}

const elapsedLabel = computed(() => formatTabularDuration(elapsedMs.value))

interface DutyRosterMember {
  id: string
  name: string
  layer: 'operation' | 'execution'
  roleLabel: string
  handle: string | null
  agentSlug: string | null
  taskId: string | null
  nodeId: string | null
  tone: 'running' | 'waiting' | 'completed' | 'error' | 'idle'
  statusLabel: string
  duty: string | null
  latestThought: string | null
  elapsedText: string | null
  canSteer: boolean
}

function normalizeAgentLayer(layer: unknown): 'operation' | 'execution' {
  const v = String(layer ?? '').trim().toLowerCase()
  if (v === 'operation' || v === 'planning' || v === 'meeting' || v === 'governance') {
    return 'operation'
  }
  return 'execution'
}

function mapMemberTone(
  rawStatus: string,
  paused: boolean,
): 'running' | 'waiting' | 'completed' | 'error' | 'idle' {
  if (paused) return 'waiting'
  const s = rawStatus.toLowerCase()
  if (s === 'active' || s === 'running' || s === 'working' || s === 'in_progress') return 'running'
  if (s === 'waiting' || s === 'waiting_approval' || s === 'awaiting_approval' || s === 'paused') return 'waiting'
  if (s === 'completed' || s === 'done' || s === 'succeeded') return 'completed'
  if (s === 'error' || s === 'failed' || s === 'blocked') return 'error'
  return 'idle'
}

function memberStatusText(rawStatus: string, paused: boolean): string {
  if (paused) return tr('teammateControl.status.paused', isZh.value ? '已暂停' : 'Paused')
  const tone = mapMemberTone(rawStatus, false)
  switch (tone) {
    case 'running':
      return t('activity.agent.active')
    case 'waiting':
      return t('activity.agent.waiting')
    case 'completed':
      return t('activity.agent.completed')
    case 'error':
      return t('activity.agent.error')
    default:
      return t('activity.agent.idle')
  }
}

const latestGeneralThought = computed<string | null>(() => {
  for (let i = props.thinkingSteps.length - 1; i >= 0; i -= 1) {
    const step = props.thinkingSteps[i]!
    const text = (step.description || step.title || '').trim()
    if (text) return text
  }
  return null
})

function findMemberThought(agentId: string, name: string, handle: string | null): string | null {
  const needles = [agentId, name, handle]
    .filter((v): v is string => Boolean(v && v.trim()))
    .map((v) => v.toLowerCase())

  for (let i = props.thinkingSteps.length - 1; i >= 0; i -= 1) {
    const step = props.thinkingSteps[i]!
    const detailAgentId = typeof step.details?.agentId === 'string' ? step.details.agentId.toLowerCase() : ''
    const detailSlug = typeof step.details?.slug === 'string' ? step.details.slug.toLowerCase() : ''
    const combinedTitle = `${step.title} ${step.description}`.toLowerCase()
    if (
      (detailAgentId && needles.includes(detailAgentId))
      || (detailSlug && needles.includes(detailSlug))
      || needles.some((needle) => needle.length >= 2 && combinedTitle.includes(needle))
    ) {
      const clean = (step.description || step.title || '').trim()
      if (clean) return clean
    }
  }

  for (let i = (props.orchestration?.step_results.length ?? 0) - 1; i >= 0; i -= 1) {
    const res = props.orchestration!.step_results[i]!
    if (res.agent_id && needles.includes(res.agent_id.toLowerCase()) && res.summary?.trim()) {
      return res.summary.trim()
    }
  }

  return null
}

function computeMemberElapsed(lastActiveAt: string | null): string | null {
  const startRaw = props.activity.runStartedAt ?? props.orchestration?.run?.created_at ?? null
  if (!startRaw) return null
  const start = Date.parse(startRaw)
  if (!Number.isFinite(start)) return null
  const endCandidate = lastActiveAt && Number.isFinite(Date.parse(lastActiveAt))
    ? Date.parse(lastActiveAt)
    : (props.activity.lastUpdated && Number.isFinite(Date.parse(props.activity.lastUpdated))
      ? Date.parse(props.activity.lastUpdated)
      : Date.now())
  return formatTabularDuration(Math.max(0, endCandidate - start))
}

const rosterMembers = computed<DutyRosterMember[]>(() => {
  const out: DutyRosterMember[] = []
  const seenIds = new Set<string>()
  const nodesById = new Map((props.orchestration?.nodes ?? []).map((n) => [n.id, n]))
  const assignments = props.orchestration?.assignments ?? []

  for (const state of Object.values(props.agentStates)) {
    seenIds.add(state.agentId)
    const layer = normalizeAgentLayer(state.agentLayer)
    const matchedAssign = assignments.find(
      (a) => a.agent_id === state.agentId || a.agent_name === state.agentName,
    )
    const taskNode = matchedAssign ? nodesById.get(matchedAssign.task_node_id) : undefined
    const handle = layer === 'execution' ? (state.agentName || state.agentId) : null
    const paused = Boolean(handle && pausedOverrides.value[handle])
    const duty = state.currentTask || taskNode?.title || null
    const thought = findMemberThought(state.agentId, state.agentName, handle)
      ?? (layer === 'operation' ? latestGeneralThought.value : null)

    out.push({
      id: state.agentId,
      name: state.agentName || t('agent.agent'),
      layer,
      roleLabel:
        layer === 'operation'
          ? t('orchestrationPanel.layer.operation')
          : t('orchestrationPanel.layer.execution'),
      handle,
      agentSlug: state.agentType || state.agentId,
      taskId: taskNode?.id ?? matchedAssign?.task_node_id ?? null,
      nodeId: layer === 'operation' ? 'coordinator' : (taskNode ? `task:${taskNode.id}` : null),
      tone: mapMemberTone(state.status, paused),
      statusLabel: memberStatusText(state.status, paused),
      duty,
      latestThought: thought,
      elapsedText: computeMemberElapsed(state.lastActiveAt),
      canSteer: Boolean(effectiveRunId.value && layer === 'execution' && handle),
    })
  }

  // Synthesize execution assignments from orchestration snapshot when not yet in agentStates
  for (const assign of assignments) {
    if (seenIds.has(assign.agent_id)) continue
    seenIds.add(assign.agent_id)
    const taskNode = nodesById.get(assign.task_node_id)
    const handle = assign.agent_name || assign.agent_id
    const paused = Boolean(handle && pausedOverrides.value[handle])
    const rawStatus = taskNode?.status ?? assign.status ?? 'idle'
    out.push({
      id: assign.agent_id || assign.id,
      name: assign.agent_name || assign.agent_id || t('agent.agent'),
      layer: 'execution',
      roleLabel: t('orchestrationPanel.layer.execution'),
      handle,
      agentSlug: assign.agent_id,
      taskId: taskNode?.id ?? assign.task_node_id ?? null,
      nodeId: taskNode ? `task:${taskNode.id}` : null,
      tone: mapMemberTone(rawStatus, paused),
      statusLabel: memberStatusText(rawStatus, paused),
      duty: taskNode?.title ?? null,
      latestThought: findMemberThought(assign.agent_id, assign.agent_name, handle),
      elapsedText: elapsedLabel.value,
      canSteer: Boolean(effectiveRunId.value && handle),
    })
  }

  // Ensure governance lead appears when a run is active even before explicit planning agentStates arrive
  const hasGovernance = out.some((m) => m.layer === 'operation')
  if (!hasGovernance && (props.activity.status !== 'idle' || props.activity.activeAgentName || props.orchestration?.run)) {
    const leadName = props.activity.activeAgentName ?? t('orchestrationPanel.roleCoordinator')
    out.unshift({
      id: 'governance-lead',
      name: leadName,
      layer: 'operation',
      roleLabel: props.activity.activeAgentRole || t('orchestrationPanel.layer.operation'),
      handle: null,
      agentSlug: null,
      taskId: null,
      nodeId: 'coordinator',
      tone: statusConfig.value.tone,
      statusLabel: statusConfig.value.label,
      duty: props.activity.runSummary ?? props.orchestration?.run?.summary ?? null,
      latestThought: latestGeneralThought.value,
      elapsedText: elapsedLabel.value,
      canSteer: false,
    })
  }

  return out
})

const governanceMembers = computed(() => rosterMembers.value.filter((m) => m.layer === 'operation'))
const executionMembers = computed(() => rosterMembers.value.filter((m) => m.layer === 'execution'))

const hasActivity = computed(
  () =>
    props.activity.status !== 'idle'
    || props.activity.runId !== null
    || props.activity.activeAgentName !== null
    || rosterMembers.value.length > 0
    || subAgentCount.value > 0,
)

function openMemberSteer(member: DutyRosterMember, event?: Event): void {
  if (!member.canSteer || !effectiveRunId.value || !member.handle) return
  const el = (event?.currentTarget as HTMLElement | null) ?? null
  const rect = el?.getBoundingClientRect?.()
  const paused = Boolean(pausedOverrides.value[member.handle])
  steerTarget.value = {
    runId: effectiveRunId.value,
    handle: member.handle,
    displayName: member.name,
    status: paused ? 'paused' : member.tone,
    paused,
    taskSummary: member.duty,
    thought: member.latestThought,
    anchorRect: rect && (rect.width > 0 || rect.height > 0)
      ? { top: rect.top, left: rect.left, width: rect.width, height: rect.height, right: rect.right, bottom: rect.bottom }
      : null,
  }
  steerOpen.value = true
}

function onSteerStatusChange(payload: { handle: string; status: string; paused: boolean }): void {
  pausedOverrides.value = {
    ...pausedOverrides.value,
    [payload.handle]: payload.paused,
  }
}

function jumpToOrchestration(member?: DutyRosterMember | null): void {
  focusDockCard(
    {
      card: 'orchestration',
      reason: 'worker',
      runId: effectiveRunId.value,
      taskId: member?.taskId ?? null,
      workerHandle: member?.handle ?? null,
      agentSlug: member?.agentSlug ?? null,
      workerLabel: member?.name ?? null,
    },
    { title: t('uie.cardTitle.orchestration') },
  )
}

function jumpToTrajectory(member?: DutyRosterMember | null): void {
  focusDockCard(
    {
      card: 'trajectory',
      reason: 'worker',
      trajectoryTab: 'path',
      trajectoryNodeId: member?.nodeId ?? null,
      runId: effectiveRunId.value,
    },
    { title: tr('uie.cardTitle.trajectory', isZh.value ? '轨迹' : 'Trajectory') },
  )
}

const trajectoryShortLabel = computed(() =>
  tr('uie.cardTitle.trajectory', isZh.value ? '轨迹' : 'Trajectory'),
)
const locateOrchestrationTitle = computed(() =>
  tr('agent.locateInOrchestration', isZh.value ? '在编排图中定位' : 'Locate in orchestration'),
)
const viewMemberTrajectoryTitle = computed(() =>
  tr('agent.viewMemberTrajectory', isZh.value ? '查看该成员轨迹' : 'View member trajectory'),
)
</script>

<template>
  <section class="agent-roster-panel obs-geist-surface" data-testid="agent-activity-panel">
    <!-- Empty state -->
    <div v-if="!hasActivity && !orchestration" class="roster-empty" data-testid="agent-roster-empty">
      <Activity :size="22" aria-hidden="true" />
      <span>{{ t('agent.noActivity') }}</span>
    </div>

    <template v-else>
      <!-- Top Run Duty Summary Strip -->
      <header class="roster-header">
        <div class="roster-header__left">
          <span class="roster-lamp" :class="`is-${statusConfig.tone}`" aria-hidden="true" />
          <strong class="roster-header__lead">
            {{ activity.activeAgentName ?? t('agent.agent') }}
          </strong>
          <span class="roster-status-pill" :class="`is-${statusConfig.tone}`">
            {{ statusConfig.label }}
          </span>
          <span v-if="elapsedLabel" class="roster-elapsed" data-testid="agent-roster-elapsed">
            <Clock :size="11" aria-hidden="true" />
            <span>{{ elapsedLabel }}</span>
          </span>
        </div>

        <div class="roster-header__right">
          <span v-if="activity.totalNodes > 0" class="roster-node-counter">
            {{ activity.completedNodes }}/{{ activity.totalNodes }}
          </span>
          <button
            v-if="effectiveRunId"
            type="button"
            class="roster-header-link ui-pressable"
            :title="t('uie.cardTitle.orchestration')"
            data-testid="agent-header-jump-orchestration"
            @click="jumpToOrchestration(null)"
          >
            <Network :size="12" aria-hidden="true" />
            <span>{{ t('uie.cardTitle.orchestration') }}</span>
          </button>
          <button
            v-if="effectiveRunId"
            type="button"
            class="roster-header-link ui-pressable"
            :title="trajectoryShortLabel"
            data-testid="agent-header-jump-trajectory"
            @click="jumpToTrajectory(null)"
          >
            <GitCommitHorizontal :size="12" aria-hidden="true" />
            <span>{{ trajectoryShortLabel }}</span>
          </button>
        </div>
      </header>

      <div class="roster-scroll">
        <!-- 1. Governance / Operation Layer -->
        <section
          v-if="governanceMembers.length > 0"
          class="roster-section"
          data-testid="agent-roster-governance"
        >
          <button
            type="button"
            class="roster-section__head"
            :aria-expanded="!governanceCollapsed"
            @click="governanceCollapsed = !governanceCollapsed"
          >
            <ChevronRight v-if="governanceCollapsed" :size="12" aria-hidden="true" />
            <ChevronDown v-else :size="12" aria-hidden="true" />
            <span class="roster-section__title">{{ t('orchestrationPanel.layer.operation') }}</span>
            <span class="roster-section__count">{{ governanceMembers.length }}</span>
          </button>

          <div v-show="!governanceCollapsed" class="roster-list">
            <article
              v-for="member in governanceMembers"
              :key="member.id"
              class="roster-member"
              data-testid="agent-roster-member"
              :data-member-layer="member.layer"
            >
              <div class="roster-member__top">
                <span class="roster-lamp" :class="`is-${member.tone}`" aria-hidden="true" />
                <strong class="roster-member__name">{{ member.name }}</strong>
                <span class="roster-member__status" :class="`is-${member.tone}`">{{ member.statusLabel }}</span>
                <span v-if="member.elapsedText" class="roster-member__elapsed">{{ member.elapsedText }}</span>

                <div class="roster-member__actions">
                  <button
                    v-if="effectiveRunId"
                    type="button"
                    class="roster-icon-action ui-pressable"
                    :title="locateOrchestrationTitle"
                    :aria-label="locateOrchestrationTitle"
                    data-testid="agent-roster-jump-orchestration"
                    @click="jumpToOrchestration(member)"
                  >
                    <Network :size="12" aria-hidden="true" />
                  </button>
                  <button
                    v-if="effectiveRunId"
                    type="button"
                    class="roster-icon-action ui-pressable"
                    :title="viewMemberTrajectoryTitle"
                    :aria-label="viewMemberTrajectoryTitle"
                    data-testid="agent-roster-jump-trajectory"
                    @click="jumpToTrajectory(member)"
                  >
                    <ArrowUpRight :size="12" aria-hidden="true" />
                  </button>
                </div>
              </div>

              <p v-if="member.duty" class="roster-member__duty">{{ member.duty }}</p>
              <p v-if="member.latestThought && member.latestThought !== member.duty" class="roster-member__thought">
                {{ member.latestThought }}
              </p>
            </article>
          </div>
        </section>

        <!-- 2. Execution Layer -->
        <section class="roster-section" data-testid="agent-roster-execution">
          <button
            type="button"
            class="roster-section__head"
            :aria-expanded="!executionCollapsed"
            @click="executionCollapsed = !executionCollapsed"
          >
            <ChevronRight v-if="executionCollapsed" :size="12" aria-hidden="true" />
            <ChevronDown v-else :size="12" aria-hidden="true" />
            <span class="roster-section__title">{{ t('orchestrationPanel.layer.execution') }}</span>
            <span class="roster-section__count">{{ executionMembers.length }}</span>
          </button>

          <div v-show="!executionCollapsed" class="roster-list">
            <div v-if="executionMembers.length === 0" class="roster-waiting">
              <Loader2 v-if="statusConfig.tone === 'running'" :size="12" class="roster-spin" aria-hidden="true" />
              <span>{{ statusConfig.tone === 'running' ? t('agent.waitingAssignment') : t('agent.noActivity') }}</span>
            </div>

            <article
              v-for="member in executionMembers"
              :key="member.id"
              class="roster-member"
              data-testid="agent-roster-member"
              :data-member-layer="member.layer"
            >
              <div class="roster-member__top">
                <span class="roster-lamp" :class="`is-${member.tone}`" aria-hidden="true" />
                <strong class="roster-member__name">{{ member.name }}</strong>
                <span class="roster-member__status" :class="`is-${member.tone}`">{{ member.statusLabel }}</span>
                <span v-if="member.elapsedText" class="roster-member__elapsed">{{ member.elapsedText }}</span>

                <div class="roster-member__actions">
                  <button
                    v-if="member.canSteer"
                    type="button"
                    class="roster-steer-btn ui-pressable"
                    :title="tr('teammateControl.triggerTooltip', 'Steer or pause {name}', { name: member.name })"
                    data-testid="agent-roster-steer-btn"
                    @click="openMemberSteer(member, $event)"
                  >
                    <MessageSquare :size="11" aria-hidden="true" />
                    <span>{{ tr('teammateControl.steerAction', 'Direct control') }}</span>
                  </button>
                  <button
                    v-if="effectiveRunId"
                    type="button"
                    class="roster-icon-action ui-pressable"
                    :title="locateOrchestrationTitle"
                    :aria-label="locateOrchestrationTitle"
                    data-testid="agent-roster-jump-orchestration"
                    @click="jumpToOrchestration(member)"
                  >
                    <Network :size="12" aria-hidden="true" />
                  </button>
                  <button
                    v-if="effectiveRunId"
                    type="button"
                    class="roster-icon-action ui-pressable"
                    :title="viewMemberTrajectoryTitle"
                    :aria-label="viewMemberTrajectoryTitle"
                    data-testid="agent-roster-jump-trajectory"
                    @click="jumpToTrajectory(member)"
                  >
                    <ArrowUpRight :size="12" aria-hidden="true" />
                  </button>
                </div>
              </div>

              <p v-if="member.duty" class="roster-member__duty">{{ member.duty }}</p>
              <p v-if="member.latestThought && member.latestThought !== member.duty" class="roster-member__thought">
                {{ member.latestThought }}
              </p>
            </article>
          </div>
        </section>

        <!-- 3. Sub-agents Section (v-show preserved so SubAgentsPanel emits roster-size) -->
        <section
          v-if="feed && runs"
          v-show="subAgentCount > 0"
          class="roster-section"
          data-testid="agent-panel-sub-agents"
        >
          <button
            type="button"
            class="roster-section__head"
            :aria-expanded="!subAgentsCollapsed"
            @click="subAgentsCollapsed = !subAgentsCollapsed"
          >
            <ChevronRight v-if="subAgentsCollapsed" :size="12" aria-hidden="true" />
            <ChevronDown v-else :size="12" aria-hidden="true" />
            <Users :size="12" aria-hidden="true" />
            <span class="roster-section__title">{{ t('conversation.agents.panelTitle') }}</span>
            <span class="roster-section__count">{{ subAgentCount }}</span>
          </button>

          <div v-show="!subAgentsCollapsed" class="roster-subagents-body">
            <SubAgentsPanel
              :feed="feed"
              :runs="runs"
              :workspace-root="workspaceRoot"
              @roster-size="subAgentCount = $event"
            />
          </div>
        </section>
      </div>
    </template>

    <TeammateSteerPopover
      v-if="steerTarget"
      v-model:open="steerOpen"
      :run-id="steerTarget.runId"
      :handle="steerTarget.handle"
      :display-name="steerTarget.displayName"
      :status="steerTarget.status"
      :paused="steerTarget.paused"
      :task-summary="steerTarget.taskSummary"
      :thought="steerTarget.thought"
      :anchor-rect="steerTarget.anchorRect"
      @status-change="onSteerStatusChange"
    />
  </section>
</template>

<style scoped>
.agent-roster-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  min-width: 0;
  background: var(--surface-canvas, var(--bg-primary));
  color: var(--text-primary);
  overflow: hidden;
  font-variant-numeric: tabular-nums;
}

.roster-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  height: 100%;
  color: var(--text-muted);
  font-size: 12px;
}

/* ── Top Run Summary Strip ─────────────────────────────────────── */
.roster-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 6px 10px;
  padding: 9px 12px;
  border-bottom: 1px solid var(--obs-hairline, var(--border-muted));
  flex-shrink: 0;
}

.roster-header__left {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  min-width: 0;
}

.roster-header__lead {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.roster-lamp {
  width: 7px;
  height: 7px;
  border-radius: 999px;
  background: var(--text-muted);
  flex-shrink: 0;
}

.roster-lamp.is-running {
  background: var(--obs-status-ok, var(--accent-success));
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--obs-status-ok, var(--accent-success)) 20%, transparent);
}

.roster-lamp.is-waiting {
  background: var(--obs-status-warn, var(--accent-warning));
}

.roster-lamp.is-completed {
  background: var(--obs-status-ok, var(--accent-success));
}

.roster-lamp.is-error {
  background: var(--obs-status-danger, var(--accent-danger));
}

.roster-status-pill {
  display: inline-flex;
  align-items: center;
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10.5px;
  font-weight: 600;
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
}

.roster-status-pill.is-running {
  background: var(--obs-status-ok-soft, color-mix(in srgb, var(--accent-success) 12%, transparent));
  color: var(--obs-status-ok, var(--accent-success));
}

.roster-status-pill.is-waiting {
  background: var(--obs-status-warn-soft, color-mix(in srgb, var(--accent-warning) 13%, transparent));
  color: var(--obs-status-warn, var(--accent-warning));
}

.roster-status-pill.is-error {
  background: var(--obs-status-danger-soft, color-mix(in srgb, var(--accent-danger) 12%, transparent));
  color: var(--obs-status-danger, var(--accent-danger));
}

.roster-elapsed,
.roster-node-counter {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-size: 11px;
  color: var(--text-muted);
}

.roster-header__right {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  margin-left: auto;
}

.roster-header-link {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 23px;
  padding: 0 7px;
  border: 0;
  border-radius: 5px;
  background: var(--surface-button, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color 0.12s ease, color 0.12s ease;
}

.roster-header-link:hover {
  background: var(--surface-button-hover, var(--bg-hover));
  color: var(--text-primary);
}

/* ── Borderless Geist Sections ─────────────────────────────────── */
.roster-scroll {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
}

.roster-section {
  display: flex;
  flex-direction: column;
  border-bottom: 1px solid var(--obs-hairline, var(--border-muted));
}

.roster-section__head {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  min-height: 32px;
  padding: 6px 12px;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  text-align: left;
  cursor: pointer;
}

.roster-section__head:hover {
  background: var(--surface-hover, var(--bg-secondary));
  color: var(--text-primary);
}

.roster-section__title {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.03em;
  text-transform: uppercase;
  color: var(--text-muted);
}

.roster-section__count {
  margin-left: auto;
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-size: 10.5px;
  color: var(--text-muted);
}

.roster-list {
  display: flex;
  flex-direction: column;
}

.roster-waiting {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 12px 12px;
  color: var(--text-muted);
  font-size: 11.5px;
}

.roster-member {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 12px 10px;
  border-top: 1px solid color-mix(in srgb, var(--obs-hairline, var(--border-muted)) 55%, transparent);
  transition: background-color 0.12s ease;
}

.roster-member:first-child {
  border-top: 0;
}

.roster-member:hover {
  background: var(--surface-hover, var(--bg-secondary));
}

.roster-member__top {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.roster-member__name {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}

.roster-member__status {
  font-size: 10.5px;
  font-weight: 500;
  color: var(--text-muted);
  flex-shrink: 0;
}

.roster-member__status.is-running {
  color: var(--obs-status-ok, var(--accent-success));
}

.roster-member__status.is-waiting {
  color: var(--obs-status-warn, var(--accent-warning));
}

.roster-member__status.is-error {
  color: var(--obs-status-danger, var(--accent-danger));
}

.roster-member__elapsed {
  font-family: var(--font-mono);
  font-variant-numeric: tabular-nums;
  font-size: 11px;
  color: var(--text-muted);
  flex-shrink: 0;
}

.roster-member__actions {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.roster-steer-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 7px;
  border: 0;
  border-radius: 4px;
  background: var(--bg-primary-button);
  color: var(--text-on-primary);
  font-size: 10.5px;
  font-weight: 600;
  white-space: nowrap;
  cursor: pointer;
}

.roster-steer-btn:hover {
  background: var(--bg-primary-button-hover);
}

.roster-icon-action {
  display: inline-grid;
  place-items: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.roster-icon-action:hover {
  background: var(--surface-hover, var(--bg-hover));
  color: var(--text-primary);
}

.roster-member__duty {
  margin: 0;
  padding-left: 13px;
  font-size: 12px;
  line-height: 1.4;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.roster-member__thought {
  margin: 0;
  padding-left: 13px;
  font-size: 11.5px;
  line-height: 1.45;
  color: var(--text-muted);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.roster-subagents-body :deep(.conversation-sub-agents) {
  padding: 4px 12px 10px;
}

.roster-spin {
  animation: roster-spin 1.1s linear infinite;
}

@keyframes roster-spin {
  to {
    transform: rotate(360deg);
  }
}

@media (prefers-reduced-motion: reduce) {
  .roster-spin {
    animation: none;
  }
}
</style>
