<script setup lang="ts">
import TerminalPanel from '@/components/TerminalPanel.vue'
import { useHomeWorkbench } from '../homeController'

const home = useHomeWorkbench()
</script>

<template>
  <TerminalPanel :cwd="home.currentProject.value?.path" :visible="true" />
</template>

