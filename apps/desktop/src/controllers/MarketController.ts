import { computed, reactive, ref, watch } from 'vue'
import i18n from '@/i18n'
import { api, type AcpAdapterDto, type ExtensionInstallPreviewDto, type ExtensionSourceDto, type InstalledExtensionDto, type MarketCatalogItemDto, type McpServerDto } from '@/api'
import { useNotifications } from '@/composables/useNotifications'

// ---------------------------------------------------------------------------
// MarketController — the single domain controller for the Market page.
// Owns all market data/state so the filter/catalog/detail cards share one
// source of truth instead of each opening its own fetch.
// ---------------------------------------------------------------------------

export const BUILT_IN_SOURCES = [
  { name: 'Tinadec Curated', kind: 'marketplace-url', location: 'tinadec://marketplace/curated' },
  { name: 'Tinadec Community', kind: 'marketplace-url', location: 'tinadec://marketplace/community' },
] as const

// Module-level composables are safe (they share module refs); `useI18n` is NOT
// callable outside setup(), so translate via the global instance instead.
const t = i18n.global.t
const { notify, status, confirm, dismissByKey } = useNotifications()

const sources = ref<ExtensionSourceDto[]>([])
const catalog = ref<MarketCatalogItemDto[]>([])
const installed = ref<InstalledExtensionDto[]>([])
const mcpServers = ref<McpServerDto[]>([])
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

const selectedRuntime = computed(() => {
  const extension = selectedInstalled.value
  if (!extension) return []
  return [
    ...mcpServers.value.filter((server) => server.extension_id === extension.id).map((server) => `${server.name} · ${server.transport}`),
    ...acpAdapters.value.filter((adapter) => adapter.extension_id === extension.id).map((adapter) => `${adapter.name} · ${adapter.command}`),
  ]
})

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

async function ensureBuiltInSources() {
  for (const builtIn of BUILT_IN_SOURCES) {
    const exists = sources.value.some((s) => s.location === builtIn.location || s.name === builtIn.name)
    if (!exists) {
      try {
        await api.createExtensionSource({ name: builtIn.name, kind: builtIn.kind, location: builtIn.location, enabled: true })
      } catch {
        // source may already exist
      }
    }
  }
}

async function loadAll() {
  loading.value = true
  try {
    await ensureBuiltInSources()
    const [sourceList, installedList, servers, adapters] = await Promise.all([
      api.listExtensionSources(),
      api.listInstalledExtensions(),
      api.listMcpServers(),
      api.listAcpAdapters(),
    ])
    sources.value = sourceList
    installed.value = installedList
    mcpServers.value = servers
    acpAdapters.value = adapters
    if (!sourceFilter.value) {
      const builtin = sourceList.find((s) => s.location.includes('tinadec://'))
      sourceFilter.value = builtin?.id ?? sourceList[0]?.id ?? ''
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
    catalog.value = await api.listMarketCatalog({
      kind: kindFilter.value,
      query: query.value.trim(),
      source_id: sourceFilter.value || undefined,
    })
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
    await api.refreshExtensionSource(sourceId)
    await loadAll()
    notify.success({ message: 'Source refreshed.', source: 'market' })
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
    if (item.kind === 'mcp-server') {
      const server = mcpServers.value.find((s) => s.extension_id === installedExtension!.id)
      if (server) {
        try { await api.connectMcpServer(server.id) } catch { /* async */ }
      }
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
  sources, catalog, installed, mcpServers, acpAdapters,
  selectedCatalogId, kindFilter, sourceFilter, query, busy, loading,
  preview, directPreview, sourceForm, directForm,
  selectedItem, installedByExtensionId, selectedInstalled, selectedRuntime, builtinSource,
  start,
  loadAll, loadCatalog, loadPreview,
  addSource, refreshSource,
  approveAndInstallCatalog, previewDirectInstall, approveAndInstallDirect,
  toggleExtension, removeExtension,
}
