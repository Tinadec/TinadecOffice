<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { AlertTriangle } from '@lucide/vue'

const props = defineProps<{
  open: boolean
  actionId: string
  toolId: string
}>()

const emit = defineEmits<{
  'update:open': [value: boolean]
  confirmed: [reason: string]
  cancelled: []
}>()

const { t } = useI18n()
const acknowledged = ref(false)
const reason = ref('')
const dialog = ref<HTMLDialogElement | null>(null)

const canConfirm = computed(() => acknowledged.value && props.actionId.length > 0)

watch(
  () => props.open,
  (open) => {
    if (!import.meta.env.SSR && dialog.value) {
      if (open && !dialog.value.open) dialog.value.showModal()
      else if (!open && dialog.value.open) dialog.value.close()
    }
  },
)

function cancel(): void {
  acknowledged.value = false
  reason.value = ''
  emit('cancelled')
  emit('update:open', false)
}

function confirm(): void {
  if (!canConfirm.value) return
  const finalReason = reason.value.trim() || 'User acknowledged the change is non-reversible and chose to proceed without a snapshot.'
  acknowledged.value = false
  reason.value = ''
  emit('confirmed', finalReason)
  emit('update:open', false)
}
</script>

<template>
  <Teleport to="body">
    <dialog
      ref="dialog"
      class="detail-dialog no-drag detail-dialog--destructive"
      data-testid="override-dialog"
      @cancel.prevent="cancel"
      @click="(e: MouseEvent) => { if (e.target === dialog) cancel() }"
    >
      <div class="detail-dialog__heading">
        <span class="detail-dialog__icon" aria-hidden="true"><AlertTriangle :size="20" /></span>
        <h2 data-testid="override-title">{{ t('governance.snapshotOverrideTitle', 'Skip workspace snapshot?') }}</h2>
      </div>
      <p class="detail-dialog__summary" data-testid="override-body">
        {{
          t(
            'governance.snapshotOverrideBody',
            'The snapshot could not be captured for this action. Proceeding without one means this change may be impossible to roll back.',
          )
        }}
      </p>

      <div class="snapshot-override__meta" data-testid="override-action-ref">{{ toolId }} · {{ actionId.slice(0, 8) }}</div>

      <label class="snapshot-override__ack">
        <input v-model="acknowledged" type="checkbox" data-testid="override-acknowledge" />
        <span>
          {{
            t(
              'governance.snapshotOverrideAck',
              'I understand this change is recorded as non-reversible and cannot be undone by TinadecOffice.',
            )
          }}
        </span>
      </label>

      <textarea
        v-model="reason"
        class="snapshot-override__reason"
        rows="2"
        :placeholder="t('governance.snapshotOverrideReasonPlaceholder', 'Optional reason (recorded in the audit trail)')"
        data-testid="override-reason"
      />

      <div class="detail-dialog__actions">
        <button type="button" class="detail-dialog__btn" data-testid="override-cancel" @click="cancel">
          {{ t('common.cancel', 'Cancel') }}
        </button>
        <button
          type="button"
          class="detail-dialog__btn detail-dialog__btn--danger"
          :disabled="!canConfirm"
          data-testid="override-confirm"
          @click="confirm"
        >
          {{ t('governance.snapshotOverrideConfirm', 'Proceed without snapshot') }}
        </button>
      </div>
    </dialog>
  </Teleport>
</template>

<style scoped>
.snapshot-override__meta {
  margin: 10px 0;
  font-family: var(--font-mono, monospace);
  font-size: 11px;
  color: var(--text-secondary);
}

.snapshot-override__ack {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  margin-bottom: 12px;
  font-size: 13px;
  cursor: pointer;
}

.snapshot-override__ack input {
  margin-top: 2px;
}

.snapshot-override__reason {
  width: 100%;
  margin-bottom: 14px;
  padding: 8px 10px;
  border: 1px solid var(--border-input);
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary);
  font-size: 13px;
  resize: vertical;
}
</style>
