<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  AlertCircle,
  AlertTriangle,
  CheckCircle2,
  Info,
  LoaderCircle,
  Pin,
  RotateCw,
  Trash2,
  TriangleAlert,
  X,
} from '@lucide/vue'
import {
  isUserDismissible,
  resolveConfirmation,
  useNotifications,
  type NotificationLevel,
} from '@/composables/useNotifications'

const { t, locale } = useI18n()
const {
  currentConfirmation,
  detailItem,
  closeDetail,
  dismiss,
  runAction,
  actionStates,
} = useNotifications()

/* ── dialog / focus / confirm-mode plumbing (byte-equivalent to original) ── */
const dialog = ref<HTMLDialogElement | null>(null)
const cancelButton = ref<HTMLButtonElement | null>(null)
const resolving = ref(false)
let returnFocus: HTMLElement | null = null
let displayedConfirmId: string | null = null
let displayedDetailId: string | null = null
let unlockTimer: ReturnType<typeof setTimeout> | null = null

const mode = computed<'confirm' | 'detail' | null>(() => {
  if (currentConfirmation.value) return 'confirm'
  if (detailItem.value) return 'detail'
  return null
})

const levelIcons: Record<NotificationLevel, typeof Info> = {
  info: Info,
  success: CheckCircle2,
  warning: TriangleAlert,
  error: AlertCircle,
}

watch(
  [currentConfirmation, detailItem],
  async () => {
    await nextTick()
    const element = dialog.value
    if (!element) return

    if (mode.value === 'confirm' && currentConfirmation.value) {
      const request = currentConfirmation.value
      if (element.open && displayedConfirmId && displayedConfirmId !== request.id) {
        element.close()
      }
      displayedConfirmId = request.id
      displayedDetailId = null
      if (!element.open) {
        if (!returnFocus) {
          returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
        }
        element.showModal()
      }
      cancelButton.value?.focus()
      return
    }

    if (mode.value === 'detail' && detailItem.value) {
      const item = detailItem.value
      displayedDetailId = item.id
      displayedConfirmId = null
      if (!element.open) {
        if (!returnFocus) {
          returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
        }
        element.showModal()
      }
      cancelButton.value?.focus()
      return
    }

    if (element.open) {
      element.close()
      returnFocus?.focus()
      returnFocus = null
      displayedConfirmId = null
      displayedDetailId = null
    }
  },
  { immediate: true },
)

function finishConfirm(value: boolean): void {
  if (resolving.value || !displayedConfirmId) return
  resolving.value = true
  resolveConfirmation(displayedConfirmId, value)
  if (unlockTimer) clearTimeout(unlockTimer)
  unlockTimer = setTimeout(() => {
    resolving.value = false
    cancelButton.value?.focus()
  }, 250)
}

/* ── detail mode helpers ── */

function closeDetailDialog(): void {
  closeDetail()
}

function dismissNotification(): void {
  const item = detailItem.value
  if (!item) return
  dismiss(item.id)
}

async function runDetailAction(): Promise<void> {
  const item = detailItem.value
  if (!item?.action) return
  await runAction(item.id)
}

function onCancel(event: Event): void {
  event.preventDefault()
  if (mode.value === 'confirm') finishConfirm(false)
  else closeDetailDialog()
}

function onBackdrop(event: MouseEvent): void {
  const element = dialog.value
  if (event.target !== element || !element) return
  const rect = element.getBoundingClientRect()
  if (
    event.clientX < rect.left ||
    event.clientX > rect.right ||
    event.clientY < rect.top ||
    event.clientY > rect.bottom
  ) {
    if (mode.value === 'confirm') finishConfirm(false)
    else closeDetailDialog()
  }
}

/* relative time via Intl.RelativeTimeFormat (no date library, no new keys) */
function formatRelativeTime(timestamp: number): string {
  const seconds = Math.floor((Date.now() - timestamp) / 1000)
  const rtf = new Intl.RelativeTimeFormat(locale.value, { numeric: 'auto' })
  if (seconds < 60) return rtf.format(-seconds, 'second')
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return rtf.format(-minutes, 'minute')
  const hours = Math.floor(minutes / 60)
  return rtf.format(-hours, 'hour')
}

/* kind-based subtitle label */
const kindLabel = computed(() => {
  const item = detailItem.value
  if (!item) return ''
  if (item.kind === 'status') return t('app.systemStatus')
  if (item.kind === 'task') {
    if (item.taskState === 'settled') {
      return item.level === 'success' ? t('app.taskSucceeded') : t('app.taskFailed')
    }
    return t('app.taskPending')
  }
  return t('app.notification')
})

/* title fallback (level-based) */
const fallbackTitle = computed(() => {
  const item = detailItem.value
  if (!item) return ''
  return item.level === 'error' ? t('app.operationFailed')
    : item.level === 'success' ? t('app.operationCompleted')
    : item.level === 'warning' ? t('app.warning')
    : t('app.information')
})

/* task pending determinate progress percentage */
const progressPercent = computed(() => {
  const item = detailItem.value
  if (!item || item.kind !== 'task' || item.taskState !== 'pending' || item.progress == null) return 0
  return Math.round(item.progress * 100)
})

onBeforeUnmount(() => {
  if (unlockTimer) clearTimeout(unlockTimer)
})
</script>

<template>
  <Teleport to="body">
    <dialog
      ref="dialog"
      class="detail-dialog no-drag"
      :class="{
        'detail-dialog--destructive': currentConfirmation?.destructive,
        [`detail-dialog--${detailItem?.level}`]: mode === 'detail' && detailItem,
      }"
      :aria-labelledby="mode === 'confirm' && currentConfirmation
        ? `${currentConfirmation.id}-title`
        : detailItem
          ? `${detailItem.id}-title`
          : undefined"
      :aria-describedby="mode === 'confirm' && currentConfirmation
        ? `${currentConfirmation.id}-description`
        : detailItem
          ? `${detailItem.id}-description`
          : undefined"
      @cancel="onCancel"
      @click="onBackdrop"
    >
      <!-- ═══ Confirmation mode (preserved exactly) ═══ -->
      <template v-if="mode === 'confirm' && currentConfirmation">
        <div class="detail-dialog__heading">
          <span class="detail-dialog__icon" aria-hidden="true">
            <AlertTriangle v-if="currentConfirmation.destructive" :size="20" />
            <Info v-else :size="20" />
          </span>
          <h2 :id="`${currentConfirmation.id}-title`">
            {{ currentConfirmation.title || t('app.confirm') }}
          </h2>
        </div>
        <p class="detail-dialog__summary" :id="`${currentConfirmation.id}-description`">
          {{ currentConfirmation.message }}
        </p>
        <section v-if="currentConfirmation.details" class="detail-dialog__details">
          <strong>{{ t('app.details') }}</strong>
          <p>{{ currentConfirmation.details }}</p>
        </section>
        <div class="detail-dialog__actions">
          <button
            ref="cancelButton"
            type="button"
            class="detail-dialog__secondary"
            :disabled="resolving"
            @click="finishConfirm(false)"
          >
            {{ currentConfirmation.cancelLabel || t('app.cancel') }}
          </button>
          <button
            type="button"
            class="detail-dialog__primary"
            :class="{ 'detail-dialog__primary--destructive': currentConfirmation.destructive }"
            :disabled="resolving"
            @click="finishConfirm(true)"
          >
            {{ currentConfirmation.confirmLabel || t('app.confirm') }}
          </button>
        </div>
      </template>

      <!-- ═══ Notification detail mode ═══ -->
      <template v-else-if="mode === 'detail' && detailItem">
        <!-- heading: icon + title group + close -->
        <div class="detail-dialog__heading">
          <span class="detail-dialog__icon detail-dialog__level-icon" aria-hidden="true">
            <component :is="levelIcons[detailItem.level]" :size="20" />
          </span>
          <div class="detail-dialog__title-group">
            <h2 :id="`${detailItem.id}-title`">
              {{ detailItem.title || fallbackTitle }}
            </h2>
            <span class="detail-dialog__kind">{{ kindLabel }}</span>
          </div>
          <button
            ref="cancelButton"
            type="button"
            class="detail-dialog__close"
            :aria-label="t('app.close')"
            :title="t('app.close')"
            @click="closeDetailDialog"
          >
            <X :size="16" aria-hidden="true" />
          </button>
        </div>

        <!-- metadata row: source, count, time, remote -->
        <div
          v-if="detailItem.source || (detailItem.count > 1) || detailItem.remote"
          class="detail-dialog__meta"
        >
          <span v-if="detailItem.source" class="detail-dialog__chip">{{ detailItem.source }}</span>
          <span v-if="detailItem.count > 1" class="detail-dialog__chip">{{ t('app.repeatedCount', { count: detailItem.count }) }}</span>
          <span v-if="detailItem.remote" class="detail-dialog__chip detail-dialog__chip--remote">{{ t('app.fromOtherWindow') }}</span>
          <span class="detail-dialog__time">{{ formatRelativeTime(detailItem.createdAt) }}</span>
        </div>
        <!-- show time even when no other metadata -->
        <div v-else class="detail-dialog__meta">
          <span class="detail-dialog__time">{{ formatRelativeTime(detailItem.createdAt) }}</span>
        </div>

        <!-- task progress state -->
        <div
          v-if="detailItem.kind === 'task' && detailItem.taskState === 'pending'"
          class="detail-dialog__progress"
        >
          <template v-if="detailItem.progress != null">
            <div
              class="detail-dialog__progress-bar"
              role="progressbar"
              :aria-valuenow="progressPercent"
              aria-valuemin="0"
              aria-valuemax="100"
            >
              <div
                class="detail-dialog__progress-fill"
                :style="{ width: `${progressPercent}%` }"
              />
            </div>
            <span class="detail-dialog__progress-text">{{ progressPercent }}%</span>
          </template>
          <template v-else>
            <LoaderCircle :size="14" class="detail-dialog__spinner" aria-hidden="true" />
            <span class="detail-dialog__progress-text">{{ t('app.taskPending') }}</span>
          </template>
        </div>

        <p class="detail-dialog__summary" :id="`${detailItem.id}-description`">
          {{ detailItem.message }}
        </p>

        <section v-if="detailItem.details" class="detail-dialog__details">
          <strong>{{ t('app.details') }}</strong>
          <p>{{ detailItem.details }}</p>
        </section>

        <div
          v-if="actionStates[detailItem.id]?.error"
          class="detail-dialog__error"
          role="alert"
        >
          <AlertCircle :size="16" aria-hidden="true" />
          <div>
            <strong>{{ t('app.retryFailed') }}</strong>
            <p>{{ actionStates[detailItem.id]?.error }}</p>
          </div>
        </div>

        <div class="detail-dialog__actions">
          <!-- source-owned items (status, pending task) are never user-closable:
               show the lifecycle hint instead of the dismiss control -->
          <span
            v-if="!detailItem.remote && !isUserDismissible(detailItem)"
            class="detail-dialog__lifecycle-hint"
          >
            <Pin :size="12" aria-hidden="true" />
            {{ detailItem.kind === 'status' ? t('app.statusAutoClear') : t('app.taskRunningHint') }}
          </span>
          <button
            v-else-if="!detailItem.remote"
            type="button"
            class="detail-dialog__dismiss"
            @click="dismissNotification"
          >
            <Trash2 :size="14" aria-hidden="true" />
            {{ t('app.dismiss') }}
          </button>
          <!-- action hidden for remote -->
          <button
            v-if="detailItem.action && !detailItem.remote"
            type="button"
            class="detail-dialog__primary"
            :disabled="actionStates[detailItem.id]?.running"
            @click="runDetailAction"
          >
            <LoaderCircle
              v-if="actionStates[detailItem.id]?.running"
              :size="14"
              class="detail-dialog__spinner"
              aria-hidden="true"
            />
            <RotateCw v-else :size="14" aria-hidden="true" />
            {{ detailItem.action.label }}
          </button>
        </div>
      </template>
    </dialog>
  </Teleport>
</template>

<style scoped>
/* ── glass dialog ── */
.detail-dialog {
  width: min(calc(100vw - 32px), 460px);
  margin: auto;
  padding: 20px;
  border: 1px solid color-mix(in srgb, var(--dialog-level, var(--accent-primary, #2ec4b6)) 24%, var(--border-default, #1a1f29));
  border-radius: 8px;
  background: color-mix(in srgb, var(--bg-primary, #0a0e14) 92%, transparent);
  color: var(--text-primary, #c9d1d9);
  box-shadow: 0 24px 80px rgb(0 0 0 / 52%);
  backdrop-filter: blur(22px) saturate(120%);
  -webkit-backdrop-filter: blur(22px) saturate(120%);
  -webkit-app-region: no-drag;
}

.detail-dialog::backdrop {
  background: rgb(3 6 10 / 52%);
  backdrop-filter: blur(10px) saturate(80%);
  -webkit-backdrop-filter: blur(10px) saturate(80%);
}

/* ── level theming ── */
.detail-dialog--destructive,
.detail-dialog--error { --dialog-level: var(--accent-danger, #f85149); }
.detail-dialog--success { --dialog-level: var(--accent-success, #2ec4b6); }
.detail-dialog--warning { --dialog-level: var(--accent-warning, #d29922); }
.detail-dialog--info { --dialog-level: var(--accent-primary, #2ec4b6); }

/* ── heading ── */
.detail-dialog__heading {
  display: flex;
  align-items: center;
  gap: 9px;
}

.detail-dialog__title-group {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.detail-dialog__icon {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  flex: none;
  border-radius: 8px;
  background: color-mix(in srgb, var(--dialog-level, var(--accent-primary, #2ec4b6)) 13%, transparent);
  color: var(--dialog-level, var(--accent-primary, #2ec4b6));
}

.detail-dialog h2 {
  margin: 0;
  font-size: 15px;
  line-height: 1.3;
  overflow-wrap: anywhere;
}

.detail-dialog__kind {
  font-size: 11px;
  line-height: 1.3;
  color: var(--text-secondary, #7d8590);
}

.detail-dialog .detail-dialog__close {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary, #7d8590);
  cursor: pointer;
  padding: 0;
}

.detail-dialog .detail-dialog__close:hover {
  background: var(--bg-hover, #161b22);
  color: var(--text-primary, #c9d1d9);
}

/* ── metadata row ── */
.detail-dialog__meta {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  margin: 10px 0 0;
}

.detail-dialog__chip {
  display: inline-flex;
  align-items: center;
  padding: 2px 8px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--dialog-level, var(--accent-primary, #2ec4b6)) 10%, transparent);
  color: var(--text-secondary, #7d8590);
  font-size: 11px;
  line-height: 1.4;
  white-space: nowrap;
}

.detail-dialog__chip--remote {
  background: color-mix(in srgb, var(--accent-warning, #d29922) 12%, transparent);
  color: var(--accent-warning, #d29922);
}

.detail-dialog__time {
  margin-left: auto;
  font-size: 11px;
  color: var(--text-secondary, #7d8590);
  opacity: 0.7;
  white-space: nowrap;
}

/* ── task progress ── */
.detail-dialog__progress {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 12px 0 0;
}

.detail-dialog__progress-bar {
  flex: 1;
  height: 6px;
  border-radius: 3px;
  background: var(--bg-secondary, #11151c);
  overflow: hidden;
}

.detail-dialog__progress-fill {
  height: 100%;
  border-radius: 3px;
  background: var(--dialog-level, var(--accent-primary, #2ec4b6));
  transition: width 300ms ease;
}

.detail-dialog__progress-text {
  flex: none;
  min-width: 32px;
  font-size: 11px;
  color: var(--text-secondary, #7d8590);
  text-align: right;
}

/* ── body sections ── */
.detail-dialog__summary {
  margin: 14px 0 16px;
  color: var(--text-secondary, #7d8590);
  font-size: 13px;
  line-height: 1.5;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.detail-dialog__details {
  margin: 0 0 16px;
  padding: 11px 12px;
  border: 1px solid var(--border-default, #1a1f29);
  border-radius: 7px;
  background: color-mix(in srgb, var(--bg-secondary, #11151c) 76%, transparent);
}

.detail-dialog__details > strong {
  display: block;
  margin-bottom: 6px;
  color: var(--text-primary, #c9d1d9);
  font-size: 11px;
}

.detail-dialog__details p,
.detail-dialog__error p {
  margin: 0;
  font-size: 12px;
  line-height: 1.5;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.detail-dialog__error {
  display: flex;
  align-items: flex-start;
  gap: 9px;
  margin-bottom: 16px;
  padding: 10px 11px;
  border: 1px solid color-mix(in srgb, var(--accent-danger, #f85149) 30%, transparent);
  border-radius: 7px;
  background: color-mix(in srgb, var(--accent-danger, #f85149) 9%, transparent);
  color: var(--accent-danger, #f85149);
}

.detail-dialog__error svg { flex: none; margin-top: 1px; }
.detail-dialog__error strong { display: block; margin-bottom: 3px; font-size: 11px; }

/* ── actions ── */
.detail-dialog__actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.detail-dialog button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  min-height: 30px;
  border: 1px solid var(--border-default, #1a1f29);
  border-radius: 6px;
  padding: 5px 12px;
  font: inherit;
  font-size: 12px;
  cursor: pointer;
  -webkit-app-region: no-drag;
}

.detail-dialog__secondary {
  background: var(--bg-secondary, #11151c);
  color: var(--text-primary, #c9d1d9);
}

.detail-dialog__dismiss {
  background: transparent;
  color: var(--text-secondary, #7d8590);
}

/* lifecycle hint — replaces the dismiss control on source-owned items */
.detail-dialog__lifecycle-hint {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-height: 30px;
  padding: 5px 4px;
  color: var(--text-tertiary, #656d76);
  font-size: 11px;
  line-height: 1.3;
}

.detail-dialog__primary {
  border-color: var(--accent-primary, #2ec4b6) !important;
  background: var(--accent-primary, #2ec4b6);
  color: #0a0e14;
  font-weight: 600;
}

.detail-dialog__primary--destructive {
  border-color: var(--accent-danger, #f85149) !important;
  background: var(--accent-danger, #f85149);
  color: #fff;
}

/* ── spinner ── */
.detail-dialog__spinner {
  animation: detail-dialog-spin 800ms linear infinite;
}

@keyframes detail-dialog-spin {
  to { transform: rotate(360deg); }
}

/* ── focus / disabled ── */
.detail-dialog button:focus-visible {
  outline: 2px solid var(--accent-primary, #2ec4b6);
  outline-offset: 2px;
}

.detail-dialog button:disabled {
  opacity: 0.55;
  cursor: not-allowed;
}

/* ── motion ── */
@media (prefers-reduced-motion: reduce) {
  .detail-dialog__spinner { animation: none; }
  .detail-dialog__progress-fill { transition: none; }
}
</style>
