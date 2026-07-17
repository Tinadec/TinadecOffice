import { computed, readonly, ref } from 'vue'
import {
  SETTINGS_IDS,
  type SettingsId,
  type SettingsNavigation,
  type SettingsNavigationOptions,
  type SettingsViewTransition,
} from './types'

const REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)'

function createInitialRevisions(): Readonly<Record<SettingsId, number>> {
  return {
    model: 0,
    agents: 0,
    agentEvolution: 0,
    promptContext: 0,
    promptEngineering: 0,
    tools: 0,
    appearance: 0,
    language: 0,
    apiDocs: 0,
    about: 0,
  }
}

function orderedIds(ids: ReadonlySet<SettingsId>): readonly SettingsId[] {
  return SETTINGS_IDS.filter((id) => ids.has(id))
}

export function createSettingsNavigation(
  options: SettingsNavigationOptions,
): SettingsNavigation {
  const activeId = ref<SettingsId>(options.initialId)
  const visited = ref<readonly SettingsId[]>([])
  const failed = ref<readonly SettingsId[]>([])
  const revisions = ref(createInitialRevisions())
  const motionMode = ref<'native' | 'vue-fallback' | 'reduced'>('vue-fallback')

  let requestRevision = 0
  let currentTransition: SettingsViewTransition | undefined

  function stopCurrentTransition(): void {
    currentTransition?.skipTransition()
    currentTransition = undefined
  }

  function applyImmediate(id: SettingsId): void {
    stopCurrentTransition()
    requestRevision += 1
    activeId.value = id
  }

  function switchSection(id: SettingsId): void {
    if (options.matchMedia(REDUCED_MOTION_QUERY).matches) {
      motionMode.value = 'reduced'
      applyImmediate(id)
      return
    }

    const viewTransitionDocument = options.viewTransitionDocument
    if (!viewTransitionDocument) {
      motionMode.value = 'vue-fallback'
      applyImmediate(id)
      return
    }

    stopCurrentTransition()
    motionMode.value = 'native'
    requestRevision += 1
    const requestedRevision = requestRevision
    const transition = viewTransitionDocument.startViewTransition(() => {
      if (requestedRevision === requestRevision) {
        activeId.value = id
      }
    })
    currentTransition = transition

    const clearTransition = (): void => {
      if (currentTransition === transition) {
        currentTransition = undefined
      }
    }
    void transition.finished.then(clearTransition, clearTransition)
  }

  function markVisited(id: SettingsId): void {
    visited.value = orderedIds(new Set([...visited.value, id]))
    failed.value = failed.value.filter((failedId) => failedId !== id)
  }

  function markFailed(id: SettingsId): void {
    visited.value = visited.value.filter((visitedId) => visitedId !== id)
    failed.value = orderedIds(new Set([...failed.value, id]))
  }

  function retry(id: SettingsId): void {
    failed.value = failed.value.filter((failedId) => failedId !== id)
    revisions.value = {
      ...revisions.value,
      [id]: revisions.value[id] + 1,
    }
  }

  return {
    activeId: readonly(activeId),
    visitedIds: computed(() => visited.value),
    failedIds: computed(() => failed.value),
    revisions: computed(() => revisions.value),
    motionMode: readonly(motionMode),
    switchSection,
    markVisited,
    markFailed,
    retry,
    cacheKey: (id) => `${id}:${revisions.value[id]}`,
  }
}
