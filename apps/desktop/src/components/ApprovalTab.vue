<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { Check, CheckCheck, ChevronDown, FileText, Infinity as InfinityIcon, MessageSquareWarning, Plus, ShieldAlert, ShieldX, Terminal, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import {
  api,
  type ApprovalDto,
  type ApprovalPolicySummaryDto,
  type ApprovalRuleDto,
  type BatchApprovalDecisionInput,
  type CreateApprovalRuleInput,
} from '../api'
import PreAuthorizationDialog from './governance/PreAuthorizationDialog.vue'
import ApprovalGateStatus from './approval/ApprovalGateStatus.vue'
import { homeController, type ApprovalDecisionOptions } from '@/controllers/HomeController'
import { permissionChoices } from '@/lib/composerCommands'
import { activeDockFocus } from '@/lib/homeDockSync'
import { useHostAccess } from '@/lib/hostAccess'
import { useStatusLabels } from '@/lib/statusLabels'
import type { PermissionLevel } from '@/types/mode'
import { approvalSummary } from '@/userToolAction'

export type ApprovalSectionTab = 'pending' | 'rules' | 'history'
export type ApprovalHistoryFilter = 'all' | 'approved' | 'rejected'

const { t, te } = useI18n()
const { riskLabel, approvalStatusLabel } = useStatusLabels()
const { canAccessBackend } = useHostAccess()

const props = withDefaults(defineProps<{
  approvals: ApprovalDto[]
  approvalRules?: ApprovalRuleDto[]
  approvalPolicy?: ApprovalPolicySummaryDto | null
  permission?: PermissionLevel
  shellCommand?: string
  busy: boolean
  compact?: boolean
  selectedSessionId: string | null
  focusTab?: ApprovalSectionTab | null
  focusNonce?: number
}>(), {
  approvalRules: () => [],
  approvalPolicy: null,
  permission: undefined,
  shellCommand: '',
  focusTab: null,
  focusNonce: 0,
})

const emit = defineEmits<{
  'create-approval-rule': [input: CreateApprovalRuleInput]
  'decide-approval': [approval: ApprovalDto, decision: 'approved' | 'rejected', options?: ApprovalDecisionOptions]
  'batch-decide': [input: BatchApprovalDecisionInput]
  'revoke-approval-rule': [rule: ApprovalRuleDto]
  'update:permission': [permission: PermissionLevel]
}>()

const activeSection = ref<ApprovalSectionTab>('pending')
const historyFilter = ref<ApprovalHistoryFilter>('all')
const showModeMenu = ref(false)
const localPolicy = ref<ApprovalPolicySummaryDto | null>(null)

// Per-approval inline action states (prefix rule editor & rejection steering reason)
const openPrefixApprovalId = ref<string | null>(null)
const prefixDraft = ref('')
const prefixScope = ref<'session' | 'workspace'>('session')

const openRejectApprovalId = ref<string | null>(null)
const rejectReasonDraft = ref('')
const rejectInputRefs = new Map<string, HTMLInputElement>()

// Add-rule inline form in the Rules tab
const addRuleOpen = ref(false)
const newRulePattern = ref('')
const newRuleScope = ref<'session' | 'workspace'>('session')
const preAuthOpen = ref(false)

function syncFocusTab(tab: string | null | undefined) {
  if (!tab) return
  if (tab === 'approvals' || tab === 'pending') activeSection.value = 'pending'
  else if (tab === 'rules') activeSection.value = 'rules'
  else if (tab === 'history') activeSection.value = 'history'
}

watch(
  () => [props.focusTab, props.focusNonce] as const,
  ([tab]) => syncFocusTab(tab),
  { immediate: true },
)

watch(
  () => activeDockFocus.value,
  (focus) => {
    if (focus?.card === 'approval' && focus.approvalTab) {
      syncFocusTab(focus.approvalTab)
    }
  },
  { immediate: true },
)

const effectivePermission = computed<PermissionLevel>(
  () => props.permission ?? homeController.currentPermission.value ?? 'default',
)
const activeChoice = computed(
  () => permissionChoices.find((choice) => choice.value === effectivePermission.value) ?? permissionChoices[0]!,
)
const resolvedPolicy = computed(
  () => props.approvalPolicy ?? localPolicy.value ?? homeController.approvalPolicy.value,
)
const effectiveRiskMax = computed(() => {
  const policy = resolvedPolicy.value
  if (!policy) return 'medium'
  if (effectivePermission.value.startsWith('delegate-')) {
    return policy.delegated_risk_max || policy.risk_max || 'medium'
  }
  return policy.risk_max || 'medium'
})

onMounted(() => {
  if (!resolvedPolicy.value && canAccessBackend.value) {
    void api.getApprovalPolicy().then((policy) => {
      localPolicy.value = policy
      homeController.approvalPolicy.value = policy
    }).catch(() => {})
  }
})

function selectMode(next: PermissionLevel) {
  emit('update:permission', next)
  showModeMenu.value = false
}

const isSpecProposal = (approval: ApprovalDto) => approval.tool_id === 'spec_propose'
const isCommandTool = (approval: ApprovalDto) => {
  const tool = (approval.tool_id ?? '').trim().toLowerCase()
  return tool === 'shell' || tool === 'command_run' || Boolean(approval.command && !isSpecProposal(approval))
}
const kindLabel = (kind: string) => (te(`approval.kind.${kind}`) ? t(`approval.kind.${kind}`) : kind)

const pendingList = computed(() => props.approvals.filter((a) => a.status === 'pending'))
const eligibleBatchPending = computed(() =>
  pendingList.value.filter((a) => !isSpecProposal(a) && a.governance_status !== 'outcome_unknown'),
)

const APPROVED_HISTORY_STATUSES = new Set(['approved', 'granted', 'completed', 'consumed'])
const REJECTED_HISTORY_STATUSES = new Set(['rejected', 'denied'])

function isApprovedOutcome(approval: ApprovalDto): boolean {
  return APPROVED_HISTORY_STATUSES.has(approval.status) || approval.decision === 'approved'
}

function isRejectedOutcome(approval: ApprovalDto): boolean {
  return REJECTED_HISTORY_STATUSES.has(approval.status) || approval.decision === 'rejected'
}

function approvalTimestampMs(approval: ApprovalDto): number {
  const raw = approval.decided_at ?? approval.created_at
  const parsed = Date.parse(raw)
  return Number.isFinite(parsed) ? parsed : 0
}

const allHistoryList = computed(() =>
  props.approvals
    .filter((a) => a.status !== 'pending')
    .slice()
    .sort((a, b) => approvalTimestampMs(b) - approvalTimestampMs(a)),
)

const filteredHistoryList = computed(() => {
  if (historyFilter.value === 'approved') return allHistoryList.value.filter(isApprovedOutcome)
  if (historyFilter.value === 'rejected') return allHistoryList.value.filter(isRejectedOutcome)
  return allHistoryList.value
})

type SpecStage = 'requirements' | 'design' | 'tasks'
function specProposal(approval: ApprovalDto): { stage: SpecStage; document: string } | null {
  if (!isSpecProposal(approval) || !approval.arguments) return null
  try {
    const proposal = JSON.parse(approval.arguments) as Record<string, unknown> | null
    if (!proposal || !['requirements', 'design', 'tasks'].includes(String(proposal.stage)) || typeof proposal.document !== 'string' || !proposal.document.trim()) return null
    return { stage: proposal.stage as SpecStage, document: proposal.document }
  } catch {
    return null
  }
}

function specStageLabel(approval: ApprovalDto): string {
  switch (specProposal(approval)?.stage) {
    case 'requirements': return t('approval.specRequirements')
    case 'design': return t('approval.specDesign')
    case 'tasks': return t('approval.specTasks')
    default: return t('approval.specDocument')
  }
}

const SUBCOMMAND_RUNNERS = new Set(['git', 'npm', 'pnpm', 'yarn', 'bun', 'dotnet', 'cargo', 'python', 'python3', 'uv', 'go', 'docker', 'kubectl'])

function suggestCommandPrefix(approval: ApprovalDto): string {
  const raw = (approval.command ?? '').trim().replace(/\*/g, '')
  if (!raw) return ''
  const tokens = raw.split(/\s+/).filter(Boolean)
  if (tokens.length === 0) return ''
  const first = tokens[0]!
  const baseFirst = first.split(/[\\/]/).pop()?.toLowerCase() ?? first.toLowerCase()
  if (tokens.length >= 2 && SUBCOMMAND_RUNNERS.has(baseFirst) && /^[a-z0-9:_-]+$/i.test(tokens[1]!)) {
    return `${first} ${tokens[1]}`
  }
  return first
}

function distinctArguments(approval: ApprovalDto): string | null {
  const raw = approval.arguments?.trim()
  if (!raw || raw === '{}' || raw === 'null') return null
  if (approval.command && raw === approval.command.trim()) return null
  if (approval.resource_path && raw === approval.resource_path.trim()) return null
  try {
    const parsed = JSON.parse(raw) as unknown
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      const entries = Object.entries(parsed as Record<string, unknown>)
      if (entries.length === 0) return null
      const redundantKeys = new Set(['command', 'cwd', 'working_directory', 'resource_path', 'path'])
      const allRedundant = entries.every(([key, val]) => {
        if (!redundantKeys.has(key)) return false
        if (val == null || val === '') return true
        const str = String(val)
        return str === approval.command || str === approval.cwd || str === approval.resource_path
      })
      if (allRedundant) return null
    }
  } catch {
    // Non-JSON redacted projection: display verbatim.
  }
  return raw
}

function approveOnce(approval: ApprovalDto) {
  if (isSpecProposal(approval) && !specProposal(approval)) return
  emit('decide-approval', approval, 'approved', { scope: 'once' })
}

function approveForRun(approval: ApprovalDto) {
  if (isSpecProposal(approval)) return
  emit('decide-approval', approval, 'approved', { scope: 'run' })
}

function toggleRememberPrefix(approval: ApprovalDto) {
  if (openPrefixApprovalId.value === approval.id) {
    openPrefixApprovalId.value = null
    return
  }
  openRejectApprovalId.value = null
  openPrefixApprovalId.value = approval.id
  prefixDraft.value = suggestCommandPrefix(approval)
  prefixScope.value = props.selectedSessionId ? 'session' : 'workspace'
}

function submitRememberPrefix(approval: ApprovalDto) {
  const pattern = prefixDraft.value.trim()
  if (!pattern || pattern.includes('*')) return
  emit('decide-approval', approval, 'approved', {
    scope: 'once',
    rememberRule: {
      kind: 'command_prefix',
      pattern,
      scope: prefixScope.value,
    },
  })
  openPrefixApprovalId.value = null
  prefixDraft.value = ''
}

function rejectDirect(approval: ApprovalDto) {
  const pendingReason = openRejectApprovalId.value === approval.id ? rejectReasonDraft.value.trim() : ''
  emit('decide-approval', approval, 'rejected', pendingReason ? { reason: pendingReason } : undefined)
  if (openRejectApprovalId.value === approval.id) {
    openRejectApprovalId.value = null
    rejectReasonDraft.value = ''
  }
}

async function toggleRejectWithReason(approval: ApprovalDto) {
  if (openRejectApprovalId.value === approval.id) {
    openRejectApprovalId.value = null
    rejectReasonDraft.value = ''
    return
  }
  openPrefixApprovalId.value = null
  openRejectApprovalId.value = approval.id
  rejectReasonDraft.value = ''
  await nextTick()
  rejectInputRefs.get(approval.id)?.focus()
}

function submitRejectWithReason(approval: ApprovalDto) {
  const reason = rejectReasonDraft.value.trim()
  emit('decide-approval', approval, 'rejected', reason ? { reason } : undefined)
  openRejectApprovalId.value = null
  rejectReasonDraft.value = ''
}

function onRejectInputKeydown(event: KeyboardEvent, approval: ApprovalDto) {
  if (event.key === 'Enter' && !event.isComposing) {
    event.preventDefault()
    submitRejectWithReason(approval)
  } else if (event.key === 'Escape') {
    event.preventDefault()
    openRejectApprovalId.value = null
    rejectReasonDraft.value = ''
  }
}

function setRejectInputRef(id: string, el: unknown) {
  if (el instanceof HTMLInputElement) rejectInputRefs.set(id, el)
  else rejectInputRefs.delete(id)
}

function batchApproveEligible() {
  if (eligibleBatchPending.value.length < 2) return
  emit('batch-decide', {
    approval_ids: eligibleBatchPending.value.map((a) => a.id),
    decision: 'approved',
    scope: 'once',
  })
}

function submitNewRule() {
  const pattern = newRulePattern.value.trim()
  if (!pattern || pattern.includes('*')) return
  emit('create-approval-rule', {
    kind: 'command_prefix',
    tool_id: 'shell',
    pattern,
    session_id: newRuleScope.value === 'session' ? props.selectedSessionId : null,
  })
  newRulePattern.value = ''
  addRuleOpen.value = false
}

function openRecoveryCheck(actionId: string) {
  if (typeof window === 'undefined') return
  window.dispatchEvent(new CustomEvent('tinadec:open-recovery-check', { detail: { actionId } }))
}
</script>

<template>
  <section class="panel approval-panel" data-testid="approval-panel">
    <!-- Top compact status bar -->
    <header class="approval-topbar" data-testid="approval-status-bar">
      <div class="approval-mode-wrap">
        <button
          type="button"
          class="approval-mode-pill ui-pressable"
          data-testid="approval-mode-pill"
          :aria-expanded="showModeMenu"
          @click="showModeMenu = !showModeMenu"
        >
          <component :is="activeChoice.icon" :size="13" aria-hidden="true" />
          <span class="approval-mode-name">{{ t(activeChoice.label) }}</span>
          <span class="approval-mode-ceiling" data-testid="approval-risk-ceiling">
            {{ t('approval.riskCeilingBadge', { risk: riskLabel(effectiveRiskMax) }) }}
          </span>
          <ChevronDown :size="12" class="approval-mode-chevron" :class="{ open: showModeMenu }" aria-hidden="true" />
        </button>

        <div v-if="showModeMenu" class="approval-mode-popover" role="menu" data-testid="approval-mode-menu">
          <button
            v-for="choice in permissionChoices"
            :key="choice.value"
            type="button"
            role="menuitemradio"
            class="approval-mode-option"
            :class="{ active: choice.value === effectivePermission }"
            :aria-checked="choice.value === effectivePermission"
            @click="selectMode(choice.value)"
          >
            <component :is="choice.icon" :size="13" aria-hidden="true" />
            <span>{{ t(choice.label) }}</span>
          </button>
        </div>
      </div>

      <button
        v-if="eligibleBatchPending.length >= 2"
        type="button"
        class="approval-batch-btn ui-pressable"
        data-testid="approval-batch-approve-btn"
        :disabled="busy"
        @click="batchApproveEligible"
      >
        <CheckCheck :size="13" aria-hidden="true" />
        <span>{{ t('approval.batchApproveCount', { count: eligibleBatchPending.length }) }}</span>
      </button>
    </header>

    <!-- 3 flat first-level tabs -->
    <nav class="approval-flat-tabs" role="tablist" data-testid="approval-tabs">
      <button
        type="button"
        role="tab"
        class="approval-flat-tab ui-pressable"
        :class="{ active: activeSection === 'pending' }"
        :aria-selected="activeSection === 'pending'"
        data-testid="approval-tab-pending"
        @click="activeSection = 'pending'"
      >
        <span>{{ t('approval.tabPending') }}</span>
        <span class="approval-tab-badge" :class="{ 'has-pending': pendingList.length > 0 }">{{ pendingList.length }}</span>
      </button>
      <button
        type="button"
        role="tab"
        class="approval-flat-tab ui-pressable"
        :class="{ active: activeSection === 'rules' }"
        :aria-selected="activeSection === 'rules'"
        data-testid="approval-tab-rules"
        @click="activeSection = 'rules'"
      >
        <span>{{ t('approval.tabRules') }}</span>
        <span class="approval-tab-badge">{{ props.approvalRules.length }}</span>
      </button>
      <button
        type="button"
        role="tab"
        class="approval-flat-tab ui-pressable"
        :class="{ active: activeSection === 'history' }"
        :aria-selected="activeSection === 'history'"
        data-testid="approval-tab-history"
        @click="activeSection = 'history'"
      >
        <span>{{ t('approval.tabHistory') }}</span>
        <span class="approval-tab-badge">{{ allHistoryList.length }}</span>
      </button>
    </nav>

    <!-- 1. Pending Tab -->
    <div v-if="activeSection === 'pending'" class="approval-pane" data-testid="approval-pane-pending">
      <article
        v-for="approval in pendingList"
        :key="approval.id"
        class="approval-card-item"
        :class="{ 'approval-card-item--spec': isSpecProposal(approval) }"
        :data-testid="`approval-pending-${approval.id}`"
      >
        <div class="approval-head">
          <strong>{{ isSpecProposal(approval) ? t('approval.specConfirmation') : kindLabel(approval.kind) }}</strong>
          <code v-if="approval.tool_id && !isSpecProposal(approval)" class="approval-tool">{{ approval.tool_id }}</code>
          <span v-if="approval.risk" class="approval-risk" :data-risk="approval.risk">{{ riskLabel(approval.risk) }}</span>
          <span
            v-if="approval.decided_by_device_name"
            class="approval-decided-device"
            data-testid="approval-decided-by-device"
          >
            {{ t('approval.decidedByDevice', { device: approval.decided_by_device_name }) }}
          </span>
        </div>

        <p class="approval-summary">
          {{ isSpecProposal(approval) ? t('approval.specConfirmationHint') : approvalSummary(approval, { t, te }) }}
        </p>

        <!-- Spec proposal full document (preserved verbatim) -->
        <section v-if="isSpecProposal(approval)" class="approval-spec" :aria-labelledby="`spec-document-${approval.id}`">
          <h3 :id="`spec-document-${approval.id}`" class="approval-spec-title">{{ specStageLabel(approval) }}</h3>
          <pre
            v-if="specProposal(approval)"
            class="approval-spec-document"
            tabindex="0"
            :aria-label="specStageLabel(approval)"
            data-testid="approval-spec-document"
          >{{ specProposal(approval)!.document }}</pre>
          <p v-else class="approval-spec-unavailable" role="alert">{{ t('approval.specDocumentUnavailable') }}</p>
        </section>

        <!-- Default expanded core evidence (no <details> fold!) -->
        <div v-else class="approval-evidence-open" data-testid="approval-evidence">
          <div v-if="approval.command" class="approval-command-box" data-testid="approval-evidence-command">
            <Terminal :size="12" aria-hidden="true" />
            <code>{{ approval.command }}</code>
          </div>
          <div v-if="approval.cwd" class="approval-meta-line" data-testid="approval-evidence-cwd">
            <span class="approval-meta-key">{{ t('approval.cwdLabel') }}</span>
            <code class="approval-cwd">{{ approval.cwd }}</code>
          </div>
          <div v-if="approval.resource_path" class="approval-meta-line" data-testid="approval-evidence-path">
            <FileText :size="12" aria-hidden="true" />
            <code>{{ approval.resource_path }}</code>
          </div>
          <div v-if="distinctArguments(approval)" class="approval-arguments-box" data-testid="approval-evidence-args">
            <span class="approval-meta-key">{{ t('approval.parameters') }}</span>
            <code>{{ distinctArguments(approval) }}</code>
          </div>
          <p
            v-if="!approval.command && !approval.cwd && !approval.resource_path && !distinctArguments(approval)"
            class="approval-unknown-evidence"
          >
            {{ t('approval.evidenceUnavailable') }}
          </p>
        </div>

        <ApprovalGateStatus v-if="approval.kind === 'tool' && !isSpecProposal(approval)" :approval-id="approval.id" />

        <!-- Decision actions -->
        <div class="approval-decision-bar">
          <button
            type="button"
            class="approval-btn approval-btn--primary ui-pressable"
            data-testid="approval-approve-btn"
            :disabled="busy || (isSpecProposal(approval) && !specProposal(approval))"
            @click="approveOnce(approval)"
          >
            <Check :size="13" aria-hidden="true" />
            <span>{{ isSpecProposal(approval) ? t('approval.approveDocument') : t('approval.approve') }}</span>
          </button>

          <button
            v-if="!isSpecProposal(approval)"
            type="button"
            class="approval-btn ui-pressable"
            data-testid="approval-allow-run-btn"
            :disabled="busy"
            :title="t('approval.alwaysAllowRun')"
            @click="approveForRun(approval)"
          >
            <InfinityIcon :size="13" aria-hidden="true" />
            <span>{{ t('approval.alwaysAllowRun') }}</span>
          </button>

          <button
            v-if="!isSpecProposal(approval) && isCommandTool(approval) && suggestCommandPrefix(approval)"
            type="button"
            class="approval-btn ui-pressable"
            :class="{ 'is-open': openPrefixApprovalId === approval.id }"
            data-testid="approval-remember-toggle-btn"
            :disabled="busy"
            @click="toggleRememberPrefix(approval)"
          >
            <span>{{ t('approval.approveAndRememberPrefix') }}</span>
          </button>

          <button
            type="button"
            class="approval-btn ui-pressable"
            data-testid="approval-reject-btn"
            :disabled="busy"
            @click="rejectDirect(approval)"
          >
            <ShieldX :size="13" aria-hidden="true" />
            <span>{{ isSpecProposal(approval) ? t('approval.rejectDocument') : t('approval.reject') }}</span>
          </button>

          <button
            type="button"
            class="approval-btn approval-btn--ghost ui-pressable"
            :class="{ 'is-open': openRejectApprovalId === approval.id }"
            data-testid="approval-reject-reason-toggle"
            :disabled="busy"
            :title="t('approval.rejectWithReason')"
            @click="toggleRejectWithReason(approval)"
          >
            <MessageSquareWarning :size="13" aria-hidden="true" />
            <span>{{ t('approval.rejectWithReason') }}</span>
          </button>

          <button
            v-if="approval.governance_status === 'outcome_unknown'"
            type="button"
            class="approval-btn ui-pressable"
            data-testid="approval-open-recovery-btn"
            @click="openRecoveryCheck(approval.id)"
          >
            <ShieldAlert :size="13" aria-hidden="true" />
            <span>{{ t('governance.recoveryTitle') }}</span>
          </button>
        </div>

        <!-- Inline atomic remember-prefix editor -->
        <div v-if="openPrefixApprovalId === approval.id" class="approval-inline-drawer" data-testid="approval-remember-form">
          <input
            v-model="prefixDraft"
            type="text"
            class="approval-inline-input approval-mono-input"
            data-testid="approval-remember-prefix-input"
            :placeholder="t('approval.prefixPlaceholder')"
            :aria-label="t('approval.prefixPlaceholder')"
            @keydown.enter.prevent="submitRememberPrefix(approval)"
            @keydown.esc.prevent="openPrefixApprovalId = null"
          />
          <div class="approval-inline-controls">
            <div class="approval-scope-pills" role="group" :aria-label="t('approval.ruleScopeLabel')">
              <button
                type="button"
                class="approval-scope-pill"
                :class="{ active: prefixScope === 'session' }"
                data-testid="approval-remember-scope-session"
                @click="prefixScope = 'session'"
              >
                {{ t('approval.scopeSession') }}
              </button>
              <button
                type="button"
                class="approval-scope-pill"
                :class="{ active: prefixScope === 'workspace' }"
                data-testid="approval-remember-scope-workspace"
                @click="prefixScope = 'workspace'"
              >
                {{ t('approval.scopeWorkspace') }}
              </button>
            </div>
            <button
              type="button"
              class="approval-btn approval-btn--primary ui-pressable"
              data-testid="approval-remember-submit-btn"
              :disabled="busy || !prefixDraft.trim() || prefixDraft.includes('*')"
              @click="submitRememberPrefix(approval)"
            >
              {{ t('approval.confirmApproveAndRemember') }}
            </button>
          </div>
        </div>

        <!-- Inline 1-line rejection steering reason -->
        <div v-if="openRejectApprovalId === approval.id" class="approval-inline-drawer" data-testid="approval-reject-reason-form">
          <input
            :ref="(el) => setRejectInputRef(approval.id, el)"
            v-model="rejectReasonDraft"
            type="text"
            class="approval-inline-input"
            data-testid="approval-reject-reason-input"
            :placeholder="t('approval.rejectReasonPlaceholder')"
            :aria-label="t('approval.rejectReasonPlaceholder')"
            @keydown="onRejectInputKeydown($event, approval)"
          />
          <div class="approval-inline-controls">
            <button
              type="button"
              class="approval-btn approval-btn--primary ui-pressable"
              data-testid="approval-reject-reason-submit"
              :disabled="busy"
              @click="submitRejectWithReason(approval)"
            >
              {{ t('approval.confirmRejectWithReason') }}
            </button>
            <button
              type="button"
              class="approval-btn approval-btn--ghost ui-pressable"
              @click="openRejectApprovalId = null; rejectReasonDraft = ''"
            >
              {{ t('common.cancel') }}
            </button>
          </div>
        </div>
      </article>

      <p v-if="pendingList.length === 0" class="quiet approval-empty" data-testid="approval-pending-empty">
        {{ t('context.noApprovals') }}
      </p>
    </div>

    <!-- 2. Auto Rules Tab -->
    <div v-else-if="activeSection === 'rules'" class="approval-pane" data-testid="approval-pane-rules">
      <div class="approval-rules-toolbar">
        <span class="approval-rules-hint">{{ t('approval.rulesToolbarHint') }}</span>
        <button
          type="button"
          class="approval-btn ui-pressable"
          data-testid="approval-add-rule-toggle"
          @click="addRuleOpen = !addRuleOpen"
        >
          <Plus :size="13" aria-hidden="true" />
          <span>{{ t('approval.addRule') }}</span>
        </button>
      </div>

      <div v-if="addRuleOpen" class="approval-inline-drawer" data-testid="approval-add-rule-form">
        <input
          v-model="newRulePattern"
          type="text"
          class="approval-inline-input approval-mono-input"
          data-testid="approval-new-rule-pattern"
          :placeholder="t('approval.prefixPlaceholder')"
          :aria-label="t('approval.prefixPlaceholder')"
          @keydown.enter.prevent="submitNewRule"
          @keydown.esc.prevent="addRuleOpen = false"
        />
        <div class="approval-inline-controls">
          <div class="approval-scope-pills" role="group" :aria-label="t('approval.ruleScopeLabel')">
            <button
              type="button"
              class="approval-scope-pill"
              :class="{ active: newRuleScope === 'session' }"
              :disabled="!selectedSessionId"
              data-testid="approval-new-rule-scope-session"
              @click="newRuleScope = 'session'"
            >
              {{ t('approval.scopeSession') }}
            </button>
            <button
              type="button"
              class="approval-scope-pill"
              :class="{ active: newRuleScope === 'workspace' }"
              data-testid="approval-new-rule-scope-workspace"
              @click="newRuleScope = 'workspace'"
            >
              {{ t('approval.scopeWorkspace') }}
            </button>
          </div>
          <div class="approval-inline-submit-group">
            <button
              type="button"
              class="approval-btn approval-btn--ghost ui-pressable"
              data-testid="approval-open-preauth-btn"
              @click="preAuthOpen = true"
            >
              {{ t('approval.openPreAuthDialog') }}
            </button>
            <button
              type="button"
              class="approval-btn approval-btn--primary ui-pressable"
              data-testid="approval-new-rule-submit"
              :disabled="busy || !newRulePattern.trim() || newRulePattern.includes('*') || (newRuleScope === 'session' && !selectedSessionId)"
              @click="submitNewRule"
            >
              {{ t('approval.saveRule') }}
            </button>
          </div>
        </div>
      </div>

      <div v-if="props.approvalRules.length" class="approval-rules-list">
        <article
          v-for="rule in props.approvalRules"
          :key="rule.id"
          class="approval-rule-card"
          :data-testid="`approval-rule-${rule.id}`"
        >
          <div class="approval-rule-main">
            <div class="approval-rule-top">
              <span class="approval-rule-badge">
                {{ rule.kind === 'command_prefix' ? t('approval.commandRuleHint') : t('approval.toolRuleHint') }}
              </span>
              <code class="approval-rule-pattern">{{ rule.kind === 'command_prefix' ? (rule.pattern || rule.tool_id) : rule.tool_id }}</code>
              <span class="approval-rule-scope">
                {{ rule.session_id ? t('approval.scopeSession') : t('approval.scopeWorkspace') }}
              </span>
            </div>
            <span class="approval-rule-meta">
              {{ rule.tool_id }} · {{ t('approval.ruleUses', { count: rule.use_count }) }}
            </span>
          </div>
          <button
            type="button"
            class="approval-rule-revoke ui-pressable"
            data-testid="approval-revoke-rule-btn"
            :title="t('approval.revokeRule')"
            :aria-label="t('approval.revokeRule')"
            @click="emit('revoke-approval-rule', rule)"
          >
            <X :size="13" aria-hidden="true" />
          </button>
        </article>
      </div>
      <p v-else class="quiet approval-empty" data-testid="approval-rules-empty">{{ t('approval.noAllowed') }}</p>

      <PreAuthorizationDialog v-model:open="preAuthOpen" />
    </div>

    <!-- 3. History Tab -->
    <div v-else class="approval-pane" data-testid="approval-pane-history">
      <div class="approval-history-filters" role="group" :aria-label="t('approval.historyTitle')" data-testid="approval-history-filters">
        <button
          type="button"
          class="approval-filter-pill ui-pressable"
          :class="{ active: historyFilter === 'all' }"
          data-testid="approval-history-filter-all"
          @click="historyFilter = 'all'"
        >
          {{ t('approval.filterAll') }}
        </button>
        <button
          type="button"
          class="approval-filter-pill ui-pressable"
          :class="{ active: historyFilter === 'approved' }"
          data-testid="approval-history-filter-approved"
          @click="historyFilter = 'approved'"
        >
          {{ t('approval.filterApproved') }}
        </button>
        <button
          type="button"
          class="approval-filter-pill ui-pressable"
          :class="{ active: historyFilter === 'rejected' }"
          data-testid="approval-history-filter-rejected"
          @click="historyFilter = 'rejected'"
        >
          {{ t('approval.filterRejected') }}
        </button>
      </div>

      <div v-if="filteredHistoryList.length" class="approval-history-items">
        <article
          v-for="approval in filteredHistoryList"
          :key="`history-${approval.id}`"
          class="approval-history-card"
          data-testid="approval-history-item"
          :data-status="approval.status"
        >
          <ShieldX v-if="isRejectedOutcome(approval)" :size="14" class="approval-history-icon rejected" aria-hidden="true" />
          <ShieldAlert v-else-if="approval.governance_status === 'outcome_unknown' || approval.status === 'outcome_unknown'" :size="14" class="approval-history-icon unknown" aria-hidden="true" />
          <Check v-else :size="14" class="approval-history-icon" aria-hidden="true" />

          <div class="approval-history-body">
            <div class="approval-history-head">
              <strong class="approval-history-title">
                {{ approval.command ?? approval.resource_path ?? approval.tool_id ?? approvalSummary(approval, { t, te }) }}
              </strong>
              <span class="approval-history-status" :data-status="approval.status">
                {{ approvalStatusLabel(approval.status) }}
              </span>
              <span
                v-if="approval.decided_by_device_name"
                class="approval-decided-device"
                data-testid="approval-decided-by-device"
              >
                {{ t('approval.decidedByDevice', { device: approval.decided_by_device_name }) }}
              </span>
            </div>
            <span class="approval-history-summary">{{ approvalSummary(approval, { t, te }) }}</span>
            <p v-if="approval.decision_reason" class="approval-history-reason" data-testid="approval-history-reason">
              {{ approval.decision_reason }}
            </p>
          </div>

          <button
            v-if="approval.governance_status === 'outcome_unknown' || approval.status === 'outcome_unknown'"
            type="button"
            class="approval-btn ui-pressable"
            data-testid="approval-open-recovery-btn"
            @click="openRecoveryCheck(approval.id)"
          >
            {{ t('governance.recoveryTitle') }}
          </button>
        </article>
      </div>
      <p v-else class="quiet approval-empty" data-testid="approval-history-empty">{{ t('approval.noHistory') }}</p>
    </div>
  </section>
</template>

<style scoped>
.approval-panel {
  display: flex;
  flex-direction: column;
  gap: 10px;
  min-width: 0;
}

.approval-topbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}

.approval-mode-wrap {
  position: relative;
  min-width: 0;
}

.approval-mode-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 11.5px;
  cursor: pointer;
  max-width: 100%;
}

.approval-mode-pill:hover {
  background: var(--surface-hover);
}

.approval-mode-name {
  font-weight: 600;
  white-space: nowrap;
}

.approval-mode-ceiling {
  color: var(--text-muted);
  font-size: 11px;
  white-space: nowrap;
}

.approval-mode-chevron {
  color: var(--text-muted);
  transition: transform var(--duration-tap, 120ms) var(--ease-snappy, ease);
}

.approval-mode-chevron.open {
  transform: rotate(180deg);
}

.approval-mode-popover {
  position: absolute;
  top: calc(100% + 4px);
  left: 0;
  z-index: 40;
  min-width: 210px;
  padding: 4px;
  border-radius: 10px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  box-shadow: var(--shadow-panel);
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.approval-mode-option {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 9px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.approval-mode-option:hover,
.approval-mode-option.active {
  background: var(--surface-hover);
  color: var(--text-primary);
  font-weight: 500;
}

.approval-batch-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 10px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--accent-primary);
  color: var(--text-on-primary);
  font-size: 11.5px;
  font-weight: 600;
  cursor: pointer;
}

.approval-batch-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.approval-flat-tabs {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 3px;
  border-radius: 9px;
  background: var(--surface-raised);
}

.approval-flat-tab {
  flex: 1 1 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  min-height: 28px;
  padding: 4px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  white-space: nowrap;
}

.approval-flat-tab:hover {
  color: var(--text-primary);
}

.approval-flat-tab.active {
  background: var(--surface-section);
  color: var(--text-primary);
  font-weight: 600;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.08);
}

.approval-tab-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 17px;
  padding: 0 5px;
  border-radius: 999px;
  background: var(--bg-secondary);
  color: var(--text-muted);
  font-size: 10px;
  font-variant-numeric: tabular-nums;
}

.approval-tab-badge.has-pending {
  background: var(--accent-primary);
  color: var(--text-on-primary);
}

.approval-pane {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.approval-card-item {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 11px;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-raised);
  min-width: 0;
}

.approval-summary {
  margin: 0;
  color: var(--text-secondary);
  font-size: 12px;
  line-height: 1.5;
  white-space: normal;
  overflow-wrap: anywhere;
}

.approval-evidence-open {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
}

.approval-command-box {
  display: flex;
  align-items: flex-start;
  gap: 7px;
  padding: 7px 9px;
  border-radius: 7px;
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 11.5px;
  line-height: 1.5;
}

.approval-command-box svg {
  flex-shrink: 0;
  margin-top: 3px;
  color: var(--text-muted);
}

.approval-command-box code {
  font-family: var(--font-mono);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  word-break: break-word;
}

.approval-meta-line {
  display: flex;
  align-items: baseline;
  gap: 6px;
  font-size: 11.5px;
  color: var(--text-secondary);
  min-width: 0;
}

.approval-meta-line svg {
  flex-shrink: 0;
  color: var(--text-muted);
}

.approval-meta-line code {
  font-family: var(--font-mono);
  overflow-wrap: anywhere;
  word-break: break-all;
}

.approval-meta-key {
  flex-shrink: 0;
  color: var(--text-muted);
  font-size: 11px;
}

.approval-arguments-box {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.approval-arguments-box code {
  display: block;
  max-height: 140px;
  overflow: auto;
  padding: 6px 8px;
  border-radius: 6px;
  background: var(--surface-section);
  color: var(--text-secondary);
  font-family: var(--font-mono);
  font-size: 11px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.approval-decision-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  margin-top: 2px;
}

.approval-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  min-height: 27px;
  padding: 4px 10px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 11.5px;
  font-weight: 500;
  cursor: pointer;
  white-space: nowrap;
}

.approval-btn:hover:not(:disabled),
.approval-btn.is-open:not(:disabled) {
  background: var(--surface-hover);
}

.approval-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.approval-btn--primary {
  border-color: transparent;
  background: var(--accent-primary);
  color: var(--text-on-primary);
  font-weight: 600;
}

.approval-btn--primary:hover:not(:disabled) {
  opacity: 0.92;
  background: var(--accent-primary);
}

.approval-btn--ghost {
  border-color: transparent;
  background: transparent;
  color: var(--text-secondary);
}

.approval-btn--ghost:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.approval-inline-drawer {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px;
  border-radius: 8px;
  background: var(--surface-section);
}

.approval-inline-input {
  width: 100%;
  min-height: 28px;
  padding: 4px 8px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
  color: var(--text-primary);
  font-size: 12px;
}

.approval-mono-input {
  font-family: var(--font-mono);
}

.approval-inline-controls {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 6px;
}

.approval-inline-submit-group {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
}

.approval-scope-pills,
.approval-history-filters {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.approval-scope-pill,
.approval-filter-pill {
  padding: 3px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: transparent;
  color: var(--text-secondary);
  font-size: 11px;
  cursor: pointer;
}

.approval-scope-pill.active,
.approval-filter-pill.active {
  border-color: transparent;
  background: var(--accent-primary);
  color: var(--text-on-primary);
  font-weight: 600;
}

.approval-scope-pill:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.approval-rules-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.approval-rules-hint {
  color: var(--text-muted);
  font-size: 11.5px;
}

.approval-rules-list,
.approval-history-items {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.approval-rule-card,
.approval-history-card {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--surface-raised);
  min-width: 0;
}

.approval-rule-main,
.approval-history-body {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
  flex: 1;
}

.approval-rule-top,
.approval-history-head {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  min-width: 0;
}

.approval-rule-badge,
.approval-rule-scope,
.approval-decided-device {
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 10.5px;
}

.approval-rule-pattern {
  font-family: var(--font-mono);
  font-size: 11.5px;
  color: var(--text-primary);
  overflow-wrap: anywhere;
}

.approval-rule-meta,
.approval-history-summary {
  color: var(--text-muted);
  font-size: 11px;
  overflow-wrap: anywhere;
}

.approval-history-title {
  font-family: var(--font-mono);
  font-size: 11.5px;
  font-weight: 500;
  color: var(--text-primary);
  overflow-wrap: anywhere;
  flex: 1;
  min-width: 0;
}

.approval-history-reason {
  margin: 2px 0 0;
  color: var(--text-secondary);
  font-size: 11px;
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.approval-history-icon {
  flex-shrink: 0;
  margin-top: 2px;
  color: var(--text-secondary);
}

.approval-history-icon.rejected {
  color: var(--accent-danger);
}

.approval-history-icon.unknown {
  color: var(--accent-warning);
}

.approval-spec { min-width: 0; margin-top: 4px; }
.approval-spec-title { margin: 0 0 6px; font-size: 12px; font-weight: 600; color: var(--text-primary); }
.approval-spec-document { margin: 0; max-height: 360px; overflow: auto; overscroll-behavior: contain; white-space: pre-wrap; overflow-wrap: anywhere; word-break: break-word; padding: 12px; border-radius: 8px; background: var(--surface-section); color: var(--text-primary); font: inherit; font-size: 12px; line-height: 1.6; user-select: text; }
.approval-spec-document:focus-visible { outline: 2px solid var(--text-muted); outline-offset: -2px; }
.approval-spec-unavailable { margin: 6px 0; color: var(--accent-danger); font-size: 12px; white-space: normal; overflow: visible; }
</style>
