<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()
const pet = ref<DownloadedPet | null>(null)

onMounted(async () => {
  const instanceId = typeof route.query.instanceId === 'string' ? route.query.instanceId : ''
  if (instanceId) pet.value = await window.tinadec.pets.getWindowPet(instanceId)
})
</script>

<template>
  <main class="desktop-pet-window" aria-label="Pet window">
    <div v-if="pet?.imageDataUrl" class="pet-frame">
      <img class="pet-spritesheet" :src="pet.imageDataUrl" :alt="pet.displayName" draggable="false" />
    </div>
    <div v-else class="pet-placeholder">Pet</div>
  </main>
</template>

<style scoped>
:global(html),
:global(body),
:global(#app) {
  width: 100%;
  height: 100%;
  margin: 0;
  overflow: hidden;
  background: transparent !important;
}

.desktop-pet-window {
  display: grid;
  width: 100vw;
  height: 100vh;
  place-items: center;
  background: transparent;
}

.pet-placeholder {
  display: grid;
  width: 92px;
  height: 92px;
  place-items: center;
  border: 1px dashed rgb(255 255 255 / 52%);
  border-radius: 50%;
  color: rgb(255 255 255 / 80%);
  font: 600 14px/1 system-ui, sans-serif;
  letter-spacing: 0;
  text-transform: uppercase;
}

.pet-frame {
  width: min(192px, 100vw);
  aspect-ratio: 192 / 208;
  overflow: hidden;
}

.pet-spritesheet {
  display: block;
  width: 800%;
  max-width: none;
  height: auto;
  user-select: none;
}
</style>
