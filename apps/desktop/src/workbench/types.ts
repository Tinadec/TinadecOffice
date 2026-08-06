import type { Component } from 'vue'

export const WORKBENCH_PAGE_IDS = ['home', 'settings', 'market', 'code', 'debug'] as const
export const WORKBENCH_SLOT_IDS = ['left', 'center', 'right'] as const
export const WORKBENCH_STACK_IDS = ['primary', 'secondary'] as const

export type WorkbenchPageId = (typeof WORKBENCH_PAGE_IDS)[number]
export type WorkbenchSlotId = (typeof WORKBENCH_SLOT_IDS)[number]
export type WorkbenchStackId = (typeof WORKBENCH_STACK_IDS)[number]
export type WorkbenchCardInstanceId = string

export type LayoutScope =
  | Readonly<{ kind: 'global' }>
  | Readonly<{ kind: 'page'; pageId: WorkbenchPageId }>
  | Readonly<{ kind: 'workspace-page'; projectId: string; pageId: WorkbenchPageId }>

export interface PersistedCardInstance {
  id: WorkbenchCardInstanceId
  type: string
  state: Record<string, unknown>
}

export interface WorkbenchStack {
  cardIds: WorkbenchCardInstanceId[]
  activeCardId: WorkbenchCardInstanceId | null
}

export interface WorkbenchColumn {
  width: number
  collapsed: boolean
  primary: WorkbenchStack
  secondary: WorkbenchStack | null
  splitRatio: number
}

export interface WorkbenchLayoutSnapshot {
  version: 1
  revision: number
  pageId: WorkbenchPageId
  columnOrder: WorkbenchSlotId[]
  columns: Record<WorkbenchSlotId, WorkbenchColumn>
  cards: Record<WorkbenchCardInstanceId, PersistedCardInstance>
  focusedCardId: WorkbenchCardInstanceId | null
}

export interface WorkbenchCardDescriptor {
  type: string
  title: string
  icon: Component
  component: Component
  /** Pages where the card can be opened. Omitted means every page. */
  pages?: readonly WorkbenchPageId[]
  minWidth: number
  minHeight: number
  singleton: boolean
  movable: boolean
  closable: boolean
  detachable: boolean
  serializeState?: (state: Record<string, unknown>) => Record<string, unknown>
}

export type WorkbenchCommandSource = 'user' | 'route' | 'restore' | 'ai'

export type WorkbenchLayoutCommand =
  | Readonly<{
      kind: 'openCard'
      card: PersistedCardInstance
      slotId: WorkbenchSlotId
      stackId: WorkbenchStackId
      index?: number
    }>
  | Readonly<{ kind: 'closeCard'; cardId: WorkbenchCardInstanceId }>
  | Readonly<{ kind: 'activateCard'; cardId: WorkbenchCardInstanceId }>
  | Readonly<{
      kind: 'moveCard'
      cardId: WorkbenchCardInstanceId
      slotId: WorkbenchSlotId
      stackId: WorkbenchStackId
      index?: number
    }>
  | Readonly<{
      kind: 'moveStack'
      fromSlotId: WorkbenchSlotId
      fromStackId: WorkbenchStackId
      toSlotId: WorkbenchSlotId
      toStackId: WorkbenchStackId
    }>
  | Readonly<{ kind: 'swapColumns'; first: WorkbenchSlotId; second: WorkbenchSlotId }>
  | Readonly<{
      kind: 'splitStack'
      slotId: WorkbenchSlotId
      cardId?: WorkbenchCardInstanceId
      ratio?: number
    }>
  | Readonly<{ kind: 'mergeStack'; slotId: WorkbenchSlotId }>
  | Readonly<{ kind: 'resizeColumn'; slotId: WorkbenchSlotId; width: number }>
  | Readonly<{ kind: 'resizeSplit'; slotId: WorkbenchSlotId; ratio: number }>
  | Readonly<{ kind: 'collapseColumn'; slotId: WorkbenchSlotId; collapsed: boolean }>
  | Readonly<{ kind: 'applyPreset'; snapshot: WorkbenchLayoutSnapshot }>
  | Readonly<{ kind: 'resetScope'; snapshot: WorkbenchLayoutSnapshot }>

export interface WorkbenchCommandEnvelope {
  source: WorkbenchCommandSource
  expectedRevision: number
  command: WorkbenchLayoutCommand
}

export interface WorkbenchReducerOptions {
  descriptors?: ReadonlyMap<string, WorkbenchCardDescriptor>
  locked?: boolean
}

export type WorkbenchReducerResult =
  | Readonly<{
      ok: true
      state: WorkbenchLayoutSnapshot
      inverse: WorkbenchLayoutCommand
    }>
  | Readonly<{ ok: false; state: WorkbenchLayoutSnapshot; error: string }>

export interface WorkbenchRect {
  x: number
  y: number
  width: number
  height: number
}

export interface ResolvedWorkbenchStack {
  slotId: WorkbenchSlotId
  stackId: WorkbenchStackId
  rect: WorkbenchRect
  cardIds: WorkbenchCardInstanceId[]
  activeCardId: WorkbenchCardInstanceId | null
}

export interface ResolvedWorkbenchColumn {
  slotId: WorkbenchSlotId
  rect: WorkbenchRect
  collapsed: boolean
  autoCollapsed: boolean
  mergedSecondary: boolean
  stacks: ResolvedWorkbenchStack[]
}

export interface ResolvedWorkbenchLayout {
  width: number
  height: number
  columns: ResolvedWorkbenchColumn[]
}

export interface WorkbenchLayoutDocument {
  version: 1
  revision: number
  globalDefault: WorkbenchLayoutSnapshot | null
  pages: Partial<Record<WorkbenchPageId, WorkbenchLayoutSnapshot>>
  workspacePages: Record<string, Partial<Record<WorkbenchPageId, WorkbenchLayoutSnapshot>>>
}

export interface WorkbenchLayoutStorage {
  load: () => Promise<unknown>
  save: (document: WorkbenchLayoutDocument) => Promise<WorkbenchLayoutDocument>
}
