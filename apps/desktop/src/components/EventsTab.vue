<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { EventEnvelope } from '../api'

defineProps<{
  events: EventEnvelope[]
}>()
const { t } = useI18n()
</script>

<template>
  <section class="panel">
    <p v-if="events.length === 0" class="quiet">{{ t('eventsPanel.empty') }}</p>
    <ol v-else class="event-list">
      <li v-for="event in events" :key="event.seq">
        <span>{{ event.type }}</span>
        <small>#{{ event.seq }}</small>
      </li>
    </ol>
  </section>
</template>
