<script setup lang="ts">
import type { SpanNode } from '../types/trace'
import { useI18n } from 'vue-i18n'
import { Search } from '@lucide/vue'

const { t } = useI18n()

const props = defineProps<{
  span: SpanNode | null
}>()

function formatValue(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms.toFixed(0)}ms`
  return `${(ms / 1000).toFixed(2)}s`
}
</script>

<template>
  <div class="inspector-panel">
    <div v-if="!span" class="inspector-empty">
      <Search :size="32" class="empty-icon" />
      <div class="empty-text">{{ t('debugStudio.selectSpan') }}</div>
    </div>
    <div v-else class="inspector-content">
      <!-- Header -->
      <div class="inspector-header">
        <h3 class="inspector-title">{{ span.name }}</h3>
        <div class="inspector-meta">
          <span class="meta-chip" :class="span.status.toLowerCase()">{{ span.status }}</span>
          <span class="meta-duration">{{ formatDuration(span.duration_ms) }}</span>
          <span class="meta-kind">{{ span.kind }}</span>
        </div>
      </div>

      <!-- Attributes -->
      <div class="inspector-section">
        <h4 class="section-title">{{ t('debugStudio.attributes') }}</h4>
        <table class="attrs-table">
          <tr v-for="(value, key) in span.attributes" :key="key">
            <td class="attr-key">{{ key }}</td>
            <td class="attr-value">{{ formatValue(value) }}</td>
          </tr>
        </table>
      </div>

      <!-- Events -->
      <div v-if="span.events?.length" class="inspector-section">
        <h4 class="section-title">{{ t('debugStudio.eventsCount', { count: span.events.length }) }}</h4>
        <div v-for="(event, i) in span.events" :key="i" class="event-item">
          <div class="event-name">{{ event.name }}</div>
          <table v-if="Object.keys(event.attributes).length" class="attrs-table compact">
            <tr v-for="(value, key) in event.attributes" :key="key">
              <td class="attr-key">{{ key }}</td>
              <td class="attr-value">{{ formatValue(value) }}</td>
            </tr>
          </table>
        </div>
      </div>

      <!-- Span IDs -->
      <div class="inspector-section">
        <h4 class="section-title">{{ t('debugStudio.ids') }}</h4>
        <div class="id-row">
          <span class="id-label">{{ t('debugStudio.traceLabel') }}</span>
          <code class="id-value">{{ t('debugStudio.seeTrace') }}</code>
        </div>
        <div class="id-row">
          <span class="id-label">{{ t('debugStudio.spanLabel') }}</span>
          <code class="id-value">{{ span.span_id }}</code>
        </div>
        <div v-if="span.parent_span_id" class="id-row">
          <span class="id-label">{{ t('debugStudio.parentLabel') }}</span>
          <code class="id-value">{{ span.parent_span_id }}</code>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.inspector-panel {
  height: 100%;
  overflow-y: auto;
  background: transparent;
}

/* ---- Empty State ---- */
.inspector-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  height: 100%;
  gap: 10px;
  color: var(--text-muted, #6e7681);
  padding: 24px;
}
.empty-icon { color: var(--text-muted, #6e7681); opacity: 0.6; }
.empty-text { font-size: 13px; }

/* ---- Content ---- */
.inspector-content { padding: 16px; }

.inspector-header {
  margin-bottom: 16px;
  padding: 12px 14px;
  background: var(--surface-raised, #1a1f29);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 10px;
  box-shadow: var(--shadow-card-subtle);
}
.inspector-title {
  font-size: 14px;
  font-weight: 600;
  margin: 0 0 8px;
  line-height: 1.4;
  color: var(--text-primary);
}
.inspector-meta {
  display: flex;
  gap: 8px;
  align-items: center;
}
.meta-chip {
  padding: 2px 10px;
  border-radius: 10px;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.3px;
}
.meta-chip.ok { background: var(--accent-success, #238636); color: #fff; }
.meta-chip.error { background: var(--accent-danger, #da3633); color: #fff; }
.meta-duration { font-size: 13px; color: var(--text-muted, #6e7681); font-variant-numeric: tabular-nums; }
.meta-kind { font-size: 11px; color: var(--text-muted, #6e7681); }

/* ---- Sections ---- */
.inspector-section {
  margin-bottom: 14px;
  background: var(--surface-raised, #1a1f29);
  border: 1px solid var(--border-card, rgba(0,0,0,.08));
  border-radius: 10px;
  padding: 12px;
  box-shadow: var(--shadow-card-subtle);
  transition: box-shadow .18s ease, transform .18s ease;
}
.inspector-section:hover {
  box-shadow: var(--shadow-card-hover);
  transform: translateY(-1px);
}
.section-title {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted, #6e7681);
  text-transform: uppercase;
  letter-spacing: 0.5px;
  margin: 0 0 10px;
}

.attrs-table { width: 100%; border-collapse: collapse; }
.attrs-table td {
  padding: 5px 10px;
  font-size: 12px;
  border-bottom: 1px solid var(--border-muted, #161b22);
  vertical-align: top;
}
.attr-key { color: var(--text-muted, #6e7681); white-space: nowrap; width: 40%; }
.attr-value { color: var(--text-primary, #c9d1d9); word-break: break-all; }
.attrs-table.compact td { padding: 3px 10px; }

.event-item {
  padding: 8px 0;
  border-bottom: 1px solid var(--border-muted, #161b22);
}
.event-item:last-child { border-bottom: none; }
.event-name { font-size: 12px; font-weight: 500; color: var(--accent-primary, #2ec4b6); margin-bottom: 4px; }

.id-row {
  display: flex;
  gap: 10px;
  align-items: center;
  padding: 4px 0;
}
.id-label { font-size: 11px; color: var(--text-muted, #6e7681); width: 48px; flex-shrink: 0; }
.id-value {
  font-size: 11px;
  color: var(--text-muted, #6e7681);
  font-family: 'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace;
  word-break: break-all;
}
</style>
