<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ShieldCheck, Gavel, Eye } from '@lucide/vue'
import { api, type ApprovalDto, type PermissionRequestDto, type SupervisionFindingDto } from '@/api'
import { useUserActionStore } from '@/stores/userAction'
import UserActionStatusBadge from '@/components/governance/UserActionStatusBadge.vue'

/**
 * Three independent state machines, three columns (docs/app-core-ui.md §6):
 * - PermissionRequest: capability authorization (governance record)
 * - ActionApproval: one concrete high-risk action
 * - Supervision: quality verdicts (read-only here)
 *
 * Desktop never merges these facts; the board only renders Core projections.
 */
const { t } = useI18n()
const userActionStore = useUserActionStore()

const approvals = ref<ApprovalDto[]>([])
const permissions = ref<PermissionRequestDto[]>([])
const findings = ref<SupervisionFindingDto[]>([])
const loading = ref(false)
const loadError = ref<string | null>(null)
const deciding = ref<string | null>(null)

const openApprovals = computed(() => approvals.value.filter((a) => a.status === 'pending'))
const openPermissions = computed(() =>
  permissions.value.filter((p) => p.status === 'pending' || p.status.startsWith('awaiting')),
)

/** Project a permission-kind approval row into the permission column shape. */
function permissionFromApprovalProjection(x: ApprovalDto): PermissionRequestDto {
  return {
    id: x.id,
    tenant_id: '',
    workspace_id: '',
    subject_principal_id: '',
    capability: x.summary,
    action: x.command ?? x.kind,
    resource: x.cwd ?? '',
    risk: '',
    expected_cost: 0,
    status: x.status === 'pending' ? 'awaiting_user' : x.status,
    expires_at: '',
    created_at: x.created_at,
    updated_at: x.created_at,
  }
}

async function loadAll(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    const [a, p] = await Promise.all([
      api.listApprovals(undefined, 'pending').catch(() => [] as ApprovalDto[]),
      api.listPermissionRequests().catch(() => [] as PermissionRequestDto[]),
    ])
    // /approvals is a heterogeneous projection (docs/app-core-ui.md §4.5):
    // split by kind instead of treating pending as one merged gate.
    approvals.value = a.filter((x) => x.kind !== 'permission')
    permissions.value = [
      ...p,
      ...a.filter((x) => x.kind === 'permission').map((x) => permissionFromApprovalProjection(x)),
    ]
    await userActionStore.hydrate().catch(() => undefined)
    // Supervision findings endpoint is a projection placeholder; render what exists.
    findings.value = []
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

async function decideApproval(id: string, decision: 'approved' | 'rejected'): Promise<void> {
  deciding.value = id
  try {
    await api.decideApproval(id, decision, null)
    approvals.value = approvals.value.filter((a) => a.id !== id)
  } finally {
    deciding.value = null
  }
}

async function decidePermission(id: string, approve: boolean): Promise<void> {
  deciding.value = id
  try {
    await api.decidePermissionRequest(id, { approve, reason: null })
    permissions.value = permissions.value.filter((p) => p.id !== id)
  } finally {
    deciding.value = null
  }
}

onMounted(loadAll)
</script>

<template>
  <div class="approval-board" data-testid="approval-board">
    <header class="approval-board__header">
      <h1>{{ t('governance.boardTitle', 'Governance decisions') }}</h1>
      <button type="button" class="detail-dialog__btn" :disabled="loading" @click="loadAll">
        {{ loading ? t('common.loading', 'Loading…') : t('common.refresh', 'Refresh') }}
      </button>
    </header>

    <div v-if="loadError" class="approval-board__error">{{ loadError }}</div>

    <div class="approval-board__columns">
      <!-- Column 1: PermissionRequest -->
      <section class="approval-board__column" data-testid="column-permissions">
        <h2><ShieldCheck class="size-4" /> {{ t('governance.permissions', 'Capability requests') }} ({{ openPermissions.length }})</h2>
        <p v-if="!openPermissions.length" class="approval-board__empty">{{ t('common.empty', 'Nothing waiting.') }}</p>
        <article v-for="p in openPermissions" :key="p.id" class="approval-card">
          <div class="approval-card__title">{{ p.capability }} / {{ p.action }}</div>
          <div class="approval-card__meta">risk: {{ p.risk }} · {{ p.resource }}</div>
          <div class="approval-card__actions">
            <button type="button" class="detail-dialog__btn" :disabled="deciding === p.id" @click="decidePermission(p.id, true)">
              {{ t('common.approve', 'Approve') }}
            </button>
            <button type="button" class="detail-dialog__btn detail-dialog__btn--danger" :disabled="deciding === p.id" @click="decidePermission(p.id, false)">
              {{ t('common.reject', 'Reject') }}
            </button>
          </div>
        </article>
      </section>

      <!-- Column 2: ActionApproval + user actions -->
      <section class="approval-board__column" data-testid="column-approvals">
        <h2><Gavel class="size-4" /> {{ t('governance.actionApprovals', 'Action approvals') }} ({{ openApprovals.length }})</h2>
        <p v-if="!openApprovals.length" class="approval-board__empty">{{ t('common.empty', 'Nothing waiting.') }}</p>
        <article v-for="a in openApprovals" :key="a.id" class="approval-card">
          <div class="approval-card__title">{{ a.summary }}</div>
          <div class="approval-card__meta font-mono text-xs">{{ a.command ?? a.kind }}</div>
          <UserActionStatusBadge v-if="a.governance_status" :status="a.governance_status" />
          <div class="approval-card__actions">
            <button type="button" class="detail-dialog__btn" :disabled="deciding === a.id" @click="decideApproval(a.id, 'approved')">
              {{ t('common.approve', 'Approve') }}
            </button>
            <button type="button" class="detail-dialog__btn detail-dialog__btn--danger" :disabled="deciding === a.id" @click="decideApproval(a.id, 'rejected')">
              {{ t('common.reject', 'Reject') }}
            </button>
          </div>
        </article>
      </section>

      <!-- Column 3: Supervision (read-only) -->
      <section class="approval-board__column" data-testid="column-supervision">
        <h2><Eye class="size-4" /> {{ t('governance.supervision', 'Supervision review') }}</h2>
        <p v-if="!findings.length" class="approval-board__empty">
          {{ t('governance.supervisionEmpty', 'No supervision findings projected yet.') }}
        </p>
        <article v-for="f in findings" :key="f.id" class="approval-card">
          <div class="approval-card__title">{{ f.summary }}</div>
          <div class="approval-card__meta">{{ f.severity }} · {{ f.category }}</div>
          <div class="approval-card__meta">{{ f.recommendation }}</div>
        </article>
      </section>
    </div>
  </div>
</template>

<style scoped>
.approval-board {
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 24px 20px;
  max-width: 1200px;
  margin: 0 auto;
}

.approval-board__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.approval-board__header h1 {
  font-size: 17px;
  font-weight: 700;
}

.approval-board__error {
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--bg-status-danger);
  color: var(--accent-danger);
  font-size: 13px;
}

.approval-board__columns {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
}

@media (max-width: 900px) {
  .approval-board__columns {
    grid-template-columns: 1fr;
  }
}

.approval-board__column {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border-radius: 10px;
  background: var(--surface-section);
  min-height: 220px;
}

.approval-board__column h2 {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  font-weight: 700;
  color: var(--text-primary);
}

.approval-board__empty {
  font-size: 12px;
  color: var(--text-secondary);
}

.approval-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px 12px;
  border-radius: 8px;
  background: var(--surface-raised);
}

.approval-card__title {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.approval-card__meta {
  font-size: 11px;
  color: var(--text-secondary);
}

.approval-card__actions {
  display: flex;
  gap: 8px;
}
</style>
