<script setup lang="ts">
import {
  ArrowLeft,
  FilePlus,
  GitCompare,
  PanelRightClose,
  PanelRightOpen,
  RefreshCw,
  Search,
  X,
} from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api, type ApprovalDto, type ProjectDto } from '@/api'
import AppHeader from '@/components/AppHeader.vue'
import FileTreePanel from '@/components/code/FileTreePanel.vue'
import SearchPanel from '@/components/code/SearchPanel.vue'
import CodeViewer from '@/components/code/CodeViewer.vue'
import CodeEditor from '@/components/code/CodeEditor.vue'
import PatchPreview from '@/components/code/PatchPreview.vue'
import { UiButton, UiSelect } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'

interface OpenTab {
  path: string
  mode: 'view' | 'edit'
  content?: string
}

const router = useRouter()
const { notify, banner } = useNotifications()

const projects = ref<ProjectDto[]>([])
const selectedProjectId = ref<string | null>(null)
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
  projects.value.find((p) => p.id === selectedProjectId.value) ?? null,
)
const currentProjectPath = computed(() => currentProject.value?.path ?? '')
const activeTab = computed(() =>
  openTabs.value.find((t) => t.path === activeTabPath.value) ?? null,
)

async function loadProjects(): Promise<void> {
  busy.value = true
  try {
    projects.value = await api.listProjects()
    if (!selectedProjectId.value && projects.value.length > 0) {
      selectedProjectId.value = projects.value[0].id
    }
    await loadSession()
  } catch (err) {
    banner.error({
      key: 'code-load',
      title: 'Failed to load projects',
      message: err instanceof Error ? err.message : 'Failed to load projects',
      action: { label: 'Retry', run: () => loadProjects() },
    })
  } finally {
    busy.value = false
  }
}

async function loadSession(): Promise<void> {
  if (!selectedSessionId.value && currentProject.value) {
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
    const list = await api.listApprovals(selectedSessionId.value ?? undefined)
    approvals.value = list
  } catch {
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
    notify.error(err, { title: 'Failed to decide approval' })
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
    title: 'New file',
    message: 'New file creation requires an approval flow. Use the chat panel to request file creation.',
  })
}

function handleRefresh(): void {
  void loadApprovals()
}

watch(selectedProjectId, () => {
  openTabs.value = []
  activeTabPath.value = null
  void loadSession()
})

onMounted(() => {
  void loadProjects()
})
</script>

<template>
<main class="shell">
<!-- Full-width draggable bar for window dragging -->
<div class="top-drag-bar" />
<AppHeader :busy="busy" />

    <section class="code-workspace">
      <!-- Top toolbar -->
      <div class="code-toolbar">
        <UiButton variant="ghost" size="icon" class="h-8 w-8" title="Back" @click="router.push('/')">
          <ArrowLeft :size="16" />
        </UiButton>

        <div class="code-toolbar-project">
          <UiSelect
            :model-value="selectedProjectId ?? ''"
            placeholder="Select project..."
            class="h-8 w-64"
            @update:model-value="selectedProjectId = $event"
          >
            <template #default="{ select, selectedValue }">
              <button
                v-for="project in projects"
                :key="project.id"
                class="flex w-full items-center gap-2 px-3 py-2 text-left text-sm hover:bg-accent"
                :class="{ 'bg-accent': selectedValue === project.id }"
                @click="select(project.id)"
              >
                <span>{{ project.name }}</span>
                <span class="ml-auto text-xs text-muted-foreground">{{ project.path }}</span>
              </button>
            </template>
          </UiSelect>
        </div>

        <div class="code-toolbar-actions">
          <UiButton variant="ghost" size="sm" class="h-8" title="New file" @click="handleNewFile">
            <FilePlus :size="14" />
            <span>New</span>
          </UiButton>
          <UiButton variant="ghost" size="sm" class="h-8" title="Refresh" @click="handleRefresh">
            <RefreshCw :size="14" />
          </UiButton>
          <UiButton
            variant="ghost"
            size="icon"
            class="h-8 w-8"
            :title="showSearchPanel ? 'Hide search' : 'Show search'"
            @click="showSearchPanel = !showSearchPanel"
          >
            <Search :size="15" />
          </UiButton>
          <UiButton
            variant="ghost"
            size="icon"
            class="h-8 w-8"
            :title="showPatchPanel ? 'Hide patch' : 'Show patch'"
            @click="showPatchPanel = !showPatchPanel"
          >
            <GitCompare :size="15" />
          </UiButton>
        </div>
      </div>

      <!-- Main content area -->
      <div class="code-content">
        <!-- Left panel: file tree + search -->
        <aside v-if="showSearchPanel" class="code-left-panel">
          <div class="code-left-section">
            <FileTreePanel
              :cwd="currentProjectPath"
              :approvals="approvals"
              :selected-session-id="selectedSessionId"
              @select="handleFileSelect"
              @approval-created="handleApprovalCreated"
            />
          </div>
          <div class="code-left-section code-left-search">
            <SearchPanel
              :cwd="currentProjectPath"
              @select="handleFileSelect"
            />
          </div>
        </aside>

        <!-- Center panel: editor tabs -->
        <main class="code-center-panel">
          <!-- Tab bar -->
          <div v-if="openTabs.length > 0" class="code-tab-bar">
            <button
              v-for="tab in openTabs"
              :key="tab.path"
              class="code-tab"
              :class="{ active: activeTabPath === tab.path }"
              @click="handleSwitchTab(tab.path)"
            >
              <span class="code-tab-name">{{ tab.path.split(/[\\/]/).pop() }}</span>
              <span class="code-tab-mode">{{ tab.mode }}</span>
              <span class="code-tab-close" @click.stop="handleCloseTab(tab.path)">
                <X :size="11" />
              </span>
            </button>
          </div>

          <!-- Editor content -->
          <div class="code-editor-area">
            <div v-if="!activeTab" class="code-empty">
              <p>Select a file from the file tree to start editing.</p>
            </div>
            <template v-else>
              <CodeViewer
                v-if="activeTab.mode === 'view'"
                :key="`viewer-${activeTab.path}`"
                :cwd="currentProjectPath"
                :file-path="activeTab.path"
                @edit="handleEditFile"
              />
              <CodeEditor
                v-else
                :key="`editor-${activeTab.path}`"
                :cwd="currentProjectPath"
                :file-path="activeTab.path"
                :initial-content="activeTab.content"
                :selected-session-id="selectedSessionId"
                :approvals="approvals"
                @approval-requested="handleApprovalCreated"
                @saved="handleSwitchToView"
                @cancel="() => { if (activeTab) handleSwitchToView(activeTab.path) }"
              />
            </template>
          </div>
        </main>

        <!-- Right panel: patch preview (optional) -->
        <aside v-if="showPatchPanel" class="code-right-panel">
          <PatchPreview
            v-if="patchFilePath"
            :cwd="currentProjectPath"
            :file-path="patchFilePath"
            :original-content="patchOriginal"
            :modified-content="patchModified"
            :selected-session-id="selectedSessionId"
            :approvals="approvals"
            @approval-requested="handleApprovalCreated"
            @cancel="showPatchPanel = false"
          />
          <div v-else class="code-right-empty">
            <GitCompare :size="24" class="text-muted-foreground" />
            <p>No patch to preview.</p>
          </div>
        </aside>
      </div>
    </section>
  </main>
</template>

<style scoped>
.code-workspace {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
}
.code-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--border-default);
  background: var(--bg-secondary);
}
.code-toolbar-project {
  flex: 1;
}
.code-toolbar-actions {
  display: flex;
  align-items: center;
  gap: 4px;
}
.code-content {
  display: flex;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}
.code-left-panel {
  display: flex;
  flex-direction: column;
  width: 280px;
  min-width: 200px;
  border-right: 1px solid var(--border-default);
  background: var(--bg-secondary);
}
.code-left-section {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.code-left-search {
  border-top: 1px solid var(--border-default);
  max-height: 40%;
}
.code-center-panel {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  background: var(--bg-primary);
}
.code-tab-bar {
  display: flex;
  align-items: center;
  gap: 1px;
  padding: 0 4px;
  border-bottom: 1px solid var(--border-default);
  background: var(--bg-secondary);
  overflow-x: auto;
  flex-shrink: 0;
}
.code-tab {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 6px 10px;
  font-size: 12px;
  color: var(--text-secondary);
  background: transparent;
  border: 0;
  border-right: 1px solid var(--border-muted);
  cursor: pointer;
  white-space: nowrap;
}
.code-tab:hover {
  background: var(--bg-hover);
}
.code-tab.active {
  color: var(--text-primary);
  background: var(--bg-primary);
  border-bottom: 2px solid var(--accent-primary);
}
.code-tab-name {
  font-weight: 500;
}
.code-tab-mode {
  font-size: 10px;
  color: var(--text-muted);
  text-transform: uppercase;
}
.code-tab-close {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 3px;
  color: var(--text-muted);
}
.code-tab-close:hover {
  background: var(--bg-tertiary);
  color: var(--text-primary);
}
.code-editor-area {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
.code-empty {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100%;
  color: var(--text-muted);
  font-size: 14px;
}
.code-right-panel {
  width: 480px;
  min-width: 300px;
  border-left: 1px solid var(--border-default);
  background: var(--bg-primary);
  display: flex;
  flex-direction: column;
}
.code-right-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  height: 100%;
  color: var(--text-muted);
  font-size: 13px;
}
</style>
