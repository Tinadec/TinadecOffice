<script setup lang="ts">
import { AlertTriangle, FolderTree, GitBranch, Plus, RefreshCw, Trash2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api } from '@/api'
import { UiBadge, UiButton } from '@/components/ui'

interface Worktree {
  path: string
  branch?: string | null
  detached?: string | null
  head?: string | null
  commit?: string | null
  is_current?: boolean
  is_main?: boolean
}

interface Props {
  cwd: string
  /** Current working directory path, used to mark the active worktree. */
  currentPath?: string | null
}

const props = defineProps<Props>()

const emit = defineEmits<{
  switch: [path: string]
  create: [payload: { branch: string; path: string }]
  remove: [path: string]
}>()

const { t } = useI18n()

const worktrees = ref<Worktree[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const showCreateForm = ref(false)
const newBranch = ref('')
const newPath = ref('')
const blockedNotice = ref<string | null>(null)

const normalized = computed<Worktree[]>(() => {
  return worktrees.value.map((entry) => {
    const path = String(entry.path ?? '')
    const isCurrent = entry.is_current === true || Boolean(props.currentPath && path === props.currentPath)
    return {
      path,
      branch: entry.branch ?? null,
      detached: entry.detached ?? null,
      head: entry.head ?? entry.commit ?? null,
      is_current: isCurrent,
      is_main: entry.is_main === true
    }
  })
})

async function loadWorktrees() {
  if (!props.cwd) return
  loading.value = true
  error.value = null
  blockedNotice.value = null
  try {
    const res = await api.executeCodeTool('git_worktree_manager', {
      cwd: props.cwd,
      arguments: { action: 'worktrees' }
    })
    if (res.status === 'blocked') {
      blockedNotice.value = res.summary || t('context.gitWorktreeNeedsApproval')
    }
    const data = (res.data ?? {}) as { worktrees?: unknown }
    worktrees.value = Array.isArray(data.worktrees)
      ? (data.worktrees as Worktree[]).map((item) => normalizeWorktree(item))
      : []
  } catch (err) {
    error.value = err instanceof Error ? err.message : t('context.gitLoadFailed')
  } finally {
    loading.value = false
  }
}

function normalizeWorktree(item: unknown): Worktree {
  if (item && typeof item === 'object') {
    const record = item as Record<string, unknown>
    return {
      path: String(record.path ?? record.worktree ?? ''),
      branch: typeof record.branch === 'string' ? record.branch : null,
      detached: typeof record.detached === 'string' ? record.detached : null,
      head: typeof record.head === 'string' ? record.head : typeof record.commit === 'string' ? record.commit : null,
      is_current: record.is_current === true || record.current === true,
      is_main: record.is_main === true || record.main === true
    }
  }
  return { path: String(item) }
}

function switchTo(path: string) {
  emit('switch', path)
}

function removeWorktree(path: string) {
  emit('remove', path)
}

function submitCreate() {
  if (!newBranch.value.trim() || !newPath.value.trim()) return
  emit('create', { branch: newBranch.value.trim(), path: newPath.value.trim() })
  newBranch.value = ''
  newPath.value = ''
  showCreateForm.value = false
}

function refresh() {
  void loadWorktrees()
}

watch(
  () => props.cwd,
  () => {
    void loadWorktrees()
  },
  { immediate: true }
)

watch(newBranch, (value) => {
  if (!newPath.value || newPath.value.startsWith('.tinadec/worktrees/')) {
    const slug = value.trim().replace(/[^A-Za-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '')
    newPath.value = slug ? `.tinadec/worktrees/${slug}` : ''
  }
})
</script>

<template>
  <section class="worktree-manager">
    <div class="worktree-manager-head">
      <div class="worktree-manager-title">
        <FolderTree :size="14" />
        <span>{{ t('context.gitTabWorktrees') }}</span>
      </div>
      <div class="worktree-manager-head-actions">
        <button
          type="button"
          class="icon-button"
          :title="t('context.refreshGitPlan')"
          :disabled="loading"
          @click="refresh"
        >
          <RefreshCw :size="13" />
        </button>
        <UiButton
          variant="secondary"
          size="xs"
          @click="showCreateForm = !showCreateForm"
        >
          <Plus :size="12" />
          <span>{{ t('context.gitWorktreeCreate') }}</span>
        </UiButton>
      </div>
    </div>

    <div v-if="error" class="worktree-manager-error">{{ error }}</div>
    <div v-else-if="loading && worktrees.length === 0" class="worktree-manager-empty">
      {{ t('context.loadingGitPlan') }}
    </div>

    <div v-if="blockedNotice" class="worktree-manager-notice">
      <AlertTriangle :size="13" />
      <span>{{ blockedNotice }}</span>
    </div>

    <div v-if="showCreateForm" class="worktree-manager-form">
      <label>
        <span>{{ t('context.gitWorktreeBranch') }}</span>
        <input
          v-model="newBranch"
          type="text"
          :placeholder="'feature/xyz'"
        />
      </label>
      <label>
        <span>{{ t('context.gitWorktreePath') }}</span>
        <input
          v-model="newPath"
          type="text"
          :placeholder="'../repo-feature-xyz'"
        />
      </label>
      <div class="worktree-manager-form-actions">
        <UiButton
          variant="secondary"
          size="sm"
          :disabled="!newBranch.trim() || !newPath.trim()"
          @click="submitCreate"
        >
          {{ t('context.gitWorktreeCreate') }}
        </UiButton>
        <UiButton variant="ghost" size="sm" @click="showCreateForm = false">
          {{ t('context.gitCompareCancel') }}
        </UiButton>
      </div>
      <small class="worktree-manager-form-hint">{{ t('context.gitWorktreeNeedsApproval') }}</small>
    </div>

    <div v-if="normalized.length > 0" class="worktree-manager-list">
      <div
        v-for="worktree in normalized"
        :key="worktree.path"
        class="worktree-row"
        :class="{ current: worktree.is_current }"
      >
        <div class="worktree-row-head">
          <GitBranch :size="13" />
          <strong>{{ worktree.branch ?? worktree.detached ?? t('context.gitWorktreeDetached') }}</strong>
          <UiBadge v-if="worktree.is_current" variant="secondary">{{ t('context.gitWorktreeCurrent') }}</UiBadge>
          <UiBadge v-else-if="worktree.is_main" variant="outline">main</UiBadge>
        </div>
        <div class="worktree-row-path">{{ worktree.path }}</div>
        <div v-if="worktree.head" class="worktree-row-head-commit">
          <code>{{ worktree.head }}</code>
        </div>
        <div class="worktree-row-actions">
          <button
            type="button"
            class="secondary-button worktree-action"
            :disabled="worktree.is_current"
            @click="switchTo(worktree.path)"
          >
            {{ t('context.gitWorktreeSwitch') }}
          </button>
          <button
            type="button"
            class="icon-button worktree-action-icon"
            :disabled="worktree.is_current || worktree.is_main"
            :title="t('context.gitWorktreeRemove')"
            @click="removeWorktree(worktree.path)"
          >
            <Trash2 :size="13" />
          </button>
        </div>
      </div>
    </div>

    <div v-else-if="!loading && !error" class="worktree-manager-empty">
      {{ t('context.gitWorktreeEmpty') }}
    </div>
  </section>
</template>

<style scoped>
.worktree-manager {
  display: grid;
  gap: 10px;
}

.worktree-manager-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.worktree-manager-title {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
}

.worktree-manager-head-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.worktree-manager-error {
  padding: 8px 10px;
  color: var(--text-reject, #f85149);
  background: var(--bg-status-warn);
  border: 1px solid rgba(248, 81, 73, 0.25);
  border-radius: 6px;
  font-size: 12px;
}

.worktree-manager-notice {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  color: var(--text-secondary);
  background: var(--bg-status-warn);
  border: 1px solid rgba(210, 153, 34, 0.25);
  border-radius: 6px;
  font-size: 12px;
}

.worktree-manager-empty {
  padding: 16px;
  color: var(--text-muted);
  font-size: 12px;
  text-align: center;
  border: 1px dashed var(--border-muted);
  border-radius: 8px;
}

.worktree-manager-form {
  display: grid;
  gap: 8px;
  padding: 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
}

.worktree-manager-form label {
  display: grid;
  gap: 4px;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 700;
}

.worktree-manager-form input {
  width: 100%;
  padding: 6px 8px;
  color: var(--text-primary);
  background: var(--bg-primary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  font-size: 12px;
  font-family: 'Geist Mono', ui-monospace, monospace;
}

.worktree-manager-form input:focus {
  outline: none;
  border-color: var(--border-input-focus);
  box-shadow: var(--shadow-focus);
}

.worktree-manager-form-actions {
  display: flex;
  gap: 6px;
}

.worktree-manager-form-hint {
  color: var(--text-muted);
  font-size: 10px;
}

.worktree-manager-list {
  display: grid;
  gap: 8px;
}

.worktree-row {
  display: grid;
  gap: 4px;
  padding: 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
}

.worktree-row.current {
  border-color: var(--bg-selected-outline);
  background: var(--bg-selected);
}

.worktree-row-head {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--text-primary);
}

.worktree-row-head strong {
  font-size: 12px;
}

.worktree-row-path {
  color: var(--text-secondary);
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 11px;
  word-break: break-all;
}

.worktree-row-head-commit code {
  color: var(--text-muted);
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 10px;
}

.worktree-row-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
}

.worktree-action {
  height: 26px;
  width: auto;
  padding: 0 10px;
  font-size: 11px;
}

.worktree-action-icon {
  width: 26px;
  height: 26px;
}
</style>
