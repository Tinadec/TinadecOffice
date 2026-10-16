<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { RuntimeReadinessReceiptDto } from '../api'
import StatusPill from './StatusPill.vue'

/** Core's unified readiness receipt (GET /api/v1/readiness): the overall status and its fixed checks. */
const props = defineProps<{
  readiness: RuntimeReadinessReceiptDto | null
}>()

const { t, te, locale } = useI18n()
/** A status or check name in words; one this build does not know is shown as it came. */
const statusLabel = (status: string) => te(`doctor.status.${status}`) ? t(`doctor.status.${status}`) : status
const itemName = (id: string) => te(`doctor.item.${id}`) ? t(`doctor.item.${id}`) : id

const counts = computed(() => {
  const items = props.readiness?.items ?? []
  return {
    ready: items.filter((item) => item.status === 'ready').length,
    warning: items.filter((item) => item.status === 'degraded' || item.status === 'unavailable').length,
    blocked: items.filter((item) => item.status === 'blocked').length,
  }
})
const checkedAt = computed(() => {
  const value = props.readiness?.checked_at ? new Date(props.readiness.checked_at) : null
  return value && !Number.isNaN(value.getTime()) ? value.toLocaleTimeString(locale.value) : ''
})
</script>

<template>
  <section v-if="readiness" class="panel runtime-readiness-panel">
    <div class="readiness-head">
      <div>
        <strong>{{ t('doctor.readiness') }}</strong>
        <span v-if="checkedAt">{{ t('doctor.checkedAt', { time: checkedAt }) }}</span>
      </div>
      <StatusPill :status="readiness.status" :label="statusLabel(readiness.status)" />
    </div>

    <div class="readiness-metrics">
      <span><strong>{{ counts.ready }}</strong> {{ t('doctor.counts.ready') }}</span>
      <span><strong>{{ counts.warning }}</strong> {{ t('doctor.counts.warning') }}</span>
      <span><strong>{{ counts.blocked }}</strong> {{ t('doctor.counts.blocked') }}</span>
    </div>

    <div class="readiness-components">
      <div v-for="item in readiness.items" :key="item.id" class="readiness-component" :data-item="item.id">
        <div class="readiness-component-head">
          <StatusPill :status="item.status" :label="statusLabel(item.status)" />
          <strong>{{ itemName(item.id) }}</strong>
        </div>
        <p v-if="item.reason">{{ item.reason }}</p>
        <p v-if="item.action" class="readiness-action">{{ item.action }}</p>
      </div>
    </div>
  </section>
  <p v-else class="quiet">{{ t('doctor.unavailable') }}</p>
</template>
