<script setup lang="ts">
import { GitCompare, RefreshCw } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type CodeToolExecuteResultDto } from '@/api'
import { UiBadge, UiButton, UiScrollArea } from '@/components/ui'
import DiffViewer from './DiffViewer.vue'
import { buildDiffEntries, type DiffFileEntry } from './diffUtils'

interface LogCommit {
  hash: string
  short_hash: string
  author: string
  email: string
  date: string
  subject: string
}

interface LogResult {
  action: 'log'
  ref: string
  limit: number
  commits: LogCommit[]
}

interface DiffFileSummary {
  path: string
  previous_path?: string | null
  change_type: string
  additions: number
  deletions: number
  binary: boolean
  truncated: boolean
}

interface DiffCompareResult {
  action: 'diff_compare'
  base_ref: string
  head_ref: string
  diff_stat?: string | null
  diff?: string | null
  truncated?: boolean
  files?: DiffFileSummary[]
  file_count?: number
  commits?: string[]
}

interface Props {
  cwd: string
  /** Optional initial refs. */
  defaultBaseRef?: string | null
  defaultHeadRef?: string | null
}

const props = defineProps<Props>()
const { t } = useI18n()

const baseRef = ref(props.defaultBaseRef ?? 'HEAD~1')
const headRef = ref(props.defaultHeadRef ?? 'HEAD')
const baseMenuOpen = ref(false)
const headMenuOpen = ref(false)
const commits = ref<LogCommit[]>([])
const loadingLog = ref(false)
const comparing = ref(false)
const error = ref<string | null>(null)
const result = ref<DiffCompareResult | null>(null)
const selectedFilePath = ref<string | null>(null)

const canCompare = computed(() => Boolean(props.cwd && baseRef.value.trim() && headRef.value.trim()))

const entries = computed<DiffFileEntry[]>(() => {
  if (!result.value?.diff) return []
  return buildDiffEntries(result.value.diff, result.value.files)
})

const resultCommits = computed(() => result.value?.commits ?? [])

watch(entries, (next) => {
  if (!next.some((entry) => entry.path === selectedFilePath.value)) {
    selectedFilePath.value = next[0]?.path ?? null
  }
})

async function loadLog() {
  if (!props.cwd) return
  loadingLog.value = true
  error.value = null
  try {
    const res = await api.gitLog(props.cwd, 50)
    const data = (res.data ?? {}) as Partial<LogResult>
    commits.value = Array.isArray(data.commits) ? data.commits : []
    if (commits.value.length > 0 && !baseRef.value) baseRef.value = commits.value[Math.min(1, commits.value.length - 1)]?.short_hash ?? 'HEAD~1'
    if (commits.value.length > 0 && !headRef.value) headRef.value = commits.value[0]?.short_hash ?? 'HEAD'
  } catch (err) {
    error.value = err instanceof Error ? err.message : t('context.gitLoadFailed')
  } finally {
    loadingLog.value = false
  }
}

async function runCompare() {
  if (!canCompare.value) return
  comparing.value = true
  error.value = null
  result.value = null
  try {
    const res = await api.gitDiffCompare(props.cwd, baseRef.value.trim(), headRef.value.trim())
    result.value = (res.data ?? {}) as unknown as DiffCompareResult
    selectedFilePath.value = entries.value[0]?.path ?? null
  } catch (err) {
    error.value = err instanceof Error ? err.message : t('context.gitLoadFailed')
  } finally {
    comparing.value = false
  }
}

function pickBase(commit: LogCommit) {
  baseRef.value = commit.short_hash
  baseMenuOpen.value = false
}

function pickHead(commit: LogCommit) {
  headRef.value = commit.short_hash
  headMenuOpen.value = false
}

function refresh() {
  void loadLog()
}

watch(
  () => props.cwd,
  () => {
    result.value = null
    void loadLog()
  },
  { immediate: true }
)
</script>

<template>
  <section class="commit-compare">
    <div class="commit-compare-head">
      <div class="commit-compare-title">
        <GitCompare :size="14" />
        <span>{{ t('context.gitTabCompare') }}</span>
      </div>
      <button
        type="button"
        class="icon-button"
        :title="t('context.refreshGitPlan')"
        :disabled="loadingLog"
        @click="refresh"
      >
        <RefreshCw :size="13" />
      </button>
    </div>

    <div class="commit-compare-refs">
      <div class="commit-compare-ref">
        <label>{{ t('context.gitCompareBase') }}</label>
        <div class="commit-compare-ref-input">
          <input
            v-model="baseRef"
            type="text"
            :placeholder="'HEAD~1 / branch / hash'"
          />
          <button
            type="button"
            class="commit-compare-ref-toggle"
            :disabled="commits.length === 0"
            @click="baseMenuOpen = !baseMenuOpen"
          >
            ▾
          </button>
        </div>
        <div v-if="baseMenuOpen" class="commit-compare-ref-menu">
          <button
            v-for="commit in commits"
            :key="commit.hash"
            type="button"
            class="commit-compare-ref-option"
            @click="pickBase(commit)"
          >
            <code>{{ commit.short_hash }}</code>
            <span class="truncate">{{ commit.subject }}</span>
          </button>
        </div>
      </div>

      <div class="commit-compare-ref">
        <label>{{ t('context.gitCompareHead') }}</label>
        <div class="commit-compare-ref-input">
          <input
            v-model="headRef"
            type="text"
            :placeholder="'HEAD / branch / hash'"
          />
          <button
            type="button"
            class="commit-compare-ref-toggle"
            :disabled="commits.length === 0"
            @click="headMenuOpen = !headMenuOpen"
          >
            ▾
          </button>
        </div>
        <div v-if="headMenuOpen" class="commit-compare-ref-menu">
          <button
            v-for="commit in commits"
            :key="commit.hash"
            type="button"
            class="commit-compare-ref-option"
            @click="pickHead(commit)"
          >
            <code>{{ commit.short_hash }}</code>
            <span class="truncate">{{ commit.subject }}</span>
          </button>
        </div>
      </div>

      <UiButton
        variant="secondary"
        size="sm"
        :disabled="!canCompare || comparing"
        @click="runCompare"
      >
        <GitCompare :size="14" />
        <span>{{ t('context.gitCompareButton') }}</span>
      </UiButton>
    </div>

    <div v-if="error" class="commit-compare-error">{{ error }}</div>

    <template v-if="result">
      <div v-if="result.truncated" class="commit-compare-notice">
        {{ t('context.gitCompareTruncated') }}
      </div>

      <section v-if="resultCommits.length > 0" class="commit-compare-block">
        <div class="commit-compare-block-title">
          {{ t('context.gitCompareCommits') }}
          <UiBadge variant="secondary">{{ resultCommits.length }}</UiBadge>
        </div>
        <UiScrollArea class="commit-compare-scroll">
          <div
            v-for="(line, index) in resultCommits"
            :key="index"
            class="commit-compare-commit"
          >
            <code>{{ line }}</code>
          </div>
        </UiScrollArea>
      </section>

      <section v-if="entries.length > 0" class="commit-compare-block">
        <div class="commit-compare-block-title">
          {{ t('context.gitCompareFiles') }}
          <UiBadge variant="secondary">{{ entries.length }}</UiBadge>
        </div>
        <div class="commit-compare-files">
          <button
            v-for="entry in entries"
            :key="entry.path"
            type="button"
            class="commit-compare-file"
            :class="{ active: entry.path === selectedFilePath }"
            @click="selectedFilePath = entry.path"
          >
            <span class="truncate">{{ entry.path }}</span>
            <small>+{{ entry.additions ?? 0 }} -{{ entry.deletions ?? 0 }}</small>
          </button>
        </div>
      </section>

      <section v-if="entries.length > 0" class="commit-compare-diff">
        <DiffViewer
          :files="entries"
          :selected-file-path="selectedFilePath"
          @update:selected-file-path="selectedFilePath = $event"
        />
      </section>

      <div v-else-if="!error" class="commit-compare-empty">
        {{ t('context.gitNoDiff') }}
      </div>
    </template>

    <div v-else-if="comparing" class="commit-compare-empty">
      {{ t('context.loadingGitPlan') }}
    </div>
    <div v-else-if="!error" class="commit-compare-empty">
      {{ t('context.gitCompareHint') }}
    </div>
  </section>
</template>

<style scoped>
.commit-compare {
  display: grid;
  gap: 10px;
}

.commit-compare-head,
.commit-compare-block-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.commit-compare-title {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
}

.commit-compare-refs {
  display: grid;
  grid-template-columns: 1fr 1fr auto;
  gap: 8px;
  align-items: end;
}

.commit-compare-ref {
  position: relative;
  display: grid;
  gap: 4px;
  min-width: 0;
}

.commit-compare-ref label {
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 700;
}

.commit-compare-ref-input {
  display: grid;
  grid-template-columns: 1fr 28px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  overflow: hidden;
  background: var(--bg-primary);
}

.commit-compare-ref-input input {
  border: 0;
  background: transparent;
  padding: 6px 8px;
  color: var(--text-primary);
  font-size: 12px;
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.commit-compare-ref-input input:focus {
  outline: none;
}

.commit-compare-ref-toggle {
  display: flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-left: 1px solid var(--border-muted);
  background: var(--bg-secondary);
  color: var(--text-secondary);
  cursor: pointer;
  font-size: 12px;
}

.commit-compare-ref-toggle:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.commit-compare-ref-menu {
  position: absolute;
  z-index: 50;
  top: 100%;
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

.commit-compare-ref-option {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 5px 8px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.commit-compare-ref-option:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.commit-compare-ref-option code {
  color: var(--text-brand);
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 11px;
}

.commit-compare-error {
  padding: 8px 10px;
  color: var(--text-reject, #f85149);
  background: var(--bg-status-warn);
  border: 1px solid rgba(248, 81, 73, 0.25);
  border-radius: 6px;
  font-size: 12px;
}

.commit-compare-notice {
  padding: 6px 10px;
  color: var(--text-secondary);
  background: var(--bg-status-warn);
  border: 1px solid rgba(210, 153, 34, 0.25);
  border-radius: 6px;
  font-size: 12px;
}

.commit-compare-block {
  display: grid;
  gap: 6px;
  padding: 8px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
}

.commit-compare-block-title {
  justify-content: flex-start;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
}

.commit-compare-scroll {
  max-height: 160px;
}

.commit-compare-commit code {
  display: block;
  color: var(--text-secondary);
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 11px;
  padding: 2px 0;
  white-space: pre-wrap;
  word-break: break-word;
}

.commit-compare-files {
  display: grid;
  gap: 4px;
  max-height: 200px;
  overflow: auto;
}

.commit-compare-file {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  width: 100%;
  padding: 6px 8px;
  border: 1px solid transparent;
  border-radius: 4px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.commit-compare-file:hover,
.commit-compare-file.active {
  color: var(--text-primary);
  background: var(--bg-hover);
  border-color: var(--bg-selected-outline);
}

.commit-compare-file small {
  color: var(--text-muted);
  font-size: 10px;
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.commit-compare-diff {
  min-height: 320px;
  height: 480px;
}

.commit-compare-empty {
  padding: 16px;
  color: var(--text-muted);
  font-size: 12px;
  text-align: center;
  border: 1px dashed var(--border-muted);
  border-radius: 8px;
}
</style>
