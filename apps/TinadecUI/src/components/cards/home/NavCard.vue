<script setup lang="ts">
import AppSidebar from '@/components/AppSidebar.vue'
import { homeController } from '@/controllers/HomeController'
import { useUie } from '../../useUie'
import { useRoute, useRouter } from 'vue-router'

const router = useRouter()
const route = useRoute()
const c = homeController
const wb = useUie()

async function openSession(id: string) {
  await c.setSelectedSession(id)
  if (route.path !== '/') await router.push('/')
}

async function createSession(projectId: string | null) {
  await c.createSession(projectId)
  if (route.path !== '/') await router.push('/')
}

function toggleCollapse() {
  const col = wb.snapshot.value.columns.left
  wb.bus.dispatch({
    command: {
      type: 'collapseColumn',
      scope: wb.scope.value,
      slotId: 'left',
      collapsed: !col?.collapsed,
    },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}
</script>

<template vapor>
  <AppSidebar
    :projects="c.projects.value"
    :sessions="c.sessions.value"
    :selected-project-id="c.selectedProjectId.value"
    :selected-session-id="route.path === '/' ? c.selectedSessionId.value : null"
    :chatroom-active="route.path === '/chatroom'"
    :busy="c.busy.value"
    :collapsed="wb.snapshot.value.columns.left?.collapsed"
    @select-project="c.setSelectedProject($event)"
    @select-session="openSession($event)"
    @create-session="createSession($event)"
    @open-project="c.openProject()"
    @go-market="router.push('/market')"
    @go-settings="router.push('/settings')"
    @go-workbench="router.push('/workbench')"
    @go-chatroom="router.push('/chatroom')"
    @toggle-collapse="toggleCollapse"
    @rename-project="(id, name) => c.renameProject(id, name)"
    @rename-session="(id, title) => c.renameSession(id, title)"
    @archive-project="c.archiveProject($event)"
    @archive-session="c.archiveSession($event)"
    @trash-project="c.trashProject($event)"
    @trash-session="c.trashSession($event)"
  />
</template>
