<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { approvalSummary } from '@/userToolAction'
import { Activity, Eye, Gavel, ShieldAlert, ShieldCheck } from '@lucide/vue'
import { api, type ApprovalDto, type PermissionRequestDto, type SupervisionFindingDto } from '@/api'
import { useUserActionStore } from '@/stores/userAction'
import UserActionStatusBadge from '@/components/governance/UserActionStatusBadge.vue'
import PreAuthorizationDialog from '@/components/governance/PreAuthorizationDialog.vue'
import RuntimeInstancesPanel from '@/settings/sections/RuntimeInstancesPanel.vue'
import { useStatusLabels } from '@/lib/statusLabels'
import { useHostAccess } from '@/lib/hostAccess'

export type GovernanceBoardTab = 'decisions' | 'runtime'

const props = withDefaults(defineProps<{
  initialTab?: GovernanceBoardTab
  compact?: boolean
}>(), {
  initialTab: 'decisions',
  compact: false,
})

const { t, te } = useI18n()
const { riskLabel, severityLabel } = useStatusLabels()
const kindLabel = (kind: string) => (te(`approval.kind.${kind}`) ? t(`approval.kind.${kind}`) : kind)
const userActionStore = useUserActionStore()
const { canAccessBackend } = useHostAccess()

const activeTab = ref<GovernanceBoardTab>(props.initialTab)
watch(() => props.initialTab, (tab) => {
  activeTab.value = tab
})

const approvals = ref<ApprovalDto[]>([])
const permissions = ref<PermissionRequestDto[]>([])
const findings = ref<SupervisionFindingDto[]>([])
const runtimeCount = ref(0)
const runtimePanelRef = ref<InstanceType<typeof RuntimeInstancesPanel> | null>(null)
const loading = ref(false)
const loadError = ref<string | null>(null)
const deciding = ref<string | null>(null)
const preAuthOpen = ref(false)

const REFRESH_INTERVAL_MS = 12_000
let refreshTimer: ReturnType<typeof setInterval> | null = null
let loadInFlight = false

const openApprovals = computed(() => approvals.value.filter((a) => a.status === 'pending'))
const openPermissions = computed(() =>
  permissions.value.filter((p) => p.status === 'pending' || p.status.startsWith('awaiting')),
)
const openDecisionsCount = computed(() => openApprovals.value.length + openPermissions.value.length)

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

async function loadAll(background = false): Promise<void> {
  if (loadInFlight || !canAccessBackend.value) return
  loadInFlight = true
  if (!background) loading.value = true
  loadError.value = null
  try {
    const hasRuntimeApi = typeof (api as { listRuntimeInstances?: unknown }).listRuntimeInstances === 'function'
    const refreshRuntime = activeTab.value === 'runtime' && runtimePanelRef.value
      ? runtimePanelRef.value.loadRuntimeInstances(background)
      : hasRuntimeApi
        ? api.listRuntimeInstances().then((list) => {
            runtimeCount.value = Array.isArray(list) ? list.length : 0
          }).catch(() => undefined)
        : Promise.resolve()

    const [a, p] = await Promise.all([
      api.listApprovals(undefined, 'pending').catch(() => [] as ApprovalDto[]),
      api.listPermissionRequests().catch(() => [] as PermissionRequestDto[]),
      refreshRuntime,
    ])
    approvals.value = a.filter((x) => x.kind !== 'permission')
    permissions.value = [
      ...p,
      ...a.filter((x) => x.kind === 'permission').map((x) => permissionFromApprovalProjection(x)),
    ]
    await userActionStore.hydrate().catch(() => undefined)
    findings.value = []
  } catch (e) {
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    loadInFlight = false
    if (!background) loading.value = false
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

function openRecoveryCheck(actionId: string): void {
  if (typeof window === 'undefined') return
  window.dispatchEvent(new CustomEvent('tinadec:open-recovery-check', { detail: { actionId } }))
}

onMounted(() => {
  if (canAccessBackend.value) void loadAll()
  refreshTimer = setInterval(() => {
    if (!canAccessBackend.value || document.visibilityState !== 'visible') return
    void loadAll(true)
  }, REFRESH_INTERVAL_MS)
})

watch(canAccessBackend, (ready) => {
  if (ready) void loadAll(true)
})

onUnmounted(() => {
  if (refreshTimer !== null) {
    clearInterval(refreshTimer)
    refreshTimer = null
  }
})

defineExpose({ reload: loadAll })
</script>

<template>
  <div class="approval-board" :class="{ 'is-compact': compact }" data-testid="approval-board">
    <header class="approval-board__header">
      <div class="approval-board__tabs" role="tablist" data-testid="governance-board-tabs">
        <button
          type="button"
          role="tab"
          class="approval-board__tab"
          :class="{ active: activeTab === 'decisions' }"
          :aria-selected="activeTab === 'decisions'"
          data-testid="governance-tab-decisions"
          @click="activeTab = 'decisions'"
        >
          {{ t('governance.tabDecisions') }} ({{ openDecisionsCount }})
        </button>
        <button
          type="button"
          role="tab"
          class="approval-board__tab"
          :class="{ active: activeTab === 'runtime' }"
          :aria-selected="activeTab === 'runtime'"
          data-testid="governance-tab-runtime"
          @click="activeTab = 'runtime'"
        >
          <Activity :size="13" aria-hidden="true" />
          <span>{{ t('settings.runtimeInstances') }} ({{ runtimeCount }})</span>
        </button>
      </div>

      <div class="approval-board__header-actions">
        <button type="button" class="detail-dialog__btn" data-testid="open-pre-auth" @click="preAuthOpen = true">
          {{ t('governance.preAuthorize') }}
        </button>
        <button type="button" class="detail-dialog__btn" :disabled="loading" @click="void loadAll()">
          {{ loading ? t('common.loading') : t('common.refresh') }}
        </button>
      </div>
    </header>

    <div v-if="loadError" class="approval-board__error">{{ loadError }}</div>

    <div v-if="activeTab === 'decisions'" class="approval-board__columns">
      <section class="approval-board__column" data-testid="column-permissions">
        <h2><ShieldCheck :size="14" aria-hidden="true" /> {{ t('governance.permissions') }} ({{ openPermissions.length }})</h2>
        <p v-if="!openPermissions.length" class="approval-board__empty">{{ t('common.empty') }}</p>
        <article v-for="p in openPermissions" :key="p.id" class="approval-card">
          <div class="approval-card__title">{{ p.capability }} / {{ p.action }}</div>
          <div class="approval-card__meta">{{ t('governance.riskMeta', { risk: riskLabel(p.risk) }) }} · {{ p.resource }}</div>
          <div class="approval-card__actions">
            <button type="button" class="detail-dialog__btn" :disabled="deciding === p.id" @click="decidePermission(p.id, true)">
              {{ t('common.approve') }}
            </button>
            <button type="button" class="detail-dialog__btn detail-dialog__btn--danger" :disabled="deciding === p.id" @click="decidePermission(p.id, false)">
              {{ t('common.reject') }}
            </button>
          </div>
        </article>
      </section>

      <section class="approval-board__column" data-testid="column-approvals">
        <h2><Gavel :size="14" aria-hidden="true" /> {{ t('governance.actionApprovals') }} ({{ openApprovals.length }})</h2>
        <p v-if="!openApprovals.length" class="approval-board__empty">{{ t('common.empty') }}</p>
        <article v-for="a in openApprovals" :key="a.id" class="approval-card">
          <div class="approval-card__title">{{ approvalSummary(a, { t, te }) }}</div>
          <div class="approval-card__meta font-mono text-xs">{{ a.command ?? kindLabel(a.kind) }}</div>
          <UserActionStatusBadge v-if="a.governance_status" :status="a.governance_status" />
          <div class="approval-card__actions">
            <button type="button" class="detail-dialog__btn" :disabled="deciding === a.id" @click="decideApproval(a.id, 'approved')">
              {{ t('common.approve') }}
            </button>
            <button type="button" class="detail-dialog__btn detail-dialog__btn--danger" :disabled="deciding === a.id" @click="decideApproval(a.id, 'rejected')">
              {{ t('common.reject') }}
            </button>
            <button
              v-if="a.governance_status === 'outcome_unknown'"
              type="button"
              class="detail-dialog__btn"
              data-testid="open-recovery-check-btn"
              @click="openRecoveryCheck(a.id)"
            >
              <ShieldAlert :size="13" aria-hidden="true" />
              {{ t('governance.recoveryTitle') }}
            </button>
          </div>
        </article>
      </section>

      <section class="approval-board__column" data-testid="column-supervision">
        <h2><Eye :size="14" aria-hidden="true" /> {{ t('governance.supervision') }}</h2>
        <p v-if="!findings.length" class="approval-board__empty">
          {{ t('governance.supervisionEmpty') }}
        </p>
        <article v-for="f in findings" :key="f.id" class="approval-card">
          <div class="approval-card__title">{{ f.summary }}</div>
          <div class="approval-card__meta">{{ severityLabel(f.severity) }} · {{ f.category }}</div>
          <div class="approval-card__meta">{{ f.recommendation }}</div>
        </article>
      </section>
    </div>

    <section
      v-if="activeTab === 'runtime'"
      class="approval-board__runtime"
      data-testid="governance-runtime-section"
    >
      <RuntimeInstancesPanel
        ref="runtimePanelRef"
        :show-ceilings="false"
        :enable-auto-refresh="false"
        @count-changed="runtimeCount = $event"
      />
    </section>

    <PreAuthorizationDialog v-model:open="preAuthOpen" />
  </div>
</template>

<style scoped>
.approval-board {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
}

.approval-board__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.approval-board__header-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.approval-board__tabs {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.approval-board__tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 10px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
}

.approval-board__tab.active {
  border-color: var(--accent-primary);
  color: var(--text-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-raised));
}

.approval-board__runtime {
  padding: 12px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
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
  gap: 10px;
}

.approval-board.is-compact .approval-board__columns {
  grid-template-columns: 1fr;
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
  padding: 10px 12px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  min-height: 96px;
}

.approval-board__column h2 {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.approval-board__column h2 svg {
  flex-shrink: 0;
  color: var(--text-secondary);
}

.approval-board__empty {
  margin: 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.approval-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px;
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
  overflow-wrap: anywhere;
}

.approval-card__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
</style>
