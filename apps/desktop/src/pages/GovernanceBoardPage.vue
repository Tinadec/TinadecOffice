<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { approvalSummary } from '@/userToolAction'
import { Activity, ShieldCheck, Gavel, Eye } from '@lucide/vue'
import { api, type ApprovalDto, type PermissionRequestDto, type SupervisionFindingDto } from '@/api'
import { useUserActionStore } from '@/stores/userAction'
import UserActionStatusBadge from '@/components/governance/UserActionStatusBadge.vue'
import PreAuthorizationDialog from '@/components/governance/PreAuthorizationDialog.vue'
import CommandPaletteButton from '@/components/CommandPaletteButton.vue'
import RuntimeInstancesPanel from '@/settings/sections/RuntimeInstancesPanel.vue'
import { useStatusLabels } from '@/lib/statusLabels'

/**
 * Three independent state machines, three columns (docs/app-core-ui.md §6):
 * - PermissionRequest: capability authorization (governance record)
 * - ActionApproval: one concrete high-risk action
 * - Supervision: quality verdicts (read-only here)
 *
 * Plus live Runtime Instances monitoring (migrated out of Settings so Settings
 * stays configuration-only while /governance owns operational supervision).
 *
 * Desktop never merges these facts; the board only renders Core projections.
 */
type GovernanceBoardTab = 'decisions' | 'runtime'

/** Read a query value from either the hash route (`#/governance?tab=runtime`) or the path route. */
function queryValue(key: string): string | null {
  if (typeof window === 'undefined') return null
  const query = window.location.hash.split('?')[1] ?? window.location.search.slice(1)
  const value = new URLSearchParams(query).get(key)
  return value && value.trim() ? value.trim() : null
}

function initialBoardTab(): GovernanceBoardTab {
  return queryValue('tab') === 'runtime' ? 'runtime' : 'decisions'
}

/**
 * `/governance?session_id=…` opens the runtime tab filtered to that session. The runtime
 * panel is the only surface that can act on a run, so a link that names a session but
 * lands on the decisions tab makes the operator find it by hand.
 */
function initialSessionId(): string {
  const id = queryValue('session_id') ?? queryValue('session')
  return id ?? ''
}

const { t, te } = useI18n()
const { riskLabel, severityLabel } = useStatusLabels()
const kindLabel = (kind: string) => (te(`approval.kind.${kind}`) ? t(`approval.kind.${kind}`) : kind)
const userActionStore = useUserActionStore()

const requestedSessionId = initialSessionId()
const activeTab = ref<GovernanceBoardTab>(requestedSessionId ? 'runtime' : initialBoardTab())
const approvals = ref<ApprovalDto[]>([])
const permissions = ref<PermissionRequestDto[]>([])
const findings = ref<SupervisionFindingDto[]>([])
const runtimeCount = ref(0)
const runtimePanelRef = ref<InstanceType<typeof RuntimeInstancesPanel> | null>(null)
const loading = ref(false)
const loadError = ref<string | null>(null)
const deciding = ref<string | null>(null)
const preAuthOpen = ref(false)

// Lightweight board refresh: approvals/permissions/runtime instances change out-of-band
// (agent runs mint new requests), so poll on an interval, pause while the page is
// hidden, and never overlap an in-flight load. Background ticks leave the
// Refresh button's loading state alone.
const REFRESH_INTERVAL_MS = 12_000
let refreshTimer: ReturnType<typeof setInterval> | null = null
let loadInFlight = false

const openApprovals = computed(() => approvals.value.filter((a) => a.status === 'pending'))
const openPermissions = computed(() =>
  permissions.value.filter((p) => p.status === 'pending' || p.status.startsWith('awaiting')),
)
const openDecisionsCount = computed(() => openApprovals.value.length + openPermissions.value.length)

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

async function loadAll(background = false): Promise<void> {
  if (loadInFlight) return
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

onMounted(() => {
  void loadAll()
  refreshTimer = setInterval(() => {
    if (document.visibilityState !== 'visible') return
    void loadAll(true)
  }, REFRESH_INTERVAL_MS)
})

onUnmounted(() => {
  if (refreshTimer !== null) {
    clearInterval(refreshTimer)
    refreshTimer = null
  }
})
</script>

<template>
  <div class="approval-board" data-testid="approval-board">
    <header class="approval-board__header">
      <h1>{{ t('governance.boardTitle') }}</h1>
      <div class="approval-board__header-actions">
        <CommandPaletteButton />
        <button type="button" class="detail-dialog__btn" data-testid="open-pre-auth" @click="preAuthOpen = true">
          {{ t('governance.preAuthorize') }}
        </button>
        <button type="button" class="detail-dialog__btn" :disabled="loading" @click="void loadAll()">
          {{ loading ? t('common.loading') : t('common.refresh') }}
        </button>
      </div>
    </header>

    <div class="approval-board__tabs" role="tablist" data-testid="governance-board-tabs">
      <button
        type="button"
        role="tab"
        class="approval-board__tab"
        :class="{ active: activeTab === 'decisions' }"
        :aria-selected="activeTab === 'decisions'"
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
          <Activity class="size-3.5" />
          <span>{{ t('settings.runtimeInstances') }} ({{ runtimeCount }})</span>
        </button>
    </div>

    <div v-if="loadError" class="approval-board__error">{{ loadError }}</div>

    <div v-if="activeTab === 'decisions'" class="approval-board__columns">
      <!-- Column 1: PermissionRequest -->
      <section class="approval-board__column" data-testid="column-permissions">
        <h2><ShieldCheck class="size-4" /> {{ t('governance.permissions') }} ({{ openPermissions.length }})</h2>
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

      <!-- Column 2: ActionApproval + user actions -->
      <section class="approval-board__column" data-testid="column-approvals">
        <h2><Gavel class="size-4" /> {{ t('governance.actionApprovals') }} ({{ openApprovals.length }})</h2>
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
          </div>
        </article>
      </section>

      <!-- Column 3: Supervision (read-only) -->
      <section class="approval-board__column" data-testid="column-supervision">
        <h2><Eye class="size-4" /> {{ t('governance.supervision') }}</h2>
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

    <!--
      Mounted from page load and toggled with v-show, not v-if: the panel's own onMounted
      read is the "first paint already has instances" path, and a tab click must not be the
      only thing that ever asks Core for them. Auto-refresh stays off here because this page
      owns one timer that pauses while hidden.
    -->
    <section
      v-show="activeTab === 'runtime'"
      class="approval-board__runtime"
      data-testid="governance-runtime-section"
    >
      <RuntimeInstancesPanel
        ref="runtimePanelRef"
        :show-ceilings="false"
        :enable-auto-refresh="false"
        :initial-session-id="requestedSessionId"
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

.approval-board__header-actions {
  display: flex;
  gap: 8px;
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
  padding: 6px 12px;
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
  padding: 14px;
  border-radius: 12px;
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
