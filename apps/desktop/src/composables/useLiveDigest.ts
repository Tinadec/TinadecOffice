import {
  computed,
  getCurrentScope,
  onScopeDispose,
  ref,
  shallowRef,
  toValue,
  watch,
  type ComputedRef,
  type MaybeRefOrGetter,
  type Ref,
} from 'vue'
import {
  applyLiveDigestDelta,
  getLiveDigestSettings,
  getRunLiveDigest,
  isTerminalLiveDigestStatus,
  openLiveDigestStream,
  updateLiveDigestSettings,
  type LiveDigestChapterDto,
  type LiveDigestContextWaterlineDto,
  type LiveDigestDeltaDto,
  type LiveDigestGovernanceSignalDto,
  type LiveDigestMode,
  type LiveDigestPhase,
  type LiveDigestSentinelAlertDto,
  type LiveDigestSettingsDto,
  type LiveDigestSnapshotDto,
  type LiveDigestStreamEventName,
  type LiveDigestStreamHandle,
  type LiveDigestTeammateThoughtDto,
  type LiveDigestTouchedFileDto,
  type UpdateLiveDigestSettingsInput,
} from '@/lib/liveDigestApi'

export type {
  LiveDigestChapterDto,
  LiveDigestContextWaterlineDto,
  LiveDigestDeltaDto,
  LiveDigestGovernanceSignalDto,
  LiveDigestMode,
  LiveDigestPhase,
  LiveDigestSentinelAlertDto,
  LiveDigestSettingsDto,
  LiveDigestSnapshotDto,
  LiveDigestStreamEventName,
  LiveDigestTeammateThoughtDto,
  LiveDigestTouchedFileDto,
  UpdateLiveDigestSettingsInput,
}

export type LiveDigestConnectionState =
  | 'idle'
  | 'connecting'
  | 'live'
  | 'reconnecting'
  | 'done'
  | 'error'

export interface UseLiveDigestOptions {
  /** Whether to automatically subscribe to the SSE stream for non-terminal runs. Defaults to true. */
  autoStream?: MaybeRefOrGetter<boolean>
  /** Optional storage scope override when runId does not encode a storage prefix. */
  storageId?: MaybeRefOrGetter<string | undefined>
  /** Optional event callback for consumers wanting side effects on each stream frame. */
  onEvent?: (event: LiveDigestStreamEventName, payload: LiveDigestSnapshotDto | LiveDigestDeltaDto) => void
}

export interface UseLiveDigestReturn {
  runId: ComputedRef<string | null>
  snapshot: ComputedRef<LiveDigestSnapshotDto | null>
  loading: ComputedRef<boolean>
  connected: ComputedRef<boolean>
  streaming: ComputedRef<boolean>
  connectionState: ComputedRef<LiveDigestConnectionState>
  error: ComputedRef<string | null>
  isRunning: ComputedRef<boolean>
  isTerminal: ComputedRef<boolean>
  headline: ComputedRef<string>
  currentPhase: ComputedRef<LiveDigestPhase | null>
  mode: ComputedRef<LiveDigestMode>
  explainerActive: ComputedRef<boolean>
  explainerModel: ComputedRef<string | null>
  contextWaterline: ComputedRef<LiveDigestContextWaterlineDto | null>
  governanceSignals: ComputedRef<LiveDigestGovernanceSignalDto[]>
  teammateThoughts: ComputedRef<LiveDigestTeammateThoughtDto[]>
  teammateThoughtByHandle: ComputedRef<Record<string, LiveDigestTeammateThoughtDto>>
  chapters: ComputedRef<LiveDigestChapterDto[]>
  touchedFiles: ComputedRef<LiveDigestTouchedFileDto[]>
  sentinelAlerts: ComputedRef<LiveDigestSentinelAlertDto[]>
  lastEventType: ComputedRef<LiveDigestStreamEventName | null>
  refresh: () => Promise<LiveDigestSnapshotDto | null>
  reconnect: () => void
  disconnect: () => void
}

// ---------------------------------------------------------------------------
// Reference-counted per-run hub so flat panel, heartbeat strip, and spatial
// projections share a single SSE stream & reactive snapshot per runId.
// ---------------------------------------------------------------------------

interface LiveDigestHubEntry {
  runId: string
  storageId?: string
  snapshot: Ref<LiveDigestSnapshotDto | null>
  loading: Ref<boolean>
  connectionState: Ref<LiveDigestConnectionState>
  error: Ref<string | null>
  lastEventType: Ref<LiveDigestStreamEventName | null>
  refCount: number
  streamRefCount: number
  stream: LiveDigestStreamHandle | null
  inflightFetch: Promise<LiveDigestSnapshotDto | null> | null
  listeners: Set<(event: LiveDigestStreamEventName, payload: LiveDigestSnapshotDto | LiveDigestDeltaDto) => void>
  touchedAt: number
}

const MAX_CACHED_ENTRIES = 24
const hubEntries = new Map<string, LiveDigestHubEntry>()

function normalizeRunKey(runId: string | null | undefined): string | null {
  if (typeof runId !== 'string') return null
  const trimmed = runId.trim()
  return trimmed.length > 0 ? trimmed : null
}

function pruneIdleCache(): void {
  if (hubEntries.size <= MAX_CACHED_ENTRIES) return
  const idle = [...hubEntries.values()]
    .filter((entry) => entry.refCount === 0 && !entry.stream)
    .sort((a, b) => a.touchedAt - b.touchedAt)
  for (const entry of idle) {
    if (hubEntries.size <= MAX_CACHED_ENTRIES) break
    hubEntries.delete(entry.runId)
  }
}

function getOrCreateEntry(runId: string, storageId?: string): LiveDigestHubEntry {
  let entry = hubEntries.get(runId)
  if (!entry) {
    entry = {
      runId,
      storageId,
      snapshot: shallowRef<LiveDigestSnapshotDto | null>(null),
      loading: ref(false),
      connectionState: ref<LiveDigestConnectionState>('idle'),
      error: ref<string | null>(null),
      lastEventType: ref<LiveDigestStreamEventName | null>(null),
      refCount: 0,
      streamRefCount: 0,
      stream: null,
      inflightFetch: null,
      listeners: new Set(),
      touchedAt: Date.now(),
    }
    hubEntries.set(runId, entry)
    pruneIdleCache()
  } else {
    entry.touchedAt = Date.now()
    if (storageId && !entry.storageId) entry.storageId = storageId
  }
  return entry
}

function notifyEntryListeners(
  entry: LiveDigestHubEntry,
  event: LiveDigestStreamEventName,
  payload: LiveDigestSnapshotDto | LiveDigestDeltaDto,
): void {
  entry.lastEventType.value = event
  entry.touchedAt = Date.now()
  for (const listener of entry.listeners) {
    try {
      listener(event, payload)
    } catch {
      // Consumer callbacks must not disrupt shared stream delivery.
    }
  }
}

function closeEntryStream(entry: LiveDigestHubEntry): void {
  if (entry.stream) {
    entry.stream.close()
    entry.stream = null
  }
}

async function fetchEntrySnapshot(entry: LiveDigestHubEntry, force = false): Promise<LiveDigestSnapshotDto | null> {
  if (entry.inflightFetch && !force) return entry.inflightFetch
  entry.loading.value = true
  const task = getRunLiveDigest(entry.runId, entry.storageId ? { storageId: entry.storageId } : undefined)
    .then((snap) => {
      if (snap) {
        const current = entry.snapshot.value
        if (!current || snap.seq >= current.seq) {
          entry.snapshot.value = snap
        }
        entry.error.value = null
        if (isTerminalLiveDigestStatus(snap.status)) {
          entry.connectionState.value = 'done'
          closeEntryStream(entry)
        }
      }
      return entry.snapshot.value
    })
    .catch((err: unknown) => {
      const message = err instanceof Error ? err.message : String(err)
      if (!entry.snapshot.value) {
        entry.error.value = message
      }
      return entry.snapshot.value
    })
    .finally(() => {
      entry.loading.value = false
      if (entry.inflightFetch === task) entry.inflightFetch = null
    })
  entry.inflightFetch = task
  return task
}

function startEntryStream(entry: LiveDigestHubEntry, forceRestart = false): void {
  if (entry.stream && !entry.stream.closed) {
    if (!forceRestart) return
    closeEntryStream(entry)
  }
  if (entry.snapshot.value && isTerminalLiveDigestStatus(entry.snapshot.value.status) && !forceRestart) {
    entry.connectionState.value = 'done'
    return
  }

  entry.connectionState.value = entry.snapshot.value ? 'reconnecting' : 'connecting'
  const afterSeq = entry.snapshot.value?.seq ?? 0

  entry.stream = openLiveDigestStream(
    entry.runId,
    {
      onOpen: () => {
        entry.connectionState.value = 'live'
        entry.error.value = null
      },
      onSnapshot: (snap) => {
        const current = entry.snapshot.value
        if (!current || snap.seq >= current.seq) {
          entry.snapshot.value = snap
        }
        entry.loading.value = false
        entry.error.value = null
        if (isTerminalLiveDigestStatus(snap.status)) {
          entry.connectionState.value = 'done'
        } else {
          entry.connectionState.value = 'live'
        }
        notifyEntryListeners(entry, 'snapshot', snap)
      },
      onL0Delta: (delta) => {
        entry.snapshot.value = applyLiveDigestDelta(entry.snapshot.value, delta)
        entry.loading.value = false
        entry.error.value = null
        entry.connectionState.value = isTerminalLiveDigestStatus(entry.snapshot.value.status)
          ? 'done'
          : 'live'
        notifyEntryListeners(entry, 'l0_delta', delta)
      },
      onL1Narration: (delta) => {
        entry.snapshot.value = applyLiveDigestDelta(entry.snapshot.value, delta)
        entry.loading.value = false
        entry.error.value = null
        entry.connectionState.value = isTerminalLiveDigestStatus(entry.snapshot.value.status)
          ? 'done'
          : 'live'
        notifyEntryListeners(entry, 'l1_narration', delta)
      },
      onDone: (snap) => {
        entry.snapshot.value = snap
        entry.loading.value = false
        entry.error.value = null
        entry.connectionState.value = 'done'
        entry.stream = null
        notifyEntryListeners(entry, 'done', snap)
      },
      onError: (err) => {
        entry.error.value = err.message
        entry.connectionState.value = entry.snapshot.value ? 'reconnecting' : 'error'
      },
    },
    {
      afterSeq,
      storageId: entry.storageId,
    },
  )
}

/**
 * Pick the most relevant run for Live Digest from a session's run list:
 * prefers active/running/awaiting_approval runs, falling back to the latest completed run for replay.
 */
export function selectPrimaryRunId(
  runs: ReadonlyArray<{ id: string; status?: string | null; updated_at?: string | null; created_at?: string | null }> | null | undefined,
): string | null {
  if (!Array.isArray(runs) || runs.length === 0) return null
  const active = runs.filter((run) => !isTerminalLiveDigestStatus(run.status))
  if (active.length > 0) {
    return active[active.length - 1]?.id ?? null
  }
  return runs[runs.length - 1]?.id ?? null
}

/**
 * Shared reactive Live Digest composable for both Flat Mode (Heartbeat Strip + Right Dock Panel)
 * and Spatial Mode (Section Subtitle, Teammate Cursor Thought Bubbles, Inspector Live Digest Tab).
 */
export function useLiveDigest(
  runIdSource: MaybeRefOrGetter<string | null | undefined>,
  options: UseLiveDigestOptions = {},
): UseLiveDigestReturn {
  const normalizedRunId = computed(() => normalizeRunKey(toValue(runIdSource)))
  const autoStreamEnabled = computed(() => toValue(options.autoStream) ?? true)
  const resolvedStorageId = computed(() => toValue(options.storageId))

  const activeEntry = shallowRef<LiveDigestHubEntry | null>(null)
  let heldStreaming = false

  function detachCurrent(): void {
    const entry = activeEntry.value
    if (!entry) return
    if (options.onEvent) entry.listeners.delete(options.onEvent)
    if (heldStreaming) {
      entry.streamRefCount = Math.max(0, entry.streamRefCount - 1)
      heldStreaming = false
      if (entry.streamRefCount === 0) {
        closeEntryStream(entry)
        if (entry.connectionState.value !== 'done') {
          entry.connectionState.value = 'idle'
        }
      }
    }
    entry.refCount = Math.max(0, entry.refCount - 1)
    entry.touchedAt = Date.now()
    activeEntry.value = null
    pruneIdleCache()
  }

  function attach(runId: string | null, shouldStream: boolean, storageId?: string): void {
    detachCurrent()
    if (!runId) return
    const entry = getOrCreateEntry(runId, storageId)
    entry.refCount += 1
    if (options.onEvent) entry.listeners.add(options.onEvent)
    activeEntry.value = entry

    if (!entry.snapshot.value) {
      void fetchEntrySnapshot(entry)
    }

    if (shouldStream) {
      entry.streamRefCount += 1
      heldStreaming = true
      if (!entry.snapshot.value || !isTerminalLiveDigestStatus(entry.snapshot.value.status)) {
        startEntryStream(entry)
      }
    }
  }

  watch(
    [normalizedRunId, autoStreamEnabled, resolvedStorageId],
    ([runId, shouldStream, storageId]) => {
      attach(runId, shouldStream, storageId)
    },
    { immediate: true },
  )

  if (getCurrentScope()) {
    onScopeDispose(() => {
      detachCurrent()
    })
  }

  const snapshot = computed<LiveDigestSnapshotDto | null>(() => activeEntry.value?.snapshot.value ?? null)
  const loading = computed<boolean>(() => activeEntry.value?.loading.value ?? false)
  const connectionState = computed<LiveDigestConnectionState>(() => activeEntry.value?.connectionState.value ?? 'idle')
  const connected = computed<boolean>(() => connectionState.value === 'live')
  const streaming = computed<boolean>(
    () => connectionState.value === 'live' || connectionState.value === 'connecting' || connectionState.value === 'reconnecting',
  )
  const error = computed<string | null>(() => activeEntry.value?.error.value ?? null)
  const lastEventType = computed<LiveDigestStreamEventName | null>(() => activeEntry.value?.lastEventType.value ?? null)

  const isTerminal = computed<boolean>(() => isTerminalLiveDigestStatus(snapshot.value?.status))
  const isRunning = computed<boolean>(() => Boolean(snapshot.value && !isTerminal.value))
  const headline = computed<string>(() => snapshot.value?.headline ?? '')
  const currentPhase = computed<LiveDigestPhase | null>(() => snapshot.value?.current_phase ?? null)
  const mode = computed<LiveDigestMode>(() => snapshot.value?.mode ?? liveDigestSettingsState.value?.mode ?? 'auto')
  const explainerActive = computed<boolean>(() => Boolean(snapshot.value?.explainer_active))
  const explainerModel = computed<string | null>(() => snapshot.value?.explainer_model ?? null)
  const contextWaterline = computed<LiveDigestContextWaterlineDto | null>(() => snapshot.value?.context_waterline ?? null)
  const governanceSignals = computed<LiveDigestGovernanceSignalDto[]>(() => snapshot.value?.governance_signals ?? [])
  const teammateThoughts = computed<LiveDigestTeammateThoughtDto[]>(() => snapshot.value?.teammate_thoughts ?? [])
  const teammateThoughtByHandle = computed<Record<string, LiveDigestTeammateThoughtDto>>(() => {
    const map: Record<string, LiveDigestTeammateThoughtDto> = {}
    for (const item of teammateThoughts.value) {
      if (item.handle) map[item.handle] = item
    }
    return map
  })
  const chapters = computed<LiveDigestChapterDto[]>(() => snapshot.value?.chapters ?? [])
  const touchedFiles = computed<LiveDigestTouchedFileDto[]>(() => snapshot.value?.touched_files ?? [])
  const sentinelAlerts = computed<LiveDigestSentinelAlertDto[]>(() => snapshot.value?.sentinel_alerts ?? [])

  async function refresh(): Promise<LiveDigestSnapshotDto | null> {
    const entry = activeEntry.value
    if (!entry) return null
    return fetchEntrySnapshot(entry, true)
  }

  function reconnect(): void {
    const entry = activeEntry.value
    if (!entry) return
    if (!heldStreaming) {
      entry.streamRefCount += 1
      heldStreaming = true
    }
    startEntryStream(entry, true)
  }

  function disconnect(): void {
    const entry = activeEntry.value
    if (!entry) return
    if (heldStreaming) {
      entry.streamRefCount = Math.max(0, entry.streamRefCount - 1)
      heldStreaming = false
    }
    if (entry.streamRefCount === 0) {
      closeEntryStream(entry)
      if (entry.connectionState.value !== 'done') {
        entry.connectionState.value = 'idle'
      }
    }
  }

  return {
    runId: normalizedRunId,
    snapshot,
    loading,
    connected,
    streaming,
    connectionState,
    error,
    isRunning,
    isTerminal,
    headline,
    currentPhase,
    mode,
    explainerActive,
    explainerModel,
    contextWaterline,
    governanceSignals,
    teammateThoughts,
    teammateThoughtByHandle,
    chapters,
    touchedFiles,
    sentinelAlerts,
    lastEventType,
    refresh,
    reconnect,
    disconnect,
  }
}

// ---------------------------------------------------------------------------
// Shared Live Digest Settings composable (`/api/v1/live-digest/settings`)
// ---------------------------------------------------------------------------

const liveDigestSettingsState = ref<LiveDigestSettingsDto | null>(null)
const liveDigestSettingsLoading = ref(false)
const liveDigestSettingsSaving = ref(false)
const liveDigestSettingsError = ref<string | null>(null)
let liveDigestSettingsLoaded = false

export function useLiveDigestSettings() {
  async function loadSettings(force = false): Promise<LiveDigestSettingsDto | null> {
    if (liveDigestSettingsLoaded && !force && liveDigestSettingsState.value) {
      return liveDigestSettingsState.value
    }
    liveDigestSettingsLoading.value = true
    liveDigestSettingsError.value = null
    try {
      const result = await getLiveDigestSettings()
      liveDigestSettingsState.value = result
      liveDigestSettingsLoaded = true
      return result
    } catch (error) {
      liveDigestSettingsError.value = error instanceof Error ? error.message : String(error)
      return liveDigestSettingsState.value
    } finally {
      liveDigestSettingsLoading.value = false
    }
  }

  async function saveSettings(
    input: UpdateLiveDigestSettingsInput,
  ): Promise<LiveDigestSettingsDto | null> {
    liveDigestSettingsSaving.value = true
    liveDigestSettingsError.value = null
    try {
      const updated = await updateLiveDigestSettings(input)
      liveDigestSettingsState.value = updated
      liveDigestSettingsLoaded = true
      return updated
    } catch (error) {
      liveDigestSettingsError.value = error instanceof Error ? error.message : String(error)
      throw error
    } finally {
      liveDigestSettingsSaving.value = false
    }
  }

  async function setMode(nextMode: LiveDigestMode): Promise<LiveDigestSettingsDto | null> {
    const current = liveDigestSettingsState.value
    return saveSettings({
      mode: nextMode,
      provider_instance_id: current?.provider_instance_id ?? null,
      model: current?.model ?? null,
    })
  }

  async function setExplainerModel(
    providerInstanceId: string | null,
    model: string | null,
  ): Promise<LiveDigestSettingsDto | null> {
    const currentMode = liveDigestSettingsState.value?.mode ?? 'auto'
    if (!providerInstanceId) {
      return saveSettings({
        mode: currentMode,
        provider_instance_id: null,
        model: null,
        clear_model_binding: true,
      })
    }
    return saveSettings({
      mode: currentMode,
      provider_instance_id: providerInstanceId,
      model: model || null,
      clear_model_binding: false,
    })
  }

  const mode = computed<LiveDigestMode>(() => liveDigestSettingsState.value?.mode ?? 'auto')

  return {
    settings: liveDigestSettingsState,
    loading: liveDigestSettingsLoading,
    saving: liveDigestSettingsSaving,
    error: liveDigestSettingsError,
    mode,
    loadSettings,
    saveSettings,
    setMode,
    setExplainerModel,
  }
}

/** Test-only helper to clear shared hub & settings singletons between Vitest cases. */
export function resetLiveDigestHubForTests(): void {
  for (const entry of hubEntries.values()) {
    closeEntryStream(entry)
    entry.listeners.clear()
  }
  hubEntries.clear()
  liveDigestSettingsState.value = null
  liveDigestSettingsLoading.value = false
  liveDigestSettingsSaving.value = false
  liveDigestSettingsError.value = null
  liveDigestSettingsLoaded = false
}
