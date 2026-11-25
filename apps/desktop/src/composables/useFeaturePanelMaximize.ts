import { effectScope, ref, watch, type EffectScope, type Ref } from 'vue'
import { useUie, type UieStore } from '@tinadec/ui'
import { homeController } from '@/controllers/HomeController'

export type PipDockSide = 'left' | 'center' | 'right'

export interface PipSize {
  width: number
  heightPct: number
}

export const FLOATING_CHAT_PIP_STORAGE_KEY = 'tinadec:floating-chat-pip'
export const NEW_CONVERSATION_EVENT = 'tinadec:new-conversation'

export const DEFAULT_DOCK_SIDE: PipDockSide = 'right'
export const DEFAULT_PIP_SIZE: Readonly<PipSize> = Object.freeze({
  width: 420,
  heightPct: 62,
})

export const MIN_PIP_WIDTH = 340
export const MAX_PIP_WIDTH = 580
export const MIN_PIP_HEIGHT_PCT = 25
export const MAX_PIP_HEIGHT_PCT = 78

interface PersistedFloatingChatPipPrefs {
  dockSide?: unknown
  pipSize?: {
    width?: unknown
    heightPct?: unknown
  } | null
}

function isValidDockSide(value: unknown): value is PipDockSide {
  return value === 'left' || value === 'center' || value === 'right'
}

function clampPipWidth(width: unknown): number {
  const num = typeof width === 'number' ? width : Number(width)
  if (!Number.isFinite(num)) return DEFAULT_PIP_SIZE.width
  return Math.round(Math.min(MAX_PIP_WIDTH, Math.max(MIN_PIP_WIDTH, num)))
}

function clampPipHeightPct(heightPct: unknown): number {
  const num = typeof heightPct === 'number' ? heightPct : Number(heightPct)
  if (!Number.isFinite(num)) return DEFAULT_PIP_SIZE.heightPct
  return Math.round(Math.min(MAX_PIP_HEIGHT_PCT, Math.max(MIN_PIP_HEIGHT_PCT, num)))
}

function readPersistedPrefs(): { dockSide: PipDockSide; pipSize: PipSize } {
  const fallback = {
    dockSide: DEFAULT_DOCK_SIDE,
    pipSize: { width: DEFAULT_PIP_SIZE.width, heightPct: DEFAULT_PIP_SIZE.heightPct },
  }
  if (typeof localStorage === 'undefined') return fallback
  try {
    const raw = localStorage.getItem(FLOATING_CHAT_PIP_STORAGE_KEY)
    if (!raw) return fallback
    const parsed = JSON.parse(raw) as PersistedFloatingChatPipPrefs | null
    if (!parsed || typeof parsed !== 'object') return fallback
    return {
      dockSide: isValidDockSide(parsed.dockSide) ? parsed.dockSide : DEFAULT_DOCK_SIDE,
      pipSize: {
        width: clampPipWidth(parsed.pipSize?.width),
        heightPct: clampPipHeightPct(parsed.pipSize?.heightPct),
      },
    }
  } catch {
    return fallback
  }
}

function writePersistedPrefs(nextDockSide: PipDockSide, nextPipSize: PipSize): void {
  if (typeof localStorage === 'undefined') return
  try {
    localStorage.setItem(
      FLOATING_CHAT_PIP_STORAGE_KEY,
      JSON.stringify({
        dockSide: nextDockSide,
        pipSize: {
          width: nextPipSize.width,
          heightPct: nextPipSize.heightPct,
        },
      }),
    )
  } catch {
    // Optional local preference; ignore quota or private-mode failures.
  }
}

function tryGetUie(): UieStore | null {
  try {
    return useUie() ?? null
  } catch {
    return null
  }
}

const initialPrefs = readPersistedPrefs()

// Runtime-only maximize state — NEVER mutates persisted UieLayoutSnapshot.
const maximized = ref(false)
const pipOpen = ref(false)
const dockSide = ref<PipDockSide>(initialPrefs.dockSide)
const pipSize = ref<PipSize>(initialPrefs.pipSize)

let coreScope: EffectScope | null = null
let uieScope: EffectScope | null = null
let boundUie: UieStore | null = null
let windowListenersAttached = false

export function enterMaximize(): void {
  ensureLifecycleBound()
  // Every maximize entry MUST start cleanly as the 48px circular orb.
  pipOpen.value = false
  const uie = tryGetUie()
  if (uie) {
    const rightCollapsed = Boolean(uie.snapshot.value.columns.right?.collapsed)
    if (uie.pageId.value !== 'home' || rightCollapsed || Boolean(uie.snapshot.value.space)) {
      return
    }
  }
  maximized.value = true
}

export function exitMaximize(): void {
  maximized.value = false
}

export function toggleMaximize(): void {
  ensureLifecycleBound()
  if (maximized.value) {
    exitMaximize()
  } else {
    enterMaximize()
  }
}

export function openPip(): void {
  pipOpen.value = true
}

export function minimizePip(): void {
  pipOpen.value = false
}

export function setDockSide(side: PipDockSide): void {
  if (!isValidDockSide(side)) return
  dockSide.value = side
  writePersistedPrefs(dockSide.value, pipSize.value)
}

export function setPipSize(patch: Partial<PipSize>): void {
  const next: PipSize = {
    width: patch.width !== undefined ? clampPipWidth(patch.width) : pipSize.value.width,
    heightPct: patch.heightPct !== undefined ? clampPipHeightPct(patch.heightPct) : pipSize.value.heightPct,
  }
  pipSize.value = next
  writePersistedPrefs(dockSide.value, pipSize.value)
}

export function notifyNewConversation(): void {
  exitMaximize()
}

function onWindowNewConversation(): void {
  exitMaximize()
}

function isModalDialogOpen(): boolean {
  if (typeof document === 'undefined') return false
  return Boolean(document.querySelector('[role="dialog"][aria-modal="true"]'))
}

function onWindowKeyDown(event: KeyboardEvent): void {
  if (event.defaultPrevented || event.repeat || event.isComposing) return
  if (!(event.ctrlKey || event.metaKey) || !event.shiftKey || event.altKey) return
  if (event.key.toLowerCase() !== 'm' && event.code !== 'KeyM') return
  if (isModalDialogOpen()) return

  ensureUieWatcherBound()
  const uie = tryGetUie()
  if (uie) {
    if (uie.pageId.value !== 'home' || Boolean(uie.snapshot.value.space)) return
    if (!maximized.value && Boolean(uie.snapshot.value.columns.right?.collapsed)) return
  }

  event.preventDefault()
  toggleMaximize()
}

function ensureUieWatcherBound(): void {
  const currentUie = tryGetUie()
  if (currentUie === boundUie) return

  uieScope?.stop()
  uieScope = null
  boundUie = currentUie

  if (!currentUie) return
  const scope = effectScope(true)
  scope.run(() => {
    watch(
      () =>
        [
          currentUie.pageId.value,
          Boolean(currentUie.snapshot.value.columns.right?.collapsed),
          Boolean(currentUie.snapshot.value.space),
        ] as const,
      ([pageId, rightCollapsed, inSpace]) => {
        if (!maximized.value) return
        if (pageId !== 'home' || rightCollapsed || inSpace) {
          exitMaximize()
        }
      },
      { flush: 'sync' },
    )
  })
  uieScope = scope
}

function ensureLifecycleBound(): void {
  if (!coreScope) {
    const scope = effectScope(true)
    scope.run(() => {
      watch(
        () => homeController.selectedSessionId.value,
        (nextId, prevId) => {
          if (maximized.value && nextId !== prevId) {
            exitMaximize()
          }
        },
        { flush: 'sync' },
      )
    })
    coreScope = scope
  }

  if (!windowListenersAttached && typeof window !== 'undefined') {
    window.addEventListener(NEW_CONVERSATION_EVENT, onWindowNewConversation)
    window.addEventListener('keydown', onWindowKeyDown)
    windowListenersAttached = true
  }

  ensureUieWatcherBound()
}

export function __resetFeaturePanelMaximizeForTests(): void {
  coreScope?.stop()
  coreScope = null
  uieScope?.stop()
  uieScope = null
  boundUie = null

  if (windowListenersAttached && typeof window !== 'undefined') {
    window.removeEventListener(NEW_CONVERSATION_EVENT, onWindowNewConversation)
    window.removeEventListener('keydown', onWindowKeyDown)
    windowListenersAttached = false
  }

  maximized.value = false
  pipOpen.value = false
  const hydrated = readPersistedPrefs()
  dockSide.value = hydrated.dockSide
  pipSize.value = hydrated.pipSize
}

export interface FeaturePanelMaximizeStore {
  maximized: Ref<boolean>
  pipOpen: Ref<boolean>
  dockSide: Ref<PipDockSide>
  pipSize: Ref<PipSize>
  enterMaximize: () => void
  exitMaximize: () => void
  toggleMaximize: () => void
  openPip: () => void
  minimizePip: () => void
  setDockSide: (side: PipDockSide) => void
  setPipSize: (patch: Partial<PipSize>) => void
  notifyNewConversation: () => void
}

export function useFeaturePanelMaximize(): FeaturePanelMaximizeStore {
  ensureLifecycleBound()
  return {
    maximized,
    pipOpen,
    dockSide,
    pipSize,
    enterMaximize,
    exitMaximize,
    toggleMaximize,
    openPip,
    minimizePip,
    setDockSide,
    setPipSize,
    notifyNewConversation,
  }
}
