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
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import AppHeader from '@/components/AppHeader.vue'
import FileTreePanel from '@/components/code/FileTreePanel.vue'
import SearchPanel from '@/components/code/SearchPanel.vue'
import CodeViewer from '@/components/code/CodeViewer.vue'
import CodeEditor from '@/components/code/CodeEditor.vue'
import PatchPreview from '@/components/code/PatchPreview.vue'
import { UiButton, UiSelect } from '@/components/ui'
import { codeController } from '@/controllers/CodeController'

const router = useRouter()
const c = codeController
const activeTab = computed(() => c.activeTab.value)

onMounted(() => {
  c.start()
})
</script>

<template>
<main class="shell">
<div class="top-drag-bar" />
<AppHeader :busy="c.busy.value" />

    <section class="code-workspace">
      <!-- Top toolbar -->
      <div class="code-toolbar">
        <UiButton variant="ghost" size="icon" class="h-8 w-8" title="Back" @click="router.push('/')">
          <ArrowLeft :size="16" />
        </UiButton>

        <div class="code-toolbar-project">
          <UiSelect
            :model-value="c.selectedProjectId.value ?? ''"
            placeholder="Select project..."
            class="h-8 w-64"
            @update:model-value="c.setProject($event as string)"
          >
            <template #default="{ select, selectedValue }">
              <button
                v-for="project in c.projects.value"
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
          <UiButton variant="ghost" size="sm" class="h-8" title="New file" @click="c.handleNewFile()">
            <FilePlus :size="14" />
            <span>New</span>
          </UiButton>
          <UiButton variant="ghost" size="sm" class="h-8" title="Refresh" @click="c.handleRefresh()">
            <RefreshCw :size="14" />
          </UiButton>
          <UiButton
            variant="ghost"
            size="icon"
            class="h-8 w-8"
            :title="c.showSearchPanel.value ? 'Hide search' : 'Show search'"
            @click="c.showSearchPanel.value = !c.showSearchPanel.value"
          >
            <Search :size="15" />
          </UiButton>
          <UiButton
            variant="ghost"
            size="icon"
            class="h-8 w-8"
            :title="c.showPatchPanel.value ? 'Hide patch' : 'Show patch'"
            @click="c.showPatchPanel.value = !c.showPatchPanel.value"
          >
            <GitCompare :size="15" />
          </UiButton>
        </div>
      </div>

      <!-- Main content area -->
      <div class="code-content">
        <!-- Left panel: file tree + search -->
        <aside v-if="c.showSearchPanel.value" class="code-left-panel">
          <div class="code-left-section">
            <FileTreePanel
              :cwd="c.currentProjectPath.value"
              :approvals="c.approvals.value"
              :selected-session-id="c.selectedSessionId.value"
              @select="c.handleFileSelect($event)"
              @approval-created="c.handleApprovalCreated($event)"
            />
          </div>
          <div class="code-left-section code-left-search">
            <SearchPanel
              :cwd="c.currentProjectPath.value"
              @select="c.handleFileSelect($event)"
            />
          </div>
        </aside>

        <!-- Center panel: editor tabs -->
        <main class="code-center-panel">
          <!-- Tab bar -->
          <div v-if="c.openTabs.value.length > 0" class="code-tab-bar">
            <button
              v-for="tab in c.openTabs.value"
              :key="tab.path"
              class="code-tab"
              :class="{ active: c.activeTabPath.value === tab.path }"
              @click="c.handleSwitchTab(tab.path)"
            >
              <span class="code-tab-name">{{ tab.path.split(/[\\/]/).pop() }}</span>
              <span class="code-tab-mode">{{ tab.mode }}</span>
              <span class="code-tab-close" @click.stop="c.handleCloseTab(tab.path)">
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
                :cwd="c.currentProjectPath.value"
                :file-path="activeTab.path"
                @edit="c.handleEditFile(activeTab.path, $event)"
              />
              <CodeEditor
                v-else
                :key="`editor-${activeTab.path}`"
                :cwd="c.currentProjectPath.value"
                :file-path="activeTab.path"
                :initial-content="activeTab.content"
                :selected-session-id="c.selectedSessionId.value"
                :approvals="c.approvals.value"
                @approval-requested="c.handleApprovalCreated($event)"
                @saved="c.handleSwitchToView(activeTab.path)"
                @cancel="c.handleSwitchToView(activeTab.path)"
              />
            </template>
          </div>
        </main>

        <!-- Right panel: patch preview (optional) -->
        <aside v-if="c.showPatchPanel.value" class="code-right-panel">
          <PatchPreview
            v-if="c.patchFilePath.value"
            :cwd="c.currentProjectPath.value"
            :file-path="c.patchFilePath.value"
            :original-content="c.patchOriginal.value"
            :modified-content="c.patchModified.value"
            :selected-session-id="c.selectedSessionId.value"
            :approvals="c.approvals.value"
            @approval-requested="c.handleApprovalCreated($event)"
            @cancel="c.showPatchPanel.value = false"
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
