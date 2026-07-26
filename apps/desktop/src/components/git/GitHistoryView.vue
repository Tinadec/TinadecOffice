<script setup lang="ts">
import { GitCommit, RefreshCw, Loader2, User, Clock, Hash } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type CodeToolExecuteResultDto } from '../../api'
import { useNotifications } from '@/composables/useNotifications'
import { UiBadge, UiButton, UiScrollArea } from '../ui'
import type { GitLogCommit } from '../../composables/useGitOperation'

interface Props {
  cwd: string | undefined
  commits: GitLogCommit[]
  loading: boolean
}

const props = defineProps<Props>()
const { t } = useI18n()
const { notify } = useNotifications()

const selectedHash = ref<string | null>(null)
const loadingDetail = ref(false)
const commitDetail = ref<CodeToolExecuteResultDto | null>(null)

const selectedCommit = computed(() =>
  props.commits.find((c) => c.hash === selectedHash.value) ?? null,
)

interface CommitDetailData {
  hash: string
  short_hash: string
  author: string
  email: string
  date: string
  subject: string
  body?: string
  files?: Array<{
    path: string
    change_type: string
    additions: number
    deletions: number
  }>
  diff_stat?: string
}

const detail = computed<CommitDetailData | null>(() => {
  if (!commitDetail.value?.data) return null
  return commitDetail.value.data as unknown as CommitDetailData
})

async function selectCommit(commit: GitLogCommit) {
  selectedHash.value = commit.hash
  if (!props.cwd) return
  loadingDetail.value = true
  try {
    const res = await api.executeCodeTool('git_worktree_manager', {
      cwd: props.cwd,
      arguments: { action: 'diff_compare', base_ref: `${commit.short_hash}~1`, head_ref: commit.short_hash },
    })
    commitDetail.value = res
  } catch (err) {
    notify.error(err, { title: t('context.gitLoadFailed'), source: 'git', key: 'git-commit-detail' })
  } finally {
    loadingDetail.value = false
  }
}

// Group commits by date
const groupedCommits = computed(() => {
  const groups: Record<string, GitLogCommit[]> = {}
  for (const commit of props.commits) {
    const date = commit.date.split(' ')[0] ?? commit.date
    if (!groups[date]) groups[date] = []
    groups[date].push(commit)
  }
  return Object.entries(groups).slice(0, 30)
})

// Reset selection when commits change
watch(() => props.commits, () => {
  if (selectedHash.value && !props.commits.some((c) => c.hash === selectedHash.value)) {
    selectedHash.value = null
    commitDetail.value = null
  }
})

function formatDate(dateStr: string): string {
  try {
    const d = new Date(dateStr)
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
  } catch {
    return dateStr.split(' ')[0] ?? dateStr
  }
}

function formatTime(dateStr: string): string {
  try {
    const d = new Date(dateStr)
    return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
  } catch {
    return ''
  }
}

defineExpose({
  refresh: () => {
    selectedHash.value = null
    commitDetail.value = null
  },
})
</script>

<template>
  <div class="git-history-view">
    <div class="git-history-head">
      <div class="git-history-title">
        <GitCommit :size="14" />
        <span>{{ t('context.gitHistory') }}</span>
        <UiBadge variant="secondary">{{ commits.length }}</UiBadge>
      </div>
      <UiButton variant="ghost" size="xs" :disabled="loading" @click="$emit('refresh')">
        <RefreshCw :size="12" :class="{ spinning: loading }" />
      </UiButton>
    </div>

    <div v-if="loading && commits.length === 0" class="git-history-loading">
      <Loader2 :size="20" class="spinning" />
      <span>{{ t('context.loadingGitPlan') }}</span>
    </div>

    <div v-else-if="commits.length === 0" class="git-history-empty">
      {{ t('context.gitNoCommits') }}
    </div>

    <div v-else class="git-history-split">
      <!-- Commit list -->
      <UiScrollArea class="git-history-list-scroll">
        <div class="git-history-timeline">
          <div v-for="[date, dayCommits] in groupedCommits" :key="date" class="git-history-group">
            <div class="git-history-date">{{ formatDate(date) }}</div>
            <div class="git-history-commits">
              <button
                v-for="commit in dayCommits"
                :key="commit.hash"
                class="git-history-item"
                :class="{ active: selectedHash === commit.hash }"
                @click="selectCommit(commit)"
              >
                <div class="git-history-item-dot" />
                <div class="git-history-item-body">
                  <span class="git-history-item-subject">{{ commit.subject }}</span>
                  <div class="git-history-item-meta">
                    <code class="git-history-item-hash">{{ commit.short_hash }}</code>
                    <span class="git-history-item-author">
                      <User :size="10" />
                      {{ commit.author }}
                    </span>
                    <span class="git-history-item-time">{{ formatTime(commit.date) }}</span>
                  </div>
                </div>
              </button>
            </div>
          </div>
        </div>
      </UiScrollArea>

      <!-- Commit detail -->
      <div v-if="selectedCommit" class="git-history-detail">
        <div v-if="loadingDetail" class="git-history-detail-loading">
          <Loader2 :size="16" class="spinning" />
          <span>{{ t('context.loadingGitPlan') }}</span>
        </div>
        <template v-else-if="detail">
          <div class="git-detail-header">
            <h3>{{ selectedCommit.subject }}</h3>
            <div class="git-detail-meta">
              <span class="git-detail-meta-item">
                <Hash :size="11" />
                <code>{{ detail.short_hash }}</code>
              </span>
              <span class="git-detail-meta-item">
                <User :size="11" />
                {{ detail.author }}
              </span>
              <span class="git-detail-meta-item">
                <Clock :size="11" />
                {{ detail.date }}
              </span>
            </div>
          </div>

          <div v-if="detail.body" class="git-detail-body">
            <pre>{{ detail.body }}</pre>
          </div>

          <div v-if="detail.files && detail.files.length > 0" class="git-detail-files">
            <div class="git-detail-section-title">
              {{ t('context.gitCompareFiles') }}
              <UiBadge variant="outline">{{ detail.files.length }}</UiBadge>
            </div>
            <div class="git-detail-file-list">
              <div
                v-for="file in detail.files"
                :key="file.path"
                class="git-detail-file"
              >
                <span class="git-detail-file-path">{{ file.path }}</span>
                <span class="git-detail-file-stats">
                  <span class="add">+{{ file.additions }}</span>
                  <span class="del">-{{ file.deletions }}</span>
                </span>
              </div>
            </div>
          </div>
        </template>
      </div>

      <!-- Empty detail placeholder -->
      <div v-else class="git-history-detail-empty">
        <GitCommit :size="32" />
        <span>{{ t('context.gitHistorySelectHint') }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.git-history-view {
  display: flex;
  flex-direction: column;
  gap: 8px;
  height: 100%;
  min-height: 0;
}

.git-history-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.git-history-title {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
}

.git-history-loading,
.git-history-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 32px 16px;
  color: var(--text-muted);
  font-size: 12px;
}

.git-history-split {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
  min-height: 0;
  flex: 1;
}

.git-history-list-scroll {
  min-height: 200px;
  max-height: 100%;
}

.git-history-timeline {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 4px;
}

.git-history-group {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.git-history-date {
  padding: 4px 8px;
  font-size: 11px;
  font-weight: 700;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.git-history-commits {
  display: flex;
  flex-direction: column;
  gap: 1px;
  position: relative;
  padding-left: 12px;
}

.git-history-commits::before {
  content: '';
  position: absolute;
  left: 5px;
  top: 0;
  bottom: 0;
  width: 1px;
  background: var(--border-muted);
}

.git-history-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 6px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  cursor: pointer;
  text-align: left;
  transition: background 0.1s;
  position: relative;
}

.git-history-item:hover {
  background: var(--bg-hover);
}

.git-history-item.active {
  background: var(--bg-selected);
  box-shadow: inset 2px 0 0 var(--accent-primary);
}

.git-history-item-dot {
  position: absolute;
  left: -11px;
  top: 10px;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--bg-secondary);
  border: 2px solid var(--text-muted);
  z-index: 1;
}

.git-history-item.active .git-history-item-dot {
  border-color: var(--accent-primary);
  background: var(--accent-primary);
}

.git-history-item-body {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  flex: 1;
}

.git-history-item-subject {
  font-size: 12px;
  color: var(--text-primary);
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-history-item-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 10px;
  color: var(--text-muted);
}

.git-history-item-meta svg {
  opacity: 0.7;
}

.git-history-item-hash {
  color: var(--accent-primary);
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 10px;
}

.git-history-item-author {
  display: flex;
  align-items: center;
  gap: 3px;
}

.git-history-detail {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
  overflow-y: auto;
}

.git-history-detail-loading,
.git-history-detail-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 32px 16px;
  color: var(--text-muted);
  font-size: 12px;
  border: 1px dashed var(--border-muted);
  border-radius: 8px;
}

.git-detail-header h3 {
  margin: 0 0 6px 0;
  font-size: 13px;
  font-weight: 700;
  color: var(--text-primary);
  line-height: 1.4;
}

.git-detail-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.git-detail-meta-item {
  display: flex;
  align-items: center;
  gap: 3px;
  font-size: 11px;
  color: var(--text-secondary);
}

.git-detail-meta-item code {
  color: var(--accent-primary);
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.git-detail-body {
  padding: 8px;
  background: var(--bg-tertiary);
  border-radius: 6px;
  font-size: 12px;
}

.git-detail-body pre {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 11px;
  color: var(--text-secondary);
  line-height: 1.5;
}

.git-detail-section-title {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  font-weight: 700;
  color: var(--text-secondary);
  margin-bottom: 4px;
}

.git-detail-file-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-height: 240px;
  overflow-y: auto;
}

.git-detail-file {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 6px;
  border-radius: 4px;
  font-size: 11px;
}

.git-detail-file:hover {
  background: var(--bg-hover);
}

.git-detail-file-path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text-primary);
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.git-detail-file-stats {
  display: flex;
  gap: 6px;
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 10px;
}

.git-detail-file-stats .add { color: #3fb950; }
.git-detail-file-stats .del { color: #f85149; }

.spinning {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}
</style>
