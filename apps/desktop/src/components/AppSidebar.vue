<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  Bug,
  ChevronRight,
  FolderOpen,
  LayoutGrid,
  MessageSquare,
  PanelLeftClose,
  PanelLeftOpen,
  Plus,
  Settings,
  Store,
  Terminal,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { ProjectDto, SessionDto } from '../api'
import BrandLogo from '@/components/BrandLogo.vue'
import TinadecCalligraphy from '@/components/TinadecCalligraphy.vue'
import { UiButton, UiDropdownMenu } from '@/components/ui'

const { t } = useI18n()

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
}>()

const searchQuery = ref('')
const expandedProjects = ref<Set<string>>(new Set())

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
        <BrandLogo :size="14" />
        <TinadecCalligraphy v-if="!collapsed" :size="14" />
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
        <MessageSquare :size="16" />
        <span v-if="!collapsed">{{ t('sidebar.newChat') }}</span>
      </UiButton>
      <UiButton
        variant="ghost"
        size="sm"
        class="sidebar-nav-item w-full justify-start"
        :title="t('sidebar.market')"
        @click="emit('go-market')"
      >
        <Store :size="16" />
        <span v-if="!collapsed">{{ t('sidebar.market') }}</span>
      </UiButton>
      <UiButton
        variant="ghost"
        size="sm"
        class="sidebar-nav-item w-full justify-start"
        :title="t('sidebar.commandCenter')"
        disabled
      >
        <Terminal :size="16" />
        <span v-if="!collapsed">{{ t('sidebar.commandCenter') }}</span>
      </UiButton>
      <UiButton
        variant="ghost"
        size="sm"
        class="sidebar-nav-item w-full justify-start"
        title="Debug Studio"
        @click="openDebugStudio()"
      >
        <Bug :size="16" />
        <span v-if="!collapsed">Debug Studio</span>
      </UiButton>
    </nav>

    <div class="sidebar-list">
      <div
        v-for="project in filteredProjects"
        :key="project.id"
        class="project-group"
      >
        <div class="project-row">
          <button
            class="project-row-main"
            :title="project.name"
            @click="handleProjectClick(project.id)"
          >
            <ChevronRight
              v-if="!collapsed"
              :size="14"
              class="project-chevron"
              :class="{ expanded: isExpanded(project.id) }"
            />
            <FolderOpen :size="14" class="sidebar-list-item-icon" />
            <span v-if="!collapsed" class="sidebar-list-item-text">{{ project.name }}</span>
          </button>
          <button
            v-if="!collapsed"
            class="project-row-action"
            :title="t('sidebar.newChat')"
            @click.stop="handleNewSession(project.id)"
          >
            <Plus :size="14" />
          </button>
        </div>

        <div v-if="!collapsed && isExpanded(project.id)" class="project-sessions">
          <button
            v-for="session in getProjectSessions(project.id)"
            :key="session.id"
            class="session-item"
            :class="{ active: session.id === selectedSessionId }"
            @click="handleSessionClick(session.id)"
          >
            <span class="session-dot" :class="session.status" />
            <span class="session-title">{{ session.title }}</span>
          </button>
          <div v-if="getProjectSessions(project.id).length === 0" class="session-empty">
            {{ t('sidebar.noSessions') }}
          </div>
        </div>
      </div>

      <div v-if="filteredProjects.length === 0" class="sidebar-empty">
        {{ collapsed ? '...' : t('sidebar.noResults') }}
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
  </aside>
</template>
