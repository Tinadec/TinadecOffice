<script setup lang="ts">
import OrchestrationTab from '@/components/OrchestrationTab.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template>
  <OrchestrationTab
    :snapshot="c.orchestration.value"
    :tool-executions="c.toolExecutions.value"
  />
</template>
