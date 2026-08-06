import type { WorkbenchCardDescriptor } from './types'

// ---------------------------------------------------------------------------
// Card registry — the single catalog of card descriptors.
// Components are referenced by descriptorId; the registry maps id -> descriptor.
// ---------------------------------------------------------------------------

export interface CardRegistry {
  register(descriptor: WorkbenchCardDescriptor): void
  get(id: string): WorkbenchCardDescriptor | undefined
  has(id: string): boolean
  list(): readonly WorkbenchCardDescriptor[]
  /** Singleton ids (globally unique cards like chat/nav/settings content). */
  singletonIds(): readonly string[]
}

export function createCardRegistry(): CardRegistry {
  const descriptors = new Map<string, WorkbenchCardDescriptor>()

  return {
    register(descriptor) {
      if (!descriptor || typeof descriptor.type !== 'string' || descriptor.type.length === 0) {
        throw new Error('CardRegistry: descriptor requires a non-empty `type`.')
      }
      if (descriptors.has(descriptor.type)) {
        throw new Error(`CardRegistry: duplicate descriptor type '${descriptor.type}'.`)
      }
      descriptors.set(descriptor.type, descriptor)
    },
    get(id) {
      return descriptors.get(id)
    },
    has(id) {
      return descriptors.has(id)
    },
    list() {
      return [...descriptors.values()]
    },
    singletonIds() {
      return [...descriptors.values()].filter((d) => d.singleton).map((d) => d.type)
    },
  }
}
