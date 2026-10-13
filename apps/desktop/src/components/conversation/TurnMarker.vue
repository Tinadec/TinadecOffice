<script setup lang="ts">
import { AlertCircle, Archive, CircleStop, FolderOpen, Hand, RefreshCw, RotateCcw } from '@lucide/vue'
import { computed, type Component } from 'vue'
import { useI18n } from 'vue-i18n'
import { Marker, MarkerContent, MarkerIcon } from '@/components/ui/marker'
import type { MarkerEntry, MarkerType } from '@/lib/conversation/reducer'

/**
 * One system line in the feed: compacted, interrupted, retrying, recovered, stopped,
 * failed. A failure says why in words, never only by colour.
 */
const props = defineProps<{ type: MarkerType; data?: MarkerEntry['data'] }>()
const { t } = useI18n()

const ICONS: Record<MarkerType, Component> = {
  compacted: Archive,
  interrupted: Hand,
  worker_interrupted: Hand,
  model_retry: RefreshCw,
  model_recovered: RefreshCw,
  recovered: RotateCcw,
  workspace_bound: FolderOpen,
  response_fallback: AlertCircle,
  run_failed: AlertCircle,
  run_cancelled: CircleStop,
}

const label = computed(() => {
  const data = props.data ?? {}
  switch (props.type) {
    case 'run_failed': {
      const reason = data.message ?? data.category
      return reason ? t('conversation.marker.run_failed', { reason }) : t('conversation.marker.run_failed_unknown')
    }
    case 'model_retry':
      return t('conversation.marker.model_retry', { retry: data.retry ?? '?', limit: data.limit ?? '?' })
    case 'workspace_bound':
      return t('conversation.marker.workspace_bound', { root: data.root ?? '' })
    default:
      return t(`conversation.marker.${props.type}`)
  }
})
const tone = computed(() => (props.type === 'run_failed' ? 'danger' : props.type === 'model_retry' || props.type === 'response_fallback' ? 'warning' : 'muted'))
</script>

<template>
  <Marker class="conversation-marker" :data-tone="tone" :data-marker="type" data-testid="turn-marker">
    <MarkerIcon><component :is="ICONS[type]" aria-hidden="true" /></MarkerIcon>
    <MarkerContent>{{ label }}</MarkerContent>
  </Marker>
</template>

<style scoped>
.conversation-marker[data-tone='danger'] { color: var(--accent-danger); }
.conversation-marker[data-tone='warning'] { color: var(--accent-warning); }
</style>
