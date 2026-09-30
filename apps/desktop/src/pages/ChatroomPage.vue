<script setup lang="ts">
import { onMounted } from 'vue'
import { UieShell } from '@tinadec/ui'
import { homeController } from '@/controllers/HomeController'
import { useUiePage } from '@/lib/uiEngine'

useUiePage('chatroom')
onMounted(() => homeController.start())
</script>

<template>
  <UieShell />
</template>
