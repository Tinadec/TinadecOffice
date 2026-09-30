<script setup lang="ts">
/**
 * Where a delegated approval stands (delegate-* permission modes): which gate was asked, what it
 * decided and why. Renders nothing for an approval that was never delegated. Polls only while a
 * gate is still deciding, so a settled card costs nothing; the person's own buttons stay beside it
 * and always win.
 */
import { computed, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { api, type ApprovalGatesDto } from '@/api'

const props = defineProps<{ approvalId: string }>()
const { t, te } = useI18n()
const gates = ref<ApprovalGatesDto | null>(null)
let timer: ReturnType<typeof setTimeout> | null = null
let disposed = false
let generation = 0

const POLL_MS = 2000

async function load() {
  const asked = ++generation
  try {
    const result = await api.getApprovalGates(props.approvalId)
    // A late answer for an approval this card no longer shows is dropped.
    if (disposed || asked !== generation) return
    gates.value = result
  } catch {
    // Transient: the next poll (if any) tries again; the buttons do not depend on this.
  }
  schedule()
}

function schedule() {
  if (timer) clearTimeout(timer)
  timer = null
  if (!disposed && gates.value?.status === 'evaluating') timer = setTimeout(load, POLL_MS)
}

watch(() => props.approvalId, () => { gates.value = null; void load() }, { immediate: true })
onUnmounted(() => { disposed = true; if (timer) clearTimeout(timer) })

const rows = computed(() => (gates.value?.gates ?? []).map((gate) => ({
  key: `${gate.gate_index}-${gate.gate_kind}`,
  who: gate.gate_kind === 'conversation_identity' ? t('approval.gates.conversation') : t('approval.gates.reviewer'),
  decider: gate.decider_agent,
  status: gate.status,
  statusLabel: te(`approval.gates.status.${gate.status}`) ? t(`approval.gates.status.${gate.status}`) : gate.status,
  reason: gate.reason,
})))
</script>

<template>
  <div v-if="gates" class="approval-gates" :data-status="gates.status" role="status">
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
