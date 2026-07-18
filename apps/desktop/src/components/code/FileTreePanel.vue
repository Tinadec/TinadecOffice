<script setup lang="ts">
import {
  ChevronDown,
  ChevronRight,
  File,
  FileCode,
  FileText,
  Folder,
  FolderOpen,
  MoreVertical,
  Pencil,
  RefreshCw,
  Search,
  Trash2,
} from '@lucide/vue'
import { computed, ref, watch } from 'vue'
import { api, type ApprovalDto } from '@/api'
import { UiButton, UiInput, UiScrollArea } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'

interface DirEntry {
  name: string
  is_dir: boolean
  is_file: boolean
  size_bytes: number | null
}

interface TreeNode {
  name: string
  path: string
  isDir: boolean
  size: number | null
  children: TreeNode[]
  loaded: boolean
  loading: boolean
}

interface FlatNode {
  node: TreeNode
  depth: number
}

const props = defineProps<{
  cwd: string
  approvals?: ApprovalDto[]
  selectedSessionId?: string | null
}>()
const { notify } = useNotifications()

const emit = defineEmits<{
  select: [path: string]
  'approval-created': [approval: ApprovalDto]
}>()

const rootPath = ref('.')
const tree = ref<TreeNode[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const expandedPaths = ref<Set<string>>(new Set())
const selectedPath = ref<string | null>(null)
const searchQuery = ref('')
const searchResults = ref<TreeNode[] | null>(null)
const searching = ref(false)
const contextMenuPath = ref<string | null>(null)
const showHidden = ref(false)

function entryToNode(entry: DirEntry, parentPath: string): TreeNode {
  const path = parentPath === '.' ? entry.name : `${parentPath}/${entry.name}`
  return {
    name: entry.name,
    path,
    isDir: entry.is_dir,
    size: entry.size_bytes ?? null,
    children: [],
    loaded: false,
    loading: false,
  }
}

async function loadDirectory(dirPath: string): Promise<DirEntry[]> {
  const result = await api.listDirectory(props.cwd, dirPath)
  const data = result.data as { entries?: DirEntry[] }
  const entries = Array.isArray(data?.entries) ? data.entries : []
  return showHidden.value ? entries : entries.filter((e) => !e.name.startsWith('.'))
}

async function expandNode(node: TreeNode): Promise<void> {
  if (!node.isDir || node.loaded || node.loading) return
  node.loading = true
  try {
    const entries = await loadDirectory(node.path)
    node.children = entries.map((e) => entryToNode(e, node.path))
    node.loaded = true
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to load directory'
  } finally {
    node.loading = false
  }
}

function toggleExpand(node: TreeNode): void {
  const next = new Set(expandedPaths.value)
  if (next.has(node.path)) {
    next.delete(node.path)
  } else {
    next.add(node.path)
    void expandNode(node)
  }
  expandedPaths.value = next
}

function isExpanded(path: string): boolean {
  return expandedPaths.value.has(path)
}

function handleNodeClick(node: TreeNode): void {
  if (node.isDir) {
    toggleExpand(node)
    return
  }
  selectedPath.value = node.path
  emit('select', node.path)
}

async function refresh(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    const entries = await loadDirectory(rootPath.value)
    tree.value = entries.map((e) => entryToNode(e, rootPath.value))
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to load directory'
  } finally {
    loading.value = false
  }
}

async function runSearch(): Promise<void> {
  const q = searchQuery.value.trim()
  if (!q) {
    searchResults.value = null
    return
  }
  searching.value = true
  error.value = null
  try {
    const result = await api.globSearch(props.cwd, q)
    const data = result.data as { matches?: Array<{ path: string; is_dir?: boolean; is_file?: boolean }> }
    const matches = Array.isArray(data?.matches) ? data.matches : []
    searchResults.value = matches.map((m) => ({
      name: m.path.split(/[\\/]/).pop() ?? m.path,
      path: m.path,
      isDir: m.is_dir ?? false,
      size: null,
      children: [],
      loaded: false,
      loading: false,
    }))
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Search failed'
  } finally {
    searching.value = false
  }
}

function clearSearch(): void {
  searchQuery.value = ''
  searchResults.value = null
}

function getFileIcon(name: string, isDir: boolean) {
  if (isDir) return Folder
  const ext = name.split('.').pop()?.toLowerCase() ?? ''
  const codeExts = ['ts', 'js', 'tsx', 'jsx', 'vue', 'py', 'rs', 'go', 'java', 'c', 'cpp', 'cs', 'rb', 'php', 'swift', 'kt', 'scala']
  const textExts = ['md', 'txt', 'json', 'yml', 'yaml', 'toml', 'ini', 'cfg', 'xml', 'html', 'css', 'scss']
  if (codeExts.includes(ext)) return FileCode
  if (textExts.includes(ext)) return FileText
  return File
}

function formatSize(bytes: number | null): string {
  if (bytes === null) return ''
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function openContextMenu(path: string): void {
  contextMenuPath.value = contextMenuPath.value === path ? null : path
}

function closeContextMenu(): void {
  contextMenuPath.value = null
}

async function requestDeleteApproval(node: TreeNode): Promise<void> {
  closeContextMenu()
  if (!props.selectedSessionId) {
    error.value = 'A session is required to request file deletion approval.'
    return
  }
  try {
    const approval = await api.createApproval({
      session_id: props.selectedSessionId,
      kind: 'code',
      summary: `Delete file: ${node.path}`,
      command: `rm ${node.path}`,
      cwd: props.cwd,
    })
    emit('approval-created', approval)
  } catch (err) {
    notify.error(err, { title: 'Failed to create approval' })
  }
}

async function requestRenameApproval(node: TreeNode): Promise<void> {
  closeContextMenu()
  if (!props.selectedSessionId) {
    error.value = 'A session is required to request file rename approval.'
    return
  }
  try {
    const approval = await api.createApproval({
      session_id: props.selectedSessionId,
      kind: 'code',
      summary: `Rename file: ${node.path}`,
      command: `mv ${node.path} <new_name>`,
      cwd: props.cwd,
    })
    emit('approval-created', approval)
  } catch (err) {
    notify.error(err, { title: 'Failed to create approval' })
  }
}

function flattenTree(nodes: TreeNode[], depth: number, acc: FlatNode[]): FlatNode[] {
  for (const node of nodes) {
    acc.push({ node, depth })
    if (node.isDir && isExpanded(node.path) && node.children.length > 0) {
      flattenTree(node.children, depth + 1, acc)
    }
  }
  return acc
}

const flatList = computed<FlatNode[]>(() => {
  if (searchResults.value) {
    return searchResults.value.map((node) => ({ node, depth: 0 }))
  }
  return flattenTree(tree.value, 0, [])
})

watch(() => props.cwd, () => {
  expandedPaths.value = new Set()
  void refresh()
}, { immediate: true })
</script>

<template>
  <div class="flex h-full flex-col">
    <div class="flex items-center gap-2 border-b border-border p-2">
      <Search :size="14" class="text-muted-foreground" />
      <UiInput
        v-model="searchQuery"
        placeholder="glob: **/*.ts"
        class="h-7 text-xs"
        @keydown.enter="runSearch"
      />
      <UiButton variant="ghost" size="icon" class="h-7 w-7 shrink-0" title="Search" :disabled="searching" @click="runSearch">
        <RefreshCw :size="13" :class="{ 'animate-spin': searching }" />
      </UiButton>
    </div>

    <div class="flex items-center justify-between border-b border-border px-2 py-1">
      <span class="truncate text-xs font-medium text-muted-foreground">{{ rootPath }}</span>
      <UiButton variant="ghost" size="icon" class="h-6 w-6 shrink-0" title="Refresh" :disabled="loading" @click="refresh">
        <RefreshCw :size="12" :class="{ 'animate-spin': loading }" />
      </UiButton>
    </div>

    <div v-if="error" class="px-3 py-2 text-xs text-destructive">{{ error }}</div>

    <UiScrollArea class="flex-1">
      <div class="code-tree py-1">
        <div v-if="loading && tree.length === 0" class="px-3 py-4 text-center text-xs text-muted-foreground">
          Loading...
        </div>

        <template v-else-if="flatList.length === 0">
          <div class="px-3 py-4 text-center text-xs text-muted-foreground">
            {{ searchResults ? 'No matches found.' : 'Directory is empty.' }}
          </div>
        </template>

        <template v-else>
          <div
            v-for="item in flatList"
            :key="item.node.path"
            class="code-tree-row"
          >
            <button
              class="code-tree-item"
              :class="{ active: selectedPath === item.node.path }"
              :style="{ paddingLeft: `${item.depth * 12 + 8}px` }"
              @click="handleNodeClick(item.node)"
              @contextmenu.prevent="openContextMenu(item.node.path)"
            >
              <component
                :is="item.node.isDir ? (isExpanded(item.node.path) ? ChevronDown : ChevronRight) : null"
                v-if="item.node.isDir"
                :size="12"
                class="code-tree-chevron"
              />
              <span v-else class="code-tree-chevron-spacer" />
              <component
                :is="item.node.isDir ? (isExpanded(item.node.path) ? FolderOpen : Folder) : getFileIcon(item.node.name, false)"
                :size="14"
                class="code-tree-icon"
              />
              <span class="code-tree-label">{{ item.node.name }}</span>
              <span v-if="item.node.size !== null && !item.node.isDir" class="code-tree-size">
                {{ formatSize(item.node.size) }}
              </span>
              <span
                class="code-tree-menu-trigger"
                @click.stop="openContextMenu(item.node.path)"
              >
                <MoreVertical :size="12" />
              </span>
            </button>

            <div v-if="contextMenuPath === item.node.path" class="code-tree-context-menu">
              <button class="code-tree-context-item" @click="handleNodeClick(item.node); closeContextMenu()">
                <File :size="12" /> Open
              </button>
              <button class="code-tree-context-item" @click="requestRenameApproval(item.node)">
                <Pencil :size="12" /> Rename (approval)
              </button>
              <button class="code-tree-context-item danger" @click="requestDeleteApproval(item.node)">
                <Trash2 :size="12" /> Delete (approval)
              </button>
            </div>
          </div>
        </template>
      </div>
    </UiScrollArea>
  </div>
</template>

<style scoped>
.code-tree-row {
  position: relative;
}
.code-tree-item {
  display: flex;
  align-items: center;
  gap: 4px;
  width: 100%;
  padding: 3px 8px;
  font-size: 12px;
  color: var(--text-primary);
  background: transparent;
  border: 0;
  cursor: pointer;
  text-align: left;
}
.code-tree-item:hover {
  background: var(--bg-hover);
}
.code-tree-item.active {
  background: var(--bg-selected);
}
.code-tree-chevron {
  flex-shrink: 0;
  color: var(--text-muted);
}
.code-tree-chevron-spacer {
  width: 12px;
  flex-shrink: 0;
}
.code-tree-icon {
  flex-shrink: 0;
  color: var(--text-secondary);
}
.code-tree-label {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.code-tree-size {
  flex-shrink: 0;
  font-size: 10px;
  color: var(--text-muted);
}
.code-tree-menu-trigger {
  flex-shrink: 0;
  display: none;
  padding: 0 2px;
  color: var(--text-muted);
  cursor: pointer;
}
.code-tree-item:hover .code-tree-menu-trigger {
  display: inline-flex;
}
.code-tree-context-menu {
  position: absolute;
  right: 4px;
  top: 100%;
  z-index: 50;
  min-width: 160px;
  border: 1px solid var(--border-default);
  background: var(--bg-overlay);
  box-shadow: var(--shadow-panel);
  border-radius: 6px;
  padding: 4px;
}
.code-tree-context-item {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 5px 8px;
  font-size: 12px;
  color: var(--text-primary);
  background: transparent;
  border: 0;
  cursor: pointer;
  border-radius: 4px;
  text-align: left;
}
.code-tree-context-item:hover {
  background: var(--bg-hover);
}
.code-tree-context-item.danger {
  color: var(--text-reject);
}
</style>
