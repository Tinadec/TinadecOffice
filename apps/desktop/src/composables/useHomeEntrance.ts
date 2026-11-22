import { nextTick, onBeforeUnmount, onMounted, ref, type Ref } from 'vue'
import {
  PAGE_ENTER_DURATION_MS,
  PAGE_EXIT_DURATION_MS,
  PAGE_STAGGER_STEP_MS,
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

  function nextFrame(): Promise<boolean> {
    return new Promise((resolve) => {
      releaseFrame = resolve
      frameId = requestAnimationFrame(() => {
        frameId = undefined
        releaseFrame = undefined
        resolve(true)
      })
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
    phase.value = 'entering'
    onReady()
    await nextTick()

    while (alive) {
      const animations = (root.value?.getAnimations({ subtree: true }) ?? [])
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
    if (frameId !== undefined) cancelAnimationFrame(frameId)
    releaseFrame?.(false)
    releaseFrame = undefined
  })

  return { contentReady, phase }
}
