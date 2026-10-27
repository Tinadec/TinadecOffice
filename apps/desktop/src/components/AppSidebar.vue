<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'
import { useWorkspaceList, freeWorkspaceKey, recentSessionRows, recentWorkspaceSessions, type SidebarSection } from '@/composables/useWorkspaceList'
import { ACTIVITY_LIMIT, useRecentActivity, type ActivityCandidate } from '@/composables/useRecentActivity'
import { presentActivity, type ActivityFacts } from '@/lib/recentActivity'
import { workspaceIcons, type WorkspaceLoadState } from '@/lib/workspaces'
import { selectionKey, selectionIdentity, selectedStorage } from '@/lib/storageScope'
import {
  Archive,
  ArrowRightLeft,
  Bug,
  ChevronRight,
  FolderOpen,
  GripVertical,
  MessageSquare,
  MoreHorizontal,
  PanelLeftClose,
  PanelLeftOpen,
  RefreshCw,
  Pencil,
  Plus,
  Settings,
  SlidersHorizontal,
  LayoutGrid,
  Waypoints,
  Sparkles,
  Store,
  Trash2,
  ArrowUp,
  ArrowDown,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { ProjectDto, SessionDto } from '../api'
import BrandLogo from '@/components/BrandLogo.vue'
import SidebarActivityCard from '@/components/SidebarActivityCard.vue'
import TinadecCalligraphy from '@/components/TinadecCalligraphy.vue'
import PersonalAvatar from '@/components/personal/PersonalAvatar.vue'
import InlineRenameInput from '@/components/InlineRenameInput.vue'
import RowContextMenu, { type RowMenuItem } from '@/components/RowContextMenu.vue'
import { UiButton, UiCheckbox, UiSelectField } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { useDebugStudio } from '@/composables/useDebugStudio'
import { errorCodeMessage } from '@/lib/apiError'
import { userProfile } from '@/lib/userProfile'
import { profileNameFits } from '@/lib/profileNameFit'
import { auth } from '@/lib/auth'

const { t, te, locale } = useI18n()
const { confirm } = useNotifications()
const { enabled: debugStudioEnabled, load: loadDebugStudio } = useDebugStudio()
onMounted(loadDebugStudio)

const props = defineProps<{
  projects: ProjectDto[]
  sessions: SessionDto[]
  selectedProjectId: string | null
  selectedSessionId: string | null
  busy: boolean
  collapsed?: boolean
  spaceActive?: boolean
  panelStyle?: Record<string, string>
  panelDataAttrs?: Record<string, string>
  workspaceLoadStates?: Record<string, WorkspaceLoadState>
}>()

const emit = defineEmits<{
  'select-project': [id: string]
  'select-session': [id: string]
  'create-session': [projectId: string | null]
  'open-project': []
  'go-settings': []
  'go-personal': []
  'go-market': []
  'change-view': [mode: 'flat' | 'space']
  'toggle-collapse': []
  'rename-project': [id: string, name: string]
  'rename-session': [id: string, title: string]
  'archive-project': [id: string]
  'archive-session': [id: string]
  'trash-project': [id: string]
  'trash-session': [id: string]
  'migrate-session': [id: string, targetProjectKey: string]
  'edit-workspace': [id: string]
  'retry-workspaces': [key: string]
  'unregister-workspace': [id: string]
}>()

const list = useWorkspaceList()
watch(() => [freeWorkspaceKey, ...props.projects.map(selectionKey)], keys => list.reconcile(keys), { immediate: true })
const draggedWorkspace = ref<string | null>(null)
const dragOverKey = ref<string | null>(null)
const dragOverAfter = ref(false)
// The free conversation is a block, not a row inside the project order: it moves with the option
// list above it, so it neither starts a drag nor accepts one.
function reorderableWorkspace(key: string) { return key !== freeWorkspaceKey }
function startDrag(event: DragEvent, key: string) {
  if (!reorderableWorkspace(key)) return
  draggedWorkspace.value = key
  if (event.dataTransfer) { event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/x-tinadec-workspace', key) }
}
function dragOverWorkspace(event: DragEvent, key: string) {
  if (!draggedWorkspace.value || !reorderableWorkspace(key)) return
  const row = (event.currentTarget as HTMLElement).getBoundingClientRect()
  dragOverKey.value = key
  dragOverAfter.value = event.clientY > row.top + row.height / 2
}
function dropWorkspace(event: DragEvent, key: string) {
  event.preventDefault()
  if (draggedWorkspace.value && reorderableWorkspace(key)) list.move(draggedWorkspace.value, key, dragOverAfter.value)
  draggedWorkspace.value = null
  dragOverKey.value = null
}
// Drag is the primary gesture, but reordering must not require a pointer.
function reorderWithKeyboard(event: KeyboardEvent, key: string) {
  if (!reorderableWorkspace(key) || !event.altKey || (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) return
  event.preventDefault()
  list.step(key, event.key === 'ArrowUp' ? -1 : 1)
}
// ---- Option blocks: the same gesture one level up ----
// 最近对话 / 最近活动 / 自由对话 / 项目分组 are siblings inside the workspace list, so the order the
// user drags them into here is the order the list renders.
const draggedSection = ref<SidebarSection | null>(null)
const sectionDragOver = ref<SidebarSection | null>(null)
const sectionDragAfter = ref(false)
function startSectionDrag(event: DragEvent, section: SidebarSection) {
  draggedSection.value = section
  if (event.dataTransfer) { event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/x-tinadec-section', section) }
}
function dragOverSection(event: DragEvent, section: SidebarSection) {
  if (!draggedSection.value) return
  const row = (event.currentTarget as HTMLElement).getBoundingClientRect()
  sectionDragOver.value = section
  sectionDragAfter.value = event.clientY > row.top + row.height / 2
}
function endSectionDrag() {
  draggedSection.value = null
  sectionDragOver.value = null
}
function dropSection(event: DragEvent, section: SidebarSection) {
  event.preventDefault()
  if (draggedSection.value) list.moveSection(draggedSection.value, section, sectionDragAfter.value)
  endSectionDrag()
}
function reorderSectionWithKeyboard(event: KeyboardEvent, section: SidebarSection) {
  if (!event.altKey || (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) return
  event.preventDefault()
  list.stepSection(section, event.key === 'ArrowUp' ? -1 : 1)
}
const viewMenu = ref<HTMLDivElement | null>(null)
const viewMenuId = `sidebar-view-${useId()}`
const viewMenuOpen = ref(false)
let viewMenuTrigger: HTMLButtonElement | null = null
const viewMenuStyle = ref({ left: '0px', top: '0px' })
function openViewMenu(event: MouseEvent) {
  viewMenuTrigger = event.currentTarget as HTMLButtonElement
  const rect = viewMenuTrigger.getBoundingClientRect()
  viewMenuStyle.value = { left: `${Math.max(8, Math.min(rect.left, window.innerWidth - 210))}px`, top: `${Math.max(8, rect.top - 116)}px` }
  // The native invoker performs the toggle after this positioning handler;
  // it also owns Escape/light-dismiss and restores focus when the menu closes.
}
function updateViewMenuState(event: Event) {
  viewMenuOpen.value = (event as ToggleEvent).newState === 'open'
}
function changeView(mode: 'flat' | 'space') {
  viewMenu.value?.hidePopover()
  viewMenuTrigger?.focus({ preventScroll: true })
  emit('change-view', mode)
}

// ---- Lifecycle management (context menu + inline rename) ----
interface MenuTarget {
  kind: 'project' | 'session' | 'free'
  id: string
  name: string
  x: number
  y: number
}

const menuTarget = ref<MenuTarget | null>(null)
const renaming = ref<{ kind: 'project' | 'session'; id: string } | null>(null)
const migrationDialog = ref<HTMLDialogElement | null>(null)
const migrationSession = ref<MenuTarget | null>(null)
const migrationProject = ref('')
const migrationProjects = computed(() => props.projects.filter(project => project.storage_id
  && project.availability !== 'error' && project.lifecycle_status === 'active'))
const migrationAllowed = computed(() => menuTarget.value?.kind === 'session' && props.sessions.some(session => selectionKey(session) === menuTarget.value?.id && !session.project_id) && migrationProjects.value.length > 0)

const menuProject = computed(() => menuTarget.value?.kind === 'project'
  ? props.projects.find(row => selectionKey(row) === menuTarget.value!.id || row.id === menuTarget.value!.id)
  : undefined)
// A registered workspace whose source folder disappeared can neither load nor be archived by
// content, so the menu offers retry plus an explicit unregister instead of dead actions.
const menuProjectUnavailable = computed(() => menuProject.value?.availability === 'error')
const menuItems = computed<RowMenuItem[]>(() => {
  const order: RowMenuItem[] = [
    { key: 'move-up', label: t('sidebar.moveUp'), icon: ArrowUp },
    { key: 'move-down', label: t('sidebar.moveDown'), icon: ArrowDown },
  ]
  // Conversations are listed chronologically inside a workspace and are not reorderable.
  if (menuTarget.value?.kind === 'session') return [
    { key: 'rename', label: t('sidebar.rename'), icon: Pencil },
    ...(migrationAllowed.value ? [{ key: 'migrate', label: t('sidebar.migrateSession'), icon: ArrowRightLeft }] : []),
    { key: 'archive', label: t('sidebar.archive'), icon: Archive },
    { key: 'trash', label: t('sidebar.moveToTrash'), icon: Trash2, danger: true },
  ]
  // The free conversation owns no project record, so it only gets reordering.
  if (menuTarget.value?.kind === 'free') return order
  if (menuProjectUnavailable.value) return [
    { key: 'retry-workspace', label: t('sidebar.retryLoad'), icon: RefreshCw },
    ...order,
    { key: 'unregister-workspace', label: t('sidebar.unregisterWorkspace'), icon: Trash2, danger: true },
  ]
  return [
    { key: 'edit-workspace', label: t('sidebar.editWorkspace'), icon: Pencil },
    ...order,
    { key: 'archive', label: t('sidebar.archive'), icon: Archive },
    { key: 'trash', label: t('sidebar.moveToTrash'), icon: Trash2, danger: true },
  ]
})

function openMenuAtCursor(event: MouseEvent, kind: 'project' | 'session' | 'free', id: string, name: string) {
  menuTarget.value = { kind, id, name, x: event.clientX, y: event.clientY }
}

function openMenuAtButton(event: MouseEvent, kind: 'project' | 'session' | 'free', id: string, name: string) {
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
  menuTarget.value = { kind, id, name, x: rect.left, y: rect.bottom + 4 }
}

function startRename(target: MenuTarget) {
  if (target.kind === 'free') return
  renaming.value = { kind: target.kind, id: target.id }
  if (target.kind === 'project' && !isExpanded(target.id)) toggleExpand(target.id)
}

function submitRename(value: string) {
  const current = renaming.value
  renaming.value = null
  if (!current) return
  if (current.kind === 'project') emit('rename-project', current.id, value)
  else emit('rename-session', current.id, value)
}

function cancelRename() {
  renaming.value = null
}

async function handleMenuSelect(key: string) {
  const target = menuTarget.value
  menuTarget.value = null
  if (!target) return
  if (key === 'edit-workspace') { emit('edit-workspace', target.id); return }
  if (key === 'retry-workspace') { emit('retry-workspaces', target.id); return }
  if (key === 'unregister-workspace') {
    const confirmed = await confirm({
      title: t('sidebar.unregisterWorkspaceConfirmTitle'),
      message: t('sidebar.unregisterWorkspaceConfirmMessage', { name: target.name }),
      confirmLabel: t('sidebar.unregisterWorkspace'),
      destructive: true,
    })
    if (confirmed) emit('unregister-workspace', target.id)
    return
  }
  if (key === 'move-up' || key === 'move-down') {
    const direction = key === 'move-up' ? -1 : 1
    // The free conversation is its own block now, so its arrows step between blocks.
    if (target.kind === 'free') list.stepSection('free', direction)
    else list.step(target.id, direction)
    return
  }
  if (key === 'rename') {
    startRename(target)
    return
  }
  if (key === 'migrate') {
    migrationSession.value = target
    migrationProject.value = migrationProjects.value[0] ? selectionKey(migrationProjects.value[0]) : ''
    await nextTick()
    migrationDialog.value?.showModal()
    return
  }
  if (key === 'archive') {
    if (target.kind === 'project') emit('archive-project', target.id)
    else emit('archive-session', target.id)
    return
  }
  if (key === 'trash') {
    const isProject = target.kind === 'project'
    const confirmed = await confirm({
      title: isProject ? t('sidebar.trashProjectConfirmTitle') : t('sidebar.trashSessionConfirmTitle'),
      message: isProject
        ? t('sidebar.trashProjectConfirmMessage', { name: target.name })
        : t('sidebar.trashSessionConfirmMessage', { name: target.name }),
      confirmLabel: t('sidebar.moveToTrash'),
      destructive: true,
    })
    if (!confirmed) return
    if (isProject) emit('trash-project', target.id)
    else emit('trash-session', target.id)
  }
}
function submitMigration() {
  if (!migrationSession.value || !migrationProjects.value.some(project => selectionKey(project) === migrationProject.value)) return
  emit('migrate-session', migrationSession.value.id, migrationProject.value)
  migrationDialog.value?.close(); migrationSession.value = null
}

// Free conversation is an ordinary list item; it is only the default first entry when the user
// has not moved it. Order is the single source of truth for the project rows.
interface WorkspaceRow { key: string; name: string; project: ProjectDto | null }
const workspaces = computed<WorkspaceRow[]>(() => {
  const candidates: WorkspaceRow[] = [
    { key: freeWorkspaceKey, name: t('chat.freeConversation'), project: null },
    ...props.projects.map(project => ({ key: selectionKey(project), name: project.name, project: project as ProjectDto | null })),
  ]
  const remaining = new Map(candidates.map(workspace => [workspace.key, workspace]))
  const ordered: WorkspaceRow[] = []
  for (const key of list.state.value.order) {
    const workspace = remaining.get(key)
    if (workspace) { ordered.push(workspace); remaining.delete(key) }
  }
  for (const workspace of candidates) if (remaining.has(workspace.key)) ordered.push(workspace)
  return ordered
})
const freeWorkspace = computed(() => workspaces.value.find(workspace => !workspace.project) ?? null)
const projectWorkspaces = computed(() => workspaces.value.filter(workspace => workspace.project))
// Sessions not bound to any project (Codex-style free conversations). The title is not a
// filter: a newly created conversation still carries the default 'Tinadec session' title until
// its first message generates one, so hiding that title made every fresh free conversation
// invisible. Declared with the other session derivations because `allSessions` reads it while
// the option lists below are being evaluated.
const freeSessions = computed(() =>
  props.sessions.filter((s) => !s.project_id && s.title)
)
function allSessions(key: string): SessionDto[] { return key === freeWorkspaceKey ? freeSessions.value : getProjectSessions(key) }
function shownSessions(key: string) {
  const active = props.sessions.find(isActiveSession)
  return recentWorkspaceSessions(allSessions(key), active ? selectionKey(active) : null, list.state.value.allKeys.includes(key))
}
function isActiveSession(session: SessionDto) { return session.id === props.selectedSessionId && (!session.storage_id || session.storage_id === selectedStorage.value) }

// ---- Section options (最近对话 / 最近活动 / 分组开关) ----
/**
 * Every conversation the sidebar can reach, newest first, each labelled with its workspace.
 * Both flat lists (最近对话, 最近的活动) read this ranking, and both deliberately ignore the
 * group switches: hiding the grouped rows must not take the shortcuts with them.
 */
const conversationRows = computed(() => {
  const source = new Map<string, string>()
  const rows: SessionDto[] = []
  for (const workspace of workspaces.value) {
    for (const row of allSessions(workspace.key)) {
      const key = selectionKey(row)
      if (source.has(key)) continue
      source.set(key, workspace.name)
      rows.push(row)
    }
  }
  return recentSessionRows(rows, rows.length)
    .map(session => ({ session, projectName: source.get(selectionKey(session)) ?? '' }))
})
const RECENT_CONVERSATION_LIMIT = 8
const recentConversations = computed(() => conversationRows.value.slice(0, RECENT_CONVERSATION_LIMIT))
// The activity cards are taller, so 最近活动 stays at five; 所有活动 is the opt-in full list.
/** A live duration only has to look right, so a half-minute tick is enough. */
const ACTIVITY_CLOCK_MS = 30_000
const activityNow = ref(Date.now())
let activityClock: ReturnType<typeof setInterval> | null = null
/** A live run's duration has to grow; a terminal one does not, so the clock only runs while on. */
function syncActivityClock(enabled: boolean) {
  if (activityClock) { clearInterval(activityClock); activityClock = null }
  if (!enabled) return
  activityNow.value = Date.now()
  activityClock = setInterval(() => { activityNow.value = Date.now() }, ACTIVITY_CLOCK_MS)
}
const activityEnabled = computed(() => list.state.value.activity || list.state.value.allActivity || list.state.value.projectActivity)
/**
 * Every conversation a visible activity view is about to draw a card for. There is no
 * cross-session activity endpoint, so this is precisely the set the composable reads: the five
 * cards 最近活动 shows, the whole list 所有活动 shows, and each project's conversations for 项目活动.
 */
const activityCandidates = computed<ActivityCandidate[]>(() => {
  const state = list.state.value
  if (!state.activity && !state.allActivity && !state.projectActivity) return []
  const rows: ActivityCandidate[] = []
  const seen = new Set<string>()
  const push = (row: ActivityCandidate) => {
    const key = selectionKey(row.session)
    if (seen.has(key)) return
    seen.add(key)
    rows.push(row)
  }
  if (state.activity) conversationRows.value.slice(0, ACTIVITY_LIMIT).forEach(push)
  if (state.allActivity) conversationRows.value.forEach(push)
  if (state.projectActivity) {
    for (const workspace of projectWorkspaces.value) {
      for (const session of allSessions(workspace.key)) push({ session, projectName: workspace.name })
    }
  }
  return rows
})
const { runs: activityRuns, refresh: refreshActivity } = useRecentActivity(
  () => activityCandidates.value,
  { enabled: () => activityEnabled.value },
)
watch(activityEnabled, enabled => {
  syncActivityClock(enabled)
  if (enabled) void refreshActivity()
})
// A view restored from the local record is already on at mount: without this it would sit on
// "状态未知" until the next session change moved the roster signature.
onMounted(() => {
  if (!activityEnabled.value) return
  syncActivityClock(true)
  void refreshActivity()
})
/** One card's worth of facts: the run it lands on, plus whether that roster was ever read. */
function activityCard(candidate: ActivityCandidate) {
  const key = selectionKey(candidate.session)
  // "No run in a roster I read" and "I never read this roster" are different facts: the second
  // one must not render as an idle task.
  const read = key in activityRuns.value
  const run = activityRuns.value[key] ?? null
  return { ...candidate, run, facts: presentActivity(run, candidate.session, activityNow.value, locale.value, read) }
}
const activities = computed(() => conversationRows.value.slice(0, ACTIVITY_LIMIT).map(activityCard))
const allActivities = computed(() => conversationRows.value.map(activityCard))
/** 项目活动: the same project groups, one card per conversation, name dropped from each card. */
function workspaceActivities(workspace: WorkspaceRow) {
  return shownSessions(workspace.key).map(session => activityCard({ session, projectName: workspace.name }))
}
/**
 * What the scrollport renders: one entry per visible block, in the order the options menu holds.
 * The toggles hide a block without touching the flat lists, so conversations stay reachable from
 * 最近对话 / 所有活动 when their groups are off.
 */
type ActivityCardView = ReturnType<typeof activityCard>
interface ActivityItem {
  key: string
  kind: 'activity'
  title: string
  empty: string
  cards: ActivityCardView[]
  /** False for cards nested in a project group, whose header already names the workspace. */
  showWorkspace: boolean
}
interface WorkspaceItem {
  key: string
  kind: 'workspace'
  workspace: WorkspaceRow
  /** 项目对话 draws compact session rows; 项目活动 draws the same conversations as cards. */
  variant: 'rows' | 'activity'
  cards: ActivityCardView[]
}
type ListItem = { key: string; kind: 'recent' } | ActivityItem | WorkspaceItem
const listItems = computed<ListItem[]>(() => {
  const state = list.state.value
  const projectItems = (variant: 'rows' | 'activity'): WorkspaceItem[] => projectWorkspaces.value.map(workspace => ({
    // The variant is part of the key: both toggles on means the same project is rendered twice.
    key: `${variant}:${workspace.key}`,
    kind: 'workspace',
    workspace,
    variant,
    cards: variant === 'activity' ? workspaceActivities(workspace) : [],
  }))
  const items: ListItem[] = []
  for (const section of state.sectionOrder) {
    if (section === 'recent') {
      if (state.recent) items.push({ key: 'recent', kind: 'recent' })
    } else if (section === 'activity') {
      if (state.activity) items.push({ key: 'activity', kind: 'activity', title: t('sidebar.recentActivity'), empty: t('sidebar.activityEmpty'), cards: activities.value, showWorkspace: true })
    } else if (section === 'allActivity') {
      if (state.allActivity) items.push({ key: 'all-activity', kind: 'activity', title: t('sidebar.allActivity'), empty: t('sidebar.activityEmpty'), cards: allActivities.value, showWorkspace: true })
    } else if (section === 'projects') {
      if (state.projectConversations) items.push(...projectItems('rows'))
    } else if (section === 'projectActivity') {
      if (state.projectActivity) items.push(...projectItems('activity'))
    } else if (state.freeConversation && freeWorkspace.value) {
      const workspace = freeWorkspace.value
      items.push({ key: `rows:${workspace.key}`, kind: 'workspace', workspace, variant: 'rows', cards: [] })
    }
  }
  return items
})
const optionsMenuId = `sidebar-workspace-options-${useId()}`
const optionsMenuOpen = ref(false)
const optionsMenuStyle = ref({ left: '0px', top: '0px' })
/** Matches `.sidebar-workspace-options` width; the panel is display:none while closed, so it
 *  cannot be measured before it opens. Change both together. */
const OPTIONS_MENU_WIDTH = 236
function openOptionsMenu(event: MouseEvent) {
  // The native invoker performs the toggle; this only positions the panel under the gear.
  const trigger = event.currentTarget as HTMLButtonElement
  const rect = trigger.getBoundingClientRect()
  optionsMenuStyle.value = {
    left: `${Math.max(8, Math.min(rect.right - OPTIONS_MENU_WIDTH, window.innerWidth - OPTIONS_MENU_WIDTH - 8))}px`,
    top: `${rect.bottom + 6}px`,
  }
}
function updateOptionsMenuState(event: Event) {
  optionsMenuOpen.value = (event as ToggleEvent).newState === 'open'
}
interface SidebarSectionOption { id: SidebarSection; label: string; checked: boolean; toggle: (value: boolean) => void }
const sectionOptions = computed<SidebarSectionOption[]>(() => [
  { id: 'recent', label: t('sidebar.recentConversations'), checked: list.state.value.recent, toggle: list.setRecent },
  { id: 'activity', label: t('sidebar.recentActivity'), checked: list.state.value.activity, toggle: list.setActivity },
  { id: 'allActivity', label: t('sidebar.allActivity'), checked: list.state.value.allActivity, toggle: list.setAllActivity },
  { id: 'projectActivity', label: t('sidebar.projectActivity'), checked: list.state.value.projectActivity, toggle: list.setProjectActivity },
  { id: 'projects', label: t('sidebar.projectConversations'), checked: list.state.value.projectConversations, toggle: list.setProjectConversations },
  { id: 'free', label: t('chat.freeConversation'), checked: list.state.value.freeConversation, toggle: list.setFreeConversation },
])
/** Rows are the blocks themselves, so the panel doubles as the order control for the list. */
const orderedSectionOptions = computed(() => list.state.value.sectionOrder.flatMap((id) => {
  const option = sectionOptions.value.find(candidate => candidate.id === id)
  return option ? [option] : []
}))

function getProjectSessions(projectKey: string): SessionDto[] {
  const { id, storageId } = selectionIdentity(projectKey)
  return props.sessions.filter((s) => (s.project_id ?? null) === id && (!storageId || s.storage_id === storageId) && s.title)
}

function isExpanded(projectId: string): boolean {
  return !list.state.value.collapsedKeys.includes(projectId)
}

function toggleExpand(projectId: string) {
  list.toggle(projectId, 'collapsedKeys')
}

function handleProjectClick(projectId: string) {
  toggleExpand(projectId)
}

function handleSessionClick(sessionId: string) {
  emit('select-session', sessionId)
}

function handleNewSession(projectId: string) {
  emit('create-session', projectId)
}

function handleNewThread() {
  const selected = props.projects.find(project => project.id === props.selectedProjectId && (!project.storage_id || project.storage_id === selectedStorage.value))
  emit('create-session', selected ? selectionKey(selected) : null)
}

// Sessions not bound to any project (Codex-style free conversations). The title is
// not a filter: a newly created conversation still carries the default
// 'Tinadec session' title until its first message generates one, so hiding that
// title made every fresh free conversation invisible.

onMounted(() => {
  // An option that is already on when the window opens still needs its first read; the clock
  // only exists while the view does.
  syncActivityClock(list.state.value.activity)
  if (list.state.value.activity) void refreshActivity()
})
watch(() => props.busy, (busy, wasBusy) => {
  if (!wasBusy || busy) return
  // A finished run is exactly what turns one activity card from "进行中" into its outcome.
  void refreshActivity()
})

function openDebugStudio() {
  if (!debugStudioEnabled.value) return
  ;(window as unknown as { tinadec?: { openDebugStudio?: () => Promise<boolean> } }).tinadec?.openDebugStudio?.()
}

// ---- Footer identity ----
// The portrait carries the nickname only while the footer row has room for it in
// full plus clear space before the shell actions. The geometry below mirrors
// `.sidebar-profile` in styles.css (button padding, the name's leading margin,
// the portrait size); the decision itself lives in a pure function so it can be
// exercised without a laid-out document.
const PROFILE_AVATAR_SIZE = 20
const PROFILE_BUTTON_PADDING = 3
const PROFILE_NAME_GAP = 6

const nickname = computed(() => userProfile.nickname.trim())
/** Signed out, the footer names the state instead of a person. */
const displayName = computed(() => (auth.loggedIn.value ? nickname.value : t('settings.personalNotLoggedIn')))
const footerEl = ref<HTMLElement | null>(null)
const footerActionsEl = ref<HTMLElement | null>(null)
const profileNameProbe = ref<HTMLElement | null>(null)
const profileNameShown = ref(false)
/** Natural name width, handed to CSS as the end of the reveal (see `.sidebar-profile-name`). */
const profileNameMax = ref(0)

function px(value: string | null | undefined): number {
  const parsed = Number.parseFloat(value ?? '')
  return Number.isFinite(parsed) ? parsed : 0
}

function measureProfileName() {
  const footer = footerEl.value
  const probe = profileNameProbe.value
  // The collapsed rail is one icon wide and animates; measuring mid-transition
  // would flash the name in and out, so it simply stays off there.
  if (!footer || !probe || props.collapsed) { profileNameShown.value = false; return }
  const styles = getComputedStyle(footer)
  const nameWidth = probe.getBoundingClientRect().width
  profileNameMax.value = Math.ceil(nameWidth)
  profileNameShown.value = profileNameFits({
    footerWidth: footer.clientWidth,
    padding: px(styles.paddingLeft) + px(styles.paddingRight),
    gap: px(styles.columnGap) || px(styles.gap),
    actionsWidth: footerActionsEl.value?.getBoundingClientRect().width ?? 0,
    avatarSize: PROFILE_AVATAR_SIZE,
    buttonPadding: PROFILE_BUTTON_PADDING,
    nameGap: PROFILE_NAME_GAP,
    nameWidth,
  })
}

// Hover keeps a hidden name discoverable: the tooltip names the person the
// portrait belongs to, and drops the redundant repetition once the name is up.
// The accessible name always carries the nickname — the visible text is
// aria-hidden, so a screen reader must not lose the identity when the row narrows.
const profileTitle = computed(() => {
  if (!auth.loggedIn.value) return t('settings.personalLogin')
  return nickname.value && !profileNameShown.value
    ? `${nickname.value} · ${t('sidebar.personal')}`
    : t('sidebar.personal')
})
const profileLabel = computed(() => {
  if (!auth.loggedIn.value) return `${t('settings.personalNotLoggedIn')} · ${t('sidebar.personal')}`
  return nickname.value
    ? `${nickname.value} · ${t('sidebar.personal')}`
    : t('sidebar.personal')
})

async function handleProfileClick(): Promise<void> {
  if (!auth.loggedIn.value) {
    await auth.login()
    return
  }
  emit('go-personal')
}

let footerResizeObserver: ResizeObserver | null = null
onMounted(() => {
  measureProfileName()
  if (typeof ResizeObserver === 'undefined') return
  footerResizeObserver = new ResizeObserver(measureProfileName)
  // Resizing the nav column (or collapsing it) resizes the footer, which is the
  // signal the fit test waits for.
  if (footerEl.value) footerResizeObserver.observe(footerEl.value)
  // The probe measures in the face that is actually loaded: a late webfont swap
  // changes the name's width, and no resize follows it.
  document.fonts?.ready.then(measureProfileName, () => {})
})
onBeforeUnmount(() => {
  footerResizeObserver?.disconnect()
  footerResizeObserver = null
  if (activityClock) { clearInterval(activityClock); activityClock = null }
})
watch([displayName, () => props.collapsed], async () => {
  await nextTick()
  measureProfileName()
})
</script>

<template>
  <aside class="sidebar" :class="{ 'sidebar-collapsed': collapsed }" :style="panelStyle" v-bind="panelDataAttrs">
    <div class="sidebar-topbar">
      <div class="brand">
        <BrandLogo :size="14" class="sidebar-icon brand-logo-icon" />
        <TinadecCalligraphy :size="14" class="sidebar-label brand-calligraphy" />
      </div>
    </div>

    <nav class="sidebar-nav">
      <UiButton
        variant="ghost"
        size="sm"
        class="sidebar-nav-item w-full justify-start"
        :disabled="busy"
        :title="t('sidebar.newChat')"
        @click="handleNewThread"
      >
        <MessageSquare :size="16" class="sidebar-icon" />
        <span class="sidebar-label">{{ t('sidebar.newChat') }}</span>
      </UiButton>
      <UiButton
        variant="ghost"
        size="sm"
        class="sidebar-nav-item w-full justify-start"
        :title="t('sidebar.market')"
        @click="emit('go-market')"
      >
        <Store :size="16" class="sidebar-icon" />
        <span class="sidebar-label">{{ t('sidebar.market') }}</span>
      </UiButton>
      <UiButton
        variant="ghost"
        size="sm"
        class="sidebar-nav-item w-full justify-start"
        v-if="debugStudioEnabled"
        :title="t('sidebar.debugStudio')"
        @click="openDebugStudio()"
      >
        <Bug :size="16" class="sidebar-icon" />
        <span class="sidebar-label">{{ t('sidebar.debugStudio') }}</span>
      </UiButton>
    </nav>

    <!-- The heading and the scrollport share one hover region: the "new workspace"
         action belongs to this block, not to the whole sidebar. -->
    <div class="workspace-section">
      <div class="workspace-section-heading">
        <button class="workspace-section-toggle" :aria-expanded="!list.state.value.collapsed" aria-controls="sidebar-workspaces" @click="list.state.value.collapsed = !list.state.value.collapsed">
          <span class="sidebar-label">{{ t('sidebar.workspaces') }}</span>
          <ChevronRight :size="13" class="workspace-section-chevron" :class="{ expanded: !list.state.value.collapsed }" />
        </button>
        <button
          type="button"
          class="workspace-section-action workspace-section-option sidebar-extra"
          :aria-label="t('sidebar.workspaceOptions')"
          :title="t('sidebar.workspaceOptions')"
          :aria-expanded="optionsMenuOpen"
          :aria-controls="optionsMenuId"
          aria-haspopup="dialog"
          :popovertarget="optionsMenuId"
          popovertargetaction="toggle"
          @click="openOptionsMenu"
        ><SlidersHorizontal :size="14" /></button>
        <button type="button" class="workspace-section-action workspace-section-add sidebar-extra" :aria-label="t('sidebar.newWorkspace')" :title="t('sidebar.newWorkspace')" @click.stop="emit('open-project')"><Plus :size="14" /></button>
      </div>
      <div id="sidebar-workspaces" class="sidebar-list" :class="{ 'sidebar-list--group-collapsed': list.state.value.collapsed }">
        <!-- One entry per visible block. The four option rows are siblings in this scrollport, so
             the order the options menu holds is literally the order rendered here. -->
        <template v-if="!list.state.value.collapsed">
          <template v-for="item in listItems" :key="item.key">
            <div v-if="item.kind === 'recent'" class="recent-conversations" role="group" :aria-label="t('sidebar.recentConversations')">
              <div class="recent-conversations-heading">{{ t('sidebar.recentConversations') }}</div>
              <div
                v-for="entry in recentConversations"
                :key="selectionKey(entry.session)"
                class="session-row recent-conversation-row"
                :class="{ active: isActiveSession(entry.session) }"
                @contextmenu.prevent="openMenuAtCursor($event, 'session', selectionKey(entry.session), entry.session.title)"
              >
                <button class="session-item" :aria-current="isActiveSession(entry.session) ? 'page' : undefined" :title="`${entry.session.title} · ${entry.projectName}`" @click="handleSessionClick(selectionKey(entry.session))">
                  <MessageSquare :size="13" class="recent-conversation-icon" />
                  <span class="session-title">{{ entry.session.title }}</span>
                  <span class="recent-conversation-source">{{ entry.projectName }}</span>
                </button>
              </div>
              <div v-if="!recentConversations.length" class="session-empty">{{ t('sidebar.noSessions') }}</div>
            </div>
            <div v-else-if="item.kind === 'activity'" class="activity-group" role="group" :aria-label="item.title">
              <div class="activity-group-heading">{{ item.title }}</div>
              <SidebarActivityCard
                v-for="entry in item.cards"
                :key="selectionKey(entry.session)"
                :session="entry.session"
                :project-name="entry.projectName"
                :facts="entry.facts"
                :show-workspace="item.showWorkspace"
                :active="isActiveSession(entry.session)"
                @select="handleSessionClick(selectionKey(entry.session))"
                @menu="openMenuAtCursor($event, 'session', selectionKey(entry.session), entry.session.title)"
              />
              <div v-if="!item.cards.length" class="session-empty">{{ item.empty }}</div>
            </div>
            <div v-else-if="item.kind === 'workspace'" class="project-group" :class="{ 'free-conversation-group': !item.workspace.project }" :data-workspace-key="item.workspace.key">
              <div class="project-row" :class="{
                  active: item.workspace.project ? item.workspace.project.id === selectedProjectId && (!item.workspace.project.storage_id || item.workspace.project.storage_id === selectedStorage) : !selectedProjectId,
                  'drag-source': draggedWorkspace === item.workspace.key,
                  'drag-over-before': dragOverKey === item.workspace.key && !dragOverAfter,
                  'drag-over-after': dragOverKey === item.workspace.key && dragOverAfter,
                }"
                :draggable="item.workspace.project !== null" @dragstart.stop="startDrag($event, item.workspace.key)" @dragend="draggedWorkspace = null; dragOverKey = null"
                @dragover.prevent="dragOverWorkspace($event, item.workspace.key)" @drop.stop="dropWorkspace($event, item.workspace.key)"
                @contextmenu.prevent="openMenuAtCursor($event, item.workspace.project ? 'project' : 'free', item.workspace.key, item.workspace.name)" @click="handleProjectClick(item.workspace.key)">
                <button class="project-row-main" :title="item.workspace.project?.path ?? item.workspace.name" :aria-expanded="isExpanded(item.workspace.key)" :aria-controls="`workspace-sessions-${item.workspace.key}`" :aria-keyshortcuts="'Alt+ArrowUp Alt+ArrowDown'" @keydown="reorderWithKeyboard($event, item.workspace.key)" @click.stop="handleProjectClick(item.workspace.key)">
                  <component :is="item.workspace.project ? workspaceIcons[item.workspace.project.icon ?? 'folder'] ?? FolderOpen : Sparkles" :size="15" class="sidebar-list-item-icon sidebar-icon" :class="`workspace-color-${item.workspace.project?.color ?? 'default'}`" />
                  <span class="sidebar-list-item-text sidebar-label">{{ item.workspace.name }}</span>
                </button>
                <button v-if="item.workspace.project" class="project-row-action sidebar-extra" :aria-label="`${item.workspace.name} · ${t('sidebar.moreActions')}`" :title="t('sidebar.moreActions')" @mousedown.stop @click.stop="openMenuAtButton($event, 'project', item.workspace.key, item.workspace.name)"><MoreHorizontal :size="14" /></button>
                <button class="project-row-action sidebar-extra" :disabled="busy || item.workspace.project?.availability === 'error'" :aria-label="`${item.workspace.name} · ${t('sidebar.newChat')}`" :title="t('sidebar.newChat')" @mousedown.stop @click.stop="emit('create-session', item.workspace.project ? item.workspace.key : null)"><Plus :size="14" /></button>
              </div>
              <div v-if="isExpanded(item.workspace.key)" :id="`workspace-sessions-${item.workspace.key}`" class="project-sessions sidebar-extra">
                <div v-if="workspaceLoadStates?.[item.workspace.key]?.status === 'loading'" class="session-empty" role="status">{{ t('common.loading') }}</div>
                <div v-else-if="workspaceLoadStates?.[item.workspace.key]?.status === 'error' || item.workspace.project?.availability === 'error'" class="workspace-load-error" role="status">
                  <span :title="workspaceLoadStates?.[item.workspace.key]?.message ?? errorCodeMessage(item.workspace.project?.availability_code, undefined, { t, te }) ?? item.workspace.project?.availability_error">{{ t('home.loadFailed') }}</span><button @click="emit('retry-workspaces', item.workspace.key)">{{ t('common.retry') }}</button>
                </div>
                <template v-if="item.variant === 'rows'">
                  <div v-for="session in shownSessions(item.workspace.key)" :key="selectionKey(session)" class="session-row" :class="{ active: isActiveSession(session) }" @contextmenu.prevent="openMenuAtCursor($event, 'session', selectionKey(session), session.title)">
                    <button class="session-item" :aria-current="isActiveSession(session) ? 'page' : undefined" :title="session.title" @click="handleSessionClick(selectionKey(session))" @dblclick.stop="renaming = { kind: 'session', id: selectionKey(session) }">
                      <InlineRenameInput v-if="renaming?.kind === 'session' && renaming.id === selectionKey(session)" :model-value="session.title" class="session-title" @submit="submitRename" @cancel="cancelRename" /><span v-else class="session-title">{{ session.title }}</span>
                    </button>
                    <button class="session-more" :title="t('sidebar.moreActions')" @click.stop="openMenuAtButton($event, 'session', selectionKey(session), session.title)"><MoreHorizontal :size="13" /></button>
                  </div>
                </template>
                <!-- 项目活动: the same conversations, drawn as activity cards. The group header already
                     names the workspace, so each card drops the trailing name. -->
                <template v-if="item.variant === 'activity'">
                  <SidebarActivityCard
                    v-for="entry in item.cards"
                    :key="`card:${selectionKey(entry.session)}`"
                    :session="entry.session"
                    :project-name="entry.projectName"
                    :facts="entry.facts"
                    :show-workspace="false"
                    :active="isActiveSession(entry.session)"
                    @select="handleSessionClick(selectionKey(entry.session))"
                    @menu="openMenuAtCursor($event, 'session', selectionKey(entry.session), entry.session.title)"
                  />
                </template>
                <div v-if="!allSessions(item.workspace.key).length && (!workspaceLoadStates?.[item.workspace.key] || workspaceLoadStates[item.workspace.key]?.status === 'ready') && item.workspace.project?.availability !== 'error'" class="session-empty">{{ t('sidebar.noSessions') }}</div>
                <button v-if="allSessions(item.workspace.key).length > shownSessions(item.workspace.key).length || list.state.value.allKeys.includes(item.workspace.key)" class="workspace-show-more" @click="list.toggle(item.workspace.key, 'allKeys')">{{ list.state.value.allKeys.includes(item.workspace.key) ? t('sidebar.showFewer') : t('sidebar.showAll') }}</button>
              </div>
            </div>
          </template>
        </template>
      </div>
    </div>

    <div ref="footerEl" class="sidebar-footer">
      <!-- Identity sits at the far left; the three shell actions stay right-aligned.
           The nickname rides beside the portrait only while the row can hold it whole. -->
      <button
        type="button"
        class="sidebar-profile"
        :class="{ 'sidebar-profile--named': profileNameShown }"
        :title="profileTitle"
        :aria-label="profileLabel"
        @click="handleProfileClick"
      >
        <PersonalAvatar :size="PROFILE_AVATAR_SIZE" :empty="!auth.loggedIn.value" />
        <!-- Rendered whenever there is a name to carry, and animated by width + opacity:
             collapsing it to zero width is what makes the fade reversible mid-resize.
             The cap is the measured natural width, so the reveal never shows a clipped word. -->
        <span
          v-if="displayName"
          class="sidebar-profile-name"
          :class="{ 'sidebar-profile-name--shown': profileNameShown }"
          :style="{ '--profile-name-max': `${profileNameMax}px` }"
          aria-hidden="true"
        >{{ displayName }}</span>
        <!-- Measured, never painted: the fit test needs the name's natural width, which
             a clamped or hidden label cannot report. -->
        <span ref="profileNameProbe" class="sidebar-profile-name-probe" aria-hidden="true">{{ displayName }}</span>
      </button>
      <div ref="footerActionsEl" class="sidebar-footer-actions" :class="{ 'sidebar-footer-actions-collapsed': collapsed }">
        <UiButton
          variant="ghost"
          size="icon"
          class="sidebar-footer-action"
          :title="t('space.switchView')"
          :aria-label="t('space.switchView')"
          :aria-expanded="viewMenuOpen"
          :aria-controls="viewMenuId"
          aria-haspopup="dialog"
          :popovertarget="viewMenuId"
          popovertargetaction="toggle"
          @click="openViewMenu"
        >
          <Waypoints v-if="spaceActive" :size="16" /><LayoutGrid v-else :size="16" />
        </UiButton>
        <UiButton
          variant="ghost"
          size="icon"
          class="sidebar-footer-action"
          :title="t('sidebar.settings')"
          @click="emit('go-settings')"
        >
          <Settings :size="16" />
        </UiButton>
        <UiButton
          variant="ghost"
          size="icon"
          class="sidebar-footer-action"
          :title="collapsed ? t('sidebar.expand') : t('sidebar.collapse')"
          @click="emit('toggle-collapse')"
        >
          <component :is="collapsed ? PanelLeftOpen : PanelLeftClose" :size="16" />
        </UiButton>
      </div>
    </div>

    <RowContextMenu
      :visible="menuTarget !== null"
      :x="menuTarget?.x ?? 0"
      :y="menuTarget?.y ?? 0"
      :items="menuItems"
      @select="handleMenuSelect"
      @close="menuTarget = null"
    />
    <div :id="viewMenuId" ref="viewMenu" popover class="sidebar-view-menu" :style="viewMenuStyle" role="dialog" :aria-label="t('space.switchView')" @beforetoggle="updateViewMenuState">
      <button :aria-pressed="!spaceActive" :autofocus="!spaceActive" @click="changeView('flat')"><LayoutGrid :size="18" />{{ t('space.flat') }}</button>
      <button :aria-pressed="!!spaceActive" :autofocus="!!spaceActive" @click="changeView('space')"><Waypoints :size="18" />{{ t('space.title') }}</button>
    </div>
    <div
      :id="optionsMenuId"
      popover
      class="sidebar-workspace-options"
      :style="optionsMenuStyle"
      role="dialog"
      :aria-label="t('sidebar.workspaceOptions')"
      @beforetoggle="updateOptionsMenuState"
    >
      <p class="sidebar-workspace-options-title">{{ t('sidebar.workspaceOptions') }}</p>
      <!-- The row is the hit target: a label wrapping a button does not forward clicks, so the
           row toggles itself and the checkbox stops the bubbling it already handled. The handle at
           the right is the drag source, which keeps a click on the row a toggle. -->
      <div
        v-for="option in orderedSectionOptions"
        :key="option.id"
        class="sidebar-option"
        :class="{
          'drag-source': draggedSection === option.id,
          'drag-over-before': sectionDragOver === option.id && !sectionDragAfter,
          'drag-over-after': sectionDragOver === option.id && sectionDragAfter,
        }"
        :data-section="option.id"
        @click="option.toggle(!option.checked)"
        @dragover.prevent="dragOverSection($event, option.id)"
        @drop.stop="dropSection($event, option.id)"
        @dragend="endSectionDrag"
        @keydown="reorderSectionWithKeyboard($event, option.id)"
      >
        <UiCheckbox
          :model-value="option.checked"
          :aria-label="option.label"
          aria-keyshortcuts="Alt+ArrowUp Alt+ArrowDown"
          @click.stop
          @update:model-value="option.toggle($event)"
        />
        <span class="sidebar-option-label">{{ option.label }}</span>
        <span
          class="sidebar-option-grip"
          role="presentation"
          draggable="true"
          :title="t('sidebar.reorderSection')"
          @click.stop
          @dragstart.stop="startSectionDrag($event, option.id)"
          @dragend="endSectionDrag"
        ><GripVertical :size="13" /></span>
      </div>
    </div>
    <dialog ref="migrationDialog" class="sidebar-migration-dialog" aria-labelledby="migration-title" @close="migrationSession = null">
      <form @submit.prevent="submitMigration">
        <h2 id="migration-title">{{ t('sidebar.migrateSession') }}</h2>
        <p>{{ migrationSession?.name }}</p>
        <label>{{ t('sidebar.migrationTarget') }}<UiSelectField v-model="migrationProject" :aria-label="t('sidebar.migrationTarget')" :options="migrationProjects.map((project) => ({ value: selectionKey(project), label: `${project.name} · ${project.path}` }))" /></label>
        <p class="quiet">{{ t('sidebar.migrationExplanation') }}</p>
        <div><UiButton type="button" variant="ghost" @click="migrationDialog?.close()">{{ t('common.cancel') }}</UiButton><UiButton type="submit" :disabled="!migrationProject || busy">{{ t('sidebar.migrateSession') }}</UiButton></div>
      </form>
    </dialog>
  </aside>
</template>

<style scoped>
/* Native popovers/dialogs live in the top layer, where the panel root's
   backdrop-filter and material background do not reach. They inherit the
   surface tokens but must apply the raised filter themselves. */
.sidebar-view-menu, .sidebar-workspace-options, .sidebar-migration-dialog { backdrop-filter: var(--material-filter-raised, none); -webkit-backdrop-filter: var(--material-filter-raised, none); }
.sidebar-view-menu { position: fixed; inset: auto; margin: 0; width: 200px; padding: 6px; border: 1px solid var(--border-muted); border-radius: 12px; background: var(--surface-raised); color: var(--text-primary); box-shadow: var(--shadow-card-subtle); opacity: 0; transform: translateY(6px) scale(0.98); transform-origin: bottom left; pointer-events: none; transition: opacity 160ms ease-out, transform 160ms ease-out, display 160ms allow-discrete, overlay 160ms allow-discrete; }
.sidebar-view-menu:popover-open { opacity: 1; transform: translateY(0) scale(1); pointer-events: auto; }
@starting-style {
  .sidebar-view-menu:popover-open { opacity: 0; transform: translateY(6px) scale(0.98); }
}
@media (prefers-reduced-motion: reduce) {
  .sidebar-view-menu { transition: none; transform: none; }
}
.sidebar-view-menu button { display: flex; align-items: center; gap: 10px; width: 100%; padding: 12px; border: 0; border-radius: 8px; background: transparent; color: inherit; cursor: pointer; }
.sidebar-view-menu button:hover, .sidebar-view-menu button[aria-pressed="true"] { background: var(--surface-selected); }
/* Section options mirror the view chooser: a native popover anchored by the invoker, so
   Escape, light dismiss and focus restore stay browser behaviour instead of hand-rolled. */
.sidebar-workspace-options { position: fixed; inset: auto; margin: 0; width: 236px; padding: 6px; border: 1px solid var(--border-muted); border-radius: 12px; background: var(--surface-raised); color: var(--text-primary); box-shadow: var(--shadow-card-subtle); opacity: 0; transform: translateY(-6px) scale(0.98); transform-origin: top right; pointer-events: none; transition: opacity 160ms ease-out, transform 160ms ease-out, display 160ms allow-discrete, overlay 160ms allow-discrete; }
.sidebar-workspace-options:popover-open { opacity: 1; transform: translateY(0) scale(1); pointer-events: auto; }
@starting-style { .sidebar-workspace-options:popover-open { opacity: 0; transform: translateY(-6px) scale(0.98); } }
@media (prefers-reduced-motion: reduce) { .sidebar-workspace-options { transition: none; transform: none; } .sidebar-option.drag-source { transition: none; } }
.sidebar-workspace-options-title { margin: 4px 8px 6px; font-size: 11px; font-weight: 500; letter-spacing: 0.02em; color: var(--text-muted); }
/* One row = one block of the list below it: the checkbox toggles it, the handle reorders it. */
.sidebar-option { position: relative; display: flex; align-items: center; gap: 8px; width: 100%; padding: 9px 8px; border-radius: 8px; background: transparent; color: var(--text-primary); cursor: pointer; }
.sidebar-option:hover, .sidebar-option:focus-within { background: var(--surface-hover); }
.sidebar-option-label { flex: 1; min-width: 0; font-size: 12px; color: var(--text-primary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
/* Always faintly visible, never invisible: an affordance nobody knows about is not an affordance. */
.sidebar-option-grip { display: inline-flex; flex: none; align-items: center; justify-content: center; padding: 2px; border-radius: 4px; color: var(--text-muted); opacity: 0.45; cursor: grab; transition: opacity 120ms ease; }
.sidebar-option:hover .sidebar-option-grip, .sidebar-option:focus-within .sidebar-option-grip, .sidebar-option.drag-source .sidebar-option-grip { opacity: 1; }
.sidebar-option-grip:active { cursor: grabbing; }
.sidebar-option.drag-source {
  opacity: 0.55;
  /* Over-damped spring (k 1200, c 80, zeta ~1.15): monotonic settle, no overshoot. */
  transition: opacity 250ms linear(0, 0.068 5%, 0.202 10%, 0.344 15%, 0.473 20%, 0.582 25%, 0.671 30%, 0.742 35%, 0.798 40%, 0.842 45%, 0.877 50%, 0.904 55%, 0.925 60%, 0.942 65%, 0.955 70%, 0.965 75%, 0.973 80%, 0.979 85%, 0.983 90%, 0.987 95%, 0.99 100%);
}
.sidebar-option.drag-over-before::before, .sidebar-option.drag-over-after::after { content: ''; position: absolute; left: 8px; right: 8px; height: 2px; border-radius: 1px; background: var(--accent-primary); pointer-events: none; }
.sidebar-option.drag-over-before::before { top: -1px; }
.sidebar-option.drag-over-after::after { bottom: -1px; }
.sidebar-migration-dialog { width: min(560px, calc(100vw - 32px)); border: 1px solid var(--border-muted); border-radius: 12px; padding: 24px; background: var(--surface-raised); color: var(--text-primary); box-shadow: var(--shadow-card-subtle); }
.sidebar-migration-dialog::backdrop { background: color-mix(in srgb, var(--bg-overlay) 65%, transparent); }
.sidebar-migration-dialog form, .sidebar-migration-dialog label { display: grid; gap: 12px; }
.sidebar-migration-dialog form > div { display: flex; justify-content: flex-end; gap: 12px; }
.sidebar-migration-dialog h2 { font-size: 18px; margin: 0; }
.sidebar-migration-dialog select:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 2px; }
</style>
