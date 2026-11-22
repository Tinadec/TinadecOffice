<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronDown, ChevronRight, Pause, Play, Search, Trash2 } from '@lucide/vue'
import type { EventEnvelope } from '../api'

const props = defineProps<{
  events: EventEnvelope[]
}>()

const { t, te } = useI18n()

function label(key: string, fallback: string): string {
  return te(key) ? String(t(key)) : fallback
}

const query = ref('')
const activeCategory = ref<'all' | 'run' | 'tool' | 'worker' | 'approval' | 'plan'>('all')
const paused = ref(false)
const clearedBeforeSeq = ref(0)
const frozenSnapshot = ref<EventEnvelope[]>([])
const expandedSeqs = ref<Record<number, boolean>>({})

const categories = ['all', 'run', 'tool', 'worker', 'approval', 'plan'] as const

const sourceEvents = computed(() => {
  const base = paused.value ? frozenSnapshot.value : props.events
  return base.filter((ev) => ev.seq > clearedBeforeSeq.value)
})

function matchesCategory(type: string, category: (typeof categories)[number]): boolean {
  if (category === 'all') return true
  const lower = type.toLowerCase()
  if (category === 'run') return lower.startsWith('run.') || lower.startsWith('task') || lower.startsWith('model.') || lower.startsWith('interaction.')
  if (category === 'tool') return lower.startsWith('tool.') || lower.startsWith('terminal.')
  if (category === 'worker') return lower.startsWith('worker.') || lower.startsWith('agent.')
  if (category === 'approval') return lower.startsWith('approval.') || lower.startsWith('governance.') || lower.startsWith('supervision.')
  if (category === 'plan') return lower.includes('plan.') || lower.startsWith('context.')
  return true
}

const filteredEvents = computed(() => {
  const q = query.value.trim().toLowerCase()
  return sourceEvents.value.filter((ev) => {
    if (!matchesCategory(ev.type, activeCategory.value)) return false
    if (!q) return true
    if (ev.type.toLowerCase().includes(q) || String(ev.seq).includes(q)) return true
    try {
      return JSON.stringify(ev.payload ?? '').toLowerCase().includes(q)
    } catch {
      return false
    }
  })
})

function togglePause(): void {
  if (!paused.value) {
    frozenSnapshot.value = [...props.events]
    paused.value = true
  } else {
    paused.value = false
    frozenSnapshot.value = []
  }
}

function clearEvents(): void {
  const maxSeq = props.events.reduce((max, ev) => Math.max(max, ev.seq), clearedBeforeSeq.value)
  clearedBeforeSeq.value = maxSeq
  frozenSnapshot.value = []
  expandedSeqs.value = {}
}

function toggleExpand(seq: number): void {
  expandedSeqs.value = {
    ...expandedSeqs.value,
    [seq]: !expandedSeqs.value[seq],
  }
}

function formatPayload(payload: unknown): string {
  try {
    return JSON.stringify(payload ?? {}, null, 2)
  } catch {
    return String(payload ?? '')
  }
}

watch(
  () => props.events.length,
  (len) => {
    if (len === 0) clearedBeforeSeq.value = 0
  },
)
</script>

<template>
  <section class="panel events-inspector" data-testid="events-inspector">
    <div class="events-toolbar">
      <div class="events-search">
        <Search :size="13" aria-hidden="true" />
        <input
          v-model="query"
          type="text"
          class="events-search-input"
          :placeholder="label('context.eventsFilterPlaceholder', 'Filter event type or payload...')"
          :aria-label="label('context.eventsFilterPlaceholder', 'Filter event type or payload...')"
          data-testid="events-filter-input"
        />
      </div>
      <div class="events-actions">
        <button
          type="button"
          class="events-action-btn"
          :class="{ active: paused }"
          data-testid="events-pause-btn"
          @click="togglePause"
        >
          <Play v-if="paused" :size="12" aria-hidden="true" />
          <Pause v-else :size="12" aria-hidden="true" />
          <span>{{ paused ? label('context.eventsResume', 'Resume') : label('context.eventsPause', 'Pause') }}</span>
        </button>
        <button
          type="button"
          class="events-action-btn"
          data-testid="events-clear-btn"
          @click="clearEvents"
        >
          <Trash2 :size="12" aria-hidden="true" />
          <span>{{ label('context.eventsClear', 'Clear') }}</span>
        </button>
      </div>
    </div>

    <div class="events-chips" role="group">
      <button
        v-for="cat in categories"
        :key="cat"
        type="button"
        class="events-chip"
        :class="{ active: activeCategory === cat }"
        @click="activeCategory = cat"
      >
        {{ cat }}
      </button>
      <span v-if="paused" class="events-paused-pill">{{ label('context.eventsPaused', 'Paused') }}</span>
    </div>

    <p v-if="filteredEvents.length === 0" class="quiet">{{ t('eventsPanel.empty') }}</p>
    <ol v-else class="event-list events-inspector-list">
      <li v-for="event in filteredEvents" :key="event.seq" class="events-row">
        <button type="button" class="events-row-head" @click="toggleExpand(event.seq)">
          <component :is="expandedSeqs[event.seq] ? ChevronDown : ChevronRight" :size="13" aria-hidden="true" />
          <span class="events-row-type">{{ event.type }}</span>
          <small class="events-row-seq">#{{ event.seq }}</small>
        </button>
        <pre v-if="expandedSeqs[event.seq]" class="events-row-payload">{{ formatPayload(event.payload) }}</pre>
      </li>
    </ol>
  </section>
</template>

<style scoped>
.events-inspector { display: flex; flex-direction: column; gap: 8px; }
.events-toolbar { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.events-search {
  display: flex; align-items: center; gap: 6px; flex: 1 1 150px; min-width: 0;
  padding: 5px 8px; border-radius: 8px; border: 1px solid var(--border-muted);
  background: var(--surface-input); color: var(--text-muted);
}
.events-search-input {
  width: 100%; border: 0; background: transparent; color: var(--text-primary);
  font-size: 12px; outline: none; min-width: 0;
}
.events-actions { display: flex; align-items: center; gap: 4px; }
.events-action-btn {
  display: inline-flex; align-items: center; gap: 4px; padding: 5px 8px; border-radius: 7px;
  border: 1px solid var(--border-muted); background: var(--surface-raised); color: var(--text-secondary);
  font-size: 11px; cursor: pointer;
}
.events-action-btn:hover, .events-action-btn.active {
  color: var(--text-primary); border-color: var(--accent-primary); background: var(--surface-hover);
}
.events-chips { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; }
.events-chip {
  padding: 2px 7px; border-radius: 999px; border: 1px solid var(--border-muted);
  background: transparent; color: var(--text-secondary); font-size: 11px; cursor: pointer;
}
.events-chip.active {
  border-color: var(--accent-primary); color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
}
.events-paused-pill {
  margin-left: auto; padding: 2px 6px; border-radius: 999px; font-size: 10px;
  color: var(--accent-warning); background: color-mix(in srgb, var(--accent-warning) 14%, transparent);
}
.events-inspector-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
.events-row {
  border-radius: 8px; border: 1px solid var(--border-muted); background: var(--surface-raised); overflow: hidden;
}
.events-row-head {
  width: 100%; display: flex; align-items: center; gap: 6px; padding: 6px 8px;
  border: 0; background: transparent; color: var(--text-primary); font-size: 12px; cursor: pointer; text-align: left;
}
.events-row-head:hover { background: var(--surface-hover); }
.events-row-type { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-family: var(--font-mono); font-size: 11.5px; }
.events-row-seq { color: var(--text-muted); font-family: var(--font-mono); font-size: 10.5px; }
.events-row-payload {
  margin: 0; padding: 8px; border-top: 1px solid var(--border-muted);
  background: var(--surface-section); color: var(--text-secondary);
  font-family: var(--font-mono); font-size: 11px; line-height: 1.45;
  overflow: auto; max-height: 220px; white-space: pre-wrap; word-break: break-word;
}
</style>
