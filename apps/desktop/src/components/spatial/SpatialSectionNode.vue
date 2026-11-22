<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { Handle, Position } from '@vue-flow/core'
import { NodeResizer } from '@vue-flow/node-resizer'
import { ChevronDown, ChevronRight } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import {
  SPATIAL_SECTION_PALETTE,
  SPATIAL_SECTION_TONES,
  isSectionTone,
  type SpatialSectionProps,
  type SpatialSectionTone,
} from './spatialWhiteboardTypes'

const props = withDefaults(defineProps<{
  id: string
  title?: string
  color?: SpatialSectionTone | string
  collapsed?: boolean
  childCount?: number
  selected?: boolean
  readonly?: boolean
  data?: Partial<SpatialSectionProps> & { childCount?: number }
}>(), {
  selected: false,
  readonly: false,
})

const emit = defineEmits<{
  'update:title': [title: string]
  'update:color': [color: SpatialSectionTone]
  'update:collapsed': [collapsed: boolean]
  'resize-start': []
  'resize-end': [params: { x: number; y: number; width: number; height: number }]
}>()

const { t } = useI18n()

const resolvedTitle = computed(() => props.title ?? props.data?.title ?? '')
const resolvedColor = computed<SpatialSectionTone>(() => {
  const raw = props.color ?? props.data?.color
  return isSectionTone(raw) ? raw : 'neutral'
})
const resolvedCollapsed = computed(() => Boolean(props.collapsed ?? props.data?.collapsed ?? false))
const resolvedChildCount = computed(() => props.childCount ?? props.data?.childCount ?? 0)
const palette = computed(() => SPATIAL_SECTION_PALETTE[resolvedColor.value])

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
</script>

<template>
  <section
    class="spatial-section-node"
    :class="{
      'is-selected': selected,
      'is-collapsed': resolvedCollapsed,
    }"
    :data-section-id="id"
    :data-section-tone="resolvedColor"
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
  box-shadow: inset 0 1px 0 color-mix(in srgb, #ffffff 10%, transparent);
  transition: border-color 140ms ease, box-shadow 140ms ease, background-color 140ms ease;
}

.spatial-section-node.is-selected {
  border-color: var(--accent-primary, #7c3aed);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-primary, #7c3aed) 24%, transparent);
}

.spatial-section-node.is-collapsed {
  height: auto;
  min-height: 44px;
}

.spatial-section-header {
  display: flex;
  align-items: center;
  gap: 6px;
  min-height: 36px;
  padding: 6px 10px;
  border-radius: 15px 15px 0 0;
  background: var(--section-header-bg);
  border-bottom: 1px solid var(--section-border);
  backdrop-filter: blur(10px);
  cursor: grab;
  user-select: none;
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
  background: var(--surface-hover, rgba(128, 128, 128, 0.14));
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
  border: 1px solid var(--accent-primary, #7c3aed);
  background: var(--surface-raised, #ffffff);
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
  background: color-mix(in srgb, var(--surface-raised, #ffffff) 75%, transparent);
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
  transition: opacity 140ms ease;
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
  transition: transform 120ms ease;
}

.spatial-section-swatch:hover {
  transform: scale(1.15);
}

.spatial-section-swatch.is-active {
  box-shadow: 0 0 0 2px var(--surface-raised, #ffffff), 0 0 0 3.5px var(--accent-primary, #7c3aed);
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
</style>
