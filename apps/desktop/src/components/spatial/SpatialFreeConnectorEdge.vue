<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import {
  BaseEdge,
  EdgeLabelRenderer,
  Position,
  getBezierPath,
  getSmoothStepPath,
} from '@vue-flow/core'
import { useI18n } from 'vue-i18n'
import type { SpatialConnectorCurve, SpatialConnectorProps } from './spatialWhiteboardTypes'

const props = withDefaults(defineProps<{
  id: string
  sourceX: number
  sourceY: number
  targetX: number
  targetY: number
  sourcePosition?: Position
  targetPosition?: Position
  label?: string
  selected?: boolean
  markerEnd?: string
  style?: Record<string, string | number>
  data?: Partial<SpatialConnectorProps>
}>(), {
  sourcePosition: Position.Right,
  targetPosition: Position.Left,
  label: '',
  selected: false,
})

const emit = defineEmits<{
  'update:label': [payload: { id: string; label: string }]
  'update:curve': [payload: { id: string; curve: SpatialConnectorCurve }]
}>()

const { t } = useI18n()

const resolvedCurve = computed<SpatialConnectorCurve>(() =>
  props.data?.curve === 'bezier' ? 'bezier' : 'smoothstep',
)
const resolvedLabel = computed(() => {
  if (typeof props.data?.label === 'string') return props.data.label
  return typeof props.label === 'string' ? props.label : ''
})
const resolvedColor = computed(() =>
  props.data?.color || (props.selected ? 'var(--accent-primary, #7c3aed)' : 'var(--text-secondary, #8b949e)'),
)

const markerId = computed(() => `spatial-free-arrow-${props.id.replace(/[^A-Za-z0-9_-]/g, '_')}`)
const effectiveMarkerEnd = computed(() => props.markerEnd || `url(#${markerId.value})`)

const pathResult = computed(() => {
  const params = {
    sourceX: props.sourceX,
    sourceY: props.sourceY,
    sourcePosition: props.sourcePosition,
    targetX: props.targetX,
    targetY: props.targetY,
    targetPosition: props.targetPosition,
  }
  return resolvedCurve.value === 'bezier'
    ? getBezierPath(params)
    : getSmoothStepPath({ ...params, borderRadius: 12 })
})

const edgePath = computed(() => pathResult.value[0])
const labelX = computed(() => pathResult.value[1])
const labelY = computed(() => pathResult.value[2])

const editing = ref(false)
const draftLabel = ref('')
const inputRef = ref<HTMLInputElement | null>(null)

watch(resolvedLabel, (val) => {
  if (!editing.value) draftLabel.value = val
})

function startEditing() {
  draftLabel.value = resolvedLabel.value
  editing.value = true
  void nextTick(() => {
    inputRef.value?.focus()
    inputRef.value?.select()
  })
}

function commitLabel() {
  if (!editing.value) return
  editing.value = false
  const trimmed = draftLabel.value.trim()
  if (trimmed !== resolvedLabel.value) {
    emit('update:label', { id: props.id, label: trimmed })
  }
}

function cancelEditing() {
  editing.value = false
  draftLabel.value = resolvedLabel.value
}

function toggleCurve() {
  emit('update:curve', {
    id: props.id,
    curve: resolvedCurve.value === 'bezier' ? 'smoothstep' : 'bezier',
  })
}
</script>

<template>
  <g class="spatial-free-connector" :data-connector-id="id" :data-connector-curve="resolvedCurve">
    <defs>
      <marker
        :id="markerId"
        viewBox="0 0 12 12"
        refX="10"
        refY="6"
        markerWidth="8"
        markerHeight="8"
        orient="auto-start-reverse"
      >
        <path d="M 1 1.5 L 10.5 6 L 1 10.5 z" :fill="resolvedColor" />
      </marker>
    </defs>

    <BaseEdge
      :id="id"
      :path="edgePath"
      :marker-end="effectiveMarkerEnd"
      :style="{
        stroke: resolvedColor,
        strokeWidth: selected ? 2.4 : 1.8,
        ...style,
      }"
    />

    <path
      :d="edgePath"
      fill="none"
      stroke="transparent"
      stroke-width="18"
      class="spatial-connector-hit"
      data-testid="connector-hit-path"
      @dblclick.stop="startEditing"
    />

    <EdgeLabelRenderer>
      <div
        v-if="editing || resolvedLabel || selected"
        class="spatial-connector-label-wrap nodrag nopan"
        :style="{
          transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)`,
        }"
        @dblclick.stop="startEditing"
      >
        <input
          v-if="editing"
          ref="inputRef"
          v-model="draftLabel"
          type="text"
          class="spatial-connector-input"
          :placeholder="t('space.whiteboard.textPlaceholder')"
          data-testid="connector-label-input"
          @blur="commitLabel"
          @keydown.enter.prevent="commitLabel"
          @keydown.esc.stop.prevent="cancelEditing"
        />
        <div v-else class="spatial-connector-pill" :class="{ 'is-selected': selected }">
          <button
            type="button"
            class="spatial-connector-label-btn"
            data-testid="connector-label-btn"
            @dblclick.stop="startEditing"
          >
            {{ resolvedLabel || '+' }}
          </button>
          <span v-if="selected" class="spatial-connector-sep" aria-hidden="true" />
          <button
            v-if="selected"
            type="button"
            class="spatial-connector-curve-btn"
            data-testid="connector-curve-toggle"
            @click.stop="toggleCurve"
          >
            {{ resolvedCurve === 'bezier' ? '∿' : '┐' }}
          </button>
        </div>
      </div>
    </EdgeLabelRenderer>
  </g>
</template>

<style scoped>
.spatial-connector-hit {
  cursor: pointer;
  pointer-events: stroke;
}

.spatial-connector-label-wrap {
  position: absolute;
  pointer-events: auto;
  z-index: 6;
}

.spatial-connector-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: color-mix(in srgb, var(--surface-raised, #ffffff) 94%, transparent);
  backdrop-filter: blur(10px);
  color: var(--text-primary);
  font-size: 11.5px;
  font-weight: 500;
  box-shadow: 0 3px 10px rgba(0, 0, 0, 0.1);
}

.spatial-connector-pill.is-selected {
  border-color: var(--accent-primary, #7c3aed);
}

.spatial-connector-label-btn,
.spatial-connector-curve-btn {
  border: 0;
  padding: 0 2px;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.spatial-connector-sep {
  width: 1px;
  height: 12px;
  background: var(--border-muted);
  margin: 0 2px;
}

.spatial-connector-curve-btn {
  padding-left: 2px;
  color: var(--text-secondary);
}

.spatial-connector-curve-btn:hover {
  color: var(--accent-primary, #7c3aed);
}

.spatial-connector-input {
  min-width: 110px;
  max-width: 220px;
  padding: 3px 8px;
  border-radius: 999px;
  border: 1px solid var(--accent-primary, #7c3aed);
  background: var(--surface-raised, #ffffff);
  color: var(--text-primary);
  font-size: 12px;
  outline: none;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.14);
}
</style>
