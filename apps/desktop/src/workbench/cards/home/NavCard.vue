<script setup lang="ts">
import AppSidebar from '@/components/AppSidebar.vue'
import { homeController } from '@/controllers/HomeController'
import { useRouter } from 'vue-router'

const router = useRouter()
const c = homeController
</script>

<template vapor>
  <AppSidebar
    :projects="c.projects.value"
    :sessions="c.sessions.value"
    :selected-project-id="c.selectedProjectId.value"
    :selected-session-id="c.selectedSessionId.value"
    :busy="c.busy.value"
    @select-project="c.setSelectedProject($event)"
    @select-session="c.setSelectedSession($event)"
    @create-session="c.createSession($event)"
    @open-project="c.openProject()"
    @go-market="router.push('/market')"
    @go-settings="router.push('/settings')"
  />
</template>
