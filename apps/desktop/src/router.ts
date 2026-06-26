import { createRouter, createWebHashHistory } from 'vue-router'

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    {
      path: '/',
      name: 'home',
      component: () => import('./pages/HomePage.vue'),
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('./pages/SettingsPage.vue'),
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
      path: '/panel',
      name: 'detached-panel',
      component: () => import('./pages/DetachedPanelPage.vue'),
    },
  ],
})

export default router
