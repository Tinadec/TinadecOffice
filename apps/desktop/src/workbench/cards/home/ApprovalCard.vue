<script setup lang="ts">
import ApprovalTab from '@/components/ApprovalTab.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template vapor>
  <ApprovalTab
    :approvals="c.approvals.value"
    :shell-command="c.shellCommand.value"
    :busy="c.busy.value"
    :selected-session-id="c.selectedSessionId.value"
    @request-approval="c.requestShellApproval()"
    @decide-approval="(approval, decision) => c.decideApproval(approval, decision)"
    @update:shell-command="c.shellCommand.value = $event"
  />
</template>
