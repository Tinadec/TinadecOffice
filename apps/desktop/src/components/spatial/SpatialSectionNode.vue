<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { Handle, Position } from '@vue-flow/core'
import { NodeResizer } from '@vue-flow/node-resizer'
import { ChevronDown, ChevronRight } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { ProjectBoardTaskDto } from '@/api'
import { useLiveDigest } from '@/composables/useLiveDigest'
import SpatialTaskStatusCapsule from './SpatialTaskStatusCapsule.vue'
import {
  SPATIAL_SECTION_PALETTE,
  SPATIAL_SECTION_TONES,
  isSectionTone,
  type SpatialSectionProps,
  type SpatialSectionTone,
} from './spatialWhiteboardTypes'

export interface SpatialSectionLiveDigestData {
  runId?: string | null
  liveHeadline?: string | null
  liveUsagePercent?: number | null
  liveCompactionCount?: number
  liveRunning?: boolean
  livePhase?: string | null
}

const props = withDefaults(defineProps<{
  id: string
  title?: string
  color?: SpatialSectionTone | string
  collapsed?: boolean
  childCount?: number
  runId?: string | null
  board_task_id?: string | null
  board_task_key?: string | null
  linkedTask?: ProjectBoardTaskDto | null
  liveHeadline?: string | null
  liveUsagePercent?: number | null
  liveCompactionCount?: number
  liveRunning?: boolean
  selected?: boolean
  readonly?: boolean
  data?: Partial<SpatialSectionProps> & {
    childCount?: number
    linkedTask?: ProjectBoardTaskDto | null
  } & SpatialSectionLiveDigestData
}>(), {
  selected: false,
  readonly: false,
  runId: undefined,
  board_task_id: undefined,
  board_task_key: undefined,
  linkedTask: undefined,
  liveHeadline: undefined,
  liveUsagePercent: undefined,
  liveCompactionCount: undefined,
  liveRunning: undefined,
})

const emit = defineEmits<{
  'update:title': [title: string]
  'update:color': [color: SpatialSectionTone]
  'update:collapsed': [collapsed: boolean]
  'open-live-digest': [runId: string | null]
  'open-board-task': [
    payload: {
      taskId: string | null
      taskKey: string | null
      task: ProjectBoardTaskDto | null
    },
  ]
  'resize-start': []
  'resize-end': [params: { x: number; y: number; width: number; height: number }]
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? t(key, params) : t(key)
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

const resolvedTitle = computed(() => props.title ?? props.data?.title ?? '')
const resolvedColor = computed<SpatialSectionTone>(() => {
  const raw = props.color ?? props.data?.color
  return isSectionTone(raw) ? raw : 'neutral'
})
const resolvedCollapsed = computed(() => Boolean(props.collapsed ?? props.data?.collapsed ?? false))
const resolvedChildCount = computed(() => props.childCount ?? props.data?.childCount ?? 0)
const resolvedRunId = computed<string | null>(() => {
  const raw = props.runId ?? props.data?.runId
  return typeof raw === 'string' && raw.trim().length > 0 ? raw.trim() : null
})
const resolvedBoardTaskId = computed(() => props.board_task_id ?? props.data?.board_task_id ?? null)
const resolvedBoardTaskKey = computed(() => props.board_task_key ?? props.data?.board_task_key ?? null)
const resolvedLinkedTask = computed(() => props.linkedTask ?? props.data?.linkedTask ?? null)
const palette = computed(() => SPATIAL_SECTION_PALETTE[resolvedColor.value])

const explicitHeadline = computed<string | null>(() => {
  const raw = props.liveHeadline ?? props.data?.liveHeadline
  return typeof raw === 'string' && raw.trim().length > 0 ? raw.trim() : null
})
const explicitUsagePercent = computed<number | null>(() => {
  const raw = props.liveUsagePercent ?? props.data?.liveUsagePercent
  return typeof raw === 'number' && Number.isFinite(raw) ? Math.max(0, Math.min(100, Math.round(raw))) : null
})
const hasExplicitDigest = computed(() => explicitHeadline.value !== null || explicitUsagePercent.value !== null)

// Subscribe to same-source shared SSE digest for Run sections (ref-counted per runId).
const digest = useLiveDigest(() => (!hasExplicitDigest.value ? resolvedRunId.value : null))

const resolvedHeadline = computed<string>(() => {
  if (explicitHeadline.value !== null) return explicitHeadline.value
  return digest.headline.value.trim()
})

const resolvedUsagePercent = computed<number | null>(() => {
  if (explicitUsagePercent.value !== null) return explicitUsagePercent.value
  const wl = digest.contextWaterline.value
  if (!wl) return null
  if (wl.max_tokens <= 0 && wl.usage_percent <= 0) return null
  return Math.max(0, Math.min(100, Math.round(wl.usage_percent)))
})

const resolvedCompactionCount = computed<number>(() => {
  const explicit = props.liveCompactionCount ?? props.data?.liveCompactionCount
  if (typeof explicit === 'number' && Number.isFinite(explicit)) return Math.max(0, Math.round(explicit))
  return digest.contextWaterline.value?.compaction_count ?? 0
})

const resolvedLiveRunning = computed<boolean>(() => {
  const explicit = props.liveRunning ?? props.data?.liveRunning
  if (typeof explicit === 'boolean') return explicit
  return digest.isRunning.value
})

const waterlineTone = computed<'normal' | 'warning' | 'critical'>(() => {
  const pct = resolvedUsagePercent.value ?? 0
  if (pct >= 90) return 'critical'
  if (pct >= 75) return 'warning'
  return 'normal'
})

const hasLiveDigest = computed(() => Boolean(resolvedHeadline.value || resolvedUsagePercent.value !== null))

const editing = ref(false)
const draftTitle = ref('')
const inputRef = ref<HTMLInputElement | null>(null)
const sides = [Position.Top, Position.Bottom, Position.Left, Position.Right] as const

watch(resolvedTitle, (val) => {
  if (!editing.value) draftTitle.value = val
})

function startEditing() {
  if (props.readonly) return
  draftTitle.value = resolvedTitle.value
  editing.value = true
  void nextTick(() => {
    inputRef.value?.focus()
    inputRef.value?.select()
  })
}

function commitTitle() {
  if (!editing.value) return
  editing.value = false
  const trimmed = draftTitle.value.trim()
  if (trimmed !== resolvedTitle.value) {
    emit('update:title', trimmed)
  }
}

function cancelEditing() {
  editing.value = false
  draftTitle.value = resolvedTitle.value
}

function toggleCollapse() {
  emit('update:collapsed', !resolvedCollapsed.value)
}

function selectColor(tone: SpatialSectionTone) {
  if (props.readonly) return
  emit('update:color', tone)
}

function onOpenDigest() {
  emit('open-live-digest', resolvedRunId.value)
}
</script>

<template>
  <section
    class="spatial-section-node"
    :class="{
      'is-selected': selected,
      'is-collapsed': resolvedCollapsed,
      'has-live-digest': hasLiveDigest,
    }"
    :data-section-id="id"
    :data-section-tone="resolvedColor"
    :data-run-id="resolvedRunId || undefined"
    :style="{
      '--section-fill': palette.fill,
      '--section-border': palette.border,
      '--section-header-bg': palette.headerBg,
      '--section-swatch': palette.swatch,
    }"
  >
    <template v-for="side in sides" :key="side">
      <Handle :id="`${side}-target`" type="target" :position="side" class="spatial-section-handle" />
      <Handle :id="`${side}-source`" type="source" :position="side" class="spatial-section-handle" />
    </template>

    <NodeResizer
      :is-visible="selected && !resolvedCollapsed && !readonly"
      :min-width="220"
      :min-height="140"
      :max-width="3200"
      :max-height="2400"
      @resize-start="emit('resize-start')"
      @resize-end="({ params }) => emit('resize-end', params)"
    />

    <header class="spatial-section-header space-drag-handle" @dblclick.stop="startEditing">
      <div class="spatial-section-header-main">
        <button
          type="button"
          class="spatial-section-collapse nodrag nopan"
          :aria-label="resolvedCollapsed ? t('space.whiteboard.expandSection') : t('space.whiteboard.collapseSection')"
          :title="resolvedCollapsed ? t('space.whiteboard.expandSection') : t('space.whiteboard.collapseSection')"
          data-testid="section-collapse-btn"
          @click.stop="toggleCollapse"
        >
          <ChevronRight v-if="resolvedCollapsed" :size="14" />
          <ChevronDown v-else :size="14" />
        </button>

        <input
          v-if="editing"
          ref="inputRef"
          v-model="draftTitle"
          type="text"
          class="spatial-section-title-input nodrag nopan"
          :aria-label="t('space.whiteboard.renameSection')"
          :placeholder="t('space.whiteboard.sectionUntitled')"
          data-testid="section-title-input"
          @blur="commitTitle"
          @keydown.enter.prevent="commitTitle"
          @keydown.esc.stop.prevent="cancelEditing"
        />
        <span
          v-else
          class="spatial-section-title"
          :title="t('space.whiteboard.renameSection')"
          data-testid="section-title-label"
        >
          {{ resolvedTitle || t('space.whiteboard.sectionUntitled') }}
        </span>

        <span v-if="resolvedChildCount > 0" class="spatial-section-count" data-testid="section-child-count">
          {{ resolvedChildCount }}
        </span>

        <SpatialTaskStatusCapsule
          :board-task-id="resolvedBoardTaskId"
          :board-task-key="resolvedBoardTaskKey"
          :linked-task="resolvedLinkedTask"
          @open-task="emit('open-board-task', $event)"
        />

        <div
          v-if="!readonly"
          class="spatial-section-swatches nodrag nopan"
          :class="{ 'is-visible': selected }"
          role="group"
        >
          <button
            v-for="tone in SPATIAL_SECTION_TONES"
            :key="tone"
            type="button"
            class="spatial-section-swatch"
            :class="{ 'is-active': tone === resolvedColor }"
            :style="{ backgroundColor: SPATIAL_SECTION_PALETTE[tone].swatch }"
            :aria-label="tone"
            :aria-pressed="tone === resolvedColor"
            :data-section-color-dot="tone"
            @click.stop="selectColor(tone)"
          />
        </div>
      </div>

      <!-- Restrained single-line Live Digest subtitle & Context Waterline micro-badge -->
      <button
        v-if="hasLiveDigest"
        type="button"
        class="spatial-section-live-row nodrag nopan"
        :title="resolvedHeadline || tr('space.liveDigest.tabLabel', 'Live Digest')"
        data-testid="section-live-digest"
        @click.stop="onOpenDigest"
        @dblclick.stop
      >
        <span
          class="spatial-section-live-dot"
          :class="{ 'is-running': resolvedLiveRunning }"
          aria-hidden="true"
        />
        <span
          v-if="resolvedHeadline"
          class="spatial-section-live-headline"
          data-testid="section-live-headline"
        >
          {{ resolvedHeadline }}
        </span>
        <span
          v-if="resolvedUsagePercent !== null"
          class="spatial-section-ctx-badge"
          :class="`is-${waterlineTone}`"
          :title="resolvedCompactionCount > 0
            ? tr('space.liveDigest.compactionCount', '{count} compacted', { count: resolvedCompactionCount })
            : tr('space.liveDigest.contextBadge', 'Ctx {percent}%', { percent: resolvedUsagePercent })"
          data-testid="section-live-waterline"
        >
          <span class="spatial-section-ctx-track" aria-hidden="true">
            <span class="spatial-section-ctx-fill" :style="{ width: `${resolvedUsagePercent}%` }" />
          </span>
          <span class="spatial-section-ctx-text">{{ resolvedUsagePercent }}%</span>
        </span>
      </button>
    </header>

    <div v-if="!resolvedCollapsed" class="spatial-section-canvas" />
  </section>
</template>

<style scoped>
.spatial-section-node {
  position: relative;
  width: 100%;
  height: 100%;
  min-width: 220px;
  min-height: 140px;
  display: flex;
  flex-direction: column;
  border-radius: 16px;
  border: 1px solid var(--section-border);
  background: var(--section-fill);
  box-shadow: inset 0 1px 0 color-mix(in srgb, var(--text-primary) 6%, transparent);
  transition:
    border-color var(--duration-tap, 120ms) var(--ease-snappy, ease),
    box-shadow var(--duration-tap, 120ms) var(--ease-snappy, ease),
    background-color var(--duration-tap, 120ms) var(--ease-snappy, ease);
}

.spatial-section-node.is-selected {
  border-color: var(--accent-primary);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary) 24%, transparent);
}

.spatial-section-node.is-collapsed {
  height: auto;
  min-height: 44px;
}

.spatial-section-header {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-height: 36px;
  padding: 6px 10px;
  border-radius: 15px 15px 0 0;
  background: var(--section-header-bg);
  border-bottom: 1px solid var(--section-border);
  backdrop-filter: blur(10px);
  cursor: grab;
  user-select: none;
}

.spatial-section-header-main {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.spatial-section-node.is-collapsed .spatial-section-header {
  border-radius: 15px;
  border-bottom: 0;
}

.spatial-section-collapse {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  flex-shrink: 0;
}

.spatial-section-collapse:hover {
  background: var(--surface-hover, var(--bg-hover));
  color: var(--text-primary);
}

.spatial-section-title {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 280px;
}

.spatial-section-title-input {
  flex: 1;
  min-width: 80px;
  max-width: 280px;
  padding: 2px 6px;
  border-radius: 6px;
  border: 1px solid var(--accent-primary);
  background: var(--surface-raised, var(--bg-overlay));
  color: var(--text-primary);
  font-size: 13px;
  font-weight: 600;
  outline: none;
}

.spatial-section-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 20px;
  height: 18px;
  padding: 0 5px;
  border-radius: 999px;
  border: 1px solid var(--section-border);
  background: color-mix(in srgb, var(--surface-raised, var(--bg-overlay)) 75%, transparent);
  color: var(--text-secondary);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.spatial-section-swatches {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  margin-left: auto;
  opacity: 0;
  pointer-events: none;
  transition: opacity var(--duration-tap, 120ms) var(--ease-snappy, ease);
}

.spatial-section-header:hover .spatial-section-swatches,
.spatial-section-swatches.is-visible {
  opacity: 1;
  pointer-events: auto;
}

.spatial-section-swatch {
  width: 13px;
  height: 13px;
  border-radius: 999px;
  border: 1px solid rgba(0, 0, 0, 0.18);
  padding: 0;
  cursor: pointer;
  transition: transform var(--duration-tap, 120ms) var(--ease-spring-soft, ease);
}

.spatial-section-swatch:hover {
  transform: scale(1.15);
}

.spatial-section-swatch.is-active {
  box-shadow: 0 0 0 2px var(--surface-raised, var(--bg-overlay)), 0 0 0 3.5px var(--accent-primary);
}

/* Single-line restrained Live Digest subtitle & Context Waterline micro-badge */
.spatial-section-live-row {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  width: 100%;
  padding: 2px 4px 1px 26px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-family: inherit;
  font-size: 11px;
  line-height: 1.35;
  text-align: left;
  cursor: pointer;
  transition:
    background-color var(--duration-tap, 120ms) var(--ease-snappy, ease),
    color var(--duration-tap, 120ms) var(--ease-snappy, ease);
}

.spatial-section-live-row:hover {
  background: color-mix(in srgb, var(--surface-hover, var(--bg-hover)) 65%, transparent);
  color: var(--text-primary);
}

.spatial-section-live-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  flex-shrink: 0;
  background: var(--text-muted);
}

.spatial-section-live-dot.is-running {
  background: var(--accent-primary);
  animation: section-live-pulse 1.6s ease-in-out infinite;
}

@keyframes section-live-pulse {
  0%, 100% {
    opacity: 1;
  }
  50% {
    opacity: 0.36;
  }
}

.spatial-section-live-headline {
  flex: 1;
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.spatial-section-ctx-badge {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  flex-shrink: 0;
  margin-left: auto;
  height: 17px;
  padding: 0 6px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: color-mix(in srgb, var(--surface-raised, var(--bg-overlay)) 72%, transparent);
  color: var(--text-secondary);
  font-size: 10px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.spatial-section-ctx-badge.is-warning {
  border-color: color-mix(in srgb, var(--accent-warning) 40%, var(--border-muted));
  color: var(--accent-warning);
}

.spatial-section-ctx-badge.is-critical {
  border-color: color-mix(in srgb, var(--accent-danger) 42%, var(--border-muted));
  color: var(--accent-danger);
}

.spatial-section-ctx-track {
  width: 24px;
  height: 3px;
  border-radius: 999px;
  background: var(--border-default);
  overflow: hidden;
}

.spatial-section-ctx-fill {
  display: block;
  height: 100%;
  border-radius: 999px;
  background: currentColor;
  transition: width var(--duration-panel, 240ms) var(--ease-snappy, ease);
}

.spatial-section-ctx-text {
  line-height: 1;
}

.spatial-section-canvas {
  flex: 1;
  min-height: 0;
  pointer-events: none;
}

.spatial-section-handle {
  opacity: 0;
  width: 8px;
  height: 8px;
}

@media (prefers-reduced-motion: reduce) {
  .spatial-section-live-dot.is-running {
    animation: none;
  }
}
</style>
