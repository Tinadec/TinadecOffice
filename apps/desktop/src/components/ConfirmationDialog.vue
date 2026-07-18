<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle } from '@lucide/vue'
import { resolveConfirmation, useNotifications } from '@/composables/useNotifications'

const { t } = useI18n()
const { currentConfirmation } = useNotifications()
const dialog = ref<HTMLDialogElement | null>(null)
const cancelButton = ref<HTMLButtonElement | null>(null)
const resolving = ref(false)
let returnFocus: HTMLElement | null = null
let displayedRequestId: string | null = null
let unlockTimer: ReturnType<typeof setTimeout> | null = null

watch(currentConfirmation, async (request) => {
  await nextTick()
  const element = dialog.value
  if (request) {
    if (element?.open && displayedRequestId && displayedRequestId !== request.id) element.close()
    displayedRequestId = request.id
    if (!element?.open) {
      if (!returnFocus) returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
      element?.showModal()
    }
    cancelButton.value?.focus()
  } else if (element?.open) {
    element.close()
    returnFocus?.focus()
    returnFocus = null
    displayedRequestId = null
  }
}, { immediate: true })

function finish(value: boolean): void {
  if (resolving.value || !displayedRequestId) return
  resolving.value = true
  resolveConfirmation(displayedRequestId, value)
  if (unlockTimer) clearTimeout(unlockTimer)
  unlockTimer = setTimeout(() => {
    resolving.value = false
    cancelButton.value?.focus()
  }, 250)
}

onBeforeUnmount(() => {
  if (unlockTimer) clearTimeout(unlockTimer)
})

function cancel(event: Event): void {
  event.preventDefault()
  finish(false)
}

function backdrop(event: MouseEvent): void {
  const element = dialog.value
  if (event.target !== element || !element) return
  const rect = element.getBoundingClientRect()
  if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) {
    finish(false)
  }
}
</script>

<template>
  <Teleport to="body">
    <dialog
      ref="dialog"
      class="confirmation-dialog no-drag"
      :aria-labelledby="currentConfirmation ? `${currentConfirmation.id}-title` : undefined"
      :aria-describedby="currentConfirmation ? `${currentConfirmation.id}-description` : undefined"
      @cancel="cancel"
      @click="backdrop"
    >
      <template v-if="currentConfirmation">
        <div class="confirmation-dialog__heading">
          <AlertTriangle v-if="currentConfirmation.destructive" :size="19" aria-hidden="true" />
          <h2 :id="`${currentConfirmation.id}-title`">{{ currentConfirmation.title || t('app.confirm') }}</h2>
        </div>
        <p :id="`${currentConfirmation.id}-description`">{{ currentConfirmation.message }}</p>
        <div class="confirmation-dialog__actions">
          <button ref="cancelButton" type="button" class="confirmation-dialog__cancel" :disabled="resolving" @click="finish(false)">
            {{ currentConfirmation.cancelLabel || t('app.cancel') }}
          </button>
          <button
            type="button"
            class="confirmation-dialog__confirm"
            :class="{ 'confirmation-dialog__confirm--destructive': currentConfirmation.destructive }"
            :disabled="resolving"
            @click="finish(true)"
          >
            {{ currentConfirmation.confirmLabel || t('app.confirm') }}
          </button>
        </div>
      </template>
    </dialog>
  </Teleport>
</template>

<style scoped>
.confirmation-dialog {
  width: min(calc(100vw - 32px), 430px);
  margin: auto;
  padding: 18px;
  border: 1px solid var(--border-default, #d4d4d8);
  border-radius: 8px;
  background: var(--bg-primary, #fff);
  color: var(--text-primary, #18181b);
  box-shadow: 0 18px 60px rgb(0 0 0 / 28%);
  -webkit-app-region: no-drag;
}

.confirmation-dialog::backdrop { background: rgb(0 0 0 / 42%); }
.confirmation-dialog__heading { display: flex; align-items: center; gap: 9px; }
.confirmation-dialog__heading svg { flex: none; color: var(--accent-danger, #d13c4b); }
.confirmation-dialog h2 { margin: 0; font-size: 15px; line-height: 1.3; overflow-wrap: anywhere; }
.confirmation-dialog p { margin: 10px 0 18px; color: var(--text-secondary, #52525b); font-size: 13px; line-height: 1.5; white-space: pre-wrap; overflow-wrap: anywhere; }
.confirmation-dialog__actions { display: flex; justify-content: flex-end; gap: 8px; }
.confirmation-dialog button {
  min-height: 30px;
  border: 1px solid var(--border-default, #d4d4d8);
  border-radius: 6px;
  padding: 5px 12px;
  font: inherit;
  font-size: 12px;
  cursor: pointer;
  -webkit-app-region: no-drag;
}
.confirmation-dialog__cancel { background: var(--bg-secondary, #f4f4f5); color: var(--text-primary, #18181b); }
.confirmation-dialog__confirm { border-color: var(--accent-primary, #2563eb) !important; background: var(--accent-primary, #2563eb); color: #fff; }
.confirmation-dialog__confirm--destructive { border-color: var(--accent-danger, #c92a3d) !important; background: var(--accent-danger, #c92a3d); }
.confirmation-dialog button:focus-visible { outline: 2px solid var(--accent-primary, #2563eb); outline-offset: 2px; }

@media (prefers-reduced-motion: reduce) {
  .confirmation-dialog { scroll-behavior: auto; }
}
</style>
