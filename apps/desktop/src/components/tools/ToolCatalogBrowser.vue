<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  ChevronDown,
  ChevronRight,
  FileCode2,
  FileText,
  Folder,
  GitBranch,
  Package,
  Search,
  Shield,
  ShieldAlert,
  Terminal,
  Wrench
} from '@lucide/vue'
import { api, type ToolDescriptorDto, type ToolSearchResultDto } from '@/api'

const props = defineProps<{
  tools?: ToolDescriptorDto[]
}>()

const emit = defineEmits<{
  'execute': [tool: ToolDescriptorDto]
}>()

const localTools = ref<ToolDescriptorDto[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const searchQuery = ref('')
const sourceFilter = ref<string>('all')
const riskFilter = ref<string>('all')
const approvalFilter = ref<'all' | 'required' | 'optional'>('all')
const expandedToolIds = ref<Set<string>>(new Set())
const searchResults = ref<ToolSearchResultDto[] | null>(null)
const searchLoading = ref(false)

let searchDebounce: ReturnType<typeof setTimeout> | null = null

const tools = computed(() => props.tools ?? localTools.value)

const sourceOptions = computed(() => {
  const sources = new Set<string>()
  for (const tool of tools.value) {
    sources.add(tool.source)
  }
  return [
    { key: 'all', label: 'All sources' },
    ...[...sources].sort().map((source) => ({ key: source, label: source }))
  ]
})

const riskOptions = [
  { key: 'all', label: 'All risk levels' },
  { key: 'read-only', label: 'Read-only' },
  { key: 'workspace-write', label: 'Workspace write' },
  { key: 'shell', label: 'Shell' },
  { key: 'git-write', label: 'Git write' },
  { key: 'external-url', label: 'External URL' }
]

function toolIcon(toolId: string) {
  if (toolId === 'read_file') return FileText
  if (toolId === 'list_directory') return Folder
  if (toolId === 'glob_search' || toolId === 'grep_content') return Search
  if (toolId === 'apply_patch' || toolId === 'code_editor') return FileCode2
  if (toolId === 'git_worktree_manager' || toolId.startsWith('git_')) return GitBranch
  if (toolId === 'sandbox_exec') return Terminal
  return Wrench
}

function sourceClass(source: string) {
  if (source === 'core') return 'source-core'
  if (source === 'code') return 'source-code'
  if (source === 'codex-rust') return 'source-codex'
  if (source === 'extension') return 'source-ext'
  return 'source-default'
}

function riskClass(risk: string) {
  const r = risk.toLowerCase()
  if (r.includes('read')) return 'risk-read'
  if (r.includes('shell')) return 'risk-shell'
  if (r.includes('git')) return 'risk-git'
  if (r.includes('external') || r.includes('url')) return 'risk-url'
  if (r.includes('write')) return 'risk-write'
  return 'risk-default'
}

function riskColor(risk: string) {
  const r = risk.toLowerCase()
  if (r.includes('read')) return 'var(--accent-success)'
  if (r.includes('shell')) return 'var(--accent-warning)'
  if (r.includes('git')) return 'var(--accent-danger)'
  if (r.includes('external') || r.includes('url')) return '#bc8cff'
  if (r.includes('write')) return 'var(--accent-warning)'
  return 'var(--text-muted)'
}

function matchesFilters(tool: ToolDescriptorDto): boolean {
  if (sourceFilter.value !== 'all' && tool.source !== sourceFilter.value) return false
  if (riskFilter.value !== 'all' && !tool.risk.toLowerCase().includes(riskFilter.value.replace('-', ' '))) return false
  if (approvalFilter.value === 'required' && !tool.requires_approval) return false
  if (approvalFilter.value === 'optional' && tool.requires_approval) return false
  return true
}

const groupedTools = computed(() => {
  const groups: Record<string, ToolDescriptorDto[]> = {}
  const source = tools.value
  for (const tool of source) {
    if (!matchesFilters(tool)) continue
    if (searchResults.value) {
      const found = searchResults.value.find((r) => r.tool.id === tool.id)
      if (!found) continue
    }
    const key = tool.source || 'unknown'
    if (!groups[key]) groups[key] = []
    groups[key].push(tool)
  }
  for (const key of Object.keys(groups)) {
    groups[key].sort((a, b) => a.id.localeCompare(b.id))
  }
  return groups
})

const sortedGroupKeys = computed(() => {
  const rank = (source: string) => {
    const ranks: Record<string, number> = { core: 0, code: 1, 'codex-rust': 2 }
    return ranks[source] ?? 10
  }
  return Object.keys(groupedTools.value).sort((a, b) => {
    const r = rank(a) - rank(b)
    return r !== 0 ? r : a.localeCompare(b)
  })
})

const totalCount = computed(() =>
  sortedGroupKeys.value.reduce((sum, key) => sum + groupedTools.value[key].length, 0)
)

function toggleTool(toolId: string) {
  if (expandedToolIds.value.has(toolId)) {
    expandedToolIds.value.delete(toolId)
  } else {
    expandedToolIds.value.add(toolId)
  }
}

function isExpanded(toolId: string): boolean {
  return expandedToolIds.value.has(toolId)
}

async function loadTools() {
  if (props.tools && props.tools.length > 0) return
  loading.value = true
  error.value = null
  try {
    localTools.value = await api.listTools()
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to load tools'
  } finally {
    loading.value = false
  }
}

async function runSearch(query: string) {
  if (!query.trim()) {
    searchResults.value = null
    return
  }
  searchLoading.value = true
  try {
    searchResults.value = await api.searchTools({ query: query.trim(), limit: 50 })
  } catch {
    searchResults.value = null
  } finally {
    searchLoading.value = false
  }
}

watch(searchQuery, (val) => {
  if (searchDebounce) clearTimeout(searchDebounce)
  searchDebounce = setTimeout(() => {
    runSearch(val)
  }, 300)
})

onMounted(() => {
  loadTools()
})

function onExecute(tool: ToolDescriptorDto) {
  emit('execute', tool)
}
</script>

<template>
  <div class="tool-catalog">
    <div class="tool-catalog-controls">
      <div class="tool-catalog-search">
        <Search :size="14" class="tool-catalog-search-icon" />
        <input
          v-model="searchQuery"
          type="text"
          placeholder="Search tools by name, capability, or domain..."
          class="tool-catalog-search-input"
        />
        <span v-if="searchLoading" class="tool-catalog-search-loading">Searching…</span>
      </div>
      <select v-model="sourceFilter" class="tool-catalog-select">
        <option v-for="opt in sourceOptions" :key="opt.key" :value="opt.key">{{ opt.label }}</option>
      </select>
      <select v-model="riskFilter" class="tool-catalog-select">
        <option v-for="opt in riskOptions" :key="opt.key" :value="opt.key">{{ opt.label }}</option>
      </select>
      <select v-model="approvalFilter" class="tool-catalog-select">
        <option value="all">All approval</option>
        <option value="required">Approval required</option>
        <option value="optional">No approval</option>
      </select>
    </div>

    <div v-if="loading" class="tool-catalog-loading">
      <span>Loading tools…</span>
    </div>

    <div v-else-if="error" class="tool-catalog-error">
      <span>{{ error }}</span>
      <button class="tool-catalog-retry" @click="loadTools">Retry</button>
    </div>

    <div v-else-if="totalCount === 0" class="tool-catalog-empty">
      <Package :size="20" />
      <span>No tools match the current filters.</span>
    </div>

    <div v-else class="tool-catalog-groups">
      <section
        v-for="sourceKey in sortedGroupKeys"
        :key="sourceKey"
        class="tool-catalog-group"
      >
        <header class="tool-catalog-group-head">
          <span class="tool-catalog-group-source" :class="sourceClass(sourceKey)">
            {{ sourceKey }}
          </span>
          <span class="tool-catalog-group-count">{{ groupedTools[sourceKey].length }} tools</span>
        </header>

        <div class="tool-catalog-grid">
          <article
            v-for="tool in groupedTools[sourceKey]"
            :key="tool.id"
            class="tool-catalog-card"
            :class="{ expanded: isExpanded(tool.id) }"
          >
            <div class="tool-catalog-card-head" @click="toggleTool(tool.id)">
              <div class="tool-catalog-card-icon">
                <component :is="toolIcon(tool.id)" :size="14" />
              </div>
              <div class="tool-catalog-card-main">
                <strong>{{ tool.display_name }}</strong>
                <small>{{ tool.id }}</small>
              </div>
              <component
                :is="isExpanded(tool.id) ? ChevronDown : ChevronRight"
                :size="14"
                class="tool-catalog-card-chevron"
              />
            </div>

            <div class="tool-catalog-card-tags">
              <span class="tool-catalog-tag" :class="sourceClass(tool.source)">{{ tool.source }}</span>
              <span class="tool-catalog-tag" :class="riskClass(tool.risk)" :style="{ '--risk-color': riskColor(tool.risk) }">
                {{ tool.risk }}
              </span>
              <span v-if="tool.requires_approval" class="tool-catalog-tag tag-approval">
                <Shield :size="10" />
                approval
              </span>
              <span v-if="tool.domain" class="tool-catalog-tag tag-domain">{{ tool.domain }}</span>
            </div>

            <div v-if="isExpanded(tool.id)" class="tool-catalog-card-detail">
              <div class="tool-catalog-detail-row">
                <span class="tool-catalog-detail-label">Domain</span>
                <span class="tool-catalog-detail-value">{{ tool.domain || '—' }}</span>
              </div>
              <div class="tool-catalog-detail-row">
                <span class="tool-catalog-detail-label">Source</span>
                <span class="tool-catalog-detail-value">{{ tool.source }}</span>
              </div>
              <div class="tool-catalog-detail-row">
                <span class="tool-catalog-detail-label">Risk</span>
                <span class="tool-catalog-detail-value" :style="{ color: riskColor(tool.risk) }">{{ tool.risk }}</span>
              </div>
              <div class="tool-catalog-detail-row">
                <span class="tool-catalog-detail-label">Approval</span>
                <span class="tool-catalog-detail-value">
                  <Shield v-if="tool.requires_approval" :size="11" />
                  <ShieldAlert v-else :size="11" />
                  {{ tool.requires_approval ? 'Required' : 'Not required' }}
                </span>
              </div>
              <div class="tool-catalog-detail-row">
                <span class="tool-catalog-detail-label">Endpoint</span>
                <code class="tool-catalog-detail-mono">{{ tool.execute_endpoint }}</code>
              </div>
              <div v-if="tool.capabilities.length > 0" class="tool-catalog-detail-capabilities">
                <span class="tool-catalog-detail-label">Capabilities</span>
                <div class="tool-catalog-cap-list">
                  <span v-for="cap in tool.capabilities" :key="cap" class="tool-catalog-cap">{{ cap }}</span>
                </div>
              </div>
              <button class="tool-catalog-execute" @click="onExecute(tool)">
                <Wrench :size="12" />
                <span>Execute</span>
              </button>
            </div>
          </article>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.tool-catalog {
  display: grid;
  gap: 10px;
}

.tool-catalog-controls {
  display: grid;
  grid-template-columns: minmax(180px, 1fr) auto auto auto;
  gap: 6px;
  align-items: center;
}

.tool-catalog-search {
  position: relative;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 8px;
  height: 30px;
  border: 1px solid var(--border-input);
  border-radius: 6px;
  background: var(--bg-input);
}

.tool-catalog-search:focus-within {
  border-color: var(--border-input-focus);
  box-shadow: var(--shadow-focus);
}

.tool-catalog-search-icon {
  color: var(--text-muted);
  flex-shrink: 0;
}

.tool-catalog-search-input {
  flex: 1;
  height: 100%;
  border: none;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  outline: none;
  padding: 0;
}

.tool-catalog-search-input::placeholder {
  color: var(--text-muted);
}

.tool-catalog-search-loading {
  font-size: 10px;
  color: var(--text-muted);
  flex-shrink: 0;
}

.tool-catalog-select {
  height: 30px;
  padding: 0 8px;
  font-size: 11px;
  border: 1px solid var(--border-input);
  border-radius: 6px;
  background: var(--bg-input);
  color: var(--text-primary);
  cursor: pointer;
}

.tool-catalog-loading,
.tool-catalog-empty,
.tool-catalog-error {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 32px 16px;
  color: var(--text-muted);
  border: 1px dashed var(--border-dashed);
  border-radius: 8px;
  font-size: 12px;
}

.tool-catalog-retry {
  padding: 4px 12px;
  font-size: 11px;
  color: var(--accent-primary);
  background: transparent;
  border: 1px solid var(--accent-primary);
  border-radius: 4px;
  cursor: pointer;
}

.tool-catalog-retry:hover {
  background: rgba(88, 166, 255, 0.08);
}

.tool-catalog-groups {
  display: grid;
  gap: 12px;
}

.tool-catalog-group {
  display: grid;
  gap: 6px;
}

.tool-catalog-group-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 0;
}

.tool-catalog-group-source {
  display: inline-flex;
  padding: 2px 8px;
  border-radius: 4px;
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.tool-catalog-group-source.source-core {
  color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.12);
}

.tool-catalog-group-source.source-code {
  color: var(--accent-success);
  background: rgba(63, 185, 80, 0.12);
}

.tool-catalog-group-source.source-codex {
  color: #bc8cff;
  background: rgba(188, 140, 255, 0.12);
}

.tool-catalog-group-source.source-ext {
  color: var(--accent-warning);
  background: rgba(210, 153, 34, 0.12);
}

.tool-catalog-group-source.source-default {
  color: var(--text-secondary);
  background: var(--bg-tertiary);
}

.tool-catalog-group-count {
  font-size: 11px;
  color: var(--text-muted);
}

.tool-catalog-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 6px;
}

.tool-catalog-card {
  display: grid;
  gap: 6px;
  padding: 8px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--bg-secondary);
  transition: border-color 0.15s, background 0.15s;
}

.tool-catalog-card:hover {
  border-color: var(--border-default);
}

.tool-catalog-card.expanded {
  border-color: var(--border-input-focus);
}

.tool-catalog-card-head {
  display: flex;
  align-items: center;
  gap: 8px;
  cursor: pointer;
  min-width: 0;
}

.tool-catalog-card-icon {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  flex-shrink: 0;
  border-radius: 5px;
  background: var(--bg-tertiary);
  color: var(--accent-primary);
}

.tool-catalog-card-main {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
  flex: 1;
}

.tool-catalog-card-main strong {
  font-size: 12px;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-catalog-card-main small {
  font-size: 10px;
  color: var(--text-muted);
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-catalog-card-chevron {
  color: var(--text-muted);
  flex-shrink: 0;
}

.tool-catalog-card-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.tool-catalog-tag {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 6px;
  font-size: 9px;
  font-weight: 600;
  border-radius: 999px;
  background: var(--bg-tertiary);
  color: var(--text-secondary);
}

.tool-catalog-tag.source-core {
  color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.12);
}

.tool-catalog-tag.source-code {
  color: var(--accent-success);
  background: rgba(63, 185, 80, 0.12);
}

.tool-catalog-tag.source-codex {
  color: #bc8cff;
  background: rgba(188, 140, 255, 0.12);
}

.tool-catalog-tag.source-ext {
  color: var(--accent-warning);
  background: rgba(210, 153, 34, 0.12);
}

.tool-catalog-tag.risk-read {
  color: var(--accent-success);
  background: rgba(63, 185, 80, 0.12);
}

.tool-catalog-tag.risk-write {
  color: var(--accent-warning);
  background: rgba(210, 153, 34, 0.12);
}

.tool-catalog-tag.risk-shell {
  color: var(--accent-warning);
  background: rgba(210, 153, 34, 0.18);
}

.tool-catalog-tag.risk-git {
  color: var(--accent-danger);
  background: rgba(248, 81, 73, 0.12);
}

.tool-catalog-tag.risk-url {
  color: #bc8cff;
  background: rgba(188, 140, 255, 0.12);
}

.tool-catalog-tag.risk-default {
  color: var(--text-muted);
  background: var(--bg-tertiary);
}

.tool-catalog-tag.tag-approval {
  color: var(--accent-warning);
  background: var(--bg-status-warn);
}

.tool-catalog-tag.tag-domain {
  color: var(--text-secondary);
  background: var(--bg-tertiary);
}

.tool-catalog-card-detail {
  display: grid;
  gap: 5px;
  padding: 6px 0 0;
  border-top: 1px solid var(--border-muted);
}

.tool-catalog-detail-row {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 11px;
}

.tool-catalog-detail-label {
  flex: 0 0 70px;
  color: var(--text-muted);
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.tool-catalog-detail-value {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: var(--text-primary);
  font-size: 11px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-catalog-detail-mono {
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
  font-size: 10px;
  color: var(--text-secondary);
  background: var(--bg-tertiary);
  padding: 1px 5px;
  border-radius: 3px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-catalog-detail-capabilities {
  display: grid;
  gap: 4px;
}

.tool-catalog-cap-list {
  display: flex;
  flex-wrap: wrap;
  gap: 3px;
}

.tool-catalog-cap {
  display: inline-flex;
  padding: 1px 5px;
  font-size: 9px;
  color: var(--text-secondary);
  background: var(--bg-tertiary);
  border-radius: 3px;
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
}

.tool-catalog-execute {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 4px;
  padding: 5px 10px;
  margin-top: 4px;
  font-size: 11px;
  color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.08);
  border: 1px solid var(--accent-primary);
  border-radius: 4px;
  cursor: pointer;
  transition: background 0.15s;
}

.tool-catalog-execute:hover {
  background: rgba(88, 166, 255, 0.16);
}
</style>
