<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ChevronDown,
  ChevronRight,
  FilePlus,
  FolderOpen,
  FolderPlus,
  LoaderCircle,
  Paperclip,
  RefreshCw,
  Star,
  Trash2,
} from '@lucide/vue'
import { homeController } from '@/controllers/HomeController'
import { basenameFromPath } from '@/format'
import type { WorkspaceFileGrant, WorkspaceRoot } from '@/lib/workspaces'
import type { AttachableFile } from '@/lib/pendingAttachments'
import { api } from '@/api'
import { canPickHostDirectories, pickWorkspaceFolders } from '@/lib/hostConnection'
import { useHostAccess } from '@/lib/hostAccess'
import { isAbortError } from '@/lib/isAbortError'
import { useNotifications } from '@/composables/useNotifications'
import { useUiPreferences } from '@/composables/useUiPreferences'
import type { MessageAttachmentDto } from '@/generated/client'
import FileTypeVisualIcon from '@/components/workspace/FileTypeVisualIcon.vue'
import LivingWorkspaceFolder, {
  type DroppedWorkspaceEntry,
  type WorkspaceDropBatchPayload,
} from '@/components/workspace/LivingWorkspaceFolder.vue'
import {
  loadWorkspaceItemOrder,
  mergeReorderedVisibleKeys,
  reconcileWorkspaceItemOrder,
  saveWorkspaceItemOrder,
  type WorkspaceUnifiedItem,
} from '@/components/workspace/workspaceCardOrder'
import { buildWorkspaceMediaThumbUrl } from '@/components/workspace/workspaceVisuals'

const WORKSPACE_ROOTS_KEY = 'workspace-roots'

type WorkspaceExposure = MessageAttachmentDto & {
  origin?: 'composer' | 'workspace'
  project_id?: string | null
  source_path?: string | null
  delivered_to_model?: boolean
  delivered_run_id?: string | null
  delivered_at?: string | null
}

const { t, te } = useI18n()
const { canAccessBackend } = useHostAccess()
const { notify, dismissByKey } = useNotifications()
const uiPreferences = useUiPreferences()
const c = homeController

const panelRootRef = ref<HTMLElement | null>(null)

const project = computed(() => c.currentProject.value)
const roots = computed<WorkspaceRoot[]>(() => project.value?.roots ?? [])
const files = computed<WorkspaceFileGrant[]>(() => project.value?.files ?? [])
const primaryId = computed(() => project.value?.primary_root_id ?? roots.value[0]?.id ?? null)
const busy = computed(() => Boolean(c.busy?.value))

const savedOrderKeys = ref<string[]>([])
const highlightedItemKey = ref<string | null>(null)
let highlightTimer: ReturnType<typeof setTimeout> | null = null
let activeOrderProjectId: string | null = null

function clearHighlightTimer(): void {
  if (highlightTimer !== null) {
    clearTimeout(highlightTimer)
    highlightTimer = null
  }
}

watch(
  () => project.value?.id ?? null,
  (projectId) => {
    activeOrderProjectId = projectId
    savedOrderKeys.value = loadWorkspaceItemOrder(projectId)
    clearHighlightTimer()
    highlightedItemKey.value = null
  },
  { immediate: true, flush: 'sync' },
)

const unifiedItems = computed<WorkspaceUnifiedItem[]>(() =>
  reconcileWorkspaceItemOrder(roots.value, files.value, savedOrderKeys.value),
)

watch(unifiedItems, (items) => {
  const projectId = project.value?.id ?? null
  if (!projectId || projectId !== activeOrderProjectId) return
  if (savedOrderKeys.value.length === 0) return
  const nextKeys = items.map((item) => item.key)
  const unchangedOrder =
    nextKeys.length === savedOrderKeys.value.length &&
    nextKeys.every((key, idx) => key === savedOrderKeys.value[idx])
  if (!unchangedOrder) {
    savedOrderKeys.value = nextKeys
    saveWorkspaceItemOrder(projectId, nextKeys)
  }
})

function handleCardReorder(reorderedVisibleKeys: string[]): void {
  const currentKeys = unifiedItems.value.map((item) => item.key)
  const nextOrder = mergeReorderedVisibleKeys(currentKeys, reorderedVisibleKeys)
  savedOrderKeys.value = nextOrder
  saveWorkspaceItemOrder(project.value?.id, nextOrder)
}

function handleSelectCard(item: WorkspaceUnifiedItem): void {
  clearHighlightTimer()
  highlightedItemKey.value = item.key
  highlightTimer = setTimeout(() => {
    highlightedItemKey.value = null
    highlightTimer = null
  }, 1400)

  void nextTick(() => {
    const rootEl = panelRootRef.value
    if (!rootEl) return
    const target = Array.from(
      rootEl.querySelectorAll<HTMLElement>('[data-workspace-item-key]'),
    ).find((el) => el.dataset.workspaceItemKey === item.key)
    target?.scrollIntoView?.({ behavior: 'smooth', block: 'nearest' })
  })
}

const picking = ref(false)
const result = ref<{
  imported: number
  bound: number
  attached: number
  skipped: Array<{ path: string; reason: string }>
} | null>(null)
const skippedOpen = ref(true)
const inputValidationHint = ref<string | null>(null)
const unchanged = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)

const exposures = ref<WorkspaceExposure[]>([])
const exposuresLoading = ref(false)
let exposureRead = 0

// Quietly collapsed by default per [11]
const exposuresOpen = ref(false)
const accessOpen = ref(false)

function permissionLine(): string {
  switch (c.currentPermission.value) {
    case 'full-access':
      return t('workspaceRoots.accessModeFullAccess')
    case 'auto-approve':
      return t('workspaceRoots.accessModeAutoApprove')
    case 'delegate-conversation':
    case 'delegate-reviewer':
    case 'delegate-both':
      return t('permission.delegateConversationHint')
    default:
      return t('workspaceRoots.accessModeDefault')
  }
}

const accessPillLabel = computed(() => {
  switch (c.currentPermission.value) {
    case 'full-access':
      return t('workspaceRoots.accessPillFull')
    case 'auto-approve':
      return t('workspaceRoots.accessPillAuto')
    case 'delegate-conversation':
    case 'delegate-reviewer':
    case 'delegate-both':
      return t('workspaceRoots.accessPillDelegated')
    default:
      return t('workspaceRoots.accessPillDefault')
  }
})

const parentCandidates = computed(() => [
  ...new Set((result.value?.skipped ?? []).map((item) => parentFolder(item.path)).filter(Boolean)),
])

function removeBlockedReason(root: WorkspaceRoot): string | null {
  if (roots.value.length <= 1) return t('workspaceRoots.removeBlockedLast')
  if (root.id === primaryId.value) return t('workspaceRoots.removeBlockedPrimary')
  return null
}

function folderName(root: WorkspaceRoot): string {
  return basenameFromPath(root.path)
}

function parentFolder(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, '')
  const index = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'))
  return index <= 0 ? '' : trimmed.slice(0, index)
}

function listSignature(): string {
  return `${roots.value.map((root) => root.id).join('|')}#${primaryId.value ?? ''}#${files.value.map((file) => file.id).join('|')}`
}

async function write(action: () => Promise<void>): Promise<void> {
  inputValidationHint.value = null
  unchanged.value = false
  const before = listSignature()
  try {
    await action()
    const didChange = listSignature() !== before
    unchanged.value = !didChange
    if (didChange) dismissByKey(WORKSPACE_ROOTS_KEY)
  } catch (err) {
    if (!isAbortError(err)) {
      notify.error(err, { key: WORKSPACE_ROOTS_KEY })
    }
  }
}

async function addFolders(): Promise<void> {
  if (picking.value || busy.value) return
  if (!canPickHostDirectories()) {
    notify.error(t('host.previewShim'), { key: WORKSPACE_ROOTS_KEY })
    return
  }
  picking.value = true
  try {
    const paths = await pickWorkspaceFolders(t('workspaceRoots.addFolder'))
    if (paths.length) await write(() => c.addWorkspaceFolderPaths(paths))
  } catch (err) {
    if (!isAbortError(err)) {
      notify.error(err, { key: WORKSPACE_ROOTS_KEY })
    }
  } finally {
    picking.value = false
  }
}

function setPrimary(root: WorkspaceRoot): void {
  if (busy.value || root.id === primaryId.value) return
  void write(() => c.setPrimaryWorkspaceRoot(root.id))
}

function remove(root: WorkspaceRoot): void {
  if (busy.value || removeBlockedReason(root)) return
  void write(() => c.removeWorkspaceRoot(root.id))
}

function removeFile(file: WorkspaceFileGrant): void {
  if (busy.value) return
  void write(() => c.removeWorkspaceFile(file.id))
}

function classifyFolderControlDrop(transfer: DataTransfer | null): {
  folders: DroppedWorkspaceEntry[]
  files: DroppedWorkspaceEntry[]
} {
  const folders: DroppedWorkspaceEntry[] = []
  const files: DroppedWorkspaceEntry[] = []
  const resolve = (file: File | null): string =>
    file ? window.tinadec?.pathForFile?.(file) ?? '' : ''
  const items = Array.from(transfer?.items ?? []).filter((item) => item.kind === 'file')
  if (items.length) {
    for (const item of items) {
      const file = item.getAsFile()
      const path = resolve(file)
      const name = file?.name ?? path
      const entry = typeof item.webkitGetAsEntry === 'function' ? item.webkitGetAsEntry() : null
      if (entry?.isDirectory) folders.push({ path, name })
      else files.push({ path, name })
    }
    return { folders, files }
  }
  for (const file of Array.from(transfer?.files ?? [])) {
    files.push({ path: resolve(file), name: file?.name ?? '' })
  }
  return { folders, files }
}

function unresolvedEntries(
  entries: DroppedWorkspaceEntry[],
): Array<{ path: string; reason: string }> {
  return entries
    .filter((entry) => !entry.path)
    .map((entry) => ({ path: entry.name || entry.path, reason: 'unresolved' }))
}

function skipReason(reason: string): string {
  const key = `workspaceRoots.skipReason.${reason}`
  return te(key) ? t(key) : reason
}

async function copyFiles(
  entries: DroppedWorkspaceEntry[],
): Promise<{ imported: number; skipped: Array<{ path: string; reason: string }> } | null> {
  const resolved = entries.filter((entry) => entry.path)
  const unresolved = entries.filter((entry) => !entry.path)
  let copied: Awaited<ReturnType<typeof c.importWorkspaceFiles>> | null = null
  if (resolved.length) {
    try {
      copied = await c.importWorkspaceFiles(resolved.map((entry) => entry.path))
      dismissByKey(WORKSPACE_ROOTS_KEY)
    } catch (reason) {
      notify.error(reason, { key: WORKSPACE_ROOTS_KEY })
      return null
    }
  }
  return {
    imported: copied?.imported.length ?? 0,
    skipped: [...unresolvedEntries(unresolved), ...(copied?.skipped ?? [])],
  }
}

async function bindFiles(
  entries: DroppedWorkspaceEntry[],
): Promise<{ bound: number; skipped: Array<{ path: string; reason: string }> } | null> {
  const resolved = entries.filter((entry) => entry.path)
  const unresolved = entries.filter((entry) => !entry.path)
  if (resolved.length) {
    try {
      await c.addWorkspaceFilePaths(resolved.map((entry) => entry.path))
      dismissByKey(WORKSPACE_ROOTS_KEY)
    } catch (err) {
      if (!isAbortError(err)) notify.error(err, { key: WORKSPACE_ROOTS_KEY })
      return null
    }
  }
  return {
    bound: resolved.length,
    skipped: unresolvedEntries(unresolved),
  }
}

async function addFoldersFromDrop(
  folders: DroppedWorkspaceEntry[],
): Promise<Array<{ path: string; reason: string }>> {
  const resolved = folders.filter((folder) => folder.path).map((folder) => folder.path)
  const unresolved = unresolvedEntries(folders)
  if (resolved.length) await write(() => c.addWorkspaceFolderPaths(resolved))
  return unresolved
}

async function handleFolderDropBatch(payload: WorkspaceDropBatchPayload): Promise<void> {
  if (busy.value) return
  result.value = null
  inputValidationHint.value = null

  const skippedFolders = payload.folders.length
    ? await addFoldersFromDrop(payload.folders)
    : []

  let imported = 0
  let bound = 0
  let skippedFiles: Array<{ path: string; reason: string }> = []

  if (payload.files.length) {
    if (payload.fileAction === 'copy') {
      const outcome = await copyFiles(payload.files)
      if (!outcome) return
      imported = outcome.imported
      skippedFiles = outcome.skipped
    } else {
      const outcome = await bindFiles(payload.files)
      if (!outcome) return
      bound = outcome.bound
      skippedFiles = outcome.skipped
    }
  }

  const allSkipped = [...skippedFolders, ...skippedFiles]
  if (imported > 0 || bound > 0 || allSkipped.length > 0) {
    result.value = {
      imported,
      bound,
      attached: 0,
      skipped: allSkipped,
    }
    skippedOpen.value = true
  }
}

async function dropOntoFolderOnlyButton(transfer: DataTransfer | null): Promise<void> {
  if (busy.value) return
  const { folders, files: droppedFiles } = classifyFolderControlDrop(transfer)
  result.value = null
  inputValidationHint.value = null
  if (droppedFiles.length) {
    inputValidationHint.value = t('workspaceRoots.notAFolder', {
      path: droppedFiles[0]!.path || droppedFiles[0]!.name,
    })
    return
  }
  if (folders.length) {
    const skipped = await addFoldersFromDrop(folders)
    if (skipped.length) {
      result.value = { imported: 0, bound: 0, attached: 0, skipped }
      skippedOpen.value = true
    }
  }
}

async function addParentsInstead(): Promise<void> {
  const parents = parentCandidates.value
  if (!parents.length) return
  await write(() => c.addWorkspaceFolderPaths(parents))
}

function dismissResult(): void {
  result.value = null
}

function dismissInputHint(): void {
  inputValidationHint.value = null
}

function reload(): void {
  result.value = null
  inputValidationHint.value = null
  unchanged.value = false
  dismissByKey(WORKSPACE_ROOTS_KEY)
  void loadExposures()
  void c.refreshProjectsAndSessions()
}

async function loadExposures(): Promise<void> {
  const projectId = project.value?.id
  const generation = ++exposureRead
  if (!projectId) {
    exposures.value = []
    return
  }
  if (!canAccessBackend.value) {
    return
  }
  exposuresLoading.value = true
  try {
    const rows = (await api.listProjectExposures(projectId)) as WorkspaceExposure[]
    if (generation === exposureRead) {
      exposures.value = rows
      dismissByKey(WORKSPACE_ROOTS_KEY)
    }
  } catch (error) {
    if (generation === exposureRead && !isAbortError(error)) {
      notify.error(error, {
        key: WORKSPACE_ROOTS_KEY,
        action: { label: t('workspaceRoots.reload'), run: () => loadExposures() },
      })
    }
  } finally {
    if (generation === exposureRead) exposuresLoading.value = false
  }
}

function exposureSessionName(sessionId: string | null | undefined): string {
  if (!sessionId) return '—'
  return c.sessions.value.find((session) => session.id === sessionId)?.title ?? sessionId.slice(0, 8)
}

function exposureTime(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

function exposureSource(exposure: WorkspaceExposure): string {
  return exposure.origin === 'workspace'
    ? t('workspaceRoots.exposureWorkspace')
    : t('workspaceRoots.exposureComposer')
}

watch(
  () => project.value?.id,
  () => {
    void loadExposures()
  },
  { immediate: true },
)

watch(canAccessBackend, (ready, wasReady) => {
  if (ready && !wasReady) {
    dismissByKey(WORKSPACE_ROOTS_KEY)
    void loadExposures()
  }
})

onBeforeUnmount(() => {
  clearHighlightTimer()
  ++exposureRead
  dismissByKey(WORKSPACE_ROOTS_KEY)
})

function openFilePicker(): void {
  if (busy.value) return
  fileInput.value?.click()
}

async function filesPicked(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const picked = Array.from(input.files ?? [])
  input.value = ''
  if (!picked.length) return
  const entries: DroppedWorkspaceEntry[] = picked.map((file) => ({
    path: window.tinadec?.pathForFile?.(file) ?? '',
    name: file.name,
  }))

  const mode = uiPreferences.interaction.workspace_drop_action
  if (mode === 'copy') {
    const outcome = await copyFiles(entries)
    if (outcome) {
      result.value = { imported: outcome.imported, bound: 0, attached: 0, skipped: outcome.skipped }
      skippedOpen.value = true
    }
  } else {
    const outcome = await bindFiles(entries)
    if (outcome) {
      result.value = { imported: 0, bound: outcome.bound, attached: 0, skipped: outcome.skipped }
      skippedOpen.value = true
    }
  }
}

async function attachBoundFile(file: WorkspaceFileGrant): Promise<void> {
  const projectId = project.value?.id ?? null
  if (!projectId || busy.value) return
  const name = basenameFromPath(file.path)
  const attachable: AttachableFile = {
    name,
    type: '',
    size: 0,
    sourcePath: file.path,
    origin: 'workspace',
    projectId,
    arrayBuffer: async () => {
      try {
        const res = await fetch(buildWorkspaceMediaThumbUrl(file.path))
        return res.ok ? await res.arrayBuffer() : new ArrayBuffer(0)
      } catch {
        return new ArrayBuffer(0)
      }
    },
  }
  try {
    await c.prefillWorkspaceAttachments([attachable])
    dismissByKey(WORKSPACE_ROOTS_KEY)
    result.value = { imported: 0, bound: 0, attached: 1, skipped: [] }
  } catch (err) {
    if (!isAbortError(err)) notify.error(err, { key: WORKSPACE_ROOTS_KEY })
  }
}
</script>

<template>
  <div ref="panelRootRef" class="ws-root">
    <header class="ws-header">
      <h2 class="ws-title">{{ t('workspaceRoots.title') }}</h2>
      <button
        type="button"
        class="ws-icon-button"
        :aria-label="t('workspaceRoots.reload')"
        :title="t('workspaceRoots.reload')"
        :disabled="busy"
        @click="reload"
      >
        <RefreshCw :size="14" aria-hidden="true" />
      </button>
    </header>

    <div v-if="!project" class="ws-empty">
      <FolderOpen :size="28" aria-hidden="true" class="ws-empty-icon" />
      <p class="ws-empty-title">{{ t('workspaceRoots.noWorkspaceTitle') }}</p>
      <p class="ws-empty-body">{{ t('workspaceRoots.noWorkspaceBody') }}</p>
      <button type="button" class="ws-button" @click="c.openProject()">
        {{ t('workspaceRoots.openProject') }}
      </button>
    </div>

    <div v-else class="ws-body">
      <!-- Zone 1: Top Hero 3D Living Folder + 2 Clean Action Pills -->
      <section class="ws-hero-zone">
        <LivingWorkspaceFolder
          :roots="roots"
          :files="files"
          :ordered-items="unifiedItems"
          :primary-root-id="primaryId"
          :tone="uiPreferences.appearance.workspace_folder_tone"
          :drop-action="uiPreferences.interaction.workspace_drop_action"
          :disabled="busy"
          @drop-batch="handleFolderDropBatch"
          @select-card="handleSelectCard"
          @reorder-cards="handleCardReorder"
        />

        <div class="ws-pill-actions">
          <button
            type="button"
            class="ws-pill-btn"
            data-testid="workspace-add-folder"
            :disabled="busy || picking"
            @dragover.prevent
            @drop.stop.prevent="dropOntoFolderOnlyButton($event.dataTransfer)"
            @click="addFolders"
          >
            <LoaderCircle v-if="picking" :size="14" class="animate-spin" aria-hidden="true" />
            <FolderPlus v-else :size="14" aria-hidden="true" />
            <span>{{ picking ? t('workspaceRoots.picking') : t('workspaceRoots.linkFolderPill') }}</span>
          </button>

          <button
            type="button"
            class="ws-pill-btn"
            data-testid="workspace-add-file"
            :disabled="busy"
            @click="openFilePicker"
          >
            <FilePlus :size="14" aria-hidden="true" />
            <span>{{ t('workspaceRoots.linkFilePill') }}</span>
          </button>
        </div>

        <input ref="fileInput" class="ws-file-input" type="file" multiple @change="filesPicked" />

        <!-- Concise inline feedback when present -->
        <div
          v-if="inputValidationHint"
          class="ws-input-hint"
          data-testid="workspace-input-hint"
          role="status"
        >
          <span>{{ inputValidationHint }}</span>
          <button type="button" class="ws-row-action" @click="dismissInputHint">
            {{ t('workspaceRoots.cancel') }}
          </button>
        </div>

        <div v-if="result" class="ws-summary" role="status" aria-live="polite">
          <p v-if="result.imported > 0 || (!result.bound && !result.attached)" class="ws-summary-line">
            {{ t('workspaceRoots.imported', { count: result.imported }) }}
          </p>
          <p v-if="result.bound" class="ws-summary-line">
            {{ t('workspaceRoots.boundFiles', { count: result.bound }) }}
          </p>
          <p v-if="result.attached" class="ws-summary-line">
            {{ t('workspaceRoots.attachedFiles', { count: result.attached }) }}
          </p>
          <button
            v-if="result.skipped.length"
            type="button"
            class="ws-summary-toggle"
            :aria-expanded="skippedOpen ? 'true' : 'false'"
            @click="skippedOpen = !skippedOpen"
          >
            {{ t('workspaceRoots.importSkipped', { count: result.skipped.length }) }}
          </button>
          <ul v-if="result.skipped.length && skippedOpen" class="ws-skipped">
            <li v-for="item in result.skipped" :key="`${item.path}:${item.reason}`">
              <span class="ws-skipped-path">{{ item.path }}</span>
              <span class="ws-skipped-reason">{{ skipReason(item.reason) }}</span>
            </li>
          </ul>
          <div class="ws-summary-actions">
            <button
              v-if="parentCandidates.length"
              type="button"
              class="ws-row-action"
              :disabled="busy"
              @click="addParentsInstead"
            >
              {{ t('workspaceRoots.addParentInstead') }}
            </button>
            <button type="button" class="ws-row-action" @click="dismissResult">
              {{ t('workspaceRoots.cancel') }}
            </button>
          </div>
        </div>

        <div v-if="unchanged" class="ws-notice" role="status">
          <span>{{ t('workspaceRoots.applyNextRun') }}</span>
          <button type="button" class="ws-row-action" @click="reload">
            {{ t('workspaceRoots.loadLatest') }}
          </button>
        </div>
      </section>

      <!-- Zone 2: Middle Content View (auto-switched strictly by appearance.workspace_view, unified item order) -->
      <section v-if="unifiedItems.length > 0" class="ws-items-zone">
        <!-- GRID VIEW (default) -->
        <div
          v-if="uiPreferences.appearance.workspace_view === 'grid'"
          class="ws-items-grid"
          data-testid="workspace-items-grid"
        >
          <article
            v-for="item in unifiedItems"
            :key="item.key"
            class="ws-grid-card"
            :class="{
              'is-primary': item.kind === 'root' && item.root.id === primaryId,
              'is-highlighted': highlightedItemKey === item.key,
            }"
            data-testid="workspace-grid-card"
            :data-workspace-item-key="item.key"
            :title="item.kind === 'root' ? item.root.path : item.file.path"
          >
            <template v-if="item.kind === 'root'">
              <div class="ws-grid-card-actions">
                <button
                  v-if="item.root.id !== primaryId"
                  type="button"
                  class="ws-mini-action"
                  :disabled="busy"
                  :title="t('workspaceRoots.setPrimary')"
                  :aria-label="t('workspaceRoots.setPrimary')"
                  @click="setPrimary(item.root)"
                >
                  <Star :size="12" aria-hidden="true" />
                </button>
                <button
                  type="button"
                  class="ws-mini-action is-danger"
                  :disabled="busy || !!removeBlockedReason(item.root)"
                  :title="removeBlockedReason(item.root) ?? t('workspaceRoots.remove', { name: folderName(item.root) })"
                  :aria-label="
                    removeBlockedReason(item.root)
                      ? `${t('workspaceRoots.remove', { name: folderName(item.root) })} — ${removeBlockedReason(item.root)}`
                      : t('workspaceRoots.remove', { name: folderName(item.root) })
                  "
                  @click="remove(item.root)"
                >
                  <Trash2 :size="12" aria-hidden="true" />
                </button>
              </div>

              <FileTypeVisualIcon
                kind="folder"
                :path="item.root.path"
                :is-primary="item.root.id === primaryId"
                :tone="uiPreferences.appearance.workspace_folder_tone"
                :size="46"
              />
              <span class="ws-grid-card-name">{{ folderName(item.root) }}</span>
              <span v-if="item.root.id === primaryId" class="ws-grid-card-tag">
                {{ t('workspaceRoots.primary') }}
              </span>
            </template>

            <template v-else>
              <div class="ws-grid-card-actions">
                <button
                  type="button"
                  class="ws-mini-action"
                  :disabled="busy"
                  :title="t('workspaceRoots.attachFile')"
                  :aria-label="t('workspaceRoots.attachFile')"
                  @click="attachBoundFile(item.file)"
                >
                  <Paperclip :size="12" aria-hidden="true" />
                </button>
                <button
                  type="button"
                  class="ws-mini-action is-danger"
                  :disabled="busy"
                  :title="t('workspaceRoots.removeFile', { name: basenameFromPath(item.file.path) })"
                  :aria-label="t('workspaceRoots.removeFile', { name: basenameFromPath(item.file.path) })"
                  @click="removeFile(item.file)"
                >
                  <Trash2 :size="12" aria-hidden="true" />
                </button>
              </div>

              <FileTypeVisualIcon kind="file" :path="item.file.path" :size="46" />
              <span class="ws-grid-card-name">{{ basenameFromPath(item.file.path) }}</span>
            </template>
          </article>
        </div>

        <!-- LIST VIEW -->
        <ul v-else class="ws-items-list" data-testid="workspace-items-list">
          <li
            v-for="item in unifiedItems"
            :key="`list-${item.key}`"
            class="ws-list-row"
            :class="{ 'is-highlighted': highlightedItemKey === item.key }"
            data-testid="workspace-list-row"
            :data-workspace-item-key="item.key"
          >
            <template v-if="item.kind === 'root'">
              <FileTypeVisualIcon
                kind="folder"
                :path="item.root.path"
                :is-primary="item.root.id === primaryId"
                :tone="uiPreferences.appearance.workspace_folder_tone"
                :size="32"
              />
              <span class="ws-root-text">
                <span class="ws-root-name">{{ folderName(item.root) }}</span>
                <span class="ws-root-path" :title="item.root.path">{{ item.root.path }}</span>
              </span>
              <span v-if="item.root.id === primaryId" class="ws-primary-badge">
                <Star :size="11" aria-hidden="true" />{{ t('workspaceRoots.primary') }}
              </span>
              <button
                v-else
                type="button"
                class="ws-row-action"
                :disabled="busy"
                @click="setPrimary(item.root)"
              >
                {{ t('workspaceRoots.setPrimary') }}
              </button>
              <button
                type="button"
                class="ws-row-remove"
                :disabled="busy || !!removeBlockedReason(item.root)"
                :title="removeBlockedReason(item.root) ?? t('workspaceRoots.remove', { name: folderName(item.root) })"
                :aria-label="
                  removeBlockedReason(item.root)
                    ? `${t('workspaceRoots.remove', { name: folderName(item.root) })} — ${removeBlockedReason(item.root)}`
                    : t('workspaceRoots.remove', { name: folderName(item.root) })
                "
                @click="remove(item.root)"
              >
                <Trash2 :size="14" aria-hidden="true" />
              </button>
            </template>

            <template v-else>
              <FileTypeVisualIcon kind="file" :path="item.file.path" :size="32" />
              <span class="ws-root-text">
                <span class="ws-root-name">{{ basenameFromPath(item.file.path) }}</span>
                <span class="ws-root-path" :title="item.file.path">{{ item.file.path }}</span>
              </span>
              <button
                type="button"
                class="ws-row-remove"
                :disabled="busy"
                :title="t('workspaceRoots.attachFile')"
                :aria-label="t('workspaceRoots.attachFile')"
                @click="attachBoundFile(item.file)"
              >
                <Paperclip :size="13" aria-hidden="true" />
              </button>
              <button
                type="button"
                class="ws-row-remove"
                :disabled="busy"
                :title="t('workspaceRoots.removeFile', { name: basenameFromPath(item.file.path) })"
                :aria-label="t('workspaceRoots.removeFile', { name: basenameFromPath(item.file.path) })"
                @click="removeFile(item.file)"
              >
                <Trash2 :size="14" aria-hidden="true" />
              </button>
            </template>
          </li>
        </ul>
      </section>

      <!-- Zone 3: Bottom Quietly Collapsed Low-Frequency Accordions -->
      <div class="ws-bottom-accordions">
        <!-- 3A. Exposures (collapsed by default) -->
        <section class="ws-fold-box" data-testid="workspace-exposures-section">
          <div class="ws-fold-header">
            <button
              type="button"
              class="ws-fold-toggle"
              data-testid="workspace-exposures-toggle"
              :aria-expanded="exposuresOpen"
              @click="exposuresOpen = !exposuresOpen"
            >
              <ChevronDown v-if="exposuresOpen" :size="13" aria-hidden="true" />
              <ChevronRight v-else :size="13" aria-hidden="true" />
              <span class="ws-fold-title">{{ t('workspaceRoots.exposuresCollapsibleTitle') }}</span>
              <span class="ws-fold-pill">{{ exposures.length }}</span>
            </button>
            <button
              v-if="exposuresOpen"
              type="button"
              class="ws-icon-button ws-icon-button--sm"
              :disabled="exposuresLoading"
              :title="t('workspaceRoots.reload')"
              :aria-label="t('workspaceRoots.reload')"
              @click="loadExposures"
            >
              <LoaderCircle v-if="exposuresLoading" :size="13" class="animate-spin" aria-hidden="true" />
              <RefreshCw v-else :size="13" aria-hidden="true" />
            </button>
          </div>

          <div v-if="exposuresOpen" class="ws-fold-body">
            <p class="ws-muted">{{ t('workspaceRoots.exposuresHint') }}</p>
            <ul v-if="exposures.length" class="ws-exposure-list">
              <li v-for="exposure in exposures" :key="exposure.id" class="ws-exposure-row">
                <img
                  v-if="exposure.media_type.startsWith('image/')"
                  class="ws-thumb"
                  :src="api.attachmentContentUrl(exposure.id)"
                  :alt="exposure.file_name"
                  loading="lazy"
                />
                <FileTypeVisualIcon
                  v-else
                  kind="file"
                  :path="exposure.source_path ?? exposure.file_name"
                  :size="32"
                />
                <span class="ws-exposure-main">
                  <span class="ws-exposure-name" :title="exposure.source_path ?? exposure.file_name">
                    {{ exposure.file_name }}
                  </span>
                  <span class="ws-exposure-meta">
                    {{ exposureSource(exposure) }} · {{ exposureSessionName(exposure.session_id) }} · {{ exposureTime(exposure.created_at) }}
                  </span>
                  <span class="ws-exposure-state" :class="{ 'is-delivered': exposure.delivered_to_model }">
                    {{ exposure.delivered_to_model ? t('workspaceRoots.delivered') : t('workspaceRoots.notDelivered') }}
                  </span>
                </span>
              </li>
            </ul>
            <p v-else-if="!exposuresLoading" class="ws-file-empty">
              {{ t('workspaceRoots.exposuresEmpty') }}
            </p>
          </div>
        </section>

        <!-- 3B. Path Access Rules (collapsed by default) -->
        <section class="ws-fold-box" data-testid="workspace-access-section">
          <button
            type="button"
            class="ws-fold-toggle"
            data-testid="workspace-access-toggle"
            :aria-expanded="accessOpen"
            @click="accessOpen = !accessOpen"
          >
            <ChevronDown v-if="accessOpen" :size="13" aria-hidden="true" />
            <ChevronRight v-else :size="13" aria-hidden="true" />
            <span class="ws-fold-title">{{ t('workspaceRoots.accessCollapsibleTitle') }}</span>
            <span class="ws-fold-pill">{{ accessPillLabel }}</span>
          </button>

          <div v-if="accessOpen" class="ws-fold-body">
            <p class="ws-access-line">
              <span class="ws-access-tag">{{ t('workspaceRoots.accessRead') }}</span>
              <span class="ws-access-text">{{ t('workspaceRoots.accessReadBody') }}</span>
            </p>
            <p class="ws-access-line">
              <span class="ws-access-tag">{{ t('workspaceRoots.accessWrite') }}</span>
              <span class="ws-access-text">{{ permissionLine() }}</span>
            </p>
            <p class="ws-access-note">{{ t('workspaceRoots.applyNextRun') }}</p>
          </div>
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
.ws-root {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  min-width: 0;
}

.ws-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 14px 4px;
}

.ws-title {
  flex: 1;
  margin: 0;
  font-size: 13.5px;
  font-weight: 700;
  color: var(--text-primary);
}

.ws-icon-button {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  flex: none;
  border: 0;
  border-radius: 7px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.ws-icon-button--sm {
  width: 24px;
  height: 24px;
}

.ws-icon-button:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.ws-icon-button:disabled {
  opacity: 0.45;
  cursor: default;
}

.ws-icon-button:focus-visible,
.ws-row-action:focus-visible,
.ws-pill-btn:focus-visible,
.ws-button:focus-visible,
.ws-fold-toggle:focus-visible,
.ws-summary-toggle:focus-visible {
  outline: 2px solid var(--accent-primary);
  outline-offset: 2px;
}

.ws-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 32px 20px;
  text-align: center;
}

.ws-empty-icon {
  color: var(--text-muted);
}

.ws-empty-title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.ws-empty-body {
  margin: 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--text-muted);
}

.ws-button {
  margin-top: 4px;
  padding: 6px 12px;
  border: 1px solid var(--border-default);
  border-radius: 8px;
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 12px;
  cursor: pointer;
}

.ws-button:hover {
  background: var(--surface-hover);
}

.ws-body {
  display: flex;
  flex-direction: column;
  gap: 14px;
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 4px 14px 14px;
}

/* Zone 1: Top Hero */
.ws-hero-zone {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
}

.ws-pill-actions {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  flex-wrap: wrap;
  width: 100%;
}

.ws-pill-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  height: 31px;
  padding: 0 13px;
  border-radius: 999px;
  border: 1px solid var(--border-card);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 600;
  cursor: pointer;
  transition: background-color 140ms ease, color 140ms ease, border-color 140ms ease;
}

.ws-pill-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
  border-color: var(--border-default);
}

.ws-pill-btn:disabled {
  opacity: 0.5;
  cursor: default;
}

.ws-file-input {
  display: none;
}

/* Zone 2: Items Grid & List */
.ws-items-zone {
  display: flex;
  flex-direction: column;
}

.ws-items-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(98px, 1fr));
  gap: 8px;
}

.ws-grid-card {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 5px;
  padding: 12px 8px 9px;
  border-radius: 11px;
  border: 1px solid transparent;
  background: var(--surface-section);
  text-align: center;
  transition: background-color 180ms ease, border-color 180ms ease, box-shadow 220ms ease;
}

.ws-grid-card:hover,
.ws-grid-card:focus-within {
  background: var(--surface-raised);
  border-color: var(--border-card);
}

.ws-grid-card.is-primary {
  border-color: color-mix(in srgb, #f59e0b 26%, transparent);
}

@keyframes ws-item-highlight-pulse {
  0% {
    box-shadow: 0 0 0 0 color-mix(in srgb, var(--accent-primary) 42%, transparent);
  }
  45% {
    box-shadow: 0 0 0 3px color-mix(in srgb, var(--accent-primary) 28%, transparent);
  }
  100% {
    box-shadow: 0 0 0 1.5px color-mix(in srgb, var(--accent-primary) 18%, transparent);
  }
}

.ws-grid-card.is-highlighted,
.ws-list-row.is-highlighted {
  background: var(--surface-raised);
  border-color: color-mix(in srgb, var(--accent-primary) 64%, var(--border-default));
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 24%, transparent);
  animation: ws-item-highlight-pulse 900ms cubic-bezier(0.22, 1, 0.36, 1);
}

:root[data-reduced-motion='true'] .ws-grid-card.is-highlighted,
:root[data-reduced-motion='true'] .ws-list-row.is-highlighted {
  animation: none;
}

.ws-grid-card-name {
  width: 100%;
  font-size: 11.5px;
  font-weight: 600;
  line-height: 1.3;
  color: var(--text-primary);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
  word-break: break-word;
}

.ws-grid-card-tag {
  font-size: 9.5px;
  font-weight: 600;
  color: var(--text-muted);
}

.ws-grid-card-actions {
  position: absolute;
  top: 5px;
  right: 5px;
  display: inline-flex;
  align-items: center;
  gap: 3px;
  opacity: 0;
  transition: opacity 130ms ease;
  z-index: 2;
}

.ws-grid-card:hover .ws-grid-card-actions,
.ws-grid-card:focus-within .ws-grid-card-actions {
  opacity: 1;
}

.ws-mini-action {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border: 1px solid var(--border-card);
  border-radius: 6px;
  background: color-mix(in srgb, var(--surface-raised) 92%, transparent);
  color: var(--text-secondary);
  cursor: pointer;
}

.ws-mini-action:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.ws-mini-action.is-danger:hover:not(:disabled) {
  color: var(--accent-danger, #ef4444);
}

.ws-mini-action:disabled {
  opacity: 0.38;
  cursor: default;
}

/* List mode */
.ws-items-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.ws-list-row {
  display: flex;
  align-items: center;
  gap: 9px;
  min-width: 0;
  padding: 7px 9px;
  border-radius: 10px;
  border: 1px solid transparent;
  background: var(--surface-section);
  transition: background-color 180ms ease, border-color 180ms ease, box-shadow 220ms ease;
}

.ws-root-text {
  flex: 1;
  display: grid;
  gap: 1.5px;
  min-width: 0;
}

.ws-root-name {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-root-path {
  font-size: 10.5px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-primary-badge {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  flex: none;
  font-size: 10.5px;
  color: var(--text-secondary);
  white-space: nowrap;
}

.ws-row-action {
  flex: none;
  border: 0;
  border-radius: 6px;
  padding: 4px 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
  white-space: nowrap;
}

.ws-row-action:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.ws-row-action:disabled {
  opacity: 0.45;
  cursor: default;
}

.ws-row-remove {
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  flex: none;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.ws-row-remove:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.ws-row-remove:disabled {
  opacity: 0.4;
  cursor: default;
}

/* Inline feedback boxes */
.ws-input-hint,
.ws-notice {
  width: 100%;
  box-sizing: border-box;
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 7px 10px;
  border-radius: 8px;
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11.5px;
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.ws-input-hint span,
.ws-notice span {
  flex: 1;
  min-width: 0;
}

.ws-summary {
  width: 100%;
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--surface-section);
}

.ws-summary-line {
  margin: 0;
  font-size: 11.5px;
  color: var(--text-secondary);
}

.ws-summary-toggle {
  align-self: flex-start;
  border: 0;
  padding: 2px 0;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  text-decoration: underline;
  text-underline-offset: 2px;
  cursor: pointer;
}

.ws-skipped {
  margin: 0;
  padding-left: 16px;
  font-size: 10.5px;
  color: var(--text-muted);
  overflow-wrap: anywhere;
}

.ws-skipped-path {
  margin-right: 6px;
}

.ws-skipped-reason {
  color: var(--text-secondary);
}

.ws-summary-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

/* Zone 3: Quietly Collapsed Bottom Accordions */
.ws-bottom-accordions {
  margin-top: auto;
  padding-top: 6px;
  border-top: 1px solid color-mix(in srgb, var(--border-card) 70%, transparent);
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.ws-fold-box {
  display: flex;
  flex-direction: column;
  border-radius: 9px;
  background: transparent;
}

.ws-fold-header {
  display: flex;
  align-items: center;
  gap: 4px;
}

.ws-fold-toggle {
  flex: 1;
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 600;
  cursor: pointer;
  text-align: left;
}

.ws-fold-toggle:hover {
  background: var(--surface-section);
  color: var(--text-primary);
}

.ws-fold-title {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-fold-pill {
  flex: none;
  padding: 1px 7px;
  border-radius: 999px;
  background: var(--surface-section);
  color: var(--text-muted);
  font-size: 10px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.ws-fold-body {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 4px 8px 8px 22px;
}

.ws-muted,
.ws-file-empty {
  margin: 0;
  font-size: 10.5px;
  line-height: 1.45;
  color: var(--text-muted);
}

.ws-exposure-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-height: 220px;
  overflow-y: auto;
}

.ws-exposure-row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  padding: 6px 8px;
  border-radius: 8px;
  background: var(--surface-section);
}

.ws-thumb {
  width: 32px;
  height: 32px;
  flex: none;
  object-fit: cover;
  border-radius: 6px;
  background: var(--surface-hover);
}

.ws-exposure-main {
  display: grid;
  gap: 2px;
  flex: 1;
  min-width: 0;
}

.ws-exposure-name {
  font-size: 11.5px;
  font-weight: 600;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-exposure-meta {
  font-size: 10px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-exposure-state {
  font-size: 10px;
  color: var(--text-muted);
}

.ws-exposure-state.is-delivered {
  color: var(--accent-primary);
}

.ws-access-line {
  display: flex;
  align-items: baseline;
  gap: 8px;
  margin: 0;
  min-width: 0;
}

.ws-access-tag {
  flex: none;
  padding: 1px 6px;
  border-radius: 5px;
  background: var(--surface-section);
  font-size: 10px;
  color: var(--text-secondary);
  white-space: nowrap;
}

.ws-access-text {
  font-size: 11px;
  line-height: 1.45;
  color: var(--text-muted);
}

.ws-access-note {
  margin: 0;
  font-size: 10px;
  line-height: 1.45;
  color: var(--text-muted);
}
</style>
