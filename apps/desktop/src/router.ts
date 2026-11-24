import { defineComponent } from 'vue'
import { createRouter, createWebHashHistory, type RouteRecordRaw } from 'vue-router'

export const HomeSurfaceRouteSlot = defineComponent({
  name: 'HomeSurfaceRouteSlot',
  render: () => null,
})

export const appRoutes: RouteRecordRaw[] = [
  // Primary user-facing surfaces: persistent Home shell (Chat / Board / Project Hub), Space, Companion Center & Settings
  {
    path: '/',
    component: () => import('./pages/HomePage.vue'),
    children: [
      {
        path: '',
        name: 'home',
        meta: { homeSurface: 'chat' },
        component: HomeSurfaceRouteSlot,
      },
      {
        path: 'board',
        name: 'board',
        meta: { homeSurface: 'board' },
        component: HomeSurfaceRouteSlot,
      },
      {
        path: 'projects/:projectId',
        name: 'project-hub',
        meta: { homeSurface: 'project-hub' },
        component: HomeSurfaceRouteSlot,
      },
    ],
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
]

const router = createRouter({
  history: createWebHashHistory(),
  routes: appRoutes,
})

export default router
