<script setup lang="ts">
import {
  Bot,
  Boxes,
  CheckCircle2,
  Download,
  Globe2,
  PlugZap,
  ShieldCheck,
  Terminal,
  ToggleLeft,
  ToggleRight,
  Trash2,
} from '@lucide/vue'
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { UiButton, UiInput } from '@/components/ui'
import { marketController } from '@/controllers/MarketController'

const { t } = useI18n()

const {
  busy, preview, directPreview, directForm,
  selectedItem, selectedInstalled, selectedRuntime,
  start, approveAndInstallCatalog, previewDirectInstall, approveAndInstallDirect,
  toggleExtension, removeExtension,
} = marketController

function kindIcon(kind: string) {
  if (kind === 'skill') return Bot
  if (kind === 'mcp-server') return PlugZap
  if (kind === 'acp-adapter') return Terminal
  return Boxes
}

onMounted(() => {
  start()
})
</script>

<template vapor>
  <aside class="market-detail">
    <template v-if="selectedItem">
      <div class="market-detail-head">
        <div class="market-detail-icon" :class="selectedItem.kind">
          <component :is="kindIcon(selectedItem.kind)" :size="22" />
        </div>
        <div>
          <h2>{{ selectedItem.display_name }}</h2>
          <p>{{ selectedItem.extension_id }}</p>
        </div>
      </div>

      <p class="market-detail-copy">{{ selectedItem.description }}</p>

      <div class="market-status-strip" :class="{ enabled: selectedInstalled?.enabled }">
        <CheckCircle2 v-if="selectedInstalled?.enabled" :size="16" />
        <ShieldCheck v-else :size="16" />
        <span>{{ selectedInstalled?.status_message ?? t('market.notInstalled') }}</span>
      </div>

      <div class="market-detail-grid">
        <div>
          <span>{{ t('market.source') }}</span>
          <strong>{{ selectedItem.source_kind }}</strong>
        </div>
        <div>
          <span>{{ t('market.version') }}</span>
          <strong>{{ selectedItem.version }}</strong>
        </div>
      </div>

      <div class="market-section">
        <h3>{{ t('market.capabilities') }}</h3>
        <div class="market-chip-row wrap">
          <span v-for="capability in selectedItem.capabilities" :key="capability">{{ capability }}</span>
        </div>
      </div>

      <div class="market-section">
        <h3>{{ t('market.permissions') }}</h3>
        <div class="market-chip-row wrap">
          <span v-for="permission in selectedItem.permissions" :key="permission">{{ permission }}</span>
        </div>
      </div>

      <div v-if="preview" class="market-risk-panel">
        <h3>{{ t('market.riskPreview') }}</h3>
        <ul>
          <li v-for="risk in preview.risks" :key="risk">{{ risk }}</li>
        </ul>
      </div>

      <div v-if="selectedRuntime.length > 0" class="market-section">
        <h3>{{ t('market.runtime') }}</h3>
        <div class="market-runtime-line" v-for="runtime in selectedRuntime" :key="runtime">
          <Globe2 :size="14" />
          <span>{{ runtime }}</span>
        </div>
      </div>

      <div class="market-action-row">
        <UiButton v-if="!selectedInstalled" :disabled="busy" @click="approveAndInstallCatalog">
          <Download :size="15" />
          {{ t('market.approveInstall') }}
        </UiButton>
        <UiButton v-else variant="secondary" :disabled="busy" @click="toggleExtension(selectedInstalled)">
          <component :is="selectedInstalled.enabled ? ToggleLeft : ToggleRight" :size="15" />
          {{ selectedInstalled.enabled ? t('market.disable') : t('market.enable') }}
        </UiButton>
        <UiButton v-if="selectedInstalled" variant="ghost" size="icon" :title="t('market.uninstall')" @click="removeExtension(selectedInstalled)">
          <Trash2 :size="15" />
        </UiButton>
      </div>
    </template>

    <div class="market-direct">
      <h3>{{ t('market.directInstall') }}</h3>
      <UiInput v-model="directForm.source_location" :placeholder="t('market.sourceLocation')" />
      <textarea v-model="directForm.manifest_json" class="market-textarea" :placeholder="t('market.manifestPlaceholder')" />
      <div class="market-action-row">
        <UiButton variant="secondary" size="sm" :disabled="busy || !directForm.source_location" @click="previewDirectInstall">
          <ShieldCheck :size="14" />
          {{ t('market.preview') }}
        </UiButton>
        <UiButton size="sm" :disabled="busy || !directPreview" @click="approveAndInstallDirect">
          <Download :size="14" />
          {{ t('market.approveInstall') }}
        </UiButton>
      </div>
      <div v-if="directPreview" class="market-risk-panel compact">
        <strong>{{ directPreview.display_name }}</strong>
        <ul>
          <li v-for="risk in directPreview.risks" :key="risk">{{ risk }}</li>
        </ul>
      </div>
    </div>
  </aside>
</template>

<style scoped>
.market-detail {
  display: flex;
  flex-direction: column;
  gap: 16px;
  height: 100%;
  overflow-y: auto;
  padding: 16px;
}

.market-detail-head {
  display: flex;
  align-items: center;
  gap: 12px;
}

.market-detail-head h2 {
  font-size: 16px;
  font-weight: 600;
  margin: 0;
}

.market-detail-head p {
  font-size: 12px;
  color: var(--text-secondary);
  margin: 0;
}

.market-status-strip {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-radius: 6px;
  background: var(--surface-hover);
  font-size: 13px;
}

.market-status-strip.enabled {
  color: var(--accent-success, #10b981);
}

.market-detail-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
  font-size: 13px;
}

.market-chip-row.wrap {
  flex-wrap: wrap;
}

.market-action-row {
  display: flex;
  gap: 8px;
}

.market-direct {
  display: flex;
  flex-direction: column;
  gap: 12px;
  margin-top: auto;
  padding-top: 16px;
  border-top: 1px solid var(--border-muted);
}

.market-textarea {
  min-height: 80px;
  padding: 8px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
  color: var(--text-primary);
  font-size: 12px;
  font-family: monospace;
}
</style>
