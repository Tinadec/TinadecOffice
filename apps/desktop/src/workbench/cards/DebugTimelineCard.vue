<script setup lang="ts">
import TraceTimeline from '@/debug/components/TraceTimeline.vue'
import { useDebugWorkbench } from '../debugController'
const debug = useDebugWorkbench()
</script>
<template>
  <TraceTimeline
    :traces="debug.traceData.traces.value"
    :current-trace="debug.traceData.currentTrace.value"
    :selected-span="debug.traceData.selectedSpan.value"
    :loading="debug.traceData.loading.value"
    @select-trace="debug.traceData.fetchTraceDetail"
    @select-span="debug.traceData.selectSpan"
  />
</template>

