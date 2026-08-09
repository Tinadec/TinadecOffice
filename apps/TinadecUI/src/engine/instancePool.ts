import type { Component } from 'vue'

// ---------------------------------------------------------------------------
// Surface Pool / instance pool.
//
// Card instances are created ONCE by instanceId and then moved via geometry /
// visibility changes — never remounted on slot/tab/route change. `hydrate(id)`
// creates the instance the first time; `destroy(id)` explicitly tears it down.
// Hidden cards stay mounted (only visibility/inert change). MRU auto-eviction is
// intentionally not implemented this round.
// ---------------------------------------------------------------------------

export interface PooledCardInstance {
  id: string
  descriptorId: string
  component: Component
  /** Serialized state hydrated into the card. */
  state?: Record<string, unknown>
  /** How many render sites currently reference this instance. */
  refCount: number
}

export interface InstancePool {
  /** Get or create a pooled instance. Returns null if unknown descriptor. */
  hydrate(id: string, descriptorId: string, state?: Record<string, unknown>): PooledCardInstance | null
  /** Explicitly destroy an instance. No-op if absent. */
  destroy(id: string): void
  get(id: string): PooledCardInstance | undefined
  has(id: string): boolean
  size(): number
  /** All pooled instance ids (for reconciliation against the snapshot). */
  ids(): readonly string[]
}

export function createInstancePool(
  componentFor: (descriptorId: string) => Component | undefined,
): InstancePool {
  const pool = new Map<string, PooledCardInstance>()

  return {
    hydrate(id, descriptorId, state) {
      const existing = pool.get(id)
      if (existing) {
        existing.refCount++
        return existing
      }
      const component = componentFor(descriptorId)
      if (!component) return null
      const instance: PooledCardInstance = {
        id,
        descriptorId,
        component,
        ...(state ? { state } : {}),
        refCount: 1,
      }
      pool.set(id, instance)
      return instance
    },
    destroy(id) {
      const inst = pool.get(id)
      if (!inst) return
      inst.refCount = Math.max(0, inst.refCount - 1)
      if (inst.refCount === 0) {
        pool.delete(id)
      }
    },
    get(id) {
      return pool.get(id)
    },
    has(id) {
      return pool.has(id)
    },
    size() {
      return pool.size
    },
    ids() {
      return [...pool.keys()]
    },
  }
}
