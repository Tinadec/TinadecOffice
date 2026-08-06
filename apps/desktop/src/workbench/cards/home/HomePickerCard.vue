<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Bot,
  TerminalSquare,
  GitBranch,
  ShieldCheck,
  Layers3,
  Globe,
  Activity,
  Stethoscope,
  type LucideIcon,
} from '@lucide/vue'
import { useWorkbench } from '../../useWorkbench'
import { homeController } from '@/controllers/HomeController'

const { t } = useI18n()
const wb = useWorkbench()
const c = homeController

interface FeatureCard {
  descriptorId: string
  titleKey: string
  descKey: string
  icon: LucideIcon
  color: string
  badge?: () => number
}

const features = computed<FeatureCard[]>(() => [
  { descriptorId: 'agent', titleKey: 'context.homeAgent', descKey: 'context.homeAgentDesc', icon: Bot, color: '#58a6ff' },
  { descriptorId: 'terminal', titleKey: 'context.homeTerminal', descKey: 'context.homeTerminalDesc', icon: TerminalSquare, color: '#3fb950' },
  { descriptorId: 'git', titleKey: 'context.homeGit', descKey: 'context.homeGitDesc', icon: GitBranch, color: '#f1502f' },
  { descriptorId: 'approval', titleKey: 'context.homeApproval', descKey: 'context.homeApprovalDesc', icon: ShieldCheck, color: '#d29922', badge: () => c.approvals.value.filter((a) => a.status === 'pending').length },
  { descriptorId: 'orchestration', titleKey: 'context.homeOrchestration', descKey: 'context.homeOrchestrationDesc', icon: Layers3, color: '#a371f7' },
  { descriptorId: 'browser', titleKey: 'context.homePreview', descKey: 'context.homePreviewDesc', icon: Globe, color: '#58a6ff' },
  { descriptorId: 'events', titleKey: 'context.homeEvents', descKey: 'context.homeEventsDesc', icon: Activity, color: '#7d8590' },
  { descriptorId: 'doctor', titleKey: 'context.homeDoctor', descKey: 'context.homeDoctorDesc', icon: Stethoscope, color: '#3fb950' },
])

function openCard(descriptorId: string) {
  wb.dispatch({
    command: { type: 'openCard', scope: wb.scope.value, descriptorId },
    source: 'user',
    expectedRevision: wb.snapshot.value.revision,
  })
}
</script>

<template>
  <section class="panel-home">
    <div class="panel-home-header">
      <h2>{{ t('context.homeTitle') }}</h2>
      <p>{{ t('context.homeSubtitle') }}</p>
    </div>

    <div class="panel-home-grid">
      <button
        v-for="feature in features"
        :key="feature.descriptorId"
        class="panel-home-card"
        @click="openCard(feature.descriptorId)"
      >
        <div class="panel-home-card-icon" :style="{ '--card-color': feature.color }">
          <component :is="feature.icon" :size="22" />
        </div>
        <div class="panel-home-card-body">
          <span class="panel-home-card-title">{{ t(feature.titleKey) }}</span>
          <span class="panel-home-card-desc">{{ t(feature.descKey) }}</span>
        </div>
        <span v-if="feature.badge && feature.badge() > 0" class="panel-home-card-badge">
          {{ feature.badge() }}
        </span>
      </button>
    </div>

    <div class="panel-home-footer">
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
.panel-home-header { display: flex; flex-direction: column; gap: 4px; }
.panel-home-header h2 { margin: 0; font-size: 15px; font-weight: 700; color: var(--text-primary); }
.panel-home-header p { margin: 0; font-size: 12px; color: var(--text-muted); line-height: 1.4; }
.panel-home-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
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
  position: absolute; top: 0; left: 0; right: 0; height: 2px;
  background: var(--card-color, var(--accent-primary)); opacity: 0; transition: opacity 0.18s ease;
}
.panel-home-card:hover {
  background: var(--surface-hover); border-color: var(--card-color, var(--accent-primary));
  box-shadow: var(--shadow-panel); transform: translateY(-1px);
}
.panel-home-card:hover::before { opacity: 0.7; }
.panel-home-card:active { transform: translateY(0); }
.panel-home-card-icon {
  display: grid; place-items: center; width: 36px; height: 36px; border-radius: 8px;
  background: color-mix(in srgb, var(--card-color, var(--accent-primary)) 14%, transparent);
  color: var(--card-color, var(--accent-primary));
}
.panel-home-card-body { display: flex; flex-direction: column; gap: 2px; width: 100%; }
.panel-home-card-title { font-size: 12.5px; font-weight: 600; color: var(--text-primary); }
.panel-home-card-desc { font-size: 11px; color: var(--text-muted); line-height: 1.35; }
.panel-home-card-badge {
  position: absolute; top: 8px; right: 8px;
  display: inline-flex; align-items: center; justify-content: center;
  min-width: 18px; height: 18px; padding: 0 5px; font-size: 10px; font-weight: 700;
  color: #fff; background: var(--accent-primary); border-radius: 999px;
}
.panel-home-footer { margin-top: auto; padding-top: 12px; text-align: center; }
.panel-home-footer span { font-size: 11px; color: var(--text-muted); }
</style>
