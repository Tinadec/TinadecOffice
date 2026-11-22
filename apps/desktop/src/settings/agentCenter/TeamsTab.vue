<script setup lang="ts">
import { ref } from 'vue'
import type { AgentModeDto, AgentViewDto } from '@/api'
import TeamsModesMerged from './TeamsModesMerged.vue'

withDefaults(defineProps<{
  modes?: AgentModeDto[]
  agents?: AgentViewDto[]
  canAccessBackend?: boolean
  loading?: boolean
}>(), {
  modes: undefined,
  agents: undefined,
  canAccessBackend: undefined,
  loading: false,
})

const emit = defineEmits<{
  changed: []
  'open-graph': [id: string]
}>()

const innerRef = ref<InstanceType<typeof TeamsModesMerged> | null>(null)

defineExpose({
  reload: () => innerRef.value?.reload(),
})
</script>

<template>
  <TeamsModesMerged
    ref="innerRef"
    :modes="modes"
    :agents="agents"
    :can-access-backend="canAccessBackend"
    :loading="loading"
    preferred-view="team"
    @changed="emit('changed')"
    @open-graph="emit('open-graph', $event)"
  />
</template>
