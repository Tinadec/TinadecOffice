<script setup lang="ts">
import DebugStudio from '../debug/DebugStudio.vue'

withDefaults(defineProps<{ embedded?: boolean }>(), {
  embedded: false,
})
</script>

<template>
  <DebugStudio :embedded="embedded" />
</template>
