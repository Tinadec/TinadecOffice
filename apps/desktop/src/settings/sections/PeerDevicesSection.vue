<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Ban,
  Copy,
  Eraser,
  FolderSync,
  KeyRound,
  MonitorSmartphone,
  Plus,
  Plug,
  Radar,
  RefreshCw,
  Save,
  Server,
  ShieldAlert,
  ShieldCheck,
  Trash2,
  Unplug,
} from '@lucide/vue'
import {
  UiBadge,
  UiButton,
  UiCheckbox,
  UiInput,
  UiLabel,
  UiSelectField,
  UiSwitch,
} from '@/components/ui'
import { api as baseApi, type ProjectDto } from '@/api'
import { endpointScopedApi } from '@/lib/endpointScope'
import {
  formatPeerFingerprint,
  usePeerDevices,
  type PeerDeviceDto,
  type PeerGrantDto,
  type PeerRole,
  type PeerWorkspaceMode,
  type UpdatePeerGrantPolicyInput,
} from '@/composables/usePeerDevices'
import PeerDeviceBadge from '@/components/peers/PeerDeviceBadge.vue'
import PeerPairingModal from '@/components/peers/PeerPairingModal.vue'
import PeerMountWorkspaceModal from '@/components/peers/PeerMountWorkspaceModal.vue'
import { useHostAccess } from '@/lib/hostAccess'
import { useNotifications } from '@/composables/useNotifications'
import { errorCodeMessage } from '@/lib/apiError'
import { homeController } from '@/controllers/HomeController'

const PEERS_NOTIFY_KEY = 'peer-devices-settings'
const localApi = endpointScopedApi(baseApi, () => 'local')

const { t, te } = useI18n()
const { canAccessBackend } = useHostAccess()
const { notify, confirm, dismissByKey } = useNotifications()

const {
  loading,
  inboundSettings,
  pairingCode,
  devices,
  grants,
  activeEndpoint,
  activePeerDevice,
  refreshAll,
  updateInbound,
  generatePairingCode,
  revokePairingCode,
  probeDevice,
  connectDevice,
  disconnectDevice,
  updateDevice,
  deleteDevice,
  clearDeviceLocalCache,
  updateGrantPolicy,
  revokeGrant,
  switchEndpoint,
} = usePeerDevices()

// ---- Modals ----
const pairingModalOpen = ref(false)
const mountModalOpen = ref(false)
const mountTargetDevice = ref<PeerDeviceDto | null>(null)

// ---- Block 1: Inbound & Pairing Credentials state ----
const displayNameDraft = ref('')
const listenPortDraft = ref('48735')
const selectedAddress = ref('')
const savingInbound = ref(false)
const issuingCode = ref(false)
const nowMs = ref(Date.now())
let countdownTimer: ReturnType<typeof setInterval> | null = null

// ---- Block 2: Paired Remote Devices action locks ----
const deviceBusyId = ref<string | null>(null)

// ---- Block 3: Worker Grants & Governance Policies ----
const localProjects = ref<ProjectDto[]>([])
const grantBusyId = ref<string | null>(null)

interface GrantPolicyDraft {
  role: PeerRole
  workspace_mode: PeerWorkspaceMode
  workspace_ids: string[]
  sync_all_sessions: boolean
  allow_agent_delegation: boolean
  allow_provider_proxy: boolean
}

const grantDrafts = reactive<Record<string, GrantPolicyDraft>>({})

function syncInboundDrafts() {
  displayNameDraft.value = inboundSettings.value.display_name || ''
  listenPortDraft.value = String(inboundSettings.value.listen_port || 48735)
  const addrs = inboundSettings.value.reachable_addresses ?? []
  if (addrs.length > 0 && (!selectedAddress.value || !addrs.includes(selectedAddress.value))) {
    selectedAddress.value = addrs[0]!
  } else if (addrs.length === 0) {
    selectedAddress.value = ''
  }
}

function syncGrantDrafts(list: readonly PeerGrantDto[]) {
  for (const grant of list) {
    if (grant.revoked_at) continue
    const policy = grant.policy
    grantDrafts[grant.controller_device_id] = {
      role: policy?.role ?? 'operator',
      workspace_mode: policy?.workspace_mode ?? 'all',
      workspace_ids: [...(policy?.workspace_ids ?? [])],
      sync_all_sessions: Boolean(policy?.sync_all_sessions),
      allow_agent_delegation: Boolean(policy?.allow_agent_delegation),
      allow_provider_proxy: Boolean(policy?.allow_provider_proxy),
    }
  }
}

watch(inboundSettings, syncInboundDrafts, { immediate: true, deep: true })
watch(grants, syncGrantDrafts, { immediate: true, deep: true })

const localFingerprintChunks = computed(() =>
  formatPeerFingerprint(inboundSettings.value.local_fingerprint || '')
    .split(/\s+/)
    .filter(Boolean),
)

const reachableOptions = computed(() =>
  (inboundSettings.value.reachable_addresses ?? []).map((addr) => ({
    value: addr,
    label: addr,
  })),
)

const portConflictDetected = computed(() => {
  const s = inboundSettings.value
  return Boolean(
    s.port_conflict || (s.configured_port && s.configured_port !== s.listen_port),
  )
})

const activePairingCodeValid = computed(() => {
  const code = pairingCode.value
  if (!code || !code.active || !code.pairing_code) return false
  if (code.expires_at_ms && code.expires_at_ms <= nowMs.value) return false
  return true
})

const pairingCountdownText = computed(() => {
  const expiresAt = pairingCode.value?.expires_at_ms
  if (!expiresAt) return ''
  const diffSec = Math.max(0, Math.ceil((expiresAt - nowMs.value) / 1000))
  if (diffSec <= 0) return t('peers.inbound.codeExpired')
  const mins = Math.floor(diffSec / 60)
  const secs = diffSec % 60
  return t('peers.inbound.codeExpiresIn', {
    time: `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')}`,
  })
})

const activeGrants = computed(() => grants.value.filter((g) => !g.revoked_at))

const roleOptions = computed(() => [
  { value: 'observer', label: t('peers.grants.roleObserver') },
  { value: 'operator', label: t('peers.grants.roleOperator') },
  { value: 'owner', label: t('peers.grants.roleOwner') },
])

const workspaceModeOptions = computed(() => [
  { value: 'all', label: t('peers.grants.workspaceModeAll') },
  { value: 'selected', label: t('peers.grants.workspaceModeSelected') },
])

function formatError(err: unknown, fallbackKey: string): string {
  const code = (err as { code?: string | null })?.code
  const mapped = errorCodeMessage(code, undefined, { t, te })
  if (mapped) return mapped
  if (err instanceof Error && err.message) return err.message
  return t(fallbackKey)
}

function formatTimestamp(ms: number | null | undefined): string {
  if (!ms || !Number.isFinite(ms)) return t('peers.neverConnected')
  try {
    return new Date(ms).toLocaleString()
  } catch {
    return t('peers.neverConnected')
  }
}

async function loadEverything() {
  if (!canAccessBackend.value) return
  try {
    await Promise.all([
      refreshAll(),
      localApi.listProjects().then((rows) => {
        localProjects.value = rows
      }).catch(() => {}),
    ])
    dismissByKey(PEERS_NOTIFY_KEY)
  } catch (err) {
    notify.error(formatError(err, 'peers.loadFailed'), {
      key: PEERS_NOTIFY_KEY,
      title: t('peers.loadFailed'),
      action: { label: t('common.retry'), run: () => loadEverything() },
    })
  }
}

async function handleToggleInbound(enabled: boolean) {
  if (savingInbound.value) return
  savingInbound.value = true
  try {
    await updateInbound({ enabled })
    notify.success(t('peers.inbound.inboundSaved'))
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: t('peers.saveFailed') })
  } finally {
    savingInbound.value = false
  }
}

async function handleSaveInboundConfig() {
  if (savingInbound.value) return
  const parsedPort = Number.parseInt(listenPortDraft.value.trim(), 10)
  const port = Number.isFinite(parsedPort) && parsedPort >= 1024 && parsedPort <= 65535 ? parsedPort : 48735
  savingInbound.value = true
  try {
    await updateInbound({
      display_name: displayNameDraft.value.trim() || undefined,
      listen_port: port,
    })
    notify.success(t('peers.inbound.inboundSaved'))
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: t('peers.saveFailed') })
  } finally {
    savingInbound.value = false
  }
}

async function copyText(text: string, toastMessage: string) {
  if (!text) return
  try {
    await navigator.clipboard?.writeText(text)
    notify.success(toastMessage)
  } catch {
    // Clipboard unavailable
  }
}

async function handleGenerateCode() {
  if (issuingCode.value) return
  issuingCode.value = true
  try {
    await generatePairingCode({ ttl_seconds: 300 })
    nowMs.value = Date.now()
    notify.success(t('peers.inbound.codeGenerated'))
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: t('peers.saveFailed') })
  } finally {
    issuingCode.value = false
  }
}

async function handleRevokeCode() {
  if (issuingCode.value) return
  issuingCode.value = true
  try {
    await revokePairingCode()
    notify.success(t('peers.inbound.codeRevoked'))
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: t('peers.saveFailed') })
  } finally {
    issuingCode.value = false
  }
}

async function copyAddressAndCode() {
  const code = pairingCode.value?.pairing_code
  const addr = selectedAddress.value || inboundSettings.value.reachable_addresses[0] || ''
  const bundle = addr && code ? `${addr} (${code})` : code || addr
  await copyText(bundle, t('peers.copiedBundle'))
}

// ---- Block 2 handlers ----
async function handleConnectDevice(device: PeerDeviceDto) {
  if (deviceBusyId.value) return
  deviceBusyId.value = device.device_id
  try {
    const updated = await connectDevice(device.device_id)
    notify.success(t('peers.devices.connectedToast', { name: updated.display_name }))
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: device.display_name })
  } finally {
    deviceBusyId.value = null
  }
}

async function handleDisconnectDevice(device: PeerDeviceDto) {
  if (deviceBusyId.value) return
  deviceBusyId.value = device.device_id
  try {
    const updated = await disconnectDevice(device.device_id)
    if (activeEndpoint.value.kind === 'peer' && activeEndpoint.value.deviceId === device.device_id) {
      switchEndpoint('local')
      await homeController.refreshProjectsAndSessions().catch(() => {})
    }
    notify.success(t('peers.devices.disconnectedToast', { name: updated.display_name }))
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: device.display_name })
  } finally {
    deviceBusyId.value = null
  }
}

async function handleProbeExistingDevice(device: PeerDeviceDto) {
  if (deviceBusyId.value) return
  deviceBusyId.value = device.device_id
  try {
    const res = await probeDevice(device.device_id)
    notify.success(t('peers.devices.probeOkToast', { name: res.display_name, address: res.address }))
    await refreshAll().catch(() => {})
  } catch (err) {
    notify.error(formatError(err, 'peers.loadFailed'), { title: device.display_name })
  } finally {
    deviceBusyId.value = null
  }
}

async function handleToggleDeviceFlag(
  device: PeerDeviceDto,
  patch: { connection_desired?: boolean; adopt_sessions?: boolean },
) {
  if (deviceBusyId.value) return
  deviceBusyId.value = device.device_id
  try {
    await updateDevice(device.device_id, patch)
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: device.display_name })
  } finally {
    deviceBusyId.value = null
  }
}

function openMountModal(device: PeerDeviceDto) {
  mountTargetDevice.value = device
  mountModalOpen.value = true
}

async function handleClearCache(device: PeerDeviceDto) {
  const ok = await confirm({
    title: t('peers.devices.clearCacheConfirmTitle'),
    message: t('peers.devices.clearCacheConfirmMessage', { name: device.display_name }),
    confirmLabel: t('peers.devices.clearCache'),
    destructive: true,
  })
  if (!ok) return
  deviceBusyId.value = device.device_id
  try {
    await clearDeviceLocalCache(device.device_id)
    notify.success(t('peers.devices.cacheClearedToast', { name: device.display_name }))
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: device.display_name })
  } finally {
    deviceBusyId.value = null
  }
}

async function handleDeleteDevice(device: PeerDeviceDto) {
  const ok = await confirm({
    title: t('peers.devices.unpairConfirmTitle'),
    message: t('peers.devices.unpairConfirmMessage', { name: device.display_name }),
    confirmLabel: t('peers.devices.unpair'),
    destructive: true,
  })
  if (!ok) return
  deviceBusyId.value = device.device_id
  try {
    if (activeEndpoint.value.kind === 'peer' && activeEndpoint.value.deviceId === device.device_id) {
      switchEndpoint('local')
      await homeController.refreshProjectsAndSessions().catch(() => {})
    }
    await deleteDevice(device.device_id)
    notify.success(t('peers.devices.unpairedToast', { name: device.display_name }))
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: device.display_name })
  } finally {
    deviceBusyId.value = null
  }
}

// ---- Block 3 handlers ----
function ensureGrantDraft(grant: PeerGrantDto): GrantPolicyDraft {
  let draft = grantDrafts[grant.controller_device_id]
  if (!draft) {
    draft = {
      role: grant.policy?.role ?? 'operator',
      workspace_mode: grant.policy?.workspace_mode ?? 'all',
      workspace_ids: [...(grant.policy?.workspace_ids ?? [])],
      sync_all_sessions: Boolean(grant.policy?.sync_all_sessions),
      allow_agent_delegation: Boolean(grant.policy?.allow_agent_delegation),
      allow_provider_proxy: Boolean(grant.policy?.allow_provider_proxy),
    }
    grantDrafts[grant.controller_device_id] = draft
  }
  return draft
}

function toggleGrantWorkspace(grant: PeerGrantDto, projectId: string, checked?: boolean) {
  const draft = ensureGrantDraft(grant)
  const set = new Set(draft.workspace_ids)
  const next = checked ?? !set.has(projectId)
  if (next) set.add(projectId)
  else set.delete(projectId)
  draft.workspace_ids = [...set]
}

async function handleSaveGrantPolicy(grant: PeerGrantDto) {
  if (grantBusyId.value) return
  const draft = ensureGrantDraft(grant)
  const payload: UpdatePeerGrantPolicyInput = {
    role: draft.role,
    workspace_mode: draft.workspace_mode,
    workspace_ids: draft.workspace_mode === 'selected' ? draft.workspace_ids : [],
    sync_all_sessions: draft.sync_all_sessions,
    allow_agent_delegation: draft.allow_agent_delegation,
    allow_provider_proxy: draft.allow_provider_proxy,
  }
  grantBusyId.value = grant.controller_device_id
  try {
    await updateGrantPolicy(grant.controller_device_id, payload)
    notify.success(t('peers.grants.policySavedToast', { name: grant.display_name }))
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: grant.display_name })
  } finally {
    grantBusyId.value = null
  }
}

async function handleRevokeGrant(grant: PeerGrantDto) {
  const ok = await confirm({
    title: t('peers.grants.revokeConfirmTitle'),
    message: t('peers.grants.revokeConfirmMessage', { name: grant.display_name }),
    confirmLabel: t('peers.grants.revokeBtn'),
    destructive: true,
  })
  if (!ok) return
  grantBusyId.value = grant.controller_device_id
  try {
    await revokeGrant(grant.controller_device_id)
    notify.success(t('peers.grants.revokedToast', { name: grant.display_name }))
  } catch (err) {
    notify.error(formatError(err, 'peers.saveFailed'), { title: grant.display_name })
  } finally {
    grantBusyId.value = null
  }
}

async function handleSwitchBackLocal() {
  switchEndpoint('local')
  await homeController.refreshProjectsAndSessions().catch(() => {})
  notify.success(t('peers.switchedToLocal'))
}

watch(canAccessBackend, (ready, wasReady) => {
  if (ready && !wasReady) {
    dismissByKey(PEERS_NOTIFY_KEY)
    void loadEverything()
  }
})

onMounted(() => {
  countdownTimer = setInterval(() => {
    nowMs.value = Date.now()
  }, 1000)
  if (canAccessBackend.value) {
    void loadEverything()
  }
})

onBeforeUnmount(() => {
  if (countdownTimer) {
    clearInterval(countdownTimer)
    countdownTimer = null
  }
  dismissByKey(PEERS_NOTIFY_KEY)
})

defineExpose({
  canLeave: async () => true,
})
</script>

<template>
  <section class="settings-section peer-devices-section" data-testid="peer-devices-section">
    <!-- Top Section Header -->
    <header class="peer-section-header">
      <div class="peer-section-title">
        <MonitorSmartphone :size="18" aria-hidden="true" />
        <div>
          <h2>{{ t('peers.title') }}</h2>
          <p>{{ t('peers.description') }}</p>
        </div>
      </div>

      <div class="peer-section-actions">
        <UiBadge variant="outline">
          {{
            activeEndpoint.kind === 'peer'
              ? t('peers.activeEndpointPeer', { name: activePeerDevice?.display_name ?? activeEndpoint.deviceId })
              : t('peers.activeEndpointLocal')
          }}
        </UiBadge>
        <UiButton
          v-if="activeEndpoint.kind === 'peer'"
          variant="outline"
          size="sm"
          @click="handleSwitchBackLocal"
        >
          {{ t('peers.switchBackLocal') }}
        </UiButton>
        <UiButton
          variant="ghost"
          size="sm"
          :disabled="loading || !canAccessBackend"
          :aria-label="t('common.refresh')"
          @click="loadEverything"
        >
          <RefreshCw :size="14" />
        </UiButton>
        <UiButton
          size="sm"
          :disabled="!canAccessBackend"
          data-testid="peer-open-pair-modal-btn"
          @click="pairingModalOpen = true"
        >
          <Plus :size="14" />
          <span>{{ t('peers.pairNewDevice') }}</span>
        </UiButton>
      </div>
    </header>

    <!-- =======================================================================
         BLOCK 1: Local Inbound Listener & Pairing Credentials
         ======================================================================= -->
    <section class="peer-surface-card" data-testid="peer-block-inbound">
      <div class="peer-card-head">
        <Server :size="16" aria-hidden="true" />
        <div class="peer-card-head-text">
          <h3>{{ t('peers.inbound.title') }}</h3>
          <p>{{ t('peers.inbound.subtitle') }}</p>
        </div>
        <UiBadge :variant="inboundSettings.enabled ? 'default' : 'outline'">
          {{
            inboundSettings.enabled
              ? t('peers.inbound.statusListening', { port: inboundSettings.listen_port })
              : t('peers.inbound.statusDisabled')
          }}
        </UiBadge>
      </div>

      <div class="peer-setting-row">
        <div>
          <strong>{{ t('peers.inbound.masterSwitch') }}</strong>
          <p>{{ t('peers.inbound.masterSwitchHint') }}</p>
        </div>
        <UiSwitch
          :model-value="inboundSettings.enabled"
          :disabled="savingInbound || !canAccessBackend"
          :aria-label="t('peers.inbound.masterSwitch')"
          data-testid="peer-inbound-switch"
          @update:model-value="handleToggleInbound"
        />
      </div>

      <div class="peer-form-grid">
        <div class="peer-field">
          <UiLabel for="peer-local-display-name">{{ t('peers.inbound.deviceName') }}</UiLabel>
          <UiInput
            id="peer-local-display-name"
            v-model="displayNameDraft"
            :placeholder="t('peers.inbound.deviceNamePlaceholder')"
            :disabled="savingInbound"
          />
        </div>

        <div class="peer-field">
          <UiLabel for="peer-local-listen-port">{{ t('peers.inbound.listenPort') }}</UiLabel>
          <div class="peer-inline-controls">
            <UiInput
              id="peer-local-listen-port"
              v-model="listenPortDraft"
              type="number"
              class="font-mono"
              :disabled="savingInbound"
            />
            <UiButton
              type="button"
              variant="outline"
              size="sm"
              :disabled="savingInbound || !canAccessBackend"
              data-testid="peer-save-inbound-btn"
              @click="handleSaveInboundConfig"
            >
              <Save :size="14" />
              <span>{{ t('peers.inbound.saveInbound') }}</span>
            </UiButton>
          </div>
          <span class="peer-field-meta">
            {{ t('peers.inbound.actualPortNote', { port: inboundSettings.listen_port }) }}
          </span>
        </div>
      </div>

      <p v-if="portConflictDetected" class="peer-callout-notice" role="status">
        {{
          t('peers.inbound.portConflictNote', {
            configured: inboundSettings.configured_port ?? 48735,
            actual: inboundSettings.listen_port,
          })
        }}
      </p>

      <p v-if="inboundSettings.firewall_hint" class="peer-callout-notice" role="status">
        {{ inboundSettings.firewall_hint }}
      </p>

      <!-- Local SHA-256 Certificate Fingerprint -->
      <div class="peer-subpanel">
        <div class="peer-subpanel-head">
          <div>
            <strong>{{ t('peers.inbound.localFingerprint') }}</strong>
            <p>{{ t('peers.inbound.localFingerprintHint') }}</p>
          </div>
          <UiButton
            v-if="inboundSettings.local_fingerprint"
            type="button"
            variant="outline"
            size="sm"
            @click="copyText(formatPeerFingerprint(inboundSettings.local_fingerprint), t('peers.copiedFingerprint'))"
          >
            <Copy :size="13" />
            <span>{{ t('peers.inbound.copyFingerprint') }}</span>
          </UiButton>
        </div>
        <div v-if="localFingerprintChunks.length" class="peer-fp-grid" data-testid="peer-local-fingerprint">
          <code v-for="(chunk, idx) in localFingerprintChunks" :key="idx" class="peer-fp-pill">
            {{ chunk }}
          </code>
        </div>
      </div>

      <!-- Reachable LAN & Tailscale / WireGuard IP:Port Direct Copy (No mDNS auto-discovery) -->
      <div class="peer-subpanel">
        <div class="peer-subpanel-head">
          <div>
            <strong>{{ t('peers.inbound.reachableAddresses') }}</strong>
            <p>{{ t('peers.inbound.reachableAddressesHint') }}</p>
          </div>
        </div>

        <template v-if="reachableOptions.length > 0">
          <div class="peer-address-selector">
            <UiSelectField
              v-model="selectedAddress"
              :options="reachableOptions"
              :aria-label="t('peers.inbound.reachableAddresses')"
              class="peer-address-select"
            />
            <UiButton
              type="button"
              variant="outline"
              size="sm"
              data-testid="peer-copy-selected-address-btn"
              @click="copyText(selectedAddress, t('peers.copiedAddress', { address: selectedAddress }))"
            >
              <Copy :size="13" />
              <span>{{ t('peers.inbound.copySelectedAddress') }}</span>
            </UiButton>
          </div>

          <div class="peer-address-chips">
            <button
              v-for="addr in inboundSettings.reachable_addresses"
              :key="addr"
              type="button"
              class="peer-address-chip"
              :title="addr"
              @click="selectedAddress = addr; copyText(addr, t('peers.copiedAddress', { address: addr }))"
            >
              <code>{{ addr }}</code>
              <Copy :size="12" aria-hidden="true" />
            </button>
          </div>
        </template>
        <p v-else class="peer-empty-inline">{{ t('peers.inbound.reachableEmpty') }}</p>
      </div>

      <!-- One-Time 6-8 Digit Pairing Code (5-Minute Countdown) -->
      <div class="peer-subpanel" data-testid="peer-pairing-code-panel">
        <div class="peer-subpanel-head">
          <div class="peer-subpanel-title-inline">
            <KeyRound :size="15" aria-hidden="true" />
            <div>
              <strong>{{ t('peers.inbound.pairingCodeTitle') }}</strong>
              <p>{{ t('peers.inbound.pairingCodeHint') }}</p>
            </div>
          </div>
          <div class="peer-inline-controls">
            <UiButton
              type="button"
              variant="outline"
              size="sm"
              :disabled="issuingCode || !canAccessBackend"
              data-testid="peer-generate-code-btn"
              @click="handleGenerateCode"
            >
              <KeyRound :size="13" />
              <span>
                {{ activePairingCodeValid ? t('peers.inbound.regenerateCode') : t('peers.inbound.generateCode') }}
              </span>
            </UiButton>
            <UiButton
              v-if="activePairingCodeValid"
              type="button"
              variant="ghost"
              size="sm"
              :disabled="issuingCode"
              data-testid="peer-revoke-code-btn"
              @click="handleRevokeCode"
            >
              <Ban :size="13" />
              <span>{{ t('peers.inbound.revokeCode') }}</span>
            </UiButton>
          </div>
        </div>

        <div v-if="activePairingCodeValid && pairingCode?.pairing_code" class="peer-code-display">
          <code class="peer-code-digits" data-testid="peer-active-code">{{ pairingCode.pairing_code }}</code>
          <div class="peer-code-badges">
            <UiBadge variant="secondary">{{ pairingCountdownText }}</UiBadge>
            <UiBadge v-if="pairingCode.remaining_attempts != null" variant="outline">
              {{ t('peers.inbound.attemptsRemaining', { count: pairingCode.remaining_attempts }) }}
            </UiBadge>
          </div>
          <div class="peer-inline-controls">
            <UiButton
              type="button"
              variant="outline"
              size="sm"
              @click="copyText(pairingCode.pairing_code, t('peers.copiedPairingCode'))"
            >
              <Copy :size="13" />
              <span>{{ t('peers.inbound.copyCode') }}</span>
            </UiButton>
            <UiButton
              v-if="selectedAddress || inboundSettings.reachable_addresses.length"
              type="button"
              size="sm"
              @click="copyAddressAndCode"
            >
              <Copy :size="13" />
              <span>{{ t('peers.inbound.copyAddressAndCode') }}</span>
            </UiButton>
          </div>
        </div>
        <p v-else class="peer-empty-inline">{{ t('peers.inbound.codeInactive') }}</p>
      </div>
    </section>

    <!-- =======================================================================
         BLOCK 2: Paired Remote Devices (Controller View)
         ======================================================================= -->
    <section class="peer-surface-card" data-testid="peer-block-devices">
      <div class="peer-card-head">
        <MonitorSmartphone :size="16" aria-hidden="true" />
        <div class="peer-card-head-text">
          <h3>{{ t('peers.devices.title') }}</h3>
          <p>{{ t('peers.devices.subtitle') }}</p>
        </div>
        <UiBadge variant="outline">{{ devices.length }}</UiBadge>
      </div>

      <div v-if="devices.length > 0" class="peer-entity-list">
        <article
          v-for="device in devices"
          :key="device.device_id"
          class="peer-entity-card"
          :data-testid="`peer-device-card-${device.device_id}`"
        >
          <div class="peer-entity-top">
            <div class="peer-entity-identity">
              <strong>{{ device.display_name }}</strong>
              <PeerDeviceBadge :state="device.last_state" />
              <UiBadge variant="outline">
                {{ device.transport === 'relay' ? t('peers.devices.transportRelay') : t('peers.devices.transportLan') }}
              </UiBadge>
            </div>

            <div class="peer-entity-actions">
              <UiButton
                v-if="device.last_state !== 'ready'"
                type="button"
                size="sm"
                :disabled="deviceBusyId === device.device_id"
                @click="handleConnectDevice(device)"
              >
                <Plug :size="13" />
                <span>{{ t('peers.devices.connect') }}</span>
              </UiButton>
              <UiButton
                v-else
                type="button"
                variant="outline"
                size="sm"
                :disabled="deviceBusyId === device.device_id"
                @click="handleDisconnectDevice(device)"
              >
                <Unplug :size="13" />
                <span>{{ t('peers.devices.disconnect') }}</span>
              </UiButton>

              <UiButton
                type="button"
                variant="outline"
                size="sm"
                :disabled="deviceBusyId === device.device_id"
                @click="handleProbeExistingDevice(device)"
              >
                <Radar :size="13" />
                <span>{{ t('peers.devices.probe') }}</span>
              </UiButton>

              <UiButton
                type="button"
                variant="outline"
                size="sm"
                :disabled="deviceBusyId === device.device_id"
                data-testid="peer-open-mount-modal-btn"
                @click="openMountModal(device)"
              >
                <FolderSync :size="13" />
                <span>{{ t('peers.devices.mountWorkspaces') }}</span>
              </UiButton>

              <UiButton
                type="button"
                variant="ghost"
                size="sm"
                :disabled="deviceBusyId === device.device_id"
                @click="handleClearCache(device)"
              >
                <Eraser :size="13" />
                <span>{{ t('peers.devices.clearCache') }}</span>
              </UiButton>

              <UiButton
                type="button"
                variant="ghost"
                size="sm"
                :disabled="deviceBusyId === device.device_id"
                @click="handleDeleteDevice(device)"
              >
                <Trash2 :size="13" />
                <span>{{ t('peers.devices.unpair') }}</span>
              </UiButton>
            </div>
          </div>

          <div class="peer-entity-meta-row">
            <span>{{ t('peers.devices.addressLabel') }}: <code>{{ device.address }}</code></span>
            <span>{{ t('peers.devices.lastConnectedLabel', { time: formatTimestamp(device.last_connected_at) }) }}</span>
          </div>

          <div class="peer-entity-fp-line">
            <span class="peer-meta-label">{{ t('peers.devices.fingerprintLabel') }}:</span>
            <code>{{ formatPeerFingerprint(device.certificate_fingerprint) }}</code>
          </div>

          <p
            v-if="device.last_state === 'fingerprint_mismatch'"
            class="peer-callout-danger"
            role="alert"
          >
            <ShieldAlert :size="14" aria-hidden="true" />
            <span>{{ t('peers.devices.mismatchWarning') }}</span>
          </p>
          <p v-else-if="device.last_failure_code" class="peer-callout-danger" role="status">
            <span>{{ errorCodeMessage(device.last_failure_code, undefined, { t, te }) ?? device.last_failure_code }}</span>
          </p>

          <div class="peer-device-toggles">
            <div class="peer-toggle-inline">
              <span>{{ t('peers.devices.autoConnect') }}</span>
              <UiSwitch
                :model-value="device.connection_desired"
                :disabled="deviceBusyId === device.device_id"
                :aria-label="t('peers.devices.autoConnect')"
                @update:model-value="handleToggleDeviceFlag(device, { connection_desired: $event })"
              />
            </div>
            <div class="peer-toggle-inline">
              <span>{{ t('peers.devices.adoptSessions') }}</span>
              <UiSwitch
                :model-value="device.adopt_sessions"
                :disabled="deviceBusyId === device.device_id"
                :aria-label="t('peers.devices.adoptSessions')"
                @update:model-value="handleToggleDeviceFlag(device, { adopt_sessions: $event })"
              />
            </div>
          </div>
        </article>
      </div>

      <p v-else class="peer-empty-card">{{ t('peers.devices.empty') }}</p>
    </section>

    <!-- =======================================================================
         BLOCK 3: Authorized Controllers & Governance Policies (Worker View)
         ======================================================================= -->
    <section class="peer-surface-card" data-testid="peer-block-grants">
      <div class="peer-card-head">
        <ShieldCheck :size="16" aria-hidden="true" />
        <div class="peer-card-head-text">
          <h3>{{ t('peers.grants.title') }}</h3>
          <p>{{ t('peers.grants.subtitle') }}</p>
        </div>
        <UiBadge variant="outline">{{ activeGrants.length }}</UiBadge>
      </div>

      <div v-if="activeGrants.length > 0" class="peer-entity-list">
        <article
          v-for="grant in activeGrants"
          :key="grant.grant_id"
          class="peer-entity-card"
          :data-testid="`peer-grant-card-${grant.controller_device_id}`"
        >
          <div class="peer-entity-top">
            <div class="peer-entity-identity">
              <strong>{{ grant.display_name }}</strong>
              <UiBadge variant="secondary">
                {{ t('peers.grants.revisionBadge', { revision: grant.policy?.revision ?? '1' }) }}
              </UiBadge>
            </div>
            <div class="peer-entity-meta-row">
              <span>{{ t('peers.grants.grantedAtLabel', { time: formatTimestamp(grant.granted_at) }) }}</span>
              <span>{{ t('peers.grants.lastControlledLabel', { time: formatTimestamp(grant.last_controlled_at) }) }}</span>
            </div>
          </div>

          <div class="peer-entity-fp-line">
            <span class="peer-meta-label">{{ t('peers.devices.fingerprintLabel') }}:</span>
            <code>{{ formatPeerFingerprint(grant.certificate_fingerprint) }}</code>
          </div>

          <div class="peer-form-grid">
            <div class="peer-field">
              <UiLabel>{{ t('peers.grants.roleLabel') }}</UiLabel>
              <UiSelectField
                :model-value="ensureGrantDraft(grant).role"
                :options="roleOptions"
                :aria-label="t('peers.grants.roleLabel')"
                :disabled="grantBusyId === grant.controller_device_id"
                @update:model-value="ensureGrantDraft(grant).role = $event as PeerRole"
              />
            </div>

            <div class="peer-field">
              <UiLabel>{{ t('peers.grants.workspaceModeLabel') }}</UiLabel>
              <UiSelectField
                :model-value="ensureGrantDraft(grant).workspace_mode"
                :options="workspaceModeOptions"
                :aria-label="t('peers.grants.workspaceModeLabel')"
                :disabled="grantBusyId === grant.controller_device_id"
                @update:model-value="ensureGrantDraft(grant).workspace_mode = $event as PeerWorkspaceMode"
              />
            </div>
          </div>

          <!-- Selected Workspace Whitelist Multi-Select -->
          <div v-if="ensureGrantDraft(grant).workspace_mode === 'selected'" class="peer-whitelist-box">
            <span class="peer-meta-label">
              {{ t('peers.grants.workspaceWhitelistLabel', { count: ensureGrantDraft(grant).workspace_ids.length }) }}
            </span>
            <div v-if="localProjects.length > 0" class="peer-whitelist-grid">
              <div
                v-for="proj in localProjects"
                :key="proj.id"
                class="peer-whitelist-item"
                @click="toggleGrantWorkspace(grant, proj.id)"
              >
                <UiCheckbox
                  :model-value="ensureGrantDraft(grant).workspace_ids.includes(proj.id)"
                  :aria-label="proj.name"
                  :disabled="grantBusyId === grant.controller_device_id"
                  @update:model-value="toggleGrantWorkspace(grant, proj.id, $event)"
                />
                <div class="peer-whitelist-text">
                  <strong>{{ proj.name }}</strong>
                  <code v-if="proj.path">{{ proj.path }}</code>
                </div>
              </div>
            </div>
            <p v-else class="peer-empty-inline">{{ t('peers.grants.workspaceWhitelistEmpty') }}</p>
          </div>

          <!-- Governance Policy Toggles -->
          <div class="peer-policy-switches">
            <div class="peer-setting-row">
              <div>
                <strong>{{ t('peers.grants.syncAllSessions') }}</strong>
                <p>{{ t('peers.grants.syncAllSessionsHint') }}</p>
              </div>
              <UiSwitch
                :model-value="ensureGrantDraft(grant).sync_all_sessions"
                :disabled="grantBusyId === grant.controller_device_id"
                :aria-label="t('peers.grants.syncAllSessions')"
                @update:model-value="ensureGrantDraft(grant).sync_all_sessions = $event"
              />
            </div>

            <div class="peer-setting-row">
              <div>
                <strong>{{ t('peers.grants.allowAgentDelegation') }}</strong>
                <p>{{ t('peers.grants.allowAgentDelegationHint') }}</p>
              </div>
              <UiSwitch
                :model-value="ensureGrantDraft(grant).allow_agent_delegation"
                :disabled="grantBusyId === grant.controller_device_id"
                :aria-label="t('peers.grants.allowAgentDelegation')"
                @update:model-value="ensureGrantDraft(grant).allow_agent_delegation = $event"
              />
            </div>

            <div class="peer-setting-row">
              <div>
                <strong>{{ t('peers.grants.allowProviderProxy') }}</strong>
                <p>{{ t('peers.grants.allowProviderProxyHint') }}</p>
              </div>
              <UiSwitch
                :model-value="ensureGrantDraft(grant).allow_provider_proxy"
                :disabled="grantBusyId === grant.controller_device_id"
                :aria-label="t('peers.grants.allowProviderProxy')"
                @update:model-value="ensureGrantDraft(grant).allow_provider_proxy = $event"
              />
            </div>
          </div>

          <footer class="peer-grant-footer">
            <UiButton
              type="button"
              variant="outline"
              size="sm"
              :disabled="grantBusyId === grant.controller_device_id"
              data-testid="peer-revoke-grant-btn"
              @click="handleRevokeGrant(grant)"
            >
              <Ban :size="13" />
              <span>{{ t('peers.grants.revokeBtn') }}</span>
            </UiButton>
            <UiButton
              type="button"
              size="sm"
              :disabled="grantBusyId === grant.controller_device_id"
              data-testid="peer-save-grant-policy-btn"
              @click="handleSaveGrantPolicy(grant)"
            >
              <Save :size="13" />
              <span>{{ t('peers.grants.savePolicy') }}</span>
            </UiButton>
          </footer>
        </article>
      </div>

      <p v-else class="peer-empty-card">{{ t('peers.grants.empty') }}</p>
    </section>

    <PeerPairingModal
      v-model:open="pairingModalOpen"
      @paired="loadEverything"
    />

    <PeerMountWorkspaceModal
      v-model:open="mountModalOpen"
      :device="mountTargetDevice"
      @saved="loadEverything"
    />
  </section>
</template>
