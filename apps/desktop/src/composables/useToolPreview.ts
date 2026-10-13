import { ref, shallowRef, type Ref } from 'vue'
import { api, type ToolExecutionPreviewDto } from '@/api'

/**
 * The bounded result preview of one tool call, fetched lazily: only when the reader opens
 * the row (`load`), only once the call is terminal (a running call's preview would change
 * under them), and at most once per execution id for the lifetime of the window.
 */
const cache = new Map<string, ToolExecutionPreviewDto>()
const inflight = new Map<string, Promise<ToolExecutionPreviewDto>>()

const TERMINAL = new Set(['completed', 'failed', 'outcome_unknown', 'cancelled'])

export function useToolPreview(executionId: Ref<string>, status: Ref<string>) {
  const preview = shallowRef<ToolExecutionPreviewDto | null>(cache.get(executionId.value) ?? null)
  const loading = ref(false)
  const error = ref<unknown>(null)

  async function load(): Promise<void> {
    const id = executionId.value
    if (!TERMINAL.has(status.value)) return
    const cached = cache.get(id)
    if (cached) {
      preview.value = cached
      return
    }
    loading.value = true
    error.value = null
    try {
      let request = inflight.get(id)
      if (!request) {
        request = api.getToolExecutionPreview(id)
        inflight.set(id, request)
      }
      const result = await request
      cache.set(id, result)
      if (executionId.value === id) preview.value = result
    } catch (cause) {
      if (executionId.value === id) error.value = cause
    } finally {
      inflight.delete(id)
      if (executionId.value === id) loading.value = false
    }
  }

  return { preview, loading, error, load }
}

/** For tests. */
export function clearToolPreviewCache(): void {
  cache.clear()
  inflight.clear()
}
