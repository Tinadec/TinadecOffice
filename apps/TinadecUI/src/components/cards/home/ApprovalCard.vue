<script setup lang="ts">
import { computed } from 'vue'
import ApprovalTab from '@/components/ApprovalTab.vue'
import CardScroll from './CardScroll.vue'
import { homeController } from '@/controllers/HomeController'
import { activeDockFocus } from '@/lib/homeDockSync'

const c = homeController
defineProps<{ compact?: boolean }>()

const focusedSection = computed<'pending' | 'rules' | 'history' | null>(() => {
  const focus = activeDockFocus.value
  if (focus?.card !== 'approval' || !focus.approvalTab) return null
  if (focus.approvalTab === 'approvals' || focus.approvalTab === 'pending') return 'pending'
  if (focus.approvalTab === 'rules') return 'rules'
  if (focus.approvalTab === 'history') return 'history'
  return null
})

const focusNonce = computed(() => (activeDockFocus.value?.card === 'approval' ? activeDockFocus.value.nonce : 0))
</script>

<template>
  <CardScroll>
    <div class="approval-card-host" data-testid="approval-card-host">
      <ApprovalTab
        :compact="compact"
        :approvals="c.approvals.value"
        :approval-rules="c.approvalRules.value"
        :approval-policy="c.approvalPolicy.value"
        :permission="c.currentPermission.value"
        :busy="c.busy.value"
        :selected-session-id="c.selectedSessionId.value"
        :focus-tab="focusedSection"
        :focus-nonce="focusNonce"
        @decide-approval="(approval, decision, options) => c.decideApproval(approval, decision, options)"
        @batch-decide="(input) => c.batchDecideApprovals(input)"
        @revoke-approval-rule="c.revokeApprovalRule($event)"
        @create-approval-rule="c.createApprovalRule($event)"
        @update:permission="c.updatePermission($event)"
      />
    </div>
  </CardScroll>
</template>

<style scoped>
.approval-card-host {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
</style>
