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
  action?: NotificationAction
  duration?: number
  persistent?: boolean
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
const expandedId = ref<string | null>(null)
const currentConfirmation = ref<ConfirmationRequest | null>(null)
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
  const primary = items.value.find((item) => item.id === primaryId.value)
  const rest = rankItems().filter((item) => item.id !== primary?.id)
  return primary ? [primary, ...rest].slice(0, 3) : rest.slice(0, 3)
}

function selectPrimary(): void {
  if (expandedId.value && items.value.some((item) => item.id === expandedId.value)) {
    primaryId.value = expandedId.value
    return
  }
  primaryId.value = rankItems()[0]?.id ?? null
}

function syncTimerVisibility(): void {
  const visibleIds = new Set(currentVisibleItems().map((item) => item.id))
  for (const id of timers.keys()) {
    if (expandedId.value || !visibleIds.has(id)) pause(id, 'visibility')
    else resume(id, 'visibility')
  }
}

function dismiss(id: string): void {
  const timer = timers.get(id)
  if (timer?.handle) globalThis.clearTimeout(timer.handle)
  timers.delete(id)
  items.value = items.value.filter((item) => item.id !== id)
  if (expandedId.value === id) expandedId.value = null
  selectPrimary()
  syncTimerVisibility()
}

function startTimer(id: string, duration: number): void {
  const timer: Timer = { handle: null, remaining: duration, startedAt: Date.now(), pauseReasons: new Set() }
  timer.handle = globalThis.setTimeout(() => dismiss(id), duration)
  timers.set(id, timer)
}

function pause(id: string, reason = 'manual'): void {
  const timer = timers.get(id)
  if (!timer || timer.pauseReasons.has(reason)) return
  timer.pauseReasons.add(reason)
  if (timer.handle) {
    globalThis.clearTimeout(timer.handle)
    timer.remaining = Math.max(0, timer.remaining - (Date.now() - timer.startedAt))
    timer.handle = null
  }
}

function resume(id: string, reason = 'manual'): void {
  const timer = timers.get(id)
  if (!timer) return
  timer.pauseReasons.delete(reason)
  if (timer.pauseReasons.size || timer.handle) return
  if (timer.remaining <= 0) {
    dismiss(id)
    return
  }
  timer.startedAt = Date.now()
  timer.handle = globalThis.setTimeout(() => dismiss(id), timer.remaining)
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
    createdAt: nextId,
  }
  items.value.push(item)
  if (!persistent) startTimer(item.id, options.duration ?? (level === 'error' ? 10000 : level === 'warning' ? 7000 : 5000))
  selectPrimary()
  syncTimerVisibility()
  return item.id
}

function addError(kind: NotificationKind, input: ErrorInput, options: Omit<NotificationOptions, 'message'> = {}): string {
  if (input && typeof input === 'object' && 'message' in input && !(input instanceof Error)) {
    const errorOptions = input as ErrorNotificationOptions
    return add(kind, 'error', { ...errorOptions, message: normalizeError(errorOptions.message, errorOptions.title) })
  }
  return add(kind, 'error', { ...options, message: normalizeError(input, options.title) })
}

function expand(id: string): void {
  if (!items.value.some((item) => item.id === id)) return
  if (expandedId.value && expandedId.value !== id) resume(expandedId.value, 'expanded')
  expandedId.value = id
  primaryId.value = id
  pause(id, 'expanded')
  syncTimerVisibility()
}

function collapse(): void {
  const id = expandedId.value
  expandedId.value = null
  if (id) resume(id, 'expanded')
  selectPrimary()
  syncTimerVisibility()
}

function promote(id: string): void {
  if (items.value.some((item) => item.id === id)) {
    primaryId.value = id
    syncTimerVisibility()
  }
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

const orderedItems = computed(() => currentVisibleItems().concat(
  rankItems().filter((item) => !currentVisibleItems().some((visible) => visible.id === item.id)),
))
const visibleItems = computed(() => currentVisibleItems())
const primaryItem = computed(() => items.value.find((item) => item.id === primaryId.value) ?? null)
const overflowCount = computed(() => Math.max(0, items.value.length - visibleItems.value.length))

const notify = {
  info: (input: MessageOptions) => add('notification', 'info', input),
  success: (input: MessageOptions) => add('notification', 'success', input),
  warning: (input: MessageOptions) => add('notification', 'warning', input),
  error: (input: ErrorInput, options?: Omit<NotificationOptions, 'message'>) => addError('notification', input, options),
}

const banner = {
  info: (input: MessageOptions) => add('banner', 'info', input),
  success: (input: MessageOptions) => add('banner', 'success', input),
  warning: (input: MessageOptions) => add('banner', 'warning', input),
  error: (input: ErrorInput, options?: Omit<NotificationOptions, 'message'>) => addError('banner', input, options),
}

export function useNotifications() {
  return {
    items,
    primaryId,
    expandedId,
    currentConfirmation,
    orderedItems,
    visibleItems,
    primaryItem,
    overflowCount,
    notify,
    banner,
    dismiss,
    expand,
    collapse,
    promote,
    pause,
    resume,
    confirm,
  }
}
