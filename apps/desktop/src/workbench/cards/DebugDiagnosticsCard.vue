<script setup lang="ts">
import DiagnosticsReport from '@/debug/components/DiagnosticsReport.vue'
import { useDebugWorkbench } from '../debugController'
const debug = useDebugWorkbench()
</script>
<template><DiagnosticsReport :report="debug.metrics.diagnostics.value" /></template>

