import { inject, provide, type InjectionKey, type Ref } from 'vue'
import type {
  PersistedCardInstance,
  ResolvedWorkbenchStack,
  WorkbenchCardDescriptor,
  WorkbenchCardInstanceId,
  WorkbenchPageId,
  WorkbenchSlotId,
} from './types'

export interface WorkbenchHostApi {
  activePage: Readonly<Ref<WorkbenchPageId>>
  openCard: (type: string, options?: {
    pageId?: WorkbenchPageId
    slotId?: WorkbenchSlotId
    stackId?: 'primary' | 'secondary'
    index?: number
    state?: Record<string, unknown>
    instanceId?: string
  }) => string | null
  closeCard: (cardId: WorkbenchCardInstanceId) => boolean
  moveCard: (cardId: WorkbenchCardInstanceId, slotId: WorkbenchSlotId, stackId?: 'primary' | 'secondary') => boolean
  detachCard: (card: PersistedCardInstance, pageId: WorkbenchPageId) => Promise<boolean>
  setActiveProject: (projectId: string | null) => Promise<void>
  listDescriptors: (pageId?: WorkbenchPageId) => readonly WorkbenchCardDescriptor[]
  findDefaultStack: (pageId: WorkbenchPageId) => ResolvedWorkbenchStack | null
}

const WORKBENCH_HOST_KEY: InjectionKey<WorkbenchHostApi> = Symbol('tinadec-workbench-host')

export function provideWorkbenchHost(api: WorkbenchHostApi): void {
  provide(WORKBENCH_HOST_KEY, api)
}

export function useWorkbenchHost(): WorkbenchHostApi {
  const api = inject(WORKBENCH_HOST_KEY)
  if (!api) throw new Error('Workbench host was not provided.')
  return api
}

export function useOptionalWorkbenchHost(): WorkbenchHostApi | null {
  return inject(WORKBENCH_HOST_KEY, null)
}
