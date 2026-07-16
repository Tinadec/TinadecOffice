import { describe, expect, it, vi } from 'vitest'
import type {
  SettingsMatchMedia,
  SettingsViewTransition,
  SettingsViewTransitionDocument,
} from './types'
import { createSettingsNavigation } from './useSettingsNavigation'

const motionAllowed: SettingsMatchMedia = () => ({ matches: false })
const reducedMotion: SettingsMatchMedia = () => ({ matches: true })

function unresolvedTransition(skipTransition = vi.fn()): SettingsViewTransition {
  return {
    finished: new Promise<void>(() => undefined),
    skipTransition,
  }
}

describe('settings navigation cache and retry state', () => {
  it('preserves successful pages across A to B to A navigation', () => {
    // Given a fresh controller with A loaded successfully
    const navigation = createSettingsNavigation({ initialId: 'model', matchMedia: motionAllowed })
    navigation.markVisited('model')

    // When B succeeds and navigation returns to A
    navigation.switchSection('agents')
    navigation.markVisited('agents')
    navigation.switchSection('model')

    // Then both successful pages remain in the KeepAlive include set
    expect(navigation.activeId.value).toBe('model')
    expect(navigation.visitedIds.value).toEqual(['model', 'agents'])
  })

  it('evicts only the failed page and retry increments only its revision', () => {
    // Given A, B, and C were loaded successfully
    const navigation = createSettingsNavigation({ initialId: 'model', matchMedia: motionAllowed })
    navigation.markVisited('model')
    navigation.markVisited('agents')
    navigation.markVisited('tools')

    // When B fails and is retried
    navigation.markFailed('agents')
    navigation.retry('agents')

    // Then A and C stay cached while only B receives a new identity
    expect(navigation.visitedIds.value).toEqual(['model', 'tools'])
    expect(navigation.failedIds.value).toEqual([])
    expect(navigation.revisions.value.model).toBe(0)
    expect(navigation.revisions.value.agents).toBe(1)
    expect(navigation.revisions.value.tools).toBe(0)
    expect(navigation.cacheKey('agents')).toBe('agents:1')
  })

  it('does not share mutable state between controller instances', () => {
    // Given two fresh controllers
    const first = createSettingsNavigation({ initialId: 'model', matchMedia: motionAllowed })
    const second = createSettingsNavigation({ initialId: 'model', matchMedia: motionAllowed })

    // When only the first controller records a failure and retry
    first.markVisited('agents')
    first.markFailed('agents')
    first.retry('agents')

    // Then the second controller remains pristine
    expect(second.visitedIds.value).toEqual([])
    expect(second.failedIds.value).toEqual([])
    expect(second.revisions.value.agents).toBe(0)
  })
})

describe('settings navigation motion modes', () => {
  it('uses a native view transition around the active-id update', () => {
    // Given motion is allowed and the document supports View Transitions
    const startViewTransition = vi.fn((update: () => void) => {
      update()
      return { finished: Promise.resolve(), skipTransition: vi.fn() }
    })
    const viewTransitionDocument: SettingsViewTransitionDocument = { startViewTransition }
    const navigation = createSettingsNavigation({
      initialId: 'model',
      matchMedia: motionAllowed,
      viewTransitionDocument,
    })

    // When a section is switched
    navigation.switchSection('agents')

    // Then native mode owns the visual transition and applies the update
    expect(navigation.motionMode.value).toBe('native')
    expect(startViewTransition).toHaveBeenCalledOnce()
    expect(navigation.activeId.value).toBe('agents')
  })

  it('uses the Vue fallback and updates synchronously when native support is absent', () => {
    // Given motion is allowed without a native transition API
    const navigation = createSettingsNavigation({ initialId: 'model', matchMedia: motionAllowed })

    // When a section is switched
    navigation.switchSection('agents')

    // Then the state updates synchronously for the keyed Vue Transition host
    expect(navigation.motionMode.value).toBe('vue-fallback')
    expect(navigation.activeId.value).toBe('agents')
  })

  it('uses reduced mode synchronously without invoking native transitions', () => {
    // Given reduced motion is requested even though native support exists
    const startViewTransition = vi.fn(() => unresolvedTransition())
    const navigation = createSettingsNavigation({
      initialId: 'model',
      matchMedia: reducedMotion,
      viewTransitionDocument: { startViewTransition },
    })

    // When a section is switched
    navigation.switchSection('agents')

    // Then animation is bypassed and state is stable immediately
    expect(navigation.motionMode.value).toBe('reduced')
    expect(startViewTransition).not.toHaveBeenCalled()
    expect(navigation.activeId.value).toBe('agents')
  })

  it('ends on C during rapid A to B to C native switching without timers', () => {
    // Given native update callbacks can complete out of order
    const updates: Array<() => void> = []
    const skipped: ReturnType<typeof vi.fn>[] = []
    const startViewTransition = vi.fn((update: () => void) => {
      const skipTransition = vi.fn()
      updates.push(update)
      skipped.push(skipTransition)
      return unresolvedTransition(skipTransition)
    })
    const navigation = createSettingsNavigation({
      initialId: 'model',
      matchMedia: motionAllowed,
      viewTransitionDocument: { startViewTransition },
    })

    // When B and C are requested before either native callback runs
    navigation.switchSection('agents')
    navigation.switchSection('tools')
    updates[0]?.()
    updates[1]?.()

    // Then the stale B transition is skipped and cannot overwrite C
    expect(skipped[0]).toHaveBeenCalledOnce()
    expect(navigation.activeId.value).toBe('tools')
  })
})
