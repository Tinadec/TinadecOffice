<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import {
  ArrowUp,
  Check,
  Eye,
  EyeOff,
  File,
  Film,
  Folder,
  FolderOpen,
  FolderPlus,
  GitBranch,
  HardDrive,
  Home,
  Image,
  Loader2,
  X,
} from '@lucide/vue'
import { UiButton, UiInput } from '@/components/ui'
import {
  api,
  type FilesystemBrowseEntryDto,
  type FilesystemBrowseRootDto,
} from '@/api'
import {
  isElectronHost,
  registerServerDirectoryPicker,
  type ServerDirectoryPickerOptions,
} from '@/lib/hostConnection'

const IMAGE_EXTENSIONS = new Set(['.png', '.jpg', '.jpeg', '.webp', '.gif', '.avif', '.bmp', '.svg'])
const VIDEO_EXTENSIONS = new Set(['.mp4', '.webm', '.mov', '.m4v', '.ogg', '.ogv', '.mkv'])

const { t } = useI18n()

const open = ref(false)
const title = ref('')
const mode = ref<'directory' | 'file'>('directory')
const multiple = ref(false)
const acceptExtensions = ref<string[]>([])
const nativeFallback = ref<(() => Promise<string[]>) | null>(null)
const nativePicking = ref(false)

const loading = ref(false)
const error = ref<string | null>(null)
const platform = ref('')
const currentPath = ref('')
const pathInput = ref('')
const parentPath = ref<string | null>(null)
const roots = ref<FilesystemBrowseRootDto[]>([])
const entries = ref<FilesystemBrowseEntryDto[]>([])
const selectedPaths = ref<string[]>([])
const showHidden = ref(false)
const filterQuery = ref('')

const creatingFolder = ref(false)
const newFolderName = ref('')
const creatingFolderSubmit = ref(false)
const newFolderInputRef = ref<{ $el?: HTMLInputElement } | null>(null)
const listRef = ref<HTMLElement | null>(null)

let pendingResolve: ((paths: string[]) => void) | null = null
let browseSeq = 0

function normalizeExtensions(exts?: string[]): string[] {
  if (!Array.isArray(exts)) return []
  return exts
    .map((ext) => ext.trim().toLowerCase())
    .filter(Boolean)
    .map((ext) => (ext.startsWith('.') ? ext : `.${ext}`))
}

function fileExtension(name: string): string {
  const idx = name.lastIndexOf('.')
  return idx >= 0 ? name.slice(idx).toLowerCase() : ''
}

function isFolderLike(entry: FilesystemBrowseEntryDto): boolean {
  return Boolean(entry.is_dir || entry.kind === 'drive')
}

function fileIconKind(entry: FilesystemBrowseEntryDto): 'drive' | 'folder' | 'image' | 'video' | 'file' {
  if (entry.kind === 'drive') return 'drive'
  if (entry.is_dir) return 'folder'
  const ext = fileExtension(entry.name)
  if (IMAGE_EXTENSIONS.has(ext)) return 'image'
  if (VIDEO_EXTENSIONS.has(ext)) return 'video'
  return 'file'
}

function formatFileSize(bytes?: number | null): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes < 0) return ''
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GB`
}

const canCreateFolderHere = computed(() =>
  Boolean(currentPath.value) && currentPath.value !== '__roots__',
)

const showNativeEscapeHatch = computed(() =>
  isElectronHost() && typeof nativeFallback.value === 'function',
)

const nativeEscapeLabel = computed(() => {
  const p = platform.value.toLowerCase()
  if (p === 'windows') return t('webDirPicker.useNativeExplorer')
  if (p === 'macos' || p === 'darwin') return t('webDirPicker.useNativeFinder')
  if (p === 'linux') return t('webDirPicker.useNativeFileManager')

  const bridgePlatform = (typeof window !== 'undefined'
    ? ((window as unknown as { tinadec?: { platform?: string } }).tinadec?.platform ?? '')
    : '').toLowerCase()
  if (bridgePlatform === 'win32') return t('webDirPicker.useNativeExplorer')
  if (bridgePlatform === 'darwin') return t('webDirPicker.useNativeFinder')
  if (bridgePlatform === 'linux') return t('webDirPicker.useNativeFileManager')

  const ua = typeof navigator !== 'undefined' ? navigator.userAgent : ''
  if (/win/i.test(ua)) return t('webDirPicker.useNativeExplorer')
  if (/mac/i.test(ua)) return t('webDirPicker.useNativeFinder')
  return t('webDirPicker.useNativeFileManager')
})

const filteredEntries = computed(() => {
  const q = filterQuery.value.trim().toLowerCase()
  const dirs = entries.value.filter((entry) => isFolderLike(entry))
  if (mode.value === 'directory') {
    return q ? dirs.filter((entry) => entry.name.toLowerCase().includes(q)) : dirs
  }
  const exts = acceptExtensions.value
  const files = entries.value.filter((entry) => {
    if (isFolderLike(entry)) return false
    if (exts.length === 0) return true
    const lower = entry.name.toLowerCase()
    return exts.some((ext) => lower.endsWith(ext))
  })
  const combined = [...dirs, ...files]
  return q ? combined.filter((entry) => entry.name.toLowerCase().includes(q)) : combined
})

const canConfirm = computed(() => {
  if (loading.value || nativePicking.value) return false
  if (mode.value === 'file') return selectedPaths.value.length > 0
  return selectedPaths.value.length > 0
    || (Boolean(currentPath.value) && currentPath.value !== '__roots__')
})

const confirmButtonLabel = computed(() => {
  if (mode.value === 'file') {
    return multiple.value && selectedPaths.value.length > 1
      ? t('webDirPicker.confirmSelected')
      : t('webDirPicker.confirmFile')
  }
  return selectedPaths.value.length > 0
    ? t('webDirPicker.confirmSelected')
    : t('webDirPicker.useCurrentFolder')
})

const selectionStatusText = computed(() => {
  if (selectedPaths.value.length > 0) {
    if (selectedPaths.value.length === 1) return selectedPaths.value[0]!
    return t('webDirPicker.selectedCount', { count: selectedPaths.value.length })
  }
  if (mode.value === 'file') {
    return t('webDirPicker.filePickerHint')
  }
  return currentPath.value
})

async function browse(targetPath?: string, options?: { selectAfter?: string[] }) {
  const seq = ++browseSeq
  loading.value = true
  error.value = null
  const previousPath = currentPath.value
  try {
    const payload = await api.browseFilesystem({
      path: targetPath !== undefined && targetPath !== '' ? targetPath : undefined,
      includeFiles: mode.value === 'file',
      showHidden: showHidden.value,
      limit: 500,
    })
    if (seq !== browseSeq || !open.value) return
    platform.value = payload.platform || ''
    currentPath.value = payload.current_path
    pathInput.value = payload.current_path
    parentPath.value = payload.parent_path ?? null
    roots.value = Array.isArray(payload.roots) ? payload.roots : []
    entries.value = Array.isArray(payload.entries) ? payload.entries : []
    if (options?.selectAfter) {
      selectedPaths.value = options.selectAfter
    } else if (payload.current_path !== previousPath) {
      selectedPaths.value = []
      creatingFolder.value = false
      newFolderName.value = ''
    }
  } catch (err) {
    if (seq !== browseSeq || !open.value) return
    error.value = err instanceof Error && err.message ? err.message : t('webDirPicker.loadFailed')
  } finally {
    if (seq === browseSeq) loading.value = false
  }
}

function toggleSelect(entryPath: string) {
  if (!multiple.value) {
    selectedPaths.value = selectedPaths.value[0] === entryPath ? [] : [entryPath]
    return
  }
  if (selectedPaths.value.includes(entryPath)) {
    selectedPaths.value = selectedPaths.value.filter((item) => item !== entryPath)
  } else {
    selectedPaths.value = [...selectedPaths.value, entryPath]
  }
}

function isSelected(entryPath: string): boolean {
  return selectedPaths.value.includes(entryPath)
}

function handleEntryClick(entry: FilesystemBrowseEntryDto) {
  if (mode.value === 'file') {
    if (isFolderLike(entry)) {
      if (!loading.value) void browse(entry.path)
      return
    }
    toggleSelect(entry.path)
    return
  }
  toggleSelect(entry.path)
}

function handleEntryDblClick(entry: FilesystemBrowseEntryDto) {
  if (mode.value === 'file') {
    if (isFolderLike(entry)) return
    if (!multiple.value) {
      selectedPaths.value = [entry.path]
      closeWith([entry.path])
    }
    return
  }
  if (!loading.value) void browse(entry.path)
}

function handleListKeydown(event: KeyboardEvent) {
  if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return
  const container = listRef.value
  if (!container) return
  const rows = Array.from(container.querySelectorAll<HTMLButtonElement>('button.dir-picker-row'))
  if (rows.length === 0) return
  const activeIdx = rows.indexOf(document.activeElement as HTMLButtonElement)
  event.preventDefault()
  const delta = event.key === 'ArrowDown' ? 1 : -1
  const nextIdx = activeIdx < 0
    ? (delta > 0 ? 0 : rows.length - 1)
    : (activeIdx + delta + rows.length) % rows.length
  rows[nextIdx]?.focus()
}

async function openNewFolderInput() {
  if (!canCreateFolderHere.value || loading.value) return
  if (creatingFolder.value) {
    cancelNewFolder()
    return
  }
  creatingFolder.value = true
  newFolderName.value = ''
  error.value = null
  await nextTick()
  newFolderInputRef.value?.$el?.focus()
}

function cancelNewFolder() {
  creatingFolder.value = false
  newFolderName.value = ''
}

async function submitNewFolder() {
  const name = newFolderName.value.trim()
  if (!name || !canCreateFolderHere.value || creatingFolderSubmit.value) return
  creatingFolderSubmit.value = true
  error.value = null
  try {
    const res = await api.createHostDirectory({
      parent_path: currentPath.value,
      name,
    })
    creatingFolder.value = false
    newFolderName.value = ''
    await browse(currentPath.value, {
      selectAfter: mode.value === 'directory' && res?.created_path ? [res.created_path] : undefined,
    })
  } catch (err) {
    error.value = err instanceof Error && err.message ? err.message : t('webDirPicker.mkdirFailed')
  } finally {
    creatingFolderSubmit.value = false
  }
}

async function triggerNativeFallback() {
  if (!showNativeEscapeHatch.value || !nativeFallback.value || nativePicking.value) return
  nativePicking.value = true
  error.value = null
  try {
    const paths = await nativeFallback.value()
    if (Array.isArray(paths) && paths.length > 0) {
      closeWith(paths)
    }
  } catch (err) {
    error.value = err instanceof Error && err.message ? err.message : t('webDirPicker.loadFailed')
  } finally {
    nativePicking.value = false
  }
}

function confirmSelection() {
  if (!canConfirm.value) return
  if (mode.value === 'file') {
    if (selectedPaths.value.length === 0) return
    closeWith([...selectedPaths.value])
    return
  }
  const result = selectedPaths.value.length > 0
    ? [...selectedPaths.value]
    : currentPath.value && currentPath.value !== '__roots__'
      ? [currentPath.value]
      : []
  closeWith(result)
}

function closeWith(result: string[]) {
  open.value = false
  creatingFolder.value = false
  newFolderName.value = ''
  nativeFallback.value = null
  nativePicking.value = false
  const resolve = pendingResolve
  pendingResolve = null
  resolve?.(result)
}

function handleOpenChange(nextOpen: boolean) {
  if (!nextOpen && open.value) {
    if (nativePicking.value) return
    closeWith([])
  }
}

function handleEscapeKeyDown(event: KeyboardEvent) {
  if (nativePicking.value) {
    event.preventDefault()
    return
  }
  if (creatingFolder.value) {
    event.preventDefault()
    cancelNewFolder()
  }
}

function handleInteractOutside(event: Event) {
  if (nativePicking.value) {
    event.preventDefault()
  }
}

function openPicker(options: ServerDirectoryPickerOptions): Promise<string[]> {
  if (pendingResolve) {
    pendingResolve([])
    pendingResolve = null
  }
  mode.value = options.mode === 'file' ? 'file' : 'directory'
  title.value = options.title
    || (mode.value === 'file' ? t('webDirPicker.selectFileTitle') : t('webDirPicker.defaultTitle'))
  multiple.value = Boolean(options.multiple)
  acceptExtensions.value = normalizeExtensions(options.acceptExtensions)
  nativeFallback.value = typeof options.nativeFallback === 'function' ? options.nativeFallback : null
  nativePicking.value = false
  selectedPaths.value = []
  filterQuery.value = ''
  creatingFolder.value = false
  newFolderName.value = ''
  error.value = null
  currentPath.value = ''
  pathInput.value = ''
  parentPath.value = null
  entries.value = []
  open.value = true
  void browse(options.initialPath)
  return new Promise<string[]>((resolve) => {
    pendingResolve = resolve
  })
}

onMounted(() => {
  registerServerDirectoryPicker(openPicker)
})

onBeforeUnmount(() => {
  registerServerDirectoryPicker(null)
  if (pendingResolve) {
    pendingResolve([])
    pendingResolve = null
  }
})
</script>

<template>
  <DialogRoot :open="open" @update:open="handleOpenChange">
    <DialogPortal>
      <DialogOverlay class="dir-picker-backdrop" />
      <DialogContent
        class="dir-picker-modal"
        data-testid="server-directory-picker-modal"
        @escape-key-down="handleEscapeKeyDown"
        @interact-outside="handleInteractOutside"
      >
        <header class="dir-picker-header">
          <DialogTitle class="dir-picker-title">{{ title }}</DialogTitle>
          <DialogDescription class="sr-only">{{ t('webDirPicker.dialogDescription') }}</DialogDescription>
          <DialogClose as-child>
            <button
              type="button"
              class="dir-picker-close"
              :aria-label="t('common.close')"
              :disabled="nativePicking"
            >
              <X :size="16" />
            </button>
          </DialogClose>
        </header>

        <div v-if="roots.length > 0" class="dir-picker-roots">
          <button
            v-for="root in roots"
            :key="root.path"
            type="button"
            class="dir-picker-root-chip"
            :class="{ active: currentPath === root.path }"
            @click="browse(root.path)"
          >
            <Home v-if="root.kind === 'home'" :size="13" />
            <HardDrive v-else :size="13" />
            <span>{{ root.name }}</span>
          </button>
        </div>

        <form class="dir-picker-nav" @submit.prevent="browse(pathInput)">
          <UiButton
            type="button"
            variant="outline"
            size="icon"
            :disabled="!parentPath || loading"
            :title="t('webDirPicker.parentFolder')"
            :aria-label="t('webDirPicker.parentFolder')"
            @click="parentPath && browse(parentPath)"
          >
            <ArrowUp :size="15" />
          </UiButton>
          <UiInput
            v-model="pathInput"
            class="dir-picker-path-input"
            data-testid="dir-picker-path-input"
            :placeholder="t('webDirPicker.pathPlaceholder')"
          />
          <UiButton type="submit" variant="outline" size="sm" :disabled="loading">
            {{ t('webDirPicker.go') }}
          </UiButton>
          <UiButton
            v-if="canCreateFolderHere"
            type="button"
            variant="outline"
            size="sm"
            data-testid="dir-picker-new-folder-btn"
            :disabled="loading || creatingFolderSubmit"
            :title="t('webDirPicker.newFolder')"
            @click="openNewFolderInput"
          >
            <FolderPlus :size="14" />
            <span>{{ t('webDirPicker.newFolder') }}</span>
          </UiButton>
          <UiButton
            type="button"
            variant="ghost"
            size="icon"
            :title="showHidden ? t('webDirPicker.hideHidden') : t('webDirPicker.showHidden')"
            :aria-label="showHidden ? t('webDirPicker.hideHidden') : t('webDirPicker.showHidden')"
            @click="showHidden = !showHidden; browse(currentPath)"
          >
            <EyeOff v-if="showHidden" :size="15" />
            <Eye v-else :size="15" />
          </UiButton>
        </form>

        <form
          v-if="creatingFolder"
          class="dir-picker-mkdir-row"
          data-testid="dir-picker-mkdir-form"
          @submit.prevent="submitNewFolder"
        >
          <UiInput
            ref="newFolderInputRef"
            v-model="newFolderName"
            class="dir-picker-mkdir-input"
            data-testid="dir-picker-mkdir-input"
            :placeholder="t('webDirPicker.newFolderPlaceholder')"
            :disabled="creatingFolderSubmit"
            @keydown.esc.stop.prevent="cancelNewFolder"
          />
          <UiButton
            type="submit"
            size="sm"
            data-testid="dir-picker-mkdir-submit"
            :disabled="creatingFolderSubmit || !newFolderName.trim()"
          >
            {{ t('webDirPicker.newFolderConfirm') }}
          </UiButton>
          <UiButton
            type="button"
            variant="ghost"
            size="sm"
            :disabled="creatingFolderSubmit"
            @click="cancelNewFolder"
          >
            {{ t('common.cancel') }}
          </UiButton>
        </form>

        <div class="dir-picker-filter">
          <UiInput
            v-model="filterQuery"
            data-testid="dir-picker-filter-input"
            :placeholder="mode === 'file' ? t('webDirPicker.filterFilesPlaceholder') : t('webDirPicker.filterPlaceholder')"
          />
        </div>

        <div v-if="error" class="dir-picker-error" role="alert" data-testid="dir-picker-error">
          <span class="dir-picker-error-text">{{ error }}</span>
          <UiButton
            v-if="showNativeEscapeHatch"
            type="button"
            variant="outline"
            size="sm"
            class="dir-picker-error-native-btn"
            data-testid="dir-picker-error-native-btn"
            :disabled="nativePicking"
            @click="triggerNativeFallback"
          >
            <FolderOpen :size="13" />
            <span>{{ nativeEscapeLabel }}</span>
          </UiButton>
        </div>

        <div
          ref="listRef"
          class="dir-picker-list"
          data-testid="dir-picker-list"
          @keydown="handleListKeydown"
        >
          <div v-if="loading" class="dir-picker-state">
            <Loader2 :size="18" class="spin" />
            <span>{{ t('common.loading') }}</span>
          </div>
          <div v-else-if="filteredEntries.length === 0" class="dir-picker-state">
            <span>{{ mode === 'file' ? t('webDirPicker.emptyFilesFolder') : t('webDirPicker.emptyFolder') }}</span>
          </div>
          <button
            v-for="entry in filteredEntries"
            v-else
            :key="entry.path"
            type="button"
            class="dir-picker-row"
            :class="{ selected: isSelected(entry.path), 'is-file': !isFolderLike(entry) }"
            :data-entry-path="entry.path"
            :data-entry-kind="entry.kind"
            @click="handleEntryClick(entry)"
            @dblclick="handleEntryDblClick(entry)"
          >
            <HardDrive v-if="fileIconKind(entry) === 'drive'" :size="16" class="dir-picker-icon" />
            <Folder v-else-if="fileIconKind(entry) === 'folder'" :size="16" class="dir-picker-icon" />
            <Image v-else-if="fileIconKind(entry) === 'image'" :size="16" class="dir-picker-icon" />
            <Film v-else-if="fileIconKind(entry) === 'video'" :size="16" class="dir-picker-icon" />
            <File v-else :size="16" class="dir-picker-icon" />
            <span class="dir-picker-name">{{ entry.name }}</span>
            <span v-if="!isFolderLike(entry) && entry.size_bytes != null" class="dir-picker-size">
              {{ formatFileSize(entry.size_bytes) }}
            </span>
            <span v-if="entry.is_git_repo" class="dir-picker-git">
              <GitBranch :size="11" />
              <span>{{ t('webDirPicker.gitBadge') }}</span>
            </span>
            <Check v-if="isSelected(entry.path)" :size="15" class="dir-picker-check" />
          </button>
        </div>

        <div class="dir-picker-status-bar">
          <span class="dir-picker-selection-hint" :title="selectionStatusText">
            {{ selectionStatusText }}
          </span>
          <span v-if="mode === 'file' && acceptExtensions.length > 0" class="dir-picker-ext-pill">
            {{ acceptExtensions.join(' ') }}
          </span>
        </div>

        <footer class="dir-picker-footer">
          <div class="dir-picker-footer-left">
            <UiButton
              v-if="showNativeEscapeHatch"
              type="button"
              variant="outline"
              size="sm"
              class="dir-picker-native-btn"
              data-testid="dir-picker-native-fallback"
              :disabled="nativePicking"
              @click="triggerNativeFallback"
            >
              <FolderOpen :size="14" />
              <span>{{ nativeEscapeLabel }}</span>
            </UiButton>
          </div>
          <div class="dir-picker-actions">
            <UiButton
              type="button"
              variant="ghost"
              size="sm"
              :disabled="nativePicking"
              @click="closeWith([])"
            >
              {{ t('common.cancel') }}
            </UiButton>
            <UiButton
              type="button"
              size="sm"
              data-testid="dir-picker-confirm"
              :disabled="!canConfirm"
              @click="confirmSelection"
            >
              {{ confirmButtonLabel }}
            </UiButton>
          </div>
        </footer>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>

<style scoped>
.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

.dir-picker-backdrop {
  position: fixed;
  inset: 0;
  z-index: 1400;
  background: color-mix(in srgb, var(--bg-overlay, rgba(0, 0, 0, 0.65)) 72%, transparent);
  backdrop-filter: blur(6px);
}

.dir-picker-modal {
  position: fixed;
  top: 50%;
  left: 50%;
  z-index: 1401;
  transform: translate(-50%, -50%);
  width: min(680px, calc(100vw - 32px));
  max-height: min(640px, calc(100vh - 40px));
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 18px;
  border-radius: var(--radius-lg, 12px);
  border: 1px solid var(--border-muted, var(--border-default));
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-primary);
  box-shadow: var(--shadow-panel, 0 20px 50px rgba(0, 0, 0, 0.35));
  outline: none;
}

.dir-picker-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.dir-picker-title {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
  color: var(--text-primary);
}

.dir-picker-close {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 1px solid transparent;
  border-radius: 7px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.dir-picker-close:hover:not(:disabled) {
  background: var(--surface-hover, var(--bg-hover));
  color: var(--text-primary);
}

.dir-picker-close:disabled {
  opacity: 0.45;
  cursor: default;
}

.dir-picker-roots {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}

.dir-picker-root-chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 10px;
  font-size: 12px;
  border-radius: 999px;
  border: 1px solid var(--border-muted, var(--border-default));
  background: var(--surface-section, var(--bg-primary));
  color: var(--text-secondary);
  cursor: pointer;
  transition: border-color 140ms ease, color 140ms ease, background 140ms ease;
}

.dir-picker-root-chip.active,
.dir-picker-root-chip:hover {
  border-color: var(--accent-primary);
  color: var(--text-primary);
  background: color-mix(in srgb, var(--accent-primary) 10%, var(--surface-section, var(--bg-primary)));
}

.dir-picker-nav {
  display: flex;
  align-items: center;
  gap: 6px;
}

.dir-picker-path-input {
  flex: 1;
  min-width: 0;
}

.dir-picker-mkdir-row {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  border-radius: 9px;
  border: 1px solid var(--border-muted, var(--border-default));
  background: var(--surface-section, var(--bg-primary));
}

.dir-picker-mkdir-input {
  flex: 1;
  min-width: 0;
}

.dir-picker-error {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid color-mix(in srgb, var(--text-error, #ef4444) 32%, transparent);
  background: var(--bg-error, rgba(220, 38, 38, 0.1));
  color: var(--text-error, #ef4444);
  font-size: 12px;
}

.dir-picker-error-text {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
}

.dir-picker-error-native-btn {
  flex-shrink: 0;
}

.dir-picker-list {
  flex: 1;
  min-height: 220px;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 4px;
  border-radius: 10px;
  border: 1px solid var(--border-muted, var(--border-default));
  background: var(--bg-primary);
}

.dir-picker-state {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  color: var(--text-muted);
  font-size: 12px;
}

.dir-picker-row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 7px 10px;
  border: 1px solid transparent;
  border-radius: 7px;
  background: transparent;
  color: var(--text-primary);
  font-size: 13px;
  text-align: left;
  cursor: pointer;
}

.dir-picker-row:hover {
  background: var(--surface-hover, var(--bg-hover));
}

.dir-picker-row:focus-visible {
  outline: none;
  border-color: var(--accent-primary);
}

.dir-picker-row.selected {
  border-color: color-mix(in srgb, var(--accent-primary) 42%, transparent);
  background: var(--surface-selected, var(--bg-active, rgba(139, 92, 246, 0.14)));
}

.dir-picker-icon {
  flex-shrink: 0;
  color: var(--text-secondary);
}

.dir-picker-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.dir-picker-size {
  flex-shrink: 0;
  font-size: 11px;
  font-variant-numeric: tabular-nums;
  color: var(--text-muted);
}

.dir-picker-git {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 1px 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted, var(--border-default));
  background: var(--surface-section, var(--bg-secondary));
  color: var(--text-secondary);
  font-size: 11px;
  line-height: 1.4;
}

.dir-picker-check {
  flex-shrink: 0;
  color: var(--accent-primary);
}

.dir-picker-status-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 0 2px;
}

.dir-picker-selection-hint {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: var(--text-secondary);
}

.dir-picker-ext-pill {
  flex-shrink: 0;
  max-width: 240px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  padding: 1px 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted, var(--border-default));
  background: var(--surface-section, var(--bg-primary));
  color: var(--text-muted);
  font-family: var(--font-mono);
  font-size: 11px;
}

.dir-picker-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding-top: 6px;
  border-top: 1px solid var(--border-muted, var(--border-default));
}

.dir-picker-footer-left {
  display: flex;
  align-items: center;
  min-width: 0;
}

.dir-picker-native-btn {
  white-space: nowrap;
}

.dir-picker-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}

.spin {
  animation: dir-picker-spin 0.9s linear infinite;
}

@keyframes dir-picker-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
