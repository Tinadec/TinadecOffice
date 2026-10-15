<script setup lang="ts">
/**
 * The scroll body of a right-column card whose panel only stacks sections. The card host is a
 * fixed-height column that clips, so panels with their own scroller (chat, terminal, agent
 * activity, Git) fill it exactly; a panel without one lost whatever did not fit, with no way to
 * scroll to it.
 */
</script>

<template>
  <div class="card-scroll">
    <slot />
  </div>
</template>

<style scoped>
.card-scroll {
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  overscroll-behavior: contain;
}
</style>
