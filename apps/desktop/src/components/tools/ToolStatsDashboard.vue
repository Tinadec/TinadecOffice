<script setup lang="ts">
import { computed } from 'vue'
import {
  Activity,
  AlertOctagon,
  BarChart3,
  CheckCircle2,
  Clock,
  Shield,
  Wrench,
  XCircle
} from '@lucide/vue'
import type { ToolExecutionTimelineItemDto } from '@/api'

const props = defineProps<{
  toolExecutions: ToolExecutionTimelineItemDto[]
}>()

interface ToolStat {
  toolId: string
  toolName: string
  count: number
  successCount: number
  failedCount: number
  totalDurationMs: number
}

interface SourceStat {
  source: string
  count: number
}

const stats = computed(() => {
  const total = props.toolExecutions.length
  let success = 0
  let failed = 0
  let running = 0
  let pending = 0
  let approval = 0
  let blocked = 0
  let totalDuration = 0
  let durationCount = 0
  let approvalRequired = 0
  let approvalCompleted = 0

  const byTool = new Map<string, ToolStat>()
  const bySource = new Map<string, number>()

  for (const exec of props.toolExecutions) {
    if (exec.status === 'completed') {
      success++
      if (exec.requires_approval) approvalCompleted++
    }
    if (exec.status === 'failed') failed++
    if (exec.status === 'running') running++
    if (exec.status === 'pending') pending++
    if (exec.status === 'waiting_approval') approval++
    if (exec.status === 'blocked') blocked++
    if (exec.requires_approval) approvalRequired++

    if (exec.duration_ms > 0) {
      totalDuration += exec.duration_ms
      durationCount++
    }

    const toolKey = exec.tool_id
    const existing = byTool.get(toolKey)
    if (existing) {
      existing.count++
      if (exec.status === 'completed') existing.successCount++
      if (exec.status === 'failed') existing.failedCount++
      if (exec.duration_ms > 0) existing.totalDurationMs += exec.duration_ms
    } else {
      byTool.set(toolKey, {
        toolId: exec.tool_id,
        toolName: exec.tool_display_name,
        count: 1,
        successCount: exec.status === 'completed' ? 1 : 0,
        failedCount: exec.status === 'failed' ? 1 : 0,
        totalDurationMs: exec.duration_ms > 0 ? exec.duration_ms : 0
      })
    }

    const sourceKey = exec.source || 'unknown'
    bySource.set(sourceKey, (bySource.get(sourceKey) ?? 0) + 1)
  }

  const averageDuration = durationCount > 0 ? totalDuration / durationCount : 0
  const approvalRate = approvalRequired > 0 ? (approvalCompleted / approvalRequired) * 100 : 0

  const topTools = [...byTool.values()]
    .sort((a, b) => b.count - a.count)
    .slice(0, 5)

  const sourceStats: SourceStat[] = [...bySource.entries()]
    .map(([source, count]) => ({ source, count }))
    .sort((a, b) => b.count - a.count)

  const maxToolCount = topTools.length > 0 ? topTools[0].count : 0

  return {
    total,
    success,
    failed,
    running,
    pending,
    approval,
    blocked,
    averageDuration,
    approvalRequired,
    approvalCompleted,
    approvalRate,
    topTools,
    sourceStats,
    maxToolCount
  }
})

function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '—'
  if (ms < 1000) return `${Math.round(ms)} ms`
  return `${(ms / 1000).toFixed(2)} s`
}

function sourceClass(source: string) {
  if (source === 'core') return 'source-core'
  if (source === 'code') return 'source-code'
  if (source === 'codex-rust') return 'source-codex'
  if (source === 'extension') return 'source-ext'
  return 'source-default'
}

function barWidth(count: number, max: number): string {
  if (max <= 0) return '0%'
  return `${Math.max(4, (count / max) * 100)}%`
}

function sourceBarWidth(count: number, total: number): string {
  if (total <= 0) return '0%'
  return `${Math.max(4, (count / total) * 100)}%`
}
</script>

<template>
  <div class="tool-stats">
    <section class="tool-stats-section">
      <header class="tool-stats-section-head">
        <Activity :size="14" />
        <strong>Overview</strong>
      </header>
      <div class="tool-stats-grid">
        <div class="tool-stat-card">
          <div class="tool-stat-icon icon-total">
            <Wrench :size="14" />
          </div>
          <div class="tool-stat-body">
            <strong>{{ stats.total }}</strong>
            <span>Total calls</span>
          </div>
        </div>
        <div class="tool-stat-card">
          <div class="tool-stat-icon icon-success">
            <CheckCircle2 :size="14" />
          </div>
          <div class="tool-stat-body">
            <strong>{{ stats.success }}</strong>
            <span>Completed</span>
          </div>
        </div>
        <div class="tool-stat-card">
          <div class="tool-stat-icon icon-failed">
            <XCircle :size="14" />
          </div>
          <div class="tool-stat-body">
            <strong>{{ stats.failed }}</strong>
            <span>Failed</span>
          </div>
        </div>
        <div class="tool-stat-card">
          <div class="tool-stat-icon icon-approval">
            <Shield :size="14" />
          </div>
          <div class="tool-stat-body">
            <strong>{{ stats.approval }}</strong>
            <span>Awaiting approval</span>
          </div>
        </div>
        <div class="tool-stat-card">
          <div class="tool-stat-icon icon-duration">
            <Clock :size="14" />
          </div>
          <div class="tool-stat-body">
            <strong>{{ formatDuration(stats.averageDuration) }}</strong>
            <span>Avg duration</span>
          </div>
        </div>
        <div class="tool-stat-card">
          <div class="tool-stat-icon icon-rate">
            <BarChart3 :size="14" />
          </div>
          <div class="tool-stat-body">
            <strong>{{ stats.approvalRate.toFixed(0) }}%</strong>
            <span>Approval pass rate</span>
          </div>
        </div>
      </div>
    </section>

    <section v-if="stats.total > 0" class="tool-stats-section">
      <header class="tool-stats-section-head">
        <AlertOctagon :size="14" />
        <strong>Status breakdown</strong>
      </header>
      <div class="tool-stats-bars">
        <div class="tool-stats-bar-row">
          <span class="tool-stats-bar-label">Completed</span>
          <div class="tool-stats-bar-track">
            <div
              class="tool-stats-bar-fill fill-success"
              :style="{ width: sourceBarWidth(stats.success, stats.total) }"
            />
          </div>
          <span class="tool-stats-bar-value">{{ stats.success }}</span>
        </div>
        <div class="tool-stats-bar-row">
          <span class="tool-stats-bar-label">Failed</span>
          <div class="tool-stats-bar-track">
            <div
              class="tool-stats-bar-fill fill-failed"
              :style="{ width: sourceBarWidth(stats.failed, stats.total) }"
            />
          </div>
          <span class="tool-stats-bar-value">{{ stats.failed }}</span>
        </div>
        <div class="tool-stats-bar-row">
          <span class="tool-stats-bar-label">Running</span>
          <div class="tool-stats-bar-track">
            <div
              class="tool-stats-bar-fill fill-running"
              :style="{ width: sourceBarWidth(stats.running, stats.total) }"
            />
          </div>
          <span class="tool-stats-bar-value">{{ stats.running }}</span>
        </div>
        <div class="tool-stats-bar-row">
          <span class="tool-stats-bar-label">Pending</span>
          <div class="tool-stats-bar-track">
            <div
              class="tool-stats-bar-fill fill-pending"
              :style="{ width: sourceBarWidth(stats.pending, stats.total) }"
            />
          </div>
          <span class="tool-stats-bar-value">{{ stats.pending }}</span>
        </div>
        <div class="tool-stats-bar-row">
          <span class="tool-stats-bar-label">Approval</span>
          <div class="tool-stats-bar-track">
            <div
              class="tool-stats-bar-fill fill-approval"
              :style="{ width: sourceBarWidth(stats.approval, stats.total) }"
            />
          </div>
          <span class="tool-stats-bar-value">{{ stats.approval }}</span>
        </div>
        <div class="tool-stats-bar-row">
          <span class="tool-stats-bar-label">Blocked</span>
          <div class="tool-stats-bar-track">
            <div
              class="tool-stats-bar-fill fill-blocked"
              :style="{ width: sourceBarWidth(stats.blocked, stats.total) }"
            />
          </div>
          <span class="tool-stats-bar-value">{{ stats.blocked }}</span>
        </div>
      </div>
    </section>

    <section v-if="stats.topTools.length > 0" class="tool-stats-section">
      <header class="tool-stats-section-head">
        <BarChart3 :size="14" />
        <strong>Top tools</strong>
      </header>
      <div class="tool-stats-top">
        <div v-for="tool in stats.topTools" :key="tool.toolId" class="tool-stats-top-row">
          <span class="tool-stats-top-name" :title="tool.toolId">{{ tool.toolName }}</span>
          <div class="tool-stats-top-bar-track">
            <div
              class="tool-stats-top-bar-fill"
              :style="{ width: barWidth(tool.count, stats.maxToolCount) }"
            />
          </div>
          <span class="tool-stats-top-count">{{ tool.count }}</span>
          <span class="tool-stats-top-duration">{{ formatDuration(tool.totalDurationMs / tool.count) }}</span>
        </div>
      </div>
    </section>

    <section v-if="stats.sourceStats.length > 0" class="tool-stats-section">
      <header class="tool-stats-section-head">
        <Wrench :size="14" />
        <strong>By source</strong>
      </header>
      <div class="tool-stats-sources">
        <div v-for="source in stats.sourceStats" :key="source.source" class="tool-stats-source-row">
          <span class="tool-stats-source-tag" :class="sourceClass(source.source)">
            {{ source.source }}
          </span>
          <div class="tool-stats-source-bar-track">
            <div
              class="tool-stats-source-bar-fill"
              :class="sourceClass(source.source)"
              :style="{ width: sourceBarWidth(source.count, stats.total) }"
            />
          </div>
          <span class="tool-stats-source-count">{{ source.count }}</span>
          <span class="tool-stats-source-pct">
            {{ ((source.count / stats.total) * 100).toFixed(0) }}%
          </span>
        </div>
      </div>
    </section>

    <div v-if="stats.total === 0" class="tool-stats-empty">
      <Activity :size="20" />
      <span>No tool execution data available for this session.</span>
    </div>
  </div>
</template>

<style scoped>
.tool-stats {
  display: grid;
  gap: 12px;
}

.tool-stats-section {
  display: grid;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid var(--border-muted);
  border-radius: 8px;
  background: var(--bg-secondary);
}

.tool-stats-section-head {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
}

.tool-stats-section-head svg {
  color: var(--accent-primary);
}

.tool-stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
  gap: 6px;
}

.tool-stat-card {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 10px;
  border: 1px solid var(--border-muted);
  border-radius: 6px;
  background: var(--bg-primary);
}

.tool-stat-icon {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  flex-shrink: 0;
  border-radius: 6px;
}

.tool-stat-icon.icon-total {
  color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.12);
}

.tool-stat-icon.icon-success {
  color: var(--accent-success);
  background: var(--bg-status-ok);
}

.tool-stat-icon.icon-failed {
  color: var(--accent-danger);
  background: var(--bg-status-danger);
}

.tool-stat-icon.icon-approval {
  color: var(--accent-warning);
  background: var(--bg-status-warn);
}

.tool-stat-icon.icon-duration {
  color: #bc8cff;
  background: rgba(188, 140, 255, 0.12);
}

.tool-stat-icon.icon-rate {
  color: var(--accent-success);
  background: rgba(63, 185, 80, 0.12);
}

.tool-stat-body {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
}

.tool-stat-body strong {
  font-size: 16px;
  color: var(--text-primary);
  line-height: 1.1;
}

.tool-stat-body span {
  font-size: 10px;
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-stats-bars,
.tool-stats-top,
.tool-stats-sources {
  display: grid;
  gap: 5px;
}

.tool-stats-bar-row,
.tool-stats-top-row,
.tool-stats-source-row {
  display: grid;
  align-items: center;
  gap: 8px;
  font-size: 11px;
}

.tool-stats-bar-row {
  grid-template-columns: 70px 1fr 32px;
}

.tool-stats-bar-label {
  color: var(--text-secondary);
}

.tool-stats-bar-track {
  height: 8px;
  background: var(--bg-tertiary);
  border-radius: 4px;
  overflow: hidden;
}

.tool-stats-bar-fill {
  height: 100%;
  border-radius: 4px;
  transition: width 0.3s ease;
}

.tool-stats-bar-fill.fill-success {
  background: var(--accent-success);
}

.tool-stats-bar-fill.fill-failed {
  background: var(--accent-danger);
}

.tool-stats-bar-fill.fill-running {
  background: var(--accent-primary);
}

.tool-stats-bar-fill.fill-pending {
  background: var(--text-muted);
}

.tool-stats-bar-fill.fill-approval {
  background: var(--accent-warning);
}

.tool-stats-bar-fill.fill-blocked {
  background: var(--text-muted);
}

.tool-stats-bar-value {
  text-align: right;
  color: var(--text-primary);
  font-weight: 600;
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
}

.tool-stats-top-row {
  grid-template-columns: minmax(80px, 1.4fr) minmax(60px, 1fr) 32px 60px;
}

.tool-stats-top-name {
  color: var(--text-primary);
  font-size: 11px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tool-stats-top-bar-track {
  height: 8px;
  background: var(--bg-tertiary);
  border-radius: 4px;
  overflow: hidden;
}

.tool-stats-top-bar-fill {
  height: 100%;
  background: var(--accent-primary);
  border-radius: 4px;
  transition: width 0.3s ease;
}

.tool-stats-top-count {
  text-align: right;
  color: var(--text-primary);
  font-weight: 600;
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
}

.tool-stats-top-duration {
  text-align: right;
  color: var(--text-muted);
  font-size: 10px;
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
}

.tool-stats-source-row {
  grid-template-columns: 80px 1fr 32px 36px;
}

.tool-stats-source-tag {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 2px 6px;
  border-radius: 4px;
  font-size: 9px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.tool-stats-source-tag.source-core,
.tool-stats-source-bar-fill.source-core {
  color: var(--accent-primary);
  background: rgba(88, 166, 255, 0.12);
}

.tool-stats-source-bar-fill.source-core {
  background: var(--accent-primary);
}

.tool-stats-source-tag.source-code,
.tool-stats-source-bar-fill.source-code {
  color: var(--accent-success);
  background: rgba(63, 185, 80, 0.12);
}

.tool-stats-source-bar-fill.source-code {
  background: var(--accent-success);
}

.tool-stats-source-tag.source-codex,
.tool-stats-source-bar-fill.source-codex {
  color: #bc8cff;
  background: rgba(188, 140, 255, 0.12);
}

.tool-stats-source-bar-fill.source-codex {
  background: #bc8cff;
}

.tool-stats-source-tag.source-ext,
.tool-stats-source-bar-fill.source-ext {
  color: var(--accent-warning);
  background: rgba(210, 153, 34, 0.12);
}

.tool-stats-source-bar-fill.source-ext {
  background: var(--accent-warning);
}

.tool-stats-source-tag.source-default,
.tool-stats-source-bar-fill.source-default {
  color: var(--text-secondary);
  background: var(--bg-tertiary);
}

.tool-stats-source-bar-fill.source-default {
  background: var(--text-muted);
}

.tool-stats-source-bar-track {
  height: 8px;
  background: var(--bg-tertiary);
  border-radius: 4px;
  overflow: hidden;
}

.tool-stats-source-bar-fill {
  height: 100%;
  border-radius: 4px;
  transition: width 0.3s ease;
}

.tool-stats-source-count {
  text-align: right;
  color: var(--text-primary);
  font-weight: 600;
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
}

.tool-stats-source-pct {
  text-align: right;
  color: var(--text-muted);
  font-size: 10px;
  font-family: 'SF Mono', 'Cascadia Code', 'Fira Code', monospace;
}

.tool-stats-empty {
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
</style>
