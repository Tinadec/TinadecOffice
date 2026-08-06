<script setup lang="ts">
import PanelHome from '@/components/PanelHome.vue'
import { useHomeWorkbench } from '../homeController'
import { useWorkbenchHost } from '../host'

const home = useHomeWorkbench()
const host = useWorkbenchHost()
const typeMap: Record<string, string> = {
  git: 'home.git',
  approval: 'home.approvals',
  orchestration: 'home.orchestration',
  events: 'home.events',
  doctor: 'home.doctor',
  preview: 'tool.browser',
  agent: 'home.agent',
  terminal: 'home.terminal',
}

function openPanel(type: string): void {
  const cardType = typeMap[type] ?? type
  host.openCard(cardType, { pageId: 'home', slotId: 'right', stackId: 'primary' })
}
</script>

<template>
  <PanelHome
    :pending-approval-count="home.approvals.value.filter((approval) => approval.status === 'pending').length"
    @open-panel="(type) => openPanel(type)"
  />
</template>

<style scoped>
:deep(.panel-home) { height: 100%; }
</style>
