import { onMounted, watch } from 'vue'
import {
  buildUieRegistry,
  createComponentLookup,
  createElectronLayoutAdapter,
  createLayerStore,
  initUie,
  useUie,
  type UiePageId,
  type UieStore,
} from '@tinadec/ui'
import { homeController } from '@/controllers/HomeController'

/**
 * Initialize the production UIE once for the renderer window.
 *
 * UIE owns the layout, persistence, and card registry. Routes must only select
 * a page; creating a second store or a second persistence adapter here would
 * split layout state between competing UI systems.
 *
 * The app's selected project is bound to the engine here, once: project-scoped
 * pages (Home) then show and save a layout per project. HomeController stays the
 * only owner of the selection; UIE only follows it.
 */
export function ensureProductionUie(): UieStore {
  try {
    return useUie()
  } catch {
    const registry = buildUieRegistry()
    const uie = initUie({
      registry,
      componentFor: createComponentLookup(registry),
      activeProjectId: homeController.selectedProjectId.value,
      persistence: { store: createLayerStore(createElectronLayoutAdapter()) },
    })
    watch(homeController.selectedProjectId, (id) => uie.setActiveProjectId(id))
    return uie
  }
}

/**
 * Route-page entry for a UIE page: initialize the engine and, on mount, switch
 * the singleton layout to `pageId` (restoring its persisted layout, else its
 * preset). Every UIE-rendered route must go through this — never call
 * `applyPreset` on navigation, which would discard the user's saved layout.
 */
export function useUiePage(pageId: UiePageId): UieStore {
  const uie = ensureProductionUie()
  onMounted(() => uie.showPage(pageId))
  return uie
}
