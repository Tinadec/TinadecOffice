<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { Handle, Position } from '@vue-flow/core'
import { NodeResizer } from '@vue-flow/node-resizer'
import { useI18n } from 'vue-i18n'
import type { ProjectBoardTaskDto } from '@/api'
import SpatialTaskStatusCapsule from './SpatialTaskStatusCapsule.vue'
import {
  SPATIAL_TEXT_SCALES,
  isTextScale,
  type SpatialTextProps,
  type SpatialTextScale,
} from './spatialWhiteboardTypes'

const props = withDefaults(defineProps<{
  id: string
  text?: string
  scale?: SpatialTextScale
  color?: string
  align?: 'left' | 'center' | 'right'
  board_task_id?: string | null
  board_task_key?: string | null
  linkedTask?: ProjectBoardTaskDto | null
  selected?: boolean
  readonly?: boolean
  data?: Partial<SpatialTextProps> & { linkedTask?: ProjectBoardTaskDto | null }
}>(), {
  selected: false,
  readonly: false,
  board_task_id: undefined,
  board_task_key: undefined,
  linkedTask: undefined,
})

const emit = defineEmits<{
  'update:text': [text: string]
  'update:scale': [scale: SpatialTextScale]
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

const { t } = useI18n()

const resolvedText = computed(() => props.text ?? props.data?.text ?? '')
const resolvedScale = computed<SpatialTextScale>(() => {
  const raw = props.scale ?? props.data?.scale
  return isTextScale(raw) ? raw : 'md'
})
const resolvedColor = computed(() => props.color ?? props.data?.color ?? '')
const resolvedAlign = computed<'left' | 'center' | 'right'>(() => props.align ?? props.data?.align ?? 'left')
const resolvedBoardTaskId = computed(() => props.board_task_id ?? props.data?.board_task_id ?? null)
const resolvedBoardTaskKey = computed(() => props.board_task_key ?? props.data?.board_task_key ?? null)
const resolvedLinkedTask = computed(() => props.linkedTask ?? props.data?.linkedTask ?? null)
const hasBoardTaskLink = computed(() =>
  Boolean(resolvedBoardTaskId.value || resolvedBoardTaskKey.value || resolvedLinkedTask.value),
)

const scaleLabels: Record<SpatialTextScale, string> = {
  sm: 'S',
  md: 'M',
  lg: 'L',
  xl: 'XL',
}

const editing = ref(false)
const draft = ref('')
const textareaRef = ref<HTMLTextAreaElement | null>(null)
const sides = [Position.Top, Position.Bottom, Position.Left, Position.Right] as const

watch(resolvedText, (val) => {
  if (!editing.value) draft.value = val
})

function startEditing() {
  if (props.readonly) return
  draft.value = resolvedText.value
  editing.value = true
  void nextTick(() => {
    textareaRef.value?.focus()
    const len = draft.value.length
    textareaRef.value?.setSelectionRange(len, len)
  })
}

function commit() {
  if (!editing.value) return
  editing.value = false
  if (draft.value !== resolvedText.value) {
    emit('update:text', draft.value)
  }
}

function setScale(scale: SpatialTextScale) {
  if (props.readonly) return
  emit('update:scale', scale)
}
</script>

<template>
  <div
    class="spatial-text-node space-drag-handle"
    :class="[
      `scale-${resolvedScale}`,
      { 'is-selected': selected, 'is-editing': editing },
    ]"
    :data-text-id="id"
    :data-text-scale="resolvedScale"
    :style="{
      color: resolvedColor || 'var(--text-primary)',
      textAlign: resolvedAlign,
    }"
    @dblclick.stop="startEditing"
  >
    <template v-for="side in sides" :key="side">
      <Handle :id="`${side}-target`" type="target" :position="side" class="spatial-text-handle" />
      <Handle :id="`${side}-source`" type="source" :position="side" class="spatial-text-handle" />
    </template>

    <NodeResizer
      :is-visible="selected && !readonly"
      :min-width="100"
      :min-height="36"
      :max-width="1200"
      :max-height="800"
      @resize-start="emit('resize-start')"
      @resize-end="({ params }) => emit('resize-end', params)"
    />

    <div
      v-if="selected && !readonly"
      class="spatial-text-scale-bar nodrag nopan"
      role="group"
    >
      <button
        v-for="item in SPATIAL_TEXT_SCALES"
        :key="item"
        type="button"
        class="spatial-text-scale-btn"
        :class="{ 'is-active': item === resolvedScale }"
        :aria-pressed="item === resolvedScale"
        :data-text-scale-btn="item"
        @click.stop="setScale(item)"
      >
        {{ scaleLabels[item] }}
      </button>
    </div>

    <div v-if="hasBoardTaskLink" class="spatial-text-capsule-row">
      <SpatialTaskStatusCapsule
        :board-task-id="resolvedBoardTaskId"
        :board-task-key="resolvedBoardTaskKey"
        :linked-task="resolvedLinkedTask"
        @open-task="emit('open-board-task', $event)"
      />
    </div>

    <textarea
      v-if="editing"
      ref="textareaRef"
      v-model="draft"
      class="spatial-text-input nodrag nopan nowheel"
      :placeholder="t('space.whiteboard.textPlaceholder')"
      data-testid="text-node-input"
      @blur="commit"
      @keydown.esc.stop.prevent="commit"
    />
    <p
      v-else
      class="spatial-text-display"
      :class="{ 'is-placeholder': !resolvedText }"
      data-testid="text-node-display"
    >
      {{ resolvedText || t('space.whiteboard.textPlaceholder') }}
    </p>
  </div>
</template>

<style scoped>
.spatial-text-node {
  position: relative;
  width: 100%;
  height: 100%;
  min-width: 100px;
  min-height: 36px;
  padding: 6px 10px;
  border-radius: 8px;
  border: 1px solid transparent;
  background: transparent;
  cursor: grab;
  transition: border-color 140ms ease, background-color 140ms ease;
}

.spatial-text-node:hover {
  border-color: color-mix(in srgb, var(--border-muted) 65%, transparent);
}

.spatial-text-node.is-selected,
.spatial-text-node.is-editing {
  border-color: var(--accent-primary, #7c3aed);
  background: color-mix(in srgb, var(--surface-raised, #ffffff) 26%, transparent);
}

.spatial-text-node.is-editing {
  cursor: text;
}

.spatial-text-scale-bar {
  position: absolute;
  top: -36px;
  left: 50%;
  transform: translateX(-50%);
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 3px;
  border-radius: 999px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 6px 16px rgba(0, 0, 0, 0.12);
  z-index: 4;
}

.spatial-text-scale-btn {
  min-width: 26px;
  height: 22px;
  padding: 0 6px;
  border: 0;
  border-radius: 999px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
}

.spatial-text-scale-btn:hover {
  background: var(--surface-hover, rgba(128, 128, 128, 0.14));
  color: var(--text-primary);
}

.spatial-text-scale-btn.is-active {
  background: color-mix(in srgb, var(--accent-primary, #7c3aed) 18%, transparent);
  color: var(--accent-primary, #7c3aed);
}

.spatial-text-capsule-row {
  display: flex;
  align-items: center;
  margin-bottom: 4px;
}

.spatial-text-display,
.spatial-text-input {
  width: 100%;
  height: 100%;
  margin: 0;
  padding: 0;
  border: 0;
  background: transparent;
  color: inherit;
  font-family: inherit;
  text-align: inherit;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  outline: none;
  resize: none;
}

.spatial-text-display.is-placeholder {
  color: var(--text-muted);
  opacity: 0.72;
}

.scale-sm .spatial-text-display,
.scale-sm .spatial-text-input {
  font-size: 14px;
  line-height: 1.5;
  font-weight: 400;
}

.scale-md .spatial-text-display,
.scale-md .spatial-text-input {
  font-size: 18px;
  line-height: 1.45;
  font-weight: 500;
}

.scale-lg .spatial-text-display,
.scale-lg .spatial-text-input {
  font-size: 26px;
  line-height: 1.3;
  font-weight: 600;
  letter-spacing: -0.01em;
}

.scale-xl .spatial-text-display,
.scale-xl .spatial-text-input {
  font-size: 36px;
  line-height: 1.2;
  font-weight: 700;
  letter-spacing: -0.02em;
}

.spatial-text-handle {
  opacity: 0;
  width: 8px;
  height: 8px;
}
</style>
