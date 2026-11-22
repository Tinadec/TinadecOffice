<script setup lang="ts">
import { computed, inject, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Activity, Stethoscope } from '@lucide/vue'
import DoctorTab from '@/components/DoctorTab.vue'
import EventsTab from '@/components/EventsTab.vue'
import CardScroll from './CardScroll.vue'
import { homeController } from '@/controllers/HomeController'

export type DiagnosticsSubTab = 'readiness' | 'events'

const props = withDefaults(
  defineProps<{
    initialTab?: DiagnosticsSubTab
  }>(),
  {
    initialTab: 'readiness',
  },
)

const { t, te } = useI18n()
const c = homeController
const cardState = inject<Record<string, unknown> | undefined>('uie:cardState', undefined)

const activeTab = ref<DiagnosticsSubTab>(
  cardState?.tab === 'events' ? 'events' : props.initialTab,
)

const eventCount = computed(() => c.recentEvents?.value?.length ?? 0)

function label(key: string, fallback: string): string {
  return te(key) ? String(t(key)) : fallback
}

function onSelectTabEvent(event: Event): void {
  const detail = (event as CustomEvent<{ tab?: DiagnosticsSubTab }>).detail
  if (detail?.tab === 'events' || detail?.tab === 'readiness') {
    activeTab.value = detail.tab
  }
}

watch(
  () => cardState?.tab,
  (tab) => {
    if (tab === 'events' || tab === 'readiness') activeTab.value = tab
  },
)

onMounted(() => {
  window.addEventListener('tinadec:doctor-tab', onSelectTabEvent)
})

onBeforeUnmount(() => {
  window.removeEventListener('tinadec:doctor-tab', onSelectTabEvent)
})
</script>

<template>
  <CardScroll>
    <div class="doctor-unified" data-testid="doctor-unified-card">
      <div class="doctor-subtabs" role="tablist">
        <button
          type="button"
          role="tab"
          class="doctor-subtab"
          :class="{ active: activeTab === 'readiness' }"
          :aria-selected="activeTab === 'readiness'"
          data-testid="doctor-tab-readiness"
          @click="activeTab = 'readiness'"
        >
          <Stethoscope :size="13" aria-hidden="true" />
          <span>{{ label('context.doctorTabReadiness', 'Environment Readiness') }}</span>
        </button>
        <button
          type="button"
          role="tab"
          class="doctor-subtab"
          :class="{ active: activeTab === 'events' }"
          :aria-selected="activeTab === 'events'"
          data-testid="doctor-tab-events"
          @click="activeTab = 'events'"
        >
          <Activity :size="13" aria-hidden="true" />
          <span>{{ label('context.doctorTabEvents', 'Live Event Stream') }}</span>
          <span v-if="eventCount > 0" class="doctor-subtab-badge">{{ eventCount }}</span>
        </button>
      </div>

      <DoctorTab v-if="activeTab === 'readiness'" :readiness="c.readiness.value" />
      <EventsTab v-else :events="c.recentEvents.value" />
    </div>
  </CardScroll>
</template>

<style scoped>
.doctor-unified {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 10px 12px 12px;
  min-width: 0;
}
.doctor-unified :deep(.panel) {
  padding: 0;
}
.doctor-subtabs {
  display: flex; align-items: center; gap: 4px; padding: 3px;
  border-radius: 9px; background: var(--surface-raised); border: 1px solid var(--border-muted);
}
.doctor-subtab {
  flex: 1; display: inline-flex; align-items: center; justify-content: center; gap: 6px;
  padding: 6px 10px; border: 0; border-radius: 7px; background: transparent;
  color: var(--text-secondary); font-size: 12px; font-weight: 500; cursor: pointer;
}
.doctor-subtab.active {
  background: var(--surface-section); color: var(--text-primary); font-weight: 600;
  box-shadow: var(--shadow-subtle);
}
.doctor-subtab-badge {
  padding: 1px 6px; border-radius: 999px; font-size: 10px; font-variant-numeric: tabular-nums;
  background: color-mix(in srgb, var(--accent-primary) 14%, transparent); color: var(--accent-primary);
}
</style>
