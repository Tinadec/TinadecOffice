import {
  computed,
  getCurrentInstance,
  getCurrentScope,
  onBeforeUnmount,
  onScopeDispose,
  ref,
} from 'vue'
import {
  ApiError,
  UNAVAILABLE_STORAGE_CODES,
  apiErrorDetails,
  type ApiErrorActionKind,
} from '@/lib/apiError'

export type NotificationLevel = 'info' | 'success' | 'warning' | 'error'

export type NotificationCategory = 'connection' | 'general'

/**
 * Presentation class — decides which island zone owns the item and how it expires.
 *
 * - `transient`  short-lived feedback; auto-expires; queued in the right zone.
 * - `status`     state-driven condition (backend down, API unavailable); keyed,
 *                never auto-expires, owned by the state source, pinned to the
 *                left zone, and optionally broadcast to sibling windows when
 *                `broadcast: true` is explicitly set.
 * - `task`       long-running operation; starts `pending`, later resolves to
 *                success/error in place without losing its slot.
 */
export type NotificationKind = 'transient' | 'status' | 'task'

/**
 * Lifecycle owner — who ends the display. Modeled on Apple Live Activities
 * (ongoing activities are system-owned; settled ones linger briefly) and
 * 小米超级岛 (设备状态常驻, cleared only by the owning state source).
 *
 * - `auto`    expires on its own after `duration` (or the level default); the
 *             user may dismiss it early. Default for transient feedback and
 *             settled tasks.
 * - `sticky`  never expires on its own; stays on the island until the user
 *             dismisses it. For persistent notices the user owns.
 * - `source`  never expires and is NOT user-dismissible; only the owning state
 *             source (keyed `status.*` conditions, via `dismissByKey` or a
 *             remote broadcast) or the task handle (pending tasks) may clear
 *             it. Assigned internally — callers cannot request it.
 */
export type NotificationPersistence = 'auto' | 'sticky' | 'source'

/** Lifecycle of a `task` notification. `settled` items expire like transients. */
export type TaskState = 'pending' | 'settled'

export interface NotificationAction {
  label: string
  run: () => unknown
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
  /**
   * Lifecycle override for transient feedback: `'auto'` (default) expires on
   * its own; `'sticky'` stays on the island until the user dismisses it.
   * Ignored for `status.*` and tasks — their persistence is always `'source'`
   * while the condition/operation is live.
   */
  persistence?: 'auto' | 'sticky'
  /**
   * Whether the user may close a transient item before it expires on its own.
   * Ignored for `status` and pending tasks, which are never user-closable.
   * @default true
   */
  dismissible?: boolean
  /**
   * Domain category used for centralized connection-avalanche suppression and
   * recovery cleanup. Connection-level errors are auto-tagged `'connection'`.
   */
  category?: NotificationCategory
  /**
   * Whether a keyed `status.*` condition replicates across renderer windows via IPC.
   * Defaults to `false` so local or detached-panel statuses never leak ghost cards.
   * @default false
   */
  broadcast?: boolean
}

export interface ErrorNotificationOptions extends Omit<NotificationOptions, 'message'> {
  message?: unknown
}

export interface TaskOptions extends NotificationOptions {
  /** 0–1. Omit for an indeterminate spinner. */
  progress?: number
}

export interface NotificationItem extends Omit<NotificationOptions, 'persistence'> {
  id: string
  kind: NotificationKind
  level: NotificationLevel
  /** Resolved lifecycle owner. See {@link NotificationPersistence}. */
  persistence: NotificationPersistence
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

/** Canonical key of the single global host/gateway connection status capsule. */
const GLOBAL_CONNECTION_KEY = 'backend-connection'

const CONNECTION_ERROR_CODES = new Set([
  'backend_network_unavailable',
  'backend_write_outcome_unknown',
  'host_unavailable',
  'host_bridge_unavailable',
  'host_connection_unavailable',
  'host_identity_unavailable',
  'host_identity_rejected',
  'host_endpoint_invalid',
  'host_credential_invalid',
  'host_nonce_invalid',
  'host_connection_stopped',
  'desktop_restart_required',
  'desktop_host_required',
  'web_auth_required',
])

const NETWORK_MESSAGE_PATTERN =
  /(failed to fetch|networkerror|network request failed|load failed|econnrefused|econnreset|etimedout|err_connection_refused|fetch failed|cannot connect to backend)/i

/**
 * Localized host/network strings registered by `main.ts` (and seeded with default
 * zh-CN/en host readiness messages so callers that forwarded `error.message` without
 * the underlying `ApiError` still classify into `category: 'connection'`).
 */
const localizedConnectionMessages = new Set<string>([
  '本地服务暂时不可用，请重试宿主连接。',
  '本地服务无法证明自己是受信任的宿主。请同时启动 Core、Gateway 和桌面端，然后重试。',
  '桌面宿主尚未就绪，请重试连接。',
  '正在验证桌面宿主，业务操作暂不可用。',
  '桌面宿主状态接口暂不可用，请重试连接。',
  '无法检查宿主连接，请重试桌面宿主。',
  '宿主正在关闭。',
  'The local services are temporarily unavailable. Retry the host connection.',
  'The local services could not prove their trusted host identity. Start Core, Gateway and Desktop together, then retry.',
  'Desktop host is not ready yet. Retry the connection.',
  'Verifying desktop host; workspace operations are temporarily unavailable.',
  'Desktop host status bridge is unavailable. Retry the connection.',
  'Could not check the host connection. Retry the desktop host.',
  'The host is shutting down.',
])

export function setLocalizedConnectionMessages(messages: Iterable<string>): void {
  localizedConnectionMessages.clear()
  for (const message of messages) {
    const trimmed = typeof message === 'string' ? message.trim() : ''
    if (trimmed) localizedConnectionMessages.add(trimmed)
  }
}

function isKnownConnectionMessage(message: string): boolean {
  const trimmed = message.trim()
  return Boolean(trimmed) && (NETWORK_MESSAGE_PATTERN.test(trimmed) || localizedConnectionMessages.has(trimmed))
}

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
/** Which zone's fold stack panel is open ('status' | 'transient'), if any. */
const stackZone = ref<'status' | 'transient' | null>(null)
const currentConfirmation = ref<ConfirmationRequest | null>(null)
const actionStates = ref<Record<string, { running: boolean; error: string | null }>>({})
const globalConnectionBlocked = ref(false)
const timers = new Map<string, Timer>()
const confirmations: QueuedConfirmation[] = []
let activeConfirmation: QueuedConfirmation | null = null
let nextId = 1
let nextScopedKey = 1

/**
 * Classifies errors caused by an unreachable backend, disconnected Gateway, or
 * unverified desktop host so callers and the notification engine can coalesce
 * derived sub-module errors into the single `'backend-connection'` status capsule.
 */
export function isConnectionLevelError(error: unknown): boolean {
  if (!error) return false
  if (typeof error === 'string') {
    return CONNECTION_ERROR_CODES.has(error.trim()) || isKnownConnectionMessage(error)
  }
  if (typeof error === 'object') {
    const record = error as {
      code?: unknown
      status?: unknown
      message?: unknown
      error?: unknown
      detail?: unknown
      cause?: unknown
    }
    const code = typeof record.code === 'string' ? record.code : undefined
    if (code && CONNECTION_ERROR_CODES.has(code)) return true
    if (code && UNAVAILABLE_STORAGE_CODES.has(code)) return false

    const status = typeof record.status === 'number' ? record.status : undefined
    if (status === 502 || status === 503 || status === 504) return true

    if (error instanceof TypeError) {
      const msg = error.message ?? ''
      if (/(fetch|network|load failed|connect)/i.test(msg)) return true
    }

    for (const candidate of [record.message, record.error, record.detail]) {
      if (typeof candidate === 'string' && isKnownConnectionMessage(candidate)) {
        return true
      }
    }
    if (record.cause && record.cause !== error && isConnectionLevelError(record.cause)) {
      return true
    }
  }
  return false
}

function isGlobalConnectionActive(): boolean {
  return (
    globalConnectionBlocked.value ||
    items.value.some((candidate) => candidate.key === GLOBAL_CONNECTION_KEY)
  )
}

function coalesceDerivedConnectionItems(): void {
  for (const item of [...items.value]) {
    if (item.category === 'connection' && item.key !== GLOBAL_CONNECTION_KEY) {
      if (item.key && item.kind === 'status') dismissByKey(item.key)
      else dismiss(item.id)
    }
  }
}

/**
 * Marks whether global host/gateway connectivity is currently blocked.
 * When `true`, derived `category: 'connection'` notifications from sub-modules
 * are suppressed and any existing derived connection items are coalesced.
 */
export function setGlobalConnectionBlocked(blocked: boolean): void {
  globalConnectionBlocked.value = blocked
  if (blocked) {
    coalesceDerivedConnectionItems()
  }
}

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

/**
 * Recovery actions a caller can register once, at bootstrap, so every error notification can
 * offer the same escape hatch without each call site re-wiring it. The server names an action
 * kind; whoever owns navigation maps it to behaviour here.
 */
export interface ErrorRecoveryHandlers {
  retry?: (error?: ApiError) => void | Promise<void>
  reload?: () => void | Promise<void>
  open_settings?: () => void | Promise<void>
  open_storage_settings?: () => void | Promise<void>
  open_tool_settings?: () => void | Promise<void>
  unregister_workspace?: (storageId?: string) => void | Promise<void>
  choose_folder?: () => void | Promise<void>
}

let recoveryHandlers: ErrorRecoveryHandlers = {}

/** Installed once by the app shell; later calls merge, so a page can add its own. */
export function setErrorRecoveryHandlers(handlers: ErrorRecoveryHandlers): void {
  recoveryHandlers = { ...recoveryHandlers, ...handlers }
}

/** Localized labels for the recovery actions, injected at bootstrap like the fallback text. */
let actionLabels: Partial<Record<ApiErrorActionKind, string>> = {}

export function setErrorActionLabels(labels: Partial<Record<ApiErrorActionKind, string>>): void {
  actionLabels = { ...actionLabels, ...labels }
}

export function getErrorActionLabel(kind: ApiErrorActionKind): string {
  const defaults: Record<ApiErrorActionKind, string> = {
    retry: 'Retry',
    reload: 'Reload',
    open_settings: 'Open settings',
    open_storage_settings: 'Storage settings',
    open_tool_settings: 'Tool settings',
    unregister_workspace: 'Unregister',
    choose_folder: 'Choose folder',
  }
  return actionLabels[kind] ?? defaults[kind]
}

/**
 * Turns a server-classified error into a ready-to-run notification action. Only the first
 * available handler wins: offering "retry" and "reload" side by side for one failure asks the
 * person to choose between two things they cannot tell apart.
 */
function recoveryActionFor(error: unknown): NotificationAction | undefined {
  if (!(error instanceof ApiError) || error.actions.length === 0) return undefined
  for (const kind of error.actions) {
    const run = recoveryHandlers[kind]
    if (!run || (kind === 'unregister_workspace' && !error.storageId)) continue
    return {
      label: actionLabels[kind] ?? kind.replace(/_/g, ' '),
      run: () => {
        if (kind === 'unregister_workspace') {
          return (run as (id?: string) => void | Promise<void>)(error.storageId)
        }
        return (run as (error?: ApiError) => void | Promise<void>)(error)
      },
    }
  }
  return undefined
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
  return statusItems().concat(queueItems())
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
  if (item) recordHistory(item)
  items.value = items.value.filter((candidate) => candidate.id !== targetId)
  if (detailId.value === targetId) detailId.value = null
  if (hoveredId.value === targetId) hoveredId.value = null
  if (openId.value === targetId) openId.value = null
  const { [targetId]: _removed, ...remainingActionStates } = actionStates.value
  actionStates.value = remainingActionStates
  if (!items.value.length) overflowOpen.value = false
  // Auto-collapse an open fold stack once its zone empties.
  if (stackZone.value === 'status' && statusItems().length === 0) stackZone.value = null
  if (stackZone.value === 'transient' && queueItems().length === 0) stackZone.value = null
  selectPrimary()
  syncTimerVisibility()
}

function dismissByKey(key: string): void {
  const owned = items.value.filter((candidate) => candidate.key === key)
  for (const item of owned) dismiss(item.id)
  // Only an explicit owner clear of an opted-in broadcast status replicates across windows.
  if (owned.some((item) => item.kind === 'status' && item.broadcast === true && !item.remote)) {
    publishStatus({ op: 'clear', key })
  }
}

/**
 * Dismisses every live notification matching `category` (including keyed status
 * items owned by that category). Used on connection recovery to clear 100% of
 * connection-derived notifications in one pass.
 */
export function dismissByCategory(category: NotificationCategory): void {
  for (const item of [...items.value]) {
    const itemCategory = item.category ?? 'general'
    if (itemCategory === category) {
      if (item.key && item.kind === 'status') dismissByKey(item.key)
      else dismiss(item.id)
    }
  }
}

/**
 * User action (notification center) — clears everything the user is allowed
 * to close. Source-owned status conditions and pending tasks survive; their
 * owners remain responsible for their lifecycle.
 */
function dismissAll(): void {
  for (const item of [...items.value]) {
    if (isUserDismissible(item)) dismiss(item.id)
  }
}

function clearHistory(): void {
  history.value = []
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
  if (item.persistence !== 'auto') return false
  if (item.kind === 'task' && item.taskState === 'pending') return false
  return true
}

/**
 * Single source of truth for close affordances across capsule, expanded card,
 * notification center, and detail dialog. Status conditions and pending tasks
 * are owned by their source/handle and can never be closed by the user;
 * everything else is closable unless the caller passed `dismissible: false`.
 */
export function isUserDismissible(item: NotificationItem): boolean {
  if (item.kind === 'status') return false
  if (item.kind === 'task' && item.taskState === 'pending') return false
  if (item.persistence === 'sticky') return true
  return item.dismissible !== false
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
  const resolvedCategory: NotificationCategory =
    extra.category ??
    options.category ??
    (options.key === GLOBAL_CONNECTION_KEY || isConnectionLevelError(options.message)
      ? 'connection'
      : 'general')
  const resolvedBroadcast = extra.broadcast ?? options.broadcast ?? false

  // Avalanche suppression: while `'backend-connection'` is active or global
  // connection is blocked, sub-modules never stack derived connection cards.
  if (
    resolvedCategory === 'connection' &&
    options.key !== GLOBAL_CONNECTION_KEY &&
    isGlobalConnectionActive()
  ) {
    if (options.key) {
      dismissByKey(options.key)
    }
    const globalItem = items.value.find((candidate) => candidate.key === GLOBAL_CONNECTION_KEY)
    return globalItem?.id ?? `suppressed:${GLOBAL_CONNECTION_KEY}`
  }

  const print = fingerprintOf(kind, level, options.title, options.message, options.key)
  const existing = findByFingerprint(print)

  if (existing) {
    // Merge in place so the capsule keeps its slot, animation, and hover target
    // instead of flashing a fresh item for a repeating condition.
    const sameSignal = existing.message === options.message && existing.level === level
    const merged: NotificationItem = {
      ...existing,
      ...options,
      details: options.details,
      action: options.action,
      ...extra,
      id: existing.id,
      kind,
      level,
      category: resolvedCategory,
      broadcast: resolvedBroadcast,
      persistence: kind === 'transient' ? (options.persistence ?? existing.persistence) : 'source',
      count: sameSignal ? existing.count + 1 : 1,
      createdAt: Date.now(),
    }
    items.value = items.value.map((item) => (item.id === existing.id ? merged : item))
    if (options.key === GLOBAL_CONNECTION_KEY) {
      coalesceDerivedConnectionItems()
    }
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
    category: resolvedCategory,
    broadcast: resolvedBroadcast,
    // Status conditions and tasks are source-owned while live; only transient
    // feedback honors a caller-requested lifecycle.
    persistence: kind === 'transient' ? (options.persistence ?? 'auto') : 'source',
    dismissible: options.dismissible ?? true,
    createdAt: Date.now(),
    count: 1,
    ...extra,
  }
  items.value = [...items.value, item]
  if (options.key === GLOBAL_CONNECTION_KEY) {
    coalesceDerivedConnectionItems()
  }
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
  if ('code' in record || 'trace_id' in record || 'status' in record) return false
  return (
    (['key', 'title', 'details', 'action', 'duration', 'dismissible', 'source', 'broadcast'] as const).some(
      (field) => field in record,
    ) ||
    record.category === 'connection' ||
    record.category === 'general'
  )
}

function addError(
  kind: NotificationKind,
  input: unknown,
  options: Omit<NotificationOptions, 'message'> = {},
): string {
  if (isErrorOptions(input)) {
    const inferredCategory =
      input.category ?? (isConnectionLevelError(input.message) ? 'connection' : undefined)
    return add(kind, 'error', {
      ...input,
      ...(inferredCategory ? { category: inferredCategory } : {}),
      message: normalizeError(input.message, input.title),
    })
  }
  const inferredCategory =
    options.category ?? (isConnectionLevelError(input) ? 'connection' : undefined)
  return add(kind, 'error', {
    ...options,
    ...(inferredCategory ? { category: inferredCategory } : {}),
    // A classified failure carries its own recovery path; the caller can still override it.
    action: options.action ?? recoveryActionFor(input),
    details: options.details ?? apiErrorDetails(input),
    message: normalizeError(input, options.title),
  })
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
  // A settled task returns to user-owned, auto-expiring feedback (Apple Live
  // Activity model: the activity ends, its result lingers briefly, then leaves).
  if (patch.taskState === 'settled') merged.persistence = 'auto'
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
  const itemId = add('status', level, options)
  const item = items.value.find((candidate) => candidate.id === itemId)
  if (item?.key && item.broadcast === true && !item.remote) {
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

/**
 * Component/effect-scoped status helper. Tracks every key raised through this
 * handle (defaulting `broadcast` to `false`) and automatically calls
 * `dismissByKey` when the enclosing Vue component or effectScope disposes.
 */
export function useScopedStatus(defaultKey?: string) {
  const trackedKeys = new Set<string>(defaultKey ? [defaultKey] : [])
  const fallbackKey = defaultKey ?? `scoped-status-${nextScopedKey++}`

  const resolveScopedOptions = (
    input: MessageOptions,
    options?: Omit<NotificationOptions, 'message'>,
  ): NotificationOptions => {
    const base: NotificationOptions =
      typeof input === 'string'
        ? options
          ? { ...options, message: input }
          : { message: input }
        : { ...options, ...input }
    const key = base.key ?? fallbackKey
    trackedKeys.add(key)
    return {
      ...base,
      key,
      broadcast: base.broadcast ?? false,
    }
  }

  const cleanup = () => {
    for (const key of trackedKeys) {
      dismissByKey(key)
    }
    trackedKeys.clear()
  }

  if (getCurrentScope()) {
    onScopeDispose(cleanup)
  } else if (getCurrentInstance()) {
    onBeforeUnmount(cleanup)
  }

  return {
    info: (input: MessageOptions, options?: Omit<NotificationOptions, 'message'>) =>
      addStatus('info', resolveScopedOptions(input, options)),
    success: (input: MessageOptions, options?: Omit<NotificationOptions, 'message'>) =>
      addStatus('success', resolveScopedOptions(input, options)),
    warning: (input: MessageOptions, options?: Omit<NotificationOptions, 'message'>) =>
      addStatus('warning', resolveScopedOptions(input, options)),
    error: (input: unknown, options?: Omit<NotificationOptions, 'message'>) => {
      if (isErrorOptions(input)) {
        const scoped = resolveScopedOptions({
          ...input,
          message: normalizeError(input.message, input.title),
        })
        return status.error(scoped)
      }
      const key = options?.key ?? fallbackKey
      trackedKeys.add(key)
      return status.error(input, {
        ...options,
        key,
        broadcast: options?.broadcast ?? false,
      })
    },
    clear: (key?: string) => {
      if (key) {
        dismissByKey(key)
        trackedKeys.delete(key)
        return
      }
      cleanup()
    },
    dismiss: (key?: string) => {
      if (key) {
        dismissByKey(key)
        trackedKeys.delete(key)
        return
      }
      cleanup()
    },
  }
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
      settle('error', {
        ...failOptions,
        details: failOptions.details ?? apiErrorDetails(error),
        message: normalizeError(error, failOptions.title),
      }),
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

function openStack(zone: 'status' | 'transient'): void {
  stackZone.value = zone
}

function closeStack(): void {
  stackZone.value = null
}

function toggleStack(zone: 'status' | 'transient'): void {
  stackZone.value = stackZone.value === zone ? null : zone
}

// ---------------------------------------------------------------------------
// 通知打开 interaction model (Apple Dynamic Island desktop equivalent):
// hover auto-expands the enlarged island window; click opens the detail dialog.
// ---------------------------------------------------------------------------

const HOVER_OPEN_DELAY_MS = 240
const HOVER_SWITCH_DELAY_MS = 90
const HOVER_CLOSE_DELAY_MS = 400

let hoverOpenTimer: ReturnType<typeof setTimeout> | null = null
let hoverCloseTimer: ReturnType<typeof setTimeout> | null = null

function clearHoverOpenTimer(): void {
  if (hoverOpenTimer) {
    globalThis.clearTimeout(hoverOpenTimer)
    hoverOpenTimer = null
  }
}

function clearHoverCloseTimer(): void {
  if (hoverCloseTimer) {
    globalThis.clearTimeout(hoverCloseTimer)
    hoverCloseTimer = null
  }
}

/** Close both the expanded card and any open fold stack. */
function closeExpanded(): void {
  closeOpen()
  closeStack()
}

function hoverCapsuleEnter(targetId: string): void {
  setHovered(targetId)
  clearHoverCloseTimer()
  // Hovering a plain (single) capsule dismisses any open fold stack so the
  // card and the stack are never both up.
  if (stackZone.value) closeStack()
  if (openId.value === targetId) return
  clearHoverOpenTimer()
  const delay = openId.value ? HOVER_SWITCH_DELAY_MS : HOVER_OPEN_DELAY_MS
  hoverOpenTimer = globalThis.setTimeout(() => {
    hoverOpenTimer = null
    // Suppressed while a modal surface owns the interaction, and stale if the
    // pointer already moved on to another capsule.
    if (hoveredId.value !== targetId) return
    if (detailId.value || currentConfirmation.value) return
    openId.value = targetId
    syncTimerVisibility()
  }, delay)
}

/** Hovering an aggregated hero (folded count > 0) opens that zone's stack. */
function hoverHeroEnter(zone: 'status' | 'transient', targetId: string): void {
  setHovered(targetId)
  clearHoverCloseTimer()
  if (stackZone.value === zone) return
  // The card and the fold stack are mutually exclusive.
  if (openId.value) closeOpen()
  clearHoverOpenTimer()
  const delay = stackZone.value ? HOVER_SWITCH_DELAY_MS : HOVER_OPEN_DELAY_MS
  hoverOpenTimer = globalThis.setTimeout(() => {
    hoverOpenTimer = null
    if (hoveredId.value !== targetId) return
    if (detailId.value || currentConfirmation.value) return
    stackZone.value = zone
  }, delay)
}

function hoverCapsuleLeave(): void {
  setHovered(null)
  clearHoverOpenTimer()
  if ((!openId.value && !stackZone.value) || hoverCloseTimer) return
  hoverCloseTimer = globalThis.setTimeout(() => {
    hoverCloseTimer = null
    // Only close when the pointer did not land on the card or a sibling capsule.
    if (!hoveredId.value) closeExpanded()
  }, HOVER_CLOSE_DELAY_MS)
}

function hoverCardEnter(): void {
  clearHoverCloseTimer()
}

function hoverCardLeave(): void {
  if ((!openId.value && !stackZone.value) || hoverCloseTimer) return
  hoverCloseTimer = globalThis.setTimeout(() => {
    hoverCloseTimer = null
    if (!hoveredId.value) closeExpanded()
  }, HOVER_CLOSE_DELAY_MS)
}

/**
 * 通知打开 — clicking a capsule opens its detail dialog directly (one step to
 * full information + actions). The hover-expanded card and the notification
 * center close behind the modal surface.
 */
function openNotification(targetId: string): void {
  clearHoverOpenTimer()
  clearHoverCloseTimer()
  if (openId.value) closeOpen()
  closeOverflow()
  closeStack()
  openDetail(targetId)
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
const transientZone = computed(() => queueItems())
const visibleItems = computed(() => statusZone.value.concat(transientZone.value))
const orderedItems = computed(() => statusItems().concat(queueItems()))
const detailItem = computed(() => items.value.find((item) => item.id === detailId.value) ?? null)
const openItem = computed(() => items.value.find((item) => item.id === openId.value) ?? null)
const hasPendingTask = computed(() => items.value.some((item) => item.taskState === 'pending'))

// Dynamic Island "hero" aggregation: at rest only the most relevant capsule of
// each zone is shown; the rest collapse behind a +N fold badge and reveal as a
// stack panel on hover/focus. See NotificationIslandHost.vue.
const statusHero = computed<NotificationItem | null>(() => statusZone.value[0] ?? null)
const transientHero = computed<NotificationItem | null>(() => transientZone.value[0] ?? null)
const statusFoldedCount = computed(() => Math.max(0, statusZone.value.length - 1))
const transientFoldedCount = computed(() => Math.max(0, transientZone.value.length - 1))

/** Rows shown in the open fold-stack panel, by zone. */
const stackItems = computed(() =>
  stackZone.value === 'status'
    ? statusZone.value
    : stackZone.value === 'transient'
      ? transientZone.value
      : [],
)

function mergeMessageOptions(
  input: MessageOptions,
  options?: Omit<NotificationOptions, 'message'>,
): MessageOptions {
  if (!options) return input
  return typeof input === 'string' ? { ...options, message: input } : { ...options, ...input }
}

const notify = {
  info: (input: MessageOptions, options?: Omit<NotificationOptions, 'message'>) =>
    add('transient', 'info', mergeMessageOptions(input, options)),
  success: (input: MessageOptions, options?: Omit<NotificationOptions, 'message'>) =>
    add('transient', 'success', mergeMessageOptions(input, options)),
  warning: (input: MessageOptions, options?: Omit<NotificationOptions, 'message'>) =>
    add('transient', 'warning', mergeMessageOptions(input, options)),
  error: (input: unknown, options?: Omit<NotificationOptions, 'message'>) =>
    addError('transient', input, options),
  task: startTask,
  update,
}

/**
 * Persistent, state-driven conditions. Keyed items replicate to sibling windows
 * only when `broadcast: true` is explicitly passed; the owning state source must
 * clear them with `dismissByKey` on recovery.
 */
const status = {
  info: (input: MessageOptions, options?: Omit<NotificationOptions, 'message'>) =>
    addStatus('info', mergeMessageOptions(input, options)),
  success: (input: MessageOptions, options?: Omit<NotificationOptions, 'message'>) =>
    addStatus('success', mergeMessageOptions(input, options)),
  warning: (input: MessageOptions, options?: Omit<NotificationOptions, 'message'>) =>
    addStatus('warning', mergeMessageOptions(input, options)),
  error: (input: unknown, options?: Omit<NotificationOptions, 'message'>) => {
    if (isErrorOptions(input)) {
      const inferredCategory =
        input.category ?? (isConnectionLevelError(input.message) ? 'connection' : undefined)
      return addStatus('error', {
        ...input,
        ...(inferredCategory ? { category: inferredCategory } : {}),
        message: normalizeError(input.message, input.title),
      })
    }
    const inferredCategory =
      options?.category ?? (isConnectionLevelError(input) ? 'connection' : undefined)
    return addStatus('error', {
      ...options,
      ...(inferredCategory ? { category: inferredCategory } : {}),
      details: options?.details ?? apiErrorDetails(input),
      message: normalizeError(input, options?.title),
    })
  },
}

/** Back-compat alias — `banner.*` was the previous name for the status class. */
const banner = status

/**
 * Test-only: force-remove every live item (ignoring lifecycle ownership),
 * clear all timers and history. `dismissAll` intentionally preserves
 * source-owned items, so tests need this hard reset between cases.
 */
export function __resetNotificationsForTests(): void {
  for (const timer of timers.values()) {
    if (timer.handle) globalThis.clearTimeout(timer.handle)
  }
  timers.clear()
  clearHoverOpenTimer()
  clearHoverCloseTimer()
  globalConnectionBlocked.value = false
  items.value = []
  history.value = []
  primaryId.value = null
  detailId.value = null
  hoveredId.value = null
  openId.value = null
  stackZone.value = null
  overflowOpen.value = false
  actionStates.value = {}
}

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
    statusHero,
    transientHero,
    statusFoldedCount,
    transientFoldedCount,
    stackZone,
    stackItems,
    detailItem,
    openItem,
    hasPendingTask,
    notify,
    status,
    banner,
    dismiss,
    dismissByKey,
    dismissByCategory,
    dismissAll,
    clearHistory,
    openDetail,
    closeDetail,
    setHovered,
    toggleOpen,
    closeOpen,
    openStack,
    closeStack,
    toggleStack,
    toggleOverflow,
    closeOverflow,
    hoverCapsuleEnter,
    hoverCapsuleLeave,
    hoverHeroEnter,
    hoverCardEnter,
    hoverCardLeave,
    openNotification,
    runAction,
    update,
    pause,
    resume,
    confirm,
    isConnectionLevelError,
    setGlobalConnectionBlocked,
    // Errors surfaced outside a component can still offer recovery when the
    // caller passes the ApiError straight through.
    recover: (error: unknown) => recoveryActionFor(error),
  }
}
