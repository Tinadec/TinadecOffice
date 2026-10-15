import { ref } from 'vue'
import i18n from '@/i18n'
import { storageFetch as fetch } from '@/lib/storageScope'
import type { MetricsResponse, DiagnosticsReport } from '../types/metrics'
import { useNotifications } from '@/composables/useNotifications'

/**
 * Composable for fetching metrics and diagnostics data.
 */
export function useMetrics() {
  const gatewayUrl = window.tinadec?.gatewayUrl?.() ?? 'http://127.0.0.1:48730'
  const { status, notify, dismissByKey } = useNotifications()

  const metrics = ref<MetricsResponse | null>(null)
  const diagnostics = ref<DiagnosticsReport | null>(null)
  const loading = ref(false)
  const error = ref<string | null>(null)

  async function fetchMetrics(metricName: string, windowMs?: number, bucketMs?: number): Promise<void> {
    loading.value = true
    error.value = null
    try {
      const params = new URLSearchParams()
      params.set('metricName', metricName)
      if (windowMs) params.set('windowMs', String(windowMs))
      if (bucketMs) params.set('bucketMs', String(bucketMs))

      const response = await fetch(`${gatewayUrl}/api/v1/debug/metrics?${params.toString()}`)
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      metrics.value = await response.json()
      error.value = null
      dismissByKey('debug-metrics')
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.fetchMetrics', { error: String(e) })
      status.error({ key: 'debug-metrics', message: i18n.global.t('debugStudio.requestFailed.fetchMetrics', { error: String(e) }), source: 'debug' })
    } finally {
      loading.value = false
    }
  }

  async function fetchDiagnostics(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/diagnostics`)
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      diagnostics.value = await response.json()
      error.value = null
      dismissByKey('debug-metrics')
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.fetchDiagnostics', { error: String(e) })
      status.error({ key: 'debug-metrics', message: i18n.global.t('debugStudio.requestFailed.fetchDiagnostics', { error: String(e) }), source: 'debug' })
    } finally {
      loading.value = false
    }
  }

  async function fetchProcessInfo(): Promise<Record<string, unknown> | null> {
    error.value = null
    try {
      const response = await fetch(`${gatewayUrl}/api/v1/debug/processes`)
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      const result = await response.json()
      error.value = null
      return result
    } catch (e) {
      error.value = i18n.global.t('debugStudio.requestFailed.fetchProcessInfo', { error: String(e) })
      notify.error(i18n.global.t('debugStudio.requestFailed.fetchProcessInfo', { error: String(e) }), { source: 'debug' })
      return null
    }
  }

  return {
    metrics,
    diagnostics,
    loading,
    error,
    fetchMetrics,
    fetchDiagnostics,
    fetchProcessInfo,
  }
}
