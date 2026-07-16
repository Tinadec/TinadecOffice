import { createRouter, createWebHashHistory } from 'vue-router'
import { settingsRouteChildren } from './pages/settings/settingsRoutes'

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
      component: () => import('./pages/settings/SettingsLayout.vue'),
      redirect: { name: 'settings-model' },
      children: settingsRouteChildren,
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
