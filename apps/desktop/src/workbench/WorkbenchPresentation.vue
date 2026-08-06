<script setup lang="ts">
import { KeepAlive, Transition } from 'vue'
import { RouterView } from 'vue-router'

defineProps<{
  transitionName: string
}>()
</script>

<template>
  <div class="main-content">
    <RouterView v-slot="{ Component }">
      <Transition :name="transitionName" mode="out-in">
        <KeepAlive :max="5">
          <component :is="Component" />
        </KeepAlive>
      </Transition>
    </RouterView>
  </div>
</template>
