import { inject, provide, type InjectionKey } from 'vue'
import { useNotifications } from '@/composables/useNotifications'
import { useDebugWebSocket } from '@/debug/composables/useDebugWebSocket'
import { useTraceData } from '@/debug/composables/useTraceData'
import { useSimulation } from '@/debug/composables/useSimulation'
import { useMetrics } from '@/debug/composables/useMetrics'
import type { ForceApprovalDecisionRequest, SimulateMessageRequest } from '@/debug/types/simulation'

export function createDebugWorkbenchController() {
  const { notify, confirm } = useNotifications()
  const ws = useDebugWebSocket()
  const traceData = useTraceData()
  const simulation = useSimulation()
  const metrics = useMetrics()
  let started = false

  async function injectMessage(request: SimulateMessageRequest): Promise<void> {
    const response = await simulation.injectMessage(request)
    if (response?.simulated) notify.success({ message: 'Simulation message injected', source: 'debug' })
  }

  async function forceApproval(request: ForceApprovalDecisionRequest): Promise<void> {
    if (!await confirm({
      title: 'Force simulated approval decision',
      message: `Force this approval to be ${request.decision}?`,
      confirmLabel: request.decision === 'approved' ? 'Approve' : 'Reject',
      destructive: true,
    })) return
    await simulation.forceApprovalDecision(request)
  }

  function start(): void {
    if (started) return
    started = true
    ws.connect()
    void traceData.fetchTraces()
    void metrics.fetchDiagnostics()
  }

  return { ws, traceData, simulation, metrics, start, injectMessage, forceApproval }
}

export type DebugWorkbenchController = ReturnType<typeof createDebugWorkbenchController>
const DEBUG_KEY: InjectionKey<DebugWorkbenchController> = Symbol('debug-workbench')
export function provideDebugWorkbench(controller: DebugWorkbenchController): void { provide(DEBUG_KEY, controller) }
export function useDebugWorkbench(): DebugWorkbenchController {
  const controller = inject(DEBUG_KEY)
  if (!controller) throw new Error('Debug workbench controller was not provided.')
  return controller
}

