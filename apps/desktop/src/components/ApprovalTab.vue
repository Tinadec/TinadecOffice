<script setup lang="ts">
import { Check, ChevronDown, FileText, Infinity as InfinityIcon, ShieldAlert, ShieldX, Terminal } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import type { ApprovalDto, ApprovalRuleDto, CreateApprovalRuleInput } from '../api'
import ApprovalGateStatus from './approval/ApprovalGateStatus.vue'

const { t } = useI18n()

const props = withDefaults(defineProps<{
  approvals: ApprovalDto[]
  approvalRules?: ApprovalRuleDto[]
  shellCommand: string
  busy: boolean
  selectedSessionId: string | null
}>(), { approvalRules: () => [] })

const emit = defineEmits<{
  'request-approval': []
  'create-approval-rule': [input: CreateApprovalRuleInput]
  /** `scope: 'run'` is "always allow this tool for this session". */
  'decide-approval': [approval: ApprovalDto, decision: 'approved' | 'rejected', scope?: 'once' | 'run']
  'revoke-approval-rule': [rule: ApprovalRuleDto]
  'update:shellCommand': [value: string]
}>()

const pendingApprovals = (approvals: ApprovalDto[]) => approvals.filter((a) => a.status === 'pending')
const allowedApprovals = (approvals: ApprovalDto[]) => approvals.filter((a) => a.status === 'approved' || a.governance_status === 'completed')
const historyApprovals = (approvals: ApprovalDto[]) => approvals.filter((a) => a.status !== 'pending' && !allowedApprovals([a]).length)
const ruleLabel = (rule: ApprovalRuleDto) => rule.kind === 'command_prefix'
  ? `${rule.tool_id} · ${rule.pattern ?? ''}`
  : rule.tool_id
</script>

<template>
  <section class="panel">
    <div class="approval-request-box">
      <div class="approval-request-heading">
        <div>
          <strong>{{ t('approval.commandRequestTitle') }}</strong>
          <span>{{ t('approval.commandRequestHint') }}</span>
        </div>
        <Terminal :size="15" aria-hidden="true" />
      </div>
      <div class="approval-input">
        <input
          :value="shellCommand"
          :placeholder="t('approval.commandPlaceholder')"
          :aria-label="t('approval.commandLabel')"
          @input="emit('update:shellCommand', ($event.target as HTMLInputElement).value)"
        />
        <button
          class="approval-request-button"
          :title="t('approval.request')"
          :disabled="busy || !selectedSessionId"
          @click="emit('request-approval')"
        >
          <ShieldAlert :size="14" />
          <span>{{ t('approval.request') }}</span>
        </button>
        <button
          class="approval-rule-request-button"
          :title="t('approval.rememberCommand')"
          :disabled="busy || !selectedSessionId || !shellCommand.trim()"
          @click="emit('create-approval-rule', { kind: 'command_prefix', tool_id: 'shell', pattern: shellCommand.trim(), session_id: selectedSessionId })"
        >
          <InfinityIcon :size="14" />
          <span>{{ t('approval.rememberCommand') }}</span>
        </button>
      </div>
    </div>

    <section class="approval-section approval-section-pending">
      <header class="approval-section-heading">
        <div><strong>{{ t('approval.pendingTitle') }}</strong><span>{{ t('approval.pendingHint') }}</span></div>
        <span class="approval-count approval-count-pending">{{ pendingApprovals(approvals).length }}</span>
      </header>
      <article v-for="approval in pendingApprovals(approvals)" :key="approval.id" class="approval-row approval-row-pending">
      <div class="approval-facts">
        <div class="approval-head">
          <strong>{{ approval.kind }}</strong>
          <code v-if="approval.tool_id" class="approval-tool">{{ approval.tool_id }}</code>
          <span v-if="approval.risk" class="approval-risk" :data-risk="approval.risk">{{ approval.risk }}</span>
        </div>
        <p class="approval-summary">{{ approval.summary }}</p>
        <details class="approval-evidence-details">
          <summary><ChevronDown :size="13" />{{ t('approval.viewEvidence') }}</summary>
          <!-- The three facts a decision actually turns on: command, location and target. -->
          <p v-if="approval.command" class="approval-command">
            <Terminal :size="12" aria-hidden="true" /><code>{{ approval.command }}</code>
          </p>
          <p v-if="approval.cwd" class="approval-cwd-row"><FileText :size="12" aria-hidden="true" /><code class="approval-cwd">{{ approval.cwd }}</code></p>
          <p v-if="approval.resource_path" class="approval-target">
            <FileText :size="12" aria-hidden="true" /><code>{{ approval.resource_path }}</code>
          </p>
          <div v-if="approval.arguments" class="approval-arguments">
            <span>{{ t('approval.parameters') }}</span><code>{{ approval.arguments }}</code>
          </div>
          <p v-if="!approval.command && !approval.cwd && !approval.resource_path && !approval.arguments" class="approval-unknown-evidence">
            {{ t('approval.evidenceUnavailable') }}
          </p>
        </details>
        <!-- Only the approval layer is ever delegated; a policy park always waits for the person. -->
        <ApprovalGateStatus v-if="approval.kind === 'tool'" :approval-id="approval.id" />
      </div>
      <div class="approval-actions">
        <!-- "Always allow for this session": one tool, this run. Only offered for a
             policy park, whose escalation is a tool id the scope can name exactly. -->
        <button
          v-if="approval.kind === 'permission'"
          class="approval-action-button always"
          :title="t('approval.alwaysAllow')"
          @click="emit('decide-approval', approval, 'approved', 'run')"
        >
          <InfinityIcon :size="14" /><span>{{ t('approval.alwaysAllow') }}</span>
        </button>
        <button class="approval-action-button approve" :title="t('approval.approve')" @click="emit('decide-approval', approval, 'approved')">
          <Check :size="14" /><span>{{ t('approval.approve') }}</span>
        </button>
        <button class="approval-action-button reject" :title="t('approval.reject')" @click="emit('decide-approval', approval, 'rejected')">
          <ShieldX :size="14" /><span>{{ t('approval.reject') }}</span>
        </button>
      </div>
      </article>
      <span v-if="pendingApprovals(approvals).length === 0" class="quiet approval-empty">{{ t('context.noApprovals') }}</span>
    </section>

    <details class="approval-section approval-section-collapsed" open>
      <summary class="approval-section-heading">
        <div><strong>{{ t('approval.allowedTitle') }}</strong><span>{{ t('approval.allowedHint') }}</span></div>
        <span class="approval-count approval-count-allowed">{{ props.approvalRules.length }}</span>
      </summary>
      <div v-if="props.approvalRules.length" class="approval-history-list">
        <article v-for="rule in props.approvalRules" :key="rule.id" class="approval-history-row approval-rule-row">
          <InfinityIcon :size="14" class="approval-history-icon" />
          <div class="approval-history-copy"><strong>{{ ruleLabel(rule) }}</strong><span>{{ rule.kind === 'command_prefix' ? t('approval.commandRuleHint') : t('approval.toolRuleHint') }} · {{ t('approval.ruleUses', { count: rule.use_count }) }}</span></div>
          <button class="approval-rule-revoke" type="button" :title="t('approval.revokeRule')" @click="emit('revoke-approval-rule', rule)"><ShieldX :size="13" /></button>
        </article>
      </div>
      <span v-else class="quiet approval-empty">{{ t('approval.noAllowed') }}</span>
    </details>

    <details class="approval-section approval-section-collapsed">
      <summary class="approval-section-heading">
        <div><strong>{{ t('approval.historyTitle') }}</strong><span>{{ t('approval.historyHint') }}</span></div>
        <span class="approval-count">{{ historyApprovals(approvals).length }}</span>
      </summary>
      <div v-if="historyApprovals(approvals).length" class="approval-history-list">
        <article v-for="approval in historyApprovals(approvals)" :key="`history-${approval.id}`" class="approval-history-row">
          <ShieldX v-if="approval.status === 'rejected'" :size="14" class="approval-history-icon rejected" />
          <Check v-else :size="14" class="approval-history-icon" />
          <div class="approval-history-copy"><strong>{{ approval.command ?? approval.tool_id ?? approval.summary }}</strong><span>{{ approval.summary }}</span></div>
          <span class="approval-history-status" :data-status="approval.status">{{ approval.status }}</span>
        </article>
      </div>
      <span v-else class="quiet approval-empty">{{ t('approval.noHistory') }}</span>
    </details>
  </section>
</template>
