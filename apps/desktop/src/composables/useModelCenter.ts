import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  api as baseApi,
  type AcpAdapterDto,
  type AgentRuntimePolicyDto,
  type CliDiscoveryCandidateDto,
  type HarnessChannelDto,
  type ModelCatalogReadinessReceiptDto,
  type ModelCenterApiConnectionDto,
  type ModelCenterOverviewDto,
  type ModelCenterSnapshotDto,
  type ModelCenterSnapshotPartDto,
  type ModelCostTier,
  type ModelProfileDto,
  type ModelProfileTier,
  type ModelProviderInstanceDto,
  type ModelProviderTemplateDto,
  type ModelReadinessReceiptDto,
  type ModelReferenceDto,
  type ModelRouteDto,
  type ModelSpecialty,
  type RuntimeReadinessReceiptDto,
  type SaveModelProviderInstanceInput,
} from '@/api'
import { scopedApi } from '@/lib/storageScope'
import { routesAfterModelRemoval } from '@/lib/modelRouteEdits'
import { aggregateModelCenterOverview, providersFromOverview } from '@/runtimeCenterView'
import { useNotifications } from './useNotifications'
import { useRecoverableRead, type ReadContext } from './useRecoverableRead'
import { toErrorState, type ErrorState } from './useErrorState'

export const MODEL_PROFILE_SPECIALTIES = [
  'coding',
  'architecture',
  'research',
  'review',
  'writing',
  'vision',
  'tool_calling',
  'fast_qa',
  'long_context',
] as const satisfies readonly ModelSpecialty[]

export const MODEL_PROFILE_TIERS: readonly ModelProfileTier[] = ['fast', 'balanced', 'deep']
export const MODEL_PROFILE_COST_TIERS: readonly ModelCostTier[] = ['low', 'medium', 'high']

/**
 * Non-destructive default ability suggestions inferred from common model families.
 * Never persisted automatically; only offered as an editable starting point.
 */
export function suggestModelProfile(modelId: string): ModelProfileDto {
  const id = modelId.trim().toLowerCase()
  if (!id) {
    return { specialties: ['coding', 'research', 'tool_calling'], tier: 'balanced', notes: '' }
  }
  if (/(^|[-_/:])(o1|o3|o4|opus|deepseek-r1|r1|reasoner|qwq|thinking)([-_/:]|$)/.test(id)) {
    const specialties: ModelSpecialty[] = ['architecture', 'coding', 'review', 'research']
    if (/(opus|claude|gpt-5|o3|o4)/.test(id)) specialties.push('tool_calling')
    if (/(claude|opus)/.test(id)) specialties.push('writing', 'long_context')
    return { specialties, tier: 'deep', notes: '' }
  }
  if (/(mini|flash|haiku|lite|nano|instant|turbo|8b|7b|3b)/.test(id)) {
    const specialties: ModelSpecialty[] = ['fast_qa', 'tool_calling', 'coding']
    if (/(gemini|4o|flash)/.test(id)) specialties.push('vision', 'long_context')
    return { specialties, tier: 'fast', notes: '' }
  }
  if (/(coder|codex|starcoder|devstral|swe)/.test(id)) {
    return { specialties: ['coding', 'review', 'tool_calling'], tier: 'balanced', notes: '' }
  }
  if (/(claude|sonnet)/.test(id)) {
    return {
      specialties: ['coding', 'architecture', 'review', 'writing', 'vision', 'tool_calling', 'long_context'],
      tier: 'balanced',
      notes: '',
    }
  }
  if (/(gemini)/.test(id)) {
    return {
      specialties: ['research', 'vision', 'long_context', 'coding', 'tool_calling'],
      tier: 'balanced',
      notes: '',
    }
  }
  if (/(gpt-5|gpt-4o|4o)/.test(id)) {
    return {
      specialties: ['coding', 'research', 'writing', 'vision', 'tool_calling'],
      tier: /(gpt-5)/.test(id) ? 'deep' : 'balanced',
      notes: '',
    }
  }
  if (/(vision|\bvl\b)/.test(id)) {
    return { specialties: ['vision', 'research', 'fast_qa'], tier: 'balanced', notes: '' }
  }
  return { specialties: ['coding', 'research', 'tool_calling'], tier: 'balanced', notes: '' }
}

export interface UseModelCenterOptions {
  /**
   * A write changed the shared provider/route catalog. The page owns the agent center, so it
   * decides whether that surface is already loaded and needs the same refresh.
   */
  onModelsChanged?: () => Promise<void> | void
}

/**
 * One read and one write seam for the model catalog. The projection is shared: the composer's
 * default-model reader is scope-aware and must not be duplicated here, and the agent center
 * consumes the same providers/routes refs.
 */
export function useModelCenter(options: UseModelCenterOptions = {}) {
  const { t } = useI18n()
  const { notify, confirm } = useNotifications()
  const api = scopedApi(baseApi, () => 'user')

  const modelCenterOverview = ref<ModelCenterOverviewDto | null>(null)
  const providers = ref<ModelProviderInstanceDto[]>([])
  const modelReadiness = ref<ModelReadinessReceiptDto | null>(null)
  const modelCatalogReadiness = ref<ModelCatalogReadinessReceiptDto | null>(null)
  const readinessReceipt = ref<RuntimeReadinessReceiptDto | null>(null)
  const routes = ref<ModelRouteDto[]>([])
  /** Last raw provider rows; the center degrades to these, not to the derived rows. */
  const modelCenterRawProviders = ref<ModelProviderInstanceDto[]>([])
  /** Sections whose last read failed; each one degrades alone instead of failing the center. */
  const modelCenterPartialFailures = ref<Record<string, ErrorState>>({})
  const selectedProviderId = ref('')
  const modelCenterBusy = ref(false)

  const modelCenterRead = useRecoverableRead(loadModelCenterData, {
    lazy: true,
    notificationKey: 'model-center',
    notificationSource: 'models',
    retryLabel: t('settings.retry'),
  })
  const modelCenterLoading = modelCenterRead.loading
  const modelCenterFailure = modelCenterRead.failure.error
  const modelCenterLoaded = modelCenterRead.loaded
  function loadModelCenter(): Promise<void> {
    return modelCenterRead.refresh()
  }

  async function loadModelCenterData({ signal, isCurrent }: ReadContext) {
    // model-center/overview BFF was deleted; derive the same projection from versioned APIs.
    // One round trip first; the per-endpoint reads below stay the fallback for a gateway that
    // predates the snapshot, and for the case where the snapshot itself is unusable.
    modelCenterPartialFailures.value = {}
    const snapshot = await api.getModelCenterSnapshot({ signal }).catch(() => null)
    if (!isCurrent()) return
    // A snapshot that cannot be read as a snapshot is not a degraded section — it is no
    // snapshot at all, so the per-endpoint path below answers instead of a broken shape.
    if (isModelCenterSnapshot(snapshot)) {
      applyModelCenterSnapshot(snapshot)
      reportModelCenterPartialFailures()
      return
    }
    // No read here may reject the whole exchange: before this, a failing providers or routes
    // read rejected the Promise.all and the page reported "failed to load" while six other
    // sections had in fact answered. Each section keeps its own data and names itself.
    const [providerRows, providerTemplates, routeRows, acpAdapters, modelReadinessReceipt, catalogReadinessReceipt, unifiedReceipt] = await Promise.all([
      api.listModelProviders({ signal }).catch(preserveSection('model-providers', modelCenterRawProviders.value)),
      api.listModelProviderTemplates({ signal }).catch(preserveSection('model-provider-templates', [] as ModelProviderTemplateDto[])),
      api.listModelRoutes({ signal }).catch(preserveSection('model-routes', routes.value)),
      api.listAcpAdapters({ signal }).catch(preserveSection('acp-adapters', [] as AcpAdapterDto[])),
      api.getModelReadiness({ signal }).catch(preserveSection('model-readiness', modelReadiness.value)),
      api.getModelCatalogReadiness({ signal }).catch(preserveSection('model-catalog-readiness', modelCatalogReadiness.value)),
      api.readiness({ signal }).catch(preserveSection('readiness', readinessReceipt.value)),
    ])
    if (!isCurrent()) return
    applyModelCenterRead(
      providerRows, providerTemplates, routeRows, acpAdapters,
      modelReadinessReceipt, catalogReadinessReceipt, unifiedReceipt,
    )
    reportModelCenterPartialFailures()
  }

  /**
   * One failed section keeps the value it already had, so the rest of the center keeps working.
   */
  function preserveSection<T>(source: string, fallback: T) {
    return (error: unknown): T => {
      modelCenterPartialFailures.value = { ...modelCenterPartialFailures.value, [source]: toErrorState(error) }
      return fallback
    }
  }

  /**
   * A degraded section is still a failure the user must be able to see and retry — otherwise
   * the read looks successful while one source silently kept stale data. Throwing here runs the
   * read's normal failure path (the banner and the status notice) after the data was applied.
   */
  function reportModelCenterPartialFailures() {
    const sources = Object.keys(modelCenterPartialFailures.value)
    if (sources.length === 0) return
    throw new Error(`model center sections failed: ${sources.join(', ')}`)
  }

  function isModelCenterSnapshot(value: unknown): value is ModelCenterSnapshotDto {
    const parts = (value as ModelCenterSnapshotDto | null)?.parts
    return !!parts
      && typeof parts.providers?.ok === 'boolean'
      && typeof parts.templates?.ok === 'boolean'
      && typeof parts.routes?.ok === 'boolean'
      && typeof parts.acp_adapters?.ok === 'boolean'
      && typeof parts.model_readiness?.ok === 'boolean'
      && typeof parts.catalog_readiness?.ok === 'boolean'
      && typeof parts.readiness?.ok === 'boolean'
  }

  /** A part that failed keeps the value the section already had, exactly like the fallback path. */
  function applyModelCenterSnapshot(snapshot: ModelCenterSnapshotDto) {
    const failed: Record<string, ErrorState> = {}
    const part = <T>(name: string, current: T, read: ModelCenterSnapshotPartDto<T>): T => {
      if (read.ok && read.data !== undefined) return read.data
      failed[name] = toErrorState(read.error ?? new Error(`model-center snapshot part ${name} failed`))
      return current
    }
    const parts = snapshot.parts
    const providerRows = part('model-providers', modelCenterRawProviders.value, parts.providers)
    const providerTemplates = part('model-provider-templates', [] as ModelProviderTemplateDto[], parts.templates)
    const routeRows = part('model-routes', routes.value, parts.routes)
    const acpAdapters = part('acp-adapters', [] as AcpAdapterDto[], parts.acp_adapters)
    const modelReadinessReceipt = part('model-readiness', modelReadiness.value, parts.model_readiness)
    const catalogReadinessReceipt = part('model-catalog-readiness', modelCatalogReadiness.value, parts.catalog_readiness)
    const unifiedReceipt = part('readiness', readinessReceipt.value, parts.readiness)
    modelCenterPartialFailures.value = failed
    applyModelCenterRead(
      providerRows, providerTemplates, routeRows, acpAdapters,
      modelReadinessReceipt, catalogReadinessReceipt, unifiedReceipt,
    )
  }

  function applyModelCenterRead(
    providerRows: ModelProviderInstanceDto[],
    providerTemplates: ModelProviderTemplateDto[],
    routeRows: ModelRouteDto[],
    acpAdapters: AcpAdapterDto[],
    modelReadinessReceipt: ModelReadinessReceiptDto | null,
    catalogReadinessReceipt: ModelCatalogReadinessReceiptDto | null,
    unifiedReceipt: RuntimeReadinessReceiptDto | null,
  ) {
    const overview = aggregateModelCenterOverview({
      providers: providerRows,
      templates: providerTemplates,
      routes: routeRows,
      acp_adapters: acpAdapters,
      model_readiness: modelReadinessReceipt,
      catalog_readiness: catalogReadinessReceipt,
    })
    modelCenterOverview.value = overview
    modelCenterRawProviders.value = providerRows
    // The routes ref feeds the Routes tab, the route editor, and the
    // "set default model" If-Match token; leaving it unassigned made every
    // route write send no precondition (Core 428).
    routes.value = routeRows
    const rawById = new Map(providerRows.map((row) => [row.id, row]))
    const instances = providersFromOverview(overview).map((instance) => ({
      ...instance,
      model_profiles: instance.model_profiles ?? rawById.get(instance.id)?.model_profiles,
    }))
    providers.value = instances
    modelReadiness.value = overview.readiness.model ?? null
    modelCatalogReadiness.value = overview.readiness.catalog ?? null
    readinessReceipt.value = unifiedReceipt

    const selected = instances.find((provider) => provider.id === selectedProviderId.value) ?? instances[0]
    if (selected) {
      selectedProviderId.value = selected.id
    }
  }

  /** 412/428 mean another window changed the config between load and save; reload instead of failing. */
  function isModelWriteConflict(error: unknown): boolean {
    const err = error as { code?: string | null; status?: number }
    return err?.code === 'precondition_required' || err?.code === 'invalid_if_match' || err?.status === 412 || err?.status === 428
  }

  function notifyModelsChanged() {
    return options.onModelsChanged?.()
  }

  const selectedProvider = computed(() =>
    providers.value.find((provider) => provider.id === selectedProviderId.value) ?? null,
  )

  /** Sources that failed on the last read; each one kept its section's previous data. */
  const modelCenterPartialFailureSources = computed(() => Object.keys(modelCenterPartialFailures.value))

  function modelApiProvider(providerId: string) {
    return modelCenterOverview.value?.api_connections.find((item) => item.id === providerId) ?? null
  }

  function modelsForProvider(providerId: string) {
    return (modelCenterOverview.value?.models ?? []).filter((model) => model.provider_instance_id === providerId)
  }

  async function setDefaultChatModel(providerInstanceId: string, modelId: string | null, displayName?: string) {
    modelCenterBusy.value = true
    try {
      // “设为默认模型”= 把该模型放到 chat 路由首位。Keep the existing
      // candidates as fallbacks: a single-candidate overwrite silently deleted a
      // failover chain the user had configured.
      const resolvedModel = modelId?.trim() ? modelId.trim() : null
      const chatRoute = routes.value.find((route: { purpose: string }) => route.purpose === 'chat')
      const existing = (chatRoute?.candidates ?? [])
        .filter(candidate => !(candidate.provider_instance_id === providerInstanceId && (candidate.model ?? null) === resolvedModel))
        .map(candidate => ({ provider_instance_id: candidate.provider_instance_id, model: candidate.model }))
      await api.saveModelRoute('chat', { candidates: [{ provider_instance_id: providerInstanceId, model: resolvedModel }, ...existing] },
        undefined,
        chatRoute?.revision != null ? { expected_revision: chatRoute.revision } : undefined)
      notify.success(t('settings.defaultModelSet', { model: resolvedModel ?? displayName ?? providerInstanceId }))
      await loadModelCenter()
    } catch (error) {
      notify.error(error instanceof Error ? error : new Error(String(error)))
    } finally {
      modelCenterBusy.value = false
    }
  }

  async function toggleProviderEnabled(provider: ModelProviderInstanceDto, options: { referencesNote?: string } = {}) {
    modelCenterBusy.value = true
    try {
      const payload: SaveModelProviderInstanceInput = {
        id: provider.id,
        driver: provider.driver,
        display_name: provider.display_name,
        connection_kind: provider.connection_kind,
        base_url: provider.base_url,
        model: provider.model,
        clear_api_key: false,
        binary_path: provider.binary_path,
        home_path: provider.home_path,
        server_url: provider.server_url,
        launch_args: provider.launch_args,
        capabilities: provider.capabilities,
        enabled: !provider.enabled,
      }
      try {
        await api.saveModelProvider(provider.id, payload, { expected_revision: provider.revision })
      } catch (error) {
        const err = error as { code?: string | null; message?: string }
        if (err.code === 'provider_in_use') {
          const message = err.message ?? t('settings.providerInUse')
          if (!await confirm({
            title: provider.display_name,
            message: options.referencesNote ? `${message}\n${options.referencesNote}` : message,
            confirmLabel: t('settings.forceAnyway'),
            cancelLabel: t('settings.cancel'),
            destructive: true,
          })) return
          await api.saveModelProvider(provider.id, payload, { expected_revision: provider.revision, force: true })
        } else {
          throw error
        }
      }
      await loadModelCenter()
      await notifyModelsChanged()
      notify.success(`${provider.display_name}: ${provider.enabled ? t('settings.disable') : t('settings.enable')}`)
    } catch (error) {
      if (isModelWriteConflict(error)) {
        notify.warning({ message: t('settings.configStaleReloading') })
        await loadModelCenter()
      } else {
        notify.error(error, { title: provider.display_name })
      }
    } finally {
      modelCenterBusy.value = false
    }
  }

  /** True when the provider is gone; the caller clears any selection that pointed at it. */
  async function deleteProvider(providerId: string, options: { referencesNote?: string } = {}): Promise<boolean> {
    const provider = providers.value.find((item) => item.id === providerId)
    if (!await confirm({
      title: t('settings.delete'),
      message: [`${t('settings.confirmDeleteProvider')}\n${provider?.display_name ?? providerId} (${providerId})`, options.referencesNote].filter(Boolean).join('\n'),
      confirmLabel: t('settings.confirmDelete'),
      cancelLabel: t('settings.cancel'),
      destructive: true,
    })) return false
    modelCenterBusy.value = true
    try {
      try {
        await api.deleteModelProvider(providerId, { expected_revision: provider?.revision })
      } catch (error) {
        const err = error as { code?: string | null; message?: string }
        if (err.code === 'provider_in_use') {
          const message = err.message ?? t('settings.providerInUse')
          if (!await confirm({
            title: provider?.display_name ?? providerId,
            message,
            confirmLabel: t('settings.forceAnyway'),
            cancelLabel: t('settings.cancel'),
            destructive: true,
          })) return false
          await api.deleteModelProvider(providerId, { expected_revision: provider?.revision, force: true })
        } else {
          throw error
        }
      }
      await loadModelCenter()
      await notifyModelsChanged()
      notify.success(`${provider?.display_name ?? providerId}: ${t('settings.delete')}`)
      return true
    } catch (error) {
      if (isModelWriteConflict(error)) {
        notify.warning({ message: t('settings.configStaleReloading') })
        await loadModelCenter()
      } else {
        notify.error(error, { title: provider?.display_name ?? providerId })
      }
      return false
    } finally {
      modelCenterBusy.value = false
    }
  }

  async function discoverProviderModels(providerInstanceId: string): Promise<Array<{ id: string; display_name: string }>> {
    const result = await api.refreshProviderModels(providerInstanceId)
    return result.models
  }

  async function saveProviderModels(
    provider: ModelCenterApiConnectionDto,
    models: string[],
    model: string | null,
  ): Promise<boolean> {
    modelCenterBusy.value = true
    try {
      const fullProvider = providers.value.find((item) => item.id === provider.id)
      const seededProfiles: Record<string, ModelProfileDto> = { ...(fullProvider?.model_profiles ?? {}) }
      for (const modelId of models) {
        if (modelId && !seededProfiles[modelId]) {
          seededProfiles[modelId] = suggestModelProfile(modelId)
        }
      }
      const payload: SaveModelProviderInstanceInput = {
        id: provider.id,
        driver: provider.driver,
        protocol: fullProvider?.protocol ?? null,
        display_name: provider.display_name,
        connection_kind: provider.connection_kind,
        base_url: provider.base_url ?? null,
        model,
        models,
        model_profiles: seededProfiles,
        server_url: provider.server_url ?? null,
        capabilities: provider.capabilities,
        enabled: provider.enabled,
      }
      await api.saveModelProvider(provider.id, payload, {
        expected_revision: provider.revision ?? undefined,
      })
      await loadModelCenter()
      notify.success(t('settings.refreshModels'))
      return true
    } catch (error) {
      if (isModelWriteConflict(error)) {
        notify.warning({ message: t('settings.configStaleReloading') })
        await loadModelCenter()
      } else {
        notify.error(error, { title: provider.display_name })
      }
      return false
    } finally {
      modelCenterBusy.value = false
    }
  }

  async function removeModel(providerId: string, modelId: string) {
    const provider = modelApiProvider(providerId)
    if (!provider) return
    if (!await confirm({
      title: t('settings.removeModel'),
      message: `${t('settings.confirmRemoveModel')}\n${modelId} (${provider.display_name})`,
      confirmLabel: t('settings.confirmDelete'),
      cancelLabel: t('settings.cancel'),
      destructive: true,
    })) return
    modelCenterBusy.value = true
    try {
      // Clear every source that contributes the model so it stays gone after reload.
      const updates = routesAfterModelRemoval(await api.listModelRoutes(), providerId, modelId)
      for (const route of updates) {
        await api.saveModelRoute(route.purpose, { candidates: route.candidates }, undefined, { expected_revision: route.revision })
      }
      const merged = (provider.models ?? []).filter((id) => id !== modelId)
      const nextDefault = provider.model === modelId ? merged[0] ?? null : (provider.model ?? null)
      await saveProviderModels(provider, merged, nextDefault)
      await notifyModelsChanged()
    } catch (error) {
      notify.error(error instanceof Error && error.message === 'model_route_requires_replacement'
        ? new Error(t('settings.modelRouteRequiresReplacement')) : error, { title: modelId })
      await loadModelCenter()
    } finally {
      modelCenterBusy.value = false
    }
  }

  async function saveProviderInstance(
    payload: SaveModelProviderInstanceInput,
    request: { id?: string; expectedRevision?: number; force?: boolean } = {},
  ): Promise<ModelProviderInstanceDto> {
    modelCenterBusy.value = true
    try {
      const saved = request.id
        ? await api.saveModelProvider(request.id, payload, {
          expected_revision: request.expectedRevision,
          ...(request.force ? { force: true } : {}),
        })
        : await api.createModelProvider(payload)
      selectedProviderId.value = saved.id
      await loadModelCenter()
      await notifyModelsChanged()
      notify.success(saved.display_name)
      return saved
    } finally {
      modelCenterBusy.value = false
    }
  }

  async function saveModelProfile(
    provider: ModelProviderInstanceDto,
    modelId: string,
    profile: ModelProfileDto | null,
    expectedRevision?: number | null,
  ): Promise<boolean> {
    modelCenterBusy.value = true
    try {
      const nextProfiles: Record<string, ModelProfileDto> = { ...(provider.model_profiles ?? {}) }
      if (profile === null) {
        delete nextProfiles[modelId]
      } else {
        const trimmedNotes = profile.notes?.trim() ?? ''
        nextProfiles[modelId] = {
          specialties: Array.from(new Set(profile.specialties.map((s) => s.trim()).filter(Boolean))),
          tier: profile.tier,
          notes: trimmedNotes ? trimmedNotes.slice(0, 500) : null,
          ...(profile.context_window && Number.isFinite(profile.context_window) && profile.context_window > 0
            ? { context_window: Math.round(profile.context_window) }
            : {}),
          ...(profile.cost_tier ? { cost_tier: profile.cost_tier } : {}),
        }
      }
      const payload: SaveModelProviderInstanceInput = {
        id: provider.id,
        driver: provider.driver,
        protocol: provider.protocol ?? null,
        display_name: provider.display_name,
        connection_kind: provider.connection_kind,
        base_url: provider.base_url ?? null,
        model: provider.model ?? null,
        models: provider.models,
        binary_path: provider.binary_path ?? null,
        home_path: provider.home_path ?? null,
        server_url: provider.server_url ?? null,
        launch_args: provider.launch_args ?? null,
        capabilities: provider.capabilities,
        enabled: provider.enabled,
        model_profiles: nextProfiles,
      }
      await api.saveModelProvider(provider.id, payload, {
        expected_revision: expectedRevision ?? provider.revision ?? undefined,
      })
      await loadModelCenter()
      await notifyModelsChanged()
      notify.success(t(profile === null ? 'settings.modelProfileCleared' : 'settings.modelProfileSaved', { model: modelId }))
      return true
    } catch (error) {
      if (isModelWriteConflict(error)) {
        notify.warning({ message: t('settings.configStaleReloading') })
        await loadModelCenter()
      } else {
        notify.error(error, { title: modelId })
      }
      return false
    } finally {
      modelCenterBusy.value = false
    }
  }

  async function saveRouteCandidates(
    purpose: string,
    candidates: Array<{ provider_instance_id: string; model: string | null }>,
  ): Promise<boolean> {
    if (!purpose) return false
    modelCenterBusy.value = true
    try {
      const route = routes.value.find((item) => item.purpose === purpose)
      await api.saveModelRoute(purpose, {
        candidates: candidates.map((candidate) => ({
          provider_instance_id: candidate.provider_instance_id,
          model: candidate.model ?? null,
        })),
      }, undefined, { expected_revision: route?.revision })
      await loadModelCenter()
      notify.success(purpose)
      return true
    } catch (error) {
      if (isModelWriteConflict(error)) {
        notify.warning({ message: t('settings.configStaleReloading') })
        await loadModelCenter()
      } else {
        notify.error(error, { title: purpose })
      }
      return false
    } finally {
      modelCenterBusy.value = false
    }
  }

  /** Reverse references (agents / modes / routes / recent invocations) for one configured model. */
  async function fetchModelReferences(providerInstanceId: string, modelId: string): Promise<ModelReferenceDto[]> {
    return api.listModelReferences({ provider_instance_id: providerInstanceId, model: modelId })
  }

  /** Cross-model reference index loaded lazily when entering the Model Capability Library. */
  const allModelReferences = ref<ModelReferenceDto[]>([])
  const allModelReferencesLoading = ref(false)
  const allModelReferencesLoaded = ref(false)

  async function loadAllModelReferences(): Promise<void> {
    if (allModelReferencesLoading.value) return
    allModelReferencesLoading.value = true
    try {
      const rows = await api.listModelReferences({})
      allModelReferences.value = Array.isArray(rows) ? rows : []
      allModelReferencesLoaded.value = true
    } catch {
      // Keep previous index if the read fails; cards stay usable without reference badges.
    } finally {
      allModelReferencesLoading.value = false
    }
  }

  const referencesByModelKey = computed<Record<string, ModelReferenceDto[]>>(() => {
    const map: Record<string, ModelReferenceDto[]> = {}
    for (const row of allModelReferences.value) {
      const key = `${row.provider_instance_id}:${row.model ?? ''}`
      if (!map[key]) map[key] = []
      map[key].push(row)
    }
    return map
  })

  function referencesForModel(providerInstanceId: string, modelId: string): ModelReferenceDto[] {
    return referencesByModelKey.value[`${providerInstanceId}:${modelId}`] ?? []
  }

  function agentReferenceCountForModel(providerInstanceId: string, modelId: string): number {
    const refs = referencesForModel(providerInstanceId, modelId)
    return refs.filter((item) => item.reference_kind === 'agent_default' || item.reference_kind === 'mode_override').length
  }

  /** Optional read-only runtime token ceilings shown in the Routing & Budget policy slot. */
  const runtimePolicy = ref<AgentRuntimePolicyDto | null>(null)
  const runtimePolicyLoaded = ref(false)

  async function loadRuntimePolicy(): Promise<void> {
    if (typeof (api as { getAgentRuntimePolicy?: unknown }).getAgentRuntimePolicy !== 'function') {
      runtimePolicyLoaded.value = true
      return
    }
    try {
      runtimePolicy.value = await api.getAgentRuntimePolicy()
    } catch {
      runtimePolicy.value = null
    } finally {
      runtimePolicyLoaded.value = true
    }
  }

  const cliDiscoveryCandidates = ref<CliDiscoveryCandidateDto[]>([])
  const cliDiscoveryLoading = ref(false)
  const cliDiscoveryLoaded = ref(false)

  async function discoverHarnesses() {
    cliDiscoveryLoading.value = true
    try {
      const res = await api.discoverHarnesses()
      cliDiscoveryCandidates.value = res.cli_runtimes ?? []
      cliDiscoveryLoaded.value = true
    } catch (error) {
      notify.error(error, { title: t('settings.cliDiscoveryError') })
    } finally {
      cliDiscoveryLoading.value = false
    }
  }

  /**
   * Connects one channel of a discovered harness. The channel is not a cosmetic choice: it decides the
   * protocol Core stores and the argv it starts the process with, so the same binary connected over
   * `acp` and over `tui` are two different providers, not two labels for one thing.
   */
  async function connectDiscoveredCli(candidate: CliDiscoveryCandidateDto, channel?: HarnessChannelDto) {
    if (!candidate.binary_path || !channel) return
    cliDiscoveryLoading.value = true
    try {
      const ext = candidate as CliDiscoveryCandidateDto & {
        home_path?: string | null
        config_home?: string | null
        launch_args?: string | null
        default_launch_args?: string | null
        detected_models?: string[] | null
      }
      const homePath = ext.home_path || ext.config_home || null
      const launchArgs = ext.launch_args || ext.default_launch_args || null
      const detectedModels = Array.isArray(ext.detected_models) ? ext.detected_models.filter(Boolean) : []
      const created = await api.connectHarness({
        driver: candidate.driver,
        binary_path: candidate.binary_path,
        channel: channel.channel ?? null,
        protocol: channel.protocol,
        display_name: candidate.display_name || undefined,
        ...(homePath ? { home_path: homePath } : {}),
        ...(launchArgs ? { launch_args: launchArgs } : {}),
        ...(detectedModels.length > 0 ? { model: detectedModels[0], models: detectedModels } : {}),
      } as Parameters<typeof api.connectHarness>[0])
      await loadModelCenter()
      notify.success(created.display_name)
    } catch (error) {
      notify.error(error, { title: `${candidate.display_name} · ${channel.channel ?? channel.protocol}` })
    } finally {
      cliDiscoveryLoading.value = false
    }
  }

  return {
    modelCenterOverview,
    modelCenterRawProviders,
    providers,
    modelReadiness,
    modelCatalogReadiness,
    readinessReceipt,
    routes,
    modelCenterPartialFailures,
    modelCenterPartialFailureSources,
    selectedProviderId,
    selectedProvider,
    modelCenterBusy,
    modelCenterLoading,
    modelCenterFailure,
    modelCenterLoaded,
    loadModelCenter,
    isModelWriteConflict,
    modelApiProvider,
    modelsForProvider,
    setDefaultChatModel,
    toggleProviderEnabled,
    deleteProvider,
    discoverProviderModels,
    saveProviderModels,
    removeModel,
    saveProviderInstance,
    saveModelProfile,
    saveRouteCandidates,
    fetchModelReferences,
    allModelReferences,
    allModelReferencesLoading,
    allModelReferencesLoaded,
    loadAllModelReferences,
    referencesForModel,
    agentReferenceCountForModel,
    runtimePolicy,
    runtimePolicyLoaded,
    loadRuntimePolicy,
    cliDiscoveryCandidates,
    cliDiscoveryLoading,
    cliDiscoveryLoaded,
    discoverHarnesses,
    connectDiscoveredCli,
  }
}

export type ModelCenterContext = ReturnType<typeof useModelCenter>
