import { ref, onUnmounted } from 'vue'
import type { WsMessage } from '../types/debug-api'
import { useNotifications } from '@/composables/useNotifications'

const WS_KEY = 'debug-ws'

/**
 * Composable for managing the Debug Studio WebSocket connection.
 * Provides real-time span events, metric samples, and breakpoint hits.
 *
 * Anti-spam: uses a single stable notification key (WS_KEY) so repeated
 * reconnect failures merge in-place rather than stacking. The key is
 * cleared on successful reconnect via dismissByKey.
 */
export function useDebugWebSocket() {
  const connected = ref(false)
  const messages = ref<WsMessage[]>([])
  const error = ref<string | null>(null)
  const { status, dismissByKey } = useNotifications()

  let ws: WebSocket | null = null
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null

  function getWsUrl(): string {
    const gatewayUrl = window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
    return gatewayUrl.replace(/^http/, 'ws') + '/api/v1/debug/ws'
  }

  function connect() {
    if (ws && ws.readyState === WebSocket.OPEN) return

    try {
      ws = new WebSocket(getWsUrl())

      ws.onopen = () => {
        connected.value = true
        error.value = null
        dismissByKey(WS_KEY)
      }

      ws.onmessage = (event) => {
        try {
          const message: WsMessage = JSON.parse(event.data)
          messages.value.push(message)
          // Keep only the last 500 messages
          if (messages.value.length > 500) {
            messages.value = messages.value.slice(-500)
          }
        } catch {
          // Ignore malformed messages
        }
      }

      ws.onclose = () => {
        connected.value = false
        // Auto-reconnect after 3 seconds
        reconnectTimer = setTimeout(connect, 3000)
      }

      ws.onerror = () => {
        error.value = 'WebSocket connection error'
        connected.value = false
        // Single stable key: repeated failures replace in-place, no spam
        status.error({ key: WS_KEY, message: 'WebSocket connection error', source: 'debug' })
      }
    } catch (e) {
      error.value = `Failed to connect: ${e}`
      status.error({ key: WS_KEY, message: `Failed to connect: ${e}`, source: 'debug' })
    }
  }

  function disconnect() {
    if (reconnectTimer) {
      clearTimeout(reconnectTimer)
      reconnectTimer = null
    }
    ws?.close()
    ws = null
    connected.value = false
  }

  function send(type: string, data?: unknown) {
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type, data }))
    }
  }

  function subscribe(topics: string[]) {
    send('subscribe.topics', { topics })
  }

  function resumeSimulation() {
    send('simulation.resume')
  }

  function stepSimulation() {
    send('simulation.step')
  }

  function resetSimulation() {
    send('simulation.reset')
  }

  onUnmounted(() => {
    disconnect()
  })

  return {
    connected,
    messages,
    error,
    connect,
    disconnect,
    send,
    subscribe,
    resumeSimulation,
    stepSimulation,
    resetSimulation,
  }
}
