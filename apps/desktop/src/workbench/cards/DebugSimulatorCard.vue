<script setup lang="ts">
import SimulatorBar from '@/debug/components/SimulatorBar.vue'
import { useDebugWorkbench } from '../debugController'
const debug = useDebugWorkbench()
</script>
<template>
  <div class="debug-simulator-card">
    <SimulatorBar
      :mode="debug.simulation.mode.value"
      :current-step="debug.simulation.currentStep.value"
      :total-steps="debug.simulation.totalSteps.value"
      @step="debug.ws.stepSimulation"
      @run="debug.ws.resumeSimulation"
      @pause="debug.simulation.mode.value = 'paused'"
      @reset="debug.ws.resetSimulation"
      @inject-message="debug.injectMessage"
      @inject-tool-result="debug.simulation.injectToolResult"
      @force-approval="debug.forceApproval"
    />
  </div>
</template>
<style scoped>
.debug-simulator-card { display: flex; align-items: flex-end; width: 100%; height: 100%; background: #0d1117; }
:deep(.simulator-bar) { width: 100%; }
</style>

