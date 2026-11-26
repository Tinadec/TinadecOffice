import { computed, ref, type ComputedRef, type Ref } from 'vue'
import {
  api,
  type PairPeerDeviceInput,
  type PeerAccessPolicyDto,
  type PeerConnectionState,
  type PeerDeviceDto,
  type PeerGrantDto,
  type PeerInboundSettingsDto,
  type PeerMountedWorkspaceDto,
  type PeerPairingCodeDto,
  type PeerProbeResultDto,
  type PeerRemoteWorkspaceDto,
  type PeerRole,
  type PeerTransportKind,
  type PeerWorkspaceMode,
  type PeerWorkspaceMountInput,
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
const remoteWorkspacesCache = new Map<string, PeerRemoteWorkspaceDto[]>()

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
  remoteWorkspacesCache.clear()
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
  activeEndpoint: ComputedRef<EndpointScope>
  activeEndpointId: ComputedRef<EndpointId>
  activePeerDevice: ComputedRef<PeerDeviceDto | null>
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
  clearDeviceLocalCache: (deviceId: string) => Promise<void>
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
      inboundSettings.value = inbound
      pairingCode.value = codeState.active && codeState.pairing_code ? codeState : null
      devices.value = deviceList
      grants.value = grantList
      for (const device of deviceList) {
        if (device.mounted_workspaces) {
          remoteWorkspacesCache.set(device.device_id, device.mounted_workspaces)
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
      remoteWorkspacesCache.delete(deviceId)
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

  async function clearDeviceLocalCache(deviceId: string): Promise<void> {
    error.value = null
    try {
      await api.clearPeerDeviceLocalCache(deviceId)
      purgeFrontendPeerEndpointCaches(deviceId)
      remoteWorkspacesCache.delete(deviceId)
      devices.value = devices.value.map((item) =>
        item.device_id === deviceId ? { ...item, mounted_workspaces: [] } : item,
      )
      rebuildMountedWorkspaces()
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
    activeEndpoint,
    activeEndpointId,
    activePeerDevice,
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
    clearDeviceLocalCache,
    updateGrantPolicy,
    revokeGrant,
    switchEndpoint,
  }
}
