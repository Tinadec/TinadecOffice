<script setup lang="ts">
import { GitCommitHorizontal, History, Search, UserCheck } from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ApprovalDto } from '@/api'
import SearchPanel from '@/components/code/SearchPanel.vue'
import DocumentEditorPane from '@/components/document/DocumentEditorPane.vue'
import { homeController } from '@/controllers/HomeController'
import { documentBaseName, documentParentDir } from '@/lib/documentPath'
import {
  closeEditor,
  editorFocus,
  editorPath,
  openInEditor,
  syncDocumentWorkspace,
} from '@/lib/documentWorkspace'
import { focusDockCard } from '@/lib/homeDockSync'
import { consumeRequest, pendingWorkspaceFile } from '@/lib/pageRequests'
import { selectionKey } from '@/lib/storageScope'
import { listWorkspaceDir } from '@/lib/workspaceFiles'

/**
 * The `codeEditor` card: the workspace folder tree, workspace grep search, plus the real
 * editor inside Home's dock or a detached `/panel` window.
 */
const { t, te } = useI18n()
const lbl = (key: string, fallback: string) => (typeof te === 'function' && te(key) ? t(key) : fallback)
const controller = homeController

const showSearch = ref(false)
const workspaceRoot = computed(() => controller.currentProject.value?.path ?? '')
const revealLine = computed(() => (
  editorFocus.value && editorFocus.value.path === editorPath.value ? editorFocus.value.line : undefined
))
const revealNonce = computed(() => (
  editorFocus.value && editorFocus.value.path === editorPath.value ? editorFocus.value.nonce : 0
))
const searchToggleLabel = computed(() =>
  showSearch.value
    ? (te('code.hideSearch') ? t('code.hideSearch') : t('context.homeCodeEditor'))
    : (te('code.showSearch') ? t('code.showSearch') : t('context.homeCodeEditor')),
)

/** Restore this workspace's own editor state; a fresh window has none bound yet. */
onMounted(() => { syncDocumentWorkspace(workspaceRoot.value) })

watch(workspaceRoot, (root) => { syncDocumentWorkspace(root) })

watch([workspaceRoot, editorPath], async ([root, path]) => {
  if (!root || !path) return
  const entries = await listWorkspaceDir(root, documentParentDir(path))
  if (!entries) return
  if (editorPath.value !== path) return
  const name = documentBaseName(path)
  if (!entries.some((entry) => !entry.isDir && entry.name === name)) closeEditor()
}, { immediate: true })

function select(path: string): void {
  showSearch.value = false
  openInEditor({ path, cwd: workspaceRoot.value })
}

consumeRequest(pendingWorkspaceFile, (file) => {
  showSearch.value = false
  if (file.projectId && file.projectId !== controller.selectedProjectId?.value && typeof controller.setSelectedProject === 'function') {
    void controller.setSelectedProject(file.projectId)
  }
  const matchedProject = file.projectId
    ? controller.projects?.value?.find((p) => selectionKey(p) === file.projectId)
    : controller.currentProject?.value
  const cwd = matchedProject?.path ?? workspaceRoot.value
  if (cwd) {
    openInEditor({ path: file.path, cwd })
  }
})

function recordApproval(approval: ApprovalDto): void {
  controller.recordApproval(approval)
}

function openActiveFileGitSubView(gitSubView: 'diff' | 'file_history' | 'blame'): void {
  if (!editorPath.value) return
  focusDockCard({
    card: 'git',
    reason: 'code',
    filePath: editorPath.value,
    gitTab: 'changes',
    gitSubView,
  })
}
</script>

<template>
  <div class="doc-card code-editor-card-shell" role="region" :aria-label="t('context.homeCodeEditor')">
    <div v-if="workspaceRoot" class="code-editor-card-toolbar">
      <div v-if="editorPath" class="code-editor-card-bridge">
        <span class="code-editor-card-breadcrumb" data-testid="code-card-breadcrumb" :title="editorPath">
          {{ editorPath }}
        </span>
        <div class="code-editor-card-git-actions">
          <button
            type="button"
            class="code-editor-card-bridge-btn"
            data-testid="code-git-diff-btn"
            :title="lbl('context.gitDiffShortcut', 'Diff')"
            @click="openActiveFileGitSubView('diff')"
          >
            <GitCommitHorizontal :size="12" aria-hidden="true" />
            <span>{{ lbl('context.gitDiffShortcut', 'Diff') }}</span>
          </button>
          <button
            type="button"
            class="code-editor-card-bridge-btn"
            data-testid="code-git-history-btn"
            :title="lbl('context.gitShowHistory', 'History')"
            @click="openActiveFileGitSubView('file_history')"
          >
            <History :size="12" aria-hidden="true" />
            <span>{{ lbl('context.gitShowHistory', 'History') }}</span>
          </button>
          <button
            type="button"
            class="code-editor-card-bridge-btn"
            data-testid="code-git-blame-btn"
            :title="lbl('context.gitAnnotateBlame', 'Blame')"
            @click="openActiveFileGitSubView('blame')"
          >
            <UserCheck :size="12" aria-hidden="true" />
            <span>{{ lbl('context.gitAnnotateBlame', 'Blame') }}</span>
          </button>
        </div>
      </div>
      <button
        type="button"
        class="code-editor-card-search-btn"
        :class="{ active: showSearch }"
        :title="searchToggleLabel"
        :aria-pressed="showSearch"
        data-testid="code-card-search-toggle"
        @click="showSearch = !showSearch"
      >
        <Search :size="13" aria-hidden="true" />
        <span>{{ searchToggleLabel }}</span>
      </button>
    </div>

    <div v-if="showSearch && workspaceRoot" class="code-editor-card-search">
      <SearchPanel :cwd="workspaceRoot" @select="select" />
    </div>

    <DocumentEditorPane
      v-else
      :path="editorPath"
      :cwd="workspaceRoot"
      :line="revealLine"
      :reveal-nonce="revealNonce"
      :approvals="controller.approvals.value"
      :selected-session-id="controller.selectedSessionId.value"
      @close="closeEditor()"
      @select="select"
      @approval-requested="recordApproval"
    />
  </div>
</template>

<style scoped>
.code-editor-card-shell {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-width: 0;
  min-height: 0;
}

.code-editor-card-toolbar {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding: 4px 8px;
  border-bottom: 1px solid var(--border-muted);
  flex-shrink: 0;
}

.code-editor-card-bridge {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 6px;
}

.code-editor-card-breadcrumb {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11px;
  font-family: var(--font-mono);
  color: var(--text-secondary);
}

.code-editor-card-git-actions {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.code-editor-card-bridge-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 10px;
  font-weight: 600;
  cursor: pointer;
}

.code-editor-card-bridge-btn:hover {
  color: var(--text-primary);
  border-color: var(--accent-primary);
  background: var(--surface-hover);
}

.code-editor-card-search-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 3px 8px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
}

.code-editor-card-search-btn:hover,
.code-editor-card-search-btn.active {
  color: var(--text-primary);
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 10%, var(--surface-raised));
}

.code-editor-card-search {
  flex: 1;
  min-height: 0;
  overflow: hidden;
  padding: 6px;
}
</style>
