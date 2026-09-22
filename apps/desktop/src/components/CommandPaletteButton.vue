<script setup lang="ts">
import { Search } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiButton } from '@/components/ui'
import { useCommandPalette } from '@/composables/useCommandPalette'

const { t } = useI18n()
// One mouse entry for the palette, because every surface that needs one needs exactly this
// button: `open` drives `aria-expanded`, `comboLabel` is the string the binding was registered
// with, so no page can advertise a shortcut the platform does not use.
const { open, comboLabel, openPalette } = useCommandPalette()
</script>

<template>
  <UiButton
    variant="ghost"
    size="icon"
    class="window-btn commands"
    data-testid="command-palette-button"
    aria-haspopup="dialog"
    :aria-expanded="open"
    :aria-label="t('palette.title')"
    :aria-keyshortcuts="comboLabel"
    :title="t('palette.openWithShortcut', { combo: comboLabel })"
    @click="openPalette()"
  >
    <Search :size="14" />
  </UiButton>
</template>
