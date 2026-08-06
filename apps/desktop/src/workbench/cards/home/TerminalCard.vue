<script setup lang="ts">
import { computed } from 'vue'
import TerminalPanel from '@/components/TerminalPanel.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController

const visible = computed(() => c.busy.value)
</script>

<template>
  <TerminalPanel
    :cwd="c.currentProject.value?.path"
    :visible="visible"
  />
</template>
