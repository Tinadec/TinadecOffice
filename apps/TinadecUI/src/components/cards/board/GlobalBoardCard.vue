<script setup lang="ts">
import GlobalBoardPage from '@/pages/GlobalBoardPage.vue'
</script>

<template>
  <div class="board-card-shell" data-testid="global-board-card">
    <GlobalBoardPage />
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
