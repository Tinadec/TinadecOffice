<script setup lang="ts">
import SubAgentsPanel from '@/components/conversation/SubAgentsPanel.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template>
  <SubAgentsPanel
    :feed="c.conversationFeed"
    :runs="c.runs.value"
    :workspace-root="c.currentProject.value?.path ?? null"
  />
</template>
