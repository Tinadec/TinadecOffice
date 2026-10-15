import { computed, ref, type Ref } from 'vue'
import i18n from '@/i18n'
import { api as baseApi, type ApprovalDto, type ProjectDto } from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import { projectStorageId, scopedApi, selectionKey } from '@/lib/storageScope'

// ---------------------------------------------------------------------------
// CodeController — the single domain controller for the Code page.
// Owns all Code data/state so the file-tree / search / editor / patch cards
// share one source of truth (projects, tabs, approvals) instead of duplicating.
// ---------------------------------------------------------------------------

export interface OpenTab {
  path: string
  mode: 'view' | 'edit'
  content?: string
}

const t = i18n.global.t
const projects = ref<ProjectDto[]>([])
const selectedProjectId = ref<string | null>(null)
const api = scopedApi(baseApi, () => projectStorageId(selectedProjectId.value))
const openTabs = ref<OpenTab[]>([])
const activeTabPath = ref<string | null>(null)
const approvals = ref<ApprovalDto[]>([])
const selectedSessionId = ref<string | null>(null)
const showSearchPanel = ref(true)
const showPatchPanel = ref(false)
const patchOriginal = ref('')
const patchModified = ref('')
const patchFilePath = ref('')
const busy = ref(false)

const currentProject = computed(() =>
  projects.value.find((p) => selectionKey(p) === selectedProjectId.value) ?? null,
)
const currentProjectPath = computed(() => currentProject.value?.path ?? '')
const activeTab = computed(() =>
  openTabs.value.find((t) => t.path === activeTabPath.value) ?? null,
)

const { notify, banner, dismissByKey } = useNotifications()

async function loadProjects(): Promise<void> {
  busy.value = true
  try {
    projects.value = await api.listProjects()
    if (!selectedProjectId.value && projects.value.length > 0) {
      selectedProjectId.value = selectionKey(projects.value[0])
    }
    await loadSession()
    dismissByKey('code-load')
  } catch (err) {
    banner.error({
      key: 'code-load',
      title: t('code.loadProjectsFailed'),
      message: t('code.projectsUnavailable'),
      details: err instanceof Error ? err.message : t('code.loadProjectsFailed'),
      action: { label: t('common.retry'), run: () => loadProjects() },
    })
  } finally {
    busy.value = false
  }
}

async function loadSession(): Promise<void> {
  const projectKey = selectedProjectId.value
  const capturedApi = scopedApi(baseApi, () => projectStorageId(projectKey))
  if (!selectedSessionId.value && currentProject.value) {
    try {
      const sessions = await capturedApi.listSessions(selectionKey(currentProject.value))
      if (selectedProjectId.value !== projectKey) return
      selectedSessionId.value = sessions[0]?.id ?? null
    } catch {
      if (selectedProjectId.value !== projectKey) return
      selectedSessionId.value = null
    }
  }
  await loadApprovals()
}

async function loadApprovals(): Promise<void> {
  const projectKey = selectedProjectId.value; const sessionId = selectedSessionId.value
  const capturedApi = scopedApi(baseApi, () => projectStorageId(projectKey))
  try {
    const list = await capturedApi.listApprovals(sessionId ?? undefined)
    if (selectedProjectId.value !== projectKey || selectedSessionId.value !== sessionId) return
    approvals.value = list
  } catch {
    if (selectedProjectId.value !== projectKey || selectedSessionId.value !== sessionId) return
    approvals.value = []
  }
}

function handleFileSelect(path: string): void {
  const existing = openTabs.value.find((t) => t.path === path)
  if (existing) {
    activeTabPath.value = path
    return
  }
  openTabs.value = [...openTabs.value, { path, mode: 'view' }]
  activeTabPath.value = path
}

function handleEditFile(path: string, content: string): void {
  const idx = openTabs.value.findIndex((t) => t.path === path)
  if (idx !== -1) {
    openTabs.value[idx] = { ...openTabs.value[idx], mode: 'edit', content }
  }
}

function handleCloseTab(path: string): void {
  const idx = openTabs.value.findIndex((t) => t.path === path)
  if (idx === -1) return
  openTabs.value = openTabs.value.filter((t) => t.path !== path)
  if (activeTabPath.value === path) {
    activeTabPath.value = openTabs.value[idx]?.path ?? openTabs.value[idx - 1]?.path ?? null
  }
}

function handleSwitchTab(path: string): void {
  activeTabPath.value = path
}

function handleSwitchToView(path: string): void {
  const idx = openTabs.value.findIndex((t) => t.path === path)
  if (idx !== -1) {
    openTabs.value[idx] = { ...openTabs.value[idx], mode: 'view' }
  }
}

function handleApprovalCreated(approval: ApprovalDto): void {
  approvals.value = [approval, ...approvals.value]
}

async function decideApproval(approval: ApprovalDto, decision: 'approved' | 'rejected'): Promise<void> {
  try {
    await api.decideApproval(approval.id, decision)
    await loadApprovals()
  } catch (err) {
    notify.error(err, { title: t('approval.decideFailed') })
  }
}

function handleShowPatch(filePath: string, original: string, modified: string): void {
  patchFilePath.value = filePath
  patchOriginal.value = original
  patchModified.value = modified
  showPatchPanel.value = true
}

function handleNewFile(): void {
  notify.info({
    title: t('code.newFile'),
    message: t('code.newFileHint'),
  })
}

function handleRefresh(): void {
  void loadApprovals()
}

function setProject(id: string): void {
  if (id === selectedProjectId.value) return
  selectedProjectId.value = id
  selectedSessionId.value = null
  approvals.value = []
  openTabs.value = []
  activeTabPath.value = null
  void loadSession()
}

let started = false
function start() {
  if (started) return
  started = true
  void loadProjects()
}

export const codeController = {
  projects,
  selectedProjectId,
  openTabs,
  activeTabPath,
  approvals,
  selectedSessionId,
  showSearchPanel,
  showPatchPanel,
  patchOriginal,
  patchModified,
  patchFilePath,
  busy,
  currentProject,
  currentProjectPath,
  activeTab,
  start,
  handleFileSelect,
  handleEditFile,
  handleCloseTab,
  handleSwitchTab,
  handleSwitchToView,
  handleApprovalCreated,
  decideApproval,
  handleShowPatch,
  handleNewFile,
  handleRefresh,
  setProject,
  loadApprovals,
  loadProjects,
}
