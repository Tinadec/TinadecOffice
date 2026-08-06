<script setup lang="ts">
import GitPanel from '@/components/GitPanel.vue'
import { useHomeWorkbench } from '../homeController'

const home = useHomeWorkbench()
</script>

<template>
  <GitPanel
    :approvals="home.approvals.value"
    :current-project-path="home.currentProject.value?.path"
    :selected-session-id="home.selectedSessionId.value"
    @decide-approval="home.decideApproval"
    @approval-created="home.recordApproval"
  />
</template>

<style scoped>
:deep(.git-panel) { width: 100%; height: 100%; min-height: 0; }
</style>

