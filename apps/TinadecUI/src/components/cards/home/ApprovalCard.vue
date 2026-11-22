<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import ApprovalTab from '@/components/ApprovalTab.vue'
import GovernanceBoardContent from '@/components/governance/GovernanceBoardContent.vue'
import CardScroll from './CardScroll.vue'
import { homeController } from '@/controllers/HomeController'

const { t } = useI18n()
const c = homeController
defineProps<{ compact?: boolean }>()

type ApprovalCardTab = 'approvals' | 'governance' | 'runtime'
const activeTab = ref<ApprovalCardTab>('approvals')
</script>

<template>
  <CardScroll>
    <div class="approval-card-host" data-testid="approval-card-host">
      <div class="approval-card-subtabs" role="tablist">
        <button
          type="button"
          role="tab"
          class="approval-card-subtab"
          :class="{ active: activeTab === 'approvals' }"
          :aria-selected="activeTab === 'approvals'"
          data-testid="approval-card-tab-approvals"
          @click="activeTab = 'approvals'"
        >
          {{ t('approval.pendingTitle') }}
        </button>
        <button
          type="button"
          role="tab"
          class="approval-card-subtab"
          :class="{ active: activeTab === 'governance' }"
          :aria-selected="activeTab === 'governance'"
          data-testid="approval-card-tab-governance"
          @click="activeTab = 'governance'"
        >
          {{ t('governance.boardTitle') }}
        </button>
        <button
          type="button"
          role="tab"
          class="approval-card-subtab"
          :class="{ active: activeTab === 'runtime' }"
          :aria-selected="activeTab === 'runtime'"
          data-testid="approval-card-tab-runtime"
          @click="activeTab = 'runtime'"
        >
          {{ t('settings.runtimeInstances') }}
        </button>
      </div>

      <ApprovalTab
        v-if="activeTab === 'approvals'"
        :compact="compact"
        :approvals="c.approvals.value"
        :approval-rules="c.approvalRules.value"
        :shell-command="c.shellCommand.value"
        :busy="c.busy.value"
        :selected-session-id="c.selectedSessionId.value"
        @request-approval="c.requestShellApproval()"
        @decide-approval="(approval, decision, scope) => c.decideApproval(approval, decision, scope)"
        @revoke-approval-rule="c.revokeApprovalRule($event)"
        @create-approval-rule="c.createApprovalRule($event)"
        @update:shell-command="c.shellCommand.value = $event"
      />

      <div v-else class="approval-card-governance-wrap">
        <GovernanceBoardContent
          compact
          :initial-tab="activeTab === 'runtime' ? 'runtime' : 'decisions'"
        />
      </div>
    </div>
  </CardScroll>
</template>

<style scoped>
.approval-card-host {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.approval-card-subtabs {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  padding: 6px 8px 0;
}

.approval-card-subtab {
  padding: 4px 9px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 600;
  cursor: pointer;
}

.approval-card-subtab.active {
  border-color: var(--accent-primary);
  color: var(--text-primary);
  background: color-mix(in srgb, var(--accent-primary) 12%, var(--surface-raised));
}

.approval-card-governance-wrap {
  padding: 0 8px 10px;
}
</style>
