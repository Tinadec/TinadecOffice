<script setup lang="ts">
import { Copy, Minus, Square, X } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiButton } from '@/components/ui'
import BrandLogo from '@/components/BrandLogo.vue'
import CommandPaletteButton from '@/components/CommandPaletteButton.vue'
import TinadecCalligraphy from '@/components/TinadecCalligraphy.vue'
import { usePlatform } from '@/composables/usePlatform'
import { useHostAccess } from '@/lib/hostAccess'

const { t } = useI18n()
const hostAccess = useHostAccess()
const isElectron = computed(() => hostAccess.isElectron?.value ?? true)
const { isMac, isElectronMac } = usePlatform()
withDefaults(defineProps<{ showSearch?: boolean }>(), { showSearch: true })

// There is no main-process maximize event today, so the icon follows the window's
// outer size: at the screen's working area the window is effectively maximized.
const maximized = ref(false)

function syncMaximized() {
  maximized.value =
    window.outerWidth >= window.screen.availWidth - 4 &&
    window.outerHeight >= window.screen.availHeight - 4
}

onMounted(() => {
  syncMaximized()
  window.addEventListener('resize', syncMaximized)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', syncMaximized)
})

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
  <header class="topbar">
    <div class="window-controls">
      <!--
        The palette itself stays mounted once, in App.vue; this row only asks it to open.
        This header is the live window chrome: Home renders it through TinadecUI's
        `UieShell`, and Code/Library/Market render it directly. Workbench, Settings, Governance and
        Snapshots have their own bars and mount `CommandPaletteButton` themselves.
      -->
      <CommandPaletteButton v-if="showSearch" />
      <div
        v-if="isElectronMac"
        class="brand topbar-brand"
        data-testid="macos-topbar-brand"
        aria-hidden="true"
      >
        <BrandLogo :size="14" class="brand-logo-icon" />
        <TinadecCalligraphy :size="14" class="brand-calligraphy" />
      </div>
      <template v-if="isElectron && !isMac">
      <UiButton variant="ghost" size="icon" class="window-btn minimize" :title="t('app.minimize')" :aria-label="t('app.minimize')" @click="minimizeWindow">
        <Minus :size="14" aria-hidden="true" />
      </UiButton>
      <UiButton
        variant="ghost"
        size="icon"
        class="window-btn maximize"
        :title="maximized ? t('app.restore') : t('app.maximize')"
        :aria-label="maximized ? t('app.restore') : t('app.maximize')"
        @click="maximizeWindow"
      >
        <Copy v-if="maximized" :size="11" aria-hidden="true" />
        <Square v-else :size="12" aria-hidden="true" />
      </UiButton>
      <UiButton variant="ghost" size="icon" class="window-btn close" :title="t('app.close')" :aria-label="t('app.close')" @click="closeWindow">
        <X :size="14" aria-hidden="true" />
      </UiButton>
      </template>
    </div>
  </header>
</template>
