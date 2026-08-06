<script setup lang="ts">
import { useRouter } from 'vue-router'
import AppSidebar from '@/components/AppSidebar.vue'
import { useHomeWorkbench } from '../homeController'

const router = useRouter()
const home = useHomeWorkbench()
</script>

<template>
  <AppSidebar
    class="workbench-navigation-sidebar"
    :projects="home.projects.value"
    :sessions="home.sessions.value"
    :selected-project-id="home.selectedProjectId.value"
    :selected-session-id="home.selectedSessionId.value"
    :busy="home.busy.value"
    @select-project="home.selectedProjectId.value = $event"
    @select-session="home.selectedSessionId.value = $event"
    @create-session="home.createSession"
    @open-project="home.openProject"
    @go-market="router.push('/market')"
    @go-code="router.push('/code-editor')"
    @go-debug="router.push('/debug-studio')"
    @go-settings="router.push('/settings')"
  />
</template>

<style scoped>
.workbench-navigation-sidebar {
  position: relative;
  inset: auto;
  width: 100%;
  height: 100%;
  border: 0;
  border-radius: 0;
  box-shadow: none;
  background: transparent;
}
</style>
