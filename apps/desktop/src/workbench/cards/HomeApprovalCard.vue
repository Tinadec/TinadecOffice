<script setup lang="ts">
import ApprovalTab from '@/components/ApprovalTab.vue'
import { useHomeWorkbench } from '../homeController'

const home = useHomeWorkbench()
</script>

<template>
  <ApprovalTab
    :approvals="home.approvals.value"
    :shell-command="home.shellCommand.value"
    :busy="home.busy.value"
    :selected-session-id="home.selectedSessionId.value"
    @request-approval="home.requestShellApproval"
    @decide-approval="home.decideApproval"
    @update:shell-command="home.shellCommand.value = $event"
  />
</template>

