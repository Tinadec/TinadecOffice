<script setup lang="ts">
import { Archive, ArchiveRestore, GitBranch, Loader2, Play, Plus, RefreshCw, Trash2 } from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNotifications } from '@/composables/useNotifications'
import { UiBadge, UiCheckbox, UiInput } from '@/components/ui'
import DiffViewer from './DiffViewer.vue'
import {
  fetchGitStashList,
  fetchGitStashShow,
  patchFileToUnifiedDiff,
  type GitStashEntry,
  type GitStashShowResponse,
} from './gitReadClient'

const props = defineProps<{
  cwd: string | undefined
  operationLoading: boolean
}>()

const emit = defineEmits<{
  'stash-push': [payload: { message: string; includeUntracked: boolean }]
  'stash-apply': [stashRef: string]
  'stash-pop': [stashRef: string]
  'stash-drop': [stashRef: string, reload: () => Promise<void>]
}>()

const { t, te } = useI18n()
const { notify } = useNotifications()

const loading = ref(false)
const showLoading = ref(false)
const stashes = ref<GitStashEntry[]>([])
const selectedRef = ref<string | null>(null)
const detail = ref<GitStashShowResponse | null>(null)
const message = ref('')
const includeUntracked = ref(false)

const lbl = (key: string, fallback: string) => (typeof te === 'function' && te(key) ? t(key) : fallback)

async function loadStashes(): Promise<void> {
  if (!props.cwd) {
    stashes.value = []
    return
  }
  loading.value = true
  try {
    const res = await fetchGitStashList(props.cwd)
    stashes.value = Array.isArray(res?.entries) ? res.entries : []
    if (selectedRef.value && !stashes.value.some((s) => s.ref_name === selectedRef.value)) {
      selectedRef.value = null
      detail.value = null
    }
  } catch (err) {
    stashes.value = []
    notify.error(err, { title: t('context.gitLoadFailed'), source: 'git', key: 'git-stash-list' })
  } finally {
    loading.value = false
  }
}

async function selectStash(entry: GitStashEntry): Promise<void> {
  if (!props.cwd) return
  if (selectedRef.value === entry.ref_name && detail.value) {
    selectedRef.value = null
    detail.value = null
    return
  }
  selectedRef.value = entry.ref_name
  showLoading.value = true
  detail.value = null
  try {
    detail.value = await fetchGitStashShow(props.cwd, entry.ref_name)
  } catch (err) {
    notify.error(err, { title: t('context.gitLoadFailed'), source: 'git', key: 'git-stash-show' })
  } finally {
    showLoading.value = false
  }
}

function submitPush(): void {
  emit('stash-push', {
    message: message.value.trim(),
    includeUntracked: includeUntracked.value,
  })
  message.value = ''
}

const combinedDiff = computed(() => {
  if (!detail.value) return ''
  if (Array.isArray(detail.value.patches) && detail.value.patches.length > 0) {
    return detail.value.patches.map((p) => patchFileToUnifiedDiff(p)).join('\n')
  }
  return ''
})

watch(() => props.cwd, () => {
  selectedRef.value = null
  detail.value = null
  void loadStashes()
}, { immediate: true })

defineExpose({ reload: loadStashes })
</script>

<template>
  <div class="git-stash-view" data-testid="git-stash-view">
    <form class="git-stash-form" @submit.prevent="submitPush">
      <div class="git-stash-form-row">
        <UiInput
          v-model="message"
          class="git-stash-input"
          :placeholder="lbl('context.gitStashMessagePlaceholder', 'Optional stash message')"
          :disabled="operationLoading || !cwd"
        />
        <button
          type="submit"
          class="git-stash-push-btn"
          :disabled="operationLoading || !cwd"
          data-testid="git-stash-push-btn"
        >
          <Plus :size="13" aria-hidden="true" />
          <span>{{ lbl('context.gitStashPush', 'Stash Changes') }}</span>
        </button>
      </div>
      <div class="git-stash-form-meta">
        <label class="git-stash-checkbox">
          <UiCheckbox v-model="includeUntracked" :disabled="operationLoading || !cwd" />
          <span>{{ lbl('context.gitStashIncludeUntracked', 'Include untracked') }}</span>
        </label>
        <button
          type="button"
          class="git-stash-refresh-btn"
          :title="t('common.refresh')"
          :disabled="loading || !cwd"
          @click="loadStashes"
        >
          <RefreshCw :size="13" :class="{ spinning: loading }" aria-hidden="true" />
        </button>
      </div>
    </form>

    <div v-if="loading && stashes.length === 0" class="git-stash-empty">
      <Loader2 :size="18" class="spinning" aria-hidden="true" />
    </div>

    <div v-else-if="stashes.length === 0" class="git-stash-empty" data-testid="git-stash-empty">
      <Archive :size="20" aria-hidden="true" />
      <span>{{ lbl('context.gitStashEmpty', 'No stashes saved.') }}</span>
    </div>

    <ul v-else class="git-stash-list">
      <li
        v-for="entry in stashes"
        :key="entry.ref_name"
        class="git-stash-item"
        :class="{ active: selectedRef === entry.ref_name }"
        data-testid="git-stash-item"
      >
        <div class="git-stash-row" @click="selectStash(entry)">
          <div class="git-stash-row-copy">
            <div class="git-stash-row-top">
              <code class="git-stash-ref">{{ entry.ref_name }}</code>
              <UiBadge v-if="entry.branch" variant="outline" class="git-stash-branch">
                <GitBranch :size="10" aria-hidden="true" />
                <span>{{ entry.branch }}</span>
              </UiBadge>
              <span v-if="entry.created_at" class="git-stash-date">{{ entry.created_at }}</span>
            </div>
            <strong class="git-stash-msg">{{ entry.subject || entry.message || entry.ref_name }}</strong>
          </div>
          <div class="git-stash-actions" @click.stop>
            <button
              type="button"
              class="git-stash-act-btn"
              :title="lbl('context.gitStashApply', 'Apply')"
              :disabled="operationLoading"
              data-testid="git-stash-apply-btn"
              @click="emit('stash-apply', entry.ref_name)"
            >
              <Play :size="11" aria-hidden="true" />
              <span>{{ lbl('context.gitStashApply', 'Apply') }}</span>
            </button>
            <button
              type="button"
              class="git-stash-act-btn"
              :title="lbl('context.gitStashPop', 'Pop')"
              :disabled="operationLoading"
              data-testid="git-stash-pop-btn"
              @click="emit('stash-pop', entry.ref_name)"
            >
              <ArchiveRestore :size="11" aria-hidden="true" />
              <span>{{ lbl('context.gitStashPop', 'Pop') }}</span>
            </button>
            <button
              type="button"
              class="git-stash-act-btn is-danger"
              :title="lbl('context.gitStashDrop', 'Drop')"
              :disabled="operationLoading"
              data-testid="git-stash-drop-btn"
              @click="emit('stash-drop', entry.ref_name, loadStashes)"
            >
              <Trash2 :size="11" aria-hidden="true" />
            </button>
          </div>
        </div>

        <div v-if="selectedRef === entry.ref_name" class="git-stash-detail">
          <div v-if="showLoading" class="git-stash-detail-loading">
            <Loader2 :size="15" class="spinning" aria-hidden="true" />
          </div>
          <template v-else-if="detail">
            <ul v-if="detail.files?.length" class="git-stash-files">
              <li v-for="file in detail.files" :key="file.new_path" class="git-stash-file">
                <span class="git-stash-file-status">{{ file.status || 'M' }}</span>
                <span class="git-stash-file-path">{{ file.new_path }}</span>
              </li>
            </ul>
            <DiffViewer
              v-if="combinedDiff"
              :diff-text="combinedDiff"
              :file-path="detail.files?.[0]?.new_path ?? entry.ref_name"
            />
          </template>
        </div>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.git-stash-view {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.git-stash-form {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px;
  border-radius: 8px;
  border: 1px solid var(--border-card);
  background: var(--surface-section);
}

.git-stash-form-row {
  display: flex;
  gap: 6px;
  align-items: center;
}

.git-stash-input {
  flex: 1;
  min-width: 0;
}

.git-stash-push-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 5px 10px;
  border-radius: 6px;
  border: 1px solid var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 16%, var(--surface-raised));
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
}

.git-stash-push-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.git-stash-form-meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.git-stash-checkbox {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: var(--text-secondary);
  cursor: pointer;
}

.git-stash-refresh-btn {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.git-stash-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 6px;
  padding: 20px 10px;
  color: var(--text-muted);
  font-size: 12px;
}

.git-stash-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.git-stash-item {
  border-radius: 8px;
  border: 1px solid var(--border-card);
  background: var(--surface-section);
  overflow: hidden;
}

.git-stash-item.active {
  border-color: var(--accent-primary);
}

.git-stash-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 10px;
  cursor: pointer;
  flex-wrap: wrap;
}

.git-stash-row:hover {
  background: var(--surface-hover);
}

.git-stash-row-copy {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  flex: 1 1 150px;
}

.git-stash-row-top {
  display: flex;
  align-items: center;
  gap: 5px;
  flex-wrap: wrap;
}

.git-stash-ref {
  font-size: 10px;
  padding: 1px 5px;
  border-radius: 4px;
  background: var(--surface-raised);
  color: var(--accent-primary);
}

.git-stash-branch {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 10px;
}

.git-stash-date {
  font-size: 10px;
  color: var(--text-muted);
}

.git-stash-msg {
  font-size: 12px;
  font-weight: 500;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.git-stash-actions {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.git-stash-act-btn {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 3px 7px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 11px;
  cursor: pointer;
}

.git-stash-act-btn:hover:not(:disabled) {
  background: var(--surface-hover);
}

.git-stash-act-btn.is-danger:hover:not(:disabled) {
  color: var(--accent-danger);
  border-color: color-mix(in srgb, var(--accent-danger) 50%, transparent);
}

.git-stash-detail {
  border-top: 1px solid var(--border-muted);
  padding: 8px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.git-stash-detail-loading {
  display: flex;
  justify-content: center;
  padding: 12px;
}

.git-stash-files {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 3px;
  font-size: 11px;
}

.git-stash-file {
  display: flex;
  gap: 6px;
  align-items: center;
}

.git-stash-file-status {
  font-family: var(--font-mono);
  font-weight: 700;
  color: var(--accent-primary);
}

.git-stash-file-path {
  font-family: var(--font-mono);
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.spinning {
  animation: stash-spin 1s linear infinite;
}

@keyframes stash-spin {
  to { transform: rotate(360deg); }
}
</style>
