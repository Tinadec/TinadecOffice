import { nextTick, onBeforeUnmount, onMounted, ref, type Ref } from 'vue'
import {
  SPLASH_CURTAIN_LEAD_IN_MS,
  waitForStartupSplashGate,
} from '@/lib/startupSplashGate'
import {
  PAGE_ENTER_DURATION_MS,
  PAGE_EXIT_DURATION_MS,
  PAGE_STAGGER_STEP_MS,
  prefersReducedMotion,
} from './usePageTransition'

export { PAGE_ENTER_DURATION_MS, PAGE_EXIT_DURATION_MS, PAGE_STAGGER_STEP_MS }

export type HomeEntrancePhase = 'preparing' | 'entering' | 'settled'

/** Prepare and paint the measured layout before starting the column animations. */
export function useHomeEntrance(
  root: Ref<HTMLElement | null>,
  layoutReady: Promise<void>,
  onReady: () => void,
) {
  const contentReady = ref(false)
  const phase = ref<HomeEntrancePhase>('preparing')
  let alive = true
  let frameId: number | undefined
  let releaseFrame: ((painted: boolean) => void) | undefined
  let leadInTimer: ReturnType<typeof setTimeout> | undefined
  let releaseLeadIn: ((proceeded: boolean) => void) | undefined

  function nextFrame(): Promise<boolean> {
    if (typeof requestAnimationFrame !== 'function') {
      return Promise.resolve(true)
    }
    return new Promise((resolve) => {
      releaseFrame = resolve
      frameId = requestAnimationFrame(() => {
        frameId = undefined
        releaseFrame = undefined
        resolve(true)
      })
    })
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

  onMounted(async () => {
    await layoutReady
    if (!alive) return
    contentReady.value = true
    // Child mounted hooks measure the canvas; flush their geometry updates,
    // then allow the preparation state to paint before changing CSS classes.
    await nextTick()
    // Home is the first text-heavy surface. A late font swap otherwise moves
    // its controls while the column animation is already playing.
    await document.fonts?.ready
    if (!alive || !await nextFrame() || !alive || !await nextFrame() || !alive) return
    // Notify App.vue that the measured frame is ready underneath the splash.
    onReady()
    const isInitialUnveil = await waitForStartupSplashGate(() => alive)
    if (!alive) return
    if (isInitialUnveil && !await waitLeadIn(SPLASH_CURTAIN_LEAD_IN_MS)) return
    if (!alive) return
    if (prefersReducedMotion()) {
      phase.value = 'settled'
      return
    }
    phase.value = 'entering'
    await nextTick()

    while (alive) {
      const animations = (root.value?.getAnimations?.({ subtree: true }) ?? [])
        .filter((animation) => 'animationName' in animation
          && animation.animationName === 'home-up-enter'
          && animation.playState !== 'finished' && animation.playState !== 'idle')
      if (!animations.length) break
      await Promise.allSettled(animations.map((animation) => animation.finished))
      await nextTick()
      // A project/layout switch can replace a stack during entry. Wait for the
      // replacement's real animation too, rather than truncating it at 500ms.
    }
    if (alive) phase.value = 'settled'
  })

  onBeforeUnmount(() => {
    alive = false
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
  })

  return { contentReady, phase }
}
