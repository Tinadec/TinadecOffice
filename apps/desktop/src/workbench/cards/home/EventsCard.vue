<script setup lang="ts">
import EventsTab from '@/components/EventsTab.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template vapor>
  <EventsTab :events="c.recentEvents.value" />
</template>
