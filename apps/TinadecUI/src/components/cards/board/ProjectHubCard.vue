<script setup lang="ts">
import ProjectHubPage from '@/pages/ProjectHubPage.vue'
</script>

<template>
  <div class="board-card-shell" data-testid="project-hub-card">
    <ProjectHubPage />
  </div>
</template>

<style scoped>
.board-card-shell {
  height: 100%;
  width: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
</style>
