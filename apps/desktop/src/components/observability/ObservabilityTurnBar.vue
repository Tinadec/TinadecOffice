<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { ChevronLeft, ChevronRight, X } from '@lucide/vue'
import type { ObservabilityTurnEntry } from '@/composables/useObservabilityRunSelection'

const props = withDefaults(
  defineProps<{
    turns: readonly ObservabilityTurnEntry[]
    effectiveRunId: string | null
    isFollowingLatest: boolean
    compact?: boolean
    filterLabel?: string | null
  }>(),
  {
    compact: false,
    filterLabel: null,
  },
)

const emit = defineEmits<{
  'select-run': [runId: string]
  'follow-latest': []
  'clear-filter': []
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? t(key, params ?? {}) : fallback
}

const useDropdownMode = computed(() => props.turns.length > 4)

const currentIndex = computed(() => {
  if (!props.effectiveRunId || props.turns.length === 0) return props.turns.length - 1
  const idx = props.turns.findIndex((turn) => turn.runId === props.effectiveRunId)
  return idx >= 0 ? idx : props.turns.length - 1
})

const canStepPrev = computed(() => props.turns.length > 1 && currentIndex.value > 0)
const canStepNext = computed(() => props.turns.length > 1 && currentIndex.value < props.turns.length - 1)

function formatDurationShort(durationMs: number | null): string | null {
  if (durationMs === null || !Number.isFinite(durationMs) || durationMs < 0) return null
  if (durationMs < 1000) return `${Math.round(durationMs)}ms`
  const sec = durationMs / 1000
  if (sec < 60) return `${sec.toFixed(1)}s`
  const mins = Math.floor(sec / 60)
  const rem = Math.round(sec % 60)
  return `${mins}m${rem.toString().padStart(2, '0')}s`
}

function statusGlyph(turn: ObservabilityTurnEntry): string {
  if (turn.isRunning) return '●'
  if (turn.isFailed) return '✕'
  return '✓'
}

function statusTone(turn: ObservabilityTurnEntry): 'running' | 'failed' | 'done' {
  if (turn.isRunning) return 'running'
  if (turn.isFailed) return 'failed'
  return 'done'
}

function formatTurnOption(turn: ObservabilityTurnEntry): string {
  const dur = formatDurationShort(turn.durationMs)
  const glyph = statusGlyph(turn)
  return dur ? `#${turn.turnNo} · ${dur} ${glyph}` : `#${turn.turnNo} ${glyph}`
}

function formatPillTitle(turn: ObservabilityTurnEntry): string {
  const base = tr('observability.turnBar.turnTooltip', `Turn #${turn.turnNo}`, { turn: turn.turnNo })
  const dur = formatDurationShort(turn.durationMs)
  const parts = [base]
  if (dur) parts.push(dur)
  if (turn.headline) parts.push(turn.headline)
  return parts.join(' · ')
}

function onSelectPill(turn: ObservabilityTurnEntry): void {
  emit('select-run', turn.runId)
}

function onSelectChange(event: Event): void {
  const target = event.target as HTMLSelectElement | null
  const value = target?.value?.trim()
  if (value) {
    emit('select-run', value)
  }
}

function stepBy(delta: -1 | 1): void {
  const nextIdx = currentIndex.value + delta
  if (nextIdx >= 0 && nextIdx < props.turns.length) {
    emit('select-run', props.turns[nextIdx]!.runId)
  }
}
</script>

<template>
  <div
    class="obs-turn-bar"
    :class="{ 'obs-turn-bar--compact': compact }"
    data-testid="observability-turn-bar"
    role="toolbar"
    :aria-label="tr('observability.turnBar.ariaLabel', 'Session turns')"
  >
    <div class="obs-turn-bar__main">
      <!-- <= 4 turns: flat compact pills -->
      <div v-if="turns.length > 0 && !useDropdownMode" class="obs-turn-bar__pills">
        <button
          v-for="turn in turns"
          :key="turn.runId"
          type="button"
          class="obs-turn-pill"
          :class="{ 'is-active': turn.runId === effectiveRunId }"
          :data-tone="statusTone(turn)"
          :data-turn-no="turn.turnNo"
          data-testid="observability-turn-pill"
          :aria-pressed="turn.runId === effectiveRunId"
          :title="formatPillTitle(turn)"
          @click="onSelectPill(turn)"
        >
          <span class="obs-turn-pill__num">#{{ turn.turnNo }}</span>
          <span class="obs-turn-pill__glyph" :data-tone="statusTone(turn)" aria-hidden="true">{{ statusGlyph(turn) }}</span>
        </button>
      </div>

      <!-- > 4 turns: compact stepper + native select -->
      <div v-else-if="useDropdownMode" class="obs-turn-bar__stepper">
        <button
          type="button"
          class="obs-turn-step-btn"
          data-testid="observability-turn-prev"
          :disabled="!canStepPrev"
          :title="tr('observability.turnBar.prevTurn', 'Previous turn')"
          :aria-label="tr('observability.turnBar.prevTurn', 'Previous turn')"
          @click="stepBy(-1)"
        >
          <ChevronLeft :size="13" />
        </button>
        <div class="obs-turn-select-wrap">
          <select
            class="obs-turn-select"
            data-testid="observability-turn-select"
            :aria-label="tr('observability.turnBar.selectTurn', 'Select turn')"
            :value="effectiveRunId ?? ''"
            @change="onSelectChange"
          >
            <option
              v-for="turn in turns"
              :key="turn.runId"
              :value="turn.runId"
            >
              {{ formatTurnOption(turn) }}
            </option>
          </select>
          <span class="obs-turn-select-caret" aria-hidden="true">▾</span>
        </div>
        <button
          type="button"
          class="obs-turn-step-btn"
          data-testid="observability-turn-next"
          :disabled="!canStepNext"
          :title="tr('observability.turnBar.nextTurn', 'Next turn')"
          :aria-label="tr('observability.turnBar.nextTurn', 'Next turn')"
          @click="stepBy(1)"
        >
          <ChevronRight :size="13" />
        </button>
      </div>

      <!-- Empty / awaiting first turn label when bar still mounts for filter/actions -->
      <span v-else class="obs-turn-bar__idle">
        {{ tr('observability.turnBar.noTurns', 'Live') }}
      </span>

      <!-- Active node/category filter chip -->
      <button
        v-if="filterLabel"
        type="button"
        class="obs-turn-filter-chip"
        data-testid="observability-filter-chip"
        :title="tr('observability.turnBar.clearFilter', 'Clear filter')"
        @click="emit('clear-filter')"
      >
        <span class="obs-turn-filter-chip__text">{{ filterLabel }}</span>
        <X :size="11" aria-hidden="true" />
      </button>

      <!-- Return to live button when pinned on a historical turn -->
      <button
        v-if="!isFollowingLatest"
        type="button"
        class="obs-turn-follow-btn"
        data-testid="observability-follow-latest"
        @click="emit('follow-latest')"
      >
        <span class="obs-turn-follow-dot" aria-hidden="true">●</span>
        <span>{{ tr('observability.turnBar.followLatest', 'Back to live') }}</span>
      </button>
    </div>

    <div v-if="$slots.actions" class="obs-turn-bar__actions">
      <slot name="actions" />
    </div>
  </div>
</template>

<style scoped>
.obs-turn-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-height: 36px;
  padding: 5px 12px;
  background: var(--bg-primary);
  border-bottom: 1px solid var(--border-muted);
  flex-shrink: 0;
  font-variant-numeric: tabular-nums;
}

.obs-turn-bar--compact {
  min-height: 32px;
  padding: 3px 10px;
}

.obs-turn-bar__main {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 5px;
  min-width: 0;
  flex: 1 1 auto;
}

.obs-turn-bar__pills {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px;
}

.obs-turn-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 24px;
  padding: 0 7px;
  border: 1px solid transparent;
  border-radius: 5px;
  background: transparent;
  color: var(--text-secondary);
  font-family: var(--font-mono);
  font-size: 11.5px;
  line-height: 1;
  font-variant-numeric: tabular-nums;
  cursor: pointer;
  transition: background-color 0.12s ease, color 0.12s ease, border-color 0.12s ease;
}

.obs-turn-pill:hover {
  background: var(--bg-secondary);
  color: var(--text-primary);
}

.obs-turn-pill.is-active {
  background: var(--bg-primary-button);
  color: var(--text-on-primary);
  font-weight: 600;
}

.obs-turn-pill__glyph {
  font-size: 10px;
  line-height: 1;
}

.obs-turn-pill:not(.is-active) .obs-turn-pill__glyph[data-tone='running'] {
  color: var(--obs-status-ok, var(--accent-success));
}

.obs-turn-pill:not(.is-active) .obs-turn-pill__glyph[data-tone='failed'] {
  color: var(--obs-status-danger, var(--accent-danger));
}

.obs-turn-pill:not(.is-active) .obs-turn-pill__glyph[data-tone='done'] {
  color: var(--text-muted);
}

.obs-turn-bar__stepper {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.obs-turn-step-btn {
  display: inline-grid;
  place-items: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: none;
  border-radius: 5px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  transition: background-color 0.12s ease, color 0.12s ease;
}

.obs-turn-step-btn:hover:not(:disabled) {
  background: var(--bg-secondary);
  color: var(--text-primary);
}

.obs-turn-step-btn:disabled {
  opacity: 0.3;
  cursor: default;
}

.obs-turn-select-wrap {
  position: relative;
  display: inline-flex;
  align-items: center;
}

.obs-turn-select {
  appearance: none;
  height: 24px;
  padding: 0 20px 0 8px;
  border: 1px solid var(--border-muted);
  border-radius: 5px;
  background: var(--bg-secondary);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 11.5px;
  line-height: 22px;
  font-variant-numeric: tabular-nums;
  cursor: pointer;
}

.obs-turn-select:hover {
  border-color: var(--border-default);
}

.obs-turn-select:focus-visible {
  outline: 2px solid var(--border-input-focus);
  outline-offset: 1px;
}

.obs-turn-select-caret {
  position: absolute;
  right: 6px;
  pointer-events: none;
  font-size: 10px;
  color: var(--text-muted);
}

.obs-turn-bar__idle {
  font-family: var(--font-mono);
  font-size: 11px;
  color: var(--text-muted);
}

.obs-turn-filter-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  max-width: 150px;
  padding: 0 7px;
  border: 1px solid var(--border-muted);
  border-radius: 999px;
  background: var(--bg-secondary);
  color: var(--text-primary);
  font-family: var(--font-mono);
  font-size: 11px;
  cursor: pointer;
}

.obs-turn-filter-chip:hover {
  background: var(--bg-hover);
}

.obs-turn-filter-chip__text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.obs-turn-follow-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  height: 22px;
  padding: 0 8px;
  border: 1px solid color-mix(in srgb, var(--obs-status-ok, var(--accent-success)) 32%, var(--border-muted));
  border-radius: 999px;
  background: color-mix(in srgb, var(--obs-status-ok, var(--accent-success)) 10%, transparent);
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: background-color 0.12s ease;
}

.obs-turn-follow-btn:hover {
  background: color-mix(in srgb, var(--obs-status-ok, var(--accent-success)) 18%, transparent);
}

.obs-turn-follow-dot {
  color: var(--obs-status-ok, var(--accent-success));
  font-size: 9px;
  line-height: 1;
}

.obs-turn-bar__actions {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}
</style>
