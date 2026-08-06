<script setup lang="ts">
import { CheckCircle2, Download, ShieldCheck, ToggleLeft, ToggleRight, Trash2 } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel } from '@/components/ui'
import { useMarketWorkbench } from '../marketController'

const market = useMarketWorkbench()
</script>

<template>
  <div class="market-details-card">
    <template v-if="market.selectedItem.value">
      <header><div><h2>{{ market.selectedItem.value.display_name }}</h2><p>{{ market.selectedItem.value.extension_id }}</p></div><UiBadge variant="outline">{{ market.selectedItem.value.version }}</UiBadge></header>
      <p>{{ market.selectedItem.value.description }}</p>
      <div class="market-detail-status"><CheckCircle2 v-if="market.selectedInstalled.value?.enabled" /><ShieldCheck v-else /><span>{{ market.selectedInstalled.value?.status_message ?? 'Not installed' }}</span></div>
      <section><h3>Capabilities</h3><div class="market-detail-chips"><span v-for="capability in market.selectedItem.value.capabilities" :key="capability">{{ capability }}</span></div></section>
      <section v-if="market.preview.value"><h3>Install review</h3><ul><li v-for="risk in market.preview.value.risks" :key="risk">{{ risk }}</li></ul></section>
      <section v-if="market.selectedRuntime.value.length"><h3>Runtime</h3><code v-for="runtime in market.selectedRuntime.value" :key="runtime">{{ runtime }}</code></section>
      <div class="market-detail-actions">
        <UiButton v-if="!market.selectedInstalled.value" :disabled="market.busy.value" @click="market.installCatalog"><Download data-icon="inline-start" />Install</UiButton>
        <template v-else>
          <UiButton variant="outline" @click="market.toggleExtension(market.selectedInstalled.value)"><ToggleRight v-if="market.selectedInstalled.value.enabled" data-icon="inline-start" /><ToggleLeft v-else data-icon="inline-start" />{{ market.selectedInstalled.value.enabled ? 'Disable' : 'Enable' }}</UiButton>
          <UiButton variant="destructive" @click="market.removeExtension(market.selectedInstalled.value)"><Trash2 data-icon="inline-start" />Uninstall</UiButton>
        </template>
      </div>
    </template>
    <div v-else class="market-detail-empty">Select an extension to inspect it.</div>
    <section class="market-direct-install">
      <h3>Direct install</h3>
      <UiLabel>Source location</UiLabel><UiInput v-model="market.directForm.source_location" placeholder="Path or URL" />
      <textarea v-model="market.directForm.manifest_json" placeholder="Optional manifest JSON" />
      <div><UiButton variant="outline" size="sm" :disabled="!market.directForm.source_location" @click="market.previewDirectInstall">Preview</UiButton><UiButton size="sm" :disabled="!market.directPreview.value" @click="market.installDirect">Approve and install</UiButton></div>
    </section>
  </div>
</template>

<style scoped>
.market-details-card { display: flex; flex-direction: column; gap: 14px; height: 100%; padding: 14px; overflow: auto; color: var(--text-secondary); }
.market-details-card header { display: flex; justify-content: space-between; gap: 12px; }
.market-details-card h2, .market-details-card h3, .market-details-card p { margin: 0; }
.market-details-card h2 { color: var(--text-primary); font-size: 16px; }
.market-details-card h3 { color: var(--text-primary); font-size: 13px; margin-bottom: 7px; }
.market-detail-status { display: flex; align-items: center; gap: 8px; padding: 10px; border-radius: 7px; background: var(--surface-raised); }
.market-detail-status svg { width: 16px; color: var(--accent-primary); }
.market-detail-chips { display: flex; flex-wrap: wrap; gap: 5px; }
.market-detail-chips span { padding: 3px 7px; border-radius: 5px; background: var(--surface-button); font-size: 11px; }
.market-detail-actions, .market-direct-install > div { display: flex; gap: 7px; }
.market-direct-install { display: flex; flex-direction: column; gap: 7px; margin-top: auto; padding-top: 12px; border-top: 1px solid var(--border-muted); }
.market-direct-install textarea { min-height: 80px; padding: 8px; border: 1px solid var(--border-default); border-radius: 6px; color: var(--text-primary); background: var(--surface-input); resize: vertical; }
.market-detail-empty { display: grid; place-items: center; min-height: 180px; color: var(--text-muted); }
</style>

