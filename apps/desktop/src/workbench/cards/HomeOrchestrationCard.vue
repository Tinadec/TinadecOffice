<script setup lang="ts">
import OrchestrationTab from '@/components/OrchestrationTab.vue'
import { useHomeWorkbench } from '../homeController'

const home = useHomeWorkbench()
</script>

<template>
  <OrchestrationTab :snapshot="home.orchestration.value" :tool-executions="home.toolExecutions.value" />
</template>

