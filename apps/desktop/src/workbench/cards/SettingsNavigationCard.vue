<script setup lang="ts">
import {
  ArrowLeft, Bot, Dna, FileText, GitBranch, Globe, Info, KeyRound, Palette, PawPrint, Settings2, Terminal, Workflow,
} from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { UiButton } from '@/components/ui'
import { useSettingsWorkbench, type SettingsSection } from '../settingsController'

const { t } = useI18n()
const router = useRouter()
const settings = useSettingsWorkbench()

const navItems = computed(() => ([
  ['general', Settings2], ['model', KeyRound], ['agents', Workflow], ['agentEvolution', Dna],
  ['promptContext', Bot], ['promptEngineering', GitBranch], ['tools', Terminal], ['appearance', Palette],
  ['pets', PawPrint], ['language', Globe], ['apiDocs', FileText], ['about', Info],
] as const).map(([key, icon]) => ({ key: key as SettingsSection, icon, label: t(`settings.${key}`) })))

function select(section: SettingsSection): void {
  settings.selectSection(section)
}
</script>

<template>
  <nav class="settings-workbench-nav">
    <div class="settings-nav-header">
      <UiButton variant="ghost" size="icon" :title="t('settings.back')" @click="router.push('/')">
        <ArrowLeft />
      </UiButton>
      <span>{{ t('settings.title') }}</span>
    </div>
    <UiButton
      v-for="item in navItems"
      :key="item.key"
      variant="ghost"
      size="sm"
      class="settings-nav-item w-full justify-start"
      :class="{ active: settings.activeSection.value === item.key }"
      :aria-current="settings.activeSection.value === item.key ? 'page' : undefined"
      @click="select(item.key)"
    >
      <component :is="item.icon" />
      {{ item.label }}
    </UiButton>
  </nav>
</template>

<style scoped>
.settings-workbench-nav { width: 100%; height: 100%; padding: 12px 10px; overflow: auto; background: var(--surface-section); }
.settings-nav-header { display: flex; align-items: center; gap: 8px; padding-bottom: 10px; margin-bottom: 6px; color: var(--text-primary); font-weight: 700; }
:deep(.settings-nav-item) { gap: 8px; color: var(--text-secondary); }
:deep(.settings-nav-item.active) { color: var(--accent-primary); background: var(--surface-selected); }
</style>
