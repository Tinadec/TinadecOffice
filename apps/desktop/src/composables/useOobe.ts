import { computed, ref } from 'vue'
import {
  api,
  type ModelProbeResultDto,
  type ModelProviderInstanceDto,
  type ReadinessItemDto,
  type RuntimeReadinessReceiptDto,
} from '@/api'
import { useHostAccess } from '@/lib/hostAccess'

export const OOBE_STEPS = ['welcome', 'provider', 'ecosystem', 'habits', 'firstTask'] as const
export type OobeStepId = (typeof OOBE_STEPS)[number]

export interface OobeTourRecord {
  status: 'done' | 'dismissed' | 'snoozed'
  at: string
  until?: string
}

export interface OobeState {
  version: 1
  firstRun: {
    completedAt?: string
    skippedAt?: string
    resumeStep?: OobeStepId
  }
  tours: Record<string, OobeTourRecord>
}

export type ProbeErrorKind =
  | 'auth'
  | 'model_not_found'
  | 'network'
  | 'rate_limit'
  | 'not_run'
  | 'unknown'

export interface ClassifiedProbeError {
  kind: ProbeErrorKind
  detail: string
}

export interface OobeReadinessSummary {
  ready: boolean
  modelRouteResolvable: boolean
  modelSecretReady: boolean
  modelProbeStatus: 'ready' | 'failed' | 'not_run'
  hasConfiguredProvider: boolean
}

export interface DeferredOobeHabits {
  companionCapsule: boolean | null
  desktopPet: boolean | null
}

const STORAGE_KEY = 'tinadec.oobe.state.v1'

const LOCAL_OR_NO_KEY_DRIVERS = new Set(['ollama', 'lmstudio', 'vllm', 'pollinations'])

export function createDefaultOobeState(): OobeState {
  return {
    version: 1,
    firstRun: {},
    tours: {},
  }
}

export function normalizeOobeState(raw: unknown): OobeState {
  if (!raw || typeof raw !== 'object') return createDefaultOobeState()
  const candidate = raw as Partial<OobeState>
  const firstRun = candidate.firstRun && typeof candidate.firstRun === 'object' ? candidate.firstRun : {}
  const resumeStep =
    typeof firstRun.resumeStep === 'string' && (OOBE_STEPS as readonly string[]).includes(firstRun.resumeStep)
      ? (firstRun.resumeStep as OobeStepId)
      : undefined
  return {
    version: 1,
    firstRun: {
      ...(typeof firstRun.completedAt === 'string' ? { completedAt: firstRun.completedAt } : {}),
      ...(typeof firstRun.skippedAt === 'string' ? { skippedAt: firstRun.skippedAt } : {}),
      ...(resumeStep ? { resumeStep } : {}),
    },
    tours: candidate.tours && typeof candidate.tours === 'object' ? { ...candidate.tours } : {},
  }
}

/**
 * Determine whether a provider instance actually has usable credentials/configuration.
 * Explicitly ignores keyless DevSeed placeholder rows so Step 2 starts in clean empty state.
 */
export function isProviderEffectivelyConfigured(
  provider: Pick<ModelProviderInstanceDto, 'id' | 'driver' | 'display_name' | 'connection_kind' | 'has_api_key' | 'enabled'>,
): boolean {
  if (!provider.enabled) return false
  const idLower = (provider.id || '').toLowerCase()
  const nameLower = (provider.display_name || '').toLowerCase()
  const driverLower = (provider.driver || '').toLowerCase()
  const isDevSeedName = idLower.includes('devseed') || idLower.includes('dev-seed') || nameLower.includes('devseed') || nameLower.includes('dev seed')

  if (isDevSeedName && !provider.has_api_key) {
    return false
  }

  if (provider.connection_kind === 'local-server' || provider.connection_kind === 'cli' || provider.connection_kind === 'public-api') {
    return true
  }

  if (LOCAL_OR_NO_KEY_DRIVERS.has(driverLower)) {
    return true
  }

  // Standard HTTP API-key providers MUST have a stored key to count as configured.
  return Boolean(provider.has_api_key)
}

function findReadinessItem(
  receipt: RuntimeReadinessReceiptDto | null | undefined,
  ids: string[],
): ReadinessItemDto | undefined {
  if (!receipt?.items?.length) return undefined
  return receipt.items.find((item) => ids.includes(item.id))
}

/**
 * Evaluate Core readiness + configured providers using the OOBE admission contract:
 * - Already configured users (route resolvable + secret ready + probe not failed + real provider) see nothing.
 * - `model_probe` with status `unavailable` / reason_code `probe_not_run` is NOT treated as a failure.
 */
export function evaluateReadinessForOobe(
  receipt: RuntimeReadinessReceiptDto | null | undefined,
  providers: Array<Pick<ModelProviderInstanceDto, 'id' | 'driver' | 'display_name' | 'connection_kind' | 'has_api_key' | 'enabled'>>,
): OobeReadinessSummary {
  const configuredProviders = providers.filter(isProviderEffectivelyConfigured)
  const hasConfiguredProvider = configuredProviders.length > 0

  const routeItem = findReadinessItem(receipt, ['model_route', 'model_routes'])
  const secretItem = findReadinessItem(receipt, ['model_secret', 'model_secrets', 'model_credentials'])
  const probeItem = findReadinessItem(receipt, ['model_probe'])

  const modelRouteResolvable = routeItem ? routeItem.status === 'ready' : hasConfiguredProvider
  const modelSecretReady = secretItem ? secretItem.status === 'ready' : hasConfiguredProvider

  let modelProbeStatus: 'ready' | 'failed' | 'not_run' = 'not_run'
  if (probeItem) {
    if (probeItem.status === 'ready') {
      modelProbeStatus = 'ready'
    } else if (probeItem.status === 'unavailable' || probeItem.reason_code === 'probe_not_run') {
      modelProbeStatus = 'not_run'
    } else {
      modelProbeStatus = 'failed'
    }
  }

  const ready = Boolean(
    hasConfiguredProvider &&
      modelRouteResolvable &&
      modelSecretReady &&
      modelProbeStatus !== 'failed',
  )

  return {
    ready,
    modelRouteResolvable,
    modelSecretReady,
    modelProbeStatus,
    hasConfiguredProvider,
  }
}

/**
 * Pure admission policy: readiness-driven rather than naive first-run flag.
 * `skippedAt` and readiness remain independent states — skipping prevents auto-reopen
 * while letting users replay OOBE at any time from Settings.
 */
export function shouldAutoOpenOobe(
  receipt: RuntimeReadinessReceiptDto | null | undefined,
  providers: Array<Pick<ModelProviderInstanceDto, 'id' | 'driver' | 'display_name' | 'connection_kind' | 'has_api_key' | 'enabled'>>,
  state: OobeState,
): boolean {
  if (state.firstRun.completedAt || state.firstRun.skippedAt) {
    return false
  }
  const summary = evaluateReadinessForOobe(receipt, providers)
  return !summary.ready
}

export function classifyProbeError(input: ModelProbeResultDto | Error | unknown): ClassifiedProbeError {
  if (input && typeof input === 'object' && 'status' in input && 'id' in input) {
    const item = input as ModelProbeResultDto
    const code = (item.reason_code ?? '').toLowerCase()
    const detail = item.reason || item.action || item.reason_code || item.status || ''
    const httpStatus = Number((item.data as Record<string, unknown> | undefined)?.http_status ?? (item.data as Record<string, unknown> | undefined)?.status_code ?? 0)
    const combined = `${code} ${detail} ${httpStatus}`.toLowerCase()

    if (item.status === 'unavailable' || code === 'probe_not_run') {
      return { kind: 'not_run', detail }
    }
    if (httpStatus === 401 || httpStatus === 403 || /\b(401|403)\b|unauthoriz|forbidden|invalid[_ ]?api[_ ]?key|auth/.test(combined)) {
      return { kind: 'auth', detail }
    }
    if (httpStatus === 404 || /\b404\b|model[_ ]?not[_ ]?found|not found/.test(combined)) {
      return { kind: 'model_not_found', detail }
    }
    if (httpStatus === 429 || /\b429\b|rate[_ ]?limit|quota|too many requests/.test(combined)) {
      return { kind: 'rate_limit', detail }
    }
    if (/timeout|timed out|econnrefused|enotfound|network|unreachable|connection refused|failed to fetch|socket|dns/.test(combined)) {
      return { kind: 'network', detail }
    }
    return { kind: 'unknown', detail }
  }

  const status = (input as { status?: number } | null)?.status ?? 0
  const detail = input instanceof Error ? input.message : String(input ?? '')
  const lower = `${status} ${detail}`.toLowerCase()

  if (status === 401 || status === 403 || /\b(401|403)\b|unauthoriz|forbidden|invalid[_ ]?api[_ ]?key|auth/.test(lower)) {
    return { kind: 'auth', detail }
  }
  if (status === 404 || /\b404\b|model[_ ]?not[_ ]?found|not found/.test(lower)) {
    return { kind: 'model_not_found', detail }
  }
  if (status === 429 || /\b429\b|rate[_ ]?limit|quota|too many requests/.test(lower)) {
    return { kind: 'rate_limit', detail }
  }
  if (/timeout|timed out|econnrefused|enotfound|network|unreachable|connection refused|failed to fetch|socket|dns/.test(lower)) {
    return { kind: 'network', detail }
  }
  return { kind: 'unknown', detail }
}

export function getNextStep(step: OobeStepId): OobeStepId | null {
  const index = OOBE_STEPS.indexOf(step)
  return index >= 0 && index < OOBE_STEPS.length - 1 ? OOBE_STEPS[index + 1] : null
}

export function getPrevStep(step: OobeStepId): OobeStepId | null {
  const index = OOBE_STEPS.indexOf(step)
  return index > 0 ? OOBE_STEPS[index - 1] : null
}

// ---- Persistence bridge (Electron IPC when available, localStorage fallback) ----

interface OobeStorageBridge {
  readState?: () => Promise<unknown>
  writeState?: (state: OobeState) => Promise<void>
}

function ipcBridge(): OobeStorageBridge | undefined {
  if (typeof window === 'undefined') return undefined
  return (window.tinadec as unknown as { oobe?: OobeStorageBridge } | undefined)?.oobe
}

export async function readPersistedOobeState(): Promise<OobeState> {
  const bridge = ipcBridge()
  if (bridge?.readState) {
    try {
      return normalizeOobeState(await bridge.readState())
    } catch {
      // Fall back to localStorage below.
    }
  }
  try {
    const raw = typeof localStorage !== 'undefined' ? localStorage.getItem(STORAGE_KEY) : null
    return raw ? normalizeOobeState(JSON.parse(raw)) : createDefaultOobeState()
  } catch {
    return createDefaultOobeState()
  }
}

export async function writePersistedOobeState(next: OobeState): Promise<void> {
  const normalized = normalizeOobeState(next)
  try {
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(normalized))
    }
  } catch {
    // Storage disabled/full.
  }
  const bridge = ipcBridge()
  if (bridge?.writeState) {
    try {
      await bridge.writeState(normalized)
    } catch {
      // Non-fatal: localStorage already persisted.
    }
  }
}

// ---- Module-level singleton controller ----

const oobeOpen = ref(false)
const currentStep = ref<OobeStepId>('welcome')
const oobeState = ref<OobeState>(createDefaultOobeState())
const readinessSummary = ref<OobeReadinessSummary | null>(null)
const providerVerified = ref(false)
const deferredHabits = ref<DeferredOobeHabits>({ companionCapsule: null, desktopPet: null })
let admissionEvaluated = false

export function isOobeOpen(): boolean {
  return oobeOpen.value
}

export function queueDeferredHabit(habit: keyof DeferredOobeHabits, enabled: boolean): void {
  deferredHabits.value = {
    ...deferredHabits.value,
    [habit]: enabled,
  }
}

export async function flushDeferredHabits(): Promise<void> {
  const pending = { ...deferredHabits.value }
  deferredHabits.value = { companionCapsule: null, desktopPet: null }

  if (typeof window === 'undefined' || !window.tinadec) return
  if (pending.companionCapsule !== null && typeof window.tinadec.saveCompanionMode === 'function') {
    try {
      await window.tinadec.saveCompanionMode(pending.companionCapsule)
    } catch {
      // Non-fatal preference flush.
    }
  }
  if (pending.desktopPet === true && window.tinadec.pets?.listDownloaded && window.tinadec.pets?.setEnabled) {
    try {
      const downloaded = await window.tinadec.pets.listDownloaded()
      const first = downloaded.find((pet) => !pet.enabled) ?? downloaded[0]
      if (first) {
        await window.tinadec.pets.setEnabled(first.slug, true)
      }
    } catch {
      // Non-fatal pet window launch.
    }
  }
}

export function openOobeWizard(step?: OobeStepId): void {
  currentStep.value = step ?? oobeState.value.firstRun.resumeStep ?? 'welcome'
  oobeOpen.value = true
}

export function replayOobeWizard(): void {
  currentStep.value = 'welcome'
  oobeOpen.value = true
}

export async function setOobeStep(step: OobeStepId): Promise<void> {
  currentStep.value = step
  const next: OobeState = {
    ...oobeState.value,
    firstRun: {
      ...oobeState.value.firstRun,
      resumeStep: step,
    },
  }
  oobeState.value = next
  await writePersistedOobeState(next)
}

export async function closeOobeWizard(mode: 'skip' | 'complete' | 'dismiss' = 'dismiss'): Promise<void> {
  oobeOpen.value = false
  const now = new Date().toISOString()
  if (mode === 'complete') {
    const next: OobeState = {
      ...oobeState.value,
      firstRun: {
        ...oobeState.value.firstRun,
        completedAt: now,
        resumeStep: undefined,
      },
    }
    oobeState.value = next
    await writePersistedOobeState(next)
  } else if (mode === 'skip') {
    const next: OobeState = {
      ...oobeState.value,
      firstRun: {
        ...oobeState.value.firstRun,
        skippedAt: now,
        resumeStep: currentStep.value,
      },
    }
    oobeState.value = next
    await writePersistedOobeState(next)
  }
  // Always flush deferred floating windows ONLY after the wizard closes so alwaysOnTop never overlaps OOBE.
  await flushDeferredHabits()
}

export async function evaluateOobeAdmission(): Promise<boolean> {
  if (admissionEvaluated) return oobeOpen.value
  const { canAccessBackend } = useHostAccess()
  if (!canAccessBackend.value) return false
  if (typeof api.readiness !== 'function' || typeof api.listModelProviders !== 'function') {
    // Minimal mocked test environment: stay silent.
    return false
  }
  admissionEvaluated = true
  try {
    const persisted = await readPersistedOobeState()
    oobeState.value = persisted
    const [receipt, providers] = await Promise.all([
      api.readiness().catch(() => null),
      api.listModelProviders().catch(() => [] as ModelProviderInstanceDto[]),
    ])
    const summary = evaluateReadinessForOobe(receipt, providers)
    readinessSummary.value = summary
    providerVerified.value = summary.ready
    if (shouldAutoOpenOobe(receipt, providers, persisted)) {
      currentStep.value = persisted.firstRun.resumeStep ?? 'welcome'
      oobeOpen.value = true
      return true
    }
    return false
  } catch {
    return false
  }
}

export function __resetOobeForTests(): void {
  oobeOpen.value = false
  currentStep.value = 'welcome'
  oobeState.value = createDefaultOobeState()
  readinessSummary.value = null
  providerVerified.value = false
  deferredHabits.value = { companionCapsule: null, desktopPet: null }
  admissionEvaluated = false
}

export function useOobe() {
  const stepIndex = computed(() => OOBE_STEPS.indexOf(currentStep.value))
  const canGoBack = computed(() => stepIndex.value > 0)
  const isLastStep = computed(() => stepIndex.value === OOBE_STEPS.length - 1)

  return {
    steps: OOBE_STEPS,
    open: oobeOpen,
    currentStep,
    stepIndex,
    canGoBack,
    isLastStep,
    state: oobeState,
    readinessSummary,
    providerVerified,
    deferredHabits,
    openOobeWizard,
    replayOobeWizard,
    closeOobeWizard,
    setOobeStep,
    queueDeferredHabit,
    flushDeferredHabits,
    evaluateOobeAdmission,
  }
}
