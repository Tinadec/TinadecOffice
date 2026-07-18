import { ref, type Ref } from 'vue'
import { api } from '@/api'

/**
 * Connection state machine for splash screen gating.
 * - 'connecting': splash visible, polling backend health
 * - 'connected': backend reachable, splash hidden
 * - 'timeout': 30s elapsed without backend, splash hidden (enter main UI anyway)
 * - 'disconnected': was connected, later health probe failed
 */
export type ConnectionState = 'connecting' | 'connected' | 'timeout' | 'disconnected'

export const CONNECTION_TIMEOUT_MS = 30_000
export const CONNECTION_POLL_INTERVAL_MS = 1_500
export const CONNECTION_BANNER_KEY = 'backend-connection'

let state: Ref<ConnectionState> | null = null
let timeoutHandle: ReturnType<typeof setTimeout> | null = null
let pollHandle: ReturnType<typeof setInterval> | null = null
let watchHandle: ReturnType<typeof setInterval> | null = null
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

function clearWatch() {
  if (watchHandle !== null) {
    clearInterval(watchHandle)
    watchHandle = null
  }
}

function markConnected() {
  const current = getState()
  if (current.value === 'connecting' || current.value === 'timeout' || current.value === 'disconnected') {
    current.value = 'connected'
    clearTimers()
    startHealthWatch()
  }
}

function markDisconnected() {
  const current = getState()
  if (current.value === 'connected') {
    current.value = 'disconnected'
  }
}

function markTimeout() {
  const current = getState()
  if (current.value === 'connecting') {
    current.value = 'timeout'
    clearTimers()
    startHealthWatch()
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

function startHealthWatch() {
  if (watchHandle !== null) return
  watchHandle = setInterval(async () => {
    const current = getState()
    if (current.value === 'connecting') return
    const ok = await probe()
    if (ok) {
      if (current.value !== 'connected') markConnected()
    } else if (current.value === 'connected') {
      markDisconnected()
    }
  }, CONNECTION_POLL_INTERVAL_MS * 4)
}

export async function retryConnection(): Promise<boolean> {
  const ok = await probe()
  if (ok) {
    markConnected()
    return true
  }
  return false
}

export function useConnection() {
  const connectionState = getState()

  async function start() {
    if (started) return
    started = true

    timeoutHandle = setTimeout(() => {
      markTimeout()
    }, CONNECTION_TIMEOUT_MS)

    if (await probe()) {
      markConnected()
      return
    }

    pollHandle = setInterval(async () => {
      if (await probe()) {
        markConnected()
      }
    }, CONNECTION_POLL_INTERVAL_MS)
  }

  return { connectionState, start, retryConnection }
}

/** Test-only: reset singleton state between tests. */
export function __resetConnectionForTests() {
  clearTimers()
  clearWatch()
  if (state) state.value = 'connecting'
  started = false
}
