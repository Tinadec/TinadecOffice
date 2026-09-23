import { computed, reactive, ref, watch } from 'vue'
import i18n from '@/i18n'
import {
  api,
  MARKET_INSTALL_ACTION_UNINSTALL,
  MCP_SOURCE_PROVIDER,
  type AcpAdapterDto,
  type ExtensionSourceDto,
  type MarketCatalogItemDto,
  type MarketInstallationDto,
  type MarketInstallProposalDto,
  type McpInventoryDto,
  type McpServerDto,
} from '@/api'
import { homeController } from '@/controllers/HomeController'
import { useNotifications } from '@/composables/useNotifications'
import { isUserToolActionTerminal, userToolActionNeedsDecision } from '@/userToolAction'

// ---------------------------------------------------------------------------
// MarketController — the single domain controller for the Market page.
// Owns all market data/state so the filter/catalog/detail cards share one
// source of truth instead of each opening its own fetch.
// ---------------------------------------------------------------------------


// Module-level composables are safe (they share module refs); `useI18n` is NOT
// callable outside setup(), so translate via the global instance instead.
const t = i18n.global.t
const { notify, status, dismissByKey } = useNotifications()

const sources = ref<ExtensionSourceDto[]>([])
/** The kinds this build has an adapter for, straight from Core. A picker that guesses offers a kind that 400s. */
const supportedKinds = ref<string[]>([])
const catalog = ref<MarketCatalogItemDto[]>([])
const installations = ref<MarketInstallationDto[]>([])
const mcpInventory = ref<McpInventoryDto | null>(null)
const acpAdapters = ref<AcpAdapterDto[]>([])
const selectedCatalogId = ref('')
const kindFilter = ref('all')
const sourceFilter = ref('')
const query = ref('')
const busy = ref(false)
const loading = ref(false)

/**
 * The frozen install or removal a human is being asked to decide on, or null. A proposal is the
 * only thing that can queue a write, it names the project it writes into, and it stops being
 * applyable when it expires or when the market moves under it — so it is held here and dropped on
 * any of those events rather than kept as a button that will fail.
 */
const proposal = ref<MarketInstallProposalDto | null>(null)
const proposalBusy = ref(false)

const sourceForm = reactive({
  name: 'MCP Registry',
  kind: '',
  location: '',
})

export const kindOptions = [
  { key: 'all', label: 'All', icon: null },
  { key: 'skill', label: 'Skill', icon: null },
  { key: 'mcp-server', label: 'MCP', icon: null },
  { key: 'acp-adapter', label: 'ACP', icon: null },
]

const selectedItem = computed(() =>
  catalog.value.find((item) => item.catalog_id === selectedCatalogId.value) ?? catalog.value[0] ?? null
)

/**
 * The workspace a preview would write into. There is deliberately one owner of "which project am I
 * working in" — the composer's selector on Home — because a second project picker would be a
 * second answer to that question, and installs are per project.
 */
const targetProjectId = computed(() => homeController.selectedProjectId.value)
const targetProject = computed(() =>
  homeController.projects.value.find((project) => project.id === targetProjectId.value) ?? null
)

// Keyed by project plus the extension id the source published, because that pair is what an
// installation row and a catalog row both carry. The config key Core writes is a slug of the
// extension id — deriving it here would copy Core's algorithm into the renderer.
const installationKeys = computed(() => {
  const map = new Map<string, MarketInstallationDto>()
  for (const row of installations.value) map.set(`${row.project_id}\u0000${row.extension_id}`, row)
  return map
})

function installationFor(item: MarketCatalogItemDto): MarketInstallationDto | null {
  const project = targetProjectId.value
  if (!project) return null
  return installationKeys.value.get(`${project}\u0000${item.extension_id}`) ?? null
}

const selectedInstallation = computed(() =>
  selectedItem.value ? installationFor(selectedItem.value) : null
)

/**
 * The proposal is bound to the row and the workspace it was previewed against, because those are the
 * two things whose state froze into it. Rather than watching another controller's state (a module
 * that reaches across for a watcher is a load order waiting to break), a proposal is simply not
 * offered once it describes something else.
 */
const activeProposal = computed(() => {
  const pending = proposal.value
  if (!pending) return null
  if (pending.project_id !== targetProjectId.value) return null
  if (pending.action === MARKET_INSTALL_ACTION_UNINSTALL) {
    return pending.installation_id && selectedInstallation.value?.id === pending.installation_id ? pending : null
  }
  return pending.catalog_id && selectedItem.value?.catalog_id === pending.catalog_id ? pending : null
})

/** Whether the row's own write is parked in front of a human, on the approval surface. */
function awaitingDecision(row: MarketInstallationDto | null): boolean {
  return !!row?.action_status && userToolActionNeedsDecision(row.action_status)
}

function actionFinished(row: MarketInstallationDto | null): boolean {
  return !!row?.action_status && isUserToolActionTerminal(row.action_status)
}

/**
 * The MCP servers Core could actually see, plus the answer to "why is this list empty".
 *
 * There is deliberately no attribution from a catalog item to a server: Core reads the inventory
 * from the Tool Provider's config file, which knows nothing about which extension wrote it, so a
 * per-item runtime list would be invented. The previous version of this computed filtered on
 * `server.extension_id` — a field the route never sent — which made it permanently empty.
 */
const mcpServers = computed<McpServerDto[]>(() => mcpInventory.value?.servers ?? [])
const mcpSource = computed(() => mcpInventory.value?.source ?? '')
const mcpReadSucceeded = computed(() => mcpSource.value === MCP_SOURCE_PROVIDER)
const mcpReason = computed(() => mcpInventory.value?.reason ?? '')
const mcpConfigPath = computed(() => mcpInventory.value?.config_path ?? '')

async function run(label: string, action: () => Promise<void>) {
  busy.value = true
  try {
    await action()
  } catch (err) {
    notify.error(err, { title: `${label} failed`, source: 'market' })
  } finally {
    busy.value = false
  }
}

async function loadAll() {
  loading.value = true
  try {
    const [sourceListResult, installationList, inventory, adapters] = await Promise.all([
      api.listExtensionSources(),
      api.listMarketInstallations(),
      api.listMcpServers(),
      api.listAcpAdapters(),
    ])
    sources.value = sourceListResult.sources
    supportedKinds.value = sourceListResult.supported_kinds
    if (!sourceForm.kind) sourceForm.kind = sourceListResult.supported_kinds[0] ?? ''
    installations.value = installationList.installations
    mcpInventory.value = inventory
    acpAdapters.value = adapters
    if (!sourceFilter.value) {
      sourceFilter.value = sourceListResult.sources[0]?.id ?? ''
    }
    await loadCatalog()
    dismissByKey('market-load')
  } catch (err) {
    status.error({
      key: 'market-load',
      title: t('market.loadFailed'),
      message: t('app.loadFailedMessage'),
      details: err instanceof Error ? err.message : t('market.loadFailed'),
      source: 'market',
      action: { label: t('app.retry'), run: loadAll },
    })
  } finally {
    loading.value = false
  }
}

async function loadCatalog() {
  try {
    const page = await api.listMarketCatalog({
      kind: kindFilter.value,
      q: query.value.trim(),
      source_id: sourceFilter.value || undefined,
    })
    catalog.value = page.items
    if (!catalog.value.some((item) => item.catalog_id === selectedCatalogId.value)) {
      selectedCatalogId.value = catalog.value[0]?.catalog_id ?? ''
    }
    dismissByKey('market-catalog')
  } catch (err) {
    status.error({
      key: 'market-catalog',
      title: t('market.loadFailed'),
      message: t('app.loadFailedMessage'),
      details: err instanceof Error ? err.message : t('market.loadFailed'),
      source: 'market',
      action: { label: t('app.retry'), run: loadCatalog },
    })
  }
}

async function loadInstallations() {
  const list = await api.listMarketInstallations()
  installations.value = list.installations
}

function discardProposal() {
  proposal.value = null
}

/**
 * Asks Core what it would write, and shows that answer. Reading the Tool Provider's current config
 * is part of a preview, so this is a button rather than something that fires whenever a row is
 * clicked.
 */
async function previewInstall() {
  const item = selectedItem.value
  if (!item) return
  if (!targetProjectId.value) {
    notify.warning({ message: t('market.installNeedsProject'), source: 'market' })
    return
  }
  if (item.installable === false) {
    notify.warning({ message: item.install_blocker || t('market.notInstallable'), source: 'market' })
    return
  }
  proposalBusy.value = true
  try {
    await run('install preview', async () => {
      proposal.value = await api.previewMarketInstall(item.catalog_id, targetProjectId.value!)
    })
  } finally {
    proposalBusy.value = false
  }
}

async function previewRemoval() {
  const row = selectedInstallation.value
  if (!row) return
  proposalBusy.value = true
  try {
    await run('uninstall preview', async () => {
      proposal.value = await api.previewMarketUninstall(row.id)
    })
  } finally {
    proposalBusy.value = false
  }
}

/**
 * Queues the one governed write this proposal describes. Nothing lands here: the file changes when
 * a human approves the user tool action on the approval surface, and Core refuses a second action
 * for the same proposal, so pressing twice is safe.
 */
async function applyProposal() {
  const pending = activeProposal.value
  if (!pending) return
  await run('queue install', async () => {
    try {
      const row = await api.applyMarketInstallProposal(pending.id)
      proposal.value = null
      await loadInstallations()
      notify.info({
        message: t('market.queuedForApproval', {
          server: row.server_id,
          action: row.install_action_id || row.uninstall_action_id || '',
        }),
        source: 'market',
      })
    } catch (err) {
      // Expired, used, or the market moved: the reviewed bytes no longer describe anything, and a
      // panel that kept offering them would be a button that can only fail.
      proposal.value = null
      throw err
    }
  })
}

async function addSource() {
  await run('add source', async () => {
    await api.createExtensionSource({ name: sourceForm.name, kind: sourceForm.kind, location: sourceForm.location, enabled: true })
    sourceForm.location = ''
    await loadAll()
    notify.success({ message: 'Source added.', source: 'market' })
  })
}

async function toggleSource(sourceId: string, enabled: boolean) {
  await run('enable source', async () => {
    await api.setExtensionSourceEnabled(sourceId, enabled)
    await loadAll()
  })
}

async function removeSource(sourceId: string) {
  await run('delete source', async () => {
    try {
      await api.deleteExtensionSource(sourceId)
      await loadAll()
    } catch (err) {
      // A source whose entries are still installed is kept on purpose: its rows are what an
      // installed server is traceable to.
      notify.error(err, { title: t('market.deleteSourceFailed'), source: 'market' })
    }
  })
}

async function refreshSource(sourceId: string) {
  await run('refresh source', async () => {
    const outcome = await api.refreshExtensionSource(sourceId)
    await loadAll()
    // A refresh that was blocked or never reached the market leaves the catalog standing, which
    // looks exactly like success from the outside. Say which of the three happened.
    if (outcome.outcome !== 'fetched') {
      throw new Error(outcome.reason || `The source did not answer (${outcome.outcome}).`)
    }
    notify.success({
      message: outcome.truncated_pages
        ? `Refreshed ${outcome.fetched_rows} entry/entries from ${outcome.pages_fetched} page(s); the listing was longer than this.`
        : `Refreshed ${outcome.fetched_rows} entry/entries (${outcome.refused_rows} refused, ${outcome.removed_rows} dropped, ${outcome.retained_rows} kept because they are installed).`,
      source: 'market',
    })
  })
}

watch([kindFilter, sourceFilter], () => { void loadCatalog() })

let started = false
function start() {
  if (started) return
  started = true
  void loadAll()
}

export const marketController = {
  sources, supportedKinds, catalog, installations, acpAdapters,
  mcpInventory, mcpServers, mcpSource, mcpReadSucceeded, mcpReason, mcpConfigPath,
  selectedCatalogId, kindFilter, sourceFilter, query, busy, loading,
  proposal, activeProposal, proposalBusy, sourceForm,
  selectedItem, selectedInstallation, installationFor, awaitingDecision, actionFinished,
  targetProjectId, targetProject,
  start,
  loadAll, loadCatalog, loadInstallations,
  addSource, refreshSource, toggleSource, removeSource, discardProposal,
  previewInstall, previewRemoval, applyProposal,
}
