<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { Handle, Position } from '@vue-flow/core'
import { NodeResizer } from '@vue-flow/node-resizer'
import { Bot } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { ProjectBoardTaskDto } from '@/api'
import SpatialTaskStatusCapsule from './SpatialTaskStatusCapsule.vue'
import {
  SPATIAL_STICKY_PALETTE,
  SPATIAL_STICKY_TONES,
  isStickyTone,
  type SpatialStickyProps,
  type SpatialStickyTone,
} from './spatialWhiteboardTypes'

const props = withDefaults(defineProps<{
  id: string
  text?: string
  tone?: SpatialStickyTone
  authorName?: string
  authorKind?: 'human' | 'ai'
  board_task_id?: string | null
  board_task_key?: string | null
  linkedTask?: ProjectBoardTaskDto | null
  selected?: boolean
  readonly?: boolean
  data?: Partial<SpatialStickyProps> & { linkedTask?: ProjectBoardTaskDto | null }
}>(), {
  selected: false,
  readonly: false,
  board_task_id: undefined,
  board_task_key: undefined,
  linkedTask: undefined,
})

const emit = defineEmits<{
  'update:text': [text: string]
  'update:tone': [tone: SpatialStickyTone]
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
const resolvedTone = computed<SpatialStickyTone>(() => {
  const raw = props.tone ?? props.data?.tone
  return isStickyTone(raw) ? raw : 'butter'
})
const resolvedAuthorName = computed(() => props.authorName ?? props.data?.authorName ?? '')
const resolvedAuthorKind = computed<'human' | 'ai'>(() => props.authorKind ?? props.data?.authorKind ?? 'human')
const resolvedBoardTaskId = computed(() => props.board_task_id ?? props.data?.board_task_id ?? null)
const resolvedBoardTaskKey = computed(() => props.board_task_key ?? props.data?.board_task_key ?? null)
const resolvedLinkedTask = computed(() => props.linkedTask ?? props.data?.linkedTask ?? null)
const hasBoardTaskLink = computed(() =>
  Boolean(resolvedBoardTaskId.value || resolvedBoardTaskKey.value || resolvedLinkedTask.value),
)
const palette = computed(() => SPATIAL_STICKY_PALETTE[resolvedTone.value])

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

function selectTone(tone: SpatialStickyTone) {
  if (props.readonly) return
  emit('update:tone', tone)
}
</script>

<template>
  <article
    class="spatial-sticky-node"
    :class="{ 'is-selected': selected, 'is-editing': editing }"
    :data-sticky-id="id"
    :data-sticky-tone="resolvedTone"
    :style="{
      '--sticky-bg': palette.bg,
      '--sticky-border': palette.border,
      '--sticky-text': palette.text,
      '--sticky-muted': palette.muted,
    }"
    @dblclick.stop="startEditing"
  >
    <template v-for="side in sides" :key="side">
      <Handle :id="`${side}-target`" type="target" :position="side" class="spatial-sticky-handle" />
      <Handle :id="`${side}-source`" type="source" :position="side" class="spatial-sticky-handle" />
    </template>

    <NodeResizer
      :is-visible="selected && !readonly"
      :min-width="160"
      :min-height="140"
      :max-width="720"
      :max-height="720"
      @resize-start="emit('resize-start')"
      @resize-end="({ params }) => emit('resize-end', params)"
    />

    <div
      v-if="selected && !readonly"
      class="spatial-sticky-palette nodrag nopan"
      role="group"
    >
      <button
        v-for="toneOption in SPATIAL_STICKY_TONES"
        :key="toneOption"
        type="button"
        class="spatial-sticky-dot"
        :class="{ 'is-active': toneOption === resolvedTone }"
        :style="{ backgroundColor: SPATIAL_STICKY_PALETTE[toneOption].swatch }"
        :aria-label="toneOption"
        :aria-pressed="toneOption === resolvedTone"
        :data-sticky-tone-btn="toneOption"
        @click.stop="selectTone(toneOption)"
      />
    </div>

    <div v-if="hasBoardTaskLink" class="spatial-sticky-badge-row">
      <SpatialTaskStatusCapsule
        :board-task-id="resolvedBoardTaskId"
        :board-task-key="resolvedBoardTaskKey"
        :linked-task="resolvedLinkedTask"
        @open-task="emit('open-board-task', $event)"
      />
    </div>

    <div class="spatial-sticky-body space-drag-handle">
      <textarea
        v-if="editing"
        ref="textareaRef"
        v-model="draft"
        class="spatial-sticky-textarea nodrag nopan nowheel"
        :placeholder="t('space.whiteboard.stickyPlaceholder')"
        data-testid="sticky-textarea"
        @blur="commit"
        @keydown.esc.stop.prevent="commit"
      />
      <p
        v-else
        class="spatial-sticky-text"
        :class="{ 'is-placeholder': !resolvedText }"
        data-testid="sticky-text"
      >
        {{ resolvedText || t('space.whiteboard.stickyPlaceholder') }}
      </p>
    </div>

    <footer v-if="resolvedAuthorName || resolvedAuthorKind === 'ai'" class="spatial-sticky-footer">
      <span v-if="resolvedAuthorKind === 'ai'" class="spatial-sticky-ai-pill">
        <Bot :size="11" aria-hidden="true" />
        {{ t('space.whiteboard.aiTeammateBadge') }}
      </span>
      <span v-if="resolvedAuthorName" class="spatial-sticky-author">{{ resolvedAuthorName }}</span>
    </footer>
  </article>
</template>

<style scoped>
.spatial-sticky-node {
  position: relative;
  width: 100%;
  height: 100%;
  min-width: 160px;
  min-height: 140px;
  display: flex;
  flex-direction: column;
  padding: 16px 16px 12px;
  border-radius: 12px;
  border: 1px solid var(--sticky-border);
  background: var(--sticky-bg);
  color: var(--sticky-text);
  box-shadow:
    0 10px 24px rgba(15, 23, 42, 0.1),
    0 2px 6px rgba(15, 23, 42, 0.06);
  transition: box-shadow 140ms ease, transform 140ms ease, border-color 140ms ease;
}

.spatial-sticky-node.is-selected {
  box-shadow:
    0 0 0 2px var(--accent-primary, #7c3aed),
    0 12px 28px rgba(15, 23, 42, 0.14);
}

.spatial-sticky-palette {
  position: absolute;
  top: -36px;
  left: 50%;
  transform: translateX(-50%);
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: color-mix(in srgb, var(--surface-raised, #ffffff) 92%, transparent);
  backdrop-filter: blur(12px);
  box-shadow: 0 6px 18px rgba(0, 0, 0, 0.12);
  z-index: 4;
}

.spatial-sticky-dot {
  width: 15px;
  height: 15px;
  border-radius: 999px;
  border: 1px solid rgba(0, 0, 0, 0.16);
  padding: 0;
  cursor: pointer;
  transition: transform 120ms ease;
}

.spatial-sticky-dot:hover {
  transform: scale(1.16);
}

.spatial-sticky-dot.is-active {
  box-shadow: 0 0 0 2px var(--surface-raised, #ffffff), 0 0 0 3.5px var(--accent-primary, #7c3aed);
}

.spatial-sticky-badge-row {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  margin-bottom: 6px;
}

.spatial-sticky-body {
  flex: 1;
  min-height: 0;
  display: flex;
  cursor: grab;
}

.spatial-sticky-node.is-editing .spatial-sticky-body {
  cursor: text;
}

.spatial-sticky-text {
  margin: 0;
  width: 100%;
  font-size: 14px;
  line-height: 1.55;
  font-weight: 500;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  overflow: auto;
  user-select: none;
}

.spatial-sticky-text.is-placeholder {
  color: var(--sticky-muted);
  opacity: 0.72;
}

.spatial-sticky-textarea {
  width: 100%;
  height: 100%;
  border: 0;
  padding: 0;
  margin: 0;
  resize: none;
  background: transparent;
  color: inherit;
  font-family: inherit;
  font-size: 14px;
  line-height: 1.55;
  font-weight: 500;
  outline: none;
}

.spatial-sticky-textarea::placeholder {
  color: var(--sticky-muted);
  opacity: 0.7;
}

.spatial-sticky-footer {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 8px;
  padding-top: 6px;
  border-top: 1px solid color-mix(in srgb, var(--sticky-muted) 18%, transparent);
  font-size: 11px;
  color: var(--sticky-muted);
}

.spatial-sticky-ai-pill {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 6px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--sticky-muted) 28%, transparent);
  background: color-mix(in srgb, #ffffff 36%, transparent);
  font-weight: 600;
}

.spatial-sticky-author {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.spatial-sticky-handle {
  opacity: 0;
  width: 8px;
  height: 8px;
}
</style>
