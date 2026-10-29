import type { Component } from 'vue'
import CodeEditorCard from '@/components/code/CodeEditorCard.vue'
import DocumentReaderCard from '@/components/document/DocumentReaderCard.vue'
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
import WorkspaceCard from './home/WorkspaceCard.vue'
import MarketFilterCard from './market/MarketFilterCard.vue'
import MarketCatalogCard from './market/MarketCatalogCard.vue'
import MarketDetailCard from './market/MarketDetailCard.vue'
import CompanionListCard from './companion/CompanionListCard.vue'
import CompanionFeedCard from './companion/CompanionFeedCard.vue'
import CompanionGroupsCard from './companion/CompanionGroupsCard.vue'
import CompanionConnectionsCard from './companion/CompanionConnectionsCard.vue'
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
    // The workspace card edits the source-folder set the agent is authorized on. It is a
    // singleton because a workspace has exactly one root list; opening a second card would
    // offer two editors for the same project.toml and let one save over the other's edit.
    { type: 'workspace', component: WorkspaceCard, minWidth: 260, minHeight: 160, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    // Documents are two cards, not one: the viewer is read-only and gets its paths from
    // references, the editor owns the folder tree and the approval save. Both are singleton
    // — the viewer hosts every open document as an internal tab (the feature column's own
    // bar is icon-only at 420px, so one card per document would be a row of identical
    // fallback icons with no filename), and one editor per workspace is the point of
    // "open in editor".
    { type: 'docReader', component: DocumentReaderCard, minWidth: 320, minHeight: 220, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    { type: 'codeEditor', component: CodeEditorCard, minWidth: 320, minHeight: 220, singleton: true, movable: true, closable: true, detachable: true, defaultTitle: '' },
    { type: 'marketFilter', component: MarketFilterCard, minWidth: 240, minHeight: 160, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '' },
    { type: 'marketCatalog', component: MarketCatalogCard, minWidth: 280, minHeight: 160, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '' },
    { type: 'marketDetail', component: MarketDetailCard, minWidth: 280, minHeight: 160, singleton: true, movable: false, closable: false, detachable: false, defaultTitle: '' },
    // The companion center observes the companion domain. Each panel is one view of the same
    // read-only data; opening two of the same kind would duplicate the poll, not add a view.
    { type: 'companionList', component: CompanionListCard, minWidth: 240, minHeight: 180, singleton: true, movable: true, closable: true, detachable: false, defaultTitle: '' },
    { type: 'companionFeed', component: CompanionFeedCard, minWidth: 280, minHeight: 180, singleton: true, movable: true, closable: true, detachable: false, defaultTitle: '' },
    { type: 'companionGroups', component: CompanionGroupsCard, minWidth: 240, minHeight: 180, singleton: true, movable: true, closable: true, detachable: false, defaultTitle: '' },
    { type: 'companionConnections', component: CompanionConnectionsCard, minWidth: 240, minHeight: 180, singleton: true, movable: true, closable: true, detachable: false, defaultTitle: '' },
  ]

  for (const c of cards) reg.register(c)
  return reg
}

/** Lookup: descriptorId -> Vue component (for the instance pool). */
export function createComponentLookup(registry: CardRegistry): (descriptorId: string) => Component | undefined {
  return (descriptorId: string) => registry.get(descriptorId)?.component
}
