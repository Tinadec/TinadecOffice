<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  Archive,
  Bug,
  ChevronRight,
  FolderOpen,
  LayoutGrid,
  MessageSquare,
  MoreHorizontal,
  PanelLeftClose,
  PanelLeftOpen,
  Pencil,
  Plus,
  Settings,
  Store,
  Terminal,
  Trash2,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { ProjectDto, SessionDto } from '../api'
import BrandLogo from '@/components/BrandLogo.vue'
import TinadecCalligraphy from '@/components/TinadecCalligraphy.vue'
import InlineRenameInput from '@/components/InlineRenameInput.vue'
import RowContextMenu, { type RowMenuItem } from '@/components/RowContextMenu.vue'
import { UiButton, UiDropdownMenu } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'

const { t } = useI18n()
const { confirm } = useNotifications()

const props = defineProps<{
  projects: ProjectDto[]
  sessions: SessionDto[]
  selectedProjectId: string | null
  selectedSessionId: string | null
  busy: boolean
  collapsed?: boolean
  panelStyle?: Record<string, string>
  panelDataAttrs?: Record<string, string>
}>()

const emit = defineEmits<{
  'select-project': [id: string]
  'select-session': [id: string]
  'create-session': [projectId: string]
  'open-project': []
  'go-settings': []
  'go-market': []
  'toggle-collapse': []
  'rename-project': [id: string, name: string]
  'rename-session': [id: string, title: string]
  'archive-project': [id: string]
  'archive-session': [id: string]
  'trash-project': [id: string]
  'trash-session': [id: string]
}>()

const searchQuery = ref('')
const expandedProjects = ref<Set<string>>(new Set())

// ---- Lifecycle management (context menu + inline rename) ----
interface MenuTarget {
  kind: 'project' | 'session'
  id: string
  name: string
  x: number
  y: number
}

const menuTarget = ref<MenuTarget | null>(null)
const renaming = ref<{ kind: 'project' | 'session'; id: string } | null>(null)

const menuItems = computed<RowMenuItem[]>(() => [
  { key: 'rename', label: t('sidebar.rename'), icon: Pencil },
  { key: 'archive', label: t('sidebar.archive'), icon: Archive },
  { key: 'trash', label: t('sidebar.moveToTrash'), icon: Trash2, danger: true },
])

function openMenuAtCursor(event: MouseEvent, kind: 'project' | 'session', id: string, name: string) {
  menuTarget.value = { kind, id, name, x: event.clientX, y: event.clientY }
}

function openMenuAtButton(event: MouseEvent, kind: 'project' | 'session', id: string, name: string) {
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
  menuTarget.value = { kind, id, name, x: rect.left, y: rect.bottom + 4 }
}

function startRename(target: MenuTarget) {
  renaming.value = { kind: target.kind, id: target.id }
  if (target.kind === 'project' && !expandedProjects.value.has(target.id)) {
    const next = new Set(expandedProjects.value)
    next.add(target.id)
    expandedProjects.value = next
  }
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
  if (key === 'rename') {
    startRename(target)
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

const filteredProjects = computed(() => {
  if (!searchQuery.value.trim()) return props.projects
  const q = searchQuery.value.toLowerCase()
  return props.projects.filter((project) =>
    project.name.toLowerCase().includes(q)
  )
})

function getProjectSessions(projectId: string): SessionDto[] {
  return props.sessions.filter(
    (s) => s.project_id === projectId && s.title && s.title !== 'Tinadec session'
  )
}

function isExpanded(projectId: string): boolean {
  return expandedProjects.value.has(projectId)
}

function toggleExpand(projectId: string) {
  const next = new Set(expandedProjects.value)
  if (next.has(projectId)) {
    next.delete(projectId)
  } else {
    next.add(projectId)
  }
  expandedProjects.value = next
}

function handleProjectClick(projectId: string) {
  toggleExpand(projectId)
  emit('select-project', projectId)
}

function handleSessionClick(sessionId: string) {
  emit('select-session', sessionId)
}

function handleNewSession(projectId: string) {
  emit('create-session', projectId)
}

function handleNewThread() {
  if (props.selectedProjectId) {
    emit('create-session', props.selectedProjectId)
  } else if (props.projects.length > 0) {
    emit('create-session', props.projects[0].id)
  }
}

const tokenUsage = ref<number[]>([])

// ---- Mode switch (placeholder, no actual functionality) ----
const modeMenuOpen = ref(false)
const selectedMode = ref<'im' | 'hub'>('im')

function selectMode(mode: 'im' | 'hub') {
  selectedMode.value = mode
  modeMenuOpen.value = false
}

function openDebugStudio() {
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
        :disabled="busy || projects.length === 0"
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
        :title="t('sidebar.commandCenter')"
        disabled
      >
        <Terminal :size="16" class="sidebar-icon" />
        <span class="sidebar-label">{{ t('sidebar.commandCenter') }}</span>
      </UiButton>
      <UiButton
        variant="ghost"
        size="sm"
        class="sidebar-nav-item w-full justify-start"
        title="Debug Studio"
        @click="openDebugStudio()"
      >
        <Bug :size="16" class="sidebar-icon" />
        <span class="sidebar-label">Debug Studio</span>
      </UiButton>
    </nav>

    <div class="sidebar-list">
      <div
        v-for="project in filteredProjects"
        :key="project.id"
        class="project-group"
      >
        <div
          class="project-row"
          @contextmenu.prevent="openMenuAtCursor($event, 'project', project.id, project.name)"
        >
          <button
            class="project-row-main"
            :title="project.name"
            @click="handleProjectClick(project.id)"
            @dblclick.stop="renaming = { kind: 'project', id: project.id }"
          >
            <ChevronRight
              :size="14"
              class="project-chevron sidebar-extra"
              :class="{ expanded: isExpanded(project.id) }"
            />
            <FolderOpen :size="14" class="sidebar-list-item-icon sidebar-icon" />
            <InlineRenameInput
              v-if="renaming?.kind === 'project' && renaming.id === project.id"
              :model-value="project.name"
              class="sidebar-list-item-text"
              @submit="submitRename"
              @cancel="cancelRename"
            />
            <span v-else class="sidebar-list-item-text sidebar-label">{{ project.name }}</span>
          </button>
          <button
            class="project-row-action sidebar-extra"
            :title="t('sidebar.moreActions')"
            @click.stop="openMenuAtButton($event, 'project', project.id, project.name)"
          >
            <MoreHorizontal :size="14" />
          </button>
          <button
            class="project-row-action sidebar-extra"
            :title="t('sidebar.newChat')"
            @click.stop="handleNewSession(project.id)"
          >
            <Plus :size="14" />
          </button>
        </div>

        <div v-if="isExpanded(project.id)" class="project-sessions sidebar-extra">
          <div
            v-for="session in getProjectSessions(project.id)"
            :key="session.id"
            class="session-row"
            @contextmenu.prevent="openMenuAtCursor($event, 'session', session.id, session.title)"
          >
            <button
              class="session-item"
              :class="{ active: session.id === selectedSessionId }"
              @click="handleSessionClick(session.id)"
              @dblclick.stop="renaming = { kind: 'session', id: session.id }"
            >
              <span class="session-dot" :class="session.status" />
              <InlineRenameInput
                v-if="renaming?.kind === 'session' && renaming.id === session.id"
                :model-value="session.title"
                class="session-title"
                @submit="submitRename"
                @cancel="cancelRename"
              />
              <span v-else class="session-title">{{ session.title }}</span>
            </button>
            <button
              class="session-more"
              :title="t('sidebar.moreActions')"
              @click.stop="openMenuAtButton($event, 'session', session.id, session.title)"
            >
              <MoreHorizontal :size="13" />
            </button>
          </div>
          <div v-if="getProjectSessions(project.id).length === 0" class="session-empty">
            {{ t('sidebar.noSessions') }}
          </div>
        </div>
      </div>

      <div v-if="filteredProjects.length === 0" class="sidebar-empty">
        <span class="sidebar-label">{{ t('sidebar.noResults') }}</span>
      </div>
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
          :title="t('sidebar.settings')"
          @click="emit('go-settings')"
        >
          <Settings :size="16" />
        </UiButton>
        <UiDropdownMenu v-model:open="modeMenuOpen" placement="top" class="mode-dropdown-menu">
          <template #trigger>
            <UiButton
              variant="ghost"
              size="icon"
              class="sidebar-footer-action"
              title="Mode"
            >
              <LayoutGrid :size="16" />
            </UiButton>
          </template>
          <button
            class="mode-menu-item"
            :class="{ active: selectedMode === 'im' }"
            @click="selectMode('im')"
          >
            <span>会话模式</span>
          </button>
          <button
            class="mode-menu-item"
            :class="{ active: selectedMode === 'hub' }"
            @click="selectMode('hub')"
          >
            <span>空间模式</span>
          </button>
        </UiDropdownMenu>
        <UiButton
          variant="ghost"
          size="icon"
          class="sidebar-footer-action"
          :title="collapsed ? '展开侧边栏' : '折叠侧边栏'"
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
  </aside>
</template>
