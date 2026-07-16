import type { RouteRecordRaw } from 'vue-router'

export const settingsRouteChildren: RouteRecordRaw[] = [
  { path: 'model', name: 'settings-model', component: () => import('./ModelSettingsPage.vue') },
  { path: 'agents', name: 'settings-agents', component: () => import('./AgentsSettingsPage.vue') },
  { path: 'agent-evolution', name: 'settings-agent-evolution', component: () => import('./AgentEvolutionSettingsPage.vue') },
  { path: 'prompt-context', name: 'settings-prompt-context', component: () => import('./PromptContextSettingsPage.vue') },
  { path: 'prompt-engineering', name: 'settings-prompt-engineering', component: () => import('./PromptEngineeringSettingsPage.vue') },
  { path: 'tools', name: 'settings-tools', component: () => import('./ToolsSettingsPage.vue') },
  { path: 'appearance', name: 'settings-appearance', component: () => import('./AppearanceSettingsPage.vue') },
  { path: 'language', name: 'settings-language', component: () => import('./LanguageSettingsPage.vue') },
  { path: 'api-docs', name: 'settings-api-docs', component: () => import('./ApiDocsSettingsPage.vue') },
  { path: 'about', name: 'settings-about', component: () => import('./AboutSettingsPage.vue') },
]

export const settingsRoutes: RouteRecordRaw = {
  path: '/settings',
  component: () => import('./SettingsLayout.vue'),
  redirect: { name: 'settings-model' },
  children: settingsRouteChildren,
}
