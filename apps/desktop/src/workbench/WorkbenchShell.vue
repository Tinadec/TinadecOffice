<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import WorkbenchPresentation from './WorkbenchPresentation.vue'
import WorkbenchVaporRuntime from './WorkbenchVaporRuntime.vue'
import { listWorkbenchCardDescriptors, workbenchCardRegistry } from './registry'
import { createWorkbenchLayoutController } from './useWorkbenchLayout'
import { provideWorkbenchHost } from './host'
import type {
  PersistedCardInstance,
  WorkbenchLayoutCommand,
  WorkbenchPageId,
  WorkbenchSlotId,
  WorkbenchStackId,
} from './types'

interface DetachedWorkbenchCardPayload {
  version: 1
  card: PersistedCardInstance
  pageId: WorkbenchPageId
  title: string
  returnPlacement?: { slotId: WorkbenchSlotId; stackId: WorkbenchStackId; index: number }
}

interface ReattachData {
  tabId: string
  type: string
  title: string
  state: Record<string, unknown>
  workbench?: DetachedWorkbenchCardPayload
}

defineProps<{
  transitionName: string
}>()

const route = useRoute()
const layout = createWorkbenchLayoutController(workbenchCardRegistry)
const instanceCounter = ref(0)

function routePage(name: unknown): WorkbenchPageId {
  if (name === 'settings') return 'settings'
  if (name === 'market') return 'market'
  if (name === 'code-editor') return 'code'
  if (name === 'debug-studio') return 'debug'
  return 'home'
}

const activePage = computed(() => routePage(route.name))

function dispatchForPage(pageId: WorkbenchPageId, command: WorkbenchLayoutCommand): boolean {
  const routedPage = activePage.value
  layout.setActivePage(pageId)
  try {
    return layout.dispatch(command)
  } finally {
    layout.setActivePage(routedPage)
  }
}

function placementFor(pageId: WorkbenchPageId, cardId: string) {
  const snapshot = layout.layouts.value[pageId]
  for (const slotId of snapshot.columnOrder) {
    const column = snapshot.columns[slotId]
    for (const stackId of ['primary', 'secondary'] as const) {
      const stack = stackId === 'primary' ? column.primary : column.secondary
      const index = stack?.cardIds.indexOf(cardId) ?? -1
      if (index >= 0) return { slotId, stackId, index }
    }
  }
  return null
}

function openCard(type: string, options: {
  pageId?: WorkbenchPageId
  slotId?: WorkbenchSlotId
  stackId?: WorkbenchStackId
  index?: number
  state?: Record<string, unknown>
  instanceId?: string
} = {}): string | null {
  const pageId = options.pageId ?? activePage.value
  const descriptor = workbenchCardRegistry.get(type)
  if (!descriptor || (descriptor.pages && !descriptor.pages.includes(pageId))) return null

  const snapshot = layout.layouts.value[pageId]
  const existing = descriptor.singleton
    ? Object.values(snapshot.cards).find((card) => card.type === type)
    : undefined
  if (existing) {
    dispatchForPage(pageId, { kind: 'activateCard', cardId: existing.id })
    if (options.slotId) {
      dispatchForPage(pageId, {
        kind: 'moveCard',
        cardId: existing.id,
        slotId: options.slotId,
        stackId: options.stackId ?? 'primary',
        index: options.index,
      })
    }
    return existing.id
  }

  const id = options.instanceId ?? (descriptor.singleton
    ? `${pageId}:${type}`
    : `${pageId}:${type}:${Date.now()}:${++instanceCounter.value}`)
  const opened = dispatchForPage(pageId, {
    kind: 'openCard',
    card: { id, type, state: structuredClone(options.state ?? {}) },
    slotId: options.slotId ?? 'right',
    stackId: options.stackId ?? 'primary',
    index: options.index,
  })
  return opened ? id : null
}

function closeCard(cardId: string): boolean {
  for (const pageId of ['home', 'settings', 'market', 'code', 'debug'] as const) {
    if (layout.layouts.value[pageId].cards[cardId]) {
      return dispatchForPage(pageId, { kind: 'closeCard', cardId })
    }
  }
  return false
}

function moveCard(
  cardId: string,
  slotId: WorkbenchSlotId,
  stackId: WorkbenchStackId = 'primary',
): boolean {
  for (const pageId of ['home', 'settings', 'market', 'code', 'debug'] as const) {
    if (layout.layouts.value[pageId].cards[cardId]) {
      return dispatchForPage(pageId, { kind: 'moveCard', cardId, slotId, stackId })
    }
  }
  return false
}

async function detachCard(card: PersistedCardInstance, pageId: WorkbenchPageId): Promise<boolean> {
  const descriptor = workbenchCardRegistry.get(card.type)
  const placement = placementFor(pageId, card.id)
  if (!descriptor?.detachable || !placement) return false

  const state = descriptor.serializeState
    ? descriptor.serializeState(card.state)
    : structuredClone(card.state)
  const payload = {
    version: 1 as const,
    card: { ...card, state },
    pageId,
    title: descriptor.title,
    returnPlacement: placement,
  }
  const result = window.tinadec.detachWorkbenchCard
    ? await window.tinadec.detachWorkbenchCard(payload)
    : await window.tinadec.detachPanel(card.id, card.type, descriptor.title, { ...state, __workbench: payload })
  if (!result) return false
  return dispatchForPage(pageId, { kind: 'closeCard', cardId: card.id })
}

provideWorkbenchHost({
  activePage,
  openCard,
  closeCard,
  moveCard,
  detachCard,
  setActiveProject: layout.setActiveProject,
  listDescriptors: (pageId = activePage.value) => listWorkbenchCardDescriptors().filter(
    (descriptor) => !descriptor.pages || descriptor.pages.includes(pageId),
  ),
  findDefaultStack: () => null,
})

function restoreDetachedCard(data: ReattachData): void {
  const workbench = data.workbench ?? (data.state?.__workbench as DetachedWorkbenchCardPayload | undefined)
  if (workbench?.card && workbench.pageId) {
    openCard(workbench.card.type, {
      pageId: workbench.pageId,
      slotId: workbench.returnPlacement?.slotId ?? 'right',
      stackId: workbench.returnPlacement?.stackId ?? 'primary',
      index: workbench.returnPlacement?.index,
      state: workbench.card.state,
      instanceId: workbench.card.id,
    })
    return
  }

  const legacyTypes: Record<string, string> = {
    git: 'home.git',
    approval: 'home.approvals',
    orchestration: 'home.orchestration',
    events: 'home.events',
    doctor: 'home.doctor',
    preview: 'tool.browser',
    agent: 'home.agent',
    terminal: 'home.terminal',
  }
  const type = legacyTypes[data.type]
  if (type) openCard(type, { pageId: 'home', state: data.state, instanceId: data.tabId })
}

let removeReattachListener: (() => void) | undefined

watch(activePage, (pageId) => layout.setActivePage(pageId), { immediate: true })

onMounted(async () => {
  await layout.initialize()
  layout.setActivePage(activePage.value)
  removeReattachListener = window.tinadec?.onPanelReattach?.(restoreDetachedCard) ?? undefined
})

onBeforeUnmount(() => {
  removeReattachListener?.()
  void layout.flush()
})
</script>

<template>
  <WorkbenchVaporRuntime />
  <WorkbenchPresentation :transition-name="transitionName" />
</template>
