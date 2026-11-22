<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Camera, RotateCcw } from '@lucide/vue'
import {
  api as baseApi,
  type SnapshotDto,
  type WorkspaceFileChangeDto,
  type WorkspaceFileDiffDto,
} from '@/api'
import { homeController } from '@/controllers/HomeController'
import DiffViewer from '@/components/git/DiffViewer.vue'
import { UiSelectField } from '@/components/ui'
import { projectStorageId, scopedApi, selectionKey } from '@/lib/storageScope'

const props = defineProps<{
  projectId?: string | null
  compact?: boolean
}>()

const { t, locale } = useI18n()

const snapshots = ref<SnapshotDto[]>([])
const selectedProjectId = ref<string>(props.projectId ?? '')
const api = scopedApi(baseApi, () => projectStorageId(selectedProjectId.value))
const loading = ref(false)
const loadError = ref<string | null>(null)
const restoring = ref<string | null>(null)

let snapshotEpoch = 0
let changesEpoch = 0

type RestoreResult = {
  status?: string
  applied_file_count?: number
  workspace_hash?: string
  conflicts?: unknown[]
}
const lastRestore = ref<{ snapshotId: string; result: RestoreResult } | null>(null)

const projects = computed(() => homeController.projects.value)
const sorted = computed(() =>
  [...snapshots.value].sort((a, b) => b.created_at.localeCompare(a.created_at)),
)

async function load(): Promise<void> {
  const projectId = selectedProjectId.value
  if (!projectId) return
  const epoch = ++snapshotEpoch
  loading.value = true
  loadError.value = null
  try {
    const rows = await api.listWorkspaceSnapshots(projectId)
    if (epoch !== snapshotEpoch) return
    snapshots.value = rows
  } catch (e) {
    if (epoch !== snapshotEpoch) return
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    if (epoch === snapshotEpoch) loading.value = false
  }
}

function selectProject(id: string): void {
  selectedProjectId.value = id
  snapshotEpoch += 1
  snapshots.value = []
  lastRestore.value = null
  void load()
}

async function restore(snapshot: SnapshotDto): Promise<void> {
  restoring.value = snapshot.id
  try {
    const idempotencyKey = `desktop:snapshot-restore:${snapshot.id}:${snapshot.workspace_hash}`
    const result = await api.restoreWorkspaceSnapshot(snapshot.id, { idempotency_key: idempotencyKey })
    lastRestore.value = { snapshotId: snapshot.id, result: result as RestoreResult }
    await load()
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    restoring.value = null
  }
}

onMounted(() => {
  if (!projects.value?.length && typeof homeController.start === 'function') {
    void homeController.start()
  }
})

watch(
  () => {
    if (props.projectId) return props.projectId
    return homeController.currentProject?.value
      ? selectionKey(homeController.currentProject.value)
      : homeController.selectedProjectId.value ?? (projects.value[0] ? selectionKey(projects.value[0]) : null)
  },
  (id) => {
    if (id && id !== selectedProjectId.value) selectProject(id)
  },
  { immediate: true },
)

const reviewSnapshotId = ref<string | null>(null)
const changes = ref<WorkspaceFileChangeDto[]>([])
const changesLoading = ref(false)
const reviewError = ref<string | null>(null)
const openDiffPaths = ref<string[]>([])
const diffs = ref<Record<string, WorkspaceFileDiffDto>>({})
const undoingPath = ref<string | null>(null)

const reviewedChanges = computed(() => changes.value.filter(change => change.status !== 'unchanged'))

function describeError(e: unknown): string {
  return e instanceof Error ? e.message : String(e)
}

async function loadChanges(): Promise<void> {
  const snapshotId = reviewSnapshotId.value
  if (!snapshotId) return
  const epoch = ++changesEpoch
  const rows = await api.listWorkspaceSnapshotFiles(snapshotId)
  if (epoch !== changesEpoch) return
  changes.value = rows
}

async function toggleReview(snapshot: SnapshotDto): Promise<void> {
  if (reviewSnapshotId.value === snapshot.id) {
    reviewSnapshotId.value = null
    changesEpoch += 1
    changes.value = []
    diffs.value = {}
    openDiffPaths.value = []
    return
  }
  reviewSnapshotId.value = snapshot.id
  changesEpoch += 1
  changes.value = []
  diffs.value = {}
  openDiffPaths.value = []
  reviewError.value = null
  changesLoading.value = true
  try {
    await loadChanges()
  } catch (e) {
    reviewError.value = describeError(e)
  } finally {
    changesLoading.value = false
  }
}

async function toggleDiff(change: WorkspaceFileChangeDto): Promise<void> {
  if (openDiffPaths.value.includes(change.path)) {
    openDiffPaths.value = openDiffPaths.value.filter(path => path !== change.path)
    return
  }
  openDiffPaths.value = [...openDiffPaths.value, change.path]
  if (diffs.value[change.path] || !reviewSnapshotId.value) return
  try {
    const diff = await api.getWorkspaceSnapshotFileDiff(reviewSnapshotId.value, change.path)
    diffs.value = { ...diffs.value, [change.path]: diff }
  } catch (e) {
    reviewError.value = describeError(e)
  }
}

async function undoFile(change: WorkspaceFileChangeDto): Promise<void> {
  if (!reviewSnapshotId.value) return
  undoingPath.value = change.path
  reviewError.value = null
  try {
    await api.restoreWorkspaceSnapshotFile(reviewSnapshotId.value, change.path, change.after_sha256 ?? '')
    await loadChanges()
  } catch (e) {
    reviewError.value = describeError(e)
  } finally {
    undoingPath.value = null
  }
}
</script>

<template>
  <div class="snapshot-subpanel" :class="{ 'is-compact': compact }" data-testid="snapshot-subpanel">
    <header class="snapshot-subpanel__header">
      <div class="snapshot-subpanel__title">
        <Camera class="size-3.5" aria-hidden="true" />
        <span>{{ t('governance.snapshotsTitle') }}</span>
      </div>
      <div class="snapshot-subpanel__controls">
        <UiSelectField
          v-if="projects.length > 0"
          class="snapshot-subpanel__select"
          data-testid="snapshot-project-select"
          :value="selectedProjectId"
          :aria-label="t('governance.snapshotsTitle')"
          :options="projects.map((p) => ({ value: selectionKey(p), label: p.name }))"
          @change="selectProject"
        />
        <button type="button" class="snapshot-btn" :disabled="loading || !selectedProjectId" @click="load">
          {{ loading ? t('common.loading') : t('common.refresh') }}
        </button>
      </div>
    </header>

    <p class="snapshot-subpanel__hint">{{ t('governance.snapshotsHint') }}</p>

    <div v-if="loadError" class="snapshot-subpanel__error" data-testid="snapshot-error">{{ loadError }}</div>

    <div v-if="lastRestore" class="snapshot-subpanel__restore-result" data-testid="snapshot-restore-result">
      <strong>{{ t('governance.lastRestore') }}:</strong>
      {{ lastRestore.result.status ? t(`governance.snapshotStatus.${lastRestore.result.status}`, lastRestore.result.status) : '?' }} ·
      {{ lastRestore.result.applied_file_count ?? 0 }}
      {{ t('governance.filesApplied') }}
      <span v-if="lastRestore.result.workspace_hash" class="font-mono text-xs"> · {{ lastRestore.result.workspace_hash.slice(0, 12) }}</span>
      <span v-if="(lastRestore.result.conflicts?.length ?? 0) > 0" class="text-warning">
        {{ t('governance.conflictsPresent') }}
      </span>
    </div>

    <p v-if="!loading && !sorted.length" class="snapshot-subpanel__empty" data-testid="snapshot-empty">
      {{ t('governance.noSnapshots') }}
    </p>

    <ul v-else class="snapshot-list">
      <li v-for="s in sorted" :key="s.id" class="snapshot-card" data-testid="snapshot-card">
        <div class="snapshot-card__head">
          <span class="font-mono text-xs">{{ s.id.slice(0, 8) }}</span>
          <span class="snapshot-card__badge">{{ t(`governance.snapshotKind.${s.kind}`, s.kind) }}</span>
          <span v-if="s.is_git" class="snapshot-card__badge">git</span>
          <span class="snapshot-card__status">{{ t(`governance.snapshotStatus.${s.status}`, s.status) }}</span>
        </div>
        <dl class="snapshot-card__meta">
          <div><dt>{{ t('governance.snapshotFacts.files') }}</dt><dd>{{ s.file_count }}</dd></div>
          <div><dt>{{ t('governance.snapshotFacts.workspaceHash') }}</dt><dd class="font-mono">{{ s.workspace_hash.slice(0, 12) }}</dd></div>
          <div><dt>{{ t('governance.snapshotFacts.contentHash') }}</dt><dd class="font-mono">{{ s.content_hash.slice(0, 12) }}</dd></div>
          <div><dt>{{ t('governance.snapshotFacts.createdAt') }}</dt><dd>{{ new Date(s.created_at).toLocaleString(locale) }}</dd></div>
        </dl>
        <div class="snapshot-card__actions">
          <button
            type="button"
            class="snapshot-btn"
            :disabled="restoring === s.id"
            data-testid="snapshot-restore-btn"
            @click="restore(s)"
          >
            <RotateCcw class="size-3.5" aria-hidden="true" />
            {{ restoring === s.id ? t('common.working') : t('governance.restore') }}
          </button>
          <button
            type="button"
            class="snapshot-btn"
            data-testid="snapshot-review-btn"
            @click="toggleReview(s)"
          >
            {{ reviewSnapshotId === s.id ? t('governance.hideFiles') : t('governance.reviewFiles') }}
          </button>
        </div>

        <section v-if="reviewSnapshotId === s.id" class="snapshot-review" data-testid="snapshot-review">
          <p v-if="changesLoading" class="snapshot-subpanel__empty">{{ t('common.loading') }}</p>
          <p v-else-if="!reviewedChanges.length" class="snapshot-subpanel__empty" data-testid="snapshot-review-clean">
            {{ t('governance.noFileChanges') }}
          </p>
          <ul v-else class="snapshot-review__list">
            <li v-for="change in reviewedChanges" :key="change.path" class="snapshot-review__row" data-testid="snapshot-review-row">
              <span class="snapshot-card__badge" :data-status="change.status">{{ t(`governance.fileChange.${change.status}`, change.status) }}</span>
              <code class="font-mono text-xs snapshot-review__path">{{ change.path }}</code>
              <button type="button" class="snapshot-btn" data-testid="snapshot-diff-btn" @click="toggleDiff(change)">
                {{ t('governance.compare') }}
              </button>
              <button
                v-if="change.restorable"
                type="button"
                class="snapshot-btn"
                :disabled="undoingPath === change.path"
                data-testid="snapshot-undo-btn"
                @click="undoFile(change)"
              >
                <RotateCcw class="size-3.5" aria-hidden="true" />
                {{ undoingPath === change.path ? t('common.working') : t('governance.undoFile') }}
              </button>
              <span v-else class="snapshot-review__irreversible" data-testid="snapshot-not-restorable">
                {{ t('governance.fileNotCaptured') }}
              </span>
              <DiffViewer
                v-if="diffs[change.path] && openDiffPaths.includes(change.path)"
                class="snapshot-review__diff"
                :original-content="diffs[change.path]!.before.text ?? ''"
                :modified-content="diffs[change.path]!.after.text ?? ''"
                :file-path="change.path"
                :binary="diffs[change.path]!.before.binary || diffs[change.path]!.after.binary"
                :truncated="diffs[change.path]!.before.truncated || diffs[change.path]!.after.truncated"
              />
            </li>
          </ul>
          <p v-if="reviewError" class="snapshot-subpanel__error" data-testid="snapshot-review-error">{{ reviewError }}</p>
        </section>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.snapshot-subpanel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 6px 4px;
  min-width: 0;
}

.snapshot-subpanel__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.snapshot-subpanel__title {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  font-weight: 700;
  color: var(--text-primary);
}

.snapshot-subpanel__controls {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  flex-wrap: wrap;
}

.snapshot-subpanel__select {
  min-width: 120px;
  max-width: 200px;
}

.snapshot-subpanel__hint {
  margin: 0;
  font-size: 11px;
  color: var(--text-secondary);
}

.snapshot-subpanel__error {
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--bg-status-danger);
  color: var(--accent-danger);
  font-size: 12px;
}

.snapshot-subpanel__restore-result {
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  font-size: 12px;
}

.snapshot-subpanel__empty {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.snapshot-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.snapshot-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  border-radius: 10px;
  border: 1px solid var(--border-card);
  background: var(--surface-section);
}

.snapshot-card__head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.snapshot-card__badge {
  padding: 1px 6px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  font-size: 10px;
  color: var(--text-secondary);
}

.snapshot-card__status {
  margin-left: auto;
  font-size: 11px;
  color: var(--text-muted);
}

.snapshot-card__meta {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
  gap: 4px 10px;
  margin: 0;
  font-size: 11px;
}

.snapshot-card__meta dt {
  color: var(--text-muted);
  font-size: 10px;
}

.snapshot-card__meta dd {
  margin: 0;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.snapshot-card__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.snapshot-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 9px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
}

.snapshot-btn:hover:not(:disabled) {
  background: var(--surface-hover);
}

.snapshot-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.snapshot-review {
  margin-top: 4px;
  border-top: 1px solid var(--border-muted);
  padding-top: 8px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.snapshot-review__list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.snapshot-review__row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  padding: 7px;
  background: var(--surface-raised);
}

.snapshot-review__path {
  min-width: 0;
  flex: 1 1 120px;
  overflow-wrap: anywhere;
}

.snapshot-review__row .snapshot-card__badge[data-status='deleted'] {
  color: var(--accent-danger);
}

.snapshot-review__row .snapshot-card__badge[data-status='added'] {
  color: var(--accent-success, var(--accent-primary));
}

.snapshot-review__irreversible {
  font-size: 11px;
  color: var(--text-secondary);
}

.snapshot-review__diff {
  width: 100%;
}
</style>
