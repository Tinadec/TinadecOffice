<script setup lang="ts">
import { computed } from 'vue'
import AppSidebar from '@/components/AppSidebar.vue'
import WorkspaceEditorDialog from '@/components/WorkspaceEditorDialog.vue'
import { homeController } from '@/controllers/HomeController'
import { requestSettingsSection } from '@/lib/pageRequests'
import { setFeedFilter } from '@/lib/companionStore'
import { useUie } from '../../useUie'
import { isCompanionDomainPage } from './navDomain'
import { useRoute, useRouter } from 'vue-router'

const router = useRouter()
const route = useRoute()
const c = homeController
const uie = useUie()

/**
 * The companion centre mounts the same nav card as the work pages, in its companion domain.
 *
 * Same island, same brand header, same footer — only the middle changes: no projects, no sessions,
 * and the entries re-aim the centre instead of selecting a session. Deciding this from the page id
 * keeps one sidebar component instead of a second copy that drifts.
 */
const companionDomain = computed(() => isCompanionDomainPage(uie.snapshot.value.pageId))

async function openSession(id: string) {
  await c.setSelectedSession(id)
  if (route.path !== '/' && route.path !== '/space') await router.push('/')
}

async function createSession(projectId: string | null) {
  c.startNewConversation(projectId)
  if (route.path !== '/' && route.path !== '/space') await router.push('/')
}

/** The footer portrait opens the personal module inside Settings, not whatever section was open last. */
function openPersonal() {
  requestSettingsSection('personal')
  void router.push('/settings')
}

/** The observing page. It is a route, so the selected project/session is untouched. */
function openCompanionCenter() {
  void router.push('/companion-center')
}

/** The companion nav only re-aims the centre; it never touches the work selection. */
function handleCompanionNav(target: 'all' | 'attention' | 'settings') {
  if (target === 'settings') {
    requestSettingsSection('companions')
    void router.push('/settings')
    return
  }
  setFeedFilter(target === 'attention' ? 'attention' : 'all')
}

function toggleCollapse() {
  const col = uie.snapshot.value.columns.left
  uie.bus.dispatch({
    command: {
      type: 'collapseColumn',
      scope: uie.scope.value,
      slotId: 'left',
      collapsed: !col?.collapsed,
    },
    source: 'user',
    expectedRevision: uie.snapshot.value.revision,
  })
}
</script>

<template>
  <AppSidebar
    :projects="companionDomain ? [] : c.projects.value"
    :sessions="companionDomain ? [] : c.visibleSessions.value"
    :selected-project-id="companionDomain ? null : c.selectedProjectId.value"
    :selected-session-id="companionDomain ? null : (route.path === '/' || route.path === '/space' ? c.selectedSessionId.value : null)"
    :space-active="route.path === '/space'"
    :companion-center-active="companionDomain"
    :companion-domain="companionDomain"
    :busy="c.busy.value"
    :workspace-load-states="companionDomain ? {} : c.workspaceLoadStates.value"
    :collapsed="uie.snapshot.value.columns.left?.collapsed"
    @select-session="openSession($event)"
    @create-session="createSession($event)"
    @open-project="c.openProject()"
    @edit-workspace="c.editWorkspace($event)"
    @retry-workspaces="c.retryWorkspaces($event)"
  @unregister-workspace="c.unregisterWorkspace($event)"
    @go-market="router.push('/market')"
    @go-settings="router.push('/settings')"
    @go-companion-center="openCompanionCenter"
    @go-personal="openPersonal"
    @companion-nav="handleCompanionNav"
    @change-view="router.push($event === 'space' ? '/space' : '/')"
    @toggle-collapse="toggleCollapse"
    @rename-project="(id, name) => c.renameProject(id, name)"
    @rename-session="(id, title) => c.renameSession(id, title)"
    @archive-project="c.archiveProject($event)"
    @archive-session="c.archiveSession($event)"
    @trash-project="c.trashProject($event)"
    @trash-session="c.trashSession($event)"
    @migrate-session="(id, target) => c.migrateSession(id, target)"
  />
  <WorkspaceEditorDialog />
</template>
