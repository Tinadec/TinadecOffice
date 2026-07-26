import { ref } from 'vue'
import type {
  SimulateMessageRequest,
  SimulateMessageResponse,
  SimulateModelResponseRequest,
  SimulateToolResultRequest,
  ForceApprovalDecisionRequest,
  PatchAgentStateRequest,
  Breakpoint,
  CreateBreakpointRequest,
  SimulationMode,
} from '../types/simulation'
import { useNotifications } from '@/composables/useNotifications'

/**
 * Composable for managing simulation state and API calls.
 */
export function useSimulation() {
  const gatewayUrl = window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
  const { status, notify, dismissByKey } = useNotifications()

  const mode = ref<SimulationMode>('idle')
  const currentStep = ref(0)
  const totalSteps = ref(0)
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
      error.value = `Failed to inject message: ${e}`
      notify.error(`Failed to inject message: ${e}`, { source: 'debug' })
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
      error.value = `Failed to inject model response: ${e}`
      notify.error(`Failed to inject model response: ${e}`, { source: 'debug' })
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
      error.value = `Failed to inject tool result: ${e}`
      notify.error(`Failed to inject tool result: ${e}`, { source: 'debug' })
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
      error.value = `Failed to force approval: ${e}`
      notify.error(`Failed to force approval: ${e}`, { source: 'debug' })
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
      error.value = `Failed to patch agent state: ${e}`
      notify.error(`Failed to patch agent state: ${e}`, { source: 'debug' })
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
      error.value = `Failed to fetch breakpoints: ${e}`
      status.error({ key: 'debug-sim-breakpoints', message: `Failed to fetch breakpoints: ${e}`, source: 'debug' })
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
      error.value = `Failed to create breakpoint: ${e}`
      notify.error(`Failed to create breakpoint: ${e}`, { source: 'debug' })
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
      error.value = `Failed to delete breakpoint: ${e}`
      notify.error(`Failed to delete breakpoint: ${e}`, { source: 'debug' })
      return false
    }
  }

  return {
    mode,
    currentStep,
    totalSteps,
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
