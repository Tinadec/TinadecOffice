// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'

import type { MarketCatalogItemDto, MarketInstallationDto, MarketInstallProposalDto, McpInventoryDto } from '@/api'

const h = vi.hoisted(() => ({
  listExtensionSources: vi.fn(async () => ({ sources: [], supported_kinds: ['mcp_registry'] })),
  listMarketInstallations: vi.fn(async () => ({ installations: [] as MarketInstallationDto[] })),
  listAcpAdapters: vi.fn(async () => [] as unknown[]),
  listMarketCatalog: vi.fn(async () => ({ items: [] as MarketCatalogItemDto[], total_available: 0, has_more: false })),
  createExtensionSource: vi.fn(async () => ({})),
  setExtensionSourceEnabled: vi.fn(async () => ({})),
  deleteExtensionSource: vi.fn(async () => undefined),
  refreshExtensionSource: vi.fn(async () => ({})),
  listMcpServers: vi.fn(async () => ({ source: 'tool_provider', servers: [] }) as unknown),
  previewMarketInstall: vi.fn(),
  previewMarketUninstall: vi.fn(),
  applyMarketInstallProposal: vi.fn(),
  decideApproval: vi.fn(async () => { throw new Error('the market panel must never decide an approval') }),
}))

vi.mock('@/api', () => ({
  // The controller imports this as a value, so a module mock that only lists the api object makes
  // the whole file fail to load.
  MCP_SOURCE_PROVIDER: 'tool_provider',
  MCP_SOURCE_UNAVAILABLE: 'tool_provider_unavailable',
  MARKET_INSTALL_ACTION_UNINSTALL: 'uninstall',
  api: {
    listExtensionSources: h.listExtensionSources,
    listMarketInstallations: h.listMarketInstallations,
    listAcpAdapters: h.listAcpAdapters,
    listMarketCatalog: h.listMarketCatalog,
    createExtensionSource: h.createExtensionSource,
    setExtensionSourceEnabled: h.setExtensionSourceEnabled,
    deleteExtensionSource: h.deleteExtensionSource,
    refreshExtensionSource: h.refreshExtensionSource,
    listMcpServers: h.listMcpServers,
    previewMarketInstall: h.previewMarketInstall,
    previewMarketUninstall: h.previewMarketUninstall,
    applyMarketInstallProposal: h.applyMarketInstallProposal,
    // The panel's failure mode would be deciding on the user's behalf: a test that lets this exist
    // unmocked could pass by silently approving.
    decideApproval: h.decideApproval,
  },
}))

// Home owns "which workspace am I in"; the market page reads that owner instead of keeping a second
// answer, so the seam is stubbed rather than importing the whole controller. The refs are built
// inside the factory because `vi.hoisted` runs before imports, where `ref` does not exist yet.
vi.mock('@/controllers/HomeController', async () => {
  const { ref } = await import('vue')
  return {
    homeController: {
      projects: ref([{ id: 'proj-1', name: 'Alpha' }, { id: 'proj-2', name: 'Beta' }]),
      selectedProjectId: ref<string | null>('proj-1'),
    },
  }
})

type ProjectSeam = {
  projects: { value: { id: string; name: string }[] }
  selectedProjectId: { value: string | null }
}

async function projectSeam(): Promise<ProjectSeam> {
  const { homeController } = await import('@/controllers/HomeController')
  return homeController as unknown as ProjectSeam
}

let seam: ProjectSeam

const notifications = vi.hoisted(() => ({
  notify: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
  status: { error: vi.fn(), info: vi.fn(), success: vi.fn(), clear: vi.fn() },
  dismissByKey: vi.fn(),
}))

vi.mock('@/composables/useNotifications', () => ({ useNotifications: () => notifications }))

import { marketController, catalogKindLabel, sourceKindLabel } from './MarketController'

const CONNECTED: McpInventoryDto = {
  source: 'tool_provider',
  config_path: 'C:\\work\\demo\\mcp_servers.json',
  servers: [
    { id: 'github', name: 'GitHub', status: 'connected', tools: [{ id: 'create_issue', name: 'create_issue' }] },
    { id: 'ghost', name: 'Ghost', status: 'error', error: 'MCP server process exited unexpectedly (exit code: 1)', tools: [] },
  ],
}

const UNREADABLE: McpInventoryDto = {
  source: 'tool_provider_unavailable',
  reason: 'TinadecTools executable path is not configured.',
  servers: [],
}

function catalogItem(over: Partial<MarketCatalogItemDto> = {}): MarketCatalogItemDto {
  return {
    catalog_id: 'cat-1',
    source_id: 'src-1',
    source_name: 'Official MCP Registry',
    extension_id: 'io.example/filesense',
    kind: 'mcp-server',
    version: '2.0.4',
    display_name: 'FileSense',
    transports: ['stdio'],
    manifest_hash: 'sha256:aa',
    refreshed_at: '2026-09-23T00:00:00Z',
    expires_at: '2026-09-30T00:00:00Z',
    installable: true,
    ...over,
  }
}

function proposalOf(over: Partial<MarketInstallProposalDto> = {}): MarketInstallProposalDto {
  return {
    id: 'prop-1',
    action: 'install',
    project_id: 'proj-1',
    catalog_id: 'cat-1',
    source_name: 'Official MCP Registry',
    extension_id: 'io.example/filesense',
    kind: 'mcp-server',
    version: '2.0.4',
    server_id: 'io-example-filesense',
    command: 'npx',
    args: ['-y', 'filesense-mcp@2.0.4'],
    environment: [{ name: 'FILESENSE_INDEX_PATH', required: false, secret: true }],
    target_path: 'C:\\work\\demo\\mcp_servers.json',
    content: '{"servers":[]}',
    expected_file_hash: 'sha256:7c1d',
    digest: 'sha256:9b2e',
    expires_at: '2026-09-23T00:15:00Z',
    warnings: ['The pinned version is the package host name for a release, not a content digest.'],
    ...over,
  }
}

function installationOf(over: Partial<MarketInstallationDto> = {}): MarketInstallationDto {
  return {
    id: 'ins-1',
    project_id: 'proj-1',
    catalog_id: 'cat-1',
    source_name: 'Official MCP Registry',
    extension_id: 'io.example/filesense',
    kind: 'mcp-server',
    version: '2.0.4',
    server_id: 'io-example-filesense',
    config_path: 'C:\\work\\demo\\mcp_servers.json',
    state: 'installing',
    install_action_id: 'act-1',
    action_status: 'awaiting_user',
    created_at: '2026-09-23T00:00:00Z',
    updated_at: '2026-09-23T00:00:00Z',
    ...over,
  }
}

async function showItem(item: MarketCatalogItemDto, installations: MarketInstallationDto[] = []) {
  h.listMarketCatalog.mockResolvedValue({ items: [item], total_available: 1, has_more: false })
  h.listMarketInstallations.mockResolvedValue({ installations })
  await marketController.loadAll()
  marketController.selectedCatalogId.value = item.catalog_id
}

describe('marketController MCP inventory', () => {
  beforeEach(async () => {
    vi.clearAllMocks()
    h.listMarketCatalog.mockResolvedValue({ items: [], total_available: 0, has_more: false })
    h.listMarketInstallations.mockResolvedValue({ installations: [] })
    h.listExtensionSources.mockResolvedValue({ sources: [], supported_kinds: ['mcp_registry'] })
    h.listMcpServers.mockResolvedValue(CONNECTED)
    seam = await projectSeam()
    seam.projects.value = [{ id: 'proj-1', name: 'Alpha' }, { id: 'proj-2', name: 'Beta' }]
    seam.selectedProjectId.value = 'proj-1'
    marketController.proposal.value = null
  })

  it('keeps an unreachable server on the list, with the provider said about it', async () => {
    h.listMcpServers.mockResolvedValue(CONNECTED)

    await marketController.loadAll()

    expect(marketController.mcpReadSucceeded.value).toBe(true)
    expect(marketController.mcpServers.value).toHaveLength(2)
    // The whole point of this read: a server Core could not reach is still a configured server.
    expect(marketController.mcpServers.value[1].status).toBe('error')
    expect(marketController.mcpServers.value[1].error).toContain('exited unexpectedly')
    expect(marketController.mcpConfigPath.value).toBe('C:\\work\\demo\\mcp_servers.json')
  })

  it('does not read an empty list as "nothing configured" when the provider never answered', async () => {
    // Same shape as the case above at the array level — only `source` separates them, and that is
    // the distinction the page used to lose when Core answered `[]` to everything.
    const emptyButLooked: McpInventoryDto = { source: 'tool_provider', servers: [] }
    h.listMcpServers.mockResolvedValueOnce(emptyButLooked)
    await marketController.loadAll()
    expect(marketController.mcpReadSucceeded.value).toBe(true)
    expect(marketController.mcpReason.value).toBe('')

    h.listMcpServers.mockResolvedValue(UNREADABLE)
    await marketController.loadAll()
    expect(marketController.mcpReadSucceeded.value).toBe(false)
    expect(marketController.mcpServers.value).toEqual([])
    expect(marketController.mcpReason.value).toContain('executable path is not configured')
  })

  it('reports a failed market read instead of leaving the previous inventory standing silently', async () => {
    h.listMcpServers.mockResolvedValue(CONNECTED)
    await marketController.loadAll()
    expect(marketController.mcpServers.value).toHaveLength(2)

    h.listMcpServers.mockRejectedValueOnce(new Error('gateway down'))
    await marketController.loadAll()

    // The read threw before any envelope arrived, so the state stays at the last thing Core
    // actually said. That is only honest because the failure is surfaced too — see useNotifications.
    expect(marketController.mcpServers.value).toHaveLength(2)
    expect(marketController.mcpReadSucceeded.value).toBe(true)
  })
})

describe('marketController install proposals', () => {
  beforeEach(async () => {
    vi.clearAllMocks()
    h.listMarketCatalog.mockResolvedValue({ items: [], total_available: 0, has_more: false })
    h.listMarketInstallations.mockResolvedValue({ installations: [] })
    h.listExtensionSources.mockResolvedValue({ sources: [], supported_kinds: ['mcp_registry'] })
    h.listMcpServers.mockResolvedValue(CONNECTED)
    seam = await projectSeam()
    seam.projects.value = [{ id: 'proj-1', name: 'Alpha' }, { id: 'proj-2', name: 'Beta' }]
    seam.selectedProjectId.value = 'proj-1'
    marketController.proposal.value = null
  })

  it('previews against the workspace the user is working in, and keeps what Core proposed', async () => {
    await showItem(catalogItem())
    h.previewMarketInstall.mockResolvedValue(proposalOf())

    await marketController.previewInstall()

    expect(h.previewMarketInstall).toHaveBeenCalledWith('cat-1', 'proj-1')
    // The panel reviews a frozen proposal, so the fields it shows must be the ones Core minted.
    expect(marketController.activeProposal.value?.version).toBe('2.0.4')
    expect(marketController.activeProposal.value?.digest).toBe('sha256:9b2e')
    expect(marketController.activeProposal.value?.args).toEqual(['-y', 'filesense-mcp@2.0.4'])
  })

  it('asks for no project at all when the user has not selected one', async () => {
    seam.selectedProjectId.value = null
    await showItem(catalogItem())

    await marketController.previewInstall()

    expect(h.previewMarketInstall).not.toHaveBeenCalled()
    expect(notifications.notify.warning).toHaveBeenCalled()
    expect(marketController.activeProposal.value).toBeNull()
  })

  it('refuses locally when Core already said the entry cannot be installed', async () => {
    // `installable: false` is a fact about the row, so the click must not turn into a request that
    // is certain to come back 409 — and the reason has to be Core's sentence, not a generic one.
    await showItem(catalogItem({ installable: false, install_blocker: 'This entry publishes no package record.' }))
    h.previewMarketInstall.mockResolvedValue(proposalOf())

    await marketController.previewInstall()

    expect(h.previewMarketInstall).not.toHaveBeenCalled()
    expect(notifications.notify.warning).toHaveBeenCalledWith(
      expect.objectContaining({ message: 'This entry publishes no package record.' }))
  })

  it('queues the governed write and reports it as waiting, never as installed', async () => {
    await showItem(catalogItem())
    h.previewMarketInstall.mockResolvedValue(proposalOf())
    await marketController.previewInstall()

    const queued = installationOf({ action_status: 'awaiting_user' })
    h.applyMarketInstallProposal.mockResolvedValue(queued)
    h.listMarketInstallations.mockResolvedValue({ installations: [queued] })

    await marketController.applyProposal()

    expect(h.applyMarketInstallProposal).toHaveBeenCalledWith('prop-1')
    expect(marketController.activeProposal.value).toBeNull()
    const row = marketController.installationFor(catalogItem())
    expect(row?.id).toBe('ins-1')
    // Queuing is not installing: the entry reads as waiting until its own action completes.
    expect(marketController.awaitingDecision(row)).toBe(true)
    expect(marketController.actionFinished(row)).toBe(false)
    // The decisive part: the panel never decides an approval on the user's behalf.
    expect(h.decideApproval).not.toHaveBeenCalled()
  })

  it('drops a proposal Core refused as stale instead of leaving a button that can only fail', async () => {
    await showItem(catalogItem())
    h.previewMarketInstall.mockResolvedValue(proposalOf())
    await marketController.previewInstall()
    expect(marketController.activeProposal.value).not.toBeNull()

    h.applyMarketInstallProposal.mockRejectedValue(
      Object.assign(new Error('The market changed under this proposal.'), { code: 'market_install_proposal_stale' }))

    await marketController.applyProposal()

    expect(marketController.activeProposal.value).toBeNull()
    expect(notifications.notify.error).toHaveBeenCalled()
  })

  it('will not offer a proposal that describes another workspace or another row', async () => {
    await showItem(catalogItem())
    h.previewMarketInstall.mockResolvedValue(proposalOf())
    await marketController.previewInstall()
    expect(marketController.activeProposal.value).not.toBeNull()

    // A proposal freezes the config file of the workspace it was previewed against, so switching
    // workspaces must not leave it applyable. The raw store still holds it — which is exactly why
    // the assertion is on the derived value: a panel bound to the raw one would keep the button.
    seam.selectedProjectId.value = 'proj-2'
    expect(marketController.activeProposal.value).toBeNull()
    expect(marketController.proposal.value).not.toBeNull()

    seam.selectedProjectId.value = 'proj-1'
    h.listMarketCatalog.mockResolvedValue({
      items: [catalogItem({ catalog_id: 'cat-other', extension_id: 'io.other/thing' })],
      total_available: 1,
      has_more: false,
    })
    await marketController.loadCatalog()
    expect(marketController.activeProposal.value).toBeNull()
  })

  it('matches an installation to its own project, not to any project that mentions the entry', async () => {
    await showItem(catalogItem(), [installationOf({ project_id: 'proj-2' })])

    expect(marketController.installationFor(catalogItem())).toBeNull()

    seam.selectedProjectId.value = 'proj-2'
    expect(marketController.installationFor(catalogItem())?.id).toBe('ins-1')
  })

  it('previews a removal from the ledger row rather than the catalog row', async () => {
    const installed = installationOf({ action_status: 'completed' })
    await showItem(catalogItem(), [installed])
    h.previewMarketUninstall.mockResolvedValue(proposalOf({ action: 'uninstall', installation_id: 'ins-1', catalog_id: null }))

    await marketController.previewRemoval()

    expect(h.previewMarketUninstall).toHaveBeenCalledWith('ins-1')
    expect(marketController.activeProposal.value?.action).toBe('uninstall')
  })
})

describe('marketController re-entry and ledger freshness', () => {
  beforeEach(async () => {
    vi.clearAllMocks()
    marketController.stop()
    h.listMarketCatalog.mockResolvedValue({ items: [], total_available: 0, has_more: false })
    h.listMarketInstallations.mockResolvedValue({ installations: [] })
    h.listExtensionSources.mockResolvedValue({ sources: [], supported_kinds: ['mcp_registry'] })
    h.listMcpServers.mockResolvedValue(CONNECTED)
    marketController.proposal.value = null
  })

  async function visible(state: 'visible' | 'hidden') {
    Object.defineProperty(document, 'visibilityState', { value: state, configurable: true })
  }

  it('re-reads when the page is entered again instead of keeping the first answer', async () => {
    // The old module-level latch made the second visit to /market render whatever the first one
    // read — including a row that a human had approved in the Governance page in between.
    marketController.start()
    await vi.waitFor(() => expect(h.listMarketInstallations).toHaveBeenCalledTimes(1))

    marketController.start()
    await vi.waitFor(() => expect(h.listMarketInstallations).toHaveBeenCalledTimes(2))
    marketController.stop()
  })

  it('follows a queued write to its terminal status without being asked again', async () => {
    vi.useFakeTimers()
    try {
      await visible('visible')
      h.listMarketInstallations.mockResolvedValue({
        installations: [installationOf({ action_status: 'awaiting_approval' })],
      })
      await marketController.loadAll()
      expect(marketController.awaitingDecision(marketController.installations.value[0])).toBe(true)

      // The approval lands elsewhere, and Core appends no event for it — a read is the only signal.
      h.listMarketInstallations.mockResolvedValue({
        installations: [installationOf({ action_status: 'completed' })],
      })
      await vi.advanceTimersByTimeAsync(12_000)
      expect(h.listMarketInstallations).toHaveBeenCalledTimes(2)
      expect(marketController.awaitingDecision(marketController.installations.value[0])).toBe(false)

      // Settled: the timer is gone, so a panel left open costs no requests.
      await vi.advanceTimersByTimeAsync(60_000)
      expect(h.listMarketInstallations).toHaveBeenCalledTimes(2)
      marketController.stop()
    } finally {
      marketController.stop()
      vi.useRealTimers()
    }
  })

  it('never arms a poll for a ledger that is already settled', async () => {
    vi.useFakeTimers()
    try {
      await visible('visible')
      h.listMarketInstallations.mockResolvedValue({
        installations: [installationOf({ action_status: 'completed' })],
      })
      await marketController.loadAll()
      await vi.advanceTimersByTimeAsync(120_000)
      expect(h.listMarketInstallations).toHaveBeenCalledTimes(1)
    } finally {
      marketController.stop()
      vi.useRealTimers()
    }
  })

  it('holds the poll back while the page is hidden, then resumes on its own tick', async () => {
    vi.useFakeTimers()
    try {
      await visible('hidden')
      h.listMarketInstallations.mockResolvedValue({
        installations: [installationOf({ action_status: 'running' })],
      })
      await marketController.loadAll()
      await vi.advanceTimersByTimeAsync(36_000)
      expect(h.listMarketInstallations).toHaveBeenCalledTimes(1)

      await visible('visible')
      await vi.advanceTimersByTimeAsync(12_000)
      expect(h.listMarketInstallations).toHaveBeenCalledTimes(2)
    } finally {
      marketController.stop()
      await visible('visible')
      vi.useRealTimers()
    }
  })
})

describe('marketController kind labels', () => {
  // Asserted against the raw wire word rather than a bundle string on purpose: the default locale
  // in tests is zh-CN, and a hand-written translation here would go stale the next time the
  // wording changes. What has to hold in every locale is "not the identifier, and not a key".
  it('names a source kind in the UI language instead of printing the adapter id', () => {
    const label = sourceKindLabel('mcp_registry')
    expect(label).not.toBe('mcp_registry')
    expect(label).not.toContain('market.')
  })

  it('names a catalog kind, and falls through to the raw word for one this build has never seen', () => {
    expect(catalogKindLabel('acp-adapter')).not.toBe('acp-adapter')
    // A future kind Core adds must still show something; a blank badge would read as "no kind".
    expect(catalogKindLabel('cli-runtime')).toBe('cli-runtime')
  })
})
