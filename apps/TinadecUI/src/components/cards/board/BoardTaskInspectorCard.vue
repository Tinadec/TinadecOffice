<script setup lang="ts">
import BoardTaskInspectorPanel from '@/components/board/BoardTaskInspectorPanel.vue'
</script>

<template>
  <div class="board-card-shell" data-testid="board-task-inspector-card">
    <BoardTaskInspectorPanel />
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
