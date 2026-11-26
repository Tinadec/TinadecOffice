<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { PeerConnectionState } from '@/composables/usePeerDevices'

const props = withDefaults(defineProps<{
  state: PeerConnectionState
  deviceName?: string | null
  offlineReadonly?: boolean
  compact?: boolean
}>(), {
  deviceName: null,
  offlineReadonly: false,
  compact: false,
})

const { t } = useI18n()

type BadgeTone = 'ready' | 'connecting' | 'offline' | 'danger' | 'warning'

const tone = computed<BadgeTone>(() => {
  switch (props.state) {
    case 'ready':
      return 'ready'
    case 'connecting':
    case 'handshaking':
      return 'connecting'
    case 'fingerprint_mismatch':
    case 'unauthorized':
    case 'error':
      return 'danger'
    case 'version_incompatible':
      return 'warning'
    case 'disconnected':
    default:
      return 'offline'
  }
})

const stateLabel = computed(() => {
  switch (props.state) {
    case 'ready':
      return t('peers.state.ready')
    case 'connecting':
      return t('peers.state.connecting')
    case 'handshaking':
      return t('peers.state.handshaking')
    case 'fingerprint_mismatch':
      return t('peers.state.fingerprint_mismatch')
    case 'unauthorized':
      return t('peers.state.unauthorized')
    case 'version_incompatible':
      return t('peers.state.version_incompatible')
    case 'error':
      return t('peers.state.error')
    case 'disconnected':
    default:
      return t('peers.state.disconnected')
  }
})

const showOfflineReadonly = computed(() => props.offlineReadonly && props.state !== 'ready')
</script>

<template>
  <span
    class="peer-device-badge"
    :class="[`is-${tone}`, { 'is-compact': compact }]"
    :data-state="state"
    :title="deviceName ? `${deviceName} · ${showOfflineReadonly ? t('peers.badge.offlineReadonly') : stateLabel}` : stateLabel"
    data-testid="peer-device-badge"
  >
    <span class="peer-device-badge-dot" aria-hidden="true" />
    <span v-if="deviceName" class="peer-device-badge-name">{{ deviceName }}</span>
    <span v-if="deviceName && showOfflineReadonly" class="peer-device-badge-meta">{{ t('peers.badge.offlineReadonly') }}</span>
    <span v-else-if="!deviceName" class="peer-device-badge-label">{{ stateLabel }}</span>
  </span>
</template>

<style scoped>
.peer-device-badge {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  max-width: 100%;
  min-width: 0;
  padding: 2px 7px;
  border-radius: 999px;
  border: 1px solid var(--border-card, var(--border-muted));
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  line-height: 1.35;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.peer-device-badge.is-compact {
  padding: 1px 6px;
  font-size: 10.5px;
  gap: 4px;
}

.peer-device-badge.is-ready {
  color: var(--text-primary);
}

.peer-device-badge.is-danger {
  border-color: color-mix(in srgb, var(--accent-danger) 38%, var(--border-card));
  color: var(--accent-danger);
}

.peer-device-badge.is-warning {
  border-color: color-mix(in srgb, var(--accent-warning) 38%, var(--border-card));
  color: var(--accent-warning);
}

.peer-device-badge-dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  flex-shrink: 0;
  background: var(--text-muted);
}

.peer-device-badge.is-ready .peer-device-badge-dot {
  background: var(--accent-success);
}

.peer-device-badge.is-connecting .peer-device-badge-dot {
  background: var(--accent-warning);
  animation: peer-badge-pulse 1.4s ease-in-out infinite;
}

.peer-device-badge.is-danger .peer-device-badge-dot {
  background: var(--accent-danger);
}

.peer-device-badge.is-warning .peer-device-badge-dot {
  background: var(--accent-warning);
}

.peer-device-badge-name {
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 96px;
}

.peer-device-badge-meta {
  color: var(--text-muted);
  font-size: 10px;
}

@keyframes peer-badge-pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.35; }
}

@media (prefers-reduced-motion: reduce) {
  .peer-device-badge.is-connecting .peer-device-badge-dot {
    animation: none;
  }
}
</style>
