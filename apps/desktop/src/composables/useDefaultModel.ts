import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNotifications } from '@/composables/useNotifications'
import { api, type ModelProviderInstanceDto, type ModelRouteDto } from '@/api'
import { aggregateModelCenterOverview } from '@/runtimeCenterView'
import { selectedStorage } from '@/lib/storageScope'
import { supportsReasoningEffort, withReasoningEffort, type ReasoningEffortChoice } from '@/lib/reasoningEffort'

/**
 * The default model — the model center's chat route — as the composer shows and switches it. One
 * value, the same one "set as default" writes in the model center, never a per-conversation
 * choice. Requests run in the current storage scope: a workspace on the user's models writes the
 * user file, one on its own copy writes that copy, which is the model its conversations use
 * either way. The list is the model center's own projection (provider models plus models named
 * only on routes), so nothing configured there is missing here.
 *
 * Its reasoning effort is that model's saved parameter (`model_parameters[model]`), in the same
 * scope: on the user's models it is the value the model center's parameter editor shows; a
 * workspace on its own copy changes that copy, which its conversations use, and the model center
 * says so (ModelScopeNotice).
 */
export function useDefaultModel() {
  const { t } = useI18n()
  const { notify } = useNotifications()
  const providers = ref<ModelProviderInstanceDto[]>([])
  const routes = ref<ModelRouteDto[]>([])
  /** False until a read succeeds: "no default model" is only said when it is known. */
  const loaded = ref(false)
  const busy = ref(false)
  let read = 0

  const chatRoute = computed(() => routes.value.find(route => route.purpose === 'chat') ?? null)
  const current = computed(() => chatRoute.value?.candidates[0] ?? null)
  const overview = computed(() => aggregateModelCenterOverview({ providers: providers.value, routes: routes.value, templates: [] }))
  const models = computed(() => overview.value.models.filter(model => model.enabled))
  /** Enabled CLI/ACP runtimes an agent can be given; they pick their own model. */
  const runtimes = computed(() => [...overview.value.cli_runtimes, ...overview.value.acp_runtimes]
    .filter(runtime => runtime.enabled && runtime.provider_instance_id)
    .map(runtime => ({ provider_instance_id: runtime.provider_instance_id!, display_name: runtime.display_name })))
  /**
   * The model the reasoning effort is read from and written to: the default model unless the
   * composer names another (the model its conversation agent actually runs on). Effort is that
   * model's saved parameter either way, so it follows the model wherever it is used.
   */
  const reasoningTarget = ref<{ provider_instance_id: string; model: string | null } | null>(null)
  const target = computed(() => reasoningTarget.value ?? (current.value ? { provider_instance_id: current.value.provider_instance_id, model: current.value.model ?? null } : null))
  const targetProvider = computed(() => providers.value.find(row => row.id === target.value?.provider_instance_id) ?? null)
  /** Whether the target model takes a reasoning effort at all (its provider's protocol decides). */
  const reasoningSupported = computed(() => Boolean(loaded.value && target.value?.model && targetProvider.value
    && targetProvider.value.revision != null && supportsReasoningEffort(targetProvider.value.protocol)))
  const reasoningEffort = computed<ReasoningEffortChoice>(() => {
    const model = target.value?.model
    return (model && targetProvider.value?.model_parameters?.[model]?.reasoning_effort) || ''
  })
  /** A provider's display name, for labelling a choice that names no model (CLI/ACP runtimes). */
  function providerName(providerId: string) {
    return providers.value.find(row => row.id === providerId)?.display_name ?? null
  }
  /** The model's name, null for "no default model", undefined while unknown. */
  const name = computed<string | null | undefined>(() => {
    if (!loaded.value) return undefined
    const candidate = current.value
    if (!candidate) return null
    return candidate.model || providers.value.find(row => row.id === candidate.provider_instance_id)?.display_name || null
  })

  async function load() {
    const sequence = ++read
    try {
      const [providerRows, routeRows] = await Promise.all([api.listModelProviders(), api.listModelRoutes()])
      if (sequence !== read) return
      providers.value = Array.isArray(providerRows) ? providerRows : []
      routes.value = Array.isArray(routeRows) ? routeRows : []
      loaded.value = true
    } catch { /* keeps what was last known */ }
  }

  function isCurrent(providerId: string, model: string) {
    return current.value?.provider_instance_id === providerId && current.value.model === model
  }

  /**
   * One write at a time, held busy until the reload lands so the next write carries the new
   * revision. A reasoning choice made meanwhile is kept with the scope and model it was made for,
   * and written afterwards only if those are still the default; otherwise it is dropped.
   */
  let pending: { effort: ReasoningEffortChoice; storage: string; providerId: string; model: string } | null = null
  async function write(run: () => Promise<unknown>, failure: string) {
    busy.value = true
    try { await run() } catch (error) { notify.error(error, { title: t(failure) }) }
    finally { await load(); busy.value = false }
    const next = pending
    pending = null
    if (next && next.storage === selectedStorage.value && next.providerId === targetProvider.value?.id && next.model === target.value?.model)
      await setReasoningEffort(next.effort)
  }

  async function choose(providerId: string, model: string) {
    // Never on a list read from another scope: the storage watcher clears `loaded` until it reloads.
    if (busy.value || !loaded.value || isCurrent(providerId, model)) return
    await write(() => api.saveModelRoute('chat', { candidates: [{ provider_instance_id: providerId, model }] }, undefined,
      chatRoute.value?.revision != null ? { expected_revision: chatRoute.value.revision } : undefined), 'composer.modelSwitch.failed')
  }

  async function setReasoningEffort(effort: ReasoningEffortChoice) {
    const provider = targetProvider.value
    const model = target.value?.model
    if (!reasoningSupported.value || !provider || !model) return
    if (busy.value) { pending = { effort, storage: selectedStorage.value, providerId: provider.id, model }; return }
    if (effort === reasoningEffort.value) return
    await write(() => api.saveModelProvider(provider.id, {
      driver: provider.driver, display_name: provider.display_name, connection_kind: provider.connection_kind,
      enabled: provider.enabled, model_parameters: withReasoningEffort(provider, model, effort),
    }, { expected_revision: provider.revision! }), 'composer.reasoning.failed')
  }

  watch(selectedStorage, () => { loaded.value = false; void load() })
  return { models, runtimes, current, name, loaded, busy, load, choose, isCurrent, providerName, reasoningTarget, reasoningSupported, reasoningEffort, setReasoningEffort }
}

export type DefaultModel = ReturnType<typeof useDefaultModel>
