<script lang="ts">
export interface TeammateSteerAnchorRect {
  top: number
  left: number
  width: number
  height: number
  right?: number
  bottom?: number
}

export interface TeammateSteerTarget {
  runId: string
  handle: string
  displayName?: string | null
  status?: string | null
  paused?: boolean
  taskSummary?: string | null
  thought?: string | null
  role?: string | null
  anchorRect?: TeammateSteerAnchorRect | null
}
</script>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertCircle,
  CheckCircle2,
  Loader2,
  Pause,
  Play,
  Send,
  X,
  Zap,
} from '@lucide/vue'
import {
  listRunTeammates,
  normalizeTeammateHandle,
  normalizeTeammateStatusKind,
  pauseRunTeammate,
  resumeRunTeammate,
  steerRunTeammate,
  type TeammateControlStateReceiptDto,
  type TeammateNormalizedStatus,
  type TeammateSteerReceiptDto,
} from '@/lib/teammateControlApi'

const props = withDefaults(defineProps<{
  open: boolean
  runId?: string | null
  handle?: string | null
  displayName?: string | null
  status?: string | null
  paused?: boolean
  taskSummary?: string | null
  thought?: string | null
  role?: string | null
  anchorRect?: TeammateSteerAnchorRect | null
  inline?: boolean
  teleportDisabled?: boolean
  storageId?: string
  initialDraft?: string
  autoFetchStatus?: boolean
}>(), {
  runId: '',
  handle: '',
  displayName: null,
  status: null,
  paused: false,
  taskSummary: null,
  thought: null,
  role: null,
  anchorRect: null,
  inline: false,
  teleportDisabled: false,
  storageId: undefined,
  initialDraft: '',
  autoFetchStatus: true,
})

const emit = defineEmits<{
  'update:open': [value: boolean]
  close: []
  steered: [receipt: TeammateSteerReceiptDto]
  paused: [receipt: TeammateControlStateReceiptDto]
  resumed: [receipt: TeammateControlStateReceiptDto]
  'status-change': [payload: { handle: string; status: string; paused: boolean }]
  error: [message: string]
}>()

const { t, te } = useI18n()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? t(key, params) : t(key)
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}

const POPOVER_WIDTH = 320
const POPOVER_ESTIMATED_HEIGHT = 216
const VIEWPORT_MARGIN = 12

const popoverRef = ref<HTMLElement | null>(null)
const inputRef = ref<HTMLTextAreaElement | null>(null)
const draft = ref(props.initialDraft ?? '')
const urgent = ref(true)
const sending = ref(false)
const togglingPause = ref(false)
const feedback = ref<{ kind: 'success' | 'error'; text: string } | null>(null)

const syncedStatus = ref<string | null>(null)
const syncedPaused = ref<boolean | null>(null)
const syncedTaskSummary = ref<string | null>(null)
const syncedPendingCount = ref<number>(0)

let fetchAbort: AbortController | null = null
let feedbackTimer: ReturnType<typeof setTimeout> | null = null

const normalizedHandle = computed(() => normalizeTeammateHandle(props.handle ?? ''))
const resolvedRunId = computed(() => (props.runId ?? '').trim())
const resolvedDisplayName = computed(() => {
  const name = props.displayName?.trim()
  return name || normalizedHandle.value || tr('teammateControl.title', 'Teammate')
})
const showSubHandle = computed(() => {
  const handle = normalizedHandle.value
  return Boolean(handle && resolvedDisplayName.value.toLowerCase() !== handle.toLowerCase())
})

const effectivePaused = computed<boolean>(() => {
  if (syncedPaused.value !== null) return syncedPaused.value
  if (props.paused) return true
  const s = (syncedStatus.value ?? props.status ?? '').trim().toLowerCase()
  return s === 'paused' || s === 'suspended'
})

const effectiveRawStatus = computed<string>(() => {
  if (effectivePaused.value) return 'paused'
  return syncedStatus.value ?? props.status ?? 'waiting'
})

const statusKind = computed<TeammateNormalizedStatus>(() =>
  normalizeTeammateStatusKind(effectiveRawStatus.value, effectivePaused.value),
)

const statusLabel = computed(() => {
  const map: Record<TeammateNormalizedStatus, string> = {
    running: tr('teammateControl.status.running', 'Running'),
    paused: tr('teammateControl.status.paused', 'Paused'),
    idle: tr('teammateControl.status.idle', 'Sleeping'),
    awaiting_approval: tr('teammateControl.status.awaiting_approval', 'Awaiting approval'),
    completed: tr('teammateControl.status.completed', 'Completed'),
    failed: tr('teammateControl.status.failed', 'Failed'),
    queued: tr('teammateControl.status.queued', 'Queued'),
  }
  return map[statusKind.value]
})

const resolvedTaskSummary = computed(() => {
  const candidates = [syncedTaskSummary.value, props.taskSummary, props.thought]
  for (const item of candidates) {
    const clean = item?.replace(/\s+/g, ' ').trim()
    if (clean) return clean
  }
  return ''
})

const canControl = computed(() => Boolean(resolvedRunId.value && normalizedHandle.value))
const canSend = computed(() => canControl.value && draft.value.trim().length > 0 && !sending.value)

const floatingStyle = computed<Record<string, string>>(() => {
  if (props.inline) return {} as Record<string, string>
  const vw = typeof window !== 'undefined' && window.innerWidth > 0 ? window.innerWidth : 1280
  const vh = typeof window !== 'undefined' && window.innerHeight > 0 ? window.innerHeight : 800
  const width = Math.min(POPOVER_WIDTH, Math.max(240, vw - VIEWPORT_MARGIN * 2))

  const rect = props.anchorRect
  if (!rect) {
    return {
      left: `${Math.round((vw - width) / 2)}px`,
      top: `${Math.max(VIEWPORT_MARGIN, Math.round(vh * 0.24))}px`,
      width: `${width}px`,
    }
  }

  const anchorBottom = rect.bottom ?? rect.top + rect.height
  const anchorCenterX = rect.left + rect.width / 2
  let left = Math.round(anchorCenterX - width / 2)
  left = Math.max(VIEWPORT_MARGIN, Math.min(vw - width - VIEWPORT_MARGIN, left))

  let top = Math.round(anchorBottom + 8)
  if (top + POPOVER_ESTIMATED_HEIGHT > vh - VIEWPORT_MARGIN && rect.top - POPOVER_ESTIMATED_HEIGHT - 8 >= VIEWPORT_MARGIN) {
    top = Math.round(rect.top - POPOVER_ESTIMATED_HEIGHT - 8)
  } else {
    top = Math.max(VIEWPORT_MARGIN, Math.min(vh - POPOVER_ESTIMATED_HEIGHT - VIEWPORT_MARGIN, top))
  }

  return {
    left: `${left}px`,
    top: `${top}px`,
    width: `${width}px`,
  }
})

function clearFeedbackTimer() {
  if (feedbackTimer !== null) {
    clearTimeout(feedbackTimer)
    feedbackTimer = null
  }
}

function setFeedback(kind: 'success' | 'error', text: string, autoClearMs = 3600) {
  clearFeedbackTimer()
  feedback.value = { kind, text }
  if (kind === 'success' && autoClearMs > 0) {
    feedbackTimer = setTimeout(() => {
      feedback.value = null
      feedbackTimer = null
    }, autoClearMs)
  }
}

function resizeTextarea() {
  const el = inputRef.value
  if (!el) return
  el.style.height = 'auto'
  const nextHeight = Math.min(96, Math.max(32, el.scrollHeight || 32))
  el.style.height = `${nextHeight}px`
}

function closePopover() {
  if (!props.open) return
  emit('update:open', false)
  emit('close')
}

async function hydrateTeammateStatus() {
  fetchAbort?.abort()
  fetchAbort = null
  if (!props.autoFetchStatus || !props.open || !canControl.value) return
  const runId = resolvedRunId.value
  const handle = normalizedHandle.value.toLowerCase()
  const controller = new AbortController()
  fetchAbort = controller
  try {
    const teammates = await listRunTeammates(runId, {
      signal: controller.signal,
      storageId: props.storageId,
    })
    if (controller.signal.aborted) return
    const hit = teammates.find((item) => normalizeTeammateHandle(item.handle).toLowerCase() === handle)
    if (!hit) return
    syncedStatus.value = hit.status
    syncedPaused.value = hit.paused
    syncedPendingCount.value = hit.pending_steer_count
    const summary = hit.current_task_title || hit.latest_summary || null
    if (summary) syncedTaskSummary.value = summary
  } catch {
    // Silent fallback: initial props remain authoritative when live fetch is unavailable.
  } finally {
    if (fetchAbort === controller) fetchAbort = null
  }
}

async function handleSendSteer() {
  if (!canSend.value) return
  const message = draft.value.trim()
  if (!message) return
  sending.value = true
  feedback.value = null
  try {
    const receipt = await steerRunTeammate(
      resolvedRunId.value,
      normalizedHandle.value,
      { message, urgent: urgent.value },
      { storageId: props.storageId },
    )
    draft.value = ''
    syncedStatus.value = receipt.status
    syncedPaused.value = receipt.paused
    syncedPendingCount.value = receipt.pending_steer_count
    void nextTick(resizeTextarea)

    const text = receipt.woken || receipt.delivery_mode === 'woken'
      ? tr('teammateControl.deliveredWoken', 'Delivered & woke sleeping teammate')
      : receipt.delivery_mode === 'queued_while_paused'
        ? tr('teammateControl.deliveredQueuedPaused', 'Queued — will apply upon resume')
        : tr('teammateControl.delivered', 'Delivered — will inject before next turn')
    setFeedback('success', text)
    emit('steered', receipt)
    emit('status-change', {
      handle: receipt.handle,
      status: receipt.status,
      paused: receipt.paused,
    })
  } catch (err) {
    const msg = err instanceof Error && err.message
      ? err.message
      : tr('teammateControl.actionFailed', 'Control action failed')
    setFeedback('error', msg, 0)
    emit('error', msg)
  } finally {
    sending.value = false
  }
}

async function handleTogglePause() {
  if (!canControl.value || togglingPause.value) return
  const willPause = !effectivePaused.value
  togglingPause.value = true
  feedback.value = null
  try {
    const receipt = willPause
      ? await pauseRunTeammate(resolvedRunId.value, normalizedHandle.value, { storageId: props.storageId })
      : await resumeRunTeammate(resolvedRunId.value, normalizedHandle.value, { storageId: props.storageId })
    syncedStatus.value = receipt.status
    syncedPaused.value = receipt.paused
    setFeedback(
      'success',
      receipt.paused
        ? tr('teammateControl.pausedFeedback', 'Teammate paused independently')
        : tr('teammateControl.resumedFeedback', 'Teammate resumed'),
    )
    if (receipt.paused) emit('paused', receipt)
    else emit('resumed', receipt)
    emit('status-change', {
      handle: receipt.handle,
      status: receipt.status,
      paused: receipt.paused,
    })
  } catch (err) {
    const msg = err instanceof Error && err.message
      ? err.message
      : tr('teammateControl.actionFailed', 'Control action failed')
    setFeedback('error', msg, 0)
    emit('error', msg)
  } finally {
    togglingPause.value = false
  }
}

function onInputKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    closePopover()
    return
  }
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault()
    void handleSendSteer()
  }
}

function onDocumentPointerDown(event: PointerEvent) {
  if (!props.open || props.inline) return
  const target = event.target as Node | null
  if (!target) return
  if (popoverRef.value?.contains(target)) return
  closePopover()
}

function onDocumentKeydown(event: KeyboardEvent) {
  if (!props.open || props.inline) return
  if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    closePopover()
  }
}

watch(
  () => [props.open, props.runId, props.handle] as const,
  ([isOpen]) => {
    syncedStatus.value = null
    syncedPaused.value = null
    syncedTaskSummary.value = null
    syncedPendingCount.value = 0
    feedback.value = null
    clearFeedbackTimer()
    if (isOpen) {
      if (props.initialDraft) draft.value = props.initialDraft
      void nextTick(() => {
        resizeTextarea()
        inputRef.value?.focus()
      })
      void hydrateTeammateStatus()
      if (typeof document !== 'undefined' && !props.inline) {
        document.addEventListener('pointerdown', onDocumentPointerDown, true)
        document.addEventListener('keydown', onDocumentKeydown, true)
      }
    } else {
      fetchAbort?.abort()
      fetchAbort = null
      if (typeof document !== 'undefined') {
        document.removeEventListener('pointerdown', onDocumentPointerDown, true)
        document.removeEventListener('keydown', onDocumentKeydown, true)
      }
    }
  },
  { immediate: true },
)

watch(
  () => props.initialDraft,
  (next) => {
    if (props.open && typeof next === 'string' && next.length > 0) {
      draft.value = next
      void nextTick(resizeTextarea)
    }
  },
)

onBeforeUnmount(() => {
  fetchAbort?.abort()
  clearFeedbackTimer()
  if (typeof document !== 'undefined') {
    document.removeEventListener('pointerdown', onDocumentPointerDown, true)
    document.removeEventListener('keydown', onDocumentKeydown, true)
  }
})
</script>

<template>
  <Teleport to="body" :disabled="inline || teleportDisabled">
    <div
      v-if="open"
      ref="popoverRef"
      class="teammate-steer-popover"
      :class="{ 'is-inline': inline, 'is-paused': effectivePaused }"
      :style="floatingStyle"
      role="dialog"
      :aria-label="tr('teammateControl.title', 'Direct teammate control')"
      data-testid="teammate-steer-popover"
      :data-teammate-handle="normalizedHandle"
      :data-teammate-status="statusKind"
      @click.stop
      @pointerdown.stop
    >
        <!-- Top row: Teammate name + status badge + close -->
        <header class="teammate-steer__header">
          <div class="teammate-steer__identity">
            <span
              class="teammate-steer__dot"
              :class="`is-${statusKind}`"
              aria-hidden="true"
            />
            <strong class="teammate-steer__name" data-testid="teammate-steer-name">
              {{ resolvedDisplayName }}
            </strong>
            <span v-if="showSubHandle" class="teammate-steer__handle">
              @{{ normalizedHandle }}
            </span>
          </div>

          <div class="teammate-steer__header-right">
            <span
              class="teammate-steer__status-badge"
              :class="`is-${statusKind}`"
              data-testid="teammate-steer-status"
            >
              {{ statusLabel }}
            </span>
            <button
              type="button"
              class="teammate-steer__close ui-pressable"
              :aria-label="tr('common.close', 'Close')"
              data-testid="teammate-steer-close"
              @click="closePopover"
            >
              <X :size="13" aria-hidden="true" />
            </button>
          </div>
        </header>

        <!-- Current claimed task / thought summary -->
        <p
          v-if="resolvedTaskSummary"
          class="teammate-steer__task"
          :title="resolvedTaskSummary"
          data-testid="teammate-steer-task"
        >
          {{ resolvedTaskSummary }}
        </p>

        <!-- Adaptive direct steer input -->
        <div class="teammate-steer__input-wrap">
          <textarea
            ref="inputRef"
            v-model="draft"
            rows="1"
            class="teammate-steer__textarea"
            :disabled="!canControl || sending"
            :placeholder="canControl
              ? tr('teammateControl.placeholder', 'Send direct guidance to @{handle}… (Enter to send)', { handle: normalizedHandle })
              : tr('teammateControl.noActiveRun', 'No active run bound to this teammate')"
            :aria-label="tr('teammateControl.placeholder', 'Send direct guidance to @{handle}', { handle: normalizedHandle })"
            data-testid="teammate-steer-input"
            @input="resizeTextarea"
            @keydown="onInputKeydown"
          />
        </div>

        <!-- Horizontal control bar (strictly inline-flex items-center, 13px icons) -->
        <div class="teammate-steer__actions">
          <div class="teammate-steer__actions-left">
            <button
              type="button"
              class="teammate-steer__secondary-btn ui-pressable"
              :class="{ 'is-resume': effectivePaused }"
              :disabled="!canControl || togglingPause"
              data-testid="teammate-steer-pause-btn"
              @click="handleTogglePause"
            >
              <Loader2 v-if="togglingPause" :size="13" class="teammate-steer__spin" aria-hidden="true" />
              <Play v-else-if="effectivePaused" :size="13" aria-hidden="true" />
              <Pause v-else :size="13" aria-hidden="true" />
              <span>
                {{
                  togglingPause
                    ? (effectivePaused
                      ? tr('teammateControl.resuming', 'Resuming…')
                      : tr('teammateControl.pausing', 'Pausing…'))
                    : (effectivePaused
                      ? tr('teammateControl.resume', 'Resume')
                      : tr('teammateControl.pause', 'Pause'))
                }}
              </span>
            </button>

            <button
              type="button"
              class="teammate-steer__urgent-btn ui-pressable"
              :class="{ 'is-active': urgent }"
              :aria-pressed="urgent"
              :title="tr('teammateControl.urgentHint', 'Inject before the next step with high priority')"
              :disabled="!canControl || sending"
              data-testid="teammate-steer-urgent-btn"
              @click="urgent = !urgent"
            >
              <Zap :size="13" aria-hidden="true" />
              <span>{{ tr('teammateControl.urgent', 'Urgent') }}</span>
            </button>
          </div>

          <button
            type="button"
            class="teammate-steer__send-btn ui-pressable"
            :disabled="!canSend"
            data-testid="teammate-steer-send-btn"
            @click="handleSendSteer"
          >
            <Loader2 v-if="sending" :size="13" class="teammate-steer__spin" aria-hidden="true" />
            <Send v-else :size="13" aria-hidden="true" />
            <span>
              {{ sending ? tr('teammateControl.sending', 'Sending…') : tr('teammateControl.send', 'Send') }}
            </span>
          </button>
        </div>

        <!-- Delivery / state feedback -->
        <div
          v-if="feedback"
          class="teammate-steer__feedback"
          :class="`is-${feedback.kind}`"
          role="status"
          data-testid="teammate-steer-feedback"
        >
          <CheckCircle2 v-if="feedback.kind === 'success'" :size="13" aria-hidden="true" />
          <AlertCircle v-else :size="13" aria-hidden="true" />
          <span>{{ feedback.text }}</span>
        </div>

        <div v-if="syncedPendingCount > 0 && !feedback" class="teammate-steer__meta" data-testid="teammate-steer-pending">
          <span>{{ tr('teammateControl.pendingCount', '{count} queued', { count: syncedPendingCount }) }}</span>
          <span class="teammate-steer__hint">{{ tr('teammateControl.enterHint', 'Enter send · Shift+Enter newline') }}</span>
        </div>
        <div v-else-if="!feedback" class="teammate-steer__meta">
          <span class="teammate-steer__hint">{{ tr('teammateControl.enterHint', 'Enter send · Shift+Enter newline') }}</span>
        </div>
      </div>
  </Teleport>
</template>

<style scoped>
.teammate-steer-popover {
  position: fixed;
  z-index: 90;
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  border-radius: var(--radius-island, 12px);
  border: 1px solid var(--border-default);
  background: color-mix(in srgb, var(--surface-raised, var(--bg-overlay)) 94%, transparent);
  backdrop-filter: blur(16px);
  box-shadow: var(--shadow-elevated, 0 8px 28px rgba(0, 0, 0, 0.28));
  color: var(--text-primary);
  box-sizing: border-box;
  animation: teammate-popover-enter var(--duration-pop, 180ms) var(--ease-snappy, ease) both;
}

@keyframes teammate-popover-enter {
  from {
    opacity: 0;
  }
  to {
    opacity: 1;
  }
}

.teammate-steer-popover.is-inline {
  position: relative;
  z-index: 2;
  width: 100%;
  box-shadow: var(--shadow-card-subtle);
  backdrop-filter: none;
  background: var(--surface-raised, var(--bg-overlay));
}

.teammate-steer__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-width: 0;
}

.teammate-steer__identity {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  flex: 1;
}

.teammate-steer__dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
  background: var(--text-muted);
}

.teammate-steer__dot.is-running {
  background: var(--accent-primary);
  animation: teammate-steer-pulse 1.6s ease-in-out infinite;
}

.teammate-steer__dot.is-paused,
.teammate-steer__dot.is-awaiting_approval {
  background: var(--accent-warning);
}

.teammate-steer__dot.is-completed {
  background: var(--accent-success);
}

.teammate-steer__dot.is-failed {
  background: var(--accent-danger);
}

@keyframes teammate-steer-pulse {
  0%, 100% {
    opacity: 1;
  }
  50% {
    opacity: 0.42;
  }
}

.teammate-steer__name {
  font-size: 12.5px;
  font-weight: 600;
  line-height: 1.3;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.teammate-steer__handle {
  font-size: 11px;
  font-family: var(--font-mono);
  color: var(--text-muted);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex-shrink: 1;
}

.teammate-steer__header-right {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-shrink: 0;
}

.teammate-steer__status-badge {
  display: inline-flex;
  align-items: center;
  height: 20px;
  padding: 0 7px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section, var(--bg-tertiary));
  color: var(--text-secondary);
  font-size: 10.5px;
  font-weight: 600;
  line-height: 1;
  white-space: nowrap;
}

.teammate-steer__status-badge.is-running {
  color: var(--accent-primary);
  border-color: color-mix(in srgb, var(--accent-primary) 28%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary) 10%, transparent);
}

.teammate-steer__status-badge.is-paused,
.teammate-steer__status-badge.is-awaiting_approval {
  color: var(--accent-warning);
  border-color: color-mix(in srgb, var(--accent-warning) 32%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-warning) 12%, transparent);
}

.teammate-steer__status-badge.is-completed {
  color: var(--accent-success);
  border-color: color-mix(in srgb, var(--accent-success) 30%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-success) 12%, transparent);
}

.teammate-steer__status-badge.is-failed {
  color: var(--accent-danger);
  border-color: color-mix(in srgb, var(--accent-danger) 30%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-danger) 12%, transparent);
}

.teammate-steer__close {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border: 0;
  border-radius: var(--radius-control, 6px);
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.teammate-steer__close:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.teammate-steer__task {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.4;
  color: var(--text-secondary);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.teammate-steer__input-wrap {
  display: flex;
  align-items: stretch;
  border-radius: var(--radius-control-md, 8px);
  border: 1px solid var(--border-input, var(--border-default));
  background: var(--bg-input, var(--surface-section));
  transition:
    border-color var(--duration-tap, 120ms) var(--ease-snappy, ease),
    box-shadow var(--duration-tap, 120ms) var(--ease-snappy, ease);
}

.teammate-steer__input-wrap:focus-within {
  border-color: var(--border-input-focus, var(--accent-primary));
  box-shadow: var(--focus-ring, 0 0 0 2px color-mix(in srgb, var(--accent-primary) 20%, transparent));
}

.teammate-steer__textarea {
  width: 100%;
  min-height: 32px;
  max-height: 96px;
  padding: 7px 9px;
  border: 0;
  background: transparent;
  color: var(--text-primary);
  font-family: inherit;
  font-size: 12px;
  line-height: 1.45;
  resize: none;
  outline: none;
  box-sizing: border-box;
}

.teammate-steer__textarea::placeholder {
  color: var(--text-muted);
}

.teammate-steer__textarea:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

.teammate-steer__actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.teammate-steer__actions-left {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.teammate-steer__secondary-btn,
.teammate-steer__urgent-btn,
.teammate-steer__send-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  height: 28px;
  padding: 0 10px;
  border-radius: var(--radius-control, 6px);
  font-size: 12px;
  font-weight: 500;
  line-height: 1;
  white-space: nowrap;
  cursor: pointer;
}

.teammate-steer__secondary-btn {
  border: 1px solid var(--border-default);
  background: var(--bg-secondary-button, var(--surface-section));
  color: var(--text-primary);
}

.teammate-steer__secondary-btn:hover:not(:disabled) {
  background: var(--bg-secondary-button-hover, var(--bg-hover));
}

.teammate-steer__secondary-btn.is-resume {
  border-color: color-mix(in srgb, var(--accent-warning) 42%, var(--border-default));
  color: var(--accent-warning);
}

.teammate-steer__urgent-btn {
  padding: 0 8px;
  gap: 4px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-muted);
}

.teammate-steer__urgent-btn:hover:not(:disabled) {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.teammate-steer__urgent-btn.is-active {
  border-color: color-mix(in srgb, var(--accent-primary) 34%, var(--border-default));
  background: color-mix(in srgb, var(--accent-primary) 10%, transparent);
  color: var(--text-primary);
}

.teammate-steer__send-btn {
  border: 1px solid transparent;
  background: var(--bg-primary-button, var(--accent-primary));
  color: var(--text-on-primary);
  font-weight: 600;
}

.teammate-steer__send-btn:hover:not(:disabled) {
  background: var(--bg-primary-button-hover, var(--accent-primary));
}

.teammate-steer__secondary-btn:disabled,
.teammate-steer__urgent-btn:disabled,
.teammate-steer__send-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.teammate-steer__feedback {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 5px 8px;
  border-radius: var(--radius-control, 6px);
  font-size: 11.5px;
  line-height: 1.35;
}

.teammate-steer__feedback.is-success {
  background: color-mix(in srgb, var(--accent-success) 12%, transparent);
  color: var(--accent-success);
}

.teammate-steer__feedback.is-error {
  background: color-mix(in srgb, var(--accent-danger) 12%, transparent);
  color: var(--accent-danger);
}

.teammate-steer__meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-size: 10.5px;
  color: var(--text-muted);
}

.teammate-steer__hint {
  margin-left: auto;
}

.teammate-steer__spin {
  animation: teammate-steer-spin 0.85s linear infinite;
}

@keyframes teammate-steer-spin {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}

@media (prefers-reduced-motion: reduce) {
  .teammate-steer-popover,
  .teammate-steer__dot.is-running,
  .teammate-steer__spin {
    animation: none;
  }
}
</style>
