<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  Check,
  ChevronDown,
  ChevronRight,
  Clock,
  Copy,
  FileCode2,
  FileText,
  Folder,
  GitBranch,
  MoreHorizontal,
  RotateCw,
  Search,
  Shield,
  Terminal,
  Wrench
} from '@lucide/vue'
import type { ToolExecutionTimelineItemDto } from '@/api'
import ToolResultViewer from './ToolResultViewer.vue'

const props = defineProps<{
  toolExecution: ToolExecutionTimelineItemDto
  defaultExpanded?: boolean
}>()

const emit = defineEmits<{
  'rerun': [toolExecution: ToolExecutionTimelineItemDto]
  'view-details': [toolExecution: ToolExecutionTimelineItemDto]
}>()

const expanded = ref(props.defaultExpanded ?? false)
const menuOpen = ref(false)
const copied = ref(false)

const toolIcon = computed(() => {
  const id = props.toolExecution.tool_id
  if (id === 'read_file') return FileText
  if (id === 'list_directory') return Folder
  if (id === 'glob_search' || id === 'grep_content') return Search
  if (id === 'apply_patch' || id === 'code_editor') return FileCode2
  if (id === 'git_worktree_manager' || id.startsWith('git_')) return GitBranch
  if (id === 'sandbox_exec') return Terminal
  return Wrench
})

const sourceClass = computed(() => {
  const source = props.toolExecution.source
  if (source === 'core') return 'source-core'
  if (source === 'code') return 'source-code'
  if (source === 'codex-rust') return 'source-codex'
  if (source === 'extension') return 'source-ext'
  return 'source-default'
})

const statusClass = computed(() => {
  const status = props.toolExecution.status
  if (status === 'completed') return 'status-completed'
  if (status === 'failed') return 'status-failed'
  if (status === 'running') return 'status-running'
  if (status === 'waiting_approval') return 'status-approval'
  if (status === 'blocked') return 'status-blocked'
  return 'status-pending'
})

const riskClass = computed(() => {
  const risk = props.toolExecution.risk?.toLowerCase() ?? ''
  if (risk.includes('read')) return 'risk-read'
  if (risk.includes('shell')) return 'risk-shell'
  if (risk.includes('git')) return 'risk-git'
  if (risk.includes('external') || risk.includes('url')) return 'risk-url'
  if (risk.includes('write')) return 'risk-write'
  return 'risk-default'
})

const formattedDuration = computed(() => {
  const ms = props.toolExecution.duration_ms
  if (!Number.isFinite(ms) || ms <= 0) return '—'
  if (ms < 1000) return `${Math.round(ms)} ms`
  return `${(ms / 1000).toFixed(1)} s`
})

const formattedTime = computed(() => {
  const ts = props.toolExecution.requested_at
  if (!ts) return ''
  try {
    const date = new Date(ts)
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
  } catch {
    return ts
  }
})

const paramHints = computed(() => {
  const summary = props.toolExecution.summary
  if (!summary) return []
  const parts = summary.split(/[;,]/).map((s) => s.trim()).filter(Boolean).slice(0, 4)
  return parts
})

function toggle() {
  expanded.value = !expanded.value
}

function closeMenu() {
  menuOpen.value = false
}

function toggleMenu(event: MouseEvent) {
  event.stopPropagation()
  menuOpen.value = !menuOpen.value
}

async function copyResult() {
  closeMenu()
  const items: string[] = []
  if (props.toolExecution.summary) items.push(props.toolExecution.summary)
  if (props.toolExecution.checkpoint_summary) items.push(props.toolExecution.checkpoint_summary)
  for (const ev of props.toolExecution.evidence) items.push(ev)
  try {
    await navigator.clipboard.writeText(items.join('\n'))
    copied.value = true
    setTimeout(() => {
      copied.value = false
    }, 1500)
  } catch {
    // Clipboard may be unavailable
  }
}

function rerun() {
  closeMenu()
  emit('rerun', props.toolExecution)
}

function viewDetails() {
  closeMenu()
  emit('view-details', props.toolExecution)
}
</script>

<template>
  <div class="tool-invocation-card" :class="[statusClass, { risky: toolExecution.requires_approval }]">
    <div class="tool-invocation-top">
      <div class="tool-invocation-icon">
        <component :is="toolIcon" :size="14" />
      </div>
      <div class="tool-invocation-main">
        <strong>{{ toolExecution.tool_display_name }}</strong>
        <span class="tool-invocation-id">{{ toolExecution.tool_id }}</span>
      </div>
      <span class="tool-invocation-source" :class="sourceClass">{{ toolExecution.source }}</span>
      <span class="tool-invocation-status" :class="statusClass">{{ toolExecution.status }}</span>
      <span class="tool-invocation-duration">
        <Clock :size="11" />
        {{ formattedDuration }}
      </span>
      <div class="tool-invocation-menu-wrapper">
        <button class="tool-invocation-menu-btn" title="Actions" @click="toggleMenu">
          <MoreHorizontal :size="14" />
        </button>
        <div v-if="menuOpen" class="tool-invocation-menu" @click.stop>
          <button class="tool-invocation-menu-item" @click="rerun">
            <RotateCw :size="12" />
            <span>Re-run</span>
          </button>
          <button class="tool-invocation-menu-item" @click="viewDetails">
            <Search :size="12" />
            <span>View details</span>
          </button>
          <button class="tool-invocation-menu-item" @click="copyResult">
            <Check v-if="copied" :size="12" />
            <Copy v-else :size="12" />
            <span>{{ copied ? 'Copied' : 'Copy result' }}</span>
          </button>
        </div>
        <div v-if="menuOpen" class="tool-invocation-menu-overlay" @click="closeMenu" />
      </div>
    </div>

    <div v-if="paramHints.length > 0" class="tool-invocation-params">
      <span v-for="(hint, idx) in paramHints" :key="idx" class="tool-invocation-param">{{ hint }}</span>
    </div>

    <div v-if="toolExecution.requires_approval" class="tool-invocation-approval">
      <Shield :size="11" />
      <span>Approval required</span>
      <small v-if="toolExecution.approval_id">{{ toolExecution.approval_id }}</small>
    </div>

    <div v-if="toolExecution.evidence.length > 0" class="tool-invocation-evidence">
      <span v-for="(item, idx) in toolExecution.evidence.slice(0, 3)" :key="idx" class="tool-invocation-evidence-tag">
        {{ item.length > 60 ? item.slice(0, 60) + '…' : item }}
      </span>
      <span v-if="toolExecution.evidence.length > 3" class="tool-invocation-evidence-more">
        +{{ toolExecution.evidence.length - 3 }} more
      </span>
    </div>

    <div class="tool-invocation-meta">
      <span class="tool-invocation-risk" :class="riskClass">{{ toolExecution.risk }}</span>
      <span class="tool-invocation-layer">{{ toolExecution.provider_layer }}</span>
      <span class="tool-invocation-time">{{ formattedTime }}</span>
      <span class="tool-invocation-seq">seq {{ toolExecution.requested_seq }}-{{ toolExecution.updated_seq }}</span>
    </div>

    <button class="tool-invocation-expand" @click="toggle">
      <component :is="expanded ? ChevronDown : ChevronRight" :size="12" />
      <span>{{ expanded ? 'Hide result' : 'Show result' }}</span>
    </button>

    <div v-if="expanded" class="tool-invocation-result">
      <ToolResultViewer :tool-execution="toolExecution" :default-expanded="true" />
    </div>
  </div>
</template>

<style scoped>
.tool-invocation-card {
  display: grid;
  gap: 6px;
  padding: 10px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
  transition: border-color 0.15s, background 0.15s;
}

.tool-invocation-card:hover {
  border-color: var(--border-default);
}

.tool-invocation-card.risky {
  border-left: 3px solid var(--accent-warning);
}

.tool-invocation-top {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.tool-invocation-icon {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  flex-shrink: 0;
  border-radius: 6px;
  background: var(--bg-tertiary);
  color: var(--accent-primary);
}

.tool-invocation-main {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
  flex: 1;
}

.tool-invocation-main strong {
  font-size: 12px;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-invocation-id {
  font-size: 10px;
  color: var(--text-muted);
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-invocation-source {
  display: inline-flex;
  align-items: center;
  padding: 2px 6px;
  border-radius: 4px;
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  flex-shrink: 0;
}

.tool-invocation-source.source-core {
  color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.12);
}

.tool-invocation-source.source-code {
  color: var(--accent-success);
  background: rgba(63, 185, 80, 0.12);
}

.tool-invocation-source.source-codex {
  color: #bc8cff;
  background: rgba(188, 140, 255, 0.12);
}

.tool-invocation-source.source-ext {
  color: var(--accent-warning);
  background: rgba(210, 153, 34, 0.12);
}

.tool-invocation-source.source-default {
  color: var(--text-secondary);
  background: var(--bg-tertiary);
}

.tool-invocation-status {
  display: inline-flex;
  align-items: center;
  padding: 2px 8px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  flex-shrink: 0;
}

.tool-invocation-status.status-completed {
  color: var(--accent-success);
  background: var(--bg-status-ok);
}

.tool-invocation-status.status-failed {
  color: var(--accent-danger);
  background: var(--bg-status-danger);
}

.tool-invocation-status.status-running {
  color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.15);
  animation: status-pulse 1.5s ease-in-out infinite;
}

.tool-invocation-status.status-approval {
  color: var(--accent-warning);
  background: var(--bg-status-warn);
}

.tool-invocation-status.status-blocked,
.tool-invocation-status.status-pending {
  color: var(--text-muted);
  background: var(--bg-status-neutral);
}

@keyframes status-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.65; }
}

.tool-invocation-duration {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  font-size: 11px;
  color: var(--text-secondary);
  flex-shrink: 0;
}

.tool-invocation-menu-wrapper {
  position: relative;
  flex-shrink: 0;
}

.tool-invocation-menu-btn {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  color: var(--text-muted);
  background: transparent;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.tool-invocation-menu-btn:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.tool-invocation-menu {
  position: absolute;
  top: 100%;
  right: 0;
  z-index: 30;
  min-width: 140px;
  padding: 4px;
  border: 1px solid var(--border-default);
  border-radius: 6px;
  background: var(--bg-primary);
  box-shadow: var(--shadow-panel);
}

.tool-invocation-menu-overlay {
  position: fixed;
  inset: 0;
  z-index: 20;
}

.tool-invocation-menu-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 8px;
  font-size: 12px;
  color: var(--text-primary);
  background: transparent;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  text-align: left;
  transition: background 0.15s;
}

.tool-invocation-menu-item:hover {
  background: var(--bg-hover);
}

.tool-invocation-params {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.tool-invocation-param {
  display: inline-flex;
  padding: 2px 6px;
  font-size: 10px;
  color: var(--text-secondary);
  background: var(--bg-tertiary);
  border-radius: 4px;
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-invocation-approval {
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 4px 8px;
  border-radius: 4px;
  background: var(--bg-status-warn);
  color: var(--accent-warning);
  font-size: 11px;
  font-weight: 600;
}

.tool-invocation-approval small {
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
  font-size: 10px;
  opacity: 0.8;
}

.tool-invocation-evidence {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.tool-invocation-evidence-tag {
  display: inline-flex;
  padding: 2px 6px;
  font-size: 10px;
  color: var(--text-muted);
  background: var(--bg-tertiary);
  border-radius: 999px;
  max-width: 200px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-invocation-evidence-more {
  display: inline-flex;
  padding: 2px 6px;
  font-size: 10px;
  color: var(--text-secondary);
  background: var(--bg-tertiary);
  border-radius: 999px;
  font-weight: 600;
}

.tool-invocation-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  font-size: 10px;
  color: var(--text-muted);
}

.tool-invocation-meta span {
  display: inline-flex;
  align-items: center;
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--bg-tertiary);
}

.tool-invocation-risk.risk-read {
  color: var(--accent-success);
}

.tool-invocation-risk.risk-write {
  color: var(--accent-warning);
}

.tool-invocation-risk.risk-shell {
  color: var(--accent-warning);
}

.tool-invocation-risk.risk-git {
  color: var(--accent-danger);
}

.tool-invocation-risk.risk-url {
  color: #bc8cff;
}

.tool-invocation-risk.risk-default {
  color: var(--text-muted);
}

.tool-invocation-expand {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 4px 6px;
  font-size: 11px;
  color: var(--text-secondary);
  background: transparent;
  border: 1px dashed var(--border-muted);
  border-radius: 4px;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.tool-invocation-expand:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.tool-invocation-result {
  padding-top: 4px;
}
</style>
