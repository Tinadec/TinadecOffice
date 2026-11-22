<script setup lang="ts">
import { computed } from 'vue'
import { Search } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { UiButton } from '@/components/ui'
import { openPalette, useCommandPalette } from '@/composables/useCommandPalette'
import { usePlatform } from '@/composables/usePlatform'
import { PALETTE_COMBO, formatCombo } from '@/lib/keybindings'

const { t } = useI18n()
const { open } = useCommandPalette()
const { isMac } = usePlatform()
const comboLabel = computed(() => formatCombo(PALETTE_COMBO, isMac.value))
</script>

<template>
  <UiButton
    variant="ghost"
    size="icon"
    class="window-btn commands palette-entry no-drag"
    data-testid="command-palette-button"
    aria-haspopup="dialog"
    :aria-expanded="open"
    :aria-label="t('palette.title')"
    :aria-keyshortcuts="comboLabel"
    :title="t('palette.openWithShortcut', { combo: comboLabel })"
    @click="openPalette()"
  >
    <Search data-icon="inline-start" aria-hidden="true" />
  </UiButton>
</template>

<style scoped>
.palette-entry {
  width: 36px;
  height: 32px;
  padding: 0;
  border: 0;
  border-radius: 0;
  background: transparent;
  box-shadow: none;
  color: var(--text-secondary);
  -webkit-app-region: no-drag;
}
.palette-entry:hover { background: transparent; color: var(--text-primary); }
.palette-entry:focus-visible { outline: 2px solid var(--text-brand); outline-offset: -2px; }
</style>
