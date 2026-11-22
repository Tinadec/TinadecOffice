<script setup lang="ts">
import { ref } from 'vue'
import TeamsModesMerged from '@/settings/agentCenter/TeamsModesMerged.vue'

const innerRef = ref<InstanceType<typeof TeamsModesMerged> | null>(null)

defineExpose({
  loadModes: (options?: { force?: boolean }) => innerRef.value?.loadModes(options),
})
</script>

<template>
  <TeamsModesMerged ref="innerRef" preferred-view="topology" />
</template>
