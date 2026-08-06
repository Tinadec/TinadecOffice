import type { Component } from 'vue'
import { createCardRegistry, type CardRegistry } from './registry'
import { createEmptySnapshot } from './reducer'
import type { WorkbenchLayoutSnapshot } from './types'
import type { ReduceContext } from './reducer'

/** A minimal card descriptor factory for tests (components are stubbed). */
export function makeCard(
  type: string,
  opts: Partial<{
    singleton: boolean
    movable: boolean
    closable: boolean
    detachable: boolean
    minWidth: number
    minHeight: number
  }> = {},
) {
  return {
    type,
    component: (() => null) as unknown as Component,
    minWidth: 160,
    minHeight: 120,
    singleton: false,
    movable: true,
    closable: true,
    detachable: true,
    defaultTitle: type,
    ...opts,
  }
}

let uid = 0
export function nextTestId(): string {
  return `test-inst-${++uid}`
}

/** A registry preloaded with common home-style cards. */
export function makeRegistry(): CardRegistry {
  const reg = createCardRegistry()
  reg.register(makeCard('nav', { singleton: true, closable: false, movable: false }))
  reg.register(makeCard('chat', { singleton: true, closable: false }))
  reg.register(makeCard('homePicker', { singleton: true }))
  reg.register(makeCard('git'))
  reg.register(makeCard('approval'))
  reg.register(makeCard('orchestration'))
  reg.register(makeCard('events'))
  reg.register(makeCard('doctor'))
  reg.register(makeCard('browser', { singleton: false }))
  reg.register(makeCard('agent'))
  reg.register(makeCard('terminal', { singleton: false }))
  reg.register(makeCard('settingsNav', { singleton: true, movable: false, closable: false, detachable: false }))
  reg.register(makeCard('settingsContent', { singleton: true, movable: false, closable: false, detachable: false }))
  return reg
}

/** A reduce context wired to the registry + a fresh id counter. */
export function makeContext(registry: CardRegistry): ReduceContext {
  return {
    registry,
    nextInstanceId: nextTestId,
    lockedSlots: new Set(),
  }
}

export function makeEmptySnapshot(pageId: 'home' | 'settings' = 'home'): WorkbenchLayoutSnapshot {
  return createEmptySnapshot(pageId, 1)
}
