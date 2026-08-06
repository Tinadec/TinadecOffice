import { defineAsyncComponent, defineComponent, h, type Component } from 'vue'
import {
  Activity,
  Bug,
  Code2,
  FileSearch,
  FileText,
  FolderTree,
  Gauge,
  GitCompare,
  Globe,
  ListFilter,
  MessageSquare,
  PanelLeft,
  Settings,
  ShieldCheck,
  SlidersHorizontal,
  Store,
  Wrench,
} from '@lucide/vue'
import type { WorkbenchCardDescriptor } from './types'

const EmptyCard = defineComponent({
  name: 'EmptyWorkbenchCard',
  setup: () => () => h('div'),
})

type DescriptorOptions = Partial<Omit<WorkbenchCardDescriptor, 'type' | 'title' | 'icon' | 'component'>>

function descriptor(
  type: string,
  title: string,
  icon: Component,
  component: Component = EmptyCard,
  options: DescriptorOptions = {},
): WorkbenchCardDescriptor {
  return {
    type,
    title,
    icon,
    component,
    pages: options.pages,
    minWidth: options.minWidth ?? 220,
    minHeight: options.minHeight ?? 180,
    singleton: options.singleton ?? true,
    movable: options.movable ?? true,
    closable: options.closable ?? true,
    detachable: options.detachable ?? false,
    serializeState: options.serializeState,
  }
}

const descriptors = [
  descriptor(
    'home.navigation',
    'Navigation',
    PanelLeft,
    defineAsyncComponent(() => import('./cards/HomeNavigationCard.vue')),
    { minWidth: 220 },
  ),
  descriptor(
    'home.chat',
    'Conversation',
    MessageSquare,
    defineAsyncComponent(() => import('./cards/HomeChatCard.vue')),
    { minWidth: 420, minHeight: 320 },
  ),
  descriptor(
    'home.tools',
    'Tools',
    Wrench,
    defineAsyncComponent(() => import('./cards/HomeToolsCard.vue')),
    { minWidth: 280, minHeight: 260 },
  ),
  descriptor('home.git', 'Git', GitCompare, defineAsyncComponent(() => import('./cards/HomeGitCard.vue')), {
    pages: ['home'], minWidth: 320, minHeight: 240, detachable: true,
  }),
  descriptor('home.approvals', 'Approvals', ShieldCheck, defineAsyncComponent(() => import('./cards/HomeApprovalCard.vue')), {
    pages: ['home'], minWidth: 300, minHeight: 240, detachable: true,
  }),
  descriptor('home.orchestration', 'Orchestration', Activity, defineAsyncComponent(() => import('./cards/HomeOrchestrationCard.vue')), {
    pages: ['home'], minWidth: 320, minHeight: 240, detachable: true,
  }),
  descriptor('home.events', 'Events', Activity, defineAsyncComponent(() => import('./cards/HomeEventsCard.vue')), {
    pages: ['home'], minWidth: 300, minHeight: 240, detachable: true,
  }),
  descriptor('home.doctor', 'Doctor', SlidersHorizontal, defineAsyncComponent(() => import('./cards/HomeDoctorCard.vue')), {
    pages: ['home'], minWidth: 300, minHeight: 220, detachable: true,
  }),
  descriptor('home.agent', 'Agent activity', Bug, defineAsyncComponent(() => import('./cards/HomeAgentCard.vue')), {
    pages: ['home'], minWidth: 320, minHeight: 240, detachable: true,
  }),
  descriptor('home.terminal', 'Terminal', Wrench, defineAsyncComponent(() => import('./cards/HomeTerminalCard.vue')), {
    pages: ['home'], minWidth: 360, minHeight: 220, detachable: true,
  }),
  descriptor('settings.navigation', 'Settings', Settings, defineAsyncComponent(() => import('./cards/SettingsNavigationCard.vue')), {
    pages: ['settings'],
    minWidth: 240,
    movable: false,
    closable: false,
  }),
  descriptor('settings.content', 'Settings content', SlidersHorizontal, defineAsyncComponent(() => import('./cards/SettingsContentCard.vue')), {
    pages: ['settings'],
    minWidth: 560,
    movable: false,
    closable: false,
  }),
  descriptor('market.filters', 'Market filters', ListFilter, defineAsyncComponent(() => import('./cards/MarketFiltersCard.vue')), { pages: ['market'], minWidth: 280 }),
  descriptor('market.catalog', 'Extension catalog', Store, defineAsyncComponent(() => import('./cards/MarketCatalogCard.vue')), { pages: ['market'], minWidth: 420 }),
  descriptor('market.details', 'Extension details', FileText, defineAsyncComponent(() => import('./cards/MarketDetailsCard.vue')), { pages: ['market'], minWidth: 320 }),
  descriptor('code.explorer', 'Explorer and search', FolderTree, defineAsyncComponent(() => import('./cards/CodeExplorerCard.vue')), { pages: ['code'], minWidth: 260 }),
  descriptor('code.editor', 'Code editor', Code2, defineAsyncComponent(() => import('./cards/CodeEditorCard.vue')), { pages: ['code'], minWidth: 520, minHeight: 320 }),
  descriptor('code.patch', 'Patch preview', GitCompare, defineAsyncComponent(() => import('./cards/CodePatchCard.vue')), { pages: ['code'], minWidth: 320 }),
  descriptor('debug.timeline', 'Trace timeline', Activity, defineAsyncComponent(() => import('./cards/DebugTimelineCard.vue')), { pages: ['debug'], minWidth: 320 }),
  descriptor('debug.main', 'Agent graph', Bug, defineAsyncComponent(() => import('./cards/DebugGraphCard.vue')), { pages: ['debug'], minWidth: 480 }),
  descriptor('debug.inspector', 'Inspector', FileSearch, defineAsyncComponent(() => import('./cards/DebugInspectorCard.vue')), { pages: ['debug'], minWidth: 280 }),
  descriptor('debug.metrics', 'Metrics', Gauge, defineAsyncComponent(() => import('./cards/DebugMetricsCard.vue')), { pages: ['debug'], minWidth: 320 }),
  descriptor('debug.diagnostics', 'Diagnostics', Activity, defineAsyncComponent(() => import('./cards/DebugDiagnosticsCard.vue')), { pages: ['debug'], minWidth: 320 }),
  descriptor('debug.preview', 'Preview gallery', Globe, defineAsyncComponent(() => import('./cards/DebugPreviewCard.vue')), { pages: ['debug'], minWidth: 360 }),
  descriptor('debug.simulator', 'Simulator', SlidersHorizontal, defineAsyncComponent(() => import('./cards/DebugSimulatorCard.vue')), { pages: ['debug'], minHeight: 160 }),
  descriptor('tool.browser', 'Browser', Globe, defineAsyncComponent(() => import('./cards/ToolBrowserCard.vue')), {
    minWidth: 360,
    minHeight: 240,
    singleton: false,
    detachable: true,
    pages: ['home'],
  }),
] as const

export const workbenchCardRegistry: ReadonlyMap<string, WorkbenchCardDescriptor> = new Map(
  descriptors.map((entry) => [entry.type, entry]),
)

export function getWorkbenchCardDescriptor(type: string): WorkbenchCardDescriptor | undefined {
  return workbenchCardRegistry.get(type)
}

export function listWorkbenchCardDescriptors(): readonly WorkbenchCardDescriptor[] {
  return [...workbenchCardRegistry.values()]
}
