<script setup lang="ts">
import { ArrowLeft, Bot, Dna, FileText, GitBranch, Globe, Info, KeyRound, Palette, Square, Terminal, Workflow, X, Minus } from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import { UiButton } from '@/components/ui'
import ProviderEditorModal from '@/components/settings/ProviderEditorModal.vue'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { settingsNavigation, settingsNavigationItems, type SettingsNavigationItem, type SettingsRouteName } from './settingsNavigation'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()

const settingsNavStyle = computed(() => getPanelStyle())
const settingsNavDataAttrs = computed(() => getPanelDataAttributes())
const settingsContentStyle = computed(() => getPanelStyle())
const settingsContentDataAttrs = computed(() => getPanelDataAttributes())

const iconMap = {
  key: KeyRound,
  workflow: Workflow,
  dna: Dna,
  bot: Bot,
  branch: GitBranch,
  terminal: Terminal,
  palette: Palette,
  globe: Globe,
  file: FileText,
  info: Info,
} as const

function isSettingsRouteName(name: unknown): name is SettingsRouteName {
  return settingsNavigationItems.some((item) => item.name === name)
}

const activeRouteName = computed<SettingsRouteName>(() => isSettingsRouteName(route.name) ? route.name : 'settings-model')
const activeItem = computed(() => settingsNavigationItems.find((item) => item.name === activeRouteName.value) ?? settingsNavigationItems[0])

function destinationPath(item: SettingsNavigationItem) {
  return { name: item.name }
}

function navigateToSetting(event: Event) {
  if (!(event.target instanceof HTMLSelectElement)) return
  void router.push({ name: event.target.value })
}

function minimizeWindow() {
  window.tinadec?.minimizeWindow?.()
}

function maximizeWindow() {
  window.tinadec?.maximizeWindow?.()
}

function closeWindow() {
  window.tinadec?.closeWindow?.()
}
</script>

<template>
  <div class="settings-page">
    <div class="top-drag-bar" />
    <div class="settings-window-controls">
      <UiButton variant="ghost" size="icon" class="window-btn minimize" :title="t('app.minimize')" @click="minimizeWindow">
        <Minus :size="14" />
      </UiButton>
      <UiButton variant="ghost" size="icon" class="window-btn maximize" :title="t('app.maximize')" @click="maximizeWindow">
        <Square :size="12" />
      </UiButton>
      <UiButton variant="ghost" size="icon" class="window-btn close" :title="t('app.close')" @click="closeWindow">
        <X :size="14" />
      </UiButton>
    </div>

    <div class="settings-shell">
      <nav class="settings-nav settings-desktop-nav" :style="settingsNavStyle" v-bind="settingsNavDataAttrs" :aria-label="t('settings.title')">
        <div class="settings-nav-header">
          <UiButton variant="ghost" size="icon" :title="t('settings.back')" @click="router.push('/')">
            <ArrowLeft :size="16" />
          </UiButton>
          <span>{{ t('settings.title') }}</span>
        </div>

        <section v-for="group in settingsNavigation" :key="group.labelKey" class="settings-nav-group">
          <h2>{{ t(group.labelKey) }}</h2>
          <RouterLink
            v-for="item in group.items"
            :key="item.name"
            :to="destinationPath(item)"
            class="settings-nav-item settings-destination-row"
            :class="{ active: activeRouteName === item.name }"
            :aria-current="activeRouteName === item.name ? 'page' : undefined"
          >
            <component :is="iconMap[item.icon]" :size="16" />
            <span class="settings-destination-copy">
              <strong>{{ t(item.labelKey) }}</strong>
              <small>{{ t(item.descriptionKey) }}</small>
            </span>
          </RouterLink>
        </section>
      </nav>

      <div class="settings-mobile-nav" :style="settingsNavStyle" v-bind="settingsNavDataAttrs">
        <UiButton variant="ghost" size="icon" :title="t('settings.back')" @click="router.push('/')">
          <ArrowLeft :size="16" />
        </UiButton>
        <label class="settings-mobile-select-label">
          <span>{{ t('settings.title') }}</span>
          <select class="settings-mobile-select" :value="activeItem.name" @change="navigateToSetting">
            <optgroup v-for="group in settingsNavigation" :key="group.labelKey" :label="t(group.labelKey)">
              <option v-for="item in group.items" :key="item.name" :value="item.name">
                {{ t(item.labelKey) }}
              </option>
            </optgroup>
          </select>
        </label>
      </div>

      <main class="settings-content" :style="settingsContentStyle" v-bind="settingsContentDataAttrs" :aria-label="activeItem ? t(activeItem.labelKey) : t('settings.title')">
        <Transition name="section-fade" mode="out-in">
          <div :key="activeRouteName" class="settings-section-wrapper">
            <RouterView />
          </div>
        </Transition>
      </main>
    </div>

    <ProviderEditorModal />
  </div>
</template>
