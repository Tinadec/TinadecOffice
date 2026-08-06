<script setup lang="ts">
import DoctorTab from '@/components/DoctorTab.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template>
  <DoctorTab :doctor="c.doctor.value" :readiness="c.readiness.value" />
</template>
