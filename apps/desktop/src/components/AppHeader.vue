<script setup lang="ts">
import { Minus, Search, Square, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiButton } from '@/components/ui'
import { useCommandPalette } from '@/composables/useCommandPalette'

const { t } = useI18n()
// The palette itself stays mounted once, in App.vue. This header only asks it to open:
// `openPalette` is the state, `comboLabel` is the same string the binding was registered
// with, so the tooltip can never advertise a shortcut the platform does not use.
const { open, comboLabel, openPalette } = useCommandPalette()

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
        The palette's mouse entry. This header is the live window chrome: Home and
        Chatroom render it through TinadecUI's `UieShell`, and Code/Library/Market render it
        directly — so the entry belongs here rather than on a page-specific bar, and not on
        `AppSidebar.vue`, which only the debug preview mounts.
      -->
      <UiButton
        variant="ghost"
        size="icon"
        class="window-btn commands"
        data-testid="app-header-commands"
        aria-haspopup="dialog"
        :aria-expanded="open"
        :aria-label="t('palette.title')"
        :aria-keyshortcuts="comboLabel"
        :title="t('palette.openWithShortcut', { combo: comboLabel })"
        @click="openPalette()"
      >
        <Search :size="14" />
      </UiButton>
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
  </header>
</template>
