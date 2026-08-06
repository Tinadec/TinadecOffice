import { inject, provide, type InjectionKey, type Ref } from 'vue'
import type { PersistedCardInstance, WorkbenchCardInstanceId, WorkbenchPageId } from './types'

export interface WorkbenchCardContext {
  card: Readonly<Ref<PersistedCardInstance>>
  instanceId: WorkbenchCardInstanceId
  pageId: WorkbenchPageId
  updateState: (state: Record<string, unknown>) => void
}

const WORKBENCH_CARD_KEY: InjectionKey<WorkbenchCardContext> = Symbol('tinadec-workbench-card')

export function provideWorkbenchCard(context: WorkbenchCardContext): void {
  provide(WORKBENCH_CARD_KEY, context)
}

export function useWorkbenchCard(): WorkbenchCardContext {
  const context = inject(WORKBENCH_CARD_KEY)
  if (!context) throw new Error('Workbench card context was not provided.')
  return context
}

export function useOptionalWorkbenchCard(): WorkbenchCardContext | null {
  return inject(WORKBENCH_CARD_KEY, null)
}

