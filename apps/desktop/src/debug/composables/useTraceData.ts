import { ref, computed } from 'vue'
import type { TraceSummary, TraceDetail, SpanNode } from '../types/trace'
import type { GetTracesRequest } from '../types/debug-api'
import { useNotifications } from '@/composables/useNotifications'

/**
 * Composable for fetching and caching trace data from the Debug API.
 */
export function useTraceData() {
  const gatewayUrl = window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
  const { status, dismissByKey } = useNotifications()

  const traces = ref<TraceSummary[]>([])
  const currentTrace = ref<TraceDetail | null>(null)
  const selectedSpan = ref<SpanNode | null>(null)
  const loading = ref(false)
  const error = ref<string | null>(null)

  async function fetchTraces(params?: GetTracesRequest): Promise<void> {
    loading.value = true
    error.value = null

    try {
      const query = new URLSearchParams()
      if (params?.session_id) query.set('sessionId', params.session_id)
      if (params?.run_id) query.set('runId', params.run_id)
      if (params?.name) query.set('name', params.name)
      if (params?.status) query.set('status', params.status)
      if (params?.min_duration_ms) query.set('minDurationMs', String(params.min_duration_ms))
      if (params?.limit) query.set('limit', String(params.limit))
      if (params?.offset) query.set('offset', String(params.offset))

      const response = await fetch(`${gatewayUrl}/api/v1/debug/traces?${query.toString()}`)
      if (!response.ok) throw new Error(`HTTP ${response.status}`)

      const data = await response.json()
      traces.value = data.traces ?? []
      dismissByKey('debug-trace')
    } catch (e) {
      error.value = `Failed to fetch traces: ${e}`
      status.error({ key: 'debug-trace', message: `Failed to fetch traces: ${e}`, source: 'debug' })
    } finally {
      loading.value = false
    }
  }

  async function fetchTraceDetail(traceId: string): Promise<void> {
    loading.value = true
    error.value = null

    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/traces/${traceId}`)
      if (!response.ok) throw new Error(`HTTP ${response.status}`)

      const data = await response.json()
      currentTrace.value = data
      dismissByKey('debug-trace')
    } catch (e) {
      error.value = `Failed to fetch trace detail: ${e}`
      status.error({ key: 'debug-trace', message: `Failed to fetch trace detail: ${e}`, source: 'debug' })
    } finally {
      loading.value = false
    }
  }

  function selectSpan(span: SpanNode | null) {
    selectedSpan.value = span
  }

  const spanCount = computed(() => traces.value.reduce((sum, t) => sum + t.span_count, 0))
  const errorCount = computed(() => traces.value.reduce((sum, t) => sum + t.error_count, 0))

  return {
    traces,
    currentTrace,
    selectedSpan,
    loading,
    error,
    fetchTraces,
    fetchTraceDetail,
    selectSpan,
    spanCount,
    errorCount,
  }
}
