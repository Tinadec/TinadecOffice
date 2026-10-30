<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { UieShell } from '@tinadec/ui'
import { useUiePage } from '@/lib/uiEngine'

/**
 * The companion centre is a UIE page like Home, Space and Settings: the shell, the header, the
 * sidebar island and every panel come from the shared layout. This route only selects the page.
 *
 * It used to draw its own sidebar outside that layout, which is why it read as a different app:
 * a bare column glued to the window edge next to a set of floating cards.
 */
const uie = useUiePage('companionCenter')
const emit = defineEmits<{ ready: [] }>()
const shellReady = ref(false)
let alive = true

onMounted(async () => {
  await uie.ready
  if (!alive) return
  shellReady.value = true
  await nextTick()
  if (alive) emit('ready')
})

onBeforeUnmount(() => {
  alive = false
})
</script>

<template>
  <div class="companion-center-page">
    <UieShell v-if="shellReady" />
  </div>
</template>

<style scoped>
.companion-center-page {
  position: relative;
  width: 100%;
  height: 100%;
  overflow: hidden;
}
</style>
