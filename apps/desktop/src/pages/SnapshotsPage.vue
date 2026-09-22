<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Camera, RotateCcw } from '@lucide/vue'
import {
  api,
  type SnapshotDto,
  type WorkspaceFileChangeDto,
  type WorkspaceFileDiffDto,
} from '@/api'
import { useProjectStore } from '@/stores/project'
import CommandPaletteButton from '@/components/CommandPaletteButton.vue'
import DiffViewer from '@/components/git/DiffViewer.vue'

/**
 * Workspace snapshot list + restore (docs/app-core-ui.md §4.6).
 *
 * The public DTO only carries summary fields; Git HEAD/branch/index detail is
 * a pending Core DTO, so this page renders exactly what Core returns and
 * never reads content references directly. Restore is an explicit user
 * command (not yet inside the UserToolAction loop) and is labelled as such.
 */
const { t } = useI18n()
const projectStore = useProjectStore()

const snapshots = ref<SnapshotDto[]>([])
const selectedProjectId = ref<string>('')
const loading = ref(false)
const loadError = ref<string | null>(null)
const restoring = ref<string | null>(null)

type RestoreResult = {
  status?: string
  applied_file_count?: number
  workspace_hash?: string
  conflicts?: unknown[]
}
const lastRestore = ref<{ snapshotId: string; result: RestoreResult } | null>(null)

const projects = computed(() => projectStore.projects ?? [])
const sorted = computed(() =>
  [...snapshots.value].sort((a, b) => b.created_at.localeCompare(a.created_at)),
)

async function load(): Promise<void> {
  if (!selectedProjectId.value) return
  loading.value = true
  loadError.value = null
  try {
    snapshots.value = await api.listWorkspaceSnapshots(selectedProjectId.value)
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

function selectProject(id: string): void {
  selectedProjectId.value = id
  snapshots.value = []
  lastRestore.value = null
  void load()
}

async function restore(snapshot: SnapshotDto): Promise<void> {
  restoring.value = snapshot.id
  try {
    // Idempotent retry identity; expected_workspace_hash is omitted so the
    // server reports conflicts instead of silently overwriting.
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

onMounted(async () => {
  if (!projects.value.length) await projectStore.fetchAll().catch(() => undefined)
  if (!selectedProjectId.value && projects.value.length) selectProject(projects.value[0]!.id)
})

// ---- per-file review -------------------------------------------------------------
// Core compares the snapshot's manifest with the workspace as it stands now, so this list answers
// "what did this write leave behind, and can I take just that piece back". The whole-workspace
// restore above stays for the all-or-nothing case; the two must not be conflated — undoing one
// file has to not silently revert the four the user has not looked at yet.
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
  if (!reviewSnapshotId.value) return
  changes.value = await api.listWorkspaceSnapshotFiles(reviewSnapshotId.value)
}

async function toggleReview(snapshot: SnapshotDto): Promise<void> {
  if (reviewSnapshotId.value === snapshot.id) {
    reviewSnapshotId.value = null
    changes.value = []
    diffs.value = {}
    openDiffPaths.value = []
    return
  }
  reviewSnapshotId.value = snapshot.id
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
    // The hash is the one the reviewer was just shown. A deleted row has none, and "there was no
    // file here" is sent as the empty string rather than by omitting the field — omitting it is
    // refused by Core, because nobody reviewed anything then.
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
  <div class="snapshot-page" data-testid="snapshot-page">
    <header class="snapshot-page__header">
      <h1><Camera class="size-4" /> {{ t('governance.snapshotsTitle', 'Workspace snapshots') }}</h1>
      <div class="flex items-center gap-2">
        <CommandPaletteButton />
        <select
          class="snapshot-page__select"
          :value="selectedProjectId"
          data-testid="snapshot-project-select"
          @change="selectProject(($event.target as HTMLSelectElement).value)"
        >
          <option v-for="p in projects" :key="p.id" :value="p.id">{{ p.name }}</option>
        </select>
        <button type="button" class="detail-dialog__btn" :disabled="loading || !selectedProjectId" @click="load">
          {{ loading ? t('common.loading', 'Loading…') : t('common.refresh', 'Refresh') }}
        </button>
      </div>
    </header>

    <p class="snapshot-page__hint">
      {{ t('governance.snapshotsHint', 'Restore is an explicit user command; it is not yet part of the UserToolAction approval loop.') }}
    </p>

    <div v-if="loadError" class="snapshot-page__error" data-testid="snapshot-error">{{ loadError }}</div>

    <div v-if="lastRestore" class="snapshot-page__restore-result" data-testid="snapshot-restore-result">
      <strong>{{ t('governance.lastRestore', 'Last restore') }}:</strong>
      {{ lastRestore.result.status ?? '?' }} ·
      {{ lastRestore.result.applied_file_count ?? 0 }}
      {{ t('governance.filesApplied', 'files applied') }}
      <span v-if="lastRestore.result.workspace_hash" class="font-mono text-xs"> · {{ lastRestore.result.workspace_hash.slice(0, 12) }}</span>
      <span v-if="(lastRestore.result.conflicts?.length ?? 0) > 0" class="text-warning">
        {{ t('governance.conflictsPresent', 'conflicts reported') }}
      </span>
    </div>

    <p v-if="!loading && !sorted.length" class="snapshot-page__empty" data-testid="snapshot-empty">
      {{ t('governance.noSnapshots', 'No snapshots captured for this project yet.') }}
    </p>

    <ul class="snapshot-list">
      <li v-for="s in sorted" :key="s.id" class="snapshot-card" data-testid="snapshot-card">
        <div class="snapshot-card__head">
          <span class="font-mono text-xs">{{ s.id.slice(0, 8) }}</span>
          <span class="snapshot-card__badge">{{ s.kind }}</span>
          <span v-if="s.is_git" class="snapshot-card__badge">git</span>
          <span class="snapshot-card__status">{{ s.status }}</span>
        </div>
        <dl class="snapshot-card__meta">
          <div><dt>files</dt><dd>{{ s.file_count }}</dd></div>
          <div><dt>workspace_hash</dt><dd class="font-mono">{{ s.workspace_hash.slice(0, 12) }}</dd></div>
          <div><dt>content_hash</dt><dd class="font-mono">{{ s.content_hash.slice(0, 12) }}</dd></div>
          <div><dt>created_at</dt><dd>{{ s.created_at }}</dd></div>
        </dl>
        <button
          type="button"
          class="detail-dialog__btn"
          :disabled="restoring === s.id"
          data-testid="snapshot-restore-btn"
          @click="restore(s)"
        >
          <RotateCcw class="size-3.5" />
          {{ restoring === s.id ? t('common.working', 'Working…') : t('governance.restore', 'Restore') }}
        </button>
        <button
          type="button"
          class="detail-dialog__btn"
          data-testid="snapshot-review-btn"
          @click="toggleReview(s)"
        >
          {{ reviewSnapshotId === s.id ? t('governance.hideFiles', 'Hide files') : t('governance.reviewFiles', 'Review files') }}
        </button>

        <section v-if="reviewSnapshotId === s.id" class="snapshot-review" data-testid="snapshot-review">
          <p v-if="changesLoading" class="snapshot-page__empty">{{ t('common.loading', 'Loading…') }}</p>
          <p v-else-if="!reviewedChanges.length" class="snapshot-page__empty" data-testid="snapshot-review-clean">
            {{ t('governance.noFileChanges', 'No file differs from this snapshot.') }}
          </p>
          <ul v-else class="snapshot-review__list">
            <li v-for="change in reviewedChanges" :key="change.path" class="snapshot-review__row" data-testid="snapshot-review-row">
              <span class="snapshot-card__badge" :data-status="change.status">{{ change.status }}</span>
              <code class="font-mono text-xs">{{ change.path }}</code>
              <button type="button" class="detail-dialog__btn" data-testid="snapshot-diff-btn" @click="toggleDiff(change)">
                {{ t('governance.compare', 'Compare') }}
              </button>
              <button
                v-if="change.restorable"
                type="button"
                class="detail-dialog__btn"
                :disabled="undoingPath === change.path"
                data-testid="snapshot-undo-btn"
                @click="undoFile(change)"
              >
                <RotateCcw class="size-3.5" />
                {{ undoingPath === change.path ? t('common.working', 'Working…') : t('governance.undoFile', 'Undo this file') }}
              </button>
              <span v-else class="snapshot-review__irreversible" data-testid="snapshot-not-restorable">
                {{ t('governance.fileNotCaptured', 'Its content was not captured, so this file cannot be undone from this snapshot.') }}
              </span>
              <DiffViewer
                v-if="diffs[change.path]"
                class="snapshot-review__diff"
                :original-content="diffs[change.path]!.before.text ?? ''"
                :modified-content="diffs[change.path]!.after.text ?? ''"
                :file-path="change.path"
                :binary="diffs[change.path]!.before.binary || diffs[change.path]!.after.binary"
                :truncated="diffs[change.path]!.before.truncated || diffs[change.path]!.after.truncated"
              />
            </li>
          </ul>
          <p v-if="reviewError" class="snapshot-page__error" data-testid="snapshot-review-error">{{ reviewError }}</p>
        </section>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.snapshot-page {
  max-width: 860px;
  margin: 0 auto;
  padding: 24px 20px 40px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.snapshot-review {
  margin-top: 10px;
  border-top: 1px solid var(--border-muted);
  padding-top: 10px;
  display: flex;
  flex-direction: column;
  gap: 8px;
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
  gap: 8px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  padding: 8px;
  background: var(--surface-section);
}

/* The status word is the fact; colour only reinforces it, so it never carries meaning alone. */
.snapshot-review__row .snapshot-card__badge[data-status='deleted'] {
  color: var(--accent-danger);
}

.snapshot-review__row .snapshot-card__badge[data-status='added'],
.snapshot-review__row .snapshot-card__badge[data-status='modified'] {
  color: var(--accent-warning);
}

.snapshot-review__irreversible {
  font-size: 11px;
  color: var(--text-muted);
}

.snapshot-review__diff {
  flex-basis: 100%;
  min-height: 220px;
}

.snapshot-page__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 10px;
}

.snapshot-page__header h1 {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 17px;
  font-weight: 700;
}

.snapshot-page__select {
  border: 1px solid var(--border-input);
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary);
  padding: 5px 8px;
  font-size: 13px;
}

.snapshot-page__hint {
  font-size: 12px;
  color: var(--text-secondary);
}

.snapshot-page__error,
.snapshot-page__restore-result,
.snapshot-page__empty,
.snapshot-card {
  border-radius: 8px;
  background: var(--surface-section);
  font-size: 13px;
}

.snapshot-page__error {
  padding: 10px 12px;
  color: var(--accent-danger);
}

.snapshot-page__restore-result {
  padding: 10px 12px;
  color: var(--text-primary);
}

.snapshot-page__empty {
  padding: 14px;
  color: var(--text-secondary);
  text-align: center;
}

.snapshot-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.snapshot-card {
  padding: 12px 14px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.snapshot-card__head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.snapshot-card__badge,
.snapshot-card__status {
  font-size: 10px;
  font-weight: 600;
  padding: 1px 6px;
  border-radius: 3px;
  background: var(--bg-status-neutral);
  color: var(--text-secondary);
}

.snapshot-card__status {
  background: var(--bg-status-ok);
  color: var(--accent-success);
}

.snapshot-card__meta {
  display: flex;
  gap: 16px;
  flex-wrap: wrap;
  margin: 0;
}

.snapshot-card__meta div {
  display: flex;
  gap: 4px;
}

.snapshot-card__meta dt {
  color: var(--text-secondary);
  font-size: 11px;
}

.snapshot-card__meta dd {
  margin: 0;
  color: var(--text-primary);
  font-size: 12px;
}

.snapshot-card > button {
  align-self: flex-start;
}
</style>
