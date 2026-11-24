<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Copy, FilePlus, FileText, FolderOpen, FolderPlus, FolderUp, LoaderCircle, RefreshCw, Star, Trash2 } from '@lucide/vue'
import { homeController } from '@/controllers/HomeController'
import { basenameFromPath } from '@/format'
import type { WorkspaceFileGrant, WorkspaceRoot } from '@/lib/workspaces'
import type { AttachableFile } from '@/lib/pendingAttachments'
import { api } from '@/api'
import { canPickHostDirectories, pickWorkspaceFolders } from '@/lib/hostConnection'
import { useHostAccess } from '@/lib/hostAccess'
import { isAbortError } from '@/lib/isAbortError'
import { useNotifications } from '@/composables/useNotifications'
import type { MessageAttachmentDto } from '@/generated/client'

const WORKSPACE_ROOTS_KEY = 'workspace-roots'

type WorkspaceExposure = MessageAttachmentDto & {
  origin?: 'composer' | 'workspace'
  project_id?: string | null
  source_path?: string | null
  delivered_to_model?: boolean
  delivered_run_id?: string | null
  delivered_at?: string | null
}

/**
 * The `workspace` card's body: the source-folder list of the selected workspace.
 *
 * It holds no workspace state of its own. Rows come from `homeController.currentProject`
 * (`roots` / `primary_root_id`), and every action goes through one of the controller's
 * read-modify-write methods, which all land on the same
 * `PUT /api/v1/storage/scopes/{id}/workspace`. A second copy of the list here would let
 * this card and the "edit workspace" dialog disagree about one `project.toml`.
 *
 * A source folder is the authorization unit, not an approval switch: adding one widens
 * which paths a tool may reach, while whether a write still needs a per-call approval is
 * decided by the session's frozen permission mode. The access block states that mode's
 * consequence and offers no bypass of its own.
 */
const { t, te } = useI18n()
const { canAccessBackend } = useHostAccess()
const { notify, dismissByKey } = useNotifications()
const c = homeController

const project = computed(() => c.currentProject.value)
const roots = computed<WorkspaceRoot[]>(() => project.value?.roots ?? [])
const files = computed<WorkspaceFileGrant[]>(() => project.value?.files ?? [])
const primaryId = computed(() => project.value?.primary_root_id ?? roots.value[0]?.id ?? null)
/** `busy` is the controller's shared write latch. */
const busy = computed(() => Boolean(c.busy?.value))

const picking = ref(false)
const dragging = ref(false)
/**
 * The last drop's outcome, held as one record so a count from one drop can never sit beside
 * the names of another. `null` means this card has copied nothing yet.
 */
const result = ref<{ imported: number; bound: number; attached: number; skipped: Array<{ path: string; reason: string }> } | null>(null)
const skippedOpen = ref(true)
/** Pure client-side input validation hint (e.g. dropping a file onto the folder-only control). */
const inputValidationHint = ref<string | null>(null)
/**
 * Set when a write finished without changing the list. The controller reports a refused save
 * (a 412 on a stale digest, the common one) through the notification latch rather than by
 * rejecting, so an unchanged list is the only signal available here; the card therefore states
 * the standing rule and offers a re-read instead of claiming which failure it was.
 */
const unchanged = ref(false)
const filePickerMode = ref<'bind' | 'attach' | 'copy'>('bind')
const fileInput = ref<HTMLInputElement | null>(null)
const exposures = ref<WorkspaceExposure[]>([])
const exposuresLoading = ref(false)
let exposureRead = 0

/**
 * The consequence of the session's current permission mode.
 *
 * A plain function rather than a `computed` wrapper of its own: it reads the controller's
 * `currentPermission` during render, which is reactive in the real controller and stays
 * correct for a host that hands over a plain value.
 */
function permissionLine(): string {
  switch (c.currentPermission.value) {
    case 'full-access': return t('workspaceRoots.accessModeFullAccess')
    case 'auto-approve': return t('workspaceRoots.accessModeAutoApprove')
    case 'delegate-conversation':
    case 'delegate-reviewer':
    case 'delegate-both': return t('permission.delegateConversationHint')
    default: return t('workspaceRoots.accessModeDefault')
  }
}

/** Explicit copy remains available, but dropping files binds the originals by default. */
const copyTarget = computed(() => {
  const primary = roots.value.find((root) => root.id === primaryId.value)
  return primary?.path ?? roots.value[0]?.path ?? ''
})

/** Containing folders of the files that stayed out, offered as the remaining way to reach them. */
const parentCandidates = computed(() => [...new Set(
  (result.value?.skipped ?? []).map((item) => parentFolder(item.path)).filter(Boolean),
)])

/**
 * The remove button's two refusals come from the same rules the save enforces, so the card
 * never offers an action Core would reject. The reason rides in `title` and `aria-label`:
 * a disabled control whose cause is unstated reads as a bug.
 */
function removeBlockedReason(root: WorkspaceRoot): string | null {
  // The last-folder rule is checked first because it is the binding one when both apply: with a
  // single folder the person cannot "make another primary" — that instruction would be a dead end.
  if (roots.value.length <= 1) return t('workspaceRoots.removeBlockedLast')
  if (root.id === primaryId.value) return t('workspaceRoots.removeBlockedPrimary')
  return null
}

function folderName(root: WorkspaceRoot): string {
  return basenameFromPath(root.path)
}

/**
 * The containing folder of one path, spelled with the separators it arrived with. A dropped
 * Windows path must come back as a Windows path: this string is what gets saved into
 * `project.toml` and handed to the tool layer's root set.
 */
function parentFolder(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, '')
  const index = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'))
  return index <= 0 ? '' : trimmed.slice(0, index)
}

function listSignature(): string {
  return `${roots.value.map((root) => root.id).join('|')}#${primaryId.value ?? ''}#${files.value.map((file) => file.id).join('|')}`
}

/** Runs one controller action and notices whether the list actually changed. */
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
  // A browser preview has no dialog to open; saying nothing would read as "no folder chosen".
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
  } finally { picking.value = false }
}

function setPrimary(root: WorkspaceRoot): void {
  if (busy.value || root.id === primaryId.value) return
  void write(() => c.setPrimaryWorkspaceRoot(root.id))
}

function remove(root: WorkspaceRoot): void {
  if (busy.value || removeBlockedReason(root)) return
  void write(() => c.removeWorkspaceRoot(root.id))
}

interface DroppedFile { path: string; name: string }

/**
 * A drop carries two different intents. A folder is referenced in place — that is exactly what
 * "add a source folder" means, and it needs no copy. A file is copied into the primary folder:
 * a single file cannot be referenced, and authorizing its whole folder to read one screenshot
 * would widen far more than the person asked for.
 *
 * The renderer cannot read a dropped item's real path (`File.path` is gone), so every entry
 * goes through the preload's `pathForFile`. An entry that resolves to nothing is returned with
 * an empty `path` rather than dropped here, so the caller can name it: a folder that vanished
 * silently is the worst outcome of all — the person sees nothing happen.
 */
function classifyDrop(transfer: DataTransfer | null): { folders: DroppedFile[]; files: DroppedFile[] } {
  const folders: DroppedFile[] = []
  const files: DroppedFile[] = []
  const resolve = (file: File | null): string => (file ? window.tinadec?.pathForFile?.(file) ?? '' : '')
  const items = Array.from(transfer?.items ?? []).filter((item) => item.kind === 'file')
  if (items.length) {
    for (const item of items) {
      const file = item.getAsFile()
      const path = resolve(file)
      const name = file?.name ?? path
      // `webkitGetAsEntry` is the only way to tell a folder from a file before resolving it.
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

/** Names for the entries the host could not resolve; they were never handed to the main process. */
function unresolvedEntries(entries: DroppedFile[]): Array<{ path: string; reason: string }> {
  return entries.filter((entry) => !entry.path).map((entry) => ({ path: entry.name || entry.path, reason: 'unresolved' }))
}

/**
 * The main process reports a stable reason code and spells out no user-facing language, so the
 * wording is this layer's job. An unknown code is shown as it arrived rather than swallowed: a
 * silent blank where a reason belongs is worse than a raw token, and a new code should be visible
 * enough to get translated.
 */
function skipReason(reason: string): string {
  const key = `workspaceRoots.skipReason.${reason}`
  return te(key) ? t(key) : reason
}

async function copyFiles(entries: DroppedFile[]): Promise<void> {
  const resolved = entries.filter((entry) => entry.path)
  const unresolved = entries.filter((entry) => !entry.path)
  let copied: Awaited<ReturnType<typeof c.importWorkspaceFiles>> | null = null
  if (resolved.length) {
    try {
      copied = await c.importWorkspaceFiles(resolved.map((entry) => entry.path))
      dismissByKey(WORKSPACE_ROOTS_KEY)
    } catch (reason) {
      // A rejected copy must not fall through to "0 files copied": that reads as success.
      notify.error(reason, { key: WORKSPACE_ROOTS_KEY })
      return
    }
  }
  result.value = {
    imported: copied?.imported.length ?? 0,
    bound: 0,
    attached: 0,
    skipped: [...unresolvedEntries(unresolved), ...(copied?.skipped ?? [])],
  }
  skippedOpen.value = true
}

async function bindFiles(entries: DroppedFile[]): Promise<void> {
  const resolved = entries.filter((entry) => entry.path)
  const unresolved = entries.filter((entry) => !entry.path)
  if (resolved.length) {
    try {
      await c.addWorkspaceFilePaths(resolved.map((entry) => entry.path))
      dismissByKey(WORKSPACE_ROOTS_KEY)
    } catch (err) {
      if (!isAbortError(err)) notify.error(err, { key: WORKSPACE_ROOTS_KEY })
      return
    }
  }
  result.value = {
    imported: 0,
    bound: resolved.length,
    attached: 0,
    skipped: unresolvedEntries(unresolved),
  }
  skippedOpen.value = true
}

async function dropTransfer(transfer: DataTransfer | null, foldersOnly = false): Promise<void> {
  if (busy.value) return
  const { folders, files } = classifyDrop(transfer)
  dragging.value = false
  // A new drop starts a new outcome: leaving the previous one on screen would put one drop's
  // count beside another drop's refusal.
  result.value = null
  inputValidationHint.value = null
  if (foldersOnly) {
    // Dropping onto the folder control means "authorize this"; a file there is the mismatch.
    if (files.length) inputValidationHint.value = t('workspaceRoots.notAFolder', { path: files[0]!.path || files[0]!.name })
    else if (folders.length) await addFoldersFromDrop(folders)
    return
  }
  if (folders.length) await addFoldersFromDrop(folders)
  if (files.length) await bindFiles(files)
}

/**
 * Folders are referenced by path, so an entry the host could not resolve has to be named: adding
 * nothing and saying nothing is the one outcome a person cannot act on.
 */
async function addFoldersFromDrop(folders: DroppedFile[]): Promise<void> {
  const resolved = folders.filter((folder) => folder.path).map((folder) => folder.path)
  const unresolved = unresolvedEntries(folders)
  if (resolved.length) await write(() => c.addWorkspaceFolderPaths(resolved))
  if (!unresolved.length) return
  result.value = { imported: 0, bound: 0, attached: 0, skipped: unresolved }
  skippedOpen.value = true
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
    const rows = await api.listProjectExposures(projectId) as WorkspaceExposure[]
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
  return c.sessions.value.find(session => session.id === sessionId)?.title ?? sessionId.slice(0, 8)
}

function exposureTime(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

function exposureSource(exposure: WorkspaceExposure): string {
  return exposure.origin === 'workspace' ? t('workspaceRoots.exposureWorkspace') : t('workspaceRoots.exposureComposer')
}

watch(() => project.value?.id, () => { void loadExposures() }, { immediate: true })
watch(canAccessBackend, (ready, wasReady) => {
  if (ready && !wasReady) {
    dismissByKey(WORKSPACE_ROOTS_KEY)
    void loadExposures()
  }
})
onBeforeUnmount(() => {
  ++exposureRead
  dismissByKey(WORKSPACE_ROOTS_KEY)
})

function chooseFiles(mode: 'bind' | 'attach' | 'copy') {
  if (busy.value) return
  filePickerMode.value = mode
  fileInput.value?.click()
}

async function filesPicked(event: Event) {
  const input = event.target as HTMLInputElement
  const picked = Array.from(input.files ?? [])
  input.value = ''
  if (!picked.length) return
  const entries = picked.map((file) => ({ path: window.tinadec?.pathForFile?.(file) ?? '', name: file.name }))
  if (filePickerMode.value === 'copy') await copyFiles(entries)
  else if (filePickerMode.value === 'attach') await attachFilesToComposer(picked, entries)
  else await bindFiles(entries)
}

async function attachFilesToComposer(picked: File[], entries: DroppedFile[]): Promise<void> {
  const projectId = project.value?.id ?? null
  if (!projectId) {
    notify.warning({ key: WORKSPACE_ROOTS_KEY, message: t('workspaceRoots.noWorkspaceBody') })
    return
  }
  const files: AttachableFile[] = []
  const skipped: Array<{ path: string; reason: string }> = []
  picked.forEach((file, index) => {
    const path = entries[index]?.path
    if (!path) {
      skipped.push({ path: file.name, reason: 'unresolved' })
      return
    }
    files.push({
      name: file.name,
      type: file.type,
      size: file.size,
      sourcePath: path,
      origin: 'workspace',
      projectId,
      arrayBuffer: () => file.arrayBuffer(),
    })
  })
  if (files.length) {
    try {
      await c.prefillWorkspaceAttachments(files)
      dismissByKey(WORKSPACE_ROOTS_KEY)
    } catch (err) {
      if (!isAbortError(err)) notify.error(err, { key: WORKSPACE_ROOTS_KEY })
      return
    }
  }
  result.value = { imported: 0, bound: 0, attached: files.length, skipped }
  skippedOpen.value = true
}

function removeFile(file: WorkspaceFileGrant): void {
  if (busy.value) return
  void write(() => c.removeWorkspaceFile(file.id))
}
</script>

<template>
  <div class="ws-root">
    <header class="ws-header">
      <h2 class="ws-title">{{ t('workspaceRoots.title') }}</h2>
      <button
        type="button" class="ws-icon-button" :aria-label="t('workspaceRoots.reload')"
        :title="t('workspaceRoots.reload')" :disabled="busy" @click="reload"
      >
        <RefreshCw :size="15" aria-hidden="true" />
      </button>
    </header>

    <div v-if="!project" class="ws-empty">
      <FolderOpen :size="28" aria-hidden="true" class="ws-empty-icon" />
      <p class="ws-empty-title">{{ t('workspaceRoots.noWorkspaceTitle') }}</p>
      <p class="ws-empty-body">{{ t('workspaceRoots.noWorkspaceBody') }}</p>
      <button type="button" class="ws-button" @click="c.openProject()">{{ t('workspaceRoots.openProject') }}</button>
    </div>

    <div v-else class="ws-body">
      <p class="ws-description">{{ t('workspaceRoots.description') }}</p>

      <ul
        class="ws-roots" :class="{ 'ws-dragging': dragging }"
        @dragover.prevent="dragging = true" @dragleave="dragging = false" @drop.prevent="dropTransfer($event.dataTransfer)"
      >
        <li v-for="root in roots" :key="root.id" class="ws-root-row">
          <FolderOpen :size="16" aria-hidden="true" class="ws-root-icon" />
          <span class="ws-root-text">
            <span class="ws-root-name">{{ folderName(root) }}</span>
            <span class="ws-root-path" :title="root.path">{{ root.path }}</span>
          </span>
          <span v-if="root.id === primaryId" class="ws-primary-badge">
            <Star :size="12" aria-hidden="true" />{{ t('workspaceRoots.primary') }}
          </span>
          <button v-else type="button" class="ws-row-action" :disabled="busy" @click="setPrimary(root)">
            {{ t('workspaceRoots.setPrimary') }}
          </button>
          <button
            type="button" class="ws-row-remove"
            :disabled="busy || !!removeBlockedReason(root)"
            :title="removeBlockedReason(root) ?? t('workspaceRoots.remove', { name: folderName(root) })"
            :aria-label="removeBlockedReason(root)
              ? `${t('workspaceRoots.remove', { name: folderName(root) })} — ${removeBlockedReason(root)}`
              : t('workspaceRoots.remove', { name: folderName(root) })"
            @click="remove(root)"
          >
            <Trash2 :size="14" aria-hidden="true" />
          </button>
        </li>
      </ul>

      <section class="ws-files">
        <h3 class="ws-section-title">{{ t('workspaceRoots.filesTitle') }}</h3>
        <ul v-if="files.length" class="ws-file-list">
          <li v-for="file in files" :key="file.id" class="ws-file-row">
            <FileText :size="16" aria-hidden="true" class="ws-root-icon" />
            <span class="ws-root-text">
              <span class="ws-root-name">{{ basenameFromPath(file.path) }}</span>
              <span class="ws-root-path" :title="file.path">{{ file.path }}</span>
            </span>
            <button
              type="button" class="ws-row-remove" :disabled="busy"
              :title="t('workspaceRoots.removeFile', { name: basenameFromPath(file.path) })"
              :aria-label="t('workspaceRoots.removeFile', { name: basenameFromPath(file.path) })"
              @click="removeFile(file)"
            >
              <Trash2 :size="14" aria-hidden="true" />
            </button>
          </li>
        </ul>
        <p v-else class="ws-file-empty">{{ t('workspaceRoots.filesEmpty') }}</p>
        <div class="ws-file-actions">
          <button type="button" class="ws-add" :disabled="busy" @click="chooseFiles('bind')">
            <FilePlus :size="16" aria-hidden="true" />
            {{ t('workspaceRoots.bindFile') }}
          </button>
          <button type="button" class="ws-add" :disabled="busy" @click="chooseFiles('attach')">
            <FilePlus :size="16" aria-hidden="true" />
            {{ t('workspaceRoots.attachFile') }}
          </button>
          <button type="button" class="ws-add ws-add-secondary" :disabled="busy" @click="chooseFiles('copy')">
            <Copy :size="16" aria-hidden="true" />
            {{ t('workspaceRoots.copyFile') }}
          </button>
        </div>
        <input ref="fileInput" class="ws-file-input" type="file" multiple @change="filesPicked" />
      </section>

      <button
        type="button" class="ws-add" data-testid="workspace-add-folder" :disabled="busy || picking"
        @dragover.prevent="dragging = true" @dragleave="dragging = false"
        @drop.stop.prevent="dropTransfer($event.dataTransfer, true)"
        @click="addFolders"
      >
        <LoaderCircle v-if="picking" :size="16" class="animate-spin" aria-hidden="true" />
        <FolderPlus v-else :size="16" aria-hidden="true" />
        {{ picking ? t('workspaceRoots.picking') : t('workspaceRoots.addFolder') }}
      </button>

      <div class="ws-drop" :class="{ 'ws-drop-active': dragging }" @dragover.prevent="dragging = true" @dragleave="dragging = false" @drop.prevent="dropTransfer($event.dataTransfer)">
        <FolderUp :size="16" aria-hidden="true" class="ws-drop-icon" />
        <span class="ws-drop-hint">{{ t('workspaceRoots.dropHint') }}</span>
        <span v-if="copyTarget" class="ws-drop-target" :title="copyTarget">
          {{ t('workspaceRoots.copyInto', { path: copyTarget }) }}
        </span>
      </div>

      <div v-if="inputValidationHint" class="ws-input-hint" data-testid="workspace-input-hint" role="status">
        <span>{{ inputValidationHint }}</span>
        <button type="button" class="ws-row-action" @click="dismissInputHint">{{ t('workspaceRoots.cancel') }}</button>
      </div>

      <div v-if="result" class="ws-summary" role="status" aria-live="polite">
        <p class="ws-summary-line">{{ t('workspaceRoots.imported', { count: result.imported }) }}</p>
        <p v-if="result.bound" class="ws-summary-line">{{ t('workspaceRoots.boundFiles', { count: result.bound }) }}</p>
        <p v-if="result.attached" class="ws-summary-line">{{ t('workspaceRoots.attachedFiles', { count: result.attached }) }}</p>
        <button
          v-if="result.skipped.length" type="button" class="ws-summary-toggle"
          :aria-expanded="skippedOpen ? 'true' : 'false'" @click="skippedOpen = !skippedOpen"
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
          <button v-if="parentCandidates.length" type="button" class="ws-row-action" :disabled="busy" @click="addParentsInstead">
            {{ t('workspaceRoots.addParentInstead') }}
          </button>
          <button type="button" class="ws-row-action" @click="dismissResult">{{ t('workspaceRoots.cancel') }}</button>
        </div>
      </div>

      <div v-if="unchanged" class="ws-notice" role="status">
        <span>{{ t('workspaceRoots.applyNextRun') }}</span>
        <button type="button" class="ws-row-action" @click="reload">{{ t('workspaceRoots.loadLatest') }}</button>
      </div>

      <section class="ws-exposures">
        <div class="ws-section-head">
          <h3 class="ws-section-title">{{ t('workspaceRoots.exposuresTitle') }}</h3>
          <button type="button" class="ws-icon-button" :disabled="exposuresLoading" :title="t('workspaceRoots.reload')" :aria-label="t('workspaceRoots.reload')" @click="loadExposures">
            <LoaderCircle v-if="exposuresLoading" :size="14" class="animate-spin" aria-hidden="true" />
            <RefreshCw v-else :size="14" aria-hidden="true" />
          </button>
        </div>
        <p class="ws-muted">{{ t('workspaceRoots.exposuresHint') }}</p>
        <ul v-if="exposures.length" class="ws-exposure-list">
          <li v-for="exposure in exposures" :key="exposure.id" class="ws-exposure-row">
            <img
              v-if="exposure.media_type.startsWith('image/')"
              class="ws-thumb" :src="api.attachmentContentUrl(exposure.id)" :alt="exposure.file_name" loading="lazy"
            />
            <span v-else class="ws-thumb ws-thumb-placeholder"><FileText :size="18" aria-hidden="true" /></span>
            <span class="ws-exposure-main">
              <span class="ws-exposure-name" :title="exposure.source_path ?? exposure.file_name">{{ exposure.file_name }}</span>
              <span class="ws-exposure-meta">
                {{ exposureSource(exposure) }} · {{ exposureSessionName(exposure.session_id) }} · {{ exposureTime(exposure.created_at) }}
              </span>
              <span class="ws-exposure-state" :class="{ 'is-delivered': exposure.delivered_to_model }">
                {{ exposure.delivered_to_model ? t('workspaceRoots.delivered') : t('workspaceRoots.notDelivered') }}
              </span>
            </span>
          </li>
        </ul>
        <p v-else-if="!exposuresLoading" class="ws-file-empty">{{ t('workspaceRoots.exposuresEmpty') }}</p>
      </section>

      <section class="ws-access">
        <h3 class="ws-access-title">{{ t('workspaceRoots.accessTitle') }}</h3>
        <p class="ws-access-line">
          <span class="ws-access-tag">{{ t('workspaceRoots.accessRead') }}</span>
          <span class="ws-access-text">{{ t('workspaceRoots.accessReadBody') }}</span>
        </p>
        <p class="ws-access-line">
          <span class="ws-access-tag">{{ t('workspaceRoots.accessWrite') }}</span>
          <span class="ws-access-text">{{ permissionLine() }}</span>
        </p>
        <p class="ws-access-note">{{ t('workspaceRoots.applyNextRun') }}</p>
      </section>
    </div>
  </div>
</template>

<style scoped>
.ws-root { display: flex; flex-direction: column; height: 100%; min-height: 0; min-width: 0; }
.ws-header { display: flex; align-items: center; gap: 8px; padding: 12px 14px 8px; }
.ws-title { flex: 1; margin: 0; font-size: 14px; font-weight: 700; color: var(--text-primary); }
.ws-icon-button { display: grid; place-items: center; width: 28px; height: 28px; flex: none; border: 0; border-radius: 7px; background: transparent; color: var(--text-secondary); cursor: pointer; }
.ws-icon-button:hover:not(:disabled) { background: var(--surface-hover); color: var(--text-primary); }
.ws-icon-button:disabled { opacity: .45; cursor: default; }
.ws-icon-button:focus-visible, .ws-row-action:focus-visible, .ws-add:focus-visible,
.ws-button:focus-visible, .ws-summary-toggle:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 2px; }

.ws-empty { display: flex; flex-direction: column; align-items: center; gap: 8px; padding: 32px 20px; text-align: center; }
.ws-empty-icon { color: var(--text-muted); }
.ws-empty-title { margin: 0; font-size: 13px; font-weight: 600; color: var(--text-primary); }
.ws-empty-body { margin: 0; font-size: 12px; line-height: 1.45; color: var(--text-muted); }
.ws-button { margin-top: 4px; padding: 6px 12px; border: 1px solid var(--border-default); border-radius: 8px; background: var(--surface-section); color: var(--text-primary); font-size: 12px; cursor: pointer; }
.ws-button:hover { background: var(--surface-hover); }

.ws-body { display: flex; flex-direction: column; gap: 10px; flex: 1 1 auto; min-height: 0; overflow-y: auto; overscroll-behavior: contain; padding: 0 14px 16px; }
.ws-description { margin: 0; font-size: 11.5px; line-height: 1.5; color: var(--text-muted); }

.ws-roots { list-style: none; margin: 0; padding: 2px; display: flex; flex-direction: column; gap: 6px; border: 1px dashed transparent; border-radius: 10px; }
.ws-dragging { border-color: var(--accent-primary); background: var(--surface-selected); }
.ws-root-row { display: flex; align-items: center; gap: 8px; min-width: 0; padding: 8px 10px; border-radius: 9px; background: var(--surface-section); }
.ws-file-row { display: flex; align-items: center; gap: 8px; min-width: 0; padding: 8px 10px; border-radius: 9px; background: var(--surface-section); }
.ws-root-icon { flex: none; color: var(--text-secondary); }
.ws-root-text { flex: 1; display: grid; gap: 2px; min-width: 0; }
.ws-root-name { font-size: 12px; font-weight: 600; color: var(--text-primary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ws-root-path { font-size: 10.5px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ws-primary-badge { display: inline-flex; align-items: center; gap: 4px; flex: none; font-size: 10.5px; color: var(--accent-primary); white-space: nowrap; }
.ws-row-action { flex: none; border: 0; border-radius: 6px; padding: 4px 6px; background: transparent; color: var(--text-secondary); font-size: 11px; cursor: pointer; white-space: nowrap; }
.ws-row-action:hover:not(:disabled) { background: var(--surface-hover); color: var(--text-primary); }
.ws-row-action:disabled { opacity: .45; cursor: default; }
.ws-row-remove { display: grid; place-items: center; width: 26px; height: 26px; flex: none; border: 0; border-radius: 6px; background: transparent; color: var(--text-secondary); cursor: pointer; }
.ws-row-remove:hover:not(:disabled) { background: var(--surface-hover); color: var(--text-primary); }
.ws-row-remove:disabled { opacity: .45; cursor: default; }
.ws-row-remove:focus-visible { outline: 2px solid var(--accent-primary); outline-offset: 2px; }

.ws-add { display: flex; align-items: center; justify-content: center; gap: 6px; min-height: 44px; border: 1px dashed var(--border-default); border-radius: 9px; background: transparent; color: var(--text-secondary); font-size: 12px; cursor: pointer; }
.ws-add:hover:not(:disabled) { background: var(--surface-hover); color: var(--text-primary); border-color: var(--accent-primary); }
.ws-add:disabled { opacity: .55; cursor: default; }
.ws-add-secondary { min-height: 36px; }

.ws-files { display: flex; flex-direction: column; gap: 6px; padding-top: 8px; border-top: 1px solid var(--border-default); }
.ws-section-title { margin: 0; font-size: 11px; font-weight: 700; color: var(--text-secondary); }
.ws-file-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.ws-file-empty { margin: 0; font-size: 11px; line-height: 1.45; color: var(--text-muted); }
.ws-file-actions { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 6px; }
.ws-file-actions .ws-add { min-width: 0; min-height: 36px; padding: 0 5px; font-size: 10.5px; white-space: nowrap; }
.ws-file-input { display: none; }

.ws-exposures { display: flex; flex-direction: column; gap: 6px; padding-top: 8px; border-top: 1px solid var(--border-default); }
.ws-section-head { display: flex; align-items: center; gap: 6px; }
.ws-section-head .ws-section-title { flex: 1; }
.ws-section-head .ws-icon-button { width: 26px; height: 26px; }
.ws-muted { margin: 0; font-size: 10.5px; line-height: 1.45; color: var(--text-muted); }
.ws-exposure-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; max-height: 240px; overflow-y: auto; }
.ws-exposure-row { display: flex; align-items: center; gap: 8px; min-width: 0; padding: 7px 8px; border-radius: 9px; background: var(--surface-section); }
.ws-thumb { width: 38px; height: 38px; flex: none; object-fit: cover; border-radius: 6px; background: var(--surface-hover); }
.ws-thumb-placeholder { display: grid; place-items: center; color: var(--text-muted); }
.ws-exposure-main { display: grid; gap: 2px; flex: 1; min-width: 0; }
.ws-exposure-name { font-size: 11.5px; font-weight: 600; color: var(--text-primary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ws-exposure-meta { font-size: 10px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ws-exposure-state { font-size: 10px; color: var(--text-muted); }
.ws-exposure-state.is-delivered { color: var(--accent-primary); }

.ws-drop { display: flex; flex-direction: column; align-items: center; gap: 3px; padding: 12px 10px; border: 1px dashed var(--border-muted); border-radius: 9px; color: var(--text-muted); text-align: center; }
.ws-drop-active { border-color: var(--accent-primary); background: var(--surface-selected); color: var(--text-primary); }
.ws-drop-hint { font-size: 11.5px; }
.ws-drop-target { font-size: 10.5px; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

.ws-input-hint { display: flex; align-items: flex-start; gap: 8px; padding: 8px 10px; border-radius: 8px; background: var(--surface-section); color: var(--text-secondary); font-size: 11.5px; line-height: 1.45; overflow-wrap: anywhere; }
.ws-input-hint span { flex: 1; min-width: 0; }

.ws-summary { display: flex; flex-direction: column; gap: 4px; padding: 8px 10px; border-radius: 8px; background: var(--surface-section); }
.ws-summary-line { margin: 0; font-size: 11.5px; color: var(--text-secondary); }
.ws-summary-toggle { align-self: flex-start; border: 0; padding: 2px 0; background: transparent; color: var(--text-secondary); font-size: 11.5px; text-decoration: underline; text-underline-offset: 2px; cursor: pointer; }
.ws-skipped { margin: 0; padding-left: 16px; font-size: 10.5px; color: var(--text-muted); overflow-wrap: anywhere; }
.ws-skipped-path { margin-right: 6px; }
.ws-skipped-reason { color: var(--text-secondary); }
.ws-summary-actions { display: flex; gap: 6px; flex-wrap: wrap; }

.ws-notice { display: flex; align-items: flex-start; gap: 8px; padding: 8px 10px; border-radius: 8px; background: var(--surface-section); color: var(--text-secondary); font-size: 11.5px; line-height: 1.45; }
.ws-notice span { flex: 1; min-width: 0; }

.ws-access { display: flex; flex-direction: column; gap: 6px; padding-top: 8px; border-top: 1px solid var(--border-default); }
.ws-access-title { margin: 0; font-size: 11px; font-weight: 700; color: var(--text-secondary); }
.ws-access-line { display: flex; align-items: baseline; gap: 8px; margin: 0; min-width: 0; }
.ws-access-tag { flex: none; padding: 1px 6px; border-radius: 5px; background: var(--surface-section); font-size: 10.5px; color: var(--text-secondary); white-space: nowrap; }
.ws-access-text { font-size: 11px; line-height: 1.45; color: var(--text-muted); }
.ws-access-note { margin: 0; font-size: 10.5px; line-height: 1.45; color: var(--text-muted); }
</style>
