<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import AppHeader from '@/components/AppHeader.vue'
import AppSidebar from '@/components/AppSidebar.vue'
import WorkspaceEditorDialog from '@/components/WorkspaceEditorDialog.vue'
import { homeController as c } from '@/controllers/HomeController'
import { requestSettingsSection } from '@/lib/pageRequests'
import { ensureProductionUie } from '@/lib/uiEngine'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { usePageTransition } from '@/composables/usePageTransition'

const router = useRouter()
const { t } = useI18n()
const uie = ensureProductionUie()
const { transitionClasses } = usePageTransition()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()

const sidebarPanelStyle = computed(() => getPanelStyle())
const mainPanelStyle = computed(() => getPanelStyle())
const panelDataAttrs = computed(() => getPanelDataAttributes())
const pageMaterialStyle = computed(() => {
  const material = getPanelStyle()
  return {
    '--material-filter-section': material['--material-filter-section'] ?? 'none',
    '--material-filter-raised': material['--material-filter-raised'] ?? 'none',
  }
})

const sidebarCollapsed = computed(() => Boolean(uie.snapshot.value.columns.left?.collapsed))

onMounted(() => {
  c.start()
})

async function openSession(id: string) {
  await c.setSelectedSession(id)
  await router.push('/')
}

async function createSession(projectId: string | null) {
  c.startNewConversation(projectId)
  await router.push('/')
}

function openPersonal() {
  requestSettingsSection('personal')
  void router.push('/settings')
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
  <div
    class="board-shell-page"
    :class="transitionClasses"
    :style="pageMaterialStyle"
    v-bind="panelDataAttrs"
    data-testid="board-shell-layout"
  >
    <div class="top-drag-bar" />
    <AppHeader />

    <div class="board-shell-grid" :class="{ 'board-shell-grid--collapsed': sidebarCollapsed }">
      <AppSidebar
        class="board-shell-sidebar"
        :projects="c.projects.value"
        :sessions="c.visibleSessions.value"
        :selected-project-id="c.selectedProjectId.value"
        :selected-session-id="null"
        :busy="c.busy.value"
        :workspace-load-states="c.workspaceLoadStates.value"
        :collapsed="sidebarCollapsed"
        :panel-style="sidebarPanelStyle"
        :panel-data-attrs="panelDataAttrs"
        @select-session="openSession($event)"
        @create-session="createSession($event)"
        @open-project="c.openProject()"
        @edit-workspace="c.editWorkspace($event)"
        @retry-workspaces="c.retryWorkspaces($event)"
        @unregister-workspace="c.unregisterWorkspace($event)"
        @go-settings="router.push('/settings')"
        @go-companion-center="router.push('/companion-center')"
        @go-personal="openPersonal"
        @go-board="router.push('/board')"
        @change-view="router.push($event === 'space' ? '/space' : '/')"
        @toggle-collapse="toggleCollapse"
        @rename-project="c.renameProject"
        @rename-session="c.renameSession"
        @archive-project="c.archiveProject($event)"
        @archive-session="c.archiveSession($event)"
        @trash-project="c.trashProject($event)"
        @trash-session="c.trashSession($event)"
        @migrate-session="c.migrateSession"
      />

      <main
        class="board-shell-main"
        :aria-label="t('board.mainWorkspaceAria')"
        :style="mainPanelStyle"
        v-bind="panelDataAttrs"
      >
        <slot />
      </main>
    </div>

    <WorkspaceEditorDialog />
  </div>
</template>

<style scoped>
.board-shell-page {
  --radius-island: 12px;
  position: relative;
  width: 100%;
  height: 100vh;
  overflow: hidden;
  background: transparent;
}

.board-shell-grid {
  display: grid;
  grid-template-columns: 260px minmax(0, 1fr);
  gap: 8px;
  width: 100%;
  height: 100%;
  padding: 8px;
  box-sizing: border-box;
  overflow: hidden;
  transition: grid-template-columns var(--duration-panel, 240ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.board-shell-grid--collapsed {
  grid-template-columns: 52px minmax(0, 1fr);
}

.board-shell-sidebar {
  min-height: 0;
  height: 100%;
  border-radius: var(--radius-island, 12px);
  border: 1px solid var(--border-card, var(--border-muted));
  box-shadow: var(--shadow-card-subtle);
}

.board-shell-main {
  margin-top: 38px;
  min-width: 0;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  border-radius: var(--radius-island, 12px);
  border: 1px solid var(--border-card, var(--border-muted));
  box-shadow: var(--shadow-card-subtle);
  transition: opacity var(--duration-pop, 180ms) var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.board-shell-page.route-preparing .board-shell-main,
.board-shell-page.route-exiting .board-shell-main {
  opacity: 0;
}

@media (max-width: 960px) {
  .board-shell-grid:not(.board-shell-grid--collapsed) {
    grid-template-columns: 224px minmax(0, 1fr);
  }
}

@media (prefers-reduced-motion: reduce) {
  .board-shell-grid,
  .board-shell-main {
    transition: none;
  }
}
</style>
