<script setup lang="ts">
import DoctorTab from '@/components/DoctorTab.vue'
import CardScroll from './CardScroll.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template>
  <CardScroll>
    <DoctorTab :readiness="c.readiness.value" />
  </CardScroll>
</template>
