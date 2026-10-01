<script setup lang="ts">
import ApprovalTab from '@/components/ApprovalTab.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template vapor>
  <ApprovalTab
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
</template>
