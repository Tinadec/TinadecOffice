import { computed, inject, provide, reactive, ref, watch, type InjectionKey } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  api,
  type AcpAdapterDto,
  type ExtensionInstallPreviewDto,
  type ExtensionSourceDto,
  type InstalledExtensionDto,
  type MarketCatalogItemDto,
  type McpServerDto,
} from '@/api'
import { useNotifications } from '@/composables/useNotifications'

const BUILT_IN_SOURCES = [
  { name: 'Tinadec Curated', kind: 'marketplace-url', location: 'tinadec://marketplace/curated' },
  { name: 'Tinadec Community', kind: 'marketplace-url', location: 'tinadec://marketplace/community' },
]

export function createMarketWorkbenchController() {
  const { t } = useI18n()
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
  const sourceForm = reactive({ name: 'Custom Marketplace', kind: 'marketplace-url', location: '' })
  const directForm = reactive({ source_kind: 'local-directory', source_location: '', manifest_json: '' })
  let started = false

  const selectedItem = computed(() => catalog.value.find((item) => item.catalog_id === selectedCatalogId.value) ?? catalog.value[0] ?? null)
  const installedByExtensionId = computed(() => new Map(installed.value.map((item) => [item.extension_id, item])))
  const selectedInstalled = computed(() => selectedItem.value ? installedByExtensionId.value.get(selectedItem.value.extension_id) ?? null : null)
  const selectedRuntime = computed(() => {
    const extension = selectedInstalled.value
    if (!extension) return []
    return [
      ...mcpServers.value.filter((server) => server.extension_id === extension.id).map((server) => `${server.name} / ${server.transport}`),
      ...acpAdapters.value.filter((adapter) => adapter.extension_id === extension.id).map((adapter) => `${adapter.name} / ${adapter.command}`),
    ]
  })

  function kindLabel(kind: string): string {
    if (kind === 'skill') return 'Skill'
    if (kind === 'mcp-server') return 'MCP'
    if (kind === 'acp-adapter') return 'ACP'
    return kind
  }

  function statusLabel(item: MarketCatalogItemDto): string {
    const extension = installedByExtensionId.value.get(item.extension_id)
    if (!extension) return t('market.available')
    return extension.enabled ? t('market.enabled') : t('market.installedDisabled')
  }

  function statusVariant(item: MarketCatalogItemDto): 'default' | 'secondary' | 'outline' {
    const extension = installedByExtensionId.value.get(item.extension_id)
    if (!extension) return 'secondary'
    return extension.enabled ? 'default' : 'outline'
  }

  async function run(label: string, action: () => Promise<void>): Promise<void> {
    busy.value = true
    try { await action() } catch (error) { notify.error(error, { title: `${label} failed`, source: 'market' }) }
    finally { busy.value = false }
  }

  async function ensureBuiltInSources(): Promise<void> {
    for (const builtIn of BUILT_IN_SOURCES) {
      if (sources.value.some((source) => source.location === builtIn.location || source.name === builtIn.name)) continue
      try { await api.createExtensionSource({ ...builtIn, enabled: true }) } catch { /* Existing source. */ }
    }
  }

  async function loadAll(): Promise<void> {
    loading.value = true
    try {
      await ensureBuiltInSources()
      const [sourceList, installedList, servers, adapters] = await Promise.all([
        api.listExtensionSources(), api.listInstalledExtensions(), api.listMcpServers(), api.listAcpAdapters(),
      ])
      sources.value = sourceList
      installed.value = installedList
      mcpServers.value = servers
      acpAdapters.value = adapters
      if (!sourceList.some((source) => source.id === sourceFilter.value)) {
        sourceFilter.value = sourceList.find((source) => source.location.includes('tinadec://'))?.id ?? sourceList[0]?.id ?? ''
      }
      await loadCatalog()
      dismissByKey('market-load')
    } catch (error) {
      status.error({ key: 'market-load', title: t('market.loadFailed'), message: t('app.loadFailedMessage'), details: error instanceof Error ? error.message : t('market.loadFailed'), source: 'market', action: { label: t('app.retry'), run: loadAll } })
    } finally { loading.value = false }
  }

  async function loadCatalog(): Promise<void> {
    try {
      catalog.value = await api.listMarketCatalog({ kind: kindFilter.value, query: query.value.trim(), source_id: sourceFilter.value || undefined })
      if (!catalog.value.some((item) => item.catalog_id === selectedCatalogId.value)) selectedCatalogId.value = catalog.value[0]?.catalog_id ?? ''
      dismissByKey('market-catalog')
    } catch (error) {
      status.error({ key: 'market-catalog', title: t('market.loadFailed'), message: t('app.loadFailedMessage'), details: error instanceof Error ? error.message : t('market.loadFailed'), source: 'market', action: { label: t('app.retry'), run: loadCatalog } })
    }
  }

  async function loadPreview(): Promise<void> {
    if (!selectedItem.value) { preview.value = null; return }
    try { preview.value = await api.previewExtensionInstall({ catalog_id: selectedItem.value.catalog_id }) }
    catch (error) { notify.error(error, { title: t('market.loadFailed'), source: 'market' }) }
  }

  async function addSource(): Promise<void> {
    await run('add source', async () => {
      await api.createExtensionSource({ ...sourceForm, enabled: true })
      sourceForm.location = ''
      await loadAll()
      notify.success({ message: 'Source added.', source: 'market' })
    })
  }

  async function refreshSource(sourceId: string): Promise<void> {
    await run('refresh source', async () => { await api.refreshExtensionSource(sourceId); await loadAll() })
  }

  async function installCatalog(): Promise<void> {
    const item = selectedItem.value
    if (!item) return
    await run('install extension', async () => {
      const first = await api.installExtension({ catalog_id: item.catalog_id })
      let extension = first.extension
      if (first.approval_required && first.approval) {
        await api.decideApproval(first.approval.id, 'approved')
        extension = (await api.installExtension({ catalog_id: item.catalog_id, approval_id: first.approval.id })).extension ?? extension
      }
      await loadAll()
      await loadPreview()
      if (!extension || ['failed', 'error', 'blocked'].includes(extension.status)) throw new Error(extension?.status_message || `${item.display_name} was not installed.`)
      notify.success({ message: extension.status_message || `${item.display_name} installed.`, source: 'market' })
    })
  }

  async function toggleExtension(extension: InstalledExtensionDto): Promise<void> {
    await run('toggle extension', async () => {
      const updated = extension.enabled ? await api.disableExtension(extension.id) : await api.enableExtension(extension.id)
      await loadAll(); await loadPreview(); notify.success({ message: updated.status_message, source: 'market' })
    })
  }

  async function removeExtension(extension: InstalledExtensionDto): Promise<void> {
    if (!await confirm({ title: t('market.uninstall'), message: extension.display_name, confirmLabel: t('market.uninstall'), destructive: true })) return
    await run('remove extension', async () => { await api.deleteExtension(extension.id); await loadAll(); await loadPreview() })
  }

  async function previewDirectInstall(): Promise<void> {
    await run('preview direct install', async () => {
      directPreview.value = await api.previewExtensionInstall({ source_kind: directForm.source_kind, source_location: directForm.source_location, manifest_json: directForm.manifest_json || null })
    })
  }

  async function installDirect(): Promise<void> {
    await run('install direct extension', async () => {
      const payload = { source_kind: directForm.source_kind, source_location: directForm.source_location, manifest_json: directForm.manifest_json || null }
      const first = await api.installExtension(payload)
      let extension = first.extension
      if (first.approval_required && first.approval) {
        await api.decideApproval(first.approval.id, 'approved')
        extension = (await api.installExtension({ ...payload, approval_id: first.approval.id })).extension ?? extension
      }
      if (!extension || ['failed', 'error', 'blocked'].includes(extension.status)) throw new Error(extension?.status_message || `${first.preview.display_name} was not installed.`)
      directPreview.value = null
      await loadAll()
    })
  }

  watch([kindFilter, sourceFilter], () => { if (started) void loadCatalog() })
  watch(selectedCatalogId, () => { if (started) void loadPreview() })
  function start(): void { if (!started) { started = true; void loadAll() } }

  return {
    sources, catalog, installed, selectedCatalogId, kindFilter, sourceFilter, query, busy, loading,
    preview, directPreview, sourceForm, directForm, selectedItem, selectedInstalled, selectedRuntime,
    kindLabel, statusLabel, statusVariant, start, loadAll, loadCatalog, loadPreview, addSource,
    refreshSource, installCatalog, toggleExtension, removeExtension, previewDirectInstall, installDirect,
  }
}

export type MarketWorkbenchController = ReturnType<typeof createMarketWorkbenchController>
const MARKET_KEY: InjectionKey<MarketWorkbenchController> = Symbol('market-workbench')
export function provideMarketWorkbench(controller: MarketWorkbenchController): void { provide(MARKET_KEY, controller) }
export function useMarketWorkbench(): MarketWorkbenchController {
  const controller = inject(MARKET_KEY)
  if (!controller) throw new Error('Market workbench controller was not provided.')
  return controller
}

