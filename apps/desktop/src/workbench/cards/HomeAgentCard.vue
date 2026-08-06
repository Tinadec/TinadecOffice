<script setup lang="ts">
import AgentActivityPanel from '@/components/AgentActivityPanel.vue'
import { useHomeWorkbench } from '../homeController'

const home = useHomeWorkbench()
</script>

<template>
  <AgentActivityPanel
    :activity="home.agentActivity.value"
    :agent-states="home.agentStatesMap.value"
    :thinking-steps="home.agentThinkingSteps.value"
    :progress-events="home.agentProgressEvents.value"
    :orchestration="home.orchestration.value"
  />
</template>

