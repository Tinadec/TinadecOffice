<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { Loader2 } from '@lucide/vue'
import type { DiagnosticsReport } from '../types/metrics'

const { t } = useI18n()

const props = defineProps<{
  diagnostics: DiagnosticsReport | null
}>()

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms.toFixed(0)}ms`
  return `${(ms / 1000).toFixed(1)}s`
}
</script>

<template>
  <div class="metrics-dashboard">
    <h3 class="dashboard-title">{{ t('debugStudio.metricsTitle') }}</h3>

    <div v-if="!diagnostics" class="dashboard-empty">
      <Loader2 :size="24" class="empty-icon spinning" />
      <span>{{ t('debugStudio.loadingDiagnostics') }}</span>
    </div>

    <template v-else>
      <!-- Summary cards -->
      <div class="metric-cards">
        <div class="metric-card">
          <div class="metric-value">{{ diagnostics.record_count }}</div>
          <div class="metric-label">{{ t('debugStudio.traceRecords') }}</div>
        </div>
        <div class="metric-card error">
          <div class="metric-value">{{ diagnostics.failure_count }}</div>
          <div class="metric-label">{{ t('debugStudio.failures') }}</div>
        </div>
        <div class="metric-card warning">
          <div class="metric-value">{{ diagnostics.slow_span_count }}</div>
          <div class="metric-label">{{ t('debugStudio.slowSpans') }}</div>
        </div>
        <div class="metric-card">
          <div class="metric-value">{{ diagnostics.top_spans_by_count.length }}</div>
          <div class="metric-label">{{ t('debugStudio.spanTypes') }}</div>
        </div>
      </div>

      <!-- Top spans by count -->
      <div class="metric-section">
        <h4 class="section-title">{{ t('debugStudio.topSpansByCount') }}</h4>
        <div class="span-table">
          <div class="span-table-header">
            <span>{{ t('debugStudio.name') }}</span>
            <span>{{ t('debugStudio.count') }}</span>
            <span>{{ t('debugStudio.failuresCol') }}</span>
            <span>{{ t('debugStudio.avg') }}</span>
            <span>{{ t('debugStudio.max') }}</span>
          </div>
          <div v-for="span in diagnostics.top_spans_by_count.slice(0, 10)" :key="span.name" class="span-table-row">
            <span class="span-name">{{ span.name }}</span>
            <span class="span-num">{{ span.count }}</span>
            <span class="span-num" :class="{ 'text-error': span.failure_count > 0 }">{{ span.failure_count }}</span>
            <span class="span-num">{{ formatDuration(span.average_duration_ms) }}</span>
            <span class="span-num">{{ formatDuration(span.max_duration_ms) }}</span>
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.metrics-dashboard {
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.dashboard-title { font-size: 15px; font-weight: 600; margin: 0; color: var(--text-primary); }

.dashboard-empty {
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
.empty-icon.spinning {
  animation: spin 1s linear infinite;
}
@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

/* ---- Metric Cards ---- */
.metric-cards {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
}
.metric-card {
  background: var(--surface-raised, #1a1f29);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 12px;
  padding: 18px 14px;
  text-align: center;
  box-shadow: var(--shadow-card-subtle);
  transition: box-shadow 0.2s ease, transform 0.2s ease, border-color 0.2s ease;
  cursor: default;
}
.metric-card:hover {
  box-shadow: var(--shadow-card-hover);
  transform: translateY(-1px);
  border-color: var(--border-card-active, rgba(0,0,0,.12));
}
.metric-card.error { border-color: color-mix(in srgb, var(--accent-danger, #da3633) 45%, transparent); }
.metric-card.error:hover { border-color: var(--accent-danger, #da3633); }
.metric-card.warning { border-color: color-mix(in srgb, var(--accent-warning, #d29922) 45%, transparent); }
.metric-card.warning:hover { border-color: var(--accent-warning, #d29922); }

.metric-value {
  font-size: 26px;
  font-weight: 700;
  color: var(--text-primary, #c9d1d9);
  font-variant-numeric: tabular-nums;
}
.metric-card.error .metric-value { color: var(--accent-danger, #f85149); }
.metric-card.warning .metric-value { color: var(--accent-warning, #d29922); }
.metric-label {
  font-size: 12px;
  color: var(--text-muted, #6e7681);
  margin-top: 6px;
}

/* ---- Span Table ---- */
.metric-section {
  background: var(--surface-section, #11151c);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 12px;
  padding: 14px;
  box-shadow: var(--shadow-card-subtle);
  transition: box-shadow .2s ease;
}
.metric-section:hover { box-shadow: var(--shadow-card-hover); }
.section-title {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted, #6e7681);
  text-transform: uppercase;
  letter-spacing: 0.5px;
  margin: 0 0 10px;
}

.span-table { font-size: 12px; }
.span-table-header {
  display: grid;
  grid-template-columns: 2fr 1fr 1fr 1fr 1fr;
  gap: 8px;
  padding: 6px 10px;
  color: var(--text-muted, #6e7681);
  border-bottom: 1px solid var(--border-default, #1a1f29);
  font-weight: 600;
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.3px;
}
.span-table-row {
  display: grid;
  grid-template-columns: 2fr 1fr 1fr 1fr 1fr;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid transparent;
  margin: 2px 0;
  transition: background 0.12s, box-shadow 0.18s, transform 0.18s, border-color 0.18s;
}
.span-table-row:hover {
  background: var(--surface-hover, #1a1f29);
  border-color: var(--border-card, rgba(0,0,0,.08));
  box-shadow: var(--shadow-card-subtle);
  transform: translateY(-1px);
}
.span-name { color: var(--accent-primary, #2ec4b6); font-weight: 500; }
.span-num { font-variant-numeric: tabular-nums; color: var(--text-secondary, #7d8590); }
.text-error { color: var(--accent-danger, #f85149); font-weight: 600; }
</style>
