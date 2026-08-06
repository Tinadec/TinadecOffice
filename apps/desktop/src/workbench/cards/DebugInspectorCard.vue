<script setup lang="ts">
import InspectorPanel from '@/debug/components/InspectorPanel.vue'
import { useDebugWorkbench } from '../debugController'
const debug = useDebugWorkbench()
</script>
<template><InspectorPanel :span="debug.traceData.selectedSpan.value" /></template>

