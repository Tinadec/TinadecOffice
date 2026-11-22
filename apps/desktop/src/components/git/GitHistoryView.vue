<script setup lang="ts">
import { Clock, Eye, GitBranch, GitCommit, Hash, Loader2, RefreshCw, Search, User } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNotifications } from '@/composables/useNotifications'
import { UiBadge, UiButton, UiInput, UiIslandCard, UiScrollArea, UiSelectField } from '../ui'
import type { GitBranch as GitBranchInfo, GitLogCommit } from '../../composables/useGitOperation'
import DiffViewer from './DiffViewer.vue'
import {
  fetchGitFileAtRevision,
  fetchGitLogDetail,
  fetchGitLogList,
  patchFileToUnifiedDiff,
  type GitFileChange,
  type GitGraphCommit,
  type GitLogDetailResponse,
} from './gitReadClient'

interface Props {
  cwd: string | undefined
  commits: GitLogCommit[]
  branches?: GitBranchInfo[]
  loading: boolean
  initialHash?: string | null
}

const props = withDefaults(defineProps<Props>(), {
  branches: () => [],
  initialHash: null,
})

const { t, te, locale } = useI18n()
const { notify } = useNotifications()
const lbl = (key: string, fallback: string) => (typeof te === 'function' && te(key) ? t(key) : fallback)

const graphLoading = ref(false)
const loadingMore = ref(false)
const graphCommits = ref<GitGraphCommit[]>([])
const nextCursor = ref<string | null>(null)
const hasMore = ref(false)

const filterText = ref('')
const branchFilter = ref('')
const authorFilter = ref('')

const selectedHash = ref<string | null>(props.initialHash ?? null)
const loadingDetail = ref(false)
const detail = ref<GitLogDetailResponse | null>(null)
const selectedFilePath = ref<string | null>(null)
const revisionPreview = ref<{ path: string; content: string | null; isBinary: boolean } | null>(null)

const LANE_COLORS = ['#3b82f6', '#10b981', '#a855f7', '#f59e0b', '#ec4899', '#06b6d4']
const LANE_SPACING = 14
const ROW_CENTER_Y = 14

function laneColor(lane: number): string {
  return LANE_COLORS[Math.abs(lane) % LANE_COLORS.length]!
}

function laneX(lane: number): number {
  return 10 + Math.max(0, lane) * LANE_SPACING
}

async function loadGraph(append = false): Promise<void> {
  if (!props.cwd) {
    graphCommits.value = []
    return
  }
  if (append) {
    if (!nextCursor.value || loadingMore.value) return
    loadingMore.value = true
  } else {
    graphLoading.value = true
  }
  try {
    const res = await fetchGitLogList(props.cwd, {
      revs: branchFilter.value ? [branchFilter.value] : undefined,
      limit: 60,
      after_commit: append ? nextCursor.value : undefined,
    })
    const rows = Array.isArray(res?.commits) ? res.commits : []
    graphCommits.value = append ? [...graphCommits.value, ...rows] : rows
    hasMore.value = Boolean(res?.truncated && res?.next_cursor)
    nextCursor.value = res?.next_cursor ?? null
  } catch (err) {
    if (!append) graphCommits.value = []
    notify.error(err, { title: t('context.gitLoadFailed'), source: 'git', key: 'git-log-graph' })
  } finally {
    graphLoading.value = false
    loadingMore.value = false
  }
}

const sourceCommits = computed<GitGraphCommit[]>(() => {
  if (graphCommits.value.length > 0) return graphCommits.value
  return (props.commits ?? []).map((c) => ({
    hash: c.hash,
    short_hash: c.short_hash || c.hash.slice(0, 7),
    parents: [],
    author: c.author,
    author_email: c.email,
    author_date: c.date,
    committer_date: c.date,
    subject: c.subject,
    refs: [],
    lane_index: 0,
    edges: [],
  }))
})

const maxLaneCount = computed(() => {
  let max = 1
  for (const commit of filteredCommits.value) {
    max = Math.max(max, commit.lane_index + 1)
    for (const edge of commit.edges ?? []) {
      max = Math.max(max, edge.from_lane + 1, edge.to_lane + 1)
    }
  }
  return Math.min(8, Math.max(1, max))
})

const svgWidth = computed(() => maxLaneCount.value * LANE_SPACING + 12)

const filteredCommits = computed(() => {
  const q = filterText.value.trim().toLowerCase()
  const authorQ = authorFilter.value.trim().toLowerCase()
  return sourceCommits.value.filter((commit) => {
    if (authorQ && !commit.author.toLowerCase().includes(authorQ) && !commit.author_email.toLowerCase().includes(authorQ)) {
      return false
    }
    if (q) {
      return (
        commit.hash.toLowerCase().includes(q)
        || commit.short_hash.toLowerCase().includes(q)
        || commit.subject.toLowerCase().includes(q)
        || commit.author.toLowerCase().includes(q)
      )
    }
    return true
  })
})

const selectedCommit = computed(() =>
  filteredCommits.value.find((c) => c.hash === selectedHash.value)
  ?? sourceCommits.value.find((c) => c.hash === selectedHash.value)
  ?? null,
)

async function selectCommit(commit: Pick<GitGraphCommit, 'hash'>): Promise<void> {
  if (!commit?.hash) return
  selectedHash.value = commit.hash
  selectedFilePath.value = null
  revisionPreview.value = null
  if (!props.cwd) return
  loadingDetail.value = true
  try {
    detail.value = await fetchGitLogDetail(props.cwd, commit.hash, { include_patch: true, max_files: 200 })
    selectedFilePath.value = detail.value?.files?.[0]?.new_path ?? null
  } catch (err) {
    detail.value = null
    notify.error(err, { title: t('context.gitLoadFailed'), source: 'git', key: 'git-commit-detail' })
  } finally {
    loadingDetail.value = false
  }
}

async function viewFileAtCommit(change: GitFileChange): Promise<void> {
  if (!props.cwd || !selectedHash.value) return
  try {
    const res = await fetchGitFileAtRevision(props.cwd, change.new_path, selectedHash.value)
    revisionPreview.value = {
      path: change.new_path,
      content: res?.content ?? null,
      isBinary: Boolean(res?.is_binary),
    }
  } catch (err) {
    notify.error(err, { title: t('context.gitLoadFailed'), source: 'git', key: 'git-commit-file-rev' })
  }
}

const activePatchDiff = computed(() => {
  const patches = detail.value?.patches
  if (!Array.isArray(patches) || patches.length === 0) return ''
  const target = selectedFilePath.value
    ? patches.find((p) => p.new_path === selectedFilePath.value || p.old_path === selectedFilePath.value)
    : patches[0]
  return target ? patchFileToUnifiedDiff(target) : ''
})

function edgePath(fromLane: number, toLane: number): string {
  const x1 = laneX(fromLane)
  const x2 = laneX(toLane)
  if (x1 === x2) return `M ${x1} ${ROW_CENTER_Y} L ${x2} 34`
  return `M ${x1} ${ROW_CENTER_Y} C ${x1} 24, ${x2} 22, ${x2} 34`
}

function formatDate(dateStr: string): string {
  if (!dateStr) return t('context.gitHistoryNoDate')
  try {
    const d = new Date(dateStr)
    if (Number.isNaN(d.getTime())) return dateStr.split(' ')[0] ?? dateStr
    return d.toLocaleDateString(locale?.value ?? undefined, { month: 'short', day: 'numeric' })
  } catch {
    return dateStr.split(' ')[0] ?? dateStr
  }
}

watch(() => props.cwd, () => {
  selectedHash.value = props.initialHash ?? null
  detail.value = null
  void loadGraph(false)
}, { immediate: true })

watch(branchFilter, () => {
  void loadGraph(false)
})

watch(() => props.initialHash, (nextHash) => {
  if (nextHash) void selectCommit({ hash: nextHash })
})

const branchOptions = computed(() => [
  { value: '', label: lbl('context.gitLogBranchPlaceholder', 'All branches') },
  ...(props.branches ?? []).map((b) => ({ value: b.name, label: b.name })),
])
</script>

<template>
  <div class="git-history-view" data-testid="git-history-view">
    <div class="git-history-filters">
      <div class="git-history-search-wrap">
        <Search :size="12" class="git-history-search-icon" aria-hidden="true" />
        <UiInput
          v-model="filterText"
          class="git-history-search-input"
          :placeholder="lbl('context.gitLogFilterPlaceholder', 'Search message, hash...')"
          data-testid="git-log-filter-input"
        />
      </div>
      <UiSelectField
        v-if="branches.length > 0"
        :value="branchFilter"
        :options="branchOptions"
        :aria-label="lbl('context.gitLogBranchPlaceholder', 'All branches')"
        class="git-history-branch-select"
        @change="branchFilter = $event"
      />
      <UiInput
        v-model="authorFilter"
        class="git-history-author-input"
        :placeholder="lbl('context.gitLogAuthorPlaceholder', 'Author')"
      />
      <UiButton variant="ghost" size="xs" :disabled="loading || graphLoading" :title="t('common.refresh')" @click="loadGraph(false)">
        <RefreshCw :size="13" :class="{ spinning: loading || graphLoading }" />
      </UiButton>
    </div>

    <div v-if="(loading || graphLoading) && filteredCommits.length === 0" class="git-history-loading">
      <Loader2 :size="20" class="spinning" />
      <span>{{ t('common.loading') }}</span>
    </div>

    <div v-else-if="filteredCommits.length === 0" class="git-history-empty">
      <GitCommit :size="24" />
      <span>{{ t('context.gitNoCommits') }}</span>
    </div>

    <div v-else class="git-history-layout">
      <UiIslandCard class="git-history-list-card" padding="none">
        <UiScrollArea class="git-history-list">
          <div class="git-commit-rows">
            <button
              v-for="commit in filteredCommits"
              :key="commit.hash"
              class="git-commit-row"
              :class="{ active: selectedHash === commit.hash }"
              data-testid="git-commit-row"
              @click="selectCommit(commit)"
            >
              <svg
                class="git-graph-svg"
                :width="svgWidth"
                height="34"
                :viewBox="`0 0 ${svgWidth} 34`"
                aria-hidden="true"
              >
                <path
                  v-for="(edge, idx) in commit.edges"
                  :key="`${edge.parent_hash}-${idx}`"
                  :d="edgePath(edge.from_lane, edge.to_lane)"
                  fill="none"
                  :stroke="laneColor(edge.to_lane)"
                  stroke-width="1.6"
                  stroke-linecap="round"
                />
                <circle
                  :cx="laneX(commit.lane_index)"
                  :cy="ROW_CENTER_Y"
                  r="4"
                  :fill="laneColor(commit.lane_index)"
                  stroke="var(--surface-section)"
                  stroke-width="1.5"
                />
              </svg>

              <div class="git-commit-copy">
                <div class="git-commit-top">
                  <span
                    v-for="refTag in commit.refs"
                    :key="`${refTag.type}-${refTag.name}`"
                    class="git-ref-badge"
                    :class="`is-${refTag.type}`"
                    data-testid="git-ref-badge"
                  >
                    <GitBranch v-if="refTag.type !== 'tag'" :size="9" aria-hidden="true" />
                    <span>{{ refTag.name }}</span>
                  </span>
                  <span class="git-commit-subject" :title="commit.subject">{{ commit.subject }}</span>
                </div>
                <div class="git-commit-meta">
                  <span class="git-commit-hash">{{ commit.short_hash }}</span>
                  <span v-if="commit.author" class="git-commit-author">{{ commit.author }}</span>
                  <span class="git-commit-date">{{ formatDate(commit.author_date) }}</span>
                </div>
              </div>
            </button>
          </div>
          <div v-if="hasMore" class="git-load-more">
            <UiButton variant="outline" size="sm" :disabled="loadingMore" data-testid="git-log-load-more" @click="loadGraph(true)">
              {{ loadingMore ? t('common.loading') : lbl('context.gitLogLoadMore', 'Load More') }}
            </UiButton>
          </div>
        </UiScrollArea>
      </UiIslandCard>

      <UiIslandCard v-if="selectedCommit" class="git-history-detail-card" padding="none">
        <div v-if="loadingDetail" class="git-detail-loading">
          <Loader2 :size="18" class="spinning" />
        </div>
        <div v-else class="git-detail-content">
          <div class="git-detail-header">
            <div class="git-detail-hash-row">
              <Hash :size="12" aria-hidden="true" />
              <code>{{ selectedCommit.short_hash }}</code>
              <span v-for="refTag in selectedCommit.refs" :key="`${refTag.type}-${refTag.name}`" class="git-ref-badge" :class="`is-${refTag.type}`">
                {{ refTag.name }}
              </span>
            </div>
            <h4 class="git-detail-subject">{{ detail?.commits?.[0]?.subject ?? selectedCommit.subject }}</h4>
            <div class="git-detail-meta">
              <span class="git-detail-meta-item"><User :size="11" aria-hidden="true" />{{ selectedCommit.author }}</span>
              <span class="git-detail-meta-item"><Clock :size="11" aria-hidden="true" />{{ formatDate(selectedCommit.author_date) }}</span>
            </div>
          </div>

          <div v-if="detail?.files?.length" class="git-detail-files">
            <div class="git-detail-section-title">{{ t('context.gitChangedFiles') }} ({{ detail.files.length }})</div>
            <div class="git-detail-file-list">
              <div
                v-for="file in detail.files"
                :key="file.new_path"
                class="git-detail-file-row"
                :class="{ active: selectedFilePath === file.new_path }"
                @click="selectedFilePath = file.new_path; revisionPreview = null"
              >
                <span class="git-detail-file-type">{{ file.status }}</span>
                <span class="git-detail-file-path" :title="file.new_path">{{ file.new_path }}</span>
                <span class="git-detail-file-stats">
                  <span class="stat-add">+{{ file.additions }}</span>
                  <span class="stat-del">-{{ file.deletions }}</span>
                </span>
                <button
                  type="button"
                  class="git-detail-rev-btn"
                  :title="lbl('context.gitViewAtRevision', 'View at Revision')"
                  @click.stop="viewFileAtCommit(file)"
                >
                  <Eye :size="11" aria-hidden="true" />
                </button>
              </div>
            </div>
          </div>

          <pre v-if="revisionPreview" class="git-detail-rev-preview"><code>{{ revisionPreview.isBinary ? t('context.gitBinaryFile') : (revisionPreview.content ?? '') }}</code></pre>
          <DiffViewer v-else-if="activePatchDiff" :diff-text="activePatchDiff" :file-path="selectedFilePath" />
        </div>
      </UiIslandCard>
    </div>
  </div>
</template>

<style scoped>
.git-history-view {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
  height: 100%;
}

.git-history-filters {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.git-history-search-wrap {
  position: relative;
  flex: 1 1 130px;
  min-width: 0;
  display: flex;
  align-items: center;
}

.git-history-search-icon {
  position: absolute;
  left: 8px;
  color: var(--text-muted);
  pointer-events: none;
}

.git-history-search-input {
  width: 100%;
  padding-left: 26px !important;
}

.git-history-branch-select {
  min-width: 110px;
  max-width: 160px;
}

.git-history-author-input {
  width: 110px;
}

.git-history-loading,
.git-history-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 24px 12px;
  color: var(--text-muted);
  font-size: 12px;
}

.git-history-layout {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-height: 0;
  flex: 1;
}

.git-history-list-card {
  max-height: 290px;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.git-history-list {
  height: 100%;
  overflow-y: auto;
}

.git-commit-rows {
  display: flex;
  flex-direction: column;
}

.git-commit-row {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 0 8px;
  min-height: 34px;
  border: none;
  border-bottom: 1px solid color-mix(in srgb, var(--border-muted) 40%, transparent);
  background: transparent;
  color: var(--text-primary);
  text-align: left;
  cursor: pointer;
}

.git-commit-row:hover {
  background: var(--surface-hover);
}

.git-commit-row.active {
  background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-raised));
}

.git-graph-svg {
  flex-shrink: 0;
}

.git-commit-copy {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 1px;
  padding: 4px 0;
}

.git-commit-top {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  flex-wrap: wrap;
}

.git-ref-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 0 5px;
  border-radius: 4px;
  font-size: 10px;
  font-weight: 600;
  background: var(--surface-raised);
  border: 1px solid var(--border-muted);
  color: var(--text-secondary);
}

.git-ref-badge.is-head {
  border-color: var(--accent-primary);
  color: var(--accent-primary);
}

.git-ref-badge.is-tag {
  border-color: var(--accent-warning, #f59e0b);
  color: var(--accent-warning, #f59e0b);
}

.git-commit-subject {
  font-size: 12px;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-commit-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 10px;
  color: var(--text-muted);
}

.git-commit-hash {
  font-family: var(--font-mono);
}

.git-commit-date {
  margin-left: auto;
}

.git-load-more {
  display: flex;
  justify-content: center;
  padding: 8px;
}

.git-history-detail-card {
  padding: 10px;
  overflow: auto;
}

.git-detail-loading {
  display: flex;
  justify-content: center;
  padding: 16px;
}

.git-detail-content {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.git-detail-hash-row {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--text-secondary);
}

.git-detail-subject {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.git-detail-meta {
  display: flex;
  gap: 10px;
  font-size: 11px;
  color: var(--text-secondary);
}

.git-detail-meta-item {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.git-detail-files {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.git-detail-section-title {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-secondary);
}

.git-detail-file-list {
  display: flex;
  flex-direction: column;
  gap: 3px;
  max-height: 150px;
  overflow-y: auto;
}

.git-detail-file-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 6px;
  border-radius: 5px;
  font-size: 11px;
  cursor: pointer;
}

.git-detail-file-row:hover,
.git-detail-file-row.active {
  background: var(--surface-hover);
}

.git-detail-file-type {
  font-family: var(--font-mono);
  font-weight: 700;
  color: var(--accent-primary);
}

.git-detail-file-path {
  flex: 1;
  min-width: 0;
  font-family: var(--font-mono);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-detail-file-stats {
  display: inline-flex;
  gap: 4px;
  font-family: var(--font-mono);
  font-size: 10px;
}

.stat-add { color: var(--accent-success, #10b981); }
.stat-del { color: var(--accent-danger, #ef4444); }

.git-detail-rev-btn {
  display: grid;
  place-items: center;
  padding: 2px 4px;
  border-radius: 4px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.git-detail-rev-preview {
  margin: 0;
  padding: 8px;
  border-radius: 6px;
  background: var(--surface-section);
  font-size: 11px;
  max-height: 220px;
  overflow: auto;
}

.spinning {
  animation: history-spin 1s linear infinite;
}

@keyframes history-spin {
  to { transform: rotate(360deg); }
}
</style>
