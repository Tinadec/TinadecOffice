<script setup lang="ts">
import { computed } from 'vue'
import TrajectoryWorkbenchPanel from '@/components/trajectory/TrajectoryWorkbenchPanel.vue'
import { homeController } from '@/controllers/HomeController'
import { selectionIdentity } from '@/lib/storageScope'

const c = homeController

const activeRunId = computed<string | null>(() => c.stoppableRunId?.value ?? null)

const storageId = computed<string | undefined>(() => {
  const sessionKey = c.selectedSessionId.value
  if (!sessionKey) return undefined
  return selectionIdentity(sessionKey).storageId
})
</script>

<template>
  <div class="trajectory-card-host" data-dock-card="trajectory">
    <TrajectoryWorkbenchPanel
      :session-id="c.selectedSessionId.value"
      :runs="c.runs.value"
      :active-run-id="activeRunId"
      :orchestration="c.orchestration.value"
      :tool-executions="c.toolExecutions.value"
      :storage-id="storageId"
    />
  </div>
</template>

<style scoped>
.trajectory-card-host {
  width: 100%;
  height: 100%;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}
</style>
