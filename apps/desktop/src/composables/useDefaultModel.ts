import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNotifications } from '@/composables/useNotifications'
import { api, type ModelProviderInstanceDto, type ModelRouteDto } from '@/api'
import { aggregateModelCenterOverview } from '@/runtimeCenterView'
import { selectedStorage } from '@/lib/storageScope'

/**
 * The default model — the model center's chat route — as the composer shows and switches it. One
 * value, the same one "set as default" writes in the model center, never a per-conversation
 * choice. Requests run in the current storage scope: a workspace on the user's models writes the
 * user file, one on its own copy writes that copy, which is the model its conversations use
 * either way. The list is the model center's own projection (provider models plus models named
 * only on routes), so nothing configured there is missing here.
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
  const models = computed(() => aggregateModelCenterOverview({ providers: providers.value, routes: routes.value, templates: [] }).models
    .filter(model => model.enabled))
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

  async function choose(providerId: string, model: string) {
    if (busy.value || isCurrent(providerId, model)) return
    busy.value = true
    try {
      await api.saveModelRoute('chat', { candidates: [{ provider_instance_id: providerId, model }] }, undefined,
        chatRoute.value?.revision != null ? { expected_revision: chatRoute.value.revision } : undefined)
    } catch (error) {
      notify.error(error, { title: t('composer.modelSwitch.failed') })
    } finally {
      busy.value = false
      await load()
    }
  }

  watch(selectedStorage, () => { loaded.value = false; void load() })
  return { models, current, name, loaded, busy, load, choose, isCurrent }
}

export type DefaultModel = ReturnType<typeof useDefaultModel>
