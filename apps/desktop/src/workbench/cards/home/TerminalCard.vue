<script setup lang="ts">
import { computed, inject } from 'vue'
import TerminalPanel from '@/components/TerminalPanel.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController

// Card context is provided by WorkbenchCardHost.
const active = inject<boolean>('wb:active', false)

const visible = computed(() => active ?? false)
</script>

<template vapor>
  <TerminalPanel
    :cwd="c.currentProject.value?.path"
    :visible="visible"
  />
</template>
