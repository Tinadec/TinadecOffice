<script setup lang="ts">
import DoctorTab from '@/components/DoctorTab.vue'
import { useHomeWorkbench } from '../homeController'

const home = useHomeWorkbench()
</script>

<template>
  <DoctorTab :doctor="home.doctor.value" :readiness="home.readiness.value" />
</template>

