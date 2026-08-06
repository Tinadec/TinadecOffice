<script setup lang="ts">
import MetricsDashboard from '@/debug/components/MetricsDashboard.vue'
import { useDebugWorkbench } from '../debugController'
const debug = useDebugWorkbench()
</script>
<template><MetricsDashboard :diagnostics="debug.metrics.diagnostics.value" /></template>

