<script setup lang="ts">
import type { DoctorReportDto, RuntimeReadinessReceiptDto } from '../api'
import StatusPill from './StatusPill.vue'

defineProps<{
  doctor: DoctorReportDto | null
  readiness: RuntimeReadinessReceiptDto | null
}>()
</script>

<template>
  <section v-if="readiness" class="panel runtime-readiness-panel">
    <div class="readiness-head">
      <div>
        <strong>Runtime Readiness</strong>
        <span>{{ readiness.runtime }}</span>
      </div>
      <StatusPill :status="readiness.status" />
    </div>

    <div class="readiness-metrics">
      <span><strong>{{ readiness.ready_count }}</strong> ready</span>
      <span><strong>{{ readiness.warning_count }}</strong> warning</span>
      <span><strong>{{ readiness.blocked_count }}</strong> blocked</span>
    </div>

    <div class="readiness-components">
      <div v-for="component in readiness.components" :key="component.id" class="readiness-component">
        <div class="readiness-component-head">
          <StatusPill :status="component.status" />
          <strong>{{ component.name }}</strong>
        </div>
        <p>{{ component.summary }}</p>
        <div class="readiness-evidence">
          <span v-for="item in component.evidence.slice(0, 3)" :key="item">{{ item }}</span>
        </div>
      </div>
    </div>
  </section>

  <section class="panel">
    <div class="panel-title">
      <span>Dependency Doctor</span>
    </div>
    <div v-for="check in doctor?.checks ?? []" :key="check.name" class="doctor-row">
      <StatusPill :status="check.status" />
      <strong>{{ check.name }}</strong>
    </div>
  </section>
</template>
