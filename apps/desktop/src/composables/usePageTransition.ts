import {
  computed,
  getCurrentInstance,
  nextTick,
  onBeforeUnmount,
  onMounted,
  ref,
  type ComputedRef,
  type Ref,
} from 'vue'
import * as VueRouter from 'vue-router'
import {
  SPLASH_CURTAIN_LEAD_IN_MS,
  waitForStartupSplashGate,
} from '@/lib/startupSplashGate'

export const PAGE_ENTER_DURATION_MS = 380
export const PAGE_EXIT_DURATION_MS = 190
export const PAGE_STAGGER_STEP_MS = 55

export type PageTransitionPhase = 'preparing' | 'entering' | 'idle' | 'exiting'

export interface UsePageTransitionOptions {
  /** Optional readiness gate awaited before flipping `preparing` -> `entering`. */
  ready?: Promise<unknown>
  /** Optional dirty-draft guard run before starting the exit animation. */
  canLeave?: () => boolean | Promise<boolean>
  /** Optional predicate to bypass exit wait when nothing visual is mounted to animate out. */
  skipExitWhen?: () => boolean
  /** Skip the internal mount `preparing -> entering -> idle` cycle (e.g. when `useHomeEntrance` owns it). */
  skipEnterLifecycle?: boolean
  /** Enter animation duration in ms (defaults to `PAGE_ENTER_DURATION_MS` = 280). */
  enterDurationMs?: number
  /** Extra stagger settle window in ms before clearing `entering` -> `idle` (defaults to 70). */
  enterStaggerMs?: number
  /** Exit animation duration in ms (defaults to `PAGE_EXIT_DURATION_MS` = 190). */
  exitDurationMs?: number
  /** Automatically register `onBeforeRouteLeave` when inside a routed view (defaults to `true`). */
  registerLeaveGuard?: boolean
}

export interface PageTransitionState {
  phase: Ref<PageTransitionPhase>
  isPreparing: ComputedRef<boolean>
  isEntering: ComputedRef<boolean>
  isIdle: ComputedRef<boolean>
  isExiting: ComputedRef<boolean>
  transitionClasses: ComputedRef<Record<string, boolean>>
  leaveGuard: () => Promise<boolean>
  resetTransition: () => void
}

/**
 * Returns true when reduced motion is requested or when `window.matchMedia`
 * is unavailable (headless SSR / minimal test environments), bypassing timed waits.
 */
export function prefersReducedMotion(): boolean {
  if (typeof document !== 'undefined') {
    const explicit = document.documentElement?.dataset?.reducedMotion
    if (explicit === 'true') return true
    if (explicit === 'false') return false
  }
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return true
  }
  try {
    return Boolean(window.matchMedia('(prefers-reduced-motion: reduce)')?.matches)
  } catch {
    return true
  }
}

function resolveOnBeforeRouteLeave(): typeof VueRouter.onBeforeRouteLeave | undefined {
  try {
    const candidate = (VueRouter as Record<string, unknown>).onBeforeRouteLeave
    return typeof candidate === 'function'
      ? (candidate as typeof VueRouter.onBeforeRouteLeave)
      : undefined
  } catch {
    return undefined
  }
}

function resolveRouterInstance(): VueRouter.Router | undefined {
  const instance = getCurrentInstance()
  const injectedRouter = (
    instance?.appContext.config.globalProperties as Record<string, unknown> | undefined
  )?.$router as VueRouter.Router | undefined
  if (injectedRouter) return injectedRouter
  try {
    const useRouterFn = (VueRouter as Record<string, unknown>).useRouter
    if (typeof useRouterFn === 'function') {
      return (useRouterFn as () => VueRouter.Router | undefined)()
    }
  } catch {
    // Router mock may omit useRouter; ignore cleanly.
  }
  return undefined
}

/**
 * Unified symmetric page-transition lifecycle across all primary routes.
 *
 * - Mount: `preparing -> entering -> idle` (clearing `entering` once settled so
 *   no permanent `transform` / `will-change` remains on island surfaces at rest).
 * - Leave: `onBeforeRouteLeave` runs optional `canLeave()`, sets `exiting`,
 *   awaits `exitDurationMs` (`190ms`, or `0ms` under `prefers-reduced-motion`),
 *   and resets cleanly to `idle` if downstream navigation is aborted.
 */
export function usePageTransition(options: UsePageTransitionOptions = {}): PageTransitionState {
  const enterDurationMs = options.enterDurationMs ?? PAGE_ENTER_DURATION_MS
  const enterStaggerMs = options.enterStaggerMs ?? PAGE_STAGGER_STEP_MS * 2
  const exitDurationMs = options.exitDurationMs ?? PAGE_EXIT_DURATION_MS
  const skipEnter = Boolean(options.skipEnterLifecycle)

  const phase = ref<PageTransitionPhase>(
    skipEnter || prefersReducedMotion() ? 'idle' : 'preparing',
  )

  let alive = true
  let frameId: number | undefined
  let releaseFrame: ((painted: boolean) => void) | undefined
  let leadInTimer: ReturnType<typeof setTimeout> | undefined
  let releaseLeadIn: ((proceeded: boolean) => void) | undefined
  let enterTimer: ReturnType<typeof setTimeout> | undefined
  let exitTimer: ReturnType<typeof setTimeout> | undefined
  let releaseExit: (() => void) | undefined
  let removeAfterEach: (() => void) | undefined
  let removeOnError: (() => void) | undefined

  function cancelPendingTimers(): void {
    if (frameId !== undefined && typeof cancelAnimationFrame === 'function') {
      cancelAnimationFrame(frameId)
    }
    frameId = undefined
    releaseFrame?.(false)
    releaseFrame = undefined

    if (leadInTimer !== undefined) {
      clearTimeout(leadInTimer)
      leadInTimer = undefined
    }
    releaseLeadIn?.(false)
    releaseLeadIn = undefined

    if (enterTimer !== undefined) {
      clearTimeout(enterTimer)
      enterTimer = undefined
    }
    if (exitTimer !== undefined) {
      clearTimeout(exitTimer)
      exitTimer = undefined
    }
    releaseExit?.()
    releaseExit = undefined
  }

  function waitLeadIn(ms: number): Promise<boolean> {
    if (ms <= 0 || prefersReducedMotion()) return Promise.resolve(true)
    return new Promise((resolve) => {
      releaseLeadIn = resolve
      leadInTimer = setTimeout(() => {
        leadInTimer = undefined
        releaseLeadIn = undefined
        resolve(true)
      }, ms)
    })
  }

  function nextPaintFrame(): Promise<boolean> {
    if (typeof requestAnimationFrame !== 'function') {
      return Promise.resolve(true)
    }
    return new Promise<boolean>((resolve) => {
      releaseFrame = resolve
      frameId = requestAnimationFrame(() => {
        frameId = undefined
        releaseFrame = undefined
        resolve(true)
      })
    })
  }

  function resetTransition(): void {
    cancelPendingTimers()
    if (alive) {
      phase.value = 'idle'
    }
  }

  async function leaveGuard(): Promise<boolean> {
    if (options.canLeave) {
      let allowed = true
      try {
        allowed = await options.canLeave()
      } catch {
        allowed = false
      }
      if (!allowed) {
        if (alive && phase.value === 'exiting') {
          resetTransition()
        }
        return false
      }
    }

    if (!alive || phase.value === 'exiting') {
      return true
    }

    if (options.skipExitWhen?.() || prefersReducedMotion() || exitDurationMs <= 0) {
      return true
    }

    cancelPendingTimers()
    phase.value = 'exiting'

    await new Promise<void>((resolve) => {
      releaseExit = resolve
      exitTimer = setTimeout(() => {
        exitTimer = undefined
        releaseExit = undefined
        resolve()
      }, exitDurationMs)
    })

    return true
  }

  const instance = getCurrentInstance()
  if (instance && options.registerLeaveGuard !== false) {
    const onLeave = resolveOnBeforeRouteLeave()
    const hasInstalledRouter = Boolean(
      (instance.appContext.config.globalProperties as Record<string, unknown> | undefined)?.$router,
    )
    const isMockedHook = Boolean(onLeave && (onLeave as unknown as { mock?: unknown }).mock)

    if (onLeave && (hasInstalledRouter || isMockedHook)) {
      onLeave(() => leaveGuard())
    }

    const router = hasInstalledRouter ? resolveRouterInstance() : undefined
    if (router && typeof router.afterEach === 'function') {
      removeAfterEach = router.afterEach((_to, _from, failure) => {
        // If a downstream guard or duplicate push aborts navigation after
        // beforeRouteLeave ran, restore this still-mounted page cleanly.
        if (alive && phase.value === 'exiting' && failure) {
          resetTransition()
        }
      })
    }
    if (router && typeof router.onError === 'function') {
      removeOnError = router.onError(() => {
        if (alive && phase.value === 'exiting') {
          resetTransition()
        }
      })
    }
  }

  function isStoppedOrExiting(): boolean {
    return !alive || phase.value === 'exiting'
  }

  onMounted(async () => {
    if (skipEnter) return
    if (prefersReducedMotion()) {
      phase.value = 'idle'
      return
    }
    if (options.ready) {
      try {
        await options.ready
      } catch {
        // Proceed with entrance even if an optional readiness promise rejected.
      }
    }
    if (isStoppedOrExiting()) return
    await nextTick()
    if (isStoppedOrExiting()) return
    if (!(await nextPaintFrame()) || isStoppedOrExiting()) return
    const isInitialUnveil = await waitForStartupSplashGate(() => !isStoppedOrExiting())
    if (isStoppedOrExiting()) return
    if (isInitialUnveil && !await waitLeadIn(SPLASH_CURTAIN_LEAD_IN_MS)) return
    if (isStoppedOrExiting()) return
    if (prefersReducedMotion()) {
      phase.value = 'idle'
      return
    }

    phase.value = 'entering'
    const totalEnterMs = Math.max(0, enterDurationMs + enterStaggerMs)
    if (totalEnterMs === 0) {
      phase.value = 'idle'
      return
    }
    enterTimer = setTimeout(() => {
      enterTimer = undefined
      if (alive && phase.value === 'entering') {
        phase.value = 'idle'
      }
    }, totalEnterMs)
  })

  onBeforeUnmount(() => {
    alive = false
    cancelPendingTimers()
    removeAfterEach?.()
    removeAfterEach = undefined
    removeOnError?.()
    removeOnError = undefined
  })

  const isPreparing = computed(() => phase.value === 'preparing')
  const isEntering = computed(() => phase.value === 'entering')
  const isIdle = computed(() => phase.value === 'idle')
  const isExiting = computed(() => phase.value === 'exiting')

  const transitionClasses = computed(() => ({
    'route-preparing': phase.value === 'preparing',
    'route-entering': phase.value === 'entering',
    'route-idle': phase.value === 'idle',
    'route-exiting': phase.value === 'exiting',
  }))

  return {
    phase,
    isPreparing,
    isEntering,
    isIdle,
    isExiting,
    transitionClasses,
    leaveGuard,
    resetTransition,
  }
}
