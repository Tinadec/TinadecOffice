import type {
  PersistedCardInstance,
  WorkbenchColumn,
  WorkbenchLayoutSnapshot,
  WorkbenchPageId,
  WorkbenchSlotId,
  WorkbenchStack,
} from './types'

export const DEFAULT_LEFT_WIDTH = 260
export const DEFAULT_RIGHT_WIDTH = 420
export const DEFAULT_CENTER_WIDTH = 640
export const DEFAULT_SPLIT_RATIO = 0.65

type PresetCard = Readonly<{
  id: string
  type: string
  slotId: WorkbenchSlotId
  stackId?: 'primary' | 'secondary'
  state?: Record<string, unknown>
}>

const PRESET_CARDS: Readonly<Record<WorkbenchPageId, readonly PresetCard[]>> = {
  home: [
    { id: 'home:navigation', type: 'home.navigation', slotId: 'left' },
    { id: 'home:chat', type: 'home.chat', slotId: 'center' },
    { id: 'home:tools', type: 'home.tools', slotId: 'right' },
  ],
  settings: [
    { id: 'settings:navigation', type: 'settings.navigation', slotId: 'left' },
    { id: 'settings:content', type: 'settings.content', slotId: 'center' },
  ],
  market: [
    { id: 'market:filters', type: 'market.filters', slotId: 'left' },
    { id: 'market:catalog', type: 'market.catalog', slotId: 'center' },
    { id: 'market:details', type: 'market.details', slotId: 'right' },
  ],
  code: [
    { id: 'code:explorer', type: 'code.explorer', slotId: 'left' },
    { id: 'code:editor', type: 'code.editor', slotId: 'center' },
    { id: 'code:patch', type: 'code.patch', slotId: 'right' },
  ],
  debug: [
    { id: 'debug:timeline', type: 'debug.timeline', slotId: 'left' },
    { id: 'debug:main', type: 'debug.main', slotId: 'center' },
    { id: 'debug:inspector', type: 'debug.inspector', slotId: 'right' },
    { id: 'debug:simulator', type: 'debug.simulator', slotId: 'center', stackId: 'secondary' },
  ],
}

function emptyStack(): WorkbenchStack {
  return { cardIds: [], activeCardId: null }
}

function createColumn(width: number): WorkbenchColumn {
  return {
    width,
    collapsed: false,
    primary: emptyStack(),
    secondary: null,
    splitRatio: DEFAULT_SPLIT_RATIO,
  }
}

function appendCard(
  snapshot: WorkbenchLayoutSnapshot,
  presetCard: PresetCard,
): void {
  const card: PersistedCardInstance = {
    id: presetCard.id,
    type: presetCard.type,
    state: { ...(presetCard.state ?? {}) },
  }
  snapshot.cards[card.id] = card

  const column = snapshot.columns[presetCard.slotId]
  const stackId = presetCard.stackId ?? 'primary'
  if (stackId === 'secondary' && !column.secondary) {
    column.secondary = emptyStack()
  }
  const stack = stackId === 'primary' ? column.primary : column.secondary!
  stack.cardIds.push(card.id)
  stack.activeCardId ??= card.id
}

export function createWorkbenchPreset(pageId: WorkbenchPageId): WorkbenchLayoutSnapshot {
  const snapshot: WorkbenchLayoutSnapshot = {
    version: 1,
    revision: 0,
    pageId,
    columnOrder: ['left', 'center', 'right'],
    columns: {
      left: createColumn(DEFAULT_LEFT_WIDTH),
      center: createColumn(DEFAULT_CENTER_WIDTH),
      right: createColumn(DEFAULT_RIGHT_WIDTH),
    },
    cards: {},
    focusedCardId: null,
  }

  for (const card of PRESET_CARDS[pageId]) appendCard(snapshot, card)
  snapshot.focusedCardId = snapshot.columns.center.primary.activeCardId

  if (pageId === 'settings') {
    snapshot.columns.right.collapsed = true
    snapshot.columns.right.width = 0
  }

  return snapshot
}

export function isSettingsPreset(snapshot: WorkbenchLayoutSnapshot): boolean {
  return snapshot.pageId === 'settings'
}
