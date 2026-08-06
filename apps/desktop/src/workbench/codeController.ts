import { computed, inject, provide, ref, watch, type InjectionKey } from 'vue'
import { api, type ApprovalDto, type ProjectDto } from '@/api'
import { useNotifications } from '@/composables/useNotifications'

export interface CodeOpenTab {
  path: string
  mode: 'view' | 'edit'
  content?: string
}

export function createCodeWorkbenchController() {
  const { notify, banner, dismissByKey } = useNotifications()
  const projects = ref<ProjectDto[]>([])
  const selectedProjectId = ref<string | null>(null)
  const openTabs = ref<CodeOpenTab[]>([])
  const activeTabPath = ref<string | null>(null)
  const approvals = ref<ApprovalDto[]>([])
  const selectedSessionId = ref<string | null>(null)
  const patchOriginal = ref('')
  const patchModified = ref('')
  const patchFilePath = ref('')
  const busy = ref(false)
  let started = false

  const currentProject = computed(() => projects.value.find((project) => project.id === selectedProjectId.value) ?? null)
  const currentProjectPath = computed(() => currentProject.value?.path ?? '')
  const activeTab = computed(() => openTabs.value.find((tab) => tab.path === activeTabPath.value) ?? null)

  async function loadProjects(): Promise<void> {
    busy.value = true
    try {
      projects.value = await api.listProjects()
      if (!projects.value.some((project) => project.id === selectedProjectId.value)) {
        selectedProjectId.value = projects.value[0]?.id ?? null
      }
      await loadSession()
      dismissByKey('code-load')
    } catch (error) {
      banner.error({
        key: 'code-load',
        title: 'Failed to load projects',
        message: 'The project list is currently unavailable.',
        details: error instanceof Error ? error.message : 'Failed to load projects',
        action: { label: 'Retry', run: loadProjects },
      })
    } finally {
      busy.value = false
    }
  }

  async function loadSession(): Promise<void> {
    selectedSessionId.value = null
    if (currentProject.value) {
      try {
        const sessions = await api.listSessions(currentProject.value.id)
        selectedSessionId.value = sessions[0]?.id ?? null
      } catch {
        selectedSessionId.value = null
      }
    }
    await loadApprovals()
  }

  async function loadApprovals(): Promise<void> {
    try {
      approvals.value = await api.listApprovals(selectedSessionId.value ?? undefined)
    } catch {
      approvals.value = []
    }
  }

  function selectFile(path: string): void {
    if (!openTabs.value.some((tab) => tab.path === path)) {
      openTabs.value = [...openTabs.value, { path, mode: 'view' }]
    }
    activeTabPath.value = path
  }

  function editFile(path: string, content: string): void {
    const index = openTabs.value.findIndex((tab) => tab.path === path)
    if (index >= 0) openTabs.value[index] = { ...openTabs.value[index], mode: 'edit', content }
  }

  function closeTab(path: string): void {
    const index = openTabs.value.findIndex((tab) => tab.path === path)
    if (index < 0) return
    openTabs.value = openTabs.value.filter((tab) => tab.path !== path)
    if (activeTabPath.value === path) {
      activeTabPath.value = openTabs.value[index]?.path ?? openTabs.value[index - 1]?.path ?? null
    }
  }

  function switchToView(path: string): void {
    const index = openTabs.value.findIndex((tab) => tab.path === path)
    if (index >= 0) openTabs.value[index] = { ...openTabs.value[index], mode: 'view' }
  }

  function recordApproval(approval: ApprovalDto): void {
    approvals.value = [approval, ...approvals.value.filter((item) => item.id !== approval.id)]
  }

  async function decideApproval(approval: ApprovalDto, decision: 'approved' | 'rejected'): Promise<void> {
    try {
      await api.decideApproval(approval.id, decision)
      await loadApprovals()
    } catch (error) {
      notify.error(error, { title: 'Failed to decide approval' })
    }
  }

  function showPatch(filePath: string, original: string, modified: string): void {
    patchFilePath.value = filePath
    patchOriginal.value = original
    patchModified.value = modified
  }

  function clearPatch(): void {
    patchFilePath.value = ''
    patchOriginal.value = ''
    patchModified.value = ''
  }

  function start(): void {
    if (started) return
    started = true
    void loadProjects()
  }

  watch(selectedProjectId, () => {
    if (!started) return
    openTabs.value = []
    activeTabPath.value = null
    void loadSession()
  })

  return {
    projects, selectedProjectId, openTabs, activeTabPath, approvals, selectedSessionId,
    patchOriginal, patchModified, patchFilePath, busy, currentProject, currentProjectPath, activeTab,
    start, loadProjects, loadApprovals, selectFile, editFile, closeTab, switchToView,
    recordApproval, decideApproval, showPatch, clearPatch,
  }
}

export type CodeWorkbenchController = ReturnType<typeof createCodeWorkbenchController>
const CODE_KEY: InjectionKey<CodeWorkbenchController> = Symbol('code-workbench')
export function provideCodeWorkbench(controller: CodeWorkbenchController): void { provide(CODE_KEY, controller) }
export function useCodeWorkbench(): CodeWorkbenchController {
  const controller = inject(CODE_KEY)
  if (!controller) throw new Error('Code workbench controller was not provided.')
  return controller
}

