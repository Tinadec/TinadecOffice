import { computed, reactive, ref, watch } from 'vue'
import i18n from '@/i18n'
import { api, MCP_SOURCE_PROVIDER, type AcpAdapterDto, type ExtensionInstallPreviewDto, type ExtensionSourceDto, type InstalledExtensionDto, type MarketCatalogItemDto, type McpInventoryDto, type McpServerDto } from '@/api'
import { useNotifications } from '@/composables/useNotifications'

// ---------------------------------------------------------------------------
// MarketController — the single domain controller for the Market page.
// Owns all market data/state so the filter/catalog/detail cards share one
// source of truth instead of each opening its own fetch.
// ---------------------------------------------------------------------------


// Module-level composables are safe (they share module refs); `useI18n` is NOT
// callable outside setup(), so translate via the global instance instead.
const t = i18n.global.t
const { notify, status, confirm, dismissByKey } = useNotifications()

const sources = ref<ExtensionSourceDto[]>([])
const catalog = ref<MarketCatalogItemDto[]>([])
const installed = ref<InstalledExtensionDto[]>([])
const mcpInventory = ref<McpInventoryDto | null>(null)
const acpAdapters = ref<AcpAdapterDto[]>([])
const selectedCatalogId = ref('')
const kindFilter = ref('all')
const sourceFilter = ref('')
const query = ref('')
const busy = ref(false)
const loading = ref(false)

const preview = ref<ExtensionInstallPreviewDto | null>(null)
const directPreview = ref<ExtensionInstallPreviewDto | null>(null)

const sourceForm = reactive({
  name: 'Custom Marketplace',
  kind: 'marketplace-url',
  location: '',
})

const directForm = reactive({
  source_kind: 'local-directory',
  source_location: '',
  manifest_json: '',
})

export const kindOptions = [
  { key: 'all', label: 'All', icon: null },
  { key: 'skill', label: 'Skill', icon: null },
  { key: 'mcp-server', label: 'MCP', icon: null },
  { key: 'acp-adapter', label: 'ACP', icon: null },
]

export const sourceKindOptions = [
  'local-directory',
  'local-archive',
  'github',
  'git',
  'https-archive',
  'marketplace-url',
  'mcpb',
  'dxt',
]

const selectedItem = computed(() =>
  catalog.value.find((item) => item.catalog_id === selectedCatalogId.value) ?? catalog.value[0] ?? null
)

const installedByExtensionId = computed(() => {
  const map = new Map<string, InstalledExtensionDto>()
  for (const item of installed.value) map.set(item.extension_id, item)
  return map
})

const selectedInstalled = computed(() => {
  const item = selectedItem.value
  return item ? installedByExtensionId.value.get(item.extension_id) ?? null : null
})

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

const builtinSource = computed(() => sources.value.find((s) => s.location.includes('tinadec://')))

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
    const [sourceListResult, installedList, inventory, adapters] = await Promise.all([
      api.listExtensionSources(),
      api.listInstalledExtensions(),
      api.listMcpServers(),
      api.listAcpAdapters(),
    ])
    // The envelope also carries supported_kinds, which the add-source form needs before it can
    // offer a kind this build can read; it is still hardcoded there, and that is registered as a
    // gap rather than papered over by dropping the field on the floor here.
    const sourceList = sourceListResult.sources
    sources.value = sourceList
    installed.value = installedList
    mcpInventory.value = inventory
    acpAdapters.value = adapters
    if (!sourceFilter.value) {
      sourceFilter.value = sourceList[0]?.id ?? ''
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

async function loadPreview() {
  if (!selectedItem.value) {
    preview.value = null
    return
  }
  try {
    preview.value = await api.previewExtensionInstall({ catalog_id: selectedItem.value.catalog_id })
  } catch (err) {
    notify.error(err, { title: t('market.loadFailed'), source: 'market' })
  }
}

async function addSource() {
  await run('add source', async () => {
    await api.createExtensionSource({ name: sourceForm.name, kind: sourceForm.kind, location: sourceForm.location, enabled: true })
    sourceForm.location = ''
    await loadAll()
    notify.success({ message: 'Source added.', source: 'market' })
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
        : `Refreshed ${outcome.fetched_rows} entry/entries (${outcome.refused_rows} refused, ${outcome.removed_rows} dropped).`,
      source: 'market',
    })
  })
}

async function approveAndInstallCatalog() {
  const item = selectedItem.value
  if (!item) return
  await run('install extension', async () => {
    const first = await api.installExtension({ catalog_id: item.catalog_id })
    let installedExtension = first.extension
    if (first.approval_required && first.approval) {
      await api.decideApproval(first.approval.id, 'approved')
      const second = await api.installExtension({ catalog_id: item.catalog_id, approval_id: first.approval.id })
      installedExtension = second.extension ?? installedExtension
    }
    await loadAll()
    await loadPreview()
    if (!installedExtension || ['failed', 'error', 'blocked'].includes(installedExtension.status)) {
      throw new Error(installedExtension?.status_message || `${item.display_name} was not installed.`)
    }
    notify.success({ message: installedExtension.status_message || `${item.display_name} installed.`, source: 'market' })
  })
}

async function previewDirectInstall() {
  await run('preview direct install', async () => {
    directPreview.value = await api.previewExtensionInstall({
      source_kind: directForm.source_kind,
      source_location: directForm.source_location,
      manifest_json: directForm.manifest_json || null,
    })
  })
}

async function approveAndInstallDirect() {
  await run('install direct extension', async () => {
    const payload = { source_kind: directForm.source_kind, source_location: directForm.source_location, manifest_json: directForm.manifest_json || null }
    const first = await api.installExtension(payload)
    let installedExtension = first.extension
    if (first.approval_required && first.approval) {
      await api.decideApproval(first.approval.id, 'approved')
      const second = await api.installExtension({ ...payload, approval_id: first.approval.id })
      installedExtension = second.extension ?? installedExtension
    }
    if (!installedExtension || ['failed', 'error', 'blocked'].includes(installedExtension.status)) {
      throw new Error(installedExtension?.status_message || `${first.preview.display_name} was not installed.`)
    }
    directPreview.value = null
    await loadAll()
    notify.success({ message: installedExtension.status_message || `${first.preview.display_name} installed.`, source: 'market' })
  })
}

async function toggleExtension(extension: InstalledExtensionDto) {
  await run('toggle extension', async () => {
    const updated = extension.enabled
      ? await api.disableExtension(extension.id)
      : await api.enableExtension(extension.id)
    await loadAll()
    await loadPreview()
    notify.success({ message: updated.status_message, source: 'market' })
  })
}

async function removeExtension(extension: InstalledExtensionDto) {
  if (!await confirm({ title: t('market.uninstall'), message: extension.display_name, confirmLabel: t('market.uninstall'), destructive: true })) return
  await run('remove extension', async () => {
    await api.deleteExtension(extension.id)
    await loadAll()
    await loadPreview()
    notify.success({ message: `${extension.display_name} removed.`, source: 'market' })
  })
}

watch([kindFilter, sourceFilter], () => { void loadCatalog() })
watch(selectedCatalogId, () => { void loadPreview() })

let started = false
function start() {
  if (started) return
  started = true
  void loadAll()
}

export const marketController = {
  sources, catalog, installed, acpAdapters,
  mcpInventory, mcpServers, mcpSource, mcpReadSucceeded, mcpReason, mcpConfigPath,
  selectedCatalogId, kindFilter, sourceFilter, query, busy, loading,
  preview, directPreview, sourceForm, directForm,
  selectedItem, installedByExtensionId, selectedInstalled, builtinSource,
  start,
  loadAll, loadCatalog, loadPreview,
  addSource, refreshSource,
  approveAndInstallCatalog, previewDirectInstall, approveAndInstallDirect,
  toggleExtension, removeExtension,
}
