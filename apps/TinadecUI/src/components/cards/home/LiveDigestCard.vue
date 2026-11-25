<script setup lang="ts">
import { computed } from 'vue'
import LiveDigestPanel from '@/components/liveDigest/LiveDigestPanel.vue'
import { selectPrimaryRunId } from '@/composables/useLiveDigest'
import { homeController } from '@/controllers/HomeController'
import { selectionIdentity } from '@/lib/storageScope'

const c = homeController

const activeRunId = computed<string | null>(() => c.stoppableRunId?.value ?? null)

const primaryRunId = computed<string | null>(() => {
  if (activeRunId.value) return activeRunId.value
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
      :session-id="c.selectedSessionId.value"
      :runs="c.runs.value"
      :active-run-id="activeRunId"
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
