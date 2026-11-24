import { createRouter, createWebHashHistory } from 'vue-router'

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    // Primary user-facing surfaces: Home (Flat / Space / Companion Center) & Settings
    {
      path: '/',
      name: 'home',
      component: () => import('./pages/HomePage.vue'),
    },
    {
      path: '/board',
      name: 'board',
      component: () => import('./pages/GlobalBoardPage.vue'),
    },
    {
      path: '/projects/:projectId',
      name: 'project-hub',
      component: () => import('./pages/ProjectHubPage.vue'),
    },
    {
      path: '/space',
      name: 'space',
      component: () => import('./pages/SpatialPage.vue'),
    },
    {
      path: '/companion-center',
      name: 'companion-center',
      component: () => import('./pages/CompanionCenterPage.vue'),
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('./pages/SettingsPage.vue'),
    },

    // Electron child-window-only routes
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

    // Legacy deep-link redirects -> unified Home / Settings
    { path: '/agent-center', redirect: '/settings' },
    { path: '/workbench', redirect: '/space' },
    { path: '/code-editor', redirect: '/' },
    { path: '/snapshots', redirect: '/' },
    { path: '/governance', redirect: '/' },
    { path: '/recovery/:actionId', redirect: '/' },
    { path: '/library', redirect: '/' },
    { path: '/memory', redirect: '/' },
    { path: '/market', redirect: '/' },
    { path: '/market/skills', redirect: '/' },
    { path: '/market/mcp', redirect: '/' },
    { path: '/buddy', redirect: '/companion-center' },
    { path: '/debug-studio', redirect: '/' },
  ],
})

export default router
