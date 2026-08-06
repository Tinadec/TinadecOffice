import type { Component } from 'vue'
import { createCardRegistry, type CardRegistry } from '../registry'
import NavCard from './home/NavCard.vue'
import ChatCard from './home/ChatCard.vue'
import HomePickerCard from './home/HomePickerCard.vue'
import GitCard from './home/GitCard.vue'
import ApprovalCard from './home/ApprovalCard.vue'
import OrchestrationCard from './home/OrchestrationCard.vue'
import EventsCard from './home/EventsCard.vue'
import DoctorCard from './home/DoctorCard.vue'
import BrowserCard from './home/BrowserCard.vue'
import AgentCard from './home/AgentCard.vue'
import TerminalCard from './home/TerminalCard.vue'
import type { WorkbenchCardDescriptor } from '../types'

/**
 * Register every card descriptor the Workbench knows. Each descriptor binds a
 * stable `type` key to a Vue component + layout constraints + flags.
 */
export function buildWorkbenchRegistry(): CardRegistry {
  const reg = createCardRegistry()

  const cards: WorkbenchCardDescriptor[] = [
    { type: 'nav', component: NavCard, minWidth: 220, minHeight: 120, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '项目' },
    { type: 'chat', component: ChatCard, minWidth: 320, minHeight: 200, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '聊天' },
    { type: 'homePicker', component: HomePickerCard, minWidth: 240, minHeight: 160, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: 'Home' },
    { type: 'git', component: GitCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: 'Git' },
    { type: 'approval', component: ApprovalCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '审批' },
    { type: 'orchestration', component: OrchestrationCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '编排' },
    { type: 'events', component: EventsCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '事件' },
    { type: 'doctor', component: DoctorCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: 'Doctor' },
    { type: 'browser', component: BrowserCard, minWidth: 260, minHeight: 160, singleton: false, movable: true, closable: true, detachable: true, defaultTitle: '浏览器' },
    { type: 'agent', component: AgentCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: 'Agent' },
    { type: 'terminal', component: TerminalCard, minWidth: 260, minHeight: 160, singleton: false, movable: true, closable: true, detachable: true, defaultTitle: '终端' },
  ]

  for (const c of cards) reg.register(c)
  return reg
}

/** Lookup: descriptorId -> Vue component (for the instance pool). */
export function createComponentLookup(registry: CardRegistry): (descriptorId: string) => Component | undefined {
  return (descriptorId: string) => registry.get(descriptorId)?.component
}
