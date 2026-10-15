<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { DoctorReportDto, RuntimeReadinessReceiptDto } from '../api'
import StatusPill from './StatusPill.vue'

defineProps<{
  doctor: DoctorReportDto | null
  readiness: RuntimeReadinessReceiptDto | null
}>()

const { t, te } = useI18n()
/** A readiness or check status in words; one this build does not know is shown as it came. */
const statusLabel = (status: string) => te(`doctor.status.${status}`) ? t(`doctor.status.${status}`) : status
</script>

<template>
  <section v-if="readiness" class="panel runtime-readiness-panel">
    <div class="readiness-head">
      <div>
        <strong>{{ t('doctor.readiness') }}</strong>
        <span>{{ readiness.runtime }}</span>
      </div>
      <StatusPill :status="readiness.status" :label="statusLabel(readiness.status)" />
    </div>

    <div class="readiness-metrics">
      <span><strong>{{ readiness.ready_count }}</strong> {{ t('doctor.counts.ready') }}</span>
      <span><strong>{{ readiness.warning_count }}</strong> {{ t('doctor.counts.warning') }}</span>
      <span><strong>{{ readiness.blocked_count }}</strong> {{ t('doctor.counts.blocked') }}</span>
    </div>

    <div class="readiness-components">
      <div v-for="component in readiness.components" :key="component.id" class="readiness-component">
        <div class="readiness-component-head">
          <StatusPill :status="component.status" :label="statusLabel(component.status)" />
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
      <span>{{ t('doctor.dependencies') }}</span>
    </div>
    <div v-for="check in doctor?.checks ?? []" :key="check.name" class="doctor-row">
      <StatusPill :status="check.status" :label="statusLabel(check.status)" />
      <strong>{{ check.name }}</strong>
    </div>
  </section>
</template>
