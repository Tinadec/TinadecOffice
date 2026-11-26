<script setup lang="ts">
import { computed } from 'vue'
import { Clock, RefreshCw, RotateCcw, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { PeerUncertainSubmissionRecord } from '@/lib/peerSubmissionReconcile'

const props = defineProps<{
  record: PeerUncertainSubmissionRecord
}>()

const emit = defineEmits<{
  'check-status': [clientRequestId: string]
  resend: [clientRequestId: string]
  dismiss: [clientRequestId: string]
}>()

const { t } = useI18n()

const isRetryReady = computed(() => props.record.status === 'retry_ready')
const isBusy = computed(() => props.record.reconciling || props.record.resending)

const statusBadgeText = computed(() => {
  if (props.record.reconciling) return t('peers.uncertainBubble.checkingStatus')
  if (isRetryReady.value) {
    return props.record.remoteStage === 'rejected'
      ? t('peers.uncertainBubble.statusRejected')
      : t('peers.uncertainBubble.statusNotReceived')
  }
  return t('peers.uncertainBubble.statusUncertain')
})

const guidanceText = computed(() => {
  if (isRetryReady.value) {
    return props.record.remoteStage === 'rejected'
      ? t('peers.uncertainBubble.rejectedHint')
      : t('peers.uncertainBubble.retryReadyHint')
  }
  return t('peers.uncertainBubble.uncertainHint')
})
</script>

<template>
  <article
    class="peer-uncertain-bubble"
    :class="{ 'is-retry-ready': isRetryReady }"
    :data-status="record.status"
    data-testid="peer-uncertain-bubble"
  >
    <div class="peer-uncertain-bubble-card">
      <div class="peer-uncertain-content">{{ record.text || t('peers.uncertainBubble.attachmentOnlyContent') }}</div>

      <div v-if="record.stagedAttachmentIds.length > 0" class="peer-uncertain-attachments">
        {{ t('peers.uncertainBubble.stagedAttachmentsCount', { count: record.stagedAttachmentIds.length }) }}
      </div>

      <div class="peer-uncertain-footer">
        <div class="peer-uncertain-meta">
          <Clock :size="12" aria-hidden="true" />
          <span class="peer-uncertain-badge" data-testid="peer-uncertain-status-badge">{{ statusBadgeText }}</span>
          <span class="peer-uncertain-guidance">{{ guidanceText }}</span>
        </div>

        <div class="peer-uncertain-actions">
          <button
            type="button"
            class="peer-uncertain-btn"
            :disabled="isBusy"
            data-testid="peer-uncertain-check-btn"
            @click="emit('check-status', record.clientRequestId)"
          >
            <RefreshCw :size="11" :class="{ 'is-spinning': record.reconciling }" aria-hidden="true" />
            <span>{{ t('peers.uncertainBubble.checkStatusBtn') }}</span>
          </button>

          <button
            type="button"
            class="peer-uncertain-btn peer-uncertain-btn--resend"
            :disabled="!isRetryReady || isBusy"
            :title="isRetryReady ? t('peers.uncertainBubble.resendBtn') : t('peers.uncertainBubble.resendLockedHint')"
            data-testid="peer-uncertain-resend-btn"
            @click="emit('resend', record.clientRequestId)"
          >
            <RotateCcw :size="11" aria-hidden="true" />
            <span>{{ record.resending ? t('peers.uncertainBubble.resending') : t('peers.uncertainBubble.resendBtn') }}</span>
          </button>

          <button
            v-if="isRetryReady"
            type="button"
            class="peer-uncertain-icon-btn"
            :disabled="isBusy"
            :title="t('peers.uncertainBubble.dismissBtn')"
            :aria-label="t('peers.uncertainBubble.dismissBtn')"
            data-testid="peer-uncertain-dismiss-btn"
            @click="emit('dismiss', record.clientRequestId)"
          >
            <X :size="12" aria-hidden="true" />
          </button>
        </div>
      </div>
    </div>
  </article>
</template>

<style scoped>
.peer-uncertain-bubble {
  display: flex;
  justify-content: flex-end;
  width: 100%;
}

.peer-uncertain-bubble-card {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-width: min(680px, 92%);
  padding: 10px 13px;
  border-radius: 12px;
  border: 1px dashed var(--border-dashed, var(--border-card, var(--border-default)));
  background: var(--surface-section);
  color: var(--text-secondary);
}

.peer-uncertain-bubble.is-retry-ready .peer-uncertain-bubble-card {
  border-style: solid;
  border-color: var(--border-card-active, var(--border-default));
  background: var(--surface-raised);
  color: var(--text-primary);
}

.peer-uncertain-content {
  font-size: 13px;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
  opacity: 0.85;
}

.peer-uncertain-attachments {
  font-size: 11px;
  color: var(--text-muted);
}

.peer-uncertain-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px 12px;
  padding-top: 6px;
  border-top: 1px solid var(--border-card, var(--border-muted));
}

.peer-uncertain-meta {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  font-size: 11px;
  color: var(--text-muted);
}

.peer-uncertain-badge {
  padding: 1px 6px;
  border-radius: 999px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-weight: 600;
}

.peer-uncertain-guidance {
  color: var(--text-muted);
}

.peer-uncertain-actions {
  display: flex;
  align-items: center;
  gap: 6px;
}

.peer-uncertain-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  border-radius: 6px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color 140ms ease, border-color 140ms ease;
}

.peer-uncertain-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  border-color: var(--border-card-active, var(--border-default));
}

.peer-uncertain-btn:disabled {
  opacity: 0.42;
  cursor: not-allowed;
}

.peer-uncertain-btn--resend:not(:disabled) {
  background: var(--bg-primary-button, var(--text-primary));
  border-color: transparent;
  color: var(--text-on-primary, var(--bg-primary));
}

.peer-uncertain-icon-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border-radius: 6px;
  border: 1px solid transparent;
  background: transparent;
  color: var(--text-muted);
  cursor: pointer;
}

.peer-uncertain-icon-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.is-spinning {
  animation: peer-uncertain-spin 1s linear infinite;
}

@keyframes peer-uncertain-spin {
  to {
    transform: rotate(360deg);
  }
}

@media (prefers-reduced-motion: reduce) {
  .peer-uncertain-btn {
    transition: none;
  }
  .is-spinning {
    animation: none;
  }
}
</style>
