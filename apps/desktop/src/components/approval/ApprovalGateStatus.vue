<script setup lang="ts">
/**
 * Where a delegated approval stands (delegate-* permission modes): which gate was asked, what it
 * decided and why. Renders nothing for an approval that was never delegated. Loads once via REST
 * and listens to `approval.gate_updated` on the shared session SSE bus, with an ultra-low-frequency
 * 30s safety fallback only while still evaluating without any SSE event.
 */
import { computed, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type ApprovalGateDto, type ApprovalGatesDto, type EventEnvelope } from '@/api'
import { subscribeToSessionEvents } from '@/lib/sessionEventBus'

const props = defineProps<{ approvalId: string }>()
const { t, te } = useI18n()
const gates = ref<ApprovalGatesDto | null>(null)
let fallbackTimer: ReturnType<typeof setTimeout> | null = null
let disposed = false
let generation = 0
let sseDelivered = false

const SAFETY_FALLBACK_MS = 30_000

function clearFallbackTimer() {
  if (fallbackTimer !== null) {
    clearTimeout(fallbackTimer)
    fallbackTimer = null
  }
}

function scheduleSafetyFallback() {
  clearFallbackTimer()
  if (!disposed && !sseDelivered && gates.value?.status === 'evaluating') {
    fallbackTimer = setTimeout(() => {
      fallbackTimer = null
      void load()
    }, SAFETY_FALLBACK_MS)
  }
}

async function load() {
  const asked = ++generation
  try {
    const result = await api.getApprovalGates(props.approvalId)
    if (disposed || asked !== generation) return
    gates.value = result
  } catch {
    // Transient read error; SSE updates or safety fallback can still populate later.
  }
  scheduleSafetyFallback()
}

function handleSessionEvent(event: EventEnvelope) {
  if (disposed || event.type !== 'approval.gate_updated') return
  const payload = event.payload as Record<string, unknown> | null | undefined
  if (!payload || payload.approval_id !== props.approvalId) return
  sseDelivered = true
  generation += 1
  clearFallbackTimer()
  if (typeof payload.status === 'string' && Array.isArray(payload.gates)) {
    gates.value = {
      approval_id: props.approvalId,
      run_id: event.run_id ?? gates.value?.run_id ?? null,
      status: payload.status,
      gates: payload.gates as ApprovalGateDto[],
    }
  } else {
    void load()
  }
}

const unsubscribe = subscribeToSessionEvents(handleSessionEvent)

watch(
  () => props.approvalId,
  () => {
    sseDelivered = false
    clearFallbackTimer()
    gates.value = null
    void load()
  },
  { immediate: true },
)

onUnmounted(() => {
  disposed = true
  clearFallbackTimer()
  unsubscribe()
})

const rows = computed(() =>
  (gates.value?.gates ?? []).map((gate, idx) => ({
    key: `${gate.gate_index ?? idx}-${gate.gate_kind}`,
    who: gate.gate_kind === 'conversation_identity' ? t('approval.gates.conversation') : t('approval.gates.reviewer'),
    decider: gate.decider_agent ?? null,
    status: gate.status,
    statusLabel: te(`approval.gates.status.${gate.status}`) ? t(`approval.gates.status.${gate.status}`) : gate.status,
    reason: gate.reason ?? null,
  })),
)
</script>

<template>
  <div v-if="gates" class="approval-gates" :data-status="gates.status" data-testid="approval-gate-status" role="status">
    <span class="approval-gates-title">{{ t('approval.gates.title') }}</span>
    <ul>
      <li v-for="row in rows" :key="row.key" :data-gate-status="row.status">
        <strong>{{ row.who }}</strong>
        <code v-if="row.decider" class="approval-gates-decider">{{ row.decider }}</code>
        <span class="approval-gates-state">{{ row.statusLabel }}</span>
        <span v-if="row.reason" class="approval-gates-reason">{{ row.reason }}</span>
      </li>
    </ul>
    <span v-if="gates.status === 'escalated'" class="approval-gates-note">{{ t('approval.gates.escalated') }}</span>
    <span v-else-if="gates.status === 'superseded'" class="approval-gates-note">{{ t('approval.gates.superseded') }}</span>
  </div>
</template>

<style scoped>
.approval-gates { display: flex; flex-direction: column; gap: 2px; margin: 4px 0; font-size: 11px; color: var(--text-secondary); }
.approval-gates ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 2px; }
.approval-gates li { display: flex; flex-wrap: wrap; align-items: baseline; gap: 6px; }
.approval-gates-title { font-weight: 600; color: var(--text-muted); }
.approval-gates-decider { font-size: 10px; color: var(--text-muted); }
.approval-gates-reason { flex-basis: 100%; overflow-wrap: anywhere; color: var(--text-chat-muted); }
li[data-gate-status='approved'] .approval-gates-state { color: var(--accent-success); }
li[data-gate-status='rejected'] .approval-gates-state { color: var(--accent-danger); }
li[data-gate-status='escalated'] .approval-gates-state,
.approval-gates-note { color: var(--accent-warning); }
</style>
