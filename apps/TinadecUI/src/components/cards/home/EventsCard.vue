<script setup lang="ts">
import EventsTab from '@/components/EventsTab.vue'
import CardScroll from './CardScroll.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template>
  <CardScroll>
    <EventsTab :events="c.recentEvents.value" />
  </CardScroll>
</template>
