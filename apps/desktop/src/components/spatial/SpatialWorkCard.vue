<script setup lang="ts">
import { computed, inject, onMounted, onBeforeUnmount, ref, shallowRef, watch, type ComputedRef } from 'vue'
import { Bot, CheckCircle2, Circle, FileCode2, GitBranch, ListTodo, MessageSquare, ShieldCheck, Target, Wrench } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { approvalSummary } from '@/userToolAction'
import type { SpatialObject } from '@/lib/spatialObjects'
import { homeController as c } from '@/controllers/HomeController'
import ToolCallCard from '@/components/chat/ToolCallCard.vue'
import TurnTimeline from '@/components/chat/TurnTimeline.vue'
import MarkdownRender from '@/components/MarkdownRender.vue'
import ApprovalTab from '@/components/ApprovalTab.vue'
import AgentPetAvatar from '@/components/pets/AgentPetAvatar.vue'
import { agentKeyFromHandle, instanceKeyOf } from '@/lib/avatarAssignment'
import { api, type ModelProviderInstanceDto, type OrchestrationSnapshotDto } from '@/api'
import { useAgentDirectory } from '@/composables/useAgentDirectory'
import { readFileText, type ReadFileDataDto } from '@/lib/workspaceSearch'
import type { useSpatialGit } from '@/composables/useSpatialGit'

type RuntimeEngineKind = 'acp' | 'cli' | 'tui' | 'api'
interface RuntimeEngineBadge {
  label: string
  kind: RuntimeEngineKind
}
interface RuntimeEngineMetaCandidate {
  runtime_badge?: string | null
  runtimeBadge?: string | null
  protocol?: string | null
  channel?: string | null
  connection_kind?: string | null
  provider?: string | null
  provider_name?: string | null
  provider_display_name?: string | null
  driver?: string | null
  model?: string | null
  model_id?: string | null
  provider_instance_id?: string | null
  providerInstanceId?: string | null
  recent_actual_model?: {
    protocol?: string | null
    provider_instance_id?: string | null
    model?: string | null
  } | null
}

const KNOWN_DRIVER_NAMES: Record<string, string> = {
  antigravity: 'Antigravity',
  'claude-code': 'Claude Code',
  claude_code: 'Claude Code',
  claude: 'Claude Code',
  codex: 'Codex',
  'codex-cli': 'Codex',
  'gemini-cli': 'Gemini CLI',
  gemini: 'Gemini CLI',
  opencode: 'OpenCode',
  openai: 'OpenAI',
  'openai-chat': 'OpenAI',
  'openai-responses': 'OpenAI',
  anthropic: 'Anthropic',
  'anthropic-messages': 'Anthropic',
}

function humanizeDriver(driver: string): string {
  const key = driver.trim().toLowerCase()
  if (!key) return ''
  if (KNOWN_DRIVER_NAMES[key]) return KNOWN_DRIVER_NAMES[key]!
  return driver
    .trim()
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((part) => (part.length <= 3 ? part.toUpperCase() : part[0]!.toUpperCase() + part.slice(1)))
    .join(' ')
}

function classifyEngineKind(text: string): RuntimeEngineKind {
  const head = text.trim().toLowerCase()
  if (head.startsWith('acp') || head.includes('acp')) return 'acp'
  if (head.startsWith('tui') || head.includes('tui')) return 'tui'
  if (head.startsWith('cli') || head.includes('cli') || head.includes('headless')) return 'cli'
  return 'api'
}

function formatEngineBadge(
  meta?: RuntimeEngineMetaCandidate | null,
  providers?: ReadonlyMap<string, ModelProviderInstanceDto>,
): RuntimeEngineBadge | null {
  if (!meta) return null
  const explicit = (meta.runtime_badge ?? meta.runtimeBadge ?? '').trim()
  if (explicit) {
    const normalized = explicit.replace(/\s*[·•|/]\s*/g, ' · ')
    return { label: normalized, kind: classifyEngineKind(normalized) }
  }
  const providerId = (meta.provider_instance_id ?? meta.providerInstanceId ?? meta.recent_actual_model?.provider_instance_id ?? '').trim()
  const providerRow = providerId && providers ? providers.get(providerId) : undefined
  const channel = (meta.channel ?? providerRow?.channel ?? '').trim().toLowerCase()
  const protocol = (meta.protocol ?? meta.recent_actual_model?.protocol ?? providerRow?.protocol ?? '').trim().toLowerCase()
  const connKind = (meta.connection_kind ?? providerRow?.connection_kind ?? '').trim().toLowerCase()
  const driver = (meta.driver ?? providerRow?.driver ?? '').trim()
  const displayName = (meta.provider_display_name ?? meta.provider_name ?? meta.provider ?? providerRow?.display_name ?? '').trim()
  const model = (meta.model ?? meta.model_id ?? meta.recent_actual_model?.model ?? providerRow?.model ?? '').trim()

  let prefix: string | null = null
  let kind: RuntimeEngineKind = 'api'
  if (channel === 'acp' || protocol === 'acp' || protocol.includes('acp')) {
    prefix = 'ACP'
    kind = 'acp'
  } else if (channel === 'tui' || protocol === 'tui' || protocol.includes('tui')) {
    prefix = 'TUI'
    kind = 'tui'
  } else if (channel === 'cli' || connKind === 'cli' || protocol === 'cli' || protocol.includes('cli') || protocol.includes('headless')) {
    prefix = 'CLI'
    kind = 'cli'
  } else if (protocol || channel === 'http' || channel === 'api' || driver || displayName) {
    prefix = 'API'
    kind = 'api'
  }
  if (!prefix) return null
  const name = displayName || humanizeDriver(driver) || model
  if (!name) return { label: prefix, kind }
  if (name.toUpperCase().startsWith(`${prefix} ·`) || name.toUpperCase() === prefix) return { label: name, kind }
  return { label: `${prefix} · ${name}`, kind }
}

const props = withDefaults(defineProps<{ object?: SpatialObject; preview?: boolean; compact?: boolean; detail?: boolean; macro?: boolean }>(), { compact: undefined, macro: undefined })
const { t, te } = useI18n()
const state = inject<Record<string, unknown>>('uie:cardState', {})
const objects = inject<ComputedRef<Record<string, SpatialObject>>>('space:objects')
const compactItems = inject<ComputedRef<Record<string, boolean>>>('space:compact')
const macroLod = inject<ComputedRef<boolean>>('space:macro')
const open = inject<(id: string) => void>('space:open')
const locate = inject<(id: string) => void>('space:locate')
const focusInstance = inject<(runId: string, instanceId: string) => void>('space:focus-instance')
const git = inject<ReturnType<typeof useSpatialGit>>('space:git')
const object = computed(() => props.object ?? objects?.value[String(state.objectId)])
const isCompact = computed(() => !props.detail && (props.compact ?? compactItems?.value[object.value?.id ?? ''] ?? false))
const isMacro = computed(() => !props.detail && !props.preview && (props.macro ?? macroLod?.value ?? false))
const statusTone = computed(() => {
  const s = object.value?.status
  if (!s) return 'neutral'
  if (['running', 'in_progress', 'planning'].includes(s)) return 'active'
  if (['completed', 'succeeded', 'approved'].includes(s)) return 'success'
  if (['blocked', 'waiting_approval', 'awaiting_user', 'awaiting_approval'].includes(s)) return 'warning'
  if (['failed', 'rejected', 'cancelled'].includes(s)) return 'danger'
  return 'neutral'
})
const runId = computed(() => object.value?.runId ?? (object.value && ['run', 'task', 'plan', 'tool', 'result'].includes(object.value.kind) ? object.value.groupId : undefined))
const icons = { message: MessageSquare, meeting: Bot, run: Target, result: MessageSquare, plan: ListTodo, task: FileCode2, tool: Wrench, git: GitBranch, approval: ShieldCheck }
/**
 * The owner pill is the card's "who is doing this", so the face lives there rather than next to
 * the title (that glyph is the object's kind, not the agent). The instance key is the same
 * `runId:taskId` the conversation uses, so a task wears one face everywhere it appears.
 */
const ownerInstanceKey = computed(() => object.value?.runId && object.value?.taskId
  ? instanceKeyOf(object.value.runId, object.value.taskId)
  : '')
const ownerAgentKey = computed(() => agentKeyFromHandle(object.value?.owner))
const ownerFocusable = computed(() => Boolean(object.value?.owner && !props.preview && object.value.instanceId && runId.value && focusInstance))

const agentDirectory = typeof api?.listAgents === 'function' ? useAgentDirectory() : null
const providerCatalog = shallowRef<ModelProviderInstanceDto[]>([])
onMounted(() => {
  if (!props.preview && typeof api?.listModelProviders === 'function') {
    void api.listModelProviders().then((rows) => {
      if (Array.isArray(rows)) providerCatalog.value = rows
    }).catch(() => {})
  }
})
const providerById = computed(() => {
  const map = new Map<string, ModelProviderInstanceDto>()
  for (const row of providerCatalog.value) {
    if (row?.id) map.set(row.id, row)
  }
  return map
})

const runtimeBadge = computed<RuntimeEngineBadge | null>(() => {
  const obj = object.value
  if (!obj) return null
  const direct = formatEngineBadge(obj as unknown as RuntimeEngineMetaCandidate, providerById.value)
  if (direct) return direct

  const snap = (c as unknown as { orchestration?: { value?: OrchestrationSnapshotDto | null } })?.orchestration?.value ?? null
  if (snap && (!runId.value || !snap.run?.id || snap.run.id === runId.value)) {
    const snapAny = snap as unknown as {
      teammates?: Array<RuntimeEngineMetaCandidate & { handle?: string | null; agent_slug?: string | null; agentSlug?: string | null; task_id?: string | null; taskId?: string | null }>
      agent_instances?: Array<RuntimeEngineMetaCandidate & { id?: string; instance_id?: string; task_id?: string | null; role?: string | null; agent_slug?: string | null }>
    }
    for (const tm of snapAny.teammates ?? []) {
      const tmSlug = tm.agent_slug ?? tm.agentSlug
      if (
        (obj.taskId && (tm.task_id === obj.taskId || tm.taskId === obj.taskId))
        || (obj.owner && (tm.handle === obj.owner || tmSlug === obj.owner || tmSlug === ownerAgentKey.value))
      ) {
        const hit = formatEngineBadge(tm, providerById.value)
        if (hit) return hit
      }
    }
    if (obj.taskId) {
      const node = snap.nodes?.find((n) => n.id === obj.taskId) as (RuntimeEngineMetaCandidate & { id: string }) | undefined
      const nodeHit = formatEngineBadge(node, providerById.value)
      if (nodeHit) return nodeHit
      const assign = snap.assignments?.find((a) => String(a.task_node_id) === obj.taskId) as RuntimeEngineMetaCandidate | undefined
      const assignHit = formatEngineBadge(assign, providerById.value)
      if (assignHit) return assignHit
    }
    for (const inst of snapAny.agent_instances ?? []) {
      const instId = inst.instance_id ?? inst.id
      if (
        (obj.instanceId && instId && String(instId) === obj.instanceId)
        || (obj.taskId && inst.task_id && String(inst.task_id) === obj.taskId)
      ) {
        const hit = formatEngineBadge(inst, providerById.value)
        if (hit) return hit
      }
    }
  }

  const slug = ownerAgentKey.value
  if (slug && agentDirectory) {
    const agentRow = agentDirectory.items.value.find((row) => row.slug === slug || row.id === slug)
    if (agentRow) {
      if (agentRow.recent_invocation && (!runId.value || agentRow.recent_invocation.run_id === runId.value)) {
        const hit = formatEngineBadge({
          protocol: agentRow.recent_invocation.protocol,
          provider_instance_id: agentRow.recent_invocation.provider_instance_id,
          model: agentRow.recent_invocation.model ?? null,
        }, providerById.value)
        if (hit) return hit
      }
      if (agentRow.model_binding?.mode === 'fixed' && agentRow.model_binding.provider_instance_id) {
        const hit = formatEngineBadge({
          provider_instance_id: agentRow.model_binding.provider_instance_id,
          model: agentRow.model_binding.model ?? null,
        }, providerById.value)
        if (hit) return hit
      }
      if (agentRow.configured_strategy?.kind === 'fixed' && agentRow.configured_strategy.provider_instance_id) {
        return formatEngineBadge({
          provider_instance_id: agentRow.configured_strategy.provider_instance_id,
          model: agentRow.configured_strategy.model ?? null,
        }, providerById.value)
      }
    }
  }

  return null
})

function focusOwner() {
  if (!runId.value || !object.value?.instanceId || !focusInstance) return
  focusInstance(runId.value, object.value.instanceId)
}
const kindTitle = (kind: SpatialObject['kind']) => t(kind === 'run' || kind === 'result' ? 'space.cluster.' + kind : 'space.kind.' + kind)
const completed = computed(() => object.value?.rows?.filter(r => ['completed', 'succeeded'].includes(r.status)).length ?? 0)
const status = (s?: string) => s && te('space.status.' + s) ? t('space.status.' + s) : s
// ApprovalCard forwards the controller's entire array. Scope here before reusing its view.
const approvalSessionId = computed(() => c.selectedSessionId.value && object.value?.id === `approval:${c.selectedSessionId.value}` ? c.selectedSessionId.value : null)
const approvals = computed(() => approvalSessionId.value ? c.approvals.value.filter(a => a.session_id === approvalSessionId.value) : [])
const approvalRules = computed(() => approvalSessionId.value ? c.approvalRules.value.filter(r => r.session_id === approvalSessionId.value) : [])
const summary = computed(() => object.value?.body || object.value?.tool?.argsSummary || object.value?.queue?.at(-1)?.content || (object.value?.kind === 'approval' ? approvals.value.map(a => a.summary).join('\n') : ''))
const turn = computed(() => props.detail && object.value?.kind === 'run' && runId.value ? c.agentTurnActivities.value[runId.value] : undefined)
const related = computed(() => props.detail && object.value?.kind === 'run' && runId.value
  ? Object.values(objects?.value ?? {}).filter(item => ['plan', 'tool'].includes(item.kind) && (item.runId ?? item.groupId) === runId.value)
  : [])
const draftOccupied = computed(() => Boolean(c.draft.value.trim()))
const decideApproval = c.decideApproval
function editQueued(id: string) {
  if (!props.preview && !draftOccupied.value) void c.editQueued(id)
}

const cardElement = ref<HTMLElement | null>(null)
const measure = inject<(id: string, height: number) => void>('space:measure')
let sizeObserver: ResizeObserver | undefined
onMounted(() => {
  if (props.preview || props.detail || !cardElement.value || !measure) return
  sizeObserver = new ResizeObserver(entries => {
    if (!props.preview && !props.detail && object.value && cardElement.value) {
      measure(object.value.id, entries[0]?.borderBoxSize?.[0]?.blockSize ?? cardElement.value.offsetHeight)
    }
  })
  sizeObserver.observe(cardElement.value)
})
onBeforeUnmount(() => { sizeObserver?.disconnect(); fileRead++ })

const file = ref<string | null>(null)
const code = ref('')
const codeError = ref('')
/** What the read tool itself said, for the tooltip, when `codeError` is the worded failure. */
const codeErrorDetail = ref<string>()
const reading = ref(false)
let fileRead = 0
watch(() => git?.state.value.cwd, () => { fileRead++; file.value = null; code.value = ''; codeError.value = ''; reading.value = false })
async function readFile(path: string) {
  const cwd = git?.state.value.cwd
  if (!cwd || props.preview) return
  const read = ++fileRead
  file.value = path; code.value = ''; codeError.value = ''; codeErrorDetail.value = undefined; reading.value = true
  try {
    const result = await api.readFile(cwd, path, { start_row: 1, end_row: 500 })
    if (read !== fileRead) return
    const data = result.data as ReadFileDataDto
    if (result.status === 'failed' || data.success === false) {
      codeError.value = t('space.fileReadFailed')
      codeErrorDetail.value = typeof result.data.error === 'string' ? result.data.error : result.summary
      return
    }
    code.value = readFileText(data)
  } catch (error) { if (read === fileRead) codeError.value = error instanceof Error ? error.message : String(error) }
  finally { if (read === fileRead) reading.value = false }
}
</script>
<template>
  <article
    ref="cardElement"
    v-if="object"
    class="spatial-work-card"
    :class="{ 'is-compact': isCompact, 'is-detail': detail, 'is-macro': isMacro }"
    :data-object-id="object.id"
    :data-lod="detail ? 'detail' : isMacro ? 'macro' : 'normal'"
    :data-status-tone="statusTone"
  >
    <header class="space-drag-handle">
      <component :is="icons[object.kind]" :size="isMacro ? 22 : 18" class="space-kind-icon" aria-hidden="true" />
      <h2>{{ object.title || kindTitle(object.kind) }}</h2>
    </header>
    <div v-if="object.owner || runtimeBadge || runId || object.status" class="space-card-meta nodrag nopan">
      <component
        :is="ownerFocusable ? 'button' : 'span'"
        v-if="object.owner"
        class="space-owner"
        :type="ownerFocusable ? 'button' : undefined"
        :title="ownerFocusable ? t('space.cluster.focusInstance') : undefined"
        @click="focusOwner"
      >
        <AgentPetAvatar v-if="ownerInstanceKey" :instance-key="ownerInstanceKey" :agent-id="ownerAgentKey" :size="14" />
        {{ object.owner }}
      </component>
      <span
        v-if="runtimeBadge && !isMacro"
        class="space-runtime-badge"
        :data-engine-kind="runtimeBadge.kind"
        :title="runtimeBadge.label"
        data-testid="space-runtime-badge"
      >{{ runtimeBadge.label }}</span>
      <button v-if="!isMacro && runId && !preview && locate && object.kind !== 'run'" type="button" class="space-run" :title="t('space.cluster.locateRun', { id: runId })" @click="locate(`tasks:${runId}`)">{{ t('space.cluster.runTag', { id: runId.slice(0, 8) }) }}</button>
      <span v-else-if="!isMacro && runId" class="space-run" :title="runId">{{ t('space.cluster.runTag', { id: runId.slice(0, 8) }) }}</span>
      <small v-if="object.status" class="space-card-status" :data-tone="statusTone"><span class="space-status-dot" aria-hidden="true" />{{ status(object.status) }}</small>
    </div>
    <div v-if="!isMacro" class="space-work-body nodrag nopan nowheel">
      <template v-if="isCompact">
        <p v-if="summary" class="space-work-text space-summary">{{ summary }}</p>
        <p v-if="object.kind === 'git'" class="space-muted">{{ t('space.cluster.sharedGit') }}<br>{{ t('space.filesChanged', { count: git?.state.value.preview.files?.length ?? 0 }) }}</p>
        <p v-if="object.kind === 'approval' && !approvals.length" class="space-muted">{{ t('space.noApprovals') }}</p>
      </template>
      <template v-else-if="object.kind === 'meeting'">
        <div v-if="object.queue?.length" class="space-meeting-queue">
          <div v-for="item in object.queue" :key="item.id" class="space-queue-row">
            <CheckCircle2 v-if="['completed', 'succeeded'].includes(item.status)" :size="16" class="space-success" /><Circle v-else :size="16" />
            <div class="space-queue-content">
              <span>{{ item.content }}</span>
              <small>{{ !item.runId && !item.editable ? t('space.cluster.unlinkedMessage') : status(item.status) }}</small>
              <div v-if="!preview && item.editable" class="space-queue-actions">
                <button type="button" :disabled="draftOccupied" :title="draftOccupied ? t('space.cluster.draftOccupied') : t('composer.edit')" @click="editQueued(item.id)">{{ t('composer.edit') }}</button>
                <button type="button" @click="c.dismissQueued(item.id)">{{ t('composer.dismiss') }}</button>
              </div>
            </div>
          </div>
        </div>
        <p v-if="object.body" class="space-muted">{{ t('space.cluster.unlinkedMessage') }}</p>
        <MarkdownRender v-if="object.body && detail && !preview" :content="object.body" />
        <p v-else-if="object.body" class="space-work-text">{{ object.body }}</p>
      </template>
      <template v-else-if="object.kind === 'git'">
        <p class="space-muted">{{ t('space.cluster.sharedGit') }}</p>
        <p v-if="!git?.state.value.cwd" class="space-muted">{{ t('space.noProject') }}</p>
        <template v-else>
          <button v-if="!preview" type="button" class="space-git-refresh" @click="git?.refresh()">{{ t('space.refresh') }}</button>
          <p v-if="git?.state.value.error" class="space-muted" role="status" :title="git.state.value.errorDetail">{{ git.state.value.error }}</p>
          <p v-if="git?.state.value.loading" class="space-muted">{{ t('space.loading') }}</p>
          <div v-if="git?.state.value.loaded" class="space-todo-row"><GitBranch :size="20" /><div>{{ git?.state.value.preview.branch }}<small>↑ {{ git?.state.value.preview.ahead ?? 0 }} · ↓ {{ git?.state.value.preview.behind ?? 0 }}</small></div></div>
          <div v-for="commit in git?.state.value.commits.slice(0, 3)" :key="commit.hash" class="space-git-commit"><code>{{ commit.short_hash }}</code> {{ commit.subject }}</div>
          <p v-if="git?.state.value.loaded" class="space-muted">{{ t('space.filesChanged', { count: git?.state.value.preview.files?.length ?? 0 }) }}</p>
          <div v-for="item in git?.state.value.preview.files" :key="item.path" class="space-git-file">
            <span v-if="preview">{{ item.path }}</span><button v-else type="button" @click="readFile(item.path)"><FileCode2 :size="16" />{{ item.path }}</button>
          </div>
          <details v-if="file && !preview" open class="space-file-view"><summary>{{ file }} · {{ t('space.currentFile') }}</summary><p v-if="reading">{{ t('space.loading') }}</p><p v-else-if="codeError" role="status" :title="codeErrorDetail">{{ codeError }}</p><pre v-else>{{ code }}</pre></details>
        </template>
      </template>
      <template v-else-if="object.kind === 'approval'">
        <ApprovalTab v-if="!preview && approvalSessionId" compact :approvals="approvals" :approval-rules="approvalRules" :busy="c.busy.value" :selected-session-id="approvalSessionId"
          @decide-approval="decideApproval"
          @batch-decide="c.batchDecideApprovals($event)"
          @revoke-approval-rule="c.revokeApprovalRule($event)"
          @create-approval-rule="c.createApprovalRule($event)" />
        <template v-else>
          <p v-if="!approvals.length" class="space-muted">{{ t('space.noApprovals') }}</p>
          <div v-for="a in approvals" :key="a.id" class="space-todo-row"><ShieldCheck :size="18" /><div>{{ approvalSummary(a, { t, te }) }}<small>{{ status(a.status) }}</small></div></div>
        </template>
      </template>
      <ToolCallCard v-else-if="object.kind === 'tool' && object.tool && !preview" :tool-call="object.tool" :run-id="runId" @approve="c.decideApprovalById($event, 'approved')" @reject="c.decideApprovalById($event, 'rejected')" />
      <template v-else>
        <MarkdownRender v-if="object.body && detail && !preview" :content="object.body" />
        <p v-else-if="object.body || object.tool" class="space-work-text">{{ object.body || object.tool?.argsSummary }}</p>
        <div v-for="row in object.rows" :key="row.id" class="space-todo-row">
          <CheckCircle2 v-if="['completed', 'succeeded'].includes(row.status)" :size="20" class="space-success" /><Circle v-else :size="20" />
          <div><button v-if="!preview && locate" type="button" class="space-row-link" @click="locate(row.id)">{{ row.title }}</button><span v-else>{{ row.title }}</span><small>{{ row.detail }} {{ status(row.status) }}</small></div>
        </div>
        <p v-if="object.kind === 'task' && !object.body" class="space-muted">{{ t('space.cluster.noTaskResult') }}</p>
        <p v-else-if="object.kind === 'plan' && !object.body && !object.rows?.length" class="space-muted">{{ t('space.noPlan') }}</p>
      </template>
      <p v-if="object.waitingFor?.length" class="space-muted space-waiting" :class="{ 'space-summary': isCompact }">{{ t('space.cluster.waitingFor', { tasks: object.waitingFor.join(t('conversation.listSeparator')) }) }}</p>
      <p v-if="object.unresolvedDependencies?.length" class="space-muted space-unresolved" :class="{ 'space-summary': isCompact }">{{ t('space.cluster.unresolvedDependencies', { ids: object.unresolvedDependencies.join(t('conversation.listSeparator')) }) }}</p>
      <p v-if="object.dependencyUnverified" class="space-muted">{{ t('space.cluster.unverifiedDependencies') }}</p>
      <p v-if="object.truncated" class="space-muted">{{ t('space.cluster.truncated') }}</p>
      <template v-if="detail && !preview && object.kind === 'run'">
        <h3>{{ t('space.cluster.loadedActivity') }}</h3>
        <TurnTimeline v-if="turn" :run-id="runId" :thinking-steps="turn.thinkingSteps" :tool-calls="turn.toolCalls" :supervision-review="turn.supervisionReview" @approve="c.decideApprovalById($event, 'approved')" @reject="c.decideApprovalById($event, 'rejected')" />
        <p v-else class="space-muted">{{ t('space.cluster.noLoadedActivity') }}</p>
        <section v-if="related.length" class="space-related">
          <h3>{{ t('space.cluster.relatedActivity') }}</h3>
          <div v-for="item in related" :key="item.id" class="space-todo-row">
            <button v-if="locate" type="button" class="space-row-link" @click="locate(item.id)">{{ item.title || kindTitle(item.kind) }}</button><span v-else>{{ item.title || kindTitle(item.kind) }}</span>
          </div>
        </section>
      </template>
    </div>
    <footer v-if="!isMacro && (object.rows?.length || (!preview && !detail && open))">
      <span v-if="object.rows?.length">{{ t('space.progress', { done: completed, total: object.rows.length }) }}</span>
      <button v-if="!preview && !detail && open" type="button" class="space-open-detail nodrag nopan" :data-open-object="object.id" @click="open(object.id)">{{ t('space.cluster.openDetail') }}</button>
    </footer>
  </article>
</template>
<style scoped>
.spatial-work-card { height: 100%; display: flex; flex-direction: column; min-width: 0; color: var(--text-primary); background: var(--surface-raised); backdrop-filter: var(--material-filter-raised, none); border: 1px solid var(--border-card, var(--border-muted)); border-radius: var(--radius-island, 12px); overflow: hidden; transition: border-color 140ms ease, box-shadow 140ms ease; }
.spatial-work-card.is-detail { border: 0; border-radius: 0; }
.space-drag-handle { display: flex; align-items: flex-start; gap: 9px; padding: 14px 18px 10px; cursor: grab; flex-shrink: 0; }
.space-kind-icon { color: var(--text-secondary); margin-top: 2px; }
h2 { font-size: 16px; font-weight: 600; line-height: 1.28; margin: 0; overflow-wrap: anywhere; display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 3; overflow: hidden; }
h3 { font-size: 13px; font-weight: 600; line-height: 1.6; margin: 16px 0 8px; }
.space-drag-handle svg { flex-shrink: 0; }
.is-detail .space-drag-handle { cursor: default; }
.space-card-meta { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; padding: 0 18px 10px; font-size: 11px; color: var(--text-secondary); }
.space-owner { display: inline-flex; align-items: center; gap: 4px; max-width: 100%; padding: 2px 7px; border-radius: 6px; border: 1px solid var(--border-muted); background: var(--surface-section); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.space-runtime-badge { display: inline-flex; align-items: center; padding: 1px 6px; border-radius: 6px; border: 1px solid var(--border-card, var(--border-muted)); background: var(--surface-section); color: var(--text-secondary); font-size: 11px; font-weight: 500; white-space: nowrap; flex-shrink: 0; }
.space-runtime-badge[data-engine-kind='acp'] { border-color: color-mix(in srgb, var(--accent-primary) 32%, var(--border-muted)); background: color-mix(in srgb, var(--accent-primary) 10%, transparent); color: var(--accent-primary); }
.space-runtime-badge[data-engine-kind='cli'],
.space-runtime-badge[data-engine-kind='tui'] { border-color: color-mix(in srgb, var(--accent-info, var(--accent-primary)) 32%, var(--border-muted)); background: color-mix(in srgb, var(--accent-info, var(--accent-primary)) 10%, transparent); color: var(--accent-info, var(--text-primary)); }
.space-run { font-family: var(--font-mono); font-variant-numeric: tabular-nums; white-space: nowrap; }
.space-card-status { margin-left: auto; display: inline-flex; align-items: center; gap: 5px; font-size: 11px; font-weight: 500; padding: 2px 8px; border-radius: 999px; border: 1px solid var(--border-muted); background: var(--surface-section); color: var(--text-secondary); white-space: nowrap; flex-shrink: 0; }
.space-status-dot { width: 6px; height: 6px; border-radius: 999px; background: var(--text-muted); flex-shrink: 0; }
.space-card-status[data-tone='active'] { color: var(--accent-primary); border-color: color-mix(in srgb, var(--accent-primary) 34%, var(--border-muted)); background: color-mix(in srgb, var(--accent-primary) 10%, transparent); }
.space-card-status[data-tone='active'] .space-status-dot { background: var(--accent-primary); }
.space-card-status[data-tone='success'] { color: var(--accent-success, #22c55e); border-color: color-mix(in srgb, var(--accent-success, #22c55e) 34%, var(--border-muted)); background: color-mix(in srgb, var(--accent-success, #22c55e) 10%, transparent); }
.space-card-status[data-tone='success'] .space-status-dot { background: var(--accent-success, #22c55e); }
.space-card-status[data-tone='warning'] { color: var(--accent-warning, #f59e0b); border-color: color-mix(in srgb, var(--accent-warning, #f59e0b) 34%, var(--border-muted)); background: color-mix(in srgb, var(--accent-warning, #f59e0b) 10%, transparent); }
.space-card-status[data-tone='warning'] .space-status-dot { background: var(--accent-warning, #f59e0b); }
.space-card-status[data-tone='danger'] { color: var(--accent-danger, #ef4444); border-color: color-mix(in srgb, var(--accent-danger, #ef4444) 34%, var(--border-muted)); background: color-mix(in srgb, var(--accent-danger, #ef4444) 10%, transparent); }
.space-card-status[data-tone='danger'] .space-status-dot { background: var(--accent-danger, #ef4444); }
.spatial-work-card.is-macro { justify-content: center; gap: 10px; padding: 8px 4px; }
.spatial-work-card.is-macro .space-drag-handle { padding: 12px 18px 4px; align-items: center; }
.spatial-work-card.is-macro h2 { font-size: 20px; line-height: 1.25; -webkit-line-clamp: 2; }
.spatial-work-card.is-macro .space-card-meta { padding: 0 18px 14px; font-size: 13px; gap: 8px; }
.spatial-work-card.is-macro .space-owner { padding: 4px 10px; font-size: 13px; }
.spatial-work-card.is-macro .space-card-status { padding: 4px 10px; font-size: 13px; }
.space-work-body { padding: 4px 18px 14px; flex: 1; min-height: 0; overflow: auto; user-select: text; }
.space-work-text { white-space: pre-wrap; overflow-wrap: anywhere; font-size: 14px; line-height: 1.65; margin: 0 0 16px; }
.space-muted { color: var(--text-muted); line-height: 1.7; font-size: 13px; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.space-summary { display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow: hidden; margin: 0 0 8px; }
.space-todo-row { display: flex; align-items: flex-start; gap: 8px; margin-bottom: 16px; font-size: 14px; }
.space-todo-row > div { min-width: 0; overflow-wrap: anywhere; }
.space-todo-row svg { flex-shrink: 0; margin-top: 2px; }
.space-todo-row small { display: block; margin-top: 7px; color: var(--text-muted); font: 12px var(--font-mono, monospace); font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.space-success { color: var(--accent-success); }
footer { display: flex; align-items: center; gap: 8px; flex-shrink: 0; padding: 8px 18px 14px; font-size: 12px; color: var(--text-secondary); font-variant-numeric: tabular-nums; }
button { font: inherit; color: inherit; border: 0; background: transparent; cursor: pointer; }
button:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 2px; }
button:hover:not(:disabled) { color: var(--accent-primary); }
button:disabled { cursor: not-allowed; opacity: .5; }
.space-open-detail { margin-left: auto; }
.space-row-link { padding: 0; text-align: left; overflow-wrap: anywhere; }
.space-git-refresh { float: right; color: var(--text-secondary); }
.space-git-commit { font-size: 12px; color: var(--text-secondary); margin-bottom: 8px; overflow-wrap: anywhere; }
.space-git-file { font-size: 13px; margin: 8px 0; overflow-wrap: anywhere; }
.space-git-file button { display: flex; text-align: left; align-items: center; gap: 8px; padding: 0; }
.space-git-file svg { flex-shrink: 0; }
.space-file-view { margin-top: 12px; font-size: 12px; }
.space-file-view pre { font: 12px/1.6 var(--font-mono, monospace); overflow: auto; max-height: 360px; user-select: text; }
.space-meeting-queue { display: flex; flex-direction: column; gap: 8px; margin-bottom: 12px; }
.space-queue-row { display: flex; align-items: start; gap: 8px; font-size: 13px; line-height: 1.5; }
.space-queue-row svg { flex-shrink: 0; margin-top: 2px; }
.space-queue-content { flex: 1; min-width: 0; overflow-wrap: anywhere; white-space: pre-wrap; }
.space-queue-content small { display: block; color: var(--text-muted); }
.space-queue-actions { display: flex; gap: 8px; margin-top: 4px; }
.is-compact .space-work-body { flex: 0 1 auto; }
.is-detail .space-work-body :deep(.markdown-body) { font-size: 14px; line-height: 1.65; overflow-wrap: anywhere; }
.is-detail .space-work-body :deep(.markdown-body pre) { max-width: 100%; overflow: auto; }
</style>
