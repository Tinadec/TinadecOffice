import { readonly, ref } from 'vue'
import {
  api as baseApi,
  type AgentPackInstallPreviewDto,
  type AgentPackInstallResultDto,
} from '@/api'
import { scopedApi } from '@/lib/storageScope'
const api = scopedApi(baseApi, () => 'user')
import { isConnectionLevelError, useNotifications } from '@/composables/useNotifications'
import { apiErrorDetails } from '@/lib/apiError'
import {
  GRAPH_SEED_PACK_DIGEST,
  GRAPH_SEED_PACK_ID,
  GRAPH_SEED_PACK_VERSION,
  graphSeedPackEnvelope,
} from './GraphSeedPack'

export type GraphSeedPackPhase =
  | 'idle'
  | 'checking'
  | 'install'
  | 'upgrade'
  | 'installing'
  | 'up_to_date'
  | 'newer_installed'
  | 'conflict'
  | 'owner_required'
  | 'deferred'
  | 'error'

export interface GraphSeedPackBootstrapState {
  phase: GraphSeedPackPhase
  preview: AgentPackInstallPreviewDto | null
  active_version: string | null
  error: string | null
  error_details: string | null
  checked_at: number | null
}

interface EnsureOptions {
  force?: boolean
  prompt?: boolean
}

type BootstrapBroadcast =
  | { type: 'state_request'; key: string }
  | {
      type: 'handled'
      key: string
      phase: GraphSeedPackPhase
      active_version: string | null
      error?: string | null
      error_details?: string | null
    }

const NOTIFICATION_KEY = 'graph-seed-pack'
const DEFERRED_NOTIFICATION_KEY = 'graph-seed-pack-deferred'
const CHANNEL_NAME = 'tinadec-agent-pack-bootstrap'

const state = ref<GraphSeedPackBootstrapState>({
  phase: 'idle',
  preview: null,
  active_version: null,
  error: null,
  error_details: null,
  checked_at: null,
})
const handledPromptKeys = new Set<string>()
const terminalFailures = new Map<string, Partial<GraphSeedPackBootstrapState>>()
let activeEnsure: Promise<boolean> | null = null
let channel: BroadcastChannel | null = null
let translate: (key: string, params?: Record<string, unknown>) => string = (key) => key

export const graphSeedPackState = readonly(state)

function t(key: string, params?: Record<string, unknown>): string {
  return translate(key, params)
}

export function setGraphSeedPackTranslator(
  translator: (key: string, params?: Record<string, unknown>) => string,
): void {
  translate = translator
}

function bootstrapKey(): string {
  return `${api.gatewayUrl}|user|${GRAPH_SEED_PACK_ID}|${GRAPH_SEED_PACK_VERSION}|${GRAPH_SEED_PACK_DIGEST}`
}

function setState(patch: Partial<GraphSeedPackBootstrapState>): void {
  state.value = { ...state.value, ...patch }
}

function getChannel(): BroadcastChannel | null {
  if (channel || typeof BroadcastChannel === 'undefined') return channel
  channel = new BroadcastChannel(CHANNEL_NAME)
  channel.addEventListener('message', (event: MessageEvent<BootstrapBroadcast>) => {
    const message = event.data
    const terminalPhases = [
      'up_to_date',
      'newer_installed',
      'deferred',
      'owner_required',
      'conflict',
      'error',
    ]
    if (message?.type === 'state_request' && message.key === bootstrapKey()) {
      if (terminalPhases.includes(state.value.phase)) {
        broadcastHandled(state.value.phase, state.value.active_version)
      }
      return
    }
    if (
      !message ||
      message.type !== 'handled' ||
      message.key !== bootstrapKey() ||
      !terminalPhases.includes(message.phase) ||
      (message.active_version !== null && typeof message.active_version !== 'string') ||
      (message.error != null && typeof message.error !== 'string') ||
      (message.error_details != null && typeof message.error_details !== 'string')
    ) {
      return
    }
    handledPromptKeys.add(message.key)
    const outcome = {
      phase: message.phase,
      active_version: message.active_version,
      error: message.error ?? null,
      error_details: message.error_details ?? null,
      checked_at: Date.now(),
    }
    if (message.phase === 'error' || message.phase === 'conflict') {
      terminalFailures.set(message.key, outcome)
    } else {
      terminalFailures.delete(message.key)
    }
    setState(outcome)
  })
  channel.postMessage({ type: 'state_request', key: bootstrapKey() } satisfies BootstrapBroadcast)
  return channel
}

function broadcastHandled(phase: GraphSeedPackPhase, activeVersion: string | null): void {
  getChannel()?.postMessage({
    type: 'handled',
    key: bootstrapKey(),
    phase,
    active_version: activeVersion,
    error: state.value.error,
    error_details: state.value.error_details,
  } satisfies BootstrapBroadcast)
}

function rememberFailure(): void {
  const key = bootstrapKey()
  handledPromptKeys.add(key)
  terminalFailures.set(key, { ...state.value })
  broadcastHandled(state.value.phase, state.value.active_version)
}

function previewDetails(preview: AgentPackInstallPreviewDto): string {
  const counts = preview.counts
  const lines = [
    t('agentPack.resourceCounts', {
      agents: counts.agents,
      prompts: counts.prompt_pipelines,
      modes: counts.modes,
    }),
    t('agentPack.trustReceipt', {
      owner: preview.owner,
      version: preview.bundled_version,
      digest: preview.integrity_digest,
    }),
    t('agentPack.managedReadOnly'),
    t('agentPack.workspaceDefaultNotice'),
  ]
  if (preview.warnings.length > 0) lines.push(...preview.warnings)
  return lines.join('\n')
}

function isCodedError(error: unknown, code: string): boolean {
  return error instanceof Error && (error as Error & { code?: string }).code === code
}

function isHttpStatus(error: unknown, status: number): boolean {
  return error instanceof Error && (error as Error & { status?: number }).status === status
}

async function previewPack(): Promise<AgentPackInstallPreviewDto> {
  const preview = await api.previewAgentPackInstall(graphSeedPackEnvelope)
  const expectedIdentity = {
    pack_id: GRAPH_SEED_PACK_ID,
    owner: graphSeedPackEnvelope.manifest.metadata.owner,
    bundled_version: GRAPH_SEED_PACK_VERSION,
  }
  const identityMismatch = Object.entries(expectedIdentity).find(
    ([field, expected]) => preview[field as keyof typeof expectedIdentity] !== expected,
  )
  if (identityMismatch) {
    const [field, expected] = identityMismatch
    throw Object.assign(
      new Error(
        t('agentPack.previewIdentityMismatch', {
          field,
          expected,
          actual: preview[field as keyof typeof expectedIdentity],
        }),
      ),
      { code: 'agent_pack_identity_mismatch', field },
    )
  }
  if (preview.integrity_digest !== GRAPH_SEED_PACK_DIGEST) {
    throw Object.assign(new Error(t('agentPack.previewDigestMismatch')), {
      code: 'agent_pack_digest_mismatch',
    })
  }
  const knownActions = new Set(['install', 'upgrade', 'up_to_date', 'newer_installed', 'conflict'])
  if (!knownActions.has(preview.action)) {
    throw Object.assign(
      new Error(t('agentPack.unsupportedPreviewAction', { action: preview.action })),
      { code: 'unsupported_agent_pack_action' },
    )
  }
  setState({
    phase: preview.action as GraphSeedPackPhase,
    preview,
    active_version: preview.installed_version,
    error: null,
    error_details: null,
    checked_at: Date.now(),
  })
  return preview
}

function clearPackNotifications(): void {
  const { dismissByKey } = useNotifications()
  dismissByKey(NOTIFICATION_KEY)
  dismissByKey(DEFERRED_NOTIFICATION_KEY)
}

async function confirmAndInstall(preview: AgentPackInstallPreviewDto): Promise<boolean> {
  if ((preview.action !== 'install' && preview.action !== 'upgrade') || !preview.preview_id) {
    return true
  }
  const key = bootstrapKey()
  const { confirm, notify } = useNotifications()
  const isUpgrade = preview.action === 'upgrade'
  const confirmed = await confirm({
    title: t(isUpgrade ? 'agentPack.upgradeTitle' : 'agentPack.installTitle'),
    message: t(isUpgrade ? 'agentPack.upgradeMessage' : 'agentPack.installMessage', {
      installed: preview.installed_version ?? t('common.none'),
      bundled: preview.bundled_version,
    }),
    details: previewDetails(preview),
    confirmLabel: t(isUpgrade ? 'agentPack.upgradeAndActivate' : 'agentPack.installAndActivate'),
    cancelLabel: t('agentPack.notNow'),
  })

  handledPromptKeys.add(key)
  if (!confirmed) {
    setState({ phase: 'deferred' })
    notify.warning({
      key: DEFERRED_NOTIFICATION_KEY,
      title: t('agentPack.deferredTitle'),
      message: t('agentPack.deferredMessage'),
      source: 'GraphSeedPack',
      persistence: 'sticky',
      action: {
        label: t(isUpgrade ? 'agentPack.upgradeAction' : 'agentPack.installAction'),
        run: () => ensureGraphSeedPack({ force: true, prompt: true }),
      },
    })
    broadcastHandled('deferred', preview.installed_version)
    return true
  }

  return applyPreview(preview)
}

async function applyPreview(preview: AgentPackInstallPreviewDto): Promise<boolean> {
  const { notify } = useNotifications()
  const isUpgrade = preview.action === 'upgrade'
  const task = notify.task({
    key: `${NOTIFICATION_KEY}-task`,
    title: t(isUpgrade ? 'agentPack.upgradingTitle' : 'agentPack.installingTitle'),
    message: t('agentPack.installingMessage'),
    details: undefined,
    source: 'GraphSeedPack',
  })
  setState({ phase: 'installing', error: null })

  try {
    const result = await api.installAgentPack(
      GRAPH_SEED_PACK_ID,
      { preview_id: preview.preview_id!, envelope: graphSeedPackEnvelope },
      {
        if_match: isUpgrade ? (preview.etag ?? `"${preview.revision}"`) : null,
        idempotency_key: `${GRAPH_SEED_PACK_ID}:${GRAPH_SEED_PACK_VERSION}:${GRAPH_SEED_PACK_DIGEST}`,
      },
    )
    settleInstalled(result)
    clearPackNotifications()
    task.succeed({
      message: t(
        result.status === 'updated' ? 'agentPack.upgradeSucceeded' : 'agentPack.installSucceeded',
      ),
    })
    return true
  } catch (error) {
    if (isHttpStatus(error, 409) || isHttpStatus(error, 412)) {
      try {
        const current = await previewPack()
        if (current.action === 'up_to_date' || current.action === 'newer_installed') {
          settleInstalledFromPreview(current)
          task.succeed({ message: t('agentPack.alreadyInstalled') })
          return true
        }
      } catch {
        // Preserve the original install error below.
      }
    }

    const connectionError = isConnectionLevelError(error)
    const message = error instanceof Error ? error.message : t('agentPack.installFailed')
    const details = apiErrorDetails(error)
    setState({ phase: 'error', error: message, error_details: details ?? null, checked_at: Date.now() })
    if (!connectionError) {
      rememberFailure()
    }
    task.fail(error, {
      title: t('agentPack.installFailed'),
      details,
      source: 'GraphSeedPack',
      category: connectionError ? 'connection' : 'general',
    })
    notify.error({
      key: NOTIFICATION_KEY,
      category: connectionError ? 'connection' : 'general',
      title: t('agentPack.installFailed'),
      message,
      details,
      source: 'GraphSeedPack',
      action: {
        label: t('settings.retry'),
        run: () => ensureGraphSeedPack({ force: true, prompt: true }),
      },
    })
    return false
  }
}

function settleInstalled(result: AgentPackInstallResultDto): void {
  const phase = result.status === 'newer_installed' ? 'newer_installed' : 'up_to_date'
  setState({
    phase,
    active_version: result.active_version,
    error: null,
    error_details: null,
    checked_at: Date.now(),
  })
  handledPromptKeys.add(bootstrapKey())
  terminalFailures.delete(bootstrapKey())
  broadcastHandled(phase, result.active_version)
}

function settleInstalledFromPreview(preview: AgentPackInstallPreviewDto): void {
  const phase = preview.action === 'newer_installed' ? 'newer_installed' : 'up_to_date'
  clearPackNotifications()
  setState({
    phase,
    preview,
    active_version: preview.installed_version,
    error: null,
    error_details: null,
    checked_at: Date.now(),
  })
  handledPromptKeys.add(bootstrapKey())
  terminalFailures.delete(bootstrapKey())
  broadcastHandled(phase, preview.installed_version)
}

async function runEnsure(options: EnsureOptions): Promise<boolean> {
  const key = bootstrapKey()
  const { notify, status, dismissByKey } = useNotifications()
  // Reconnects and queued windows preserve non-connection terminal failures. Only explicit
  // checks/retries replace them with a fresh preview; a failed attempt is not a "not now" decision.
  const failure = terminalFailures.get(key)
  if (!options.force && failure) {
    setState(failure)
    return false
  }
  if (options.force) terminalFailures.delete(key)
  setState({ phase: 'checking', error: null, error_details: null })

  try {
    const preview = await previewPack()
    // A peer can report its terminal result while this HTTP preview is in flight.
    // Preserve that result before a queued window opens another install prompt.
    const peerFailure = terminalFailures.get(key)
    if (!options.force && peerFailure) {
      setState(peerFailure)
      return false
    }
    if (preview.action === 'up_to_date' || preview.action === 'newer_installed') {
      settleInstalledFromPreview(preview)
      return true
    }
    if (preview.action === 'conflict') {
      const message =
        [...(preview.differences ?? []), ...preview.warnings].join('\n') ||
        t('agentPack.conflictMessage')
      setState({ phase: 'conflict', error: message })
      rememberFailure()
      notify.error({
        key: NOTIFICATION_KEY,
        category: 'general',
        title: t('agentPack.conflictTitle'),
        message,
        source: 'GraphSeedPack',
        action: {
          label: t('settings.retry'),
          run: () => ensureGraphSeedPack({ force: true, prompt: true }),
        },
      })
      return false
    }
    dismissByKey(NOTIFICATION_KEY)
    if ((preview.action === 'install' || preview.action === 'upgrade') && options.prompt !== false) {
      if (!options.force && handledPromptKeys.has(key)) {
        setState({ phase: 'deferred' })
        return true
      }
      return await confirmAndInstall(preview)
    }
    return true
  } catch (error) {
    if (isCodedError(error, 'agent_pack_management_forbidden')) {
      const message = t('agentPack.ownerRequiredMessage')
      setState({ phase: 'owner_required', error: message, checked_at: Date.now() })
      handledPromptKeys.add(key)
      status.warning({
        key: NOTIFICATION_KEY,
        broadcast: false,
        title: t('agentPack.ownerRequiredTitle'),
        message,
        source: 'GraphSeedPack',
      })
      broadcastHandled('owner_required', null)
      return false
    }
    const connectionError = isConnectionLevelError(error)
    const message = error instanceof Error ? error.message : t('agentPack.previewFailed')
    const details = apiErrorDetails(error)
    setState({ phase: 'error', error: message, error_details: details ?? null, checked_at: Date.now() })
    if (!connectionError) {
      rememberFailure()
    }
    notify.error({
      key: NOTIFICATION_KEY,
      category: connectionError ? 'connection' : 'general',
      title: t('agentPack.previewFailed'),
      message,
      details,
      source: 'GraphSeedPack',
      action: {
        label: t('settings.retry'),
        run: () => ensureGraphSeedPack({ force: true, prompt: true }),
      },
    })
    return false
  }
}

async function withCrossWindowLock(run: () => Promise<boolean>): Promise<boolean> {
  getChannel()
  const lockManager =
    typeof navigator !== 'undefined'
      ? (navigator as Navigator & { locks?: LockManager }).locks
      : undefined
  if (!lockManager) {
    return run()
  }
  let result = false
  await lockManager.request(`tinadec-agent-pack:${bootstrapKey()}`, async () => {
    result = await run()
  })
  return result
}

export function ensureGraphSeedPack(options: EnsureOptions = {}): Promise<boolean> {
  if (activeEnsure) return activeEnsure
  activeEnsure = withCrossWindowLock(() => runEnsure(options)).finally(() => {
    activeEnsure = null
  })
  return activeEnsure
}

export function refreshGraphSeedPack(): Promise<boolean> {
  return ensureGraphSeedPack({ force: true, prompt: false })
}

export function installOrUpgradeGraphSeedPack(): Promise<boolean> {
  return ensureGraphSeedPack({ force: true, prompt: true })
}

export function __resetGraphSeedPackBootstrapForTests(): void {
  state.value = {
    phase: 'idle',
    preview: null,
    active_version: null,
    error: null,
    error_details: null,
    checked_at: null,
  }
  handledPromptKeys.clear()
  terminalFailures.clear()
  activeEnsure = null
  channel?.close()
  channel = null
  translate = (key) => key
}
