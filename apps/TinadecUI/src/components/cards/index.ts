import type { Component } from 'vue'
import SpatialWorkCard from '@/components/spatial/SpatialWorkCard.vue'
import { createCardRegistry, type CardRegistry } from '../../engine/registry'
import NavCard from './home/NavCard.vue'
import ChatCard from './home/ChatCard.vue'
import HomePickerCard from './home/HomePickerCard.vue'
import GitCard from './home/GitCard.vue'
import ApprovalCard from './home/ApprovalCard.vue'
import OrchestrationCard from './home/OrchestrationCard.vue'
import OrganizationCard from './home/OrganizationCard.vue'
import EventsCard from './home/EventsCard.vue'
import DoctorCard from './home/DoctorCard.vue'
import BrowserCard from './home/BrowserCard.vue'
import AgentCard from './home/AgentCard.vue'
import TerminalCard from './home/TerminalCard.vue'
import MarketFilterCard from './market/MarketFilterCard.vue'
import MarketCatalogCard from './market/MarketCatalogCard.vue'
import MarketDetailCard from './market/MarketDetailCard.vue'
import type { UieCardDescriptor } from '../../engine/types'

/**
 * Register every card descriptor the Uie knows. Each descriptor binds a
 * stable `type` key to a Vue component + layout constraints + flags. The
 * default title is empty: tabs name a card by its type (uie.cardTitle.*) in
 * the app's language unless the card was opened with a title of its own.
 */
export function buildUieRegistry(): CardRegistry {
  const reg = createCardRegistry()

  const cards: UieCardDescriptor[] = [
    { type: 'spatialWork', component: SpatialWorkCard, minWidth: 260, minHeight: 220, singleton: false, movable: true, closable: false, detachable: false, defaultTitle: '' },
    { type: 'nav', component: NavCard, minWidth: 220, minHeight: 120, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '' },
    { type: 'chat', component: ChatCard, minWidth: 320, minHeight: 200, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '' },
    { type: 'homePicker', component: HomePickerCard, minWidth: 240, minHeight: 160, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '' },
    { type: 'git', component: GitCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    { type: 'approval', component: ApprovalCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    { type: 'orchestration', component: OrchestrationCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    { type: 'organization', component: OrganizationCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    { type: 'events', component: EventsCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    { type: 'doctor', component: DoctorCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    { type: 'browser', component: BrowserCard, minWidth: 260, minHeight: 160, singleton: false, movable: true, closable: true, detachable: true, defaultTitle: '' },
    { type: 'agent', component: AgentCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    // The panel itself is singleton. It already manages multiple terminal tabs internally;
    // mounting two TerminalCards would attach two xterm views to the same terminal IDs and
    // let one view dispose the other's renderer.
    { type: 'terminal', component: TerminalCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    { type: 'marketFilter', component: MarketFilterCard, minWidth: 240, minHeight: 160, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '' },
    { type: 'marketCatalog', component: MarketCatalogCard, minWidth: 280, minHeight: 160, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '' },
    { type: 'marketDetail', component: MarketDetailCard, minWidth: 280, minHeight: 160, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '' },
  ]

  for (const c of cards) reg.register(c)
  return reg
}

/** Lookup: descriptorId -> Vue component (for the instance pool). */
export function createComponentLookup(registry: CardRegistry): (descriptorId: string) => Component | undefined {
  return (descriptorId: string) => registry.get(descriptorId)?.component
}
