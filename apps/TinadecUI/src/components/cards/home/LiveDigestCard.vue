<script setup lang="ts">
import { computed } from 'vue'
import LiveDigestPanel from '@/components/liveDigest/LiveDigestPanel.vue'
import { selectPrimaryRunId } from '@/composables/useLiveDigest'
import { homeController } from '@/controllers/HomeController'
import { selectionIdentity } from '@/lib/storageScope'

const c = homeController

const primaryRunId = computed<string | null>(() => {
  const activeRunId = c.stoppableRunId?.value
  if (activeRunId) return activeRunId
  return selectPrimaryRunId(c.runs.value)
})

const storageId = computed<string | undefined>(() => {
  const sessionKey = c.selectedSessionId.value
  if (!sessionKey) return undefined
  return selectionIdentity(sessionKey).storageId
})
</script>

<template>
  <div class="live-digest-card-host" data-dock-card="live-digest">
    <LiveDigestPanel
      :run-id="primaryRunId"
      :storage-id="storageId"
    />
  </div>
</template>

<style scoped>
.live-digest-card-host {
  width: 100%;
  height: 100%;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}
</style>
