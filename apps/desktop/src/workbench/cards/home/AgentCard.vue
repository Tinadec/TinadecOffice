<script setup lang="ts">
import AgentActivityPanel from '@/components/AgentActivityPanel.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template>
  <AgentActivityPanel
    :activity="c.agentActivity.value"
    :agent-states="c.agentStatesMap.value"
    :thinking-steps="c.agentThinkingSteps.value"
    :progress-events="c.agentProgressEvents.value"
    :orchestration="c.orchestration.value"
  />
</template>
