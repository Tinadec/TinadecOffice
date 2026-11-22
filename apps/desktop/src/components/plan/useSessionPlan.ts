import { computed, onBeforeUnmount, ref, watch, type Ref } from 'vue'
import { api } from '@/api'
import { subscribeToSessionEvents } from '@/lib/sessionEventBus'
import { useHostAccess } from '@/lib/hostAccess'

export type SessionPlanStatus = 'draft' | 'approved' | 'active' | 'completed' | 'blocked'
export type SessionPlanStepStatus = 'pending' | 'in_progress' | 'completed' | 'blocked' | 'failed' | 'skipped'

export interface SessionPlanArtifactDto {
  kind: 'file' | 'worktree' | 'summary' | 'attachment' | string
  label: string
  path?: string | null
  ref_id?: string | null
}

export interface SessionPlanStepDto {
  id: string
  title: string
  description?: string | null
  status: SessionPlanStepStatus
  depends_on: string[]
  bound_task_key?: string | null
  assigned_agent_slug?: string | null
  verification_criteria: string[]
  artifacts: SessionPlanArtifactDto[]
  summary?: string | null
  updated_at?: string | null
}

export interface SessionPlanPhaseDto {
  id: string
  title: string
  description?: string | null
  order: number
  status: SessionPlanStepStatus
  steps: SessionPlanStepDto[]
}

export interface SessionPlanProgressDto {
  total_steps: number
  completed_steps: number
  in_progress_steps: number
  blocked_steps: number
  failed_steps: number
  pending_steps: number
}

export interface SessionPlanDto {
  id: string
  session_id: string
  title: string
  goal?: string | null
  status: SessionPlanStatus
  revision: number
  approved_at?: string | null
  last_run_id?: string | null
  updated_at: string
  phases: SessionPlanPhaseDto[]
  progress: SessionPlanProgressDto
}

export interface SessionPlanStepPatchInput {
  id: string
  status?: SessionPlanStepStatus
  title?: string
  description?: string | null
  summary?: string | null
  assigned_agent_slug?: string | null
  bound_task_key?: string | null
  verification_criteria?: string[]
  depends_on?: string[]
  artifacts?: SessionPlanArtifactDto[]
}

export interface SessionPlanAddStepInput {
  phase_id?: string | null
  id?: string | null
  title: string
  status?: SessionPlanStepStatus
  depends_on?: string[]
  assigned_agent_slug?: string | null
  bound_task_key?: string | null
  verification_criteria?: string[]
}

export interface PatchSessionPlanInput {
  title?: string
  goal?: string | null
  status?: SessionPlanStatus
  phases?: SessionPlanPhaseDto[]
  step_updates?: SessionPlanStepPatchInput[]
  add_steps?: SessionPlanAddStepInput[]
  expected_revision?: number
}

export interface PutSessionPlanInput {
  title?: string
  goal?: string | null
  status?: SessionPlanStatus
  phases: SessionPlanPhaseDto[]
  expected_revision?: number
}

interface SessionPlanApiBridge {
  getSessionPlan?: (sessionId: string, options?: { signal?: AbortSignal }) => Promise<SessionPlanDto>
  saveSessionPlan?: (sessionId: string, input: PutSessionPlanInput, etag?: string | number | null) => Promise<SessionPlanDto>
  putSessionPlan?: (sessionId: string, input: PutSessionPlanInput) => Promise<SessionPlanDto>
  patchSessionPlan?: (sessionId: string, input: PatchSessionPlanInput, etag?: string | number | null) => Promise<SessionPlanDto>
  approveSessionPlan?: (sessionId: string, input?: { expected_revision?: number }, etag?: string | number | null) => Promise<SessionPlanDto>
}

const EMPTY_PLAN_ID = '00000000-0000-0000-0000-000000000000'

function planApi(): SessionPlanApiBridge {
  return api as unknown as SessionPlanApiBridge
}

function isPreconditionFailed(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false
  const status = (error as { status?: number }).status
  if (status === 412) return true
  const message = error instanceof Error ? error.message : String(error)
  return /\b412\b|precondition|revision/i.test(message)
}

export function isSessionPlanPopulated(plan: SessionPlanDto | null | undefined): boolean {
  if (!plan) return false
  if (plan.phases.length > 0) return true
  return Boolean(plan.id && plan.id !== EMPTY_PLAN_ID && (plan.title.trim() || plan.goal?.trim()))
}

export function computePlanProgress(plan: SessionPlanDto | null): SessionPlanProgressDto {
  if (!plan) {
    return {
      total_steps: 0,
      completed_steps: 0,
      in_progress_steps: 0,
      blocked_steps: 0,
      failed_steps: 0,
      pending_steps: 0,
    }
  }
  if (plan.progress && typeof plan.progress.total_steps === 'number') {
    return plan.progress
  }
  const steps = plan.phases.flatMap((phase) => phase.steps)
  return {
    total_steps: steps.length,
    completed_steps: steps.filter((s) => s.status === 'completed').length,
    in_progress_steps: steps.filter((s) => s.status === 'in_progress').length,
    blocked_steps: steps.filter((s) => s.status === 'blocked').length,
    failed_steps: steps.filter((s) => s.status === 'failed').length,
    pending_steps: steps.filter((s) => s.status === 'pending').length,
  }
}

export function useSessionPlan(sessionId: Ref<string | null | undefined>) {
  const { canAccessBackend } = useHostAccess()
  const plan = ref<SessionPlanDto | null>(null)
  const loading = ref(false)
  const mutating = ref(false)
  const error = ref('')
  const conflictNotice = ref(false)

  let loadEpoch = 0
  let abortController: AbortController | null = null
  let unsubscribeEvents: (() => void) | null = null

  const progress = computed(() => computePlanProgress(plan.value))
  const hasPlan = computed(() => isSessionPlanPopulated(plan.value))
  const progressPercent = computed(() => {
    const total = progress.value.total_steps
    if (total <= 0) return 0
    return Math.min(100, Math.max(0, Math.round((progress.value.completed_steps / total) * 100)))
  })
  const canApprove = computed(() => Boolean(plan.value && hasPlan.value && plan.value.status === 'draft'))

  async function refresh(silent = false): Promise<void> {
    const id = sessionId.value?.trim() || null
    abortController?.abort()
    abortController = null
    if (!id || !canAccessBackend.value) {
      if (!id) plan.value = null
      loading.value = false
      return
    }
    const bridge = planApi()
    if (typeof bridge.getSessionPlan !== 'function') {
      // Partial test mock or API not wired yet: degrade cleanly without firing unmocked fetch.
      loading.value = false
      return
    }
    const epoch = ++loadEpoch
    const controller = new AbortController()
    abortController = controller
    if (!silent) loading.value = true
    error.value = ''
    try {
      const loaded = await bridge.getSessionPlan(id, { signal: controller.signal })
      if (epoch !== loadEpoch || controller.signal.aborted) return
      plan.value = loaded
      conflictNotice.value = false
    } catch (err) {
      if (epoch !== loadEpoch || controller.signal.aborted) return
      // 404 for unsaved/new session simply means no plan yet.
      if ((err as { status?: number })?.status === 404) {
        plan.value = null
        return
      }
      error.value = err instanceof Error ? err.message : String(err)
    } finally {
      if (epoch === loadEpoch) {
        loading.value = false
        if (abortController === controller) abortController = null
      }
    }
  }

  async function approvePlan(): Promise<boolean> {
    const id = sessionId.value?.trim()
    const bridge = planApi()
    if (!id || !plan.value || typeof bridge.approveSessionPlan !== 'function') return false
    mutating.value = true
    error.value = ''
    conflictNotice.value = false
    try {
      const updated = await bridge.approveSessionPlan(id, { expected_revision: plan.value.revision })
      plan.value = updated
      return true
    } catch (err) {
      if (isPreconditionFailed(err)) {
        conflictNotice.value = true
        await refresh(true)
        return false
      }
      error.value = err instanceof Error ? err.message : String(err)
      return false
    } finally {
      mutating.value = false
    }
  }

  async function patchPlan(input: Omit<PatchSessionPlanInput, 'expected_revision'>): Promise<boolean> {
    const id = sessionId.value?.trim()
    const bridge = planApi()
    if (!id || typeof bridge.patchSessionPlan !== 'function') return false
    mutating.value = true
    error.value = ''
    conflictNotice.value = false
    try {
      const updated = await bridge.patchSessionPlan(id, {
        ...input,
        ...(plan.value ? { expected_revision: plan.value.revision } : {}),
      })
      plan.value = updated
      return true
    } catch (err) {
      if (isPreconditionFailed(err)) {
        conflictNotice.value = true
        await refresh(true)
        return false
      }
      error.value = err instanceof Error ? err.message : String(err)
      return false
    } finally {
      mutating.value = false
    }
  }

  async function updateStepStatus(stepId: string, status: SessionPlanStepStatus): Promise<boolean> {
    return patchPlan({ step_updates: [{ id: stepId, status }] })
  }

  async function addStep(input: SessionPlanAddStepInput): Promise<boolean> {
    const title = input.title.trim()
    if (!title) return false
    return patchPlan({ add_steps: [{ ...input, title }] })
  }

  function setupSubscription(id: string | null) {
    unsubscribeEvents?.()
    unsubscribeEvents = null
    if (!id) return
    unsubscribeEvents = subscribeToSessionEvents(
      (event) => {
        if (event.session_id && event.session_id !== id) return
        if (event.type === 'session.plan.updated') {
          const payload = (event.payload ?? {}) as { plan?: SessionPlanDto; revision?: number }
          if (payload.plan && payload.plan.session_id === id) {
            if (!plan.value || payload.plan.revision >= plan.value.revision) {
              plan.value = payload.plan
              conflictNotice.value = false
              error.value = ''
            }
          } else {
            void refresh(true)
          }
          return
        }
        if (event.type === 'plan.updated') {
          void refresh(true)
        }
      },
      { scope: computed(() => id) },
    )
  }

  watch(
    [sessionId, canAccessBackend],
    ([nextId]) => {
      const normalized = nextId?.trim() || null
      setupSubscription(normalized)
      void refresh()
    },
    { immediate: true },
  )

  onBeforeUnmount(() => {
    abortController?.abort()
    unsubscribeEvents?.()
    unsubscribeEvents = null
  })

  return {
    plan,
    progress,
    progressPercent,
    hasPlan,
    canApprove,
    loading,
    mutating,
    error,
    conflictNotice,
    refresh,
    approvePlan,
    patchPlan,
    updateStepStatus,
    addStep,
  }
}
