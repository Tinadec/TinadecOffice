<script setup lang="ts">
import type { editor } from 'monaco-editor'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useMonacoDiff } from '@/composables/useMonacoDiff'
import { parseUnifiedDiff } from '@/gitDiffParser'
import { detectLanguage, reconstructFromHunks, summarizeEntries, type DiffFileEntry } from './diffUtils'

type DiffEditor = editor.IDiffEditor

interface Props {
  /** Single-file mode: unified diff text for one file. */
  diffText?: string | null
  /** Single-file mode: explicit original content. */
  originalContent?: string | null
  /** Single-file mode: explicit modified content. */
  modifiedContent?: string | null
  /** Single-file mode: file path (used for language detection + display). */
  filePath?: string | null
  /** Optional explicit language override. */
  language?: string | null
  additions?: number | null
  deletions?: number | null
  truncated?: boolean
  binary?: boolean
  /** Multi-file mode: list of files to choose from. */
  files?: DiffFileEntry[] | null
  /** Multi-file mode: currently selected file path. */
  selectedFilePath?: string | null
  /** Allow stage/discard hunk actions. */
  enableHunkActions?: boolean
}

const props = withDefaults(defineProps<Props>(), {
  diffText: null,
  originalContent: null,
  modifiedContent: null,
  filePath: null,
  language: null,
  additions: null,
  deletions: null,
  truncated: false,
  binary: false,
  files: null,
  selectedFilePath: null,
  enableHunkActions: false
})

const emit = defineEmits<{
  'update:selectedFilePath': [path: string]
  'stage-hunk': [payload: { filePath: string; hunkHeader: string | null }]
  'discard-hunk': [payload: { filePath: string; hunkHeader: string | null }]
}>()

const { t } = useI18n()
const { createDiffEditor, updateDiffModel, disposeDiffEditor } = useMonacoDiff()

const containerRef = ref<HTMLDivElement | null>(null)
const editorRef = ref<DiffEditor | null>(null)
const ready = ref(false)
const fileMenuOpen = ref(false)
let disposed = false

const multiFile = computed(() => Array.isArray(props.files) && props.files.length > 0)

const entries = computed<DiffFileEntry[]>(() => {
  if (multiFile.value && props.files) {
    return props.files
  }
  return [
    {
      path: props.filePath ?? 'file',
      previousPath: null,
      diffText: props.diffText,
      originalContent: props.originalContent,
      modifiedContent: props.modifiedContent,
      additions: props.additions ?? undefined,
      deletions: props.deletions ?? undefined,
      binary: props.binary,
      truncated: props.truncated
    }
  ]
})

const currentPath = computed(() => {
  if (multiFile.value) {
    return props.selectedFilePath ?? props.files?.[0]?.path ?? null
  }
  return props.filePath ?? entries.value[0]?.path ?? null
})

const currentEntry = computed<DiffFileEntry | null>(() => {
  const list = entries.value
  if (list.length === 0) return null
  if (!currentPath.value) return list[0] ?? null
  return list.find((entry) => entry.path === currentPath.value) ?? list[0] ?? null
})

const totalStats = computed(() => summarizeEntries(entries.value))

const currentAdditions = computed(() => currentEntry.value?.additions ?? 0)
const currentDeletions = computed(() => currentEntry.value?.deletions ?? 0)
const currentBinary = computed(() => currentEntry.value?.binary === true)
const currentTruncated = computed(() => currentEntry.value?.truncated === true)

const resolvedLanguage = computed(() => {
  if (props.language) return props.language
  return detectLanguage(currentEntry.value?.path ?? props.filePath)
})

function resolveOriginalModified(entry: DiffFileEntry): { original: string; modified: string } {
  if (entry.originalContent != null && entry.modifiedContent != null) {
    return { original: entry.originalContent, modified: entry.modifiedContent }
  }
  if (entry.diffText) {
    const parsed = parseUnifiedDiff(entry.diffText)
    const file = parsed.files[0]
    if (file) {
      return reconstructFromHunks(file)
    }
  }
  return { original: '', modified: '' }
}

async function mountEditor() {
  if (disposed || !containerRef.value) return
  const entry = currentEntry.value
  const { original, modified } = entry ? resolveOriginalModified(entry) : { original: '', modified: '' }
  if (editorRef.value) {
    updateDiffModel(editorRef.value, original, modified, resolvedLanguage.value)
    return
  }
  ready.value = false
  const editor = await createDiffEditor(containerRef.value, original, modified, resolvedLanguage.value)
  if (disposed) {
    disposeDiffEditor(editor)
    return
  }
  editorRef.value = editor
  ready.value = true
}

function refreshModel() {
  if (!editorRef.value) return
  const entry = currentEntry.value
  const { original, modified } = entry ? resolveOriginalModified(entry) : { original: '', modified: '' }
  updateDiffModel(editorRef.value, original, modified, resolvedLanguage.value)
}

function selectFile(path: string) {
  fileMenuOpen.value = false
  if (multiFile.value) {
    emit('update:selectedFilePath', path)
  }
}

function stageCurrent() {
  if (!currentEntry.value) return
  emit('stage-hunk', { filePath: currentEntry.value.path, hunkHeader: null })
}

function discardCurrent() {
  if (!currentEntry.value) return
  emit('discard-hunk', { filePath: currentEntry.value.path, hunkHeader: null })
}

onMounted(() => {
  void mountEditor()
})

onBeforeUnmount(() => {
  disposed = true
  disposeDiffEditor(editorRef.value)
  editorRef.value = null
})

watch(
  () => [currentPath.value, props.files, props.diffText, props.originalContent, props.modifiedContent],
  () => {
    void refreshModel()
  },
  { deep: false }
)

watch(resolvedLanguage, () => {
  void refreshModel()
})
</script>

<template>
  <div class="diff-viewer">
    <div class="diff-viewer-toolbar">
      <div class="diff-viewer-file">
        <template v-if="multiFile">
          <button
            type="button"
            class="diff-viewer-file-trigger"
            @click="fileMenuOpen = !fileMenuOpen"
          >
            <span class="truncate">{{ currentEntry?.path ?? t('context.gitDiffNoFile') }}</span>
            <small>{{ entries.length }}</small>
          </button>
          <div v-if="fileMenuOpen" class="diff-viewer-file-menu">
            <button
              v-for="entry in entries"
              :key="entry.path"
              type="button"
              class="diff-viewer-file-option"
              :class="{ active: entry.path === currentPath }"
              @click="selectFile(entry.path)"
            >
              <span class="truncate">{{ entry.path }}</span>
              <small>+{{ entry.additions ?? 0 }} -{{ entry.deletions ?? 0 }}</small>
            </button>
          </div>
        </template>
        <div v-else class="diff-viewer-file-static truncate">
          {{ currentEntry?.path ?? t('context.gitDiffNoFile') }}
        </div>
      </div>

      <div class="diff-viewer-stats">
        <span class="diff-stat-add">+{{ currentAdditions }}</span>
        <span class="diff-stat-del">-{{ currentDeletions }}</span>
        <template v-if="multiFile">
          <span class="diff-stat-sep">·</span>
          <span class="diff-stat-total">{{ t('context.gitDiffFiles') }}: {{ entries.length }}</span>
          <span class="diff-stat-total">+{{ totalStats.additions }} -{{ totalStats.deletions }}</span>
        </template>
      </div>

      <div class="diff-viewer-actions">
        <button
          v-if="enableHunkActions"
          type="button"
          class="secondary-button diff-viewer-action"
          :disabled="!currentEntry || currentBinary"
          :title="t('context.gitDiffStageHunk')"
          @click="stageCurrent"
        >
          <span>{{ t('context.gitDiffStageHunk') }}</span>
        </button>
        <button
          v-if="enableHunkActions"
          type="button"
          class="secondary-button diff-viewer-action"
          :disabled="!currentEntry || currentBinary"
          :title="t('context.gitDiffDiscardHunk')"
          @click="discardCurrent"
        >
          <span>{{ t('context.gitDiffDiscardHunk') }}</span>
        </button>
      </div>
    </div>

    <div v-if="currentTruncated" class="diff-viewer-notice">
      {{ t('context.gitCompareTruncated') }}
    </div>

    <div v-if="currentBinary" class="diff-viewer-binary">
      {{ t('context.gitBinaryFile') }}
    </div>
    <div
      v-else
      ref="containerRef"
      class="diff-viewer-container"
      :class="{ 'is-ready': ready }"
    ></div>
  </div>
</template>

<style scoped>
.diff-viewer {
  display: grid;
  grid-template-rows: auto auto 1fr;
  gap: 6px;
  min-height: 0;
  height: 100%;
}

.diff-viewer-toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  padding: 6px 8px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
}

.diff-viewer-file {
  position: relative;
  min-width: 0;
  flex: 1 1 200px;
}

.diff-viewer-file-trigger {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  width: 100%;
  padding: 5px 8px;
  color: var(--text-primary);
  background: var(--bg-primary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  font-size: 12px;
  cursor: pointer;
  text-align: left;
}

.diff-viewer-file-trigger small {
  color: var(--text-muted);
  font-size: 10px;
}

.diff-viewer-file-static {
  display: flex;
  align-items: center;
  padding: 5px 8px;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 600;
}

.diff-viewer-file-menu {
  position: absolute;
  z-index: 50;
  top: calc(100% + 2px);
  left: 0;
  right: 0;
  max-height: 240px;
  overflow: auto;
  padding: 4px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--bg-popover, var(--bg-primary));
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.25);
}

.diff-viewer-file-option {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  width: 100%;
  padding: 6px 8px;
  color: var(--text-secondary);
  background: transparent;
  border: 0;
  border-radius: 4px;
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.diff-viewer-file-option:hover,
.diff-viewer-file-option.active {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.diff-viewer-file-option small {
  color: var(--text-muted);
  font-size: 10px;
}

.diff-viewer-stats {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 11px;
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.diff-stat-add {
  color: var(--text-approve, #3fb950);
}

.diff-stat-del {
  color: var(--text-reject, #f85149);
}

.diff-stat-sep {
  color: var(--text-muted);
}

.diff-stat-total {
  color: var(--text-muted);
}

.diff-viewer-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.diff-viewer-action {
  height: 28px;
  width: auto;
  padding: 0 10px;
  font-size: 11px;
}

.diff-viewer-notice {
  padding: 6px 10px;
  color: var(--text-secondary);
  background: var(--bg-status-warn);
  border: 1px solid rgba(210, 153, 34, 0.25);
  border-radius: 6px;
  font-size: 12px;
}

.diff-viewer-binary {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  color: var(--text-muted);
  font-size: 12px;
  border: 1px dashed var(--border-muted);
  border-radius: 8px;
}

.diff-viewer-container {
  width: 100%;
  min-height: 280px;
  height: 100%;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  overflow: hidden;
  background: var(--bg-diff, var(--bg-primary));
}

.diff-viewer-container.is-ready {
  min-height: 320px;
}
</style>
