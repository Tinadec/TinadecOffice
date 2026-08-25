import { readonly, ref } from 'vue'
import {
  api,
  type AgentPackInstallPreviewDto,
  type AgentPackInstallResultDto,
} from '@/api'
import { useNotifications } from '@/composables/useNotifications'
import {
  OFFICE_AGENT_PACK_DIGEST,
  OFFICE_AGENT_PACK_ID,
  OFFICE_AGENT_PACK_VERSION,
  officeAgentPackEnvelope,
} from './OfficeAgentPack'

export type OfficeAgentPackPhase =
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

export interface OfficeAgentPackBootstrapState {
  phase: OfficeAgentPackPhase
  preview: AgentPackInstallPreviewDto | null
  active_version: string | null
  error: string | null
  checked_at: number | null
}

interface EnsureOptions {
  force?: boolean
  prompt?: boolean
}

interface BootstrapBroadcast {
  type: 'handled'
  key: string
  phase: OfficeAgentPackPhase
  active_version: string | null
}

const NOTIFICATION_KEY = 'office-agent-pack'
const DEFERRED_NOTIFICATION_KEY = 'office-agent-pack-deferred'
const CHANNEL_NAME = 'tinadec-agent-pack-bootstrap'
const LOCK_NAME = `tinadec-agent-pack:${OFFICE_AGENT_PACK_ID}:${OFFICE_AGENT_PACK_VERSION}`

const state = ref<OfficeAgentPackBootstrapState>({
  phase: 'idle',
  preview: null,
  active_version: null,
  error: null,
  checked_at: null,
})
const handledPromptKeys = new Set<string>()
let activeEnsure: Promise<void> | null = null
let channel: BroadcastChannel | null = null
let translate: (key: string, params?: Record<string, unknown>) => string = (key) => key

export const officeAgentPackState = readonly(state)

function t(key: string, params?: Record<string, unknown>): string {
  return translate(key, params)
}

export function setOfficeAgentPackTranslator(
  translator: (key: string, params?: Record<string, unknown>) => string,
): void {
  translate = translator
}

function bootstrapKey(): string {
  return `${api.gatewayUrl}|${OFFICE_AGENT_PACK_ID}|${OFFICE_AGENT_PACK_VERSION}`
}

function setState(patch: Partial<OfficeAgentPackBootstrapState>): void {
  state.value = { ...state.value, ...patch }
}

function getChannel(): BroadcastChannel | null {
  if (channel || typeof BroadcastChannel === 'undefined') return channel
  channel = new BroadcastChannel(CHANNEL_NAME)
  channel.addEventListener('message', (event: MessageEvent<BootstrapBroadcast>) => {
    const message = event.data
    if (!message || message.type !== 'handled' || message.key !== bootstrapKey()) return
    handledPromptKeys.add(message.key)
    setState({
      phase: message.phase,
      active_version: message.active_version,
      checked_at: Date.now(),
    })
  })
  return channel
}

function broadcastHandled(phase: OfficeAgentPackPhase, activeVersion: string | null): void {
  getChannel()?.postMessage({
    type: 'handled',
    key: bootstrapKey(),
    phase,
    active_version: activeVersion,
  } satisfies BootstrapBroadcast)
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
  const preview = await api.previewAgentPackInstall(officeAgentPackEnvelope)
  const expectedIdentity = {
    pack_id: OFFICE_AGENT_PACK_ID,
    owner: officeAgentPackEnvelope.manifest.metadata.owner,
    bundled_version: OFFICE_AGENT_PACK_VERSION,
  }
  const identityMismatch = Object.entries(expectedIdentity)
    .find(([field, expected]) => preview[field as keyof typeof expectedIdentity] !== expected)
  if (identityMismatch) {
    const [field, expected] = identityMismatch
    throw Object.assign(new Error(t('agentPack.previewIdentityMismatch', {
      field,
      expected,
      actual: preview[field as keyof typeof expectedIdentity],
    })), { code: 'agent_pack_identity_mismatch', field })
  }
  if (preview.integrity_digest !== OFFICE_AGENT_PACK_DIGEST) {
    throw Object.assign(new Error(t('agentPack.previewDigestMismatch')), { code: 'agent_pack_digest_mismatch' })
  }
  const knownActions = new Set(['install', 'upgrade', 'up_to_date', 'newer_installed', 'conflict'])
  if (!knownActions.has(preview.action)) {
    throw Object.assign(new Error(t('agentPack.unsupportedPreviewAction', { action: preview.action })), { code: 'unsupported_agent_pack_action' })
  }
  setState({
    phase: preview.action as OfficeAgentPackPhase,
    preview,
    active_version: preview.installed_version,
    error: null,
    checked_at: Date.now(),
  })
  return preview
}

function clearPackNotifications(): void {
  const { dismissByKey } = useNotifications()
  dismissByKey(NOTIFICATION_KEY)
  dismissByKey(DEFERRED_NOTIFICATION_KEY)
}

async function confirmAndInstall(preview: AgentPackInstallPreviewDto): Promise<void> {
  if ((preview.action !== 'install' && preview.action !== 'upgrade') || !preview.preview_id) return
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
      source: 'OfficeAgentPack',
      persistence: 'sticky',
      action: {
        label: t(isUpgrade ? 'agentPack.upgradeAction' : 'agentPack.installAction'),
        run: () => ensureOfficeAgentPack({ force: true, prompt: true }),
      },
    })
    broadcastHandled('deferred', preview.installed_version)
    return
  }

  await applyPreview(preview)
}

async function applyPreview(preview: AgentPackInstallPreviewDto): Promise<void> {
  const { notify, status, dismissByKey } = useNotifications()
  const isUpgrade = preview.action === 'upgrade'
  const task = notify.task({
    key: `${NOTIFICATION_KEY}-task`,
    title: t(isUpgrade ? 'agentPack.upgradingTitle' : 'agentPack.installingTitle'),
    message: t('agentPack.installingMessage'),
    source: 'OfficeAgentPack',
  })
  setState({ phase: 'installing', error: null })

  try {
    const result = await api.installAgentPack(
      OFFICE_AGENT_PACK_ID,
      { preview_id: preview.preview_id!, envelope: officeAgentPackEnvelope },
      {
        if_match: isUpgrade ? (preview.etag ?? `"${preview.revision}"`) : null,
        idempotency_key: `${OFFICE_AGENT_PACK_ID}:${OFFICE_AGENT_PACK_VERSION}:${OFFICE_AGENT_PACK_DIGEST}`,
      },
    )
    settleInstalled(result)
    dismissByKey(NOTIFICATION_KEY)
    dismissByKey(DEFERRED_NOTIFICATION_KEY)
    task.succeed({ message: t(result.status === 'updated' ? 'agentPack.upgradeSucceeded' : 'agentPack.installSucceeded') })
  } catch (error) {
    if (isHttpStatus(error, 409) || isHttpStatus(error, 412) || isCodedError(error, 'conflict')) {
      try {
        const current = await previewPack()
        if (current.action === 'up_to_date' || current.action === 'newer_installed') {
          settleInstalledFromPreview(current)
          task.succeed({ message: t('agentPack.alreadyInstalled') })
          return
        }
      } catch {
        // Preserve the original install error below.
      }
    }

    const message = error instanceof Error ? error.message : t('agentPack.installFailed')
    setState({ phase: 'error', error: message })
    task.fail(error, { title: t('agentPack.installFailed'), source: 'OfficeAgentPack' })
    status.error({
      key: NOTIFICATION_KEY,
      title: t('agentPack.installFailed'),
      message,
      source: 'OfficeAgentPack',
      action: { label: t('settings.retry'), run: () => ensureOfficeAgentPack({ force: true, prompt: true }) },
    })
  }
}

function settleInstalled(result: AgentPackInstallResultDto): void {
  const phase = result.status === 'newer_installed' ? 'newer_installed' : 'up_to_date'
  setState({
    phase,
    active_version: result.active_version,
    error: null,
    checked_at: Date.now(),
  })
  handledPromptKeys.add(bootstrapKey())
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
    checked_at: Date.now(),
  })
  handledPromptKeys.add(bootstrapKey())
  broadcastHandled(phase, preview.installed_version)
}

async function runEnsure(options: EnsureOptions): Promise<void> {
  const key = bootstrapKey()
  const { status } = useNotifications()
  setState({ phase: 'checking', error: null })

  try {
    const preview = await previewPack()
    if (preview.action === 'up_to_date' || preview.action === 'newer_installed') {
      settleInstalledFromPreview(preview)
      return
    }
    if (preview.action === 'conflict') {
      const message = [...(preview.differences ?? []), ...preview.warnings].join('\n') || t('agentPack.conflictMessage')
      setState({ phase: 'conflict', error: message })
      status.error({
        key: NOTIFICATION_KEY,
        title: t('agentPack.conflictTitle'),
        message,
        source: 'OfficeAgentPack',
        action: { label: t('settings.retry'), run: () => ensureOfficeAgentPack({ force: true, prompt: true }) },
      })
      return
    }
    if ((preview.action === 'install' || preview.action === 'upgrade') && options.prompt !== false) {
      if (!options.force && handledPromptKeys.has(key)) {
        setState({ phase: 'deferred' })
        return
      }
      await confirmAndInstall(preview)
    }
  } catch (error) {
    if (isHttpStatus(error, 403) || isCodedError(error, 'agent_pack_management_forbidden')) {
      const message = t('agentPack.ownerRequiredMessage')
      setState({ phase: 'owner_required', error: message, checked_at: Date.now() })
      handledPromptKeys.add(key)
      status.warning({
        key: NOTIFICATION_KEY,
        title: t('agentPack.ownerRequiredTitle'),
        message,
        source: 'OfficeAgentPack',
      })
      broadcastHandled('owner_required', null)
      return
    }
    const message = error instanceof Error ? error.message : t('agentPack.previewFailed')
    setState({ phase: 'error', error: message, checked_at: Date.now() })
    status.error({
      key: NOTIFICATION_KEY,
      title: t('agentPack.previewFailed'),
      message,
      source: 'OfficeAgentPack',
      action: { label: t('settings.retry'), run: () => ensureOfficeAgentPack({ force: true, prompt: true }) },
    })
  }
}

async function withCrossWindowLock(run: () => Promise<void>): Promise<void> {
  getChannel()
  const lockManager = typeof navigator !== 'undefined'
    ? (navigator as Navigator & { locks?: LockManager }).locks
    : undefined
  if (!lockManager) {
    await run()
    return
  }
  await lockManager.request(LOCK_NAME, run)
}

export function ensureOfficeAgentPack(options: EnsureOptions = {}): Promise<void> {
  if (activeEnsure) return activeEnsure
  activeEnsure = withCrossWindowLock(() => runEnsure(options)).finally(() => {
    activeEnsure = null
  })
  return activeEnsure
}

export function refreshOfficeAgentPack(): Promise<void> {
  return ensureOfficeAgentPack({ force: true, prompt: false })
}

export function installOrUpgradeOfficeAgentPack(): Promise<void> {
  return ensureOfficeAgentPack({ force: true, prompt: true })
}

export function __resetOfficeAgentPackBootstrapForTests(): void {
  state.value = { phase: 'idle', preview: null, active_version: null, error: null, checked_at: null }
  handledPromptKeys.clear()
  activeEnsure = null
  channel?.close()
  channel = null
  translate = (key) => key
}
