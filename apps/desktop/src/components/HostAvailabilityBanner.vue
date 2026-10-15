<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Info, ShieldAlert, WifiOff } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import { useHostAccess } from '@/lib/hostAccess'
import { retryConnection } from '@/composables/useConnection'
import { usePanelStyles } from '@/composables/usePanelStyles'

const { status } = useHostAccess()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const { t } = useI18n()
const pending = ref(false)
const restartFailed = ref(false)
const visible = computed(() => Boolean(status.value && status.value.state !== 'ready') || pending.value)
const preview = computed(() => status.value?.state === 'preview')
const rejected = computed(() => status.value?.state === 'rejected')
const restartRequired = computed(() => status.value?.state === 'restart_required')
const development = import.meta.env.DEV
const restartAvailable = computed(() => !development && typeof window.tinadec?.restartApp === 'function')
const title = computed(() => t(restartRequired.value ? 'hostBanner.restartTitle' : preview.value ? 'hostBanner.previewTitle' : rejected.value ? 'hostBanner.rejectedTitle' : 'hostBanner.unavailableTitle'))
const description = computed(() => t(status.value?.state === 'checking' ? 'hostBanner.checking' : restartRequired.value ? 'hostBanner.restartText' : preview.value ? 'hostBanner.previewText' : rejected.value ? 'hostBanner.rejectedText' : 'hostBanner.unavailableText'))
const restartGuidance = computed(() => restartRequired.value
  ? development ? t('hostBanner.developerRestartText') : !restartAvailable.value ? t('hostBanner.manualRestartText') : restartFailed.value ? t('hostBanner.restartFailedText') : '' : '')
const actionLabel = computed(() => t(restartRequired.value ? pending.value ? 'hostBanner.restarting' : 'hostBanner.restart' : pending.value ? 'hostBanner.retrying' : 'hostBanner.retry'))
async function recover() {
  if (pending.value) return
  pending.value = true
  try {
    if (restartRequired.value) {
      restartFailed.value = false
      if (!restartAvailable.value) return
      try { await window.tinadec.restartApp() } catch { restartFailed.value = true }
    } else await retryConnection()
  } finally { pending.value = false }
}
</script>

<template>
  <aside v-if="visible" class="host-availability-banner no-drag" role="status" aria-live="polite"
    :style="getPanelStyle()" v-bind="getPanelDataAttributes()">
    <Info v-if="preview" :size="18" aria-hidden="true" />
    <ShieldAlert v-else-if="rejected || restartRequired" :size="18" aria-hidden="true" />
    <WifiOff v-else :size="18" aria-hidden="true" />
    <div class="host-availability-copy"><strong>{{ title }}</strong><p>{{ description }}</p><p v-if="restartGuidance">{{ restartGuidance }}</p></div>
    <UiButton v-if="!preview && (!restartRequired || restartAvailable)" variant="outline" size="sm" :disabled="pending" @click="recover">
      {{ actionLabel }}
    </UiButton>
  </aside>
</template>

<style scoped>
.host-availability-banner { position: fixed; z-index: 90; inset: auto 16px 16px; margin-inline: auto; width: fit-content; max-width: min(720px, calc(100vw - 32px)); display: flex; align-items: center; gap: 12px; padding: 12px 16px; border: 1px solid var(--border-muted); border-radius: var(--radius-md); background: var(--surface-raised); color: var(--text-primary); box-shadow: var(--shadow-card-subtle); }
.host-availability-banner > svg { flex-shrink: 0; color: var(--text-secondary); }
.host-availability-copy { min-width: 0; font-size: 12px; }
.host-availability-copy strong { font-size: 13px; font-weight: 600; }
.host-availability-copy p { margin: 3px 0 0; color: var(--text-secondary); overflow-wrap: anywhere; }
.host-availability-banner > button { flex-shrink: 0; }
@media (max-width: 640px) { .host-availability-banner { flex-wrap: wrap; } .host-availability-copy { flex: 1; } }
</style>
