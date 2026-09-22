<script setup lang="ts">
import { Minus, Square, X } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiButton } from '@/components/ui'
import CommandPaletteButton from '@/components/CommandPaletteButton.vue'

const { t } = useI18n()

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
        This header is the live window chrome: Home and Chatroom render it through TinadecUI's
        `UieShell`, and Code/Library/Market render it directly. Workbench, Settings, Governance and
        Snapshots have their own bars and mount `CommandPaletteButton` themselves.
      -->
      <CommandPaletteButton />
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
