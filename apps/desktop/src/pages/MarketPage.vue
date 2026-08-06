<script setup lang="ts">
import {
  ArrowLeft,
  Bot,
  Boxes,
  CheckCircle2,
  Download,
  FolderPlus,
  Globe2,
  PlugZap,
  RefreshCw,
  Search,
  ShieldCheck,
  Store,
  Terminal,
  ToggleLeft,
  ToggleRight,
  Trash2,
  Zap,
} from '@lucide/vue'
import { onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import type { MarketCatalogItemDto } from '../api'
import AppHeader from '../components/AppHeader.vue'
import { UiBadge, UiButton, UiInput, UiLabel } from '@/components/ui'
import { marketController, kindOptions as _kindOptions, sourceKindOptions as _sourceKindOptions } from '@/controllers/MarketController'

const { t } = useI18n()
const router = useRouter()

// Destructure the controller's reactive state so the template uses the same refs.
const {
  sources, catalog, installed, mcpServers, acpAdapters,
  selectedCatalogId, kindFilter, sourceFilter, query, busy, loading,
  preview, directPreview, sourceForm, directForm,
  selectedItem, installedByExtensionId, selectedInstalled, selectedRuntime,
  start,
  loadCatalog, loadPreview, loadAll,
  addSource, refreshSource,
  approveAndInstallCatalog, previewDirectInstall, approveAndInstallDirect,
  toggleExtension, removeExtension,
} = marketController

const kindOptions = _kindOptions.map((o) => ({ ...o, icon: o.key === 'skill' ? Bot : o.key === 'mcp-server' ? PlugZap : o.key === 'acp-adapter' ? Terminal : Boxes }))
const sourceKindOptions = [..._sourceKindOptions]

function kindLabel(kind: string) {
  if (kind === 'skill') return 'Skill'
  if (kind === 'mcp-server') return 'MCP'
  if (kind === 'acp-adapter') return 'ACP'
  return kind
}

function kindIcon(kind: string) {
  if (kind === 'skill') return Bot
  if (kind === 'mcp-server') return PlugZap
  if (kind === 'acp-adapter') return Terminal
  return Boxes
}

function statusLabel(item: MarketCatalogItemDto) {
  const extension = installedByExtensionId.value.get(item.extension_id)
  if (!extension) return t('market.available')
  if (extension.enabled) return t('market.enabled')
  return t('market.installedDisabled')
}

function statusVariant(item: MarketCatalogItemDto) {
  const extension = installedByExtensionId.value.get(item.extension_id)
  if (!extension) return 'secondary'
  if (extension.enabled) return 'default'
  return 'outline'
}

onMounted(() => {
  start()
})
</script>

<template>
<main class="shell">
<!-- Full-width draggable bar for window dragging -->
<div class="top-drag-bar" />
<AppHeader :busy="busy || loading" />

    <section class="market-workspace">
      <aside class="market-rail">
        <div class="market-rail-title">
          <UiButton variant="ghost" size="icon" :title="t('settings.back')" @click="router.push('/')">
            <ArrowLeft :size="16" />
          </UiButton>
          <div>
            <h1>{{ t('market.title') }}</h1>
            <p>{{ t('market.subtitle') }}</p>
          </div>
        </div>

        <div class="market-search">
          <Search :size="15" class="market-search-icon" />
          <UiInput v-model="query" :placeholder="t('market.search')" @keyup.enter="loadCatalog" />
          <UiButton variant="ghost" size="icon" :title="t('settings.refresh')" @click="loadCatalog">
            <RefreshCw :size="15" />
          </UiButton>
        </div>

        <div class="market-filter-list">
          <UiButton
            v-for="option in kindOptions"
            :key="option.key"
            variant="ghost"
            size="sm"
            class="market-filter-button w-full justify-start"
            :class="{ active: kindFilter === option.key }"
            @click="kindFilter = option.key"
          >
            <component :is="option.icon" :size="15" />
            <span>{{ option.label }}</span>
          </UiButton>
        </div>

        <div class="market-source-box">
          <div class="market-section-head">
            <span>{{ t('market.sources') }}</span>
            <UiBadge variant="secondary">{{ sources.length }}</UiBadge>
          </div>
          <button
            v-for="source in sources"
            :key="source.id"
            class="market-source-item"
            :class="{ active: sourceFilter === source.id, 'is-builtin': source.location.includes('tinadec://') }"
            @click="sourceFilter = source.id"
          >
            <Zap v-if="source.location.includes('tinadec://')" :size="14" class="builtin-icon" />
            <Store v-else :size="14" />
            <span>{{ source.name }}</span>
            <UiButton variant="ghost" size="icon" :title="t('settings.refresh')" @click.stop="refreshSource(source.id)">
              <RefreshCw :size="13" />
            </UiButton>
          </button>
        </div>

        <div class="market-source-form">
          <UiLabel>{{ t('market.addSource') }}</UiLabel>
          <select v-model="sourceForm.kind" class="market-select">
            <option v-for="kind in sourceKindOptions" :key="kind" :value="kind">{{ kind }}</option>
          </select>
          <UiInput v-model="sourceForm.name" :placeholder="t('market.sourceName')" />
          <UiInput v-model="sourceForm.location" :placeholder="t('market.sourceLocation')" />
          <UiButton size="sm" :disabled="busy || !sourceForm.location" @click="addSource">
            <FolderPlus :size="14" />
            {{ t('market.add') }}
          </UiButton>
        </div>
      </aside>

      <section class="market-list">
        <div class="market-list-head">
          <div>
            <h2>{{ t('market.catalog') }}</h2>
            <p>{{ t('market.catalogHint') }}</p>
          </div>
          <UiBadge variant="outline">{{ catalog.length }}</UiBadge>
        </div>

        <button
          v-for="item in catalog"
          :key="item.catalog_id"
          class="market-card"
          :class="{ active: selectedCatalogId === item.catalog_id }"
          @click="selectedCatalogId = item.catalog_id"
        >
          <div class="market-card-icon" :class="item.kind">
            <component :is="kindIcon(item.kind)" :size="18" />
          </div>
          <div class="market-card-main">
            <div class="market-card-title">
              <strong>{{ item.display_name }}</strong>
              <UiBadge :variant="statusVariant(item)">{{ statusLabel(item) }}</UiBadge>
            </div>
            <p>{{ item.description }}</p>
            <div class="market-chip-row">
              <span>{{ kindLabel(item.kind) }}</span>
              <span>{{ item.publisher }}</span>
              <span>{{ item.version }}</span>
            </div>
          </div>
        </button>

        <div v-if="catalog.length === 0" class="market-empty">
          {{ t('market.empty') }}
        </div>
      </section>

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
          <select v-model="directForm.source_kind" class="market-select">
            <option v-for="kind in sourceKindOptions" :key="kind" :value="kind">{{ kind }}</option>
          </select>
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
    </section>
  </main>
</template>
