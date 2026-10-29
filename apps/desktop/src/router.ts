import { createRouter, createWebHashHistory } from 'vue-router'
import { watch } from 'vue'
import { loadDebugStudioPreference, useDebugStudio } from './composables/useDebugStudio'

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    {
      path: '/',
      name: 'home',
      component: () => import('./pages/HomePage.vue'),
    },
    {
      path: '/space',
      name: 'space',
      component: () => import('./pages/SpatialPage.vue'),
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('./pages/SettingsPage.vue'),
    },
    {
      // Legacy standalone agent-center page was merged into Settings (agents tab).
      path: '/agent-center',
      redirect: '/settings',
    },
    {
      path: '/market',
      name: 'market',
      component: () => import('./pages/MarketPage.vue'),
    },
    {
      path: '/debug-studio',
      name: 'debug-studio',
      component: () => import('./pages/DebugStudioPage.vue'),
    },
    {
      path: '/code-editor',
      name: 'code-editor',
      component: () => import('./pages/CodePage.vue'),
    },
    {
      path: '/workbench',
      name: 'workbench',
      redirect: '/space',
    },
    {
      path: '/recovery/:actionId',
      name: 'recovery-check',
      component: () => import('./pages/RecoveryCheckPage.vue'),
    },
    {
      path: '/governance',
      name: 'governance-board',
      component: () => import('./pages/GovernanceBoardPage.vue'),
    },
    {
      path: '/memory',
      name: 'memory',
      component: () => import('./pages/MemoryPage.vue'),
    },
    {
      path: '/snapshots',
      name: 'snapshots',
      component: () => import('./pages/SnapshotsPage.vue'),
    },
    {
      path: '/library',
      name: 'library',
      component: () => import('./pages/LibraryPage.vue'),
    },
    {
      path: '/panel',
      name: 'detached-panel',
      component: () => import('./pages/DetachedPanelPage.vue'),
    },
    {
      path: '/pet',
      name: 'desktop-pet',
      component: () => import('./pages/DesktopPetPage.vue'),
    },
    {
      path: '/companion',
      name: 'companion',
      component: () => import('./pages/CompanionPage.vue'),
    },
    {
      path: '/companion-center',
      name: 'companion-center',
      component: () => import('./pages/CompanionCenterPage.vue'),
    },
  ],
})

router.beforeEach(async (to) => {
  if (to.name === 'debug-studio' && !(await loadDebugStudioPreference())) return { name: 'home', replace: true }
})
watch(useDebugStudio().enabled, (enabled) => {
  if (!enabled && router.currentRoute.value.name === 'debug-studio') void router.replace({ name: 'home' })
})

export default router
