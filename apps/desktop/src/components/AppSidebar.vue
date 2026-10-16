<script setup lang="ts">
import { computed, nextTick, onMounted, ref, useId, watch } from 'vue'
import { useWorkspaceList, freeWorkspaceKey, recentWorkspaceSessions } from '@/composables/useWorkspaceList'
import { workspaceIcons, type WorkspaceLoadState } from '@/lib/workspaces'
import { selectionKey, selectionIdentity, selectedStorage } from '@/lib/storageScope'
import {
  Archive,
  ArrowRightLeft,
  Bug,
  ChevronRight,
  FolderOpen,
  MessageSquare,
  MoreHorizontal,
  PanelLeftClose,
  PanelLeftOpen,
  RefreshCw,
  Pencil,
  Plus,
  Settings,
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
import TinadecCalligraphy from '@/components/TinadecCalligraphy.vue'
import InlineRenameInput from '@/components/InlineRenameInput.vue'
import RowContextMenu, { type RowMenuItem } from '@/components/RowContextMenu.vue'
import { UiButton, UiSelectField } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { useDebugStudio } from '@/composables/useDebugStudio'
import { errorCodeMessage } from '@/lib/apiError'

const { t, te } = useI18n()
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
function startDrag(event: DragEvent, key: string) {
  draggedWorkspace.value = key
  if (event.dataTransfer) { event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('text/x-tinadec-workspace', key) }
}
function dragOverWorkspace(event: DragEvent, key: string) {
  if (!draggedWorkspace.value) return
  const row = (event.currentTarget as HTMLElement).getBoundingClientRect()
  dragOverKey.value = key
  dragOverAfter.value = event.clientY > row.top + row.height / 2
}
function dropWorkspace(event: DragEvent, key: string) {
  event.preventDefault()
  if (draggedWorkspace.value) list.move(draggedWorkspace.value, key, dragOverAfter.value)
  draggedWorkspace.value = null
  dragOverKey.value = null
}
// Drag is the primary gesture, but reordering must not require a pointer.
function reorderWithKeyboard(event: KeyboardEvent, key: string) {
  if (!event.altKey || (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) return
  event.preventDefault()
  list.step(key, event.key === 'ArrowUp' ? -1 : 1)
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
  if (key === 'move-up' || key === 'move-down') { list.step(target.id, key === 'move-up' ? -1 : 1); return }
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

// Free conversation is an ordinary, draggable list item; it is only the default first entry
// when the user has not moved it. Order is the single source of truth for the whole group.
const workspaces = computed(() => {
  const candidates = [
    { key: freeWorkspaceKey, name: t('chat.freeConversation'), project: null as ProjectDto | null },
    ...props.projects.map(project => ({ key: selectionKey(project), name: project.name, project: project as ProjectDto | null })),
  ]
  const remaining = new Map(candidates.map(workspace => [workspace.key, workspace]))
  const ordered: typeof candidates = []
  for (const key of list.state.value.order) {
    const workspace = remaining.get(key)
    if (workspace) { ordered.push(workspace); remaining.delete(key) }
  }
  for (const workspace of candidates) if (remaining.has(workspace.key)) ordered.push(workspace)
  return ordered
})
function allSessions(key: string): SessionDto[] { return key === freeWorkspaceKey ? freeSessions.value : getProjectSessions(key) }
function shownSessions(key: string) {
  const active = props.sessions.find(isActiveSession)
  return recentWorkspaceSessions(allSessions(key), active ? selectionKey(active) : null, list.state.value.allKeys.includes(key))
}
function isActiveSession(session: SessionDto) { return session.id === props.selectedSessionId && (!session.storage_id || session.storage_id === selectedStorage.value) }

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
const freeSessions = computed(() =>
  props.sessions.filter((s) => !s.project_id && s.title)
)

const tokenUsage = ref<number[]>([])

function openDebugStudio() {
  if (!debugStudioEnabled.value) return
  ;(window as unknown as { tinadec?: { openDebugStudio?: () => Promise<boolean> } }).tinadec?.openDebugStudio?.()
}
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

    <div class="workspace-section-heading">
      <button class="workspace-section-toggle" :aria-expanded="!list.state.value.collapsed" aria-controls="sidebar-workspaces" @click="list.state.value.collapsed = !list.state.value.collapsed">
        <span class="sidebar-label">{{ t('sidebar.workspaces') }}</span>
        <ChevronRight :size="13" class="workspace-section-chevron" :class="{ expanded: !list.state.value.collapsed }" />
      </button>
      <button class="workspace-section-add sidebar-extra" :aria-label="t('sidebar.newWorkspace')" :title="t('sidebar.newWorkspace')" @click.stop="emit('open-project')"><Plus :size="14" /></button>
    </div>
    <div id="sidebar-workspaces" class="sidebar-list" :class="{ 'sidebar-list--group-collapsed': list.state.value.collapsed }">
      <template v-if="!list.state.value.collapsed"><div v-for="workspace in workspaces" :key="workspace.key" class="project-group" :class="{ 'free-conversation-group': !workspace.project }" :data-workspace-key="workspace.key">
        <div class="project-row" :class="{
            active: workspace.project ? workspace.project.id === selectedProjectId && (!workspace.project.storage_id || workspace.project.storage_id === selectedStorage) : !selectedProjectId,
            'drag-source': draggedWorkspace === workspace.key,
            'drag-over-before': dragOverKey === workspace.key && !dragOverAfter,
            'drag-over-after': dragOverKey === workspace.key && dragOverAfter,
          }"
          draggable="true" @dragstart.stop="startDrag($event, workspace.key)" @dragend="draggedWorkspace = null; dragOverKey = null"
          @dragover.prevent="dragOverWorkspace($event, workspace.key)" @drop.stop="dropWorkspace($event, workspace.key)"
          @contextmenu.prevent="openMenuAtCursor($event, workspace.project ? 'project' : 'free', workspace.key, workspace.name)" @click="handleProjectClick(workspace.key)">
          <button class="project-row-main" :title="workspace.project?.path ?? workspace.name" :aria-expanded="isExpanded(workspace.key)" :aria-controls="`workspace-sessions-${workspace.key}`" :aria-keyshortcuts="'Alt+ArrowUp Alt+ArrowDown'" @keydown="reorderWithKeyboard($event, workspace.key)" @click.stop="handleProjectClick(workspace.key)">
            <component :is="workspace.project ? workspaceIcons[workspace.project.icon ?? 'folder'] ?? FolderOpen : Sparkles" :size="15" class="sidebar-list-item-icon sidebar-icon" :class="`workspace-color-${workspace.project?.color ?? 'default'}`" />
            <span class="sidebar-list-item-text sidebar-label">{{ workspace.name }}</span>
          </button>
          <button v-if="workspace.project" class="project-row-action sidebar-extra" :aria-label="`${workspace.name} · ${t('sidebar.moreActions')}`" :title="t('sidebar.moreActions')" @mousedown.stop @click.stop="openMenuAtButton($event, 'project', workspace.key, workspace.name)"><MoreHorizontal :size="14" /></button>
          <button class="project-row-action sidebar-extra" :disabled="busy || workspace.project?.availability === 'error'" :aria-label="`${workspace.name} · ${t('sidebar.newChat')}`" :title="t('sidebar.newChat')" @mousedown.stop @click.stop="emit('create-session', workspace.project ? workspace.key : null)"><Plus :size="14" /></button>
        </div>
        <div v-if="isExpanded(workspace.key)" :id="`workspace-sessions-${workspace.key}`" class="project-sessions sidebar-extra">
          <div v-if="workspaceLoadStates?.[workspace.key]?.status === 'loading'" class="session-empty" role="status">{{ t('common.loading') }}</div>
          <div v-else-if="workspaceLoadStates?.[workspace.key]?.status === 'error' || workspace.project?.availability === 'error'" class="workspace-load-error" role="status">
            <span :title="workspaceLoadStates?.[workspace.key]?.message ?? errorCodeMessage(workspace.project?.availability_code, undefined, { t, te }) ?? workspace.project?.availability_error">{{ t('home.loadFailed') }}</span><button @click="emit('retry-workspaces', workspace.key)">{{ t('common.retry') }}</button>
          </div>
          <div v-for="session in shownSessions(workspace.key)" :key="selectionKey(session)" class="session-row" :class="{ active: isActiveSession(session) }" @contextmenu.prevent="openMenuAtCursor($event, 'session', selectionKey(session), session.title)">
            <button class="session-item" :aria-current="isActiveSession(session) ? 'page' : undefined" :title="session.title" @click="handleSessionClick(selectionKey(session))" @dblclick.stop="renaming = { kind: 'session', id: selectionKey(session) }">
              <InlineRenameInput v-if="renaming?.kind === 'session' && renaming.id === selectionKey(session)" :model-value="session.title" class="session-title" @submit="submitRename" @cancel="cancelRename" /><span v-else class="session-title">{{ session.title }}</span>
            </button>
            <button class="session-more" :title="t('sidebar.moreActions')" @click.stop="openMenuAtButton($event, 'session', selectionKey(session), session.title)"><MoreHorizontal :size="13" /></button>
          </div>
          <div v-if="!allSessions(workspace.key).length && (!workspaceLoadStates?.[workspace.key] || workspaceLoadStates[workspace.key]?.status === 'ready') && workspace.project?.availability !== 'error'" class="session-empty">{{ t('sidebar.noSessions') }}</div>
          <button v-if="allSessions(workspace.key).length > shownSessions(workspace.key).length || list.state.value.allKeys.includes(workspace.key)" class="workspace-show-more" @click="list.toggle(workspace.key, 'allKeys')">{{ list.state.value.allKeys.includes(workspace.key) ? t('sidebar.showFewer') : t('sidebar.showAll') }}</button>
        </div>
      </div></template>
    </div>

    <div v-if="tokenUsage.length > 0" class="token-usage-area">
      <div class="token-usage-chart">
        <div
          v-for="(height, index) in tokenUsage"
          :key="index"
          class="token-usage-bar"
          :style="{ height: `${height}%` }"
          :class="{
            'low': height < 40,
            'medium': height >= 40 && height < 70,
            'high': height >= 70
          }"
        />
      </div>
    </div>

    <div class="sidebar-footer">
      <div class="sidebar-footer-actions" :class="{ 'sidebar-footer-actions-collapsed': collapsed }">
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
.sidebar-migration-dialog { width: min(560px, calc(100vw - 32px)); border: 1px solid var(--border-muted); border-radius: 12px; padding: 24px; background: var(--surface-raised); color: var(--text-primary); box-shadow: var(--shadow-card-subtle); }
.sidebar-migration-dialog::backdrop { background: color-mix(in srgb, var(--bg-overlay) 65%, transparent); }
.sidebar-migration-dialog form, .sidebar-migration-dialog label { display: grid; gap: 12px; }
.sidebar-migration-dialog form > div { display: flex; justify-content: flex-end; gap: 12px; }
.sidebar-migration-dialog h2 { font-size: 18px; margin: 0; }
.sidebar-migration-dialog select:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 2px; }
</style>
