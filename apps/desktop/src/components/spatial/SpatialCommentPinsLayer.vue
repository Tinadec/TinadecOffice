<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { Bot, Check, MessageCircle, RotateCcw, Send, Sparkles, Trash2, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import {
  hasAiOrTeamMention,
  type SpatialCommentPin,
  type SpatialViewport,
} from './spatialWhiteboardTypes'

const props = withDefaults(defineProps<{
  pins: SpatialCommentPin[]
  viewport: SpatialViewport
  activePinId?: string | null
  placing?: boolean
  showResolved?: boolean
}>(), {
  activePinId: null,
  placing: false,
  showResolved: true,
})

const emit = defineEmits<{
  'select-pin': [pinId: string | null]
  'create-pin': [payload: { x: number; y: number; body: string }]
  'reply-pin': [payload: { pinId: string; body: string }]
  'toggle-resolve': [payload: { pinId: string; resolved: boolean }]
  'delete-pin': [pinId: string]
  'dispatch-comment': [pin: SpatialCommentPin]
}>()

const { t } = useI18n()

const layerRef = ref<HTMLElement | null>(null)
const pendingPoint = ref<{ x: number; y: number } | null>(null)
const newPinBody = ref('')
const replyDraft = ref('')
const newPinInputRef = ref<HTMLTextAreaElement | null>(null)
const replyInputRef = ref<HTMLInputElement | HTMLInputElement[] | null>(null)

const visiblePins = computed(() =>
  props.showResolved ? props.pins : props.pins.filter((pin) => !pin.resolved),
)

const activePin = computed(() =>
  props.activePinId ? props.pins.find((p) => p.id === props.activePinId) ?? null : null,
)

watch(() => props.placing, (placing) => {
  if (!placing) {
    pendingPoint.value = null
    newPinBody.value = ''
  }
})

watch(() => props.activePinId, () => {
  replyDraft.value = ''
})

function screenPos(x: number, y: number) {
  const sx = x * props.viewport.zoom + props.viewport.x
  const sy = y * props.viewport.zoom + props.viewport.y
  return { left: `${sx.toFixed(1)}px`, top: `${sy.toFixed(1)}px` }
}

function pinInitial(pin: SpatialCommentPin): string {
  const name = pin.authorName.trim()
  return name ? name[0]!.toUpperCase() : '?'
}

function onCanvasClick(event: MouseEvent) {
  if (!props.placing) return
  const rect = layerRef.value?.getBoundingClientRect()
  const left = rect?.left ?? 0
  const top = rect?.top ?? 0
  const zoom = Math.max(0.05, props.viewport.zoom || 1)
  const wx = (event.clientX - left - props.viewport.x) / zoom
  const wy = (event.clientY - top - props.viewport.y) / zoom
  emit('select-pin', null)
  pendingPoint.value = { x: wx, y: wy }
  newPinBody.value = ''
  void nextTick(() => newPinInputRef.value?.focus())
}

function submitNewPin() {
  const body = newPinBody.value.trim()
  if (!pendingPoint.value || !body) return
  emit('create-pin', { x: pendingPoint.value.x, y: pendingPoint.value.y, body })
  pendingPoint.value = null
  newPinBody.value = ''
}

function cancelNewPin() {
  pendingPoint.value = null
  newPinBody.value = ''
}

function openPin(pinId: string) {
  pendingPoint.value = null
  emit('select-pin', props.activePinId === pinId ? null : pinId)
}

function submitReply() {
  const body = replyDraft.value.trim()
  if (!activePin.value || !body) return
  emit('reply-pin', { pinId: activePin.value.id, body })
  replyDraft.value = ''
  void nextTick(() => {
    const target = Array.isArray(replyInputRef.value) ? replyInputRef.value[0] : replyInputRef.value
    target?.focus?.()
  })
}
</script>

<template>
  <div
    ref="layerRef"
    class="spatial-comments-layer"
    :class="{ 'is-placing': placing }"
    data-testid="spatial-comments-layer"
    @click="onCanvasClick"
  >
    <div
      v-for="pin in visiblePins"
      :key="pin.id"
      class="spatial-comment-anchor"
      :style="screenPos(pin.x, pin.y)"
    >
      <button
        type="button"
        class="spatial-comment-pin"
        :class="{
          'is-active': activePinId === pin.id,
          'is-resolved': pin.resolved,
          'has-mention': hasAiOrTeamMention(pin),
        }"
        :aria-expanded="activePinId === pin.id"
        :data-comment-pin="pin.id"
        @click.stop="openPin(pin.id)"
      >
        <Bot v-if="pin.authorKind === 'ai'" :size="13" aria-hidden="true" />
        <Check v-else-if="pin.resolved" :size="13" aria-hidden="true" />
        <span v-else class="spatial-comment-initial">{{ pinInitial(pin) }}</span>
        <span v-if="pin.replies.length > 0" class="spatial-comment-badge">{{ pin.replies.length }}</span>
      </button>

      <section
        v-if="activePinId === pin.id"
        class="spatial-comment-popover"
        role="dialog"
        data-testid="comment-thread-popover"
        @click.stop
      >
        <header class="spatial-comment-header">
          <div class="spatial-comment-author">
            <strong>{{ pin.authorName }}</strong>
            <span v-if="pin.authorKind === 'ai'" class="spatial-comment-chip">
              <Bot :size="11" aria-hidden="true" />
              {{ t('space.whiteboard.aiTeammateBadge') }}
            </span>
            <span v-if="pin.resolved" class="spatial-comment-chip is-resolved">
              {{ t('space.whiteboard.resolvedLabel') }}
            </span>
          </div>
          <div class="spatial-comment-actions">
            <button
              type="button"
              class="spatial-comment-icon-btn"
              :title="pin.resolved ? t('space.whiteboard.reopenPin') : t('space.whiteboard.resolvePin')"
              :aria-label="pin.resolved ? t('space.whiteboard.reopenPin') : t('space.whiteboard.resolvePin')"
              data-testid="comment-resolve-btn"
              @click="emit('toggle-resolve', { pinId: pin.id, resolved: !pin.resolved })"
            >
              <RotateCcw v-if="pin.resolved" :size="13" />
              <Check v-else :size="13" />
            </button>
            <button
              type="button"
              class="spatial-comment-icon-btn is-danger"
              :title="t('space.whiteboard.deletePin')"
              :aria-label="t('space.whiteboard.deletePin')"
              data-testid="comment-delete-btn"
              @click="emit('delete-pin', pin.id)"
            >
              <Trash2 :size="13" />
            </button>
            <button
              type="button"
              class="spatial-comment-icon-btn"
              :title="t('common.close')"
              :aria-label="t('common.close')"
              @click="emit('select-pin', null)"
            >
              <X :size="13" />
            </button>
          </div>
        </header>

        <div class="spatial-comment-thread">
          <p class="spatial-comment-body" data-testid="comment-body">{{ pin.body }}</p>
          <div
            v-for="reply in pin.replies"
            :key="reply.id"
            class="spatial-comment-reply"
            :data-comment-reply="reply.id"
          >
            <div class="spatial-comment-reply-meta">
              <strong>{{ reply.authorName }}</strong>
              <span v-if="reply.authorKind === 'ai'" class="spatial-comment-chip">
                <Bot :size="10" aria-hidden="true" />
                {{ t('space.whiteboard.aiTeammateBadge') }}
              </span>
            </div>
            <p>{{ reply.body }}</p>
          </div>
        </div>

        <div class="spatial-comment-dispatch-row">
          <button
            type="button"
            class="spatial-comment-dispatch-btn"
            :class="{ 'is-highlighted': hasAiOrTeamMention(pin) }"
            data-testid="comment-dispatch-btn"
            @click="emit('dispatch-comment', pin)"
          >
            <Sparkles :size="12" aria-hidden="true" />
            <span>{{ t('space.whiteboard.dispatchMention') }}</span>
          </button>
        </div>

        <form class="spatial-comment-reply-form" @submit.prevent="submitReply">
          <input
            ref="replyInputRef"
            v-model="replyDraft"
            type="text"
            class="spatial-comment-input"
            :placeholder="t('space.whiteboard.replyPlaceholder')"
            data-testid="comment-reply-input"
          />
          <button
            type="submit"
            class="spatial-comment-send-btn"
            :disabled="!replyDraft.trim()"
            :aria-label="t('space.whiteboard.replyPlaceholder')"
            data-testid="comment-reply-submit"
          >
            <Send :size="13" />
          </button>
        </form>
      </section>
    </div>

    <div
      v-if="pendingPoint"
      class="spatial-comment-anchor"
      :style="screenPos(pendingPoint.x, pendingPoint.y)"
      @click.stop
    >
      <div class="spatial-comment-pin is-active">
        <MessageCircle :size="13" aria-hidden="true" />
      </div>
      <form
        class="spatial-comment-popover spatial-comment-draft"
        data-testid="comment-create-popover"
        @submit.prevent="submitNewPin"
      >
        <textarea
          ref="newPinInputRef"
          v-model="newPinBody"
          rows="2"
          class="spatial-comment-textarea"
          :placeholder="t('space.whiteboard.commentPlaceholder')"
          data-testid="comment-create-input"
          @keydown.enter.exact.prevent="submitNewPin"
          @keydown.esc.stop.prevent="cancelNewPin"
        />
        <div class="spatial-comment-draft-footer">
          <button
            type="button"
            class="spatial-comment-ghost-btn"
            @click="cancelNewPin"
          >
            {{ t('common.cancel') }}
          </button>
          <button
            type="submit"
            class="spatial-comment-submit-btn"
            :disabled="!newPinBody.trim()"
            data-testid="comment-create-submit"
          >
            <Send :size="12" aria-hidden="true" />
          </button>
        </div>
      </form>
    </div>
  </div>
</template>

<style scoped>
.spatial-comments-layer {
  position: absolute;
  inset: 0;
  pointer-events: none;
  z-index: 7;
  overflow: hidden;
}

.spatial-comments-layer.is-placing {
  pointer-events: auto;
  cursor: crosshair;
}

.spatial-comment-anchor {
  position: absolute;
  top: 0;
  left: 0;
  pointer-events: auto;
}

.spatial-comment-pin {
  position: relative;
  transform: translate(-50%, -100%);
  width: 30px;
  height: 30px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 50% 50% 50% 4px;
  border: 1px solid color-mix(in srgb, var(--accent-primary, #7c3aed) 45%, var(--border-muted));
  background: var(--surface-raised, #ffffff);
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 700;
  cursor: pointer;
  box-shadow: 0 6px 16px rgba(15, 23, 42, 0.16);
  transition: transform 120ms ease, background-color 120ms ease, border-color 120ms ease;
}

.spatial-comment-pin:hover,
.spatial-comment-pin.is-active {
  transform: translate(-50%, -100%) scale(1.08);
  border-color: var(--accent-primary, #7c3aed);
  background: color-mix(in srgb, var(--accent-primary, #7c3aed) 16%, var(--surface-raised, #ffffff));
}

.spatial-comment-pin.is-resolved {
  opacity: 0.68;
  border-color: var(--accent-success, #3fb950);
  color: var(--accent-success, #3fb950);
}

.spatial-comment-pin.has-mention {
  border-color: var(--accent-primary, #7c3aed);
}

.spatial-comment-badge {
  position: absolute;
  top: -5px;
  right: -5px;
  min-width: 16px;
  height: 16px;
  padding: 0 4px;
  border-radius: 999px;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary, #ffffff);
  font-size: 10px;
  font-weight: 700;
  line-height: 16px;
  text-align: center;
  font-variant-numeric: tabular-nums;
}

.spatial-comment-popover {
  position: absolute;
  top: -8px;
  left: 20px;
  width: 288px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  border-radius: 12px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-primary);
  box-shadow: 0 14px 36px rgba(15, 23, 42, 0.22);
  z-index: 12;
}

.spatial-comment-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
}

.spatial-comment-author {
  display: flex;
  align-items: center;
  gap: 5px;
  min-width: 0;
  font-size: 12px;
}

.spatial-comment-author strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.spatial-comment-chip {
  display: inline-flex;
  align-items: center;
  gap: 3px;
  padding: 1px 6px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 10px;
  font-weight: 600;
  white-space: nowrap;
}

.spatial-comment-chip.is-resolved {
  border-color: color-mix(in srgb, var(--accent-success, #3fb950) 40%, transparent);
  color: var(--accent-success, #3fb950);
}

.spatial-comment-actions {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  flex-shrink: 0;
}

.spatial-comment-icon-btn {
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.spatial-comment-icon-btn:hover {
  background: var(--surface-hover, rgba(128, 128, 128, 0.14));
  color: var(--text-primary);
}

.spatial-comment-icon-btn.is-danger:hover {
  color: var(--accent-danger, #f85149);
}

.spatial-comment-thread {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-height: 220px;
  overflow: auto;
}

.spatial-comment-body {
  margin: 0;
  font-size: 13px;
  line-height: 1.5;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.spatial-comment-reply {
  padding-top: 8px;
  border-top: 1px solid var(--border-muted);
}

.spatial-comment-reply-meta {
  display: flex;
  align-items: center;
  gap: 5px;
  font-size: 11px;
  color: var(--text-secondary);
  margin-bottom: 3px;
}

.spatial-comment-reply p {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.45;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.spatial-comment-dispatch-row {
  display: flex;
}

.spatial-comment-dispatch-btn {
  width: 100%;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 6px 10px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: border-color 120ms ease, background-color 120ms ease;
}

.spatial-comment-dispatch-btn.is-highlighted,
.spatial-comment-dispatch-btn:hover {
  border-color: color-mix(in srgb, var(--accent-primary, #7c3aed) 55%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary, #7c3aed) 14%, var(--surface-section));
  color: var(--accent-primary, #7c3aed);
}

.spatial-comment-reply-form {
  display: flex;
  align-items: center;
  gap: 6px;
}

.spatial-comment-input,
.spatial-comment-textarea {
  flex: 1;
  min-width: 0;
  padding: 6px 8px;
  border-radius: 8px;
  border: 1px solid var(--border-input, var(--border-muted));
  background: var(--surface-section);
  color: var(--text-primary);
  font-family: inherit;
  font-size: 12.5px;
  outline: none;
  resize: none;
}

.spatial-comment-input:focus,
.spatial-comment-textarea:focus {
  border-color: var(--accent-primary, #7c3aed);
}

.spatial-comment-send-btn,
.spatial-comment-submit-btn {
  width: 28px;
  height: 28px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid color-mix(in srgb, var(--accent-primary, #7c3aed) 45%, transparent);
  border-radius: 8px;
  background: color-mix(in srgb, var(--accent-primary, #7c3aed) 18%, transparent);
  color: var(--accent-primary, #7c3aed);
  cursor: pointer;
  flex-shrink: 0;
}

.spatial-comment-send-btn:disabled,
.spatial-comment-submit-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.spatial-comment-draft-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 6px;
}

.spatial-comment-ghost-btn {
  padding: 4px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  cursor: pointer;
}

.spatial-comment-ghost-btn:hover {
  background: var(--surface-hover, rgba(128, 128, 128, 0.14));
  color: var(--text-primary);
}
</style>
