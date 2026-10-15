<script setup lang="ts">
import OrchestrationTab from '@/components/OrchestrationTab.vue'
import CardScroll from './CardScroll.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template>
  <CardScroll>
    <OrchestrationTab
      :snapshot="c.orchestration.value"
      :tool-executions="c.toolExecutions.value"
      :session-id="c.selectedSessionId.value"
      @execute-tool="c.executeCatalogTool($event)"
    />
  </CardScroll>
</template>
