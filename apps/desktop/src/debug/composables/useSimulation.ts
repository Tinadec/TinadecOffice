import { ref } from 'vue'
import i18n from '@/i18n'
import { storageFetch as fetch } from '@/lib/storageScope'
import type {
  SimulateMessageRequest,
  SimulateMessageResponse,
  SimulateModelResponseRequest,
  SimulateToolResultRequest,
  ForceApprovalDecisionRequest,
  PatchAgentStateRequest,
  Breakpoint,
  CreateBreakpointRequest,
} from '../types/simulation'
import { useNotifications } from '@/composables/useNotifications'

/**
 * Composable for managing simulation state and API calls.
 */
export function useSimulation() {
  const gatewayUrl = window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
  const { status, notify, dismissByKey } = useNotifications()

  const breakpoints = ref<Breakpoint[]>([])
  const loading = ref(false)
  const error = ref<string | null>(null)

  async function injectMessage(request: SimulateMessageRequest): Promise<SimulateMessageResponse | null> {
    loading.value = true
    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/simulate/message`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(request),
      })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      return await response.json()
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.injectMessage', { error: String(e) })
      notify.error(i18n.global.t('debugStudio.requestFailed.injectMessage', { error: String(e) }), { source: 'debug' })
      return null
    } finally {
      loading.value = false
    }
  }

  async function injectModelResponse(request: SimulateModelResponseRequest): Promise<SimulateMessageResponse | null> {
    loading.value = true
    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/simulate/model-response`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(request),
      })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      return await response.json()
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.injectModelResponse', { error: String(e) })
      notify.error(i18n.global.t('debugStudio.requestFailed.injectModelResponse', { error: String(e) }), { source: 'debug' })
      return null
    } finally {
      loading.value = false
    }
  }

  async function injectToolResult(request: SimulateToolResultRequest): Promise<SimulateMessageResponse | null> {
    loading.value = true
    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/simulate/tool-result`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(request),
      })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      return await response.json()
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.injectToolResult', { error: String(e) })
      notify.error(i18n.global.t('debugStudio.requestFailed.injectToolResult', { error: String(e) }), { source: 'debug' })
      return null
    } finally {
      loading.value = false
    }
  }

  async function forceApprovalDecision(request: ForceApprovalDecisionRequest): Promise<SimulateMessageResponse | null> {
    loading.value = true
    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/simulate/approval-decision`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(request),
      })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      return await response.json()
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.forceApproval', { error: String(e) })
      notify.error(i18n.global.t('debugStudio.requestFailed.forceApproval', { error: String(e) }), { source: 'debug' })
      return null
    } finally {
      loading.value = false
    }
  }

  async function patchAgentState(request: PatchAgentStateRequest): Promise<SimulateMessageResponse | null> {
    loading.value = true
    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/simulate/state-patch`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(request),
      })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      return await response.json()
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.patchAgentState', { error: String(e) })
      notify.error(i18n.global.t('debugStudio.requestFailed.patchAgentState', { error: String(e) }), { source: 'debug' })
      return null
    } finally {
      loading.value = false
    }
  }

  async function fetchBreakpoints(): Promise<void> {
    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/breakpoints`)
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      breakpoints.value = await response.json()
      dismissByKey('debug-sim-breakpoints')
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.fetchBreakpoints', { error: String(e) })
      status.error({ key: 'debug-sim-breakpoints', message: i18n.global.t('debugStudio.requestFailed.fetchBreakpoints', { error: String(e) }), source: 'debug' })
    }
  }

  async function createBreakpoint(request: CreateBreakpointRequest): Promise<Breakpoint | null> {
    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/breakpoints`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(request),
      })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      const bp = await response.json()
      breakpoints.value.push(bp)
      return bp
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.createBreakpoint', { error: String(e) })
      notify.error(i18n.global.t('debugStudio.requestFailed.createBreakpoint', { error: String(e) }), { source: 'debug' })
      return null
    }
  }

  async function deleteBreakpoint(id: string): Promise<boolean> {
    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/breakpoints/${id}`, { method: 'DELETE' })
      if (!response.ok && response.status !== 204) throw new Error(`HTTP ${response.status}`)
      breakpoints.value = breakpoints.value.filter(bp => bp.id !== id)
      return true
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.deleteBreakpoint', { error: String(e) })
      notify.error(i18n.global.t('debugStudio.requestFailed.deleteBreakpoint', { error: String(e) }), { source: 'debug' })
      return false
    }
  }

  return {
    breakpoints,
    loading,
    error,
    injectMessage,
    injectModelResponse,
    injectToolResult,
    forceApprovalDecision,
    patchAgentState,
    fetchBreakpoints,
    createBreakpoint,
    deleteBreakpoint,
  }
}
