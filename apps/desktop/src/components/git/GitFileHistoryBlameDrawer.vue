<script setup lang="ts">
import { Eye, FileCode2, GitCommit, History, Loader2, UserCheck, X } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNotifications } from '@/composables/useNotifications'
import { openInEditor } from '@/lib/documentWorkspace'
import DiffViewer from './DiffViewer.vue'
import {
  fetchGitBlame,
  fetchGitFileAtRevision,
  fetchGitFileHistory,
  patchFileToUnifiedDiff,
  type GitBlameLine,
  type GitFileHistoryEntry,
} from './gitReadClient'

const props = withDefaults(defineProps<{
  open: boolean
  cwd: string | undefined
  filePath: string | null
  initialTab?: 'history' | 'blame'
}>(), {
  initialTab: 'history',
})

const emit = defineEmits<{
  close: []
  'open-commit': [hash: string]
}>()

const { t, te, locale } = useI18n()
const { notify } = useNotifications()
const lbl = (key: string, fallback: string) => (typeof te === 'function' && te(key) ? t(key) : fallback)

const activeTab = ref<'history' | 'blame'>(props.initialTab)
watch(() => props.initialTab, (tab) => {
  activeTab.value = tab
})

const historyLoading = ref(false)
const historyEntries = ref<GitFileHistoryEntry[]>([])
const selectedCommitHash = ref<string | null>(null)
const revisionPreview = ref<{ rev: string; content: string | null; isBinary: boolean } | null>(null)
const revisionLoading = ref(false)

const blameLoading = ref(false)
const blameLines = ref<GitBlameLine[]>([])

const LANE_PALETTE = ['#3b82f6', '#10b981', '#a855f7', '#f59e0b', '#ec4899', '#06b6d4']

function commitColor(commitHash: string): string {
  let hash = 0
  for (let i = 0; i < Math.min(commitHash.length, 8); i++) {
    hash = (hash * 31 + commitHash.charCodeAt(i)) >>> 0
  }
  return LANE_PALETTE[hash % LANE_PALETTE.length]!
}

async function loadHistory(): Promise<void> {
  if (!props.open || !props.cwd || !props.filePath) return
  historyLoading.value = true
  revisionPreview.value = null
  try {
    const res = await fetchGitFileHistory(props.cwd, props.filePath, {
      follow: true,
      include_patch: true,
      limit: 60,
    })
    historyEntries.value = Array.isArray(res?.entries) ? res.entries : []
    selectedCommitHash.value = historyEntries.value[0]?.commit.hash ?? null
  } catch (err) {
    historyEntries.value = []
    notify.error(err, { title: t('context.gitLoadFailed'), source: 'git', key: 'git-file-history' })
  } finally {
    historyLoading.value = false
  }
}

async function loadBlame(): Promise<void> {
  if (!props.open || !props.cwd || !props.filePath) return
  blameLoading.value = true
  try {
    const res = await fetchGitBlame(props.cwd, props.filePath, { rev: 'HEAD' })
    blameLines.value = Array.isArray(res?.lines) ? res.lines : []
  } catch (err) {
    blameLines.value = []
    notify.error(err, { title: t('context.gitLoadFailed'), source: 'git', key: 'git-blame' })
  } finally {
    blameLoading.value = false
  }
}

async function viewAtRevision(rev: string): Promise<void> {
  if (!props.cwd || !props.filePath) return
  revisionLoading.value = true
  try {
    const res = await fetchGitFileAtRevision(props.cwd, props.filePath, rev)
    revisionPreview.value = {
      rev,
      content: res?.content ?? null,
      isBinary: Boolean(res?.is_binary),
    }
  } catch (err) {
    notify.error(err, { title: t('context.gitLoadFailed'), source: 'git', key: 'git-file-rev' })
  } finally {
    revisionLoading.value = false
  }
}

const selectedEntry = computed(() =>
  historyEntries.value.find((e) => e.commit.hash === selectedCommitHash.value) ?? null,
)

const selectedPatchDiff = computed(() =>
  selectedEntry.value?.patch ? patchFileToUnifiedDiff(selectedEntry.value.patch) : '',
)

function formatDate(raw?: string | null): string {
  if (!raw) return ''
  try {
    const d = new Date(raw)
    if (Number.isNaN(d.getTime())) return raw.slice(0, 10)
    return d.toLocaleDateString(locale?.value ?? undefined, { month: 'short', day: 'numeric' })
  } catch {
    return raw.slice(0, 10)
  }
}

watch([() => props.open, () => props.cwd, () => props.filePath, activeTab], ([openNow]) => {
  if (!openNow || !props.cwd || !props.filePath) return
  if (activeTab.value === 'history') void loadHistory()
  else void loadBlame()
}, { immediate: true })
</script>

<template>
  <aside v-if="open" class="git-file-drawer" data-testid="git-file-inspector-drawer" role="dialog" :aria-label="lbl('context.gitFileHistoryTitle', 'File History')">
    <header class="git-file-drawer__head">
      <div class="git-file-drawer__title">
        <FileCode2 :size="14" aria-hidden="true" />
        <code class="git-file-drawer__path">{{ filePath }}</code>
      </div>
      <div class="git-file-drawer__actions">
        <button
          v-if="cwd && filePath"
          type="button"
          class="git-file-drawer__btn"
          :title="lbl('context.gitOpenInEditor', 'Open in Editor')"
          @click="openInEditor({ path: filePath, cwd })"
        >
          <FileCode2 :size="12" aria-hidden="true" />
          <span>{{ lbl('context.gitOpenInEditor', 'Open in Editor') }}</span>
        </button>
        <button type="button" class="git-file-drawer__close" :aria-label="t('common.close')" data-testid="git-file-drawer-close" @click="emit('close')">
          <X :size="14" aria-hidden="true" />
        </button>
      </div>
    </header>

    <div class="git-file-drawer__tabs" role="tablist">
      <button
        type="button"
        role="tab"
        class="git-file-drawer__tab"
        :class="{ active: activeTab === 'history' }"
        :aria-selected="activeTab === 'history'"
        data-testid="file-inspector-tab-history"
        @click="activeTab = 'history'"
      >
        <History :size="12" aria-hidden="true" />
        <span>{{ lbl('context.gitShowHistory', 'Show History') }}</span>
      </button>
      <button
        type="button"
        role="tab"
        class="git-file-drawer__tab"
        :class="{ active: activeTab === 'blame' }"
        :aria-selected="activeTab === 'blame'"
        data-testid="file-inspector-tab-blame"
        @click="activeTab = 'blame'"
      >
        <UserCheck :size="12" aria-hidden="true" />
        <span>{{ lbl('context.gitAnnotateBlame', 'Annotate Blame') }}</span>
      </button>
    </div>

    <div class="git-file-drawer__body">
      <template v-if="activeTab === 'history'">
        <div v-if="historyLoading" class="git-file-drawer__loading">
          <Loader2 :size="18" class="spinning" aria-hidden="true" />
        </div>
        <p v-else-if="historyEntries.length === 0" class="git-file-drawer__empty" data-testid="git-file-history-empty">
          {{ lbl('context.gitFileHistoryEmpty', 'No file history found.') }}
        </p>
        <template v-else>
          <ul class="git-file-history-list">
            <li
              v-for="entry in historyEntries"
              :key="entry.commit.hash"
              class="git-file-history-item"
              :class="{ active: selectedCommitHash === entry.commit.hash }"
              data-testid="git-file-history-row"
              @click="selectedCommitHash = entry.commit.hash; revisionPreview = null"
            >
              <div class="git-file-history-top">
                <span class="git-file-history-hash">{{ entry.commit.short_hash || entry.commit.hash.slice(0, 7) }}</span>
                <span class="git-file-history-author">{{ entry.commit.author }}</span>
                <span class="git-file-history-date">{{ formatDate(entry.commit.author_date) }}</span>
                <button
                  type="button"
                  class="git-file-drawer__mini"
                  :title="lbl('context.gitViewAtRevision', 'View at Revision')"
                  @click.stop="viewAtRevision(entry.commit.hash)"
                >
                  <Eye :size="11" aria-hidden="true" />
                </button>
              </div>
              <div class="git-file-history-subj">{{ entry.commit.subject }}</div>
              <div v-if="entry.change.old_path && entry.change.old_path !== entry.change.new_path" class="git-file-history-rename">
                {{ entry.change.old_path }} → {{ entry.change.new_path }}
              </div>
            </li>
          </ul>

          <div v-if="revisionLoading" class="git-file-drawer__loading">
            <Loader2 :size="16" class="spinning" aria-hidden="true" />
          </div>
          <pre v-else-if="revisionPreview" class="git-file-rev-preview" data-testid="git-file-revision-preview"><code>{{ revisionPreview.isBinary ? t('context.gitBinaryFile') : (revisionPreview.content ?? '') }}</code></pre>
          <DiffViewer
            v-else-if="selectedPatchDiff"
            :diff-text="selectedPatchDiff"
            :file-path="selectedEntry?.change.new_path ?? filePath"
          />
        </template>
      </template>

      <template v-else>
        <div v-if="blameLoading" class="git-file-drawer__loading">
          <Loader2 :size="18" class="spinning" aria-hidden="true" />
        </div>
        <p v-else-if="blameLines.length === 0" class="git-file-drawer__empty" data-testid="git-blame-empty">
          {{ lbl('context.gitBlameEmpty', 'No blame lines found.') }}
        </p>
        <div v-else class="git-blame-table" data-testid="git-blame-table">
          <div v-for="line in blameLines" :key="line.line_number" class="git-blame-row">
            <button
              type="button"
              class="git-blame-gutter"
              :style="{ borderLeftColor: commitColor(line.commit) }"
              :title="line.commit"
              @click="emit('open-commit', line.commit)"
            >
              <GitCommit :size="10" aria-hidden="true" />
              <span class="git-blame-hash">{{ line.commit.slice(0, 7) }}</span>
              <span class="git-blame-author">{{ line.author }}</span>
            </button>
            <span class="git-blame-num">{{ line.line_number }}</span>
            <code class="git-blame-code">{{ line.content }}</code>
          </div>
        </div>
      </template>
    </div>
  </aside>
</template>

<style scoped>
.git-file-drawer {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  border-radius: 10px;
  border: 1px solid var(--border-card);
  background: var(--surface-raised);
  max-height: 460px;
  min-width: 0;
}

.git-file-drawer__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.git-file-drawer__title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  flex: 1 1 140px;
}

.git-file-drawer__path {
  font-size: 11px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-file-drawer__actions {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.git-file-drawer__btn,
.git-file-drawer__mini {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 7px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 11px;
  cursor: pointer;
}

.git-file-drawer__close {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border: none;
  border-radius: 5px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.git-file-drawer__tabs {
  display: flex;
  gap: 6px;
}

.git-file-drawer__tab {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 10px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
}

.git-file-drawer__tab.active {
  border-color: var(--accent-primary);
  color: var(--text-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-raised));
}

.git-file-drawer__body {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-height: 0;
  overflow-y: auto;
}

.git-file-drawer__loading,
.git-file-drawer__empty {
  display: flex;
  justify-content: center;
  padding: 16px;
  color: var(--text-muted);
  font-size: 12px;
}

.git-file-history-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-height: 170px;
  overflow-y: auto;
}

.git-file-history-item {
  padding: 6px 8px;
  border-radius: 6px;
  border: 1px solid transparent;
  background: var(--surface-section);
  cursor: pointer;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.git-file-history-item.active,
.git-file-history-item:hover {
  border-color: var(--accent-primary);
}

.git-file-history-top {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 10px;
  color: var(--text-secondary);
}

.git-file-history-hash {
  font-family: var(--font-mono);
  color: var(--accent-primary);
  font-weight: 600;
}

.git-file-history-date {
  margin-left: auto;
}

.git-file-history-subj {
  font-size: 12px;
  color: var(--text-primary);
}

.git-file-history-rename {
  font-size: 10px;
  font-family: var(--font-mono);
  color: var(--text-muted);
}

.git-file-rev-preview {
  margin: 0;
  padding: 8px;
  border-radius: 6px;
  background: var(--surface-section);
  font-size: 11px;
  max-height: 220px;
  overflow: auto;
}

.git-blame-table {
  display: flex;
  flex-direction: column;
  font-family: var(--font-mono);
  font-size: 11px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  overflow: auto;
  max-height: 300px;
}

.git-blame-row {
  display: grid;
  grid-template-columns: 165px 36px minmax(0, 1fr);
  align-items: center;
  border-bottom: 1px solid color-mix(in srgb, var(--border-muted) 45%, transparent);
}

.git-blame-gutter {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  border: none;
  border-right: 1px solid color-mix(in srgb, var(--border-muted) 65%, transparent);
  background: var(--surface-section);
  color: var(--text-secondary);
  font: inherit;
  font-size: 10px;
  text-align: left;
  cursor: pointer;
  overflow: hidden;
}

.git-blame-author {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-blame-num {
  text-align: right;
  padding: 2px 6px;
  color: var(--text-muted);
  user-select: none;
}

.git-blame-code {
  padding: 2px 6px;
  white-space: pre;
  overflow-x: auto;
}

.spinning {
  animation: drawer-spin 1s linear infinite;
}

@keyframes drawer-spin {
  to { transform: rotate(360deg); }
}
</style>
