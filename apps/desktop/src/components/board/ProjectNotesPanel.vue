<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertTriangle,
  Columns2,
  Eye,
  FileText,
  PencilLine,
  Plus,
  RefreshCw,
  Save,
} from '@lucide/vue'
import MarkdownRender from '@/components/MarkdownRender.vue'
import { UiButton } from '@/components/ui'
import {
  api,
  type ProjectBoardStorageMode,
  type ProjectNotesDto,
} from '@/api'
import { ApiError } from '@/lib/apiError'
import { useHostAccess } from '@/lib/hostAccess'

export type NotesViewMode = 'edit' | 'split' | 'preview'

const props = defineProps<{
  projectId: string
  notes?: ProjectNotesDto | null
  fallbackResolvedPath?: string
  fallbackStorageMode?: ProjectBoardStorageMode
}>()

const emit = defineEmits<{
  saved: [notes: ProjectNotesDto]
  reloaded: [notes: ProjectNotesDto]
}>()

const { t } = useI18n()
const { canAccessBackend } = useHostAccess()

const viewMode = ref<NotesViewMode>('split')
const serverContent = ref('')
const draftContent = ref('')
const currentRevision = ref<number>(1)
const resolvedPathFromServer = ref('')
const storageModeFromServer = ref<ProjectBoardStorageMode | null>(null)
const loading = ref(false)
const saving = ref(false)
const hasLoadedOnce = ref(false)
const externalSyncedFlag = ref(false)
const conflictDetected = ref(false)
const pendingExternalNotes = ref<ProjectNotesDto | null>(null)
const errorMessage = ref<string | null>(null)
const editorRef = ref<HTMLTextAreaElement | null>(null)

const isDirty = computed(() => draftContent.value !== serverContent.value)

const effectiveResolvedPath = computed(
  () => resolvedPathFromServer.value || props.notes?.resolved_file_path || props.fallbackResolvedPath || '.tinadec/project/notes.md',
)

const effectiveStorageMode = computed<ProjectBoardStorageMode>(
  () => storageModeFromServer.value || props.notes?.storage_mode || props.fallbackStorageMode || 'repo_tinadec',
)

function applyAuthoritativeNotes(incoming: ProjectNotesDto, isExternalSync = false) {
  serverContent.value = incoming.content_md
  draftContent.value = incoming.content_md
  currentRevision.value = incoming.revision
  resolvedPathFromServer.value = incoming.resolved_file_path
  storageModeFromServer.value = incoming.storage_mode
  hasLoadedOnce.value = true
  conflictDetected.value = false
  pendingExternalNotes.value = null
  errorMessage.value = null
  if (isExternalSync) {
    externalSyncedFlag.value = true
  }
}

watch(
  () => props.notes,
  (incoming, prev) => {
    if (!incoming) return
    if (!hasLoadedOnce.value) {
      applyAuthoritativeNotes(incoming, false)
      return
    }
    const isNewerOrDifferent =
      incoming.revision !== currentRevision.value || incoming.content_md !== serverContent.value
    if (!isNewerOrDifferent) return

    if (!isDirty.value) {
      applyAuthoritativeNotes(incoming, Boolean(prev))
    } else {
      // Non-destructive guard: never overwrite unsaved local edits silently.
      pendingExternalNotes.value = incoming
    }
  },
  { immediate: true },
)

async function loadNotes(markReloaded = false) {
  if (!props.projectId || !canAccessBackend.value) return
  loading.value = true
  errorMessage.value = null
  try {
    const loaded = await api.getProjectNotes(props.projectId)
    applyAuthoritativeNotes(loaded, false)
    externalSyncedFlag.value = false
    if (markReloaded) {
      emit('reloaded', loaded)
    }
  } catch (err) {
    errorMessage.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
  }
}

watch(
  () => props.projectId,
  (nextId, prevId) => {
    if (nextId && nextId !== prevId) {
      hasLoadedOnce.value = false
      externalSyncedFlag.value = false
      conflictDetected.value = false
      pendingExternalNotes.value = null
      if (!props.notes || props.notes.project_id !== nextId) {
        void loadNotes(false)
      }
    }
  },
)

onMounted(() => {
  if (!props.notes && props.projectId) {
    void loadNotes(false)
  }
})

function isRevisionConflictError(err: unknown): boolean {
  if (err instanceof ApiError && (err.status === 409 || err.code === 'revision_conflict')) {
    return true
  }
  if (err && typeof err === 'object') {
    const status = (err as { status?: unknown }).status
    const code = (err as { code?: unknown }).code
    if (status === 409 || code === 'revision_conflict') return true
  }
  return false
}

async function handleSave(forceOverwrite = false) {
  if (!props.projectId || saving.value) return
  saving.value = true
  errorMessage.value = null
  try {
    const saved = await api.updateProjectNotes(props.projectId, {
      content_md: draftContent.value,
      expected_revision: forceOverwrite ? null : currentRevision.value,
    })
    applyAuthoritativeNotes(saved, false)
    externalSyncedFlag.value = false
    emit('saved', saved)
  } catch (err) {
    if (isRevisionConflictError(err)) {
      conflictDetected.value = true
    } else {
      errorMessage.value = err instanceof Error ? err.message : String(err)
    }
  } finally {
    saving.value = false
  }
}

async function handleReload() {
  if (pendingExternalNotes.value && !conflictDetected.value) {
    const incoming = pendingExternalNotes.value
    applyAuthoritativeNotes(incoming, true)
    emit('reloaded', incoming)
    return
  }
  await loadNotes(true)
}

function handleKeydown(event: KeyboardEvent) {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
    event.preventDefault()
    void handleSave(false)
  }
}

async function insertTemplateSection(kind: 'goals' | 'architecture' | 'decisions') {
  const snippetMap: Record<'goals' | 'architecture' | 'decisions', string> = {
    goals: t('board.notes.snippetGoals'),
    architecture: t('board.notes.snippetArchitecture'),
    decisions: t('board.notes.snippetDecisions'),
  }
  const snippet = snippetMap[kind]
  const current = draftContent.value
  if (!current.trim()) {
    draftContent.value = `${snippet}\n`
  } else {
    const separator = current.endsWith('\n\n') ? '' : current.endsWith('\n') ? '\n' : '\n\n'
    draftContent.value = `${current}${separator}${snippet}\n`
  }
  if (viewMode.value === 'preview') {
    viewMode.value = 'split'
  }
  await nextTick()
  const el = editorRef.value
  if (el) {
    el.focus()
    const end = draftContent.value.length
    el.setSelectionRange?.(end, end)
  }
}

defineExpose({
  applyExternalNotesUpdate(incoming: ProjectNotesDto) {
    if (!isDirty.value) {
      applyAuthoritativeNotes(incoming, true)
    } else {
      pendingExternalNotes.value = incoming
    }
  },
})
</script>

<template>
  <section
    class="project-notes-studio"
    data-testid="project-notes-panel"
    @keydown="handleKeydown"
  >
    <!-- Studio Header: Resolved Path, Storage Mode Pill, Revision, External Sync, Mode Switch & Save -->
    <header class="notes-studio-header">
      <div class="notes-meta-left">
        <div class="notes-title-row">
          <FileText :size="15" class="notes-title-icon" />
          <h2 class="notes-title">{{ t('board.notes.title') }}</h2>
          <span class="notes-storage-pill" data-testid="notes-storage-mode-pill">
            {{ t(`board.hub.storageModeBadge.${effectiveStorageMode}`) }}
          </span>
          <span class="notes-revision-pill" data-testid="notes-revision-badge">
            {{ t('board.notes.revisionBadge', { rev: currentRevision }) }}
          </span>
          <span
            v-if="isDirty"
            class="notes-dirty-pill"
            data-testid="notes-dirty-badge"
          >
            {{ t('board.notes.unsavedBadge') }}
          </span>
          <span
            class="notes-sync-pill"
            :class="{ synced: externalSyncedFlag }"
            data-testid="notes-external-sync-indicator"
          >
            {{
              externalSyncedFlag
                ? t('board.notes.liveSyncedExternally', { rev: currentRevision })
                : t('board.notes.liveSyncActive')
            }}
          </span>
        </div>

        <div class="notes-path-row">
          <span class="notes-path-label">{{ t('board.hub.notesPathLabel') }}:</span>
          <code class="notes-resolved-code" data-testid="notes-resolved-path">{{ effectiveResolvedPath }}</code>
        </div>
      </div>

      <div class="notes-toolbar-right">
        <!-- Edit | Split | Preview Switcher -->
        <div class="notes-mode-switcher" role="group" :aria-label="t('board.notes.viewModeAria')">
          <button
            type="button"
            class="notes-mode-btn ui-pressable"
            :class="{ active: viewMode === 'edit' }"
            data-testid="notes-mode-edit"
            @click="viewMode = 'edit'"
          >
            <PencilLine :size="12" />
            <span>{{ t('board.notes.modeEdit') }}</span>
          </button>
          <button
            type="button"
            class="notes-mode-btn ui-pressable"
            :class="{ active: viewMode === 'split' }"
            data-testid="notes-mode-split"
            @click="viewMode = 'split'"
          >
            <Columns2 :size="12" />
            <span>{{ t('board.notes.modeSplit') }}</span>
          </button>
          <button
            type="button"
            class="notes-mode-btn ui-pressable"
            :class="{ active: viewMode === 'preview' }"
            data-testid="notes-mode-preview"
            @click="viewMode = 'preview'"
          >
            <Eye :size="12" />
            <span>{{ t('board.notes.modePreview') }}</span>
          </button>
        </div>

        <UiButton
          type="button"
          variant="outline"
          size="sm"
          :disabled="loading || saving"
          data-testid="notes-reload-btn"
          @click="handleReload"
        >
          <RefreshCw :size="13" :class="{ 'animate-spin': loading }" />
          <span>{{ t('board.notes.reload') }}</span>
        </UiButton>

        <button
          type="button"
          class="notes-save-btn ui-pressable"
          :disabled="saving || loading"
          :title="t('board.notes.saveShortcutHint')"
          data-testid="notes-save-btn"
          @click="handleSave(false)"
        >
          <Save :size="13" />
          <span>{{ saving ? t('board.notes.saving') : t('board.notes.save') }}</span>
        </button>
      </div>
    </header>

    <!-- Quick Structured Template Insertion Strip -->
    <div class="notes-templates-strip" data-testid="notes-templates-strip">
      <span class="notes-templates-caption">{{ t('board.notes.quickTemplatesLabel') }}</span>
      <button
        type="button"
        class="notes-template-chip ui-pressable"
        data-testid="notes-template-goals"
        @click="insertTemplateSection('goals')"
      >
        <Plus :size="11" />
        <span>{{ t('board.notes.templateGoals') }}</span>
      </button>
      <button
        type="button"
        class="notes-template-chip ui-pressable"
        data-testid="notes-template-architecture"
        @click="insertTemplateSection('architecture')"
      >
        <Plus :size="11" />
        <span>{{ t('board.notes.templateArchitecture') }}</span>
      </button>
      <button
        type="button"
        class="notes-template-chip ui-pressable"
        data-testid="notes-template-decisions"
        @click="insertTemplateSection('decisions')"
      >
        <Plus :size="11" />
        <span>{{ t('board.notes.templateDecisions') }}</span>
      </button>
      <span class="notes-shortcut-hint">{{ t('board.notes.saveShortcutHint') }}</span>
    </div>

    <!-- 409 Revision Conflict / Non-Destructive External Update Prompt -->
    <div
      v-if="conflictDetected || pendingExternalNotes"
      class="notes-conflict-banner"
      role="alert"
      data-testid="notes-conflict-banner"
    >
      <div class="notes-conflict-copy">
        <AlertTriangle :size="14" />
        <div>
          <strong>
            {{
              conflictDetected
                ? t('board.notes.conflictTitle')
                : t('board.notes.externalChangeTitle', { rev: pendingExternalNotes?.revision ?? currentRevision })
            }}
          </strong>
          <p>{{ t('board.notes.conflictDescription') }}</p>
        </div>
      </div>
      <div class="notes-conflict-actions">
        <UiButton
          type="button"
          variant="outline"
          size="sm"
          data-testid="notes-conflict-reload-btn"
          @click="handleReload"
        >
          {{ t('board.notes.conflictReloadDisk') }}
        </UiButton>
        <button
          type="button"
          class="notes-save-btn ui-pressable"
          data-testid="notes-conflict-overwrite-btn"
          @click="handleSave(true)"
        >
          {{ t('board.notes.conflictKeepMine') }}
        </button>
      </div>
    </div>

    <div v-if="errorMessage" class="notes-error-banner" role="alert" data-testid="notes-error-banner">
      {{ errorMessage }}
    </div>

    <!-- Main Studio Surface: Edit | Split | Preview -->
    <div
      class="notes-workspace-grid"
      :class="`notes-workspace-grid--${viewMode}`"
    >
      <div
        v-if="viewMode === 'edit' || viewMode === 'split'"
        class="notes-pane notes-editor-pane"
      >
        <div class="notes-pane-header">
          <span>{{ t('board.notes.editorPaneTitle') }}</span>
        </div>
        <textarea
          ref="editorRef"
          v-model="draftContent"
          class="notes-textarea"
          :placeholder="t('board.notes.editorPlaceholder')"
          spellcheck="false"
          data-testid="notes-editor-textarea"
        />
      </div>

      <div
        v-if="viewMode === 'preview' || viewMode === 'split'"
        class="notes-pane notes-preview-pane"
        data-testid="notes-preview-pane"
      >
        <div class="notes-pane-header">
          <span>{{ t('board.notes.previewPaneTitle') }}</span>
        </div>
        <div class="notes-preview-body">
          <MarkdownRender v-if="draftContent.trim()" :content="draftContent" />
          <p v-else class="notes-preview-empty">{{ t('board.notes.emptyPreview') }}</p>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.project-notes-studio {
  flex: 1;
  min-height: 440px;
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 14px 16px 18px;
  overflow: hidden;
}

.notes-studio-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.notes-meta-left {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
}

.notes-title-row {
  display: flex;
  align-items: center;
  gap: 7px;
  flex-wrap: wrap;
}

.notes-title-icon {
  color: var(--text-secondary);
  flex-shrink: 0;
}

.notes-title {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
  color: var(--text-primary);
}

.notes-storage-pill,
.notes-revision-pill,
.notes-sync-pill,
.notes-dirty-pill {
  display: inline-flex;
  align-items: center;
  height: 21px;
  padding: 0 8px;
  border-radius: 999px;
  font-size: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  transition: background-color 150ms ease, color 150ms ease, border-color 150ms ease;
}

.notes-revision-pill {
  font-family: var(--font-mono, monospace);
  font-weight: 500;
}

.notes-dirty-pill,
.notes-sync-pill.synced {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  border-color: transparent;
  font-weight: 500;
}

.notes-path-row {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  font-size: 11.5px;
  color: var(--text-muted);
}

.notes-resolved-code {
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 5px;
  background: var(--surface-section, var(--bg-tertiary));
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.notes-toolbar-right {
  display: flex;
  align-items: center;
  gap: 7px;
  flex-wrap: wrap;
}

.notes-mode-switcher {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px;
  border-radius: var(--radius-control-md, 8px);
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
}

.notes-mode-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 9px;
  border-radius: var(--radius-control, 6px);
  border: 0;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color 150ms ease, color 150ms ease;
}

.notes-mode-btn:hover {
  color: var(--text-primary);
  background: var(--surface-hover);
}

.notes-mode-btn.active {
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
}

.notes-save-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  padding: 0 12px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid transparent;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: opacity 150ms ease, background-color 150ms ease;
}

.notes-save-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.notes-templates-strip {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.notes-templates-caption {
  font-size: 11.5px;
  color: var(--text-muted);
}

.notes-template-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 9px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  cursor: pointer;
  transition: background-color 150ms ease, color 150ms ease, border-color 150ms ease;
}

.notes-template-chip:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
  border-color: var(--border-default);
}

.notes-shortcut-hint {
  margin-left: auto;
  font-size: 11px;
  color: var(--text-muted);
}

.notes-conflict-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  padding: 10px 12px;
  border-radius: 9px;
  border: 1px solid var(--border-default, var(--border-muted));
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-primary);
}

.notes-conflict-copy {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  font-size: 12px;
}

.notes-conflict-copy p {
  margin: 2px 0 0;
  font-size: 11.5px;
  color: var(--text-secondary);
}

.notes-conflict-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.notes-error-banner {
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-error, var(--text-primary));
  font-size: 12px;
}

.notes-workspace-grid {
  flex: 1;
  min-height: 0;
  display: grid;
  gap: 12px;
}

.notes-workspace-grid--split {
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
}

.notes-workspace-grid--edit,
.notes-workspace-grid--preview {
  grid-template-columns: minmax(0, 1fr);
}

@media (max-width: 900px) {
  .notes-workspace-grid--split {
    grid-template-columns: 1fr;
  }
}

.notes-pane {
  min-height: 320px;
  display: flex;
  flex-direction: column;
  border-radius: var(--radius-island, 10px);
  border: 1px solid var(--border-card, var(--border-muted));
  background: color-mix(in srgb, var(--surface-section, var(--bg-secondary)) 62%, transparent);
  overflow: hidden;
}

.notes-pane-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 7px 12px;
  font-size: 11px;
  font-weight: 500;
  color: var(--text-muted);
  background: color-mix(in srgb, var(--surface-raised, var(--bg-secondary)) 45%, transparent);
}

.notes-textarea {
  flex: 1;
  width: 100%;
  min-height: 280px;
  resize: none;
  border: 0;
  outline: none;
  padding: 12px 14px;
  background: transparent;
  color: var(--text-primary);
  font-family: var(--font-mono, monospace);
  font-size: 12.5px;
  line-height: 1.6;
}

.notes-preview-body {
  flex: 1;
  min-height: 280px;
  overflow-y: auto;
  padding: 12px 16px;
}

.notes-preview-empty {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
}
</style>
