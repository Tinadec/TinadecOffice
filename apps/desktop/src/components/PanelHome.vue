<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { LucideIcon } from '@lucide/vue'
import type { PanelType } from '../composables/usePanelTabs'
import { panelIcons } from '../composables/usePanelTabs'

const { t } = useI18n()

const emit = defineEmits<{
  'open-panel': [type: PanelType, title: string, icon: LucideIcon]
}>()

interface FeatureCard {
  type: PanelType
  titleKey: string
  descKey: string
  icon: LucideIcon
  color: string
  badge?: () => number
}

const props = defineProps<{
  pendingApprovalCount: number
  compact?: boolean
}>()

const features = computed<FeatureCard[]>(() => [
  {
    type: 'agent',
    titleKey: 'context.homeAgent',
    descKey: 'context.homeAgentDesc',
    icon: panelIcons.Bot,
    color: '#58a6ff',
  },
  {
    type: 'terminal',
    titleKey: 'context.homeTerminal',
    descKey: 'context.homeTerminalDesc',
    icon: panelIcons.TerminalSquare,
    color: '#3fb950',
  },
  {
    type: 'git',
    titleKey: 'context.homeGit',
    descKey: 'context.homeGitDesc',
    icon: panelIcons.GitBranch,
    color: '#f1502f',
  },
  {
    type: 'approval',
    titleKey: 'context.homeApproval',
    descKey: 'context.homeApprovalDesc',
    icon: panelIcons.ShieldCheck,
    color: '#d29922',
    badge: () => props.pendingApprovalCount,
  },
  {
    type: 'orchestration',
    titleKey: 'context.homeOrchestration',
    descKey: 'context.homeOrchestrationDesc',
    icon: panelIcons.Layers3,
    color: '#a371f7',
  },
  {
    type: 'preview',
    titleKey: 'context.homePreview',
    descKey: 'context.homePreviewDesc',
    icon: panelIcons.Globe,
    color: '#58a6ff',
  },
  {
    type: 'events',
    titleKey: 'context.homeEvents',
    descKey: 'context.homeEventsDesc',
    icon: panelIcons.Activity,
    color: '#7d8590',
  },
  {
    type: 'doctor',
    titleKey: 'context.homeDoctor',
    descKey: 'context.homeDoctorDesc',
    icon: panelIcons.Stethoscope,
    color: '#3fb950',
  },
])

function handleClick(feature: FeatureCard) {
  const title = t(feature.titleKey)
  emit('open-panel', feature.type, title, feature.icon)
}
</script>

<template>
  <section class="panel-home" :class="{ 'panel-home-compact': props.compact }">
    <div class="panel-home-header">
      <h2>{{ t('context.homeTitle') }}</h2>
      <p v-if="!props.compact">{{ t('context.homeSubtitle') }}</p>
    </div>

    <div class="panel-home-grid">
      <button
        v-for="feature in features"
        :key="feature.type"
        class="panel-home-card"
        :class="{ 'panel-home-card-compact': props.compact }"
        @click="handleClick(feature)"
      >
        <div class="panel-home-card-icon" :style="{ '--card-color': feature.color }">
          <component :is="feature.icon" :size="props.compact ? 18 : 22" />
        </div>
        <div class="panel-home-card-body">
          <span class="panel-home-card-title">{{ t(feature.titleKey) }}</span>
          <span v-if="!props.compact" class="panel-home-card-desc">{{ t(feature.descKey) }}</span>
        </div>
        <span
          v-if="feature.badge && feature.badge() > 0"
          class="panel-home-card-badge"
        >
          {{ feature.badge() }}
        </span>
      </button>
    </div>

    <div v-if="!props.compact" class="panel-home-footer">
      <span>{{ t('context.homeFooterHint') }}</span>
    </div>
  </section>
</template>

<style scoped>
.panel-home {
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 18px 14px;
  height: 100%;
  overflow-y: auto;
}

.panel-home-header {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.panel-home-header h2 {
  margin: 0;
  font-size: 15px;
  font-weight: 700;
  color: var(--text-primary);
}

.panel-home-header p {
  margin: 0;
  font-size: 12px;
  color: var(--text-muted);
  line-height: 1.4;
}

.panel-home-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
}

/* Compact mode: single column, reduced padding */
.panel-home-compact {
  padding: 12px 10px;
  gap: 10px;
}

.panel-home-compact .panel-home-grid {
  grid-template-columns: 1fr;
  gap: 6px;
}

.panel-home-card-compact {
  flex-direction: row;
  align-items: center;
  padding: 8px 10px;
  gap: 10px;
}

.panel-home-card-compact .panel-home-card-icon {
  width: 28px;
  height: 28px;
  flex-shrink: 0;
}

.panel-home-card-compact .panel-home-card-body {
  flex-direction: column;
  gap: 0;
}

.panel-home-card {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 8px;
  padding: 12px 10px;
  background: var(--surface-raised);
  border: 1px solid transparent;
  border-radius: 10px;
  box-shadow: var(--shadow-subtle);
  cursor: pointer;
  transition: all 0.18s ease;
  text-align: left;
  overflow: hidden;
}

.panel-home-card::before {
  content: '';
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
  height: 2px;
  background: var(--card-color, var(--accent-primary));
  opacity: 0;
  transition: opacity 0.18s ease;
}

.panel-home-card:hover {
  background: var(--surface-hover);
  border-color: var(--card-color, var(--accent-primary));
  box-shadow: var(--shadow-panel);
  transform: translateY(-1px);
}

.panel-home-card:hover::before {
  opacity: 0.7;
}

.panel-home-card:active {
  transform: translateY(0);
}

.panel-home-card-icon {
  display: grid;
  place-items: center;
  width: 36px;
  height: 36px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--card-color, var(--accent-primary)) 14%, transparent);
  color: var(--card-color, var(--accent-primary));
}

.panel-home-card-body {
  display: flex;
  flex-direction: column;
  gap: 2px;
  width: 100%;
}

.panel-home-card-title {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.panel-home-card-desc {
  font-size: 11px;
  color: var(--text-muted);
  line-height: 1.35;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.panel-home-card-badge {
  position: absolute;
  top: 8px;
  right: 8px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  font-size: 10px;
  font-weight: 700;
  color: #fff;
  background: var(--accent-primary);
  border-radius: 999px;
}

.panel-home-footer {
  margin-top: auto;
  padding-top: 12px;
  text-align: center;
}

.panel-home-footer span {
  font-size: 11px;
  color: var(--text-muted);
}
</style>
