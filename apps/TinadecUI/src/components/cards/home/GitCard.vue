<script setup lang="ts">
import GitPanel from '@/components/GitPanel.vue'
import { homeController } from '@/controllers/HomeController'
import { highlightedDockCard } from '@/lib/homeDockSync'

const c = homeController
</script>

<template>
  <GitPanel
    :approvals="c.approvals.value"
    :current-project-path="c.currentProject.value?.path"
    :selected-session-id="c.selectedSessionId.value"
    :runs="c.runs.value"
    :feed="c.conversationFeed"
    :orchestration="c.orchestration.value"
    :tool-executions="c.toolExecutions.value"
    data-dock-card="git"
    :data-dock-highlighted="highlightedDockCard === 'git' ? 'true' : undefined"
    @decide-approval="(approval, decision) => c.decideApproval(approval, decision)"
    @approval-created="c.recordApproval($event)"
  />
</template>
