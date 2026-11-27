import { readonly, ref } from 'vue'

/**
 * Maximum milliseconds the startup AppSplash waits for backend readiness before
 * automatically unveiling the UI and surfacing "connecting to backend" on the
 * Dynamic Island.
 */
export const SPLASH_MAX_WAIT_MS = 15_000

/**
 * Brief lead-in delay (ms) between starting AppSplash's upward fly-off
 * (`entryReady = true`) and triggering the page's rise-from-below entrance so
 * the rising Logo curtain reveals the columns/modules mid-flight.
 */
export const SPLASH_CURTAIN_LEAD_IN_MS = 140

const gateOpen = ref(true)
const fallbackTriggered = ref(false)
const armed = ref(false)
let initialUnveilPending = false
let waiters: Array<() => void> = []

function resolveWaiters(): void {
  const pending = waiters
  waiters = []
  for (const resolve of pending) {
    resolve()
  }
}

export const splashGateOpen = readonly(gateOpen)
export const splashFallbackTriggered = readonly(fallbackTriggered)

/**
 * Arm the cold-start splash gate during main-window setup.
 * Left open by default so isolated component/composable unit tests never stall.
 */
export function armStartupSplashGate(): void {
  armed.value = true
  gateOpen.value = false
  fallbackTriggered.value = false
  initialUnveilPending = true
}

/**
 * Open the splash gate once the backend/host reaches a decisive state (`ready`),
 * the 15s fallback deadline expires (`fallback`), or a non-splash window
 * bypasses startup choreography (`bypass`).
 */
export function openStartupSplashGate(reason: 'ready' | 'fallback' | 'bypass' = 'ready'): void {
  if (gateOpen.value) return
  if (reason === 'fallback') {
    fallbackTriggered.value = true
  }
  if (reason === 'bypass') {
    initialUnveilPending = false
  }
  gateOpen.value = true
  resolveWaiters()
}

/**
 * Wait until the startup splash gate opens. Returns `true` only once for the
 * initial cold-start unveil so entrance choreography waits for the rising curtain;
 * returns `false` on subsequent route navigations and in unarmed tests.
 */
export async function waitForStartupSplashGate(
  isActive: () => boolean = () => true,
): Promise<boolean> {
  if (!armed.value || !initialUnveilPending || !isActive()) {
    return false
  }
  if (!gateOpen.value) {
    await new Promise<void>((resolve) => {
      waiters.push(resolve)
    })
  }
  if (!isActive() || !initialUnveilPending) {
    return false
  }
  initialUnveilPending = false
  return true
}

/** Test-only reset for startup splash gate singleton state. */
export function __resetStartupSplashGateForTests(): void {
  armed.value = false
  gateOpen.value = true
  fallbackTriggered.value = false
  initialUnveilPending = false
  resolveWaiters()
}
