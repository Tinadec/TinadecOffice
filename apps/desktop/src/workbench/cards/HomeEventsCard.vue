<script setup lang="ts">
import EventsTab from '@/components/EventsTab.vue'
import { useHomeWorkbench } from '../homeController'

const home = useHomeWorkbench()
</script>

<template>
  <EventsTab :events="home.recentEvents.value" />
</template>

