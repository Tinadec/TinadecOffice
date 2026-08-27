<script setup lang="ts">
import AppSidebar from '@/components/AppSidebar.vue'
import { homeController } from '@/controllers/HomeController'
import { useUie } from '../../useUie'
import { useRouter } from 'vue-router'

const router = useRouter()
const c = homeController
const wb = useUie()

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
    :selected-session-id="c.selectedSessionId.value"
    :busy="c.busy.value"
    :collapsed="wb.snapshot.value.columns.left?.collapsed"
    @select-project="c.setSelectedProject($event)"
    @select-session="c.setSelectedSession($event)"
    @create-session="c.createSession($event)"
    @open-project="c.openProject()"
    @go-market="router.push('/market')"
    @go-settings="router.push('/settings')"
    @toggle-collapse="toggleCollapse"
    @rename-project="(id, name) => c.renameProject(id, name)"
    @rename-session="(id, title) => c.renameSession(id, title)"
    @archive-project="c.archiveProject($event)"
    @archive-session="c.archiveSession($event)"
    @trash-project="c.trashProject($event)"
    @trash-session="c.trashSession($event)"
  />
</template>
