<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertTriangle,
  Check,
  ExternalLink,
  FileDiff,
  GitBranch,
  LoaderCircle,
  Play,
  Plus,
  RefreshCw,
  RotateCcw,
  Send,
  Square,
  Trash2,
  Waypoints,
  X,
} from '@lucide/vue'
import {
  UiButton,
  UiCheckbox,
  UiInput,
  UiSelectField,
  UiSheet,
  UiSwitch,
  UiTextarea,
} from '@/components/ui'
import {
  api,
  type AgentModeDto,
  type ApproveProjectBoardTaskInput,
  type CliDiscoveryCandidateDto,
  type CreateProjectBoardTaskInput,
  type DispatchProjectBoardTaskInput,
  type MessageDto,
  type ModelProviderInstanceDto,
  type ProjectBoardAssigneeDto,
  type ProjectBoardAssigneeKind,
  type ProjectBoardChecklistItemDto,
  type ProjectBoardConflictResolution,
  type ProjectBoardTaskDiffDto,
  type ProjectBoardTaskDto,
  type ProjectBoardTaskPriority,
  type ProjectBoardTaskStatus,
  type UpdateProjectBoardTaskInput,
} from '@/api'
import { useHostAccess } from '@/lib/hostAccess'
import { pickLocalized } from '@/lib/packI18n'
import { useLiveDigest } from '@/composables/useLiveDigest'
import { useNotifications } from '@/composables/useNotifications'
import {
  BOARD_ASSIGNEE_KINDS,
  BOARD_TASK_PRIORITIES,
  BOARD_TASK_STATUSES,
} from './useProjectBoardView'

const props = defineProps<{
  open: boolean
  mode: 'create' | 'edit'
  task: ProjectBoardTaskDto | null
  projectTasks: readonly ProjectBoardTaskDto[]
  defaultProjectId?: string | null
  defaultStatus?: ProjectBoardTaskStatus
  projects?: Array<{ id: string; name: string }>
}>()

const emit = defineEmits<{
  'update:open': [value: boolean]
  'create-task': [projectId: string, input: CreateProjectBoardTaskInput]
  'save-task': [projectId: string, taskId: string, patch: UpdateProjectBoardTaskInput]
  'delete-task': [projectId: string, taskId: string]
  'resolve-conflict': [projectId: string, taskId: string, resolution: ProjectBoardConflictResolution]
  'dispatch-task': [projectId: string, taskId: string, input: DispatchProjectBoardTaskInput]
  'cancel-task': [projectId: string, taskId: string]
  'approve-task': [projectId: string, taskId: string, input: ApproveProjectBoardTaskInput]
  'request-changes': [projectId: string, taskId: string, feedback: string]
  'open-session': [sessionId: string]
  'jump-spatial': [task: ProjectBoardTaskDto]
}>()

const { t, locale } = useI18n()
const { canAccessBackend } = useHostAccess()
const { notify, dismissByKey } = useNotifications()

const DRAWER_ERROR_KEY = 'board-task-drawer'

// ---- Editable Form State ----
const targetProjectId = ref('')
const title = ref('')
const descriptionMd = ref('')
const status = ref<ProjectBoardTaskStatus>('ready')
const priority = ref<ProjectBoardTaskPriority>('medium')
const blocked = ref(false)
const blockedReason = ref('')
const checklist = ref<ProjectBoardChecklistItemDto[]>([])
const newChecklistText = ref('')
const labels = ref<string[]>([])
const newLabelText = ref('')
const dependsOn = ref<string[]>([])

// Assignee & Dispatch controls
const assigneeKind = ref<ProjectBoardAssigneeKind>('native')
const assigneeModeVersionId = ref('')
const assigneeHarnessId = ref('')
const assigneeDisplayName = ref('')
const useWorktree = ref(true)
const extraDispatchPrompt = ref('')

// Catalogs for Native Modes & External CLI Harnesses
const publishedModes = ref<AgentModeDto[]>([])
const cliHarnesses = ref<Array<{ id: string; label: string }>>([])
const catalogsLoaded = ref(false)

// Live execution & Steer
const sessionMessages = ref<MessageDto[]>([])
const loadingMessages = ref(false)
const steerDraft = ref('')
const sendingSteer = ref(false)
const steerSentNotice = ref(false)

// Diff & Review
const taskDiff = ref<ProjectBoardTaskDiffDto | null>(null)
const loadingDiff = ref(false)
const mergeOrCleanWorktree = ref(true)
const reviewFeedback = ref('')

const activeRunId = computed(() => props.task?.active_run_id ?? null)
const liveDigest = useLiveDigest(activeRunId, {
  autoStream: () => Boolean(props.open && props.task?.active_run_id),
})

const effectiveDigestHeadline = computed(() => {
  return liveDigest.headline.value || props.task?.live_digest_summary || ''
})

const isRunActive = computed(() => {
  if (!props.task?.active_run_id) return false
  const st = (props.task.run_status ?? '').toLowerCase()
  return st !== 'completed' && st !== 'failed' && st !== 'cancelled'
})

function syncFormFromProps() {
  dismissByKey(DRAWER_ERROR_KEY)
  steerDraft.value = ''
  steerSentNotice.value = false
  reviewFeedback.value = ''
  extraDispatchPrompt.value = ''

  if (props.mode === 'create' || !props.task) {
    targetProjectId.value = props.defaultProjectId || props.projects?.[0]?.id || ''
    title.value = ''
    descriptionMd.value = ''
    status.value = props.defaultStatus ?? 'ready'
    priority.value = 'medium'
    blocked.value = false
    blockedReason.value = ''
    checklist.value = []
    labels.value = []
    dependsOn.value = []
    assigneeKind.value = 'native'
    assigneeModeVersionId.value = publishedModes.value[0]?.latest_published_mode_version_id ?? ''
    assigneeHarnessId.value = cliHarnesses.value[0]?.id ?? ''
    assigneeDisplayName.value = ''
    taskDiff.value = null
    sessionMessages.value = []
    return
  }

  const cur = props.task
  targetProjectId.value = cur.project_id
  title.value = cur.title
  descriptionMd.value = cur.description_md ?? ''
  status.value = cur.status
  priority.value = cur.priority
  blocked.value = cur.blocked
  blockedReason.value = cur.blocked_reason ?? ''
  checklist.value = cur.checklist.map((item) => ({ ...item }))
  labels.value = [...cur.labels]
  dependsOn.value = [...cur.depends_on]
  assigneeKind.value = cur.assignee?.kind ?? 'human'
  assigneeModeVersionId.value = cur.assignee?.mode_version_id ?? ''
  assigneeHarnessId.value = cur.assignee?.harness_id ?? cur.assignee?.id ?? ''
  assigneeDisplayName.value = cur.assignee?.display_name ?? ''
}

async function loadAssigneeCatalogs() {
  if (catalogsLoaded.value || !canAccessBackend.value) return
  catalogsLoaded.value = true
  try {
    const [modes, providers, discovered] = await Promise.all([
      api.listAgentModes().catch(() => [] as AgentModeDto[]),
      api.listModelProviders().catch(() => [] as ModelProviderInstanceDto[]),
      api.discoverHarnesses().catch(() => ({ cli_runtimes: [] as CliDiscoveryCandidateDto[] })),
    ])
    publishedModes.value = modes.filter(
      (m) => m.status === 'published' && Boolean(m.latest_published_mode_version_id),
    )

    const harnessMap = new Map<string, string>()
    for (const p of providers) {
      if (p.connection_kind === 'cli' || p.channel === 'cli' || p.channel === 'acp') {
        harnessMap.set(p.driver || p.id, `${p.display_name} (${p.driver})`)
      }
    }
    for (const candidate of discovered.cli_runtimes ?? []) {
      if (!harnessMap.has(candidate.driver)) {
        harnessMap.set(
          candidate.driver,
          candidate.status === 'missing'
            ? `${candidate.display_name} (${t('board.drawer.notConfigured')})`
            : candidate.display_name,
        )
      }
    }
    if (harnessMap.size === 0) {
      harnessMap.set('claude-code', 'Claude Code')
      harnessMap.set('codex', 'Codex CLI')
      harnessMap.set('gemini-cli', 'Gemini CLI')
      harnessMap.set('opencode', 'OpenCode')
    }
    cliHarnesses.value = [...harnessMap.entries()].map(([id, label]) => ({ id, label }))

    if (!assigneeModeVersionId.value && publishedModes.value[0]?.latest_published_mode_version_id) {
      assigneeModeVersionId.value = publishedModes.value[0].latest_published_mode_version_id
    }
    if (!assigneeHarnessId.value && cliHarnesses.value[0]) {
      assigneeHarnessId.value = cliHarnesses.value[0].id
    }
  } catch {
    // Catalog failure degrades cleanly without blocking task editing.
  }
}

async function loadTaskDiff() {
  if (!props.task || !canAccessBackend.value) return
  loadingDiff.value = true
  try {
    taskDiff.value = await api.getProjectBoardTaskDiff(props.task.project_id, props.task.task_id)
  } catch {
    taskDiff.value = null
  } finally {
    loadingDiff.value = false
  }
}

async function loadSessionMessages() {
  const sid = props.task?.active_session_id
  if (!sid || !canAccessBackend.value) {
    sessionMessages.value = []
    return
  }
  loadingMessages.value = true
  try {
    const msgs = await api.listMessages(sid)
    sessionMessages.value = Array.isArray(msgs) ? msgs.slice(-6) : []
  } catch {
    sessionMessages.value = []
  } finally {
    loadingMessages.value = false
  }
}

watch(
  () => [props.open, props.mode, props.task?.task_id, props.task?.revision] as const,
  ([isOpen]) => {
    if (!isOpen) {
      dismissByKey(DRAWER_ERROR_KEY)
      return
    }
    syncFormFromProps()
    void loadAssigneeCatalogs()
    if (props.mode === 'edit' && props.task) {
      if (props.task.active_session_id) void loadSessionMessages()
      if (props.task.status === 'in_review' || props.task.worktree_path || props.task.diff_stats) {
        void loadTaskDiff()
      } else {
        taskDiff.value = null
      }
    }
  },
  { immediate: true },
)

watch(canAccessBackend, (ready, wasReady) => {
  if (ready && wasReady === false) {
    dismissByKey(DRAWER_ERROR_KEY)
    if (props.open) {
      void loadAssigneeCatalogs()
      if (props.mode === 'edit' && props.task) {
        if (props.task.active_session_id) void loadSessionMessages()
        if (props.task.status === 'in_review' || props.task.worktree_path || props.task.diff_stats) {
          void loadTaskDiff()
        }
      }
    }
  }
})

onBeforeUnmount(() => {
  dismissByKey(DRAWER_ERROR_KEY)
})

const modeSelectOptions = computed(() => [
  { value: '', label: t('board.drawer.workspaceDefaultMode') },
  ...publishedModes.value.map((m) => ({
    value: m.latest_published_mode_version_id!,
    label: pickLocalized(m.i18n, locale.value, 'display_name', m.display_name) ?? m.display_name,
  })),
])

const harnessSelectOptions = computed(() =>
  cliHarnesses.value.map((h) => ({ value: h.id, label: h.label })),
)

const statusSelectOptions = computed(() =>
  BOARD_TASK_STATUSES.map((st) => ({ value: st, label: t(`board.status.${st}`) })),
)

const prioritySelectOptions = computed(() =>
  BOARD_TASK_PRIORITIES.map((p) => ({ value: p, label: t(`board.priority.${p}`) })),
)

const projectSelectOptions = computed(() =>
  (props.projects ?? []).map((p) => ({ value: p.id, label: p.name })),
)

// Exclude self and tasks that transitively depend on self to prevent obvious DAG cycles in UI.
const dependencyCandidates = computed(() => {
  const pid = props.mode === 'edit' && props.task ? props.task.project_id : targetProjectId.value
  const sameProject = props.projectTasks.filter((item) => item.project_id === pid)
  if (!props.task) return sameProject

  const currentId = props.task.task_id
  const dependents = new Set<string>([currentId])
  let changed = true
  while (changed) {
    changed = false
    for (const candidate of sameProject) {
      if (dependents.has(candidate.task_id)) continue
      if (
        candidate.depends_on.some((dep) => dependents.has(dep)) ||
        (candidate.parent_task_id && dependents.has(candidate.parent_task_id))
      ) {
        dependents.add(candidate.task_id)
        changed = true
      }
    }
  }
  return sameProject.filter((item) => !dependents.has(item.task_id))
})

function buildAssigneeDto(): ProjectBoardAssigneeDto {
  if (assigneeKind.value === 'native') {
    const selectedMode = publishedModes.value.find(
      (m) => m.latest_published_mode_version_id === assigneeModeVersionId.value,
    )
    return {
      kind: 'native',
      mode_version_id: assigneeModeVersionId.value || null,
      display_name:
        assigneeDisplayName.value.trim() ||
        (selectedMode
          ? pickLocalized(selectedMode.i18n, locale.value, 'display_name', selectedMode.display_name) ??
            selectedMode.display_name
          : null),
    }
  }
  if (assigneeKind.value === 'cli') {
    const selectedHarness = cliHarnesses.value.find((h) => h.id === assigneeHarnessId.value)
    return {
      kind: 'cli',
      harness_id: assigneeHarnessId.value || null,
      id: assigneeHarnessId.value || null,
      display_name: assigneeDisplayName.value.trim() || selectedHarness?.label || assigneeHarnessId.value || null,
    }
  }
  return {
    kind: assigneeKind.value,
    display_name: assigneeDisplayName.value.trim() || null,
  }
}

function addChecklistItem() {
  const text = newChecklistText.value.trim()
  if (!text) return
  const id =
    typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
      ? crypto.randomUUID()
      : `chk-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`
  checklist.value = [...checklist.value, { id, text, done: false }]
  newChecklistText.value = ''
}

function toggleChecklistItem(itemId: string) {
  checklist.value = checklist.value.map((item) =>
    item.id === itemId ? { ...item, done: !item.done } : item,
  )
  if (props.mode === 'edit' && props.task) {
    emit('save-task', props.task.project_id, props.task.task_id, {
      checklist: checklist.value,
      expected_revision: props.task.revision,
    })
  }
}

function removeChecklistItem(itemId: string) {
  checklist.value = checklist.value.filter((item) => item.id !== itemId)
}

function addLabel() {
  const clean = newLabelText.value.trim().replace(/^#+/, '')
  if (!clean || labels.value.includes(clean)) {
    newLabelText.value = ''
    return
  }
  labels.value = [...labels.value, clean]
  newLabelText.value = ''
}

function removeLabel(lbl: string) {
  labels.value = labels.value.filter((item) => item !== lbl)
}

function toggleDependency(depId: string) {
  if (dependsOn.value.includes(depId)) {
    dependsOn.value = dependsOn.value.filter((id) => id !== depId)
  } else {
    dependsOn.value = [...dependsOn.value, depId]
  }
}

function handlePrimarySave() {
  dismissByKey(DRAWER_ERROR_KEY)
  const trimmedTitle = title.value.trim()
  if (!trimmedTitle) return

  if (props.mode === 'create' || !props.task) {
    const pid = targetProjectId.value
    if (!pid) return
    emit('create-task', pid, {
      title: trimmedTitle,
      description_md: descriptionMd.value,
      checklist: checklist.value,
      status: status.value,
      priority: priority.value,
      blocked: blocked.value,
      blocked_reason: blocked.value ? blockedReason.value.trim() || null : null,
      assignee: buildAssigneeDto(),
      depends_on: dependsOn.value,
      labels: labels.value,
    })
    return
  }

  emit('save-task', props.task.project_id, props.task.task_id, {
    title: trimmedTitle,
    description_md: descriptionMd.value,
    checklist: checklist.value,
    status: status.value,
    priority: priority.value,
    blocked: blocked.value,
    blocked_reason: blocked.value ? blockedReason.value.trim() || null : null,
    assignee: buildAssigneeDto(),
    depends_on: dependsOn.value,
    labels: labels.value,
    expected_revision: props.task.revision,
  })
}

function handleDispatch() {
  if (!props.task) return
  dismissByKey(DRAWER_ERROR_KEY)
  emit('dispatch-task', props.task.project_id, props.task.task_id, {
    assignee_override: buildAssigneeDto(),
    use_worktree: assigneeKind.value !== 'human' ? useWorktree.value : false,
    extra_prompt: extraDispatchPrompt.value.trim() || null,
  })
}

async function handleSendSteer() {
  const sid = props.task?.active_session_id
  const text = steerDraft.value.trim()
  if (!sid || !text || sendingSteer.value) return
  sendingSteer.value = true
  dismissByKey(DRAWER_ERROR_KEY)
  steerSentNotice.value = false
  try {
    const clientMsgId =
      typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
        ? crypto.randomUUID()
        : `steer-${Date.now()}`
    await api.createInteraction(sid, {
      content: text,
      client_message_id: clientMsgId,
      dispatch_mode: 'insert',
      target_run_id: props.task?.active_run_id ?? null,
    })
    steerDraft.value = ''
    steerSentNotice.value = true
    void loadSessionMessages()
  } catch (err) {
    notify.error(err, {
      title: t('app.operationFailed'),
      key: DRAWER_ERROR_KEY,
    })
  } finally {
    sendingSteer.value = false
  }
}

function handleApprove() {
  if (!props.task) return
  emit('approve-task', props.task.project_id, props.task.task_id, {
    merge_or_clean_worktree: mergeOrCleanWorktree.value,
    note: reviewFeedback.value.trim() || null,
  })
}

function handleRequestChanges() {
  if (!props.task || !reviewFeedback.value.trim()) return
  emit('request-changes', props.task.project_id, props.task.task_id, reviewFeedback.value.trim())
  reviewFeedback.value = ''
}

function formatConflictValue(val: unknown): string {
  if (val === null || val === undefined || val === '') return '—'
  if (typeof val === 'string') return val
  if (typeof val === 'boolean' || typeof val === 'number') return String(val)
  try {
    return JSON.stringify(val)
  } catch {
    return String(val)
  }
}

const conflictFields = computed(() => {
  const conflict = props.task?.sync_conflict
  if (!conflict) return []
  const keys = conflict.conflicting_fields?.length
    ? conflict.conflicting_fields
    : Object.keys({ ...conflict.file_snapshot, ...conflict.board_snapshot })
  return keys.map((field) => ({
    field,
    fileVal: formatConflictValue(conflict.file_snapshot?.[field]),
    boardVal: formatConflictValue(conflict.board_snapshot?.[field]),
  }))
})

const diffLines = computed(() => {
  const raw = taskDiff.value?.diff_text ?? ''
  if (!raw.trim()) return []
  return raw.split('\n').map((line, index) => {
    let kind: 'add' | 'del' | 'hunk' | 'ctx' = 'ctx'
    if (line.startsWith('+++') || line.startsWith('---') || line.startsWith('@@')) kind = 'hunk'
    else if (line.startsWith('+')) kind = 'add'
    else if (line.startsWith('-')) kind = 'del'
    return { index, line, kind }
  })
})
</script>

<template>
  <UiSheet
    :open="open"
    side="right"
    class="w-[min(94vw,600px)] sm:max-w-[600px] overflow-y-auto p-0"
    @update:open="emit('update:open', $event)"
  >
    <div class="board-drawer" data-testid="board-task-drawer">
      <!-- Drawer Header -->
      <header class="board-drawer-header">
        <div class="board-drawer-id-row">
          <span v-if="mode === 'edit' && task" class="board-drawer-key">{{ task.task_key }}</span>
          <span v-else class="board-drawer-key">{{ t('board.newTask') }}</span>
          <span v-if="task?.project_name" class="board-drawer-project">{{ task.project_name }}</span>

          <button
            v-if="task?.spatial_ref"
            type="button"
            class="board-drawer-spatial-btn ui-pressable"
            data-testid="drawer-jump-spatial-btn"
            @click="emit('jump-spatial', task)"
          >
            <Waypoints :size="12" />
            <span>{{ t('board.jumpToSpatial') }}</span>
          </button>
        </div>
      </header>

      <!-- 1. Sync Conflict Resolver Banner & Side-by-Side Diff -->
      <section
        v-if="mode === 'edit' && task?.has_sync_conflict && task.sync_conflict"
        class="board-conflict-box"
        data-testid="drawer-conflict-resolver"
      >
        <div class="board-conflict-header">
          <div class="board-conflict-title">
            <AlertTriangle :size="14" />
            <span>{{ t('board.conflict.title') }}</span>
          </div>
          <code class="board-conflict-path">{{ task.sync_conflict.file_rel_path }}</code>
        </div>
        <p class="board-conflict-desc">{{ t('board.conflict.description') }}</p>

        <div class="board-conflict-grid">
          <div class="board-conflict-col-head">{{ t('board.conflict.field') }}</div>
          <div class="board-conflict-col-head">{{ t('board.conflict.fileVersion') }}</div>
          <div class="board-conflict-col-head">{{ t('board.conflict.boardVersion') }}</div>

          <template v-for="row in conflictFields" :key="row.field">
            <div class="board-conflict-cell board-conflict-cell--field">{{ row.field }}</div>
            <div class="board-conflict-cell">{{ row.fileVal }}</div>
            <div class="board-conflict-cell">{{ row.boardVal }}</div>
          </template>
        </div>

        <div class="board-conflict-actions">
          <UiButton
            type="button"
            variant="outline"
            size="sm"
            data-testid="drawer-conflict-keep-file"
            @click="emit('resolve-conflict', task.project_id, task.task_id, 'keep_file')"
          >
            {{ t('board.conflict.keepFile') }}
          </UiButton>
          <UiButton
            type="button"
            size="sm"
            data-testid="drawer-conflict-keep-board"
            @click="emit('resolve-conflict', task.project_id, task.task_id, 'keep_board')"
          >
            {{ t('board.conflict.keepBoard') }}
          </UiButton>
        </div>
      </section>

      <!-- 2. Core Task Metadata & Description -->
      <section class="board-drawer-section">
        <div v-if="mode === 'create' && projects && projects.length > 1" class="board-field-block">
          <label class="board-field-label">{{ t('board.drawer.projectLabel') }}</label>
          <UiSelectField v-model="targetProjectId" :options="projectSelectOptions" />
        </div>

        <div class="board-field-block">
          <label class="board-field-label">{{ t('board.drawer.titleLabel') }}</label>
          <UiInput
            v-model="title"
            :placeholder="t('board.drawer.titlePlaceholder')"
            data-testid="drawer-title-input"
          />
        </div>

        <div class="board-field-row-2">
          <div class="board-field-block">
            <label class="board-field-label">{{ t('board.statusLabel') }}</label>
            <UiSelectField v-model="status" :options="statusSelectOptions" />
          </div>
          <div class="board-field-block">
            <label class="board-field-label">{{ t('board.priorityLabel') }}</label>
            <UiSelectField v-model="priority" :options="prioritySelectOptions" />
          </div>
        </div>

        <div class="board-field-block">
          <label class="board-field-label">{{ t('board.drawer.descriptionLabel') }}</label>
          <UiTextarea
            v-model="descriptionMd"
            :rows="4"
            :placeholder="t('board.drawer.descriptionPlaceholder')"
            data-testid="drawer-description-input"
          />
        </div>

        <!-- Blocked toggle -->
        <div class="board-inline-switch">
          <div class="board-inline-switch-label">
            <span>{{ t('board.blocked') }}</span>
            <span class="board-muted-hint">{{ t('board.drawer.blockedHint') }}</span>
          </div>
          <UiSwitch v-model="blocked" />
        </div>
        <UiInput
          v-if="blocked"
          v-model="blockedReason"
          :placeholder="t('board.drawer.blockedReasonPlaceholder')"
        />
      </section>

      <!-- 3. Acceptance Checklist (`- [ ]`) -->
      <section class="board-drawer-section">
        <div class="board-section-heading">
          <span>{{ t('board.drawer.checklistTitle') }}</span>
          <span class="board-muted-hint">
            {{ checklist.filter((c) => c.done).length }}/{{ checklist.length }}
          </span>
        </div>

        <div v-if="checklist.length > 0" class="board-checklist-items">
          <div v-for="item in checklist" :key="item.id" class="board-checklist-row">
            <UiCheckbox
              :model-value="item.done"
              :aria-label="item.text"
              @update:model-value="toggleChecklistItem(item.id)"
            />
            <span class="board-checklist-text" :class="{ done: item.done }">{{ item.text }}</span>
            <button
              type="button"
              class="board-icon-remove"
              :aria-label="t('board.actions.delete')"
              @click="removeChecklistItem(item.id)"
            >
              <X :size="12" />
            </button>
          </div>
        </div>

        <form class="board-inline-add" @submit.prevent="addChecklistItem">
          <UiInput
            v-model="newChecklistText"
            :placeholder="t('board.drawer.addChecklistPlaceholder')"
            data-testid="drawer-checklist-input"
          />
          <UiButton type="submit" variant="outline" size="sm" :disabled="!newChecklistText.trim()">
            <Plus :size="13" />
          </UiButton>
        </form>
      </section>

      <!-- 4. Labels & DAG Dependencies (`depends_on`) -->
      <section class="board-drawer-section">
        <div class="board-field-block">
          <label class="board-field-label">{{ t('board.drawer.labelsTitle') }}</label>
          <div v-if="labels.length" class="board-tag-list">
            <span v-for="lbl in labels" :key="lbl" class="board-tag-chip">
              #{{ lbl }}
              <button type="button" :aria-label="t('board.actions.delete')" @click="removeLabel(lbl)">
                <X :size="10" />
              </button>
            </span>
          </div>
          <form class="board-inline-add" @submit.prevent="addLabel">
            <UiInput v-model="newLabelText" :placeholder="t('board.drawer.addLabelPlaceholder')" />
            <UiButton type="submit" variant="outline" size="sm" :disabled="!newLabelText.trim()">
              <Plus :size="13" />
            </UiButton>
          </form>
        </div>

        <div class="board-field-block">
          <label class="board-field-label">{{ t('board.drawer.dependsOnTitle') }}</label>
          <div v-if="dependencyCandidates.length === 0" class="board-muted-hint">
            {{ t('board.drawer.noDependencyCandidates') }}
          </div>
          <div v-else class="board-dep-list" data-testid="drawer-depends-on-list">
            <button
              v-for="cand in dependencyCandidates"
              :key="cand.task_id"
              type="button"
              class="board-dep-chip ui-pressable"
              :class="{ active: dependsOn.includes(cand.task_id) }"
              :data-testid="`drawer-dep-option-${cand.task_id}`"
              @click="toggleDependency(cand.task_id)"
            >
              <span class="board-dep-key">{{ cand.task_key }}</span>
              <span class="board-dep-title">{{ cand.title }}</span>
              <span class="board-dep-status">{{ t(`board.status.${cand.status}`) }}</span>
            </button>
          </div>
        </div>
      </section>

      <!-- 5. Assignee & Explicit Dispatch Bar -->
      <section class="board-drawer-section" data-testid="drawer-assignee-section">
        <div class="board-section-heading">
          <span>{{ t('board.drawer.assigneeAndDispatch') }}</span>
        </div>

        <div class="board-assignee-tabs" role="radiogroup" :aria-label="t('board.drawer.assigneeKind')">
          <button
            v-for="kind in BOARD_ASSIGNEE_KINDS"
            :key="kind"
            type="button"
            class="board-assignee-tab ui-pressable"
            :class="{ active: assigneeKind === kind }"
            :data-testid="`drawer-assignee-kind-${kind}`"
            @click="assigneeKind = kind"
          >
            {{ t(`board.assignee.${kind}`) }}
          </button>
        </div>

        <div v-if="assigneeKind === 'native'" class="board-field-block">
          <label class="board-field-label">{{ t('board.drawer.nativeModeLabel') }}</label>
          <UiSelectField v-model="assigneeModeVersionId" :options="modeSelectOptions" />
        </div>

        <div v-else-if="assigneeKind === 'cli'" class="board-field-block">
          <label class="board-field-label">{{ t('board.drawer.cliHarnessLabel') }}</label>
          <UiSelectField v-model="assigneeHarnessId" :options="harnessSelectOptions" />
        </div>

        <div class="board-field-block">
          <label class="board-field-label">{{ t('board.drawer.assigneeDisplayName') }}</label>
          <UiInput
            v-model="assigneeDisplayName"
            :placeholder="t('board.drawer.assigneeNamePlaceholder')"
          />
        </div>

        <template v-if="mode === 'edit' && task">
          <div v-if="assigneeKind !== 'human'" class="board-inline-switch">
            <div class="board-inline-switch-label">
              <span>{{ t('board.drawer.worktreeIsolation') }}</span>
              <span class="board-muted-hint">{{ t('board.drawer.worktreeIsolationHint') }}</span>
            </div>
            <UiSwitch v-model="useWorktree" data-testid="drawer-worktree-switch" />
          </div>

          <UiInput
            v-if="assigneeKind !== 'human'"
            v-model="extraDispatchPrompt"
            :placeholder="t('board.drawer.extraPromptPlaceholder')"
          />

          <div class="board-dispatch-actions">
            <button
              type="button"
              class="board-primary-cta ui-pressable"
              data-testid="drawer-dispatch-btn"
              @click="handleDispatch"
            >
              <Play :size="13" />
              <span>{{ t('board.drawer.dispatchRun') }}</span>
            </button>

            <UiButton
              v-if="isRunActive || task.status === 'in_progress'"
              type="button"
              variant="outline"
              size="sm"
              data-testid="drawer-cancel-run-btn"
              @click="emit('cancel-task', task.project_id, task.task_id)"
            >
              <Square :size="12" />
              <span>{{ t('board.drawer.cancelRun') }}</span>
            </UiButton>
          </div>
        </template>
      </section>

      <!-- 6. Embedded Live Execution & Steer -->
      <section
        v-if="mode === 'edit' && task && (task.active_session_id || task.active_run_id)"
        class="board-drawer-section board-live-box"
        data-testid="drawer-live-execution"
      >
        <div class="board-section-heading">
          <div class="board-live-title">
            <span class="board-live-dot" :class="{ active: isRunActive }" />
            <span>{{ t('board.drawer.liveExecutionTitle') }}</span>
            <span v-if="task.run_status" class="board-run-status-pill">{{ task.run_status }}</span>
          </div>

          <button
            v-if="task.active_session_id"
            type="button"
            class="board-link-inline ui-pressable"
            data-testid="drawer-open-session-btn"
            @click="emit('open-session', task.active_session_id)"
          >
            <span>{{ t('board.drawer.openSession') }}</span>
            <ExternalLink :size="11" />
          </button>
        </div>

        <div v-if="task.worktree_path" class="board-worktree-line">
          <GitBranch :size="12" />
          <code>{{ task.worktree_path }}</code>
        </div>

        <div v-if="effectiveDigestHeadline" class="board-live-digest-card">
          <span :class="{ 'ui-shimmer-text': isRunActive }">{{ effectiveDigestHeadline }}</span>
        </div>

        <!-- Recent messages excerpt -->
        <div v-if="sessionMessages.length > 0" class="board-session-messages">
          <div v-for="msg in sessionMessages" :key="msg.id" class="board-msg-snippet">
            <span class="board-msg-role">{{ msg.role }}</span>
            <span class="board-msg-body">{{ msg.content }}</span>
          </div>
        </div>
        <button
          v-if="task.active_session_id"
          type="button"
          class="board-refresh-mini"
          :disabled="loadingMessages"
          @click="loadSessionMessages"
        >
          <RefreshCw :size="11" :class="{ 'animate-spin': loadingMessages }" />
          <span>{{ t('board.drawer.refreshMessages') }}</span>
        </button>

        <!-- Direct Steer Input -->
        <form
          v-if="task.active_session_id"
          class="board-steer-form"
          @submit.prevent="handleSendSteer"
        >
          <UiInput
            v-model="steerDraft"
            :placeholder="t('board.drawer.steerPlaceholder')"
            :disabled="sendingSteer"
            data-testid="drawer-steer-input"
          />
          <UiButton
            type="submit"
            size="sm"
            :disabled="!steerDraft.trim() || sendingSteer"
            data-testid="drawer-steer-send"
          >
            <LoaderCircle v-if="sendingSteer" :size="13" class="animate-spin" />
            <Send v-else :size="13" />
            <span>{{ t('board.drawer.sendSteer') }}</span>
          </UiButton>
        </form>
        <span v-if="steerSentNotice" class="board-muted-hint" data-testid="drawer-steer-sent">
          {{ t('board.drawer.steerSent') }}
        </span>
      </section>

      <!-- 7. In-Review Diff Viewer & 1-Click Acceptance / Request Changes -->
      <section
        v-if="
          mode === 'edit' &&
          task &&
          (task.status === 'in_review' || task.worktree_path || task.diff_stats || taskDiff)
        "
        class="board-drawer-section"
        data-testid="drawer-review-section"
      >
        <div class="board-section-heading">
          <div class="board-live-title">
            <FileDiff :size="14" />
            <span>{{ t('board.drawer.reviewAndDiffTitle') }}</span>
          </div>
          <button
            type="button"
            class="board-refresh-mini"
            :disabled="loadingDiff"
            data-testid="drawer-load-diff-btn"
            @click="loadTaskDiff"
          >
            <RefreshCw :size="11" :class="{ 'animate-spin': loadingDiff }" />
            <span>{{ t('board.drawer.reloadDiff') }}</span>
          </button>
        </div>

        <div v-if="taskDiff?.files?.length" class="board-diff-files">
          <div v-for="file in taskDiff.files" :key="file.path" class="board-diff-file-row">
            <code class="board-diff-file-path">{{ file.path }}</code>
            <span class="board-diff-file-stats">
              <span class="board-diff-add">+{{ file.added }}</span>
              <span class="board-diff-del">-{{ file.removed }}</span>
            </span>
          </div>
        </div>

        <div v-if="diffLines.length > 0" class="board-diff-viewer" data-testid="drawer-diff-viewer">
          <div
            v-for="row in diffLines"
            :key="row.index"
            class="board-diff-line"
            :class="`board-diff-line--${row.kind}`"
          >
            {{ row.line }}
          </div>
        </div>
        <p v-else-if="!loadingDiff" class="board-muted-hint">
          {{ t('board.drawer.noDiffChanges') }}
        </p>

        <div class="board-review-controls">
          <label class="board-clean-worktree-check">
            <UiCheckbox
              :model-value="mergeOrCleanWorktree"
              data-testid="drawer-clean-worktree-checkbox"
              @update:model-value="mergeOrCleanWorktree = Boolean($event)"
            />
            <span>{{ t('board.drawer.cleanMergeWorktree') }}</span>
          </label>

          <UiTextarea
            v-model="reviewFeedback"
            :rows="2"
            :placeholder="t('board.drawer.reviewFeedbackPlaceholder')"
            data-testid="drawer-review-feedback-input"
          />

          <div class="board-review-buttons">
            <button
              type="button"
              class="board-primary-cta ui-pressable"
              data-testid="drawer-approve-btn"
              @click="handleApprove"
            >
              <Check :size="14" />
              <span>{{ t('board.drawer.approveAndComplete') }}</span>
            </button>

            <UiButton
              type="button"
              variant="outline"
              size="sm"
              :disabled="!reviewFeedback.trim()"
              data-testid="drawer-request-changes-btn"
              @click="handleRequestChanges"
            >
              <RotateCcw :size="13" />
              <span>{{ t('board.drawer.requestChanges') }}</span>
            </UiButton>
          </div>
        </div>
      </section>

      <!-- Sticky Footer Save / Delete -->
      <footer class="board-drawer-footer">
        <UiButton
          v-if="mode === 'edit' && task"
          type="button"
          variant="ghost"
          size="sm"
          data-testid="drawer-delete-task-btn"
          @click="emit('delete-task', task.project_id, task.task_id)"
        >
          <Trash2 :size="13" />
          <span>{{ t('board.actions.delete') }}</span>
        </UiButton>
        <div class="board-footer-right">
          <UiButton type="button" variant="ghost" size="sm" @click="emit('update:open', false)">
            {{ t('common.cancel') }}
          </UiButton>
          <button
            type="button"
            class="board-primary-cta ui-pressable"
            :disabled="!title.trim()"
            data-testid="drawer-save-btn"
            @click="handlePrimarySave"
          >
            {{ mode === 'create' ? t('board.drawer.createTaskSubmit') : t('board.actions.save') }}
          </button>
        </div>
      </footer>
    </div>
  </UiSheet>
</template>

<style scoped>
.board-drawer {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 18px 20px 24px;
  min-height: 100%;
  box-sizing: border-box;
}

.board-drawer-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding-right: 28px;
}

.board-drawer-id-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.board-drawer-key {
  font-family: var(--font-mono, monospace);
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.board-drawer-project {
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 11px;
  background: var(--surface-section, var(--bg-tertiary));
  color: var(--text-secondary);
}

.board-drawer-spatial-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.board-drawer-spatial-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.board-conflict-box {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border-radius: 10px;
  border: 1px solid var(--border-default);
  background: var(--surface-section, var(--bg-tertiary));
}

.board-conflict-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.board-conflict-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.board-conflict-path {
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  color: var(--text-muted);
}

.board-conflict-desc {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-secondary);
}

.board-conflict-grid {
  display: grid;
  grid-template-columns: 110px minmax(0, 1fr) minmax(0, 1fr);
  gap: 4px;
  font-size: 11px;
}

.board-conflict-col-head {
  font-weight: 600;
  color: var(--text-muted);
  padding: 3px 6px;
}

.board-conflict-cell {
  padding: 5px 6px;
  border-radius: 6px;
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.board-conflict-cell--field {
  font-family: var(--font-mono, monospace);
  color: var(--text-secondary);
}

.board-conflict-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.board-drawer-section {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border-radius: 10px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: color-mix(in srgb, var(--surface-section, var(--bg-secondary)) 55%, transparent);
}

.board-section-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
}

.board-field-block {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.board-field-label {
  font-size: 11.5px;
  font-weight: 500;
  color: var(--text-secondary);
}

.board-field-row-2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}

.board-inline-switch {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.board-inline-switch-label {
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-size: 12px;
  color: var(--text-primary);
}

.board-muted-hint {
  font-size: 11px;
  color: var(--text-muted);
}

.board-checklist-items {
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.board-checklist-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 6px;
  border-radius: 6px;
  background: var(--surface-raised, var(--bg-secondary));
}

.board-checklist-text {
  flex: 1;
  font-size: 12px;
  color: var(--text-primary);
}

.board-checklist-text.done {
  text-decoration: line-through;
  color: var(--text-muted);
}

.board-icon-remove {
  border: 0;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
  padding: 2px;
}

.board-icon-remove:hover {
  color: var(--text-error);
}

.board-inline-add {
  display: flex;
  align-items: center;
  gap: 6px;
}

.board-tag-list {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
}

.board-tag-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 7px;
  border-radius: 999px;
  font-size: 11px;
  background: var(--surface-raised, var(--bg-tertiary));
  color: var(--text-secondary);
}

.board-tag-chip button {
  border: 0;
  background: transparent;
  color: inherit;
  cursor: pointer;
  padding: 0;
  display: inline-flex;
}

.board-dep-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 150px;
  overflow-y: auto;
}

.board-dep-chip {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 8px;
  border-radius: 7px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  cursor: pointer;
  text-align: left;
}

.board-dep-chip.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
}

.board-dep-key {
  font-family: var(--font-mono, monospace);
  font-weight: 600;
  flex-shrink: 0;
}

.board-dep-title {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.board-dep-status {
  font-size: 10.5px;
  opacity: 0.8;
}

.board-assignee-tabs {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 4px;
  padding: 3px;
  border-radius: 8px;
  background: var(--surface-raised, var(--bg-tertiary));
}

.board-assignee-tab {
  height: 26px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  cursor: pointer;
}

.board-assignee-tab.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-weight: 500;
}

.board-dispatch-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.board-primary-cta {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  height: 30px;
  padding: 0 12px;
  border-radius: var(--radius-control, 6px);
  border: 0;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.board-primary-cta:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.board-live-box {
  background: var(--surface-raised, var(--bg-secondary));
}

.board-live-title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.board-live-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--text-muted);
}

.board-live-dot.active {
  background: var(--text-primary);
}

.board-run-status-pill {
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 10.5px;
  font-weight: 500;
  background: var(--surface-section, var(--bg-tertiary));
  color: var(--text-secondary);
}

.board-link-inline {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.board-link-inline:hover {
  color: var(--text-primary);
}

.board-worktree-line {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--text-muted);
  overflow-wrap: anywhere;
}

.board-live-digest-card {
  padding: 7px 9px;
  border-radius: 7px;
  background: var(--surface-section, var(--bg-tertiary));
  font-size: 12px;
  color: var(--text-primary);
}

.board-session-messages {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 140px;
  overflow-y: auto;
}

.board-msg-snippet {
  display: flex;
  gap: 6px;
  padding: 5px 7px;
  border-radius: 6px;
  background: var(--surface-section, var(--bg-tertiary));
  font-size: 11px;
}

.board-msg-role {
  font-family: var(--font-mono, monospace);
  font-weight: 600;
  color: var(--text-muted);
  flex-shrink: 0;
}

.board-msg-body {
  color: var(--text-secondary);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.board-refresh-mini {
  align-self: flex-start;
  display: inline-flex;
  align-items: center;
  gap: 4px;
  border: 0;
  background: transparent;
  color: var(--text-muted);
  font-size: 11px;
  cursor: pointer;
  padding: 2px 0;
}

.board-refresh-mini:hover {
  color: var(--text-primary);
}

.board-steer-form {
  display: flex;
  align-items: center;
  gap: 6px;
}

.board-diff-files {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.board-diff-file-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 7px;
  border-radius: 6px;
  background: var(--surface-raised, var(--bg-secondary));
  font-size: 11px;
}

.board-diff-file-path {
  font-family: var(--font-mono, monospace);
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.board-diff-file-stats {
  display: inline-flex;
  gap: 6px;
  font-family: var(--font-mono, monospace);
  flex-shrink: 0;
}

.board-diff-add {
  color: var(--text-approve, var(--accent-success));
}

.board-diff-del {
  color: var(--text-reject, var(--accent-danger));
}

.board-diff-viewer {
  max-height: 220px;
  overflow: auto;
  padding: 8px;
  border-radius: 8px;
  background: var(--bg-diff, var(--bg-primary));
  border: 1px solid var(--border-muted);
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  line-height: 1.45;
}

.board-diff-line {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  padding: 1px 4px;
  border-radius: 3px;
}

.board-diff-line--hunk {
  color: var(--text-muted);
}

.board-diff-line--add {
  background: color-mix(in srgb, var(--accent-success) 14%, transparent);
  color: var(--text-primary);
}

.board-diff-line--del {
  background: color-mix(in srgb, var(--accent-danger) 14%, transparent);
  color: var(--text-primary);
}

.board-review-controls {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.board-clean-worktree-check {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  font-size: 12px;
  color: var(--text-primary);
  cursor: pointer;
}

.board-review-buttons {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.board-drawer-footer {
  margin-top: auto;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding-top: 8px;
}

.board-footer-right {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 8px;
}
</style>
