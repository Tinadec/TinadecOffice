<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowUp,
  Check,
  Eye,
  EyeOff,
  Folder,
  GitBranch,
  HardDrive,
  Home,
  Loader2,
  X,
} from '@lucide/vue'
import { UiBadge, UiButton, UiInput } from '@/components/ui'
import {
  registerServerDirectoryPicker,
  resolveWebGatewayBase,
  type ServerDirectoryPickerOptions,
} from '@/lib/hostConnection'

interface FsRootItem {
  name: string
  path: string
  kind: 'home' | 'drive' | string
}

interface FsEntryItem {
  name: string
  path: string
  kind: 'drive' | 'directory' | 'file' | string
  is_dir: boolean
  has_children?: boolean
  is_git_repo?: boolean
  is_hidden?: boolean
}

interface FsBrowseResponse {
  platform: string
  separator: string
  home_path: string
  current_path: string
  parent_path?: string | null
  roots: FsRootItem[]
  entries: FsEntryItem[]
  truncated?: boolean
}

const { t } = useI18n()

const open = ref(false)
const title = ref('')
const multiple = ref(false)
const loading = ref(false)
const error = ref<string | null>(null)
const currentPath = ref('')
const pathInput = ref('')
const parentPath = ref<string | null>(null)
const roots = ref<FsRootItem[]>([])
const entries = ref<FsEntryItem[]>([])
const selectedPaths = ref<string[]>([])
const showHidden = ref(false)
const filterQuery = ref('')

let pendingResolve: ((paths: string[]) => void) | null = null

const filteredEntries = computed(() => {
  const q = filterQuery.value.trim().toLowerCase()
  const dirs = entries.value.filter((entry) => entry.is_dir || entry.kind === 'drive')
  if (!q) return dirs
  return dirs.filter((entry) => entry.name.toLowerCase().includes(q))
})

async function browse(targetPath?: string) {
  loading.value = true
  error.value = null
  try {
    const origin = resolveWebGatewayBase()
    const params = new URLSearchParams({
      include_files: 'false',
      show_hidden: showHidden.value ? 'true' : 'false',
      limit: '500',
    })
    if (targetPath !== undefined && targetPath !== '') {
      params.set('path', targetPath)
    }
    const response = await fetch(`${origin}/api/v1/fs/browse?${params.toString()}`, {
      credentials: 'include',
      headers: { accept: 'application/json' },
    })
    const payload = (await response.json().catch(() => null)) as FsBrowseResponse & { detail?: string; message?: string } | null
    if (!response.ok || !payload) {
      error.value = payload?.detail || payload?.message || t('webDirPicker.loadFailed')
      return
    }
    currentPath.value = payload.current_path
    pathInput.value = payload.current_path
    parentPath.value = payload.parent_path ?? null
    roots.value = Array.isArray(payload.roots) ? payload.roots : []
    entries.value = Array.isArray(payload.entries) ? payload.entries : []
  } catch {
    error.value = t('webDirPicker.loadFailed')
  } finally {
    loading.value = false
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

function confirmSelection() {
  const result = selectedPaths.value.length > 0
    ? [...selectedPaths.value]
    : currentPath.value && currentPath.value !== '__roots__'
      ? [currentPath.value]
      : []
  closeWith(result)
}

function closeWith(result: string[]) {
  open.value = false
  const resolve = pendingResolve
  pendingResolve = null
  resolve?.(result)
}

function handleKeydown(event: KeyboardEvent) {
  if (!open.value) return
  if (event.key === 'Escape') {
    event.preventDefault()
    closeWith([])
  }
}

function openPicker(options: ServerDirectoryPickerOptions): Promise<string[]> {
  if (pendingResolve) {
    pendingResolve([])
    pendingResolve = null
  }
  title.value = options.title || t('webDirPicker.defaultTitle')
  multiple.value = Boolean(options.multiple)
  selectedPaths.value = []
  filterQuery.value = ''
  error.value = null
  open.value = true
  void browse(options.initialPath)
  return new Promise<string[]>((resolve) => {
    pendingResolve = resolve
  })
}

onMounted(() => {
  registerServerDirectoryPicker(openPicker)
  document.addEventListener('keydown', handleKeydown)
})

onBeforeUnmount(() => {
  registerServerDirectoryPicker(null)
  document.removeEventListener('keydown', handleKeydown)
  if (pendingResolve) {
    pendingResolve([])
    pendingResolve = null
  }
})
</script>

<template>
  <div v-if="open" class="dir-picker-backdrop" role="dialog" aria-modal="true" :aria-label="title" @click.self="closeWith([])">
    <div class="dir-picker-modal">
      <header class="dir-picker-header">
        <h2>{{ title }}</h2>
        <button class="dir-picker-close" :aria-label="t('common.close')" @click="closeWith([])">
          <X :size="16" />
        </button>
      </header>

      <div v-if="roots.length > 0" class="dir-picker-roots">
        <button
          v-for="root in roots"
          :key="root.path"
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
          @click="parentPath && browse(parentPath)"
        >
          <ArrowUp :size="15" />
        </UiButton>
        <UiInput
          v-model="pathInput"
          class="dir-picker-path-input"
          :placeholder="t('webDirPicker.pathPlaceholder')"
        />
        <UiButton type="submit" variant="outline" size="sm" :disabled="loading">
          {{ t('webDirPicker.go') }}
        </UiButton>
        <UiButton
          type="button"
          variant="ghost"
          size="icon"
          :title="showHidden ? t('webDirPicker.hideHidden') : t('webDirPicker.showHidden')"
          @click="showHidden = !showHidden; browse(currentPath)"
        >
          <EyeOff v-if="showHidden" :size="15" />
          <Eye v-else :size="15" />
        </UiButton>
      </form>

      <div class="dir-picker-filter">
        <UiInput
          v-model="filterQuery"
          :placeholder="t('webDirPicker.filterPlaceholder')"
        />
      </div>

      <div v-if="error" class="dir-picker-error" role="alert">
        {{ error }}
      </div>

      <div class="dir-picker-list">
        <div v-if="loading" class="dir-picker-state">
          <Loader2 :size="18" class="spin" />
          <span>{{ t('common.loading') }}</span>
        </div>
        <div v-else-if="filteredEntries.length === 0" class="dir-picker-state">
          <span>{{ t('webDirPicker.emptyFolder') }}</span>
        </div>
        <button
          v-for="entry in filteredEntries"
          v-else
          :key="entry.path"
          type="button"
          class="dir-picker-row"
          :class="{ selected: isSelected(entry.path) }"
          @click="toggleSelect(entry.path)"
          @dblclick="browse(entry.path)"
        >
          <HardDrive v-if="entry.kind === 'drive'" :size="16" class="dir-picker-icon" />
          <Folder v-else :size="16" class="dir-picker-icon" />
          <span class="dir-picker-name">{{ entry.name }}</span>
          <UiBadge v-if="entry.is_git_repo" variant="secondary" class="dir-picker-git">
            <GitBranch :size="11" />
            <span>Git</span>
          </UiBadge>
          <Check v-if="isSelected(entry.path)" :size="15" class="dir-picker-check" />
        </button>
      </div>

      <footer class="dir-picker-footer">
        <span class="dir-picker-selection-hint">
          {{ selectedPaths.length > 0
            ? t('webDirPicker.selectedCount', { count: selectedPaths.length })
            : currentPath }}
        </span>
        <div class="dir-picker-actions">
          <UiButton variant="ghost" size="sm" @click="closeWith([])">
            {{ t('common.cancel') }}
          </UiButton>
          <UiButton size="sm" :disabled="loading" @click="confirmSelection">
            {{ selectedPaths.length > 0 ? t('webDirPicker.confirmSelected') : t('webDirPicker.useCurrentFolder') }}
          </UiButton>
        </div>
      </footer>
    </div>
  </div>
</template>

<style scoped>
.dir-picker-backdrop {
  position: fixed;
  inset: 0;
  z-index: 250;
  display: grid;
  place-items: center;
  padding: 20px;
  background: rgba(0, 0, 0, 0.5);
  backdrop-filter: blur(4px);
}

.dir-picker-modal {
  width: min(640px, 100%);
  max-height: min(600px, calc(100vh - 40px));
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 18px;
  border-radius: var(--radius-lg, 12px);
  border: 1px solid var(--border-default);
  background: var(--surface-raised, var(--bg-secondary));
  color: var(--text-primary);
  box-shadow: var(--shadow-panel, 0 20px 50px rgba(0, 0, 0, 0.35));
}

.dir-picker-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.dir-picker-header h2 {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
}

.dir-picker-close {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.dir-picker-close:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
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
  border: 1px solid var(--border-default);
  background: var(--bg-primary);
  color: var(--text-secondary);
  cursor: pointer;
}

.dir-picker-root-chip.active,
.dir-picker-root-chip:hover {
  border-color: var(--accent-primary);
  color: var(--text-primary);
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

.dir-picker-error {
  padding: 8px 10px;
  border-radius: 6px;
  background: var(--bg-error, rgba(220, 38, 38, 0.12));
  color: var(--text-error, #ef4444);
  font-size: 12px;
}

.dir-picker-list {
  flex: 1;
  min-height: 220px;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 4px;
  border-radius: 8px;
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
  font-size: 12.5px;
}

.dir-picker-row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 7px 10px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary);
  font-size: 13px;
  text-align: left;
  cursor: pointer;
}

.dir-picker-row:hover {
  background: var(--bg-hover);
}

.dir-picker-row.selected {
  background: var(--bg-active, rgba(139, 92, 246, 0.16));
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

.dir-picker-git {
  gap: 4px;
  font-size: 11px;
}

.dir-picker-check {
  flex-shrink: 0;
  color: var(--accent-primary);
}

.dir-picker-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
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

.dir-picker-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.spin {
  animation: dir-picker-spin 0.9s linear infinite;
}

@keyframes dir-picker-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
