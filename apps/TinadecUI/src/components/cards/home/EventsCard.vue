<script setup lang="ts">
import DoctorCard from './DoctorCard.vue'
</script>

<template>
  <!-- Legacy 'events' card deep-links cleanly into the unified Diagnostics & Events surface on the 'events' tab. -->
  <DoctorCard initial-tab="events" />
</template>
