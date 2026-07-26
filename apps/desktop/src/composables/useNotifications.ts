import { computed, ref } from 'vue'

export type NotificationLevel = 'info' | 'success' | 'warning' | 'error'

/**
 * Presentation class — decides which island zone owns the item and how it expires.
 *
 * - `transient`  short-lived feedback; auto-expires; queued in the right zone.
 * - `status`     state-driven condition (backend down, API unavailable); keyed,
 *                never auto-expires, owned by the state source, pinned to the
 *                left zone, and broadcast to every renderer window.
 * - `task`       long-running operation; starts `pending`, later resolves to
 *                success/error in place without losing its slot.
 */
export type NotificationKind = 'transient' | 'status' | 'task'

/** Lifecycle of a `task` notification. `settled` items expire like transients. */
export type TaskState = 'pending' | 'settled'

export interface NotificationAction {
  label: string
  run: () => void | Promise<void>
}

export interface NotificationOptions {
  /**
   * Dedupe identity. A second notification with the same key merges into the
   * first in place instead of stacking. Required for `status`.
   */
  key?: string
  title?: string
  message: string
  details?: string
  /** Grouping label for history/filtering (e.g. 'gateway', 'git', 'terminal'). */
  source?: string
  action?: NotificationAction
  duration?: number
  dismissible?: boolean
}

export interface ErrorNotificationOptions extends Omit<NotificationOptions, 'message'> {
  message?: unknown
}

export interface TaskOptions extends NotificationOptions {
  /** 0–1. Omit for an indeterminate spinner. */
  progress?: number
}

export interface NotificationItem extends NotificationOptions {
  id: string
  kind: NotificationKind
  level: NotificationLevel
  createdAt: number
  /** Number of times this item was re-raised while already live. */
  count: number
  progress?: number
  taskState?: TaskState
  /** True when the item originated in another renderer window. */
  remote?: boolean
}

export interface TaskHandle {
  readonly id: string
  update: (patch: Partial<TaskOptions> & { level?: NotificationLevel }) => void
  succeed: (input?: string | Partial<NotificationOptions>) => void
  fail: (error: unknown, options?: Omit<NotificationOptions, 'message'>) => void
  dismiss: () => void
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

export interface HistoryEntry {
  id: string
  kind: NotificationKind
  level: NotificationLevel
  title?: string
  message: string
  details?: string
  source?: string
  createdAt: number
  dismissedAt: number
  count: number
}

/** Wire shape for cross-window `status` replication. Functions cannot cross IPC. */
export type StatusBroadcast =
  | {
      op: 'raise'
      key: string
      level: NotificationLevel
      title?: string
      message: string
      details?: string
      source?: string
      createdAt: number
    }
  | { op: 'clear'; key: string }

type MessageOptions = string | NotificationOptions
type Timer = {
  handle: ReturnType<typeof globalThis.setTimeout> | null
  remaining: number
  startedAt: number
  pauseReasons: Set<string>
}
type QueuedConfirmation = ConfirmationRequest & { resolve: (value: boolean) => void }

/** Hard ceiling on live items so a stalled consumer can never grow unbounded. */
const MAX_ITEMS = 50
/** Retained dismissed items, newest first. */
const MAX_HISTORY = 50
/** Concurrently rendered transient capsules. */
const VISIBLE_TRANSIENT = 3

const DEFAULT_DURATION: Record<NotificationLevel, number> = {
  info: 5000,
  success: 5000,
  warning: 7000,
  error: 10000,
}

const items = ref<NotificationItem[]>([])
const history = ref<HistoryEntry[]>([])
const primaryId = ref<string | null>(null)
const detailId = ref<string | null>(null)
const hoveredId = ref<string | null>(null)
/** The single source of truth for "which card is open". */
const openId = ref<string | null>(null)
const overflowOpen = ref(false)
const currentConfirmation = ref<ConfirmationRequest | null>(null)
const actionStates = ref<Record<string, { running: boolean; error: string | null }>>({})
const timers = new Map<string, Timer>()
const confirmations: QueuedConfirmation[] = []
let activeConfirmation: QueuedConfirmation | null = null
let nextId = 1

/**
 * i18n-supplied fallback for errors carrying no usable message. Injected once at
 * bootstrap so this module never hardcodes a user-facing language.
 */
let unknownErrorText = 'An unknown error occurred'

export function setNotificationFallbackText(text: string): void {
  if (text.trim()) unknownErrorText = text
}

function id(prefix: 'notification' | 'confirmation'): string {
  return `${prefix}-${nextId++}`
}

function normalizeError(error: unknown, fallback?: string): string {
  if (error instanceof Error && error.message) return error.message
  if (typeof error === 'string' && error.trim()) return error
  if (error && typeof error === 'object') {
    const record = error as Record<string, unknown>
    for (const field of ['message', 'error', 'detail', 'title'] as const) {
      const value = record[field]
      if (typeof value === 'string' && value.trim()) return value
    }
  }
  return fallback?.trim() ? fallback : unknownErrorText
}

function priority(item: NotificationItem): number {
  if (item.level === 'error') return 3
  if (item.taskState === 'pending') return 2
  return 1
}

function byRank(a: NotificationItem, b: NotificationItem): number {
  return priority(b) - priority(a) || b.createdAt - a.createdAt
}

function statusItems(): NotificationItem[] {
  return items.value.filter((item) => item.kind === 'status').sort(byRank)
}

function queueItems(): NotificationItem[] {
  return items.value.filter((item) => item.kind !== 'status').sort(byRank)
}

function currentVisibleItems(): NotificationItem[] {
  // Status items live in a dedicated zone and never consume a transient slot.
  // This is what stops a stuck status condition from starving the queue.
  return statusItems().concat(queueItems().slice(0, VISIBLE_TRANSIENT))
}

function selectPrimary(): void {
  primaryId.value = currentVisibleItems()[0]?.id ?? null
}

/**
 * Timers pause only for direct user attention (hover / open card / detail).
 * Off-screen queueing deliberately does NOT pause: an item that can never be
 * seen must still expire, otherwise the queue stalls forever.
 */
function syncTimerVisibility(): void {
  for (const timerId of timers.keys()) {
    const attended =
      detailId.value === timerId || hoveredId.value === timerId || openId.value === timerId
    if (attended) pause(timerId, 'attention')
    else resume(timerId, 'attention')
  }
}

function recordHistory(item: NotificationItem): void {
  history.value = [
    {
      id: item.id,
      kind: item.kind,
      level: item.level,
      title: item.title,
      message: item.message,
      details: item.details,
      source: item.source,
      createdAt: item.createdAt,
      dismissedAt: Date.now(),
      count: item.count,
    },
    ...history.value,
  ].slice(0, MAX_HISTORY)
}

function dismiss(targetId: string): void {
  const item = items.value.find((candidate) => candidate.id === targetId)
  const timer = timers.get(targetId)
  if (timer?.handle) globalThis.clearTimeout(timer.handle)
  timers.delete(targetId)
  if (item) {
    recordHistory(item)
    // A status condition cleared here must clear in every other window too.
    if (item.kind === 'status' && item.key && !item.remote) {
      publishStatus({ op: 'clear', key: item.key })
    }
  }
  items.value = items.value.filter((candidate) => candidate.id !== targetId)
  if (detailId.value === targetId) detailId.value = null
  if (hoveredId.value === targetId) hoveredId.value = null
  if (openId.value === targetId) openId.value = null
  const { [targetId]: _removed, ...remainingActionStates } = actionStates.value
  actionStates.value = remainingActionStates
  if (!items.value.length) overflowOpen.value = false
  selectPrimary()
  syncTimerVisibility()
}

function dismissByKey(key: string): void {
  for (const item of items.value.filter((candidate) => candidate.key === key)) {
    dismiss(item.id)
  }
}

function dismissAll(): void {
  for (const item of [...items.value]) dismiss(item.id)
}

function startTimer(targetId: string, duration: number): void {
  const existing = timers.get(targetId)
  if (existing?.handle) globalThis.clearTimeout(existing.handle)
  // Carry pause reasons across a restart. A re-raised notification that the user
  // is still hovering (or that a caller paused explicitly) must stay paused —
  // dropping the reasons here would silently resume it under the pointer.
  const pauseReasons = existing ? new Set(existing.pauseReasons) : new Set<string>()
  const timer: Timer = {
    handle: null,
    remaining: duration,
    startedAt: Date.now(),
    pauseReasons,
  }
  if (!pauseReasons.size) {
    timer.handle = globalThis.setTimeout(() => dismiss(targetId), duration)
  }
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

/** Identity used to merge repeated notifications that carry no explicit key. */
function fingerprintOf(
  kind: NotificationKind,
  level: NotificationLevel,
  title: string | undefined,
  message: string,
  key?: string,
): string {
  return key ?? `auto:${kind}:${level}:${title ?? ''}:${message}`
}

function findByFingerprint(print: string): NotificationItem | undefined {
  return items.value.find(
    (item) => fingerprintOf(item.kind, item.level, item.title, item.message, item.key) === print,
  )
}

function isAutoExpiring(item: NotificationItem): boolean {
  if (item.kind === 'status') return false
  if (item.kind === 'task' && item.taskState === 'pending') return false
  return true
}

function scheduleExpiry(item: NotificationItem): void {
  if (!isAutoExpiring(item)) {
    const timer = timers.get(item.id)
    if (timer?.handle) globalThis.clearTimeout(timer.handle)
    timers.delete(item.id)
    return
  }
  startTimer(item.id, item.duration ?? DEFAULT_DURATION[item.level])
}

/**
 * Drop the least important expendable item once the cap is reached.
 *
 * Queue items are shed first. If the overflow is made entirely of status items
 * (a caller raising unkeyed `status.*` in a loop), the oldest status is shed too
 * — an unbounded list would be a memory leak, and the newest conditions are the
 * ones worth showing. Pending tasks are never evicted: their owner still holds a
 * handle and would settle a notification that no longer exists.
 */
function enforceCap(): void {
  while (items.value.length > MAX_ITEMS) {
    const expendable =
      queueItems()
        .filter((item) => item.taskState !== 'pending')
        .sort((a, b) => priority(a) - priority(b) || a.createdAt - b.createdAt)[0] ??
      statusItems().sort((a, b) => a.createdAt - b.createdAt)[0]
    if (!expendable) break
    dismiss(expendable.id)
  }
}

function add(
  kind: NotificationKind,
  level: NotificationLevel,
  input: MessageOptions,
  extra: Partial<NotificationItem> = {},
): string {
  const options: NotificationOptions = typeof input === 'string' ? { message: input } : input
  const print = fingerprintOf(kind, level, options.title, options.message, options.key)
  const existing = findByFingerprint(print)

  if (existing) {
    // Merge in place so the capsule keeps its slot, animation, and hover target
    // instead of flashing a fresh item for a repeating condition.
    const merged: NotificationItem = {
      ...existing,
      ...options,
      ...extra,
      id: existing.id,
      kind,
      level,
      count: existing.count + 1,
      createdAt: Date.now(),
    }
    items.value = items.value.map((item) => (item.id === existing.id ? merged : item))
    scheduleExpiry(merged)
    selectPrimary()
    syncTimerVisibility()
    return merged.id
  }

  const item: NotificationItem = {
    ...options,
    id: id('notification'),
    kind,
    level,
    dismissible: options.dismissible ?? true,
    createdAt: Date.now(),
    count: 1,
    ...extra,
  }
  items.value = [...items.value, item]
  scheduleExpiry(item)
  enforceCap()
  selectPrimary()
  syncTimerVisibility()
  return item.id
}

/**
 * Distinguishes `error(err)` from `error({ message, title, ... })`.
 * An options object must carry at least one option-only field — otherwise an API
 * DTO that merely happens to have a `message` property would be spread into the
 * item and leak its own fields (code, status, trace_id) into the notification.
 */
function isErrorOptions(input: unknown): input is ErrorNotificationOptions {
  if (!input || typeof input !== 'object' || input instanceof Error) return false
  const record = input as Record<string, unknown>
  return (['key', 'title', 'details', 'action', 'duration', 'dismissible', 'source'] as const).some(
    (field) => field in record,
  )
}

function addError(
  kind: NotificationKind,
  input: unknown,
  options: Omit<NotificationOptions, 'message'> = {},
): string {
  if (isErrorOptions(input)) {
    return add(kind, 'error', {
      ...input,
      message: normalizeError(input.message, input.title),
    })
  }
  return add(kind, 'error', { ...options, message: normalizeError(input, options.title) })
}

/** Patch a live notification without recreating it. */
function update(
  targetId: string,
  patch: Partial<NotificationOptions> & {
    level?: NotificationLevel
    progress?: number
    taskState?: TaskState
  },
): void {
  const existing = items.value.find((item) => item.id === targetId)
  if (!existing) return
  const merged: NotificationItem = { ...existing, ...patch }
  items.value = items.value.map((item) => (item.id === targetId ? merged : item))
  scheduleExpiry(merged)
  selectPrimary()
  syncTimerVisibility()
}

// ---------------------------------------------------------------------------
// Cross-window status replication
// ---------------------------------------------------------------------------

/**
 * Keys currently being applied from a remote broadcast. Guards the echo loop:
 * apply -> local add/dismiss -> publish -> remote apply -> ...
 */
const applyingRemote = new Set<string>()

function statusBridge(): {
  broadcastStatusNotification?: (payload: StatusBroadcast) => void
  onStatusNotification?: (cb: (payload: StatusBroadcast) => void) => () => void
} | undefined {
  return (globalThis as { window?: { tinadec?: Record<string, unknown> } }).window
    ?.tinadec as
    | {
        broadcastStatusNotification?: (payload: StatusBroadcast) => void
        onStatusNotification?: (cb: (payload: StatusBroadcast) => void) => () => void
      }
    | undefined
}

function publishStatus(payload: StatusBroadcast): void {
  if (applyingRemote.has(payload.key)) return
  statusBridge()?.broadcastStatusNotification?.(payload)
}

/** Applies a status broadcast originating in another renderer window. */
export function applyStatusBroadcast(payload: StatusBroadcast): void {
  applyingRemote.add(payload.key)
  try {
    if (payload.op === 'clear') {
      dismissByKey(payload.key)
      return
    }
    add(
      'status',
      payload.level,
      {
        key: payload.key,
        title: payload.title,
        message: payload.message,
        details: payload.details,
        source: payload.source,
      },
      { remote: true, createdAt: payload.createdAt },
    )
  } finally {
    applyingRemote.delete(payload.key)
  }
}

/** Subscribes this window to remote status notifications. Returns an unsubscribe. */
export function startStatusSync(): () => void {
  const subscribe = statusBridge()?.onStatusNotification
  if (!subscribe) return () => {}
  return subscribe(applyStatusBroadcast)
}

function addStatus(level: NotificationLevel, input: MessageOptions): string {
  const options: NotificationOptions = typeof input === 'string' ? { message: input } : input
  const itemId = add('status', level, { dismissible: true, ...options })
  const item = items.value.find((candidate) => candidate.id === itemId)
  if (item?.key && !item.remote) {
    publishStatus({
      op: 'raise',
      key: item.key,
      level: item.level,
      title: item.title,
      message: item.message,
      details: item.details,
      source: item.source,
      createdAt: item.createdAt,
    })
  }
  return itemId
}

// ---------------------------------------------------------------------------
// Task notifications
// ---------------------------------------------------------------------------

function startTask(input: string | TaskOptions): TaskHandle {
  const options: TaskOptions = typeof input === 'string' ? { message: input } : input
  const taskId = add('task', 'info', options, {
    taskState: 'pending',
    progress: options.progress,
  })

  const settle = (level: NotificationLevel, patch: Partial<NotificationOptions>): void => {
    update(taskId, { ...patch, level, taskState: 'settled', progress: undefined })
  }

  return {
    id: taskId,
    update: (patch) => update(taskId, patch),
    succeed: (result) =>
      settle('success', typeof result === 'string' ? { message: result } : (result ?? {})),
    fail: (error, failOptions = {}) =>
      settle('error', { ...failOptions, message: normalizeError(error, failOptions.title) }),
    dismiss: () => dismiss(taskId),
  }
}

// ---------------------------------------------------------------------------
// Interaction
// ---------------------------------------------------------------------------

/** Hover preview — highlights the capsule and holds its timer, nothing more. */
function setHovered(targetId: string | null): void {
  if (hoveredId.value && hoveredId.value !== targetId) resume(hoveredId.value, 'attention')
  hoveredId.value = targetId
  syncTimerVisibility()
}

/** Open the reusable center detail dialog without changing notification lifetime. */
function openDetail(targetId: string): void {
  if (!items.value.some((item) => item.id === targetId)) return
  detailId.value = targetId
  syncTimerVisibility()
}

function closeDetail(): void {
  detailId.value = null
  selectPrimary()
  syncTimerVisibility()
}

/** Toggle the expanded card. `openId` alone decides whether the card is shown. */
function toggleOpen(targetId: string): void {
  if (!items.value.some((item) => item.id === targetId)) return
  openId.value = openId.value === targetId ? null : targetId
  syncTimerVisibility()
}

function closeOpen(): void {
  openId.value = null
  syncTimerVisibility()
}

function toggleOverflow(): void {
  overflowOpen.value = !overflowOpen.value
}

function closeOverflow(): void {
  overflowOpen.value = false
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

function showNextConfirmation(): void {
  activeConfirmation = confirmations.shift() ?? null
  currentConfirmation.value = activeConfirmation
    ? (({ resolve: _resolve, ...request }) => request)(activeConfirmation)
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

// ---------------------------------------------------------------------------
// Derived state
// ---------------------------------------------------------------------------

const statusZone = computed(() => statusItems())
const transientZone = computed(() => queueItems().slice(0, VISIBLE_TRANSIENT))
const visibleItems = computed(() => statusZone.value.concat(transientZone.value))
const orderedItems = computed(() => statusItems().concat(queueItems()))
const primaryItem = computed(() => items.value.find((item) => item.id === primaryId.value) ?? null)
const detailItem = computed(() => items.value.find((item) => item.id === detailId.value) ?? null)
const openItem = computed(() => items.value.find((item) => item.id === openId.value) ?? null)
const hoveredItem = computed(() => items.value.find((item) => item.id === hoveredId.value) ?? null)
const overflowCount = computed(() => Math.max(0, items.value.length - visibleItems.value.length))
const hasPendingTask = computed(() => items.value.some((item) => item.taskState === 'pending'))

const notify = {
  info: (input: MessageOptions) => add('transient', 'info', input),
  success: (input: MessageOptions) => add('transient', 'success', input),
  warning: (input: MessageOptions) => add('transient', 'warning', input),
  error: (input: unknown, options?: Omit<NotificationOptions, 'message'>) =>
    addError('transient', input, options),
  task: startTask,
  update,
}

/**
 * Persistent, state-driven conditions. Keyed items replicate to every window;
 * the owning state source must clear them with `dismissByKey` on recovery.
 */
const status = {
  info: (input: MessageOptions) => addStatus('info', input),
  success: (input: MessageOptions) => addStatus('success', input),
  warning: (input: MessageOptions) => addStatus('warning', input),
  error: (input: unknown, options?: Omit<NotificationOptions, 'message'>) => {
    if (isErrorOptions(input)) {
      return addStatus('error', {
        ...input,
        message: normalizeError(input.message, input.title),
      })
    }
    return addStatus('error', { ...options, message: normalizeError(input, options?.title) })
  },
}

/** Back-compat alias — `banner.*` was the previous name for the status class. */
const banner = status

export function useNotifications() {
  return {
    items,
    history,
    primaryId,
    detailId,
    hoveredId,
    openId,
    overflowOpen,
    currentConfirmation,
    actionStates,
    orderedItems,
    visibleItems,
    statusZone,
    transientZone,
    primaryItem,
    detailItem,
    openItem,
    hoveredItem,
    overflowCount,
    hasPendingTask,
    notify,
    status,
    banner,
    dismiss,
    dismissByKey,
    dismissAll,
    openDetail,
    closeDetail,
    setHovered,
    toggleOpen,
    closeOpen,
    toggleOverflow,
    closeOverflow,
    runAction,
    update,
    pause,
    resume,
    confirm,
  }
}
