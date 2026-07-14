<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  ArrowDownWideNarrow,
  ArrowUpWideNarrow,
  CheckCircle2,
  Clock,
  FileCode2,
  FileText,
  Folder,
  GitBranch,
  ListFilter,
  Search,
  Shield,
  Terminal,
  Wrench,
  XCircle
} from '@lucide/vue'
import type { ToolExecutionTimelineItemDto } from '@/api'
import ToolInvocationCard from './ToolInvocationCard.vue'

const props = defineProps<{
  toolExecutions: ToolExecutionTimelineItemDto[]
}>()

const emit = defineEmits<{
  'rerun': [toolExecution: ToolExecutionTimelineItemDto]
  'view-details': [toolExecution: ToolExecutionTimelineItemDto]
}>()

type StatusFilter = 'all' | 'success' | 'failed' | 'approval' | 'running'
type SortOrder = 'asc' | 'desc'

const statusFilter = ref<StatusFilter>('all')
const sortOrder = ref<SortOrder>('desc')
const expandedIds = ref<Set<string>>(new Set())

const filterOptions: Array<{ key: StatusFilter; label: string; icon: typeof ListFilter }> = [
  { key: 'all', label: 'All', icon: ListFilter },
  { key: 'success', label: 'Success', icon: CheckCircle2 },
  { key: 'failed', label: 'Failed', icon: XCircle },
  { key: 'approval', label: 'Approval', icon: Shield },
  { key: 'running', label: 'Running', icon: Clock }
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

function statusClass(status: string) {
  if (status === 'completed') return 'status-completed'
  if (status === 'failed') return 'status-failed'
  if (status === 'running') return 'status-running'
  if (status === 'waiting_approval') return 'status-approval'
  if (status === 'blocked') return 'status-blocked'
  return 'status-pending'
}

function statusDotClass(status: string) {
  if (status === 'completed') return 'dot-completed'
  if (status === 'failed') return 'dot-failed'
  if (status === 'running') return 'dot-running'
  if (status === 'waiting_approval') return 'dot-approval'
  if (status === 'blocked') return 'dot-blocked'
  return 'dot-pending'
}

function matchesFilter(execution: ToolExecutionTimelineItemDto, filter: StatusFilter): boolean {
  if (filter === 'all') return true
  if (filter === 'success') return execution.status === 'completed'
  if (filter === 'failed') return execution.status === 'failed'
  if (filter === 'approval') return execution.status === 'waiting_approval' || execution.requires_approval
  if (filter === 'running') return execution.status === 'running' || execution.status === 'pending'
  return true
}

const filteredExecutions = computed(() => {
  const items = props.toolExecutions.filter((exec) => matchesFilter(exec, statusFilter.value))
  const sorted = [...items].sort((a, b) => {
    const timeA = new Date(a.requested_at).getTime() || 0
    const timeB = new Date(b.requested_at).getTime() || 0
    return sortOrder.value === 'desc' ? timeB - timeA : timeA - timeB
  })
  return sorted
})

const filterCounts = computed(() => {
  const counts: Record<StatusFilter, number> = {
    all: props.toolExecutions.length,
    success: 0,
    failed: 0,
    approval: 0,
    running: 0
  }
  for (const exec of props.toolExecutions) {
    if (exec.status === 'completed') counts.success++
    if (exec.status === 'failed') counts.failed++
    if (exec.status === 'waiting_approval' || exec.requires_approval) counts.approval++
    if (exec.status === 'running' || exec.status === 'pending') counts.running++
  }
  return counts
})

function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '—'
  if (ms < 1000) return `${Math.round(ms)} ms`
  return `${(ms / 1000).toFixed(1)} s`
}

function formatTime(ts: string): string {
  if (!ts) return ''
  try {
    return new Date(ts).toLocaleTimeString([], {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit'
    })
  } catch {
    return ts
  }
}

function toggleExpanded(id: string) {
  if (expandedIds.value.has(id)) {
    expandedIds.value.delete(id)
  } else {
    expandedIds.value.add(id)
  }
}

function isExpanded(id: string): boolean {
  return expandedIds.value.has(id)
}

function setFilter(filter: StatusFilter) {
  statusFilter.value = filter
}

function toggleSort() {
  sortOrder.value = sortOrder.value === 'desc' ? 'asc' : 'desc'
}

function onRerun(exec: ToolExecutionTimelineItemDto) {
  emit('rerun', exec)
}

function onViewDetails(exec: ToolExecutionTimelineItemDto) {
  emit('view-details', exec)
}
</script>

<template>
  <div class="tool-timeline">
    <div class="tool-timeline-toolbar">
      <div class="tool-timeline-filters">
        <button
          v-for="opt in filterOptions"
          :key="opt.key"
          class="tool-timeline-filter"
          :class="{ active: statusFilter === opt.key }"
          @click="setFilter(opt.key)"
        >
          <component :is="opt.icon" :size="12" />
          <span>{{ opt.label }}</span>
          <span class="tool-timeline-filter-count">{{ filterCounts[opt.key] }}</span>
        </button>
      </div>
      <button
        class="tool-timeline-sort"
        :title="sortOrder === 'desc' ? 'Newest first' : 'Oldest first'"
        @click="toggleSort"
      >
        <component :is="sortOrder === 'desc' ? ArrowDownWideNarrow : ArrowUpWideNarrow" :size="14" />
        <span>{{ sortOrder === 'desc' ? 'Newest' : 'Oldest' }}</span>
      </button>
    </div>

    <div v-if="filteredExecutions.length === 0" class="tool-timeline-empty">
      <Wrench :size="20" />
      <span>No tool executions match the current filter.</span>
    </div>

    <div v-else class="tool-timeline-list">
      <div
        v-for="execution in filteredExecutions"
        :key="execution.id"
        class="tool-timeline-node"
        :class="{ expanded: isExpanded(execution.id) }"
      >
        <div class="tool-timeline-rail">
          <div class="tool-timeline-dot" :class="statusDotClass(execution.status)" />
          <div class="tool-timeline-line" />
        </div>

        <div class="tool-timeline-content" @click="toggleExpanded(execution.id)">
          <div class="tool-timeline-summary">
            <div class="tool-timeline-icon">
              <component :is="toolIcon(execution.tool_id)" :size="13" />
            </div>
            <div class="tool-timeline-info">
              <div class="tool-timeline-title-row">
                <strong>{{ execution.tool_display_name }}</strong>
                <span class="tool-timeline-source" :class="sourceClass(execution.source)">
                  {{ execution.source }}
                </span>
                <span class="tool-timeline-status" :class="statusClass(execution.status)">
                  {{ execution.status }}
                </span>
                <span v-if="execution.requires_approval" class="tool-timeline-approval-flag" title="Approval required">
                  <Shield :size="11" />
                </span>
              </div>
              <div class="tool-timeline-meta">
                <span class="tool-timeline-time">
                  <Clock :size="10" />
                  {{ formatTime(execution.requested_at) }}
                </span>
                <span class="tool-timeline-duration">{{ formatDuration(execution.duration_ms) }}</span>
                <span class="tool-timeline-risk">{{ execution.risk }}</span>
                <span class="tool-timeline-layer">{{ execution.provider_layer }}</span>
                <span v-if="execution.approval_id" class="tool-timeline-approval-id">
                  approval {{ execution.approval_id }}
                </span>
              </div>
              <p v-if="execution.summary" class="tool-timeline-summary-text">{{ execution.summary }}</p>
              <div v-if="execution.evidence.length > 0" class="tool-timeline-evidence">
                <span
                  v-for="(item, idx) in execution.evidence.slice(0, 3)"
                  :key="idx"
                  class="tool-timeline-evidence-tag"
                >
                  {{ item.length > 50 ? item.slice(0, 50) + '…' : item }}
                </span>
                <span v-if="execution.evidence.length > 3" class="tool-timeline-evidence-more">
                  +{{ execution.evidence.length - 3 }}
                </span>
              </div>
            </div>
          </div>
        </div>

        <div v-if="isExpanded(execution.id)" class="tool-timeline-detail">
          <ToolInvocationCard
            :tool-execution="execution"
            :default-expanded="true"
            @rerun="onRerun"
            @view-details="onViewDetails"
          />
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.tool-timeline {
  display: grid;
  gap: 10px;
}

.tool-timeline-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.tool-timeline-filters {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.tool-timeline-filter {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 8px;
  font-size: 11px;
  color: var(--text-secondary);
  background: var(--bg-secondary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.15s;
}

.tool-timeline-filter:hover {
  color: var(--text-primary);
  border-color: var(--border-default);
}

.tool-timeline-filter.active {
  color: var(--accent-primary);
  border-color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.08);
}

.tool-timeline-filter-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 16px;
  height: 14px;
  padding: 0 4px;
  font-size: 10px;
  font-weight: 700;
  color: var(--text-muted);
  background: var(--bg-tertiary);
  border-radius: 999px;
}

.tool-timeline-filter.active .tool-timeline-filter-count {
  color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.15);
}

.tool-timeline-sort {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 4px 8px;
  font-size: 11px;
  color: var(--text-secondary);
  background: var(--bg-secondary);
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.15s;
}

.tool-timeline-sort:hover {
  color: var(--text-primary);
  border-color: var(--border-default);
}

.tool-timeline-empty {
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

.tool-timeline-list {
  display: grid;
  gap: 0;
}

.tool-timeline-node {
  display: grid;
  grid-template-columns: 20px 1fr;
  gap: 8px;
  padding-bottom: 4px;
}

.tool-timeline-rail {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: center;
  padding-top: 14px;
}

.tool-timeline-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  border: 2px solid var(--bg-primary);
  flex-shrink: 0;
  z-index: 1;
}

.tool-timeline-dot.dot-completed {
  background: var(--accent-success);
}

.tool-timeline-dot.dot-failed {
  background: var(--accent-danger);
}

.tool-timeline-dot.dot-running {
  background: var(--accent-primary);
  animation: dot-pulse 1.5s ease-in-out infinite;
}

.tool-timeline-dot.dot-approval {
  background: var(--accent-warning);
}

.tool-timeline-dot.dot-blocked,
.tool-timeline-dot.dot-pending {
  background: var(--text-muted);
}

@keyframes dot-pulse {
  0%, 100% {
    box-shadow: 0 0 0 0 rgba(88, 166, 255, 0.5);
  }
  50% {
    box-shadow: 0 0 0 5px rgba(88, 166, 255, 0);
  }
}

.tool-timeline-line {
  flex: 1;
  width: 2px;
  margin-top: 2px;
  background: var(--border-muted);
  min-height: 12px;
}

.tool-timeline-node:last-child .tool-timeline-line {
  display: none;
}

.tool-timeline-content {
  cursor: pointer;
  border-radius: 8px;
  transition: background 0.15s;
  padding: 2px 0;
}

.tool-timeline-content:hover {
  background: var(--bg-hover);
}

.tool-timeline-summary {
  display: flex;
  gap: 8px;
  padding: 8px 10px;
}

.tool-timeline-icon {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  flex-shrink: 0;
  border-radius: 5px;
  background: var(--bg-tertiary);
  color: var(--accent-primary);
}

.tool-timeline-info {
  flex: 1;
  min-width: 0;
  display: grid;
  gap: 4px;
}

.tool-timeline-title-row {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.tool-timeline-title-row strong {
  font-size: 12px;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-timeline-source {
  display: inline-flex;
  padding: 1px 6px;
  border-radius: 4px;
  font-size: 9px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.tool-timeline-source.source-core {
  color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.12);
}

.tool-timeline-source.source-code {
  color: var(--accent-success);
  background: rgba(63, 185, 80, 0.12);
}

.tool-timeline-source.source-codex {
  color: #bc8cff;
  background: rgba(188, 140, 255, 0.12);
}

.tool-timeline-source.source-ext {
  color: var(--accent-warning);
  background: rgba(210, 153, 34, 0.12);
}

.tool-timeline-source.source-default {
  color: var(--text-secondary);
  background: var(--bg-tertiary);
}

.tool-timeline-status {
  display: inline-flex;
  padding: 1px 6px;
  border-radius: 999px;
  font-size: 9px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.tool-timeline-status.status-completed {
  color: var(--accent-success);
  background: var(--bg-status-ok);
}

.tool-timeline-status.status-failed {
  color: var(--accent-danger);
  background: var(--bg-status-danger);
}

.tool-timeline-status.status-running {
  color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.15);
  animation: status-pulse 1.5s ease-in-out infinite;
}

.tool-timeline-status.status-approval {
  color: var(--accent-warning);
  background: var(--bg-status-warn);
}

.tool-timeline-status.status-blocked,
.tool-timeline-status.status-pending {
  color: var(--text-muted);
  background: var(--bg-status-neutral);
}

@keyframes status-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.65; }
}

.tool-timeline-approval-flag {
  display: inline-flex;
  color: var(--accent-warning);
}

.tool-timeline-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 10px;
  color: var(--text-muted);
}

.tool-timeline-meta span {
  display: inline-flex;
  align-items: center;
  gap: 3px;
}

.tool-timeline-time {
  color: var(--text-secondary);
}

.tool-timeline-duration {
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
  color: var(--text-secondary);
}

.tool-timeline-risk,
.tool-timeline-layer,
.tool-timeline-approval-id {
  padding: 1px 5px;
  background: var(--bg-tertiary);
  border-radius: 999px;
}

.tool-timeline-summary-text {
  margin: 0;
  font-size: 11px;
  color: var(--text-secondary);
  line-height: 1.4;
  overflow: hidden;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
}

.tool-timeline-evidence {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.tool-timeline-evidence-tag {
  display: inline-flex;
  padding: 1px 6px;
  font-size: 9px;
  color: var(--text-muted);
  background: var(--bg-tertiary);
  border-radius: 999px;
  max-width: 180px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-timeline-evidence-more {
  display: inline-flex;
  padding: 1px 6px;
  font-size: 9px;
  color: var(--text-secondary);
  background: var(--bg-tertiary);
  border-radius: 999px;
  font-weight: 600;
}

.tool-timeline-detail {
  grid-column: 2;
  padding: 4px 8px 8px;
}
</style>
