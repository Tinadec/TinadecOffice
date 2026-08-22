import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import {
  api,
  type RecoveryDecisionInput,
  type UserToolActionDto,
} from '@/api'
import {
  isUserToolActionTerminal,
  userToolActionNeedsDecision,
} from '@/userToolAction'

/**
 * Single owner of UserToolAction client state, keyed by action.id.
 *
 * Core owns the durable action; this store only mirrors what the API returns,
 * coordinates polling for in-flight actions, and funnels every decision
 * (permission, action approval, recovery) through one place so components
 * never keep divergent local copies.
 */
export const useUserActionStore = defineStore('userAction', () => {
  const actions = ref<Record<string, UserToolActionDto>>({})
  const loadingIds = ref<Set<string>>(new Set())
  const errors = ref<Record<string, string>>({})

  const pollTimers = new Map<string, ReturnType<typeof setInterval>>()
  const POLL_INTERVAL_MS = 2000
  const POLL_MAX_AGE_MS = 10 * 60 * 1000

  const allActions = computed(() =>
    Object.values(actions.value).sort((a, b) => b.created_at.localeCompare(a.created_at)),
  )
  const pendingDecisions = computed(() => allActions.value.filter((a) => userToolActionNeedsDecision(a.status)))
  const activeActions = computed(() => allActions.value.filter((a) => !isUserToolActionTerminal(a.status)))

  function get(actionId: string): UserToolActionDto | null {
    return actions.value[actionId] ?? null
  }

  function put(action: UserToolActionDto): void {
    actions.value = { ...actions.value, [action.id]: action }
    delete errors.value[action.id]
  }

  async function create(input: {
    project_id: string
    tool_id: string
    params?: Record<string, unknown> | null
    idempotency_key?: string | null
  }): Promise<UserToolActionDto> {
    const action = await api.createUserToolAction(input)
    put(action)
    syncPolling(action)
    return action
  }

  async function refresh(actionId: string): Promise<UserToolActionDto | null> {
    if (loadingIds.value.has(actionId)) return actions.value[actionId] ?? null
    loadingIds.value = new Set(loadingIds.value).add(actionId)
    try {
      const fresh = await api.getUserToolAction(actionId)
      put(fresh)
      syncPolling(fresh)
      return fresh
    } catch (e) {
      errors.value = { ...errors.value, [actionId]: e instanceof Error ? e.message : String(e) }
      return actions.value[actionId] ?? null
    } finally {
      const next = new Set(loadingIds.value)
      next.delete(actionId)
      loadingIds.value = next
    }
  }

  async function decidePermission(actionId: string, decision: 'approved' | 'rejected', reason?: string): Promise<UserToolActionDto | null> {
    const current = get(actionId)
    if (!current?.permission_request_id) throw new Error(`action ${actionId} has no permission_request_id`)
    // The permission decision lands on the governance record; the action's own
    // status advances afterwards (awaiting_user -> awaiting_approval or blocked).
    await api.decidePermissionRequest(current.permission_request_id, {
      approve: decision === 'approved',
      reason: reason ?? null,
    })
    return refresh(actionId)
  }

  async function decideApproval(actionId: string, decision: 'approved' | 'rejected', _reason?: string): Promise<UserToolActionDto | null> {
    const current = get(actionId)
    if (!current?.action_approval_id) throw new Error(`action ${actionId} has no action_approval_id`)
    // api.decideApproval posts { decision }; the Core endpoint binds the
    // approval to the caller's principal, so no reason field crosses here yet.
    await api.decideApproval(current.action_approval_id, decision)
    return refresh(actionId)
  }

  async function resume(actionId: string): Promise<UserToolActionDto | null> {
    const fresh = await api.resumeUserToolAction(actionId)
    put(fresh)
    syncPolling(fresh)
    return fresh
  }

  async function overrideSnapshot(actionId: string, reason: string): Promise<UserToolActionDto | null> {
    const fresh = await api.overrideUserToolActionSnapshot(actionId, reason)
    put(fresh)
    syncPolling(fresh)
    return fresh
  }

  async function decideRecovery(actionId: string, input: RecoveryDecisionInput): Promise<UserToolActionDto | null> {
    const fresh = await api.decideUserToolActionRecovery(actionId, input)
    put(fresh)
    stopPolling(actionId)
    return fresh
  }

  /**
   * Poll while the action sits in a non-terminal, non-decision state
   * (snapshot_required / running). Decision states wait for a human; terminal
   * states never change again.
   */
  function syncPolling(action: UserToolActionDto): void {
    const shouldPoll =
      !isUserToolActionTerminal(action.status) && !userToolActionNeedsDecision(action.status)
    if (shouldPoll && !pollTimers.has(action.id)) {
      const startedAt = Date.now()
      const timer = setInterval(async () => {
        if (Date.now() - startedAt > POLL_MAX_AGE_MS) {
          stopPolling(action.id)
          return
        }
        await refresh(action.id)
      }, POLL_INTERVAL_MS)
      pollTimers.set(action.id, timer)
    } else if (!shouldPoll) {
      stopPolling(action.id)
    }
  }

  function stopPolling(actionId: string): void {
    const timer = pollTimers.get(actionId)
    if (timer) {
      clearInterval(timer)
      pollTimers.delete(actionId)
    }
  }

  function stopAllPolling(): void {
    for (const id of [...pollTimers.keys()]) stopPolling(id)
  }

  function forget(actionId: string): void {
    stopPolling(actionId)
    const next = { ...actions.value }
    delete next[actionId]
    actions.value = next
  }

  /** Hydrate from the durable list (e.g. after reconnect / page reload). */
  async function hydrate(statusFilter?: string): Promise<void> {
    const list = await api.listUserToolActions(statusFilter)
    for (const action of list) put(action)
  }

  return {
    actions,
    errors,
    allActions,
    pendingDecisions,
    activeActions,
    get,
    create,
    refresh,
    decidePermission,
    decideApproval,
    resume,
    overrideSnapshot,
    decideRecovery,
    stopAllPolling,
    forget,
    hydrate,
  }
})
