<script setup lang="ts">
import OrganizationPanel from '@/components/organization/OrganizationPanel.vue'
import { homeController } from '@/controllers/HomeController'

const c = homeController
</script>

<template vapor>
  <OrganizationPanel :session-id="c.selectedSessionId.value" />
</template>
