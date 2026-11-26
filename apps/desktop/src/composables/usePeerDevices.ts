import { computed, ref, type ComputedRef, type Ref } from 'vue'
import {
  api,
  clearLocalPeerShadowSnapshots,
  registerPeerOfflineShadowHooks,
  type PairPeerDeviceInput,
  type PeerAccessPolicyDto,
  type PeerConnectionState,
  type PeerDeviceDto,
  type PeerDevServerLeaseDto,
  type PeerGrantDto,
  type PeerInboundSettingsDto,
  type PeerMountedWorkspaceDto,
  type PeerPairingCodeDto,
  type PeerProbeResultDto,
  type PeerRemoteWorkspaceDto,
  type PeerRole,
  type PeerShadowSessionDetailDto,
  type PeerShadowSessionDto,
  type PeerShadowSyncInput,
  type PeerTransportKind,
  type PeerWorkspaceMode,
  type PeerWorkspaceMountInput,
  type RequestPeerDevServerLeaseInput,
  type UpdatePeerDeviceInput,
  type UpdatePeerGrantPolicyInput,
  type UpdatePeerInboundInput,
} from '@/api'
import {
  activeEndpointScope,
  clearEndpointEventCursors,
  setActiveEndpoint,
  toEndpointId,
  type EndpointId,
  type EndpointInput,
  type EndpointScope,
} from '@/lib/endpointScope'
import {
  clearUncertainSubmissionsForDevice,
  reconcileDeviceUncertainSubmissions,
} from '@/lib/peerSubmissionReconcile'
import { invalidateEndpointReadCache } from '@/lib/readCache'
import { clearEndpointStorageScope } from '@/lib/storageScope'

export type {
  EndpointScope,
  EndpointId,
  EndpointInput,
  PeerTransportKind,
  PeerRole,
  PeerWorkspaceMode,
  PeerConnectionState,
  PeerInboundSettingsDto,
  UpdatePeerInboundInput,
  PeerPairingCodeDto,
  PeerProbeResultDto,
  PairPeerDeviceInput,
  UpdatePeerDeviceInput,
  PeerDeviceDto,
  PeerRemoteWorkspaceDto,
  PeerWorkspaceMountInput,
  PeerMountedWorkspaceDto,
  PeerAccessPolicyDto,
  UpdatePeerGrantPolicyInput,
  PeerGrantDto,
  PeerShadowSessionDto,
  PeerShadowSessionDetailDto,
  PeerShadowSyncInput,
  PeerDevServerLeaseDto,
  RequestPeerDevServerLeaseInput,
}

function defaultInboundSettings(): PeerInboundSettingsDto {
  return {
    enabled: false,
    listening: false,
    display_name: '',
    listen_port: 48735,
    configured_port: 48735,
    port_conflict: false,
    local_device_id: '',
    local_fingerprint: '',
    reachable_addresses: [],
    firewall_hint: null,
  }
}

/**
 * Formats a 64-char SHA-256 hex certificate fingerprint into 4-char monospace blocks
 * for side-by-side manual verification during pairing and governance audits.
 */
export function formatPeerFingerprint(fingerprint: string | null | undefined): string {
  const clean = (fingerprint ?? '').replace(/[^a-fA-F0-9]/g, '').toLowerCase()
  if (!clean) return ''
  return clean.match(/.{1,4}/g)?.join(' ') ?? clean
}

const loading = ref(false)
const error = ref<string | null>(null)
const inboundSettings = ref<PeerInboundSettingsDto>(defaultInboundSettings())
const pairingCode = ref<PeerPairingCodeDto | null>(null)
const devices = ref<PeerDeviceDto[]>([])
const grants = ref<PeerGrantDto[]>([])
const mountedWorkspaces = ref<PeerMountedWorkspaceDto[]>([])
const devServerLeases = ref<PeerDevServerLeaseDto[]>([])
const shadowFallbackDevices = ref<Record<string, boolean>>({})
const lastShadowSyncedAtByDevice = ref<Record<string, number>>({})
const remoteWorkspacesCache = new Map<string, PeerRemoteWorkspaceDto[]>()

function installOfflineShadowHooks(): void {
  registerPeerOfflineShadowHooks({
    isDeviceOffline: (deviceId: string) => {
      const found = devices.value.find((item) => item.device_id === deviceId)
      if (!found) return Boolean(shadowFallbackDevices.value[deviceId])
      return found.last_state !== 'ready' || Boolean(shadowFallbackDevices.value[deviceId])
    },
    onShadowFallback: (deviceId: string, active: boolean, syncedAt?: number | null) => {
      if (Boolean(shadowFallbackDevices.value[deviceId]) !== active) {
        shadowFallbackDevices.value = { ...shadowFallbackDevices.value, [deviceId]: active }
      }
      if (typeof syncedAt === 'number' && Number.isFinite(syncedAt) && syncedAt > 0) {
        lastShadowSyncedAtByDevice.value = { ...lastShadowSyncedAtByDevice.value, [deviceId]: syncedAt }
      }
    },
  })
}

installOfflineShadowHooks()

function rebuildMountedWorkspaces(): void {
  const rows: PeerMountedWorkspaceDto[] = []
  for (const device of devices.value) {
    const known = remoteWorkspacesCache.get(device.device_id) ?? device.mounted_workspaces ?? []
    for (const ws of known) {
      const visible = ws.visible ?? ws.mounted
      if (!ws.mounted || !visible) continue
      rows.push({
        alias_workspace_id: ws.alias_workspace_id || `peer:${device.device_id}:${ws.remote_project_id}`,
        peer_device_id: device.device_id,
        device_display_name: device.display_name || device.address || device.device_id,
        device_state: device.last_state,
        offline_readonly: device.last_state !== 'ready',
        remote_project_id: ws.remote_project_id,
        name: ws.name,
        remote_root_path: ws.remote_root_path,
        visible: true,
        synced_at: ws.synced_at ?? Date.now(),
      })
    }
  }
  mountedWorkspaces.value = rows
}

function upsertDeviceInList(updated: PeerDeviceDto): PeerDeviceDto {
  const prev = devices.value.find((item) => item.device_id === updated.device_id)
  const idx = devices.value.findIndex((item) => item.device_id === updated.device_id)
  if (idx >= 0) {
    const next = [...devices.value]
    next[idx] = updated
    devices.value = next
  } else {
    devices.value = [updated, ...devices.value]
  }
  if (updated.mounted_workspaces) {
    remoteWorkspacesCache.set(updated.device_id, updated.mounted_workspaces)
  }
  if (updated.last_state === 'ready') {
    if (shadowFallbackDevices.value[updated.device_id]) {
      shadowFallbackDevices.value = { ...shadowFallbackDevices.value, [updated.device_id]: false }
    }
    if (!prev || prev.last_state !== 'ready') {
      void reconcileDeviceUncertainSubmissions(updated.device_id)
    }
  } else {
    // Severed or disconnected peer immediately invalidates local DevServer leases in UI state
    devServerLeases.value = devServerLeases.value.filter((lease) => lease.device_id !== updated.device_id)
  }
  rebuildMountedWorkspaces()
  return updated
}

function setErrorFrom(caught: unknown): never {
  error.value = caught instanceof Error ? caught.message : String(caught)
  throw caught
}

export function resetPeerDevicesStateForTests(): void {
  loading.value = false
  error.value = null
  inboundSettings.value = defaultInboundSettings()
  pairingCode.value = null
  devices.value = []
  grants.value = []
  mountedWorkspaces.value = []
  devServerLeases.value = []
  shadowFallbackDevices.value = {}
  lastShadowSyncedAtByDevice.value = {}
  remoteWorkspacesCache.clear()
  clearLocalPeerShadowSnapshots()
  installOfflineShadowHooks()
  setActiveEndpoint('local')
}

export function usePeerDevices(): {
  loading: Ref<boolean>
  error: Ref<string | null>
  inboundSettings: Ref<PeerInboundSettingsDto>
  pairingCode: Ref<PeerPairingCodeDto | null>
  devices: Ref<PeerDeviceDto[]>
  grants: Ref<PeerGrantDto[]>
  mountedWorkspaces: Ref<PeerMountedWorkspaceDto[]>
  devServerLeases: Ref<PeerDevServerLeaseDto[]>
  activeEndpoint: ComputedRef<EndpointScope>
  activeEndpointId: ComputedRef<EndpointId>
  activePeerDevice: ComputedRef<PeerDeviceDto | null>
  isActivePeerOfflineReadonly: ComputedRef<boolean>
  activePeerLastSyncedAt: ComputedRef<number | null>
  activePeerDevServerLeases: ComputedRef<PeerDevServerLeaseDto[]>
  refreshAll: () => Promise<void>
  updateInbound: (patch: UpdatePeerInboundInput) => Promise<PeerInboundSettingsDto>
  generatePairingCode: (options?: { ttl_seconds?: number; relay_enrolled?: boolean }) => Promise<PeerPairingCodeDto>
  revokePairingCode: () => Promise<void>
  probeDevice: (addressOrDeviceId: string) => Promise<PeerProbeResultDto>
  pairDevice: (input: PairPeerDeviceInput) => Promise<PeerDeviceDto>
  connectDevice: (deviceId: string) => Promise<PeerDeviceDto>
  disconnectDevice: (deviceId: string) => Promise<PeerDeviceDto>
  updateDevice: (deviceId: string, patch: UpdatePeerDeviceInput) => Promise<PeerDeviceDto>
  deleteDevice: (deviceId: string) => Promise<void>
  fetchRemoteWorkspaces: (deviceId: string) => Promise<PeerRemoteWorkspaceDto[]>
  mountRemoteWorkspaces: (deviceId: string, mounts: PeerWorkspaceMountInput[]) => Promise<PeerRemoteWorkspaceDto[]>
  fetchShadowSessions: (deviceId: string, projectId?: string | null) => Promise<PeerShadowSessionDto[]>
  fetchShadowSessionDetail: (deviceId: string, remoteSessionId: string) => Promise<PeerShadowSessionDetailDto>
  syncShadowSnapshot: (deviceId: string, input: PeerShadowSyncInput) => Promise<void>
  clearDeviceLocalCache: (deviceId: string) => Promise<void>
  requestDevServerLease: (
    deviceId: string,
    targetPort: number,
    options?: { ttl_seconds?: number; lease_id?: string; target_host?: '127.0.0.1' | '::1' | 'localhost' },
  ) => Promise<PeerDevServerLeaseDto>
  releaseDevServerLease: (deviceId: string, leaseId: string) => Promise<void>
  updateGrantPolicy: (controllerDeviceId: string, policy: UpdatePeerGrantPolicyInput) => Promise<PeerGrantDto>
  revokeGrant: (controllerDeviceId: string) => Promise<void>
  switchEndpoint: (endpoint: EndpointInput) => EndpointScope
} {
  const activeEndpoint = computed<EndpointScope>(() => activeEndpointScope.value)
  const activeEndpointId = computed<EndpointId>(() => toEndpointId(activeEndpointScope.value))
  const activePeerDevice = computed<PeerDeviceDto | null>(() => {
    const scope = activeEndpointScope.value
    if (scope.kind !== 'peer') return null
    return devices.value.find((item) => item.device_id === scope.deviceId) ?? null
  })
  const isActivePeerOfflineReadonly = computed<boolean>(() => {
    const scope = activeEndpointScope.value
    if (scope.kind !== 'peer') return false
    const dev = activePeerDevice.value
    if (shadowFallbackDevices.value[scope.deviceId]) return true
    return dev ? dev.last_state !== 'ready' : false
  })
  const activePeerLastSyncedAt = computed<number | null>(() => {
    const scope = activeEndpointScope.value
    if (scope.kind !== 'peer') return null
    const explicit = lastShadowSyncedAtByDevice.value[scope.deviceId]
    if (explicit && explicit > 0) return explicit
    const mountSync = mountedWorkspaces.value
      .filter((ws) => ws.peer_device_id === scope.deviceId)
      .reduce((acc, ws) => Math.max(acc, ws.synced_at || 0), 0)
    if (mountSync > 0) return mountSync
    return activePeerDevice.value?.last_connected_at ?? null
  })
  const activePeerDevServerLeases = computed<PeerDevServerLeaseDto[]>(() => {
    const scope = activeEndpointScope.value
    if (scope.kind !== 'peer') return []
    return devServerLeases.value.filter((item) => item.device_id === scope.deviceId)
  })

  async function refreshAll(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      const [inbound, codeState, deviceList, grantList] = await Promise.all([
        api.getPeerInboundSettings(),
        api.getPeerPairingCode(),
        api.listPeerDevices(),
        api.listPeerGrants(),
      ])
      const previousReady = new Set(
        devices.value.filter((item) => item.last_state === 'ready').map((item) => item.device_id),
      )
      inboundSettings.value = inbound
      pairingCode.value = codeState.active && codeState.pairing_code ? codeState : null
      devices.value = deviceList
      grants.value = grantList
      for (const device of deviceList) {
        if (device.mounted_workspaces) {
          remoteWorkspacesCache.set(device.device_id, device.mounted_workspaces)
        }
        if (device.last_state === 'ready') {
          if (shadowFallbackDevices.value[device.device_id]) {
            shadowFallbackDevices.value = { ...shadowFallbackDevices.value, [device.device_id]: false }
          }
          if (!previousReady.has(device.device_id)) {
            void reconcileDeviceUncertainSubmissions(device.device_id)
          }
        }
      }
      await Promise.all(
        deviceList.map(async (device) => {
          try {
            const wsList = await api.listPeerRemoteWorkspaces(device.device_id)
            remoteWorkspacesCache.set(device.device_id, wsList)
          } catch {
            // Offline peers may fail live workspace fetch; preserve previously synced shadow mounts.
          }
        }),
      )
      rebuildMountedWorkspaces()
    } catch (caught) {
      setErrorFrom(caught)
    } finally {
      loading.value = false
    }
  }

  async function updateInbound(patch: UpdatePeerInboundInput): Promise<PeerInboundSettingsDto> {
    error.value = null
    try {
      const next = await api.updatePeerInboundSettings(patch)
      inboundSettings.value = next
      return next
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function generatePairingCode(options?: {
    ttl_seconds?: number
    relay_enrolled?: boolean
  }): Promise<PeerPairingCodeDto> {
    error.value = null
    try {
      const created = await api.generatePeerPairingCode(options)
      pairingCode.value = created.active && created.pairing_code ? created : created
      return created
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function revokePairingCode(): Promise<void> {
    error.value = null
    try {
      await api.revokePeerPairingCode()
      pairingCode.value = null
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function probeDevice(addressOrDeviceId: string): Promise<PeerProbeResultDto> {
    error.value = null
    try {
      const isKnownDeviceId = devices.value.some((item) => item.device_id === addressOrDeviceId)
      return await api.probePeerDevice(addressOrDeviceId, { byDeviceId: isKnownDeviceId })
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function pairDevice(input: PairPeerDeviceInput): Promise<PeerDeviceDto> {
    error.value = null
    try {
      const paired = await api.pairPeerDevice(input)
      upsertDeviceInList(paired)
      if (paired.adopt_sessions) {
        try {
          const wsList = await api.listPeerRemoteWorkspaces(paired.device_id)
          remoteWorkspacesCache.set(paired.device_id, wsList)
          rebuildMountedWorkspaces()
        } catch {
          // Non-fatal if remote workspace list requires explicit mount.
        }
      }
      return paired
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function connectDevice(deviceId: string): Promise<PeerDeviceDto> {
    error.value = null
    try {
      const connected = await api.connectPeerDevice(deviceId)
      return upsertDeviceInList(connected)
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function disconnectDevice(deviceId: string): Promise<PeerDeviceDto> {
    error.value = null
    try {
      const disconnected = await api.disconnectPeerDevice(deviceId)
      return upsertDeviceInList(disconnected)
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function updateDevice(deviceId: string, patch: UpdatePeerDeviceInput): Promise<PeerDeviceDto> {
    error.value = null
    try {
      const updated = await api.updatePeerDevice(deviceId, patch)
      return upsertDeviceInList(updated)
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  function purgeFrontendPeerEndpointCaches(deviceId: string): void {
    const peerScope: EndpointScope = { kind: 'peer', deviceId }
    invalidateEndpointReadCache(peerScope)
    clearEndpointStorageScope(peerScope)
    clearEndpointEventCursors(peerScope)
  }

  async function deleteDevice(deviceId: string): Promise<void> {
    error.value = null
    try {
      await api.deletePeerDevice(deviceId)
      devices.value = devices.value.filter((item) => item.device_id !== deviceId)
      devServerLeases.value = devServerLeases.value.filter((lease) => lease.device_id !== deviceId)
      remoteWorkspacesCache.delete(deviceId)
      clearUncertainSubmissionsForDevice(deviceId)
      clearLocalPeerShadowSnapshots(deviceId)
      purgeFrontendPeerEndpointCaches(deviceId)
      if (activeEndpointScope.value.kind === 'peer' && activeEndpointScope.value.deviceId === deviceId) {
        setActiveEndpoint('local')
      }
      rebuildMountedWorkspaces()
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function fetchRemoteWorkspaces(deviceId: string): Promise<PeerRemoteWorkspaceDto[]> {
    error.value = null
    try {
      const list = await api.listPeerRemoteWorkspaces(deviceId)
      remoteWorkspacesCache.set(deviceId, list)
      rebuildMountedWorkspaces()
      return list
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function mountRemoteWorkspaces(
    deviceId: string,
    mounts: PeerWorkspaceMountInput[],
  ): Promise<PeerRemoteWorkspaceDto[]> {
    error.value = null
    try {
      const updated = await api.mountPeerRemoteWorkspaces(deviceId, mounts)
      remoteWorkspacesCache.set(deviceId, updated)
      rebuildMountedWorkspaces()
      return updated
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function fetchShadowSessions(
    deviceId: string,
    projectId?: string | null,
  ): Promise<PeerShadowSessionDto[]> {
    error.value = null
    try {
      const rows = await api.listPeerShadowSessions(deviceId, { projectId })
      const latest = rows.reduce((acc, row) => Math.max(acc, row.last_synced_at || 0), 0)
      if (latest > 0) {
        lastShadowSyncedAtByDevice.value = { ...lastShadowSyncedAtByDevice.value, [deviceId]: latest }
      }
      return rows
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function fetchShadowSessionDetail(
    deviceId: string,
    remoteSessionId: string,
  ): Promise<PeerShadowSessionDetailDto> {
    error.value = null
    try {
      const detail = await api.getPeerShadowSession(deviceId, remoteSessionId)
      if (detail.session.last_synced_at > 0) {
        lastShadowSyncedAtByDevice.value = {
          ...lastShadowSyncedAtByDevice.value,
          [deviceId]: detail.session.last_synced_at,
        }
      }
      return detail
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function syncShadowSnapshot(deviceId: string, input: PeerShadowSyncInput): Promise<void> {
    error.value = null
    try {
      await api.syncPeerShadow(deviceId, input)
      lastShadowSyncedAtByDevice.value = { ...lastShadowSyncedAtByDevice.value, [deviceId]: Date.now() }
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function clearDeviceLocalCache(deviceId: string): Promise<void> {
    error.value = null
    try {
      await api.clearPeerDeviceLocalCache(deviceId)
      purgeFrontendPeerEndpointCaches(deviceId)
      remoteWorkspacesCache.delete(deviceId)
      const nextSynced = { ...lastShadowSyncedAtByDevice.value }
      delete nextSynced[deviceId]
      lastShadowSyncedAtByDevice.value = nextSynced
      devices.value = devices.value.map((item) =>
        item.device_id === deviceId ? { ...item, mounted_workspaces: [] } : item,
      )
      rebuildMountedWorkspaces()
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function requestDevServerLease(
    deviceId: string,
    targetPort: number,
    options?: { ttl_seconds?: number; lease_id?: string; target_host?: '127.0.0.1' | '::1' | 'localhost' },
  ): Promise<PeerDevServerLeaseDto> {
    error.value = null
    try {
      const lease = await api.requestPeerDevServerLease(deviceId, {
        target_port: targetPort,
        ...(options?.target_host ? { target_host: options.target_host } : {}),
        ...(options?.ttl_seconds !== undefined ? { ttl_seconds: options.ttl_seconds } : {}),
        ...(options?.lease_id ? { lease_id: options.lease_id } : {}),
      })
      const filtered = devServerLeases.value.filter(
        (item) =>
          item.lease_id !== lease.lease_id &&
          !(item.device_id === deviceId && item.target_port === lease.target_port),
      )
      devServerLeases.value = [lease, ...filtered]
      return lease
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function releaseDevServerLease(deviceId: string, leaseId: string): Promise<void> {
    error.value = null
    try {
      await api.releasePeerDevServerLease(deviceId, leaseId)
      devServerLeases.value = devServerLeases.value.filter(
        (item) => !(item.device_id === deviceId && item.lease_id === leaseId),
      )
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function updateGrantPolicy(
    controllerDeviceId: string,
    policy: UpdatePeerGrantPolicyInput,
  ): Promise<PeerGrantDto> {
    error.value = null
    try {
      const updated = await api.updatePeerGrantPolicy(controllerDeviceId, policy)
      const idx = grants.value.findIndex((item) => item.controller_device_id === controllerDeviceId)
      if (idx >= 0) {
        const next = [...grants.value]
        next[idx] = updated
        grants.value = next
      } else {
        grants.value = [updated, ...grants.value]
      }
      return updated
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  async function revokeGrant(controllerDeviceId: string): Promise<void> {
    error.value = null
    try {
      await api.revokePeerGrant(controllerDeviceId)
      grants.value = grants.value.filter((item) => item.controller_device_id !== controllerDeviceId)
    } catch (caught) {
      setErrorFrom(caught)
    }
  }

  function switchEndpoint(endpoint: EndpointInput): EndpointScope {
    return setActiveEndpoint(endpoint)
  }

  return {
    loading,
    error,
    inboundSettings,
    pairingCode,
    devices,
    grants,
    mountedWorkspaces,
    devServerLeases,
    activeEndpoint,
    activeEndpointId,
    activePeerDevice,
    isActivePeerOfflineReadonly,
    activePeerLastSyncedAt,
    activePeerDevServerLeases,
    refreshAll,
    updateInbound,
    generatePairingCode,
    revokePairingCode,
    probeDevice,
    pairDevice,
    connectDevice,
    disconnectDevice,
    updateDevice,
    deleteDevice,
    fetchRemoteWorkspaces,
    mountRemoteWorkspaces,
    fetchShadowSessions,
    fetchShadowSessionDetail,
    syncShadowSnapshot,
    clearDeviceLocalCache,
    requestDevServerLease,
    releaseDevServerLease,
    updateGrantPolicy,
    revokeGrant,
    switchEndpoint,
  }
}
