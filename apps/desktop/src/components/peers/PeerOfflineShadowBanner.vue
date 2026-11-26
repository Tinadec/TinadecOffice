<script setup lang="ts">
import { computed, ref } from 'vue'
import { CloudOff, RefreshCw, Trash2 } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { PeerConnectionState } from '@/composables/usePeerDevices'

const props = withDefaults(defineProps<{
  deviceId: string
  deviceName?: string | null
  state?: PeerConnectionState
  lastSyncedAt?: number | null
  reconnecting?: boolean
  clearingCache?: boolean
}>(), {
  deviceName: null,
  state: 'disconnected',
  lastSyncedAt: null,
  reconnecting: false,
  clearingCache: false,
})

const emit = defineEmits<{
  reconnect: [deviceId: string]
  'clear-cache': [deviceId: string]
}>()

const { t, locale } = useI18n()
const confirmingClear = ref(false)

const formattedSyncTime = computed(() => {
  const ts = props.lastSyncedAt
  if (!ts || !Number.isFinite(ts) || ts <= 0) {
    return t('peers.shadowBanner.syncTimeUnknown')
  }
  try {
    return new Intl.DateTimeFormat(locale.value || undefined, {
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    }).format(new Date(ts))
  } catch {
    return new Date(ts).toLocaleTimeString()
  }
})

const displayDeviceName = computed(() => props.deviceName?.trim() || props.deviceId)

function handleReconnect(): void {
  if (props.reconnecting) return
  emit('reconnect', props.deviceId)
}

function handleClearClick(): void {
  if (props.clearingCache) return
  if (!confirmingClear.value) {
    confirmingClear.value = true
    return
  }
  confirmingClear.value = false
  emit('clear-cache', props.deviceId)
}

function cancelClearConfirm(): void {
  confirmingClear.value = false
}
</script>

<template>
  <div
    class="peer-offline-shadow-banner"
    role="status"
    aria-live="polite"
    data-testid="peer-offline-shadow-banner"
  >
    <div class="peer-shadow-banner-main">
      <span class="peer-shadow-banner-icon" aria-hidden="true">
        <CloudOff :size="14" />
      </span>
      <div class="peer-shadow-banner-copy">
        <div class="peer-shadow-banner-headline">
          <strong>{{ t('peers.shadowBanner.title', { name: displayDeviceName }) }}</strong>
          <span class="peer-shadow-banner-sync" data-testid="peer-shadow-synced-at">
            {{ t('peers.shadowBanner.lastSynced', { time: formattedSyncTime }) }}
          </span>
        </div>
        <p class="peer-shadow-banner-desc">
          {{ t('peers.shadowBanner.readonlyHint') }}
          <span class="peer-shadow-banner-cache-note">{{ t('peers.shadowBanner.localCacheSafetyNote') }}</span>
        </p>
      </div>
    </div>

    <div class="peer-shadow-banner-actions">
      <button
        type="button"
        class="peer-shadow-btn peer-shadow-btn--primary"
        :disabled="reconnecting || state === 'connecting' || state === 'handshaking'"
        data-testid="peer-shadow-reconnect-btn"
        @click="handleReconnect"
      >
        <RefreshCw :size="12" :class="{ 'is-spinning': reconnecting || state === 'connecting' || state === 'handshaking' }" aria-hidden="true" />
        <span>{{ reconnecting || state === 'connecting' ? t('peers.shadowBanner.reconnecting') : t('peers.shadowBanner.reconnectBtn') }}</span>
      </button>

      <template v-if="!confirmingClear">
        <button
          type="button"
          class="peer-shadow-btn"
          :disabled="clearingCache"
          :title="t('peers.shadowBanner.localCacheSafetyNote')"
          data-testid="peer-shadow-clear-cache-btn"
          @click="handleClearClick"
        >
          <Trash2 :size="12" aria-hidden="true" />
          <span>{{ t('peers.shadowBanner.clearCacheBtn') }}</span>
        </button>
      </template>
      <template v-else>
        <button
          type="button"
          class="peer-shadow-btn peer-shadow-btn--confirm"
          :disabled="clearingCache"
          data-testid="peer-shadow-confirm-clear-btn"
          @click="handleClearClick"
        >
          <span>{{ clearingCache ? t('peers.shadowBanner.clearingCache') : t('peers.shadowBanner.confirmClearBtn') }}</span>
        </button>
        <button
          type="button"
          class="peer-shadow-btn"
          :disabled="clearingCache"
          data-testid="peer-shadow-cancel-clear-btn"
          @click="cancelClearConfirm"
        >
          <span>{{ t('common.cancel') }}</span>
        </button>
      </template>
    </div>
  </div>
</template>

<style scoped>
.peer-offline-shadow-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 10px 14px;
  margin: 8px 16px 0;
  padding: 9px 12px;
  border-radius: 10px;
  border: 1px solid var(--border-card, var(--border-default));
  background: var(--surface-raised);
  color: var(--text-primary);
  flex-shrink: 0;
}

.peer-shadow-banner-main {
  display: flex;
  align-items: flex-start;
  gap: 9px;
  min-width: 0;
  flex: 1 1 280px;
}

.peer-shadow-banner-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-section);
  color: var(--text-secondary);
  flex-shrink: 0;
  margin-top: 1px;
}

.peer-shadow-banner-copy {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.peer-shadow-banner-headline {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}

.peer-shadow-banner-headline strong {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.peer-shadow-banner-sync {
  padding: 1px 6px;
  border-radius: 999px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-section);
  color: var(--text-muted);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.peer-shadow-banner-desc {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.45;
  color: var(--text-secondary);
}

.peer-shadow-banner-cache-note {
  color: var(--text-muted);
}

.peer-shadow-banner-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  flex-shrink: 0;
}

.peer-shadow-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 10px;
  border-radius: 7px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color 140ms ease, border-color 140ms ease;
}

.peer-shadow-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  border-color: var(--border-card-active, var(--border-default));
}

.peer-shadow-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.peer-shadow-btn--primary {
  background: var(--bg-primary-button, var(--text-primary));
  border-color: transparent;
  color: var(--text-on-primary, var(--bg-primary));
}

.peer-shadow-btn--primary:hover:not(:disabled) {
  opacity: 0.9;
  background: var(--bg-primary-button, var(--text-primary));
}

.peer-shadow-btn--confirm {
  border-color: var(--border-card-active, var(--border-default));
  font-weight: 600;
}

.is-spinning {
  animation: peer-shadow-spin 1s linear infinite;
}

@keyframes peer-shadow-spin {
  to {
    transform: rotate(360deg);
  }
}

@media (prefers-reduced-motion: reduce) {
  .peer-shadow-btn {
    transition: none;
  }
  .is-spinning {
    animation: none;
  }
}
</style>
