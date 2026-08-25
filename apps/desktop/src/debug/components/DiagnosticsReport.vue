<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { ClipboardList, CheckCircle2 } from '@lucide/vue'
import type { DiagnosticsReport, FailureCluster, RecentFailure } from '../types/metrics'

const { t } = useI18n()

const props = defineProps<{
  report: DiagnosticsReport | null
}>()

function formatTime(iso: string): string {
  try { return new Date(iso).toLocaleTimeString() } catch { return iso }
}
</script>

<template>
  <div class="diagnostics-report">
    <h3 class="report-title">{{ t('debugStudio.diagnosticsTitle') }}</h3>

    <div v-if="!report" class="report-empty">
      <ClipboardList :size="24" class="empty-icon" />
      <span>{{ t('debugStudio.noDiagnostics') }}</span>
    </div>

    <template v-else>
      <!-- Meta info -->
      <div class="report-meta">
        <div class="meta-item">
          <span class="meta-label">{{ t('debugStudio.generated') }}</span>
          <span class="meta-value">{{ formatTime(report.generated_at) }}</span>
        </div>
        <div class="meta-item">
          <span class="meta-label">{{ t('debugStudio.traceFile') }}</span>
          <code class="meta-value code">{{ report.trace_file_path }}</code>
        </div>
      </div>

      <!-- Common Failures -->
      <div class="report-section">
        <h4 class="section-title">{{ t('debugStudio.commonFailures') }}</h4>
        <div v-if="report.common_failures.length === 0" class="empty-note">
          <CheckCircle2 :size="14" class="empty-icon-sm" /> {{ t('debugStudio.noFailures') }}
        </div>
        <div v-for="(failure, i) in report.common_failures" :key="i" class="failure-item">
          <div class="failure-header">
            <span class="failure-name">{{ failure.name }}</span>
            <span class="failure-count">{{ failure.count }}×</span>
          </div>
          <div class="failure-cause">{{ failure.cause }}</div>
          <div class="failure-time">{{ t('debugStudio.lastSeen') }}: {{ formatTime(failure.last_seen_at) }}</div>
        </div>
      </div>

      <!-- Latest Failures -->
      <div class="report-section">
        <h4 class="section-title">{{ t('debugStudio.latestFailures') }}</h4>
        <div v-if="report.latest_failures.length === 0" class="empty-note">
          <CheckCircle2 :size="14" class="empty-icon-sm" /> {{ t('debugStudio.noRecentFailures') }}
        </div>
        <div v-for="(failure, i) in report.latest_failures.slice(0, 10)" :key="i" class="failure-item compact">
          <span class="failure-name">{{ failure.name }}</span>
          <span class="failure-cause">{{ failure.cause }}</span>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.diagnostics-report {
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.report-title { font-size: 15px; font-weight: 600; margin: 0; color: var(--text-primary); }

.report-empty {
  color: var(--text-muted, #6e7681);
  text-align: center;
  padding: 40px;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  background: var(--surface-section, #11151c);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 12px;
  box-shadow: var(--shadow-card-subtle);
}
.empty-icon { color: var(--text-muted, #6e7681); }

/* ---- Meta ---- */
.report-meta {
  display: flex;
  gap: 24px;
  padding: 12px 16px;
  background: var(--surface-section, #11151c);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 12px;
  box-shadow: var(--shadow-card-subtle);
  transition: box-shadow .2s ease;
}
.report-meta:hover { box-shadow: var(--shadow-card-hover); }
.meta-item {
  display: flex;
  align-items: center;
  gap: 8px;
}
.meta-label { font-size: 12px; color: var(--text-muted, #6e7681); }
.meta-value { font-size: 12px; color: var(--text-primary, #c9d1d9); }
.meta-value.code {
  font-family: 'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace;
  color: var(--text-muted, #6e7681);
}

/* ---- Sections ---- */
.report-section {
  background: var(--surface-section, #11151c);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 12px;
  padding: 14px;
  box-shadow: var(--shadow-card-subtle);
  transition: box-shadow .2s ease;
}
.report-section:hover { box-shadow: var(--shadow-card-hover); }
.section-title {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted, #6e7681);
  text-transform: uppercase;
  letter-spacing: 0.5px;
  margin: 0 0 12px;
}
.empty-note {
  color: var(--text-muted, #6e7681);
  font-size: 13px;
  padding: 12px 0;
  display: flex;
  align-items: center;
  gap: 6px;
}
.empty-icon-sm { color: var(--accent-success, #238636); flex-shrink: 0; }

/* ---- Failure Items ---- */
.failure-item {
  padding: 12px 16px;
  background: var(--surface-raised, #1a1f29);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 10px;
  margin-bottom: 8px;
  border-left: 3px solid var(--accent-danger, #da3633);
  box-shadow: var(--shadow-card-subtle);
  transition: box-shadow 0.2s ease, transform 0.2s ease, border-color 0.2s ease;
  cursor: grab;
}
.failure-item:hover {
  border-color: var(--border-card-active, rgba(0,0,0,.12));
  border-left-color: var(--accent-danger, #f85149);
  box-shadow: var(--shadow-card-hover);
  transform: translateY(-1px);
}
.failure-item:active { cursor: grabbing; transform: translateY(-1px) scale(1.01); }
.failure-item.compact {
  padding: 8px 16px;
  display: flex;
  align-items: center;
  gap: 12px;
}

.failure-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 4px;
}
.failure-name { font-size: 13px; font-weight: 500; color: var(--accent-danger, #f85149); }
.failure-count { font-size: 12px; color: var(--text-muted, #6e7681); font-variant-numeric: tabular-nums; }
.failure-cause { font-size: 12px; color: var(--text-secondary, #7d8590); margin-top: 2px; }
.failure-time { font-size: 11px; color: var(--text-muted, #6e7681); margin-top: 4px; }
</style>
