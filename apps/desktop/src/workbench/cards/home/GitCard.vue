<script setup lang="ts">
import GitPanel from '@/components/GitPanel.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template>
  <GitPanel
    :approvals="c.approvals.value"
    :current-project-path="c.currentProject.value?.path"
    :selected-session-id="c.selectedSessionId.value"
    @decide-approval="(approval, decision) => c.decideApproval(approval, decision)"
    @approval-created="c.recordApproval($event)"
  />
</template>
