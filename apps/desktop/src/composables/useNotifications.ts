import { computed, ref } from 'vue'

export type NotificationLevel = 'info' | 'success' | 'warning' | 'error'
export type NotificationKind = 'notification' | 'banner'

export interface NotificationAction {
  label: string
  run: () => void | Promise<void>
}

export interface NotificationOptions {
  key?: string
  title?: string
  message: string
  details?: string
  action?: NotificationAction
  duration?: number
  persistent?: boolean
  dismissible?: boolean
}

export interface ErrorNotificationOptions extends Omit<NotificationOptions, 'message'> {
  message: unknown
}

export interface NotificationItem extends NotificationOptions {
  id: string
  kind: NotificationKind
  level: NotificationLevel
  createdAt: number
}

export interface ConfirmationOptions {
  title?: string
  message: string
  details?: string
  confirmLabel?: string
  cancelLabel?: string
  destructive?: boolean
}

export interface ConfirmationRequest extends ConfirmationOptions {
  id: string
}

type MessageOptions = string | NotificationOptions
type ErrorInput = unknown | ErrorNotificationOptions
type Timer = {
  handle: ReturnType<typeof globalThis.setTimeout> | null
  remaining: number
  startedAt: number
  pauseReasons: Set<string>
}
type QueuedConfirmation = ConfirmationRequest & { resolve: (value: boolean) => void }

const items = ref<NotificationItem[]>([])
const primaryId = ref<string | null>(null)
const detailId = ref<string | null>(null)
const hoveredId = ref<string | null>(null)
const pinnedId = ref<string | null>(null)
const currentConfirmation = ref<ConfirmationRequest | null>(null)
const actionStates = ref<Record<string, { running: boolean; error: string | null }>>({})
const timers = new Map<string, Timer>()
const confirmations: QueuedConfirmation[] = []
let activeConfirmation: QueuedConfirmation | null = null
let nextId = 1

function id(prefix: 'notification' | 'confirmation'): string {
  return `${prefix}-${nextId++}`
}

function normalizeError(error: unknown, fallback = 'An unknown error occurred'): string {
  if (error instanceof Error && error.message) return error.message
  if (typeof error === 'string' && error.trim()) return error
  return fallback
}

function priority(item: NotificationItem): number {
  if (item.level === 'error') return 3
  if (item.persistent && (item.kind === 'banner' || item.level === 'warning')) return 2
  return 1
}

function rankItems(): NotificationItem[] {
  return [...items.value].sort((a, b) => priority(b) - priority(a) || b.createdAt - a.createdAt)
}

function currentVisibleItems(): NotificationItem[] {
  return rankItems().slice(0, 3)
}

function selectPrimary(): void {
  primaryId.value = rankItems()[0]?.id ?? null
}

function syncTimerVisibility(): void {
  const visibleIds = new Set(currentVisibleItems().map((item) => item.id))
  for (const timerId of timers.keys()) {
    if (
      detailId.value === timerId ||
      hoveredId.value === timerId ||
      pinnedId.value === timerId ||
      !visibleIds.has(timerId)
    ) {
      pause(timerId, 'visibility')
    } else {
      resume(timerId, 'visibility')
    }
  }
}

function dismiss(targetId: string): void {
  const timer = timers.get(targetId)
  if (timer?.handle) globalThis.clearTimeout(timer.handle)
  timers.delete(targetId)
  items.value = items.value.filter((item) => item.id !== targetId)
  if (detailId.value === targetId) detailId.value = null
  if (hoveredId.value === targetId) hoveredId.value = null
  if (pinnedId.value === targetId) pinnedId.value = null
  const { [targetId]: _, ...remainingActionStates } = actionStates.value
  actionStates.value = remainingActionStates
  selectPrimary()
  syncTimerVisibility()
}

function dismissByKey(key: string): void {
  const existing = items.value.find((item) => item.key === key)
  if (existing) dismiss(existing.id)
}

function startTimer(targetId: string, duration: number): void {
  const timer: Timer = { handle: null, remaining: duration, startedAt: Date.now(), pauseReasons: new Set() }
  timer.handle = globalThis.setTimeout(() => dismiss(targetId), duration)
  timers.set(targetId, timer)
}

function pause(targetId: string, reason = 'manual'): void {
  const timer = timers.get(targetId)
  if (!timer || timer.pauseReasons.has(reason)) return
  timer.pauseReasons.add(reason)
  if (timer.handle) {
    globalThis.clearTimeout(timer.handle)
    timer.remaining = Math.max(0, timer.remaining - (Date.now() - timer.startedAt))
    timer.handle = null
  }
}

function resume(targetId: string, reason = 'manual'): void {
  const timer = timers.get(targetId)
  if (!timer) return
  timer.pauseReasons.delete(reason)
  if (timer.pauseReasons.size || timer.handle) return
  if (timer.remaining <= 0) {
    dismiss(targetId)
    return
  }
  timer.startedAt = Date.now()
  timer.handle = globalThis.setTimeout(() => dismiss(targetId), timer.remaining)
}

function add(kind: NotificationKind, level: NotificationLevel, input: MessageOptions): string {
  const options = typeof input === 'string' ? { message: input } : input
  if (options.key) {
    const existing = items.value.find((item) => item.key === options.key)
    if (existing) dismiss(existing.id)
  }
  const persistent = options.persistent ?? kind === 'banner'
  const item: NotificationItem = {
    ...options,
    id: id('notification'),
    kind,
    level,
    persistent,
    dismissible: options.dismissible ?? !persistent,
    createdAt: nextId,
  }
  items.value.push(item)
  if (!persistent) {
    startTimer(item.id, options.duration ?? (level === 'error' ? 10000 : level === 'warning' ? 7000 : 5000))
  }
  selectPrimary()
  syncTimerVisibility()
  return item.id
}

function addError(
  kind: NotificationKind,
  input: ErrorInput,
  options: Omit<NotificationOptions, 'message'> = {},
): string {
  if (input && typeof input === 'object' && 'message' in input && !(input instanceof Error)) {
    const errorOptions = input as ErrorNotificationOptions
    return add(kind, 'error', {
      ...errorOptions,
      message: normalizeError(errorOptions.message, errorOptions.title),
    })
  }
  return add(kind, 'error', { ...options, message: normalizeError(input, options.title) })
}

/** Hover preview on capsule — does not open center dialog. */
function setHovered(targetId: string | null): void {
  if (hoveredId.value && hoveredId.value !== targetId) resume(hoveredId.value, 'hover')
  hoveredId.value = targetId
  if (targetId) pause(targetId, 'hover')
  syncTimerVisibility()
}

/** Open the reusable center detail dialog without changing notification lifetime. */
function openDetail(targetId: string): void {
  if (!items.value.some((item) => item.id === targetId)) return
  detailId.value = targetId
  pause(targetId, 'detail')
  syncTimerVisibility()
}

function closeDetail(): void {
  const closed = detailId.value
  detailId.value = null
  if (closed) resume(closed, 'detail')
  selectPrimary()
  syncTimerVisibility()
}

function togglePinned(targetId: string): void {
  if (!items.value.some((item) => item.id === targetId)) return
  const previous = pinnedId.value
  if (previous) resume(previous, 'pinned')
  pinnedId.value = previous === targetId ? null : targetId
  if (pinnedId.value) pause(pinnedId.value, 'pinned')
  syncTimerVisibility()
}

function closeExpanded(): void {
  const previous = pinnedId.value
  pinnedId.value = null
  if (previous) resume(previous, 'pinned')
  syncTimerVisibility()
}

async function runAction(targetId: string): Promise<boolean> {
  const item = items.value.find((candidate) => candidate.id === targetId)
  if (!item?.action || actionStates.value[targetId]?.running) return false
  actionStates.value = { ...actionStates.value, [targetId]: { running: true, error: null } }
  try {
    await item.action.run()
    if (items.value.some((candidate) => candidate.id === targetId)) {
      actionStates.value = { ...actionStates.value, [targetId]: { running: false, error: null } }
    }
    return true
  } catch (error) {
    if (items.value.some((candidate) => candidate.id === targetId)) {
      actionStates.value = {
        ...actionStates.value,
        [targetId]: { running: false, error: normalizeError(error) },
      }
    }
    return false
  }
}

// Legacy aliases used by older call sites / tests
function expand(targetId: string): void {
  if (pinnedId.value !== targetId) togglePinned(targetId)
}

function collapse(): void {
  closeExpanded()
}

function showNextConfirmation(): void {
  activeConfirmation = confirmations.shift() ?? null
  currentConfirmation.value = activeConfirmation
    ? (({ resolve: _, ...request }) => request)(activeConfirmation)
    : null
}

function confirm(options: ConfirmationOptions): Promise<boolean> {
  return new Promise((resolve) => {
    confirmations.push({ ...options, id: id('confirmation'), resolve })
    if (!activeConfirmation) showNextConfirmation()
  })
}

export function resolveConfirmation(requestId: string, value: boolean): void {
  if (!activeConfirmation || activeConfirmation.id !== requestId) return
  const request = activeConfirmation
  activeConfirmation = null
  request.resolve(value)
  showNextConfirmation()
}

const orderedItems = computed(() =>
  currentVisibleItems().concat(
    rankItems().filter((item) => !currentVisibleItems().some((visible) => visible.id === item.id)),
  ),
)
const visibleItems = computed(() => currentVisibleItems())
const primaryItem = computed(() => items.value.find((item) => item.id === primaryId.value) ?? null)
const detailItem = computed(() => items.value.find((item) => item.id === detailId.value) ?? null)
const expandedItem = computed(() => {
  const targetId = hoveredId.value ?? pinnedId.value
  return items.value.find((item) => item.id === targetId) ?? null
})
const overflowCount = computed(() => Math.max(0, items.value.length - visibleItems.value.length))

const notify = {
  info: (input: MessageOptions) => add('notification', 'info', input),
  success: (input: MessageOptions) => add('notification', 'success', input),
  warning: (input: MessageOptions) => add('notification', 'warning', input),
  error: (input: ErrorInput, options?: Omit<NotificationOptions, 'message'>) =>
    addError('notification', input, options),
}

const banner = {
  info: (input: MessageOptions) => add('banner', 'info', input),
  success: (input: MessageOptions) => add('banner', 'success', input),
  warning: (input: MessageOptions) => add('banner', 'warning', input),
  error: (input: ErrorInput, options?: Omit<NotificationOptions, 'message'>) =>
    addError('banner', input, options),
}

export function useNotifications() {
  return {
    items,
    primaryId,
    detailId,
    hoveredId,
    pinnedId,
    currentConfirmation,
    actionStates,
    orderedItems,
    visibleItems,
    primaryItem,
    detailItem,
    expandedItem,
    overflowCount,
    notify,
    banner,
    dismiss,
    dismissByKey,
    openDetail,
    closeDetail,
    setHovered,
    togglePinned,
    closeExpanded,
    runAction,
    expand,
    collapse,
    pause,
    resume,
    confirm,
  }
}
