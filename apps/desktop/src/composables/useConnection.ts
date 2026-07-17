import { ref, type Ref } from 'vue'
import { api } from '@/api'

/**
 * Connection state machine for splash screen gating.
 * - 'connecting': splash visible, polling backend health
 * - 'connected': backend reachable, splash hidden
 * - 'timeout': 30s elapsed without backend, splash hidden (enter main UI anyway)
 */
export type ConnectionState = 'connecting' | 'connected' | 'timeout'

export const CONNECTION_TIMEOUT_MS = 30_000
export const CONNECTION_POLL_INTERVAL_MS = 1_500

let state: Ref<ConnectionState> | null = null
let timeoutHandle: ReturnType<typeof setTimeout> | null = null
let pollHandle: ReturnType<typeof setInterval> | null = null
let started = false

function getState(): Ref<ConnectionState> {
  if (!state) {
    state = ref<ConnectionState>('connecting')
  }
  return state
}

function clearTimers() {
  if (timeoutHandle !== null) {
    clearTimeout(timeoutHandle)
    timeoutHandle = null
  }
  if (pollHandle !== null) {
    clearInterval(pollHandle)
    pollHandle = null
  }
}

function markConnected() {
  if (state && state.value === 'connecting') {
    state.value = 'connected'
    clearTimers()
  }
}

async function probe(): Promise<boolean> {
  try {
    await api.health()
    return true
  } catch {
    return false
  }
}

export function useConnection() {
  const connectionState = getState()

  async function start() {
    if (started) return
    started = true

    // 30s timeout: if still 'connecting', transition to 'timeout'
    timeoutHandle = setTimeout(() => {
      if (state && state.value === 'connecting') {
        state.value = 'timeout'
        clearTimers()
      }
    }, CONNECTION_TIMEOUT_MS)

    // Immediate first probe (don't wait for first interval tick)
    if (await probe()) {
      markConnected()
      return
    }

    // Poll every CONNECTION_POLL_INTERVAL_MS until success or timeout
    pollHandle = setInterval(async () => {
      if (await probe()) {
        markConnected()
      }
    }, CONNECTION_POLL_INTERVAL_MS)
  }

  return { connectionState, start }
}

/** Test-only: reset singleton state between tests. */
export function __resetConnectionForTests() {
  clearTimers()
  if (state) state.value = 'connecting'
  started = false
}
