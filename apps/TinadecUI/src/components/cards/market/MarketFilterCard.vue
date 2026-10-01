<script setup lang="ts">
import {
  ArrowLeft,
  Bot,
  Boxes,
  FolderPlus,
  PlugZap,
  RefreshCw,
  Search,
  Store,
  Terminal,
  ToggleLeft,
  ToggleRight,
  Trash2,
} from '@lucide/vue'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { UiBadge, UiButton, UiInput, UiLabel } from '@/components/ui'
import {
  catalogKindLabel, kindOptions as _kindOptions, marketController, sourceKindLabel,
} from '@/controllers/MarketController'

const { t } = useI18n()
const router = useRouter()

const {
  sources, supportedKinds, kindFilter, sourceFilter, query, busy,
  sourceForm, loadCatalog, addSource, refreshSource, toggleSource, removeSource,
} = marketController

const kindOptions = _kindOptions.map((o) => ({ ...o, icon: o.key === 'skill' ? Bot : o.key === 'mcp-server' ? PlugZap : o.key === 'acp-adapter' ? Terminal : Boxes }))
// Core answers the kinds it has an adapter for; the picker used to list eight it could never read,
// so every add either created a row that could not refresh or failed outright.
const sourceKindOptions = computed(() => supportedKinds.value)

function filterLabel(key: string) {
  return key === 'all' ? t('market.filterAll') : catalogKindLabel(key)
}
</script>

<template vapor>
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
        <span>{{ filterLabel(option.key) }}</span>
      </UiButton>
    </div>

    <div class="market-source-box">
      <div class="market-section-head">
        <span>{{ t('market.sources') }}</span>
        <UiBadge variant="secondary">{{ sources.length }}</UiBadge>
      </div>
      <div
        v-for="source in sources"
        :key="source.id"
        class="market-source-item"
        :class="{ active: sourceFilter === source.id }"
      >
        <button class="market-source-pick" :title="t('market.filterBySource')" @click="sourceFilter = source.id">
          <Store :size="14" />
          <span>{{ source.name }}</span>
          <span class="market-source-count">{{ source.entry_count }}</span>
        </button>
        <!-- A source that failed its last refresh keeps its rows; the marker says the catalog below
             is standing on stale data rather than on nothing. -->
        <span v-if="source.last_error" class="market-source-stale" :title="source.last_error">!</span>
        <UiButton
          variant="ghost"
          size="icon"
          :title="source.enabled ? t('market.disableSource') : t('market.enableSource')"
          @click="toggleSource(source.id, !source.enabled)"
        >
          <component :is="source.enabled ? ToggleRight : ToggleLeft" :size="13" />
        </UiButton>
        <UiButton variant="ghost" size="icon" :title="t('settings.refresh')" @click="refreshSource(source.id)">
          <RefreshCw :size="13" />
        </UiButton>
        <UiButton variant="ghost" size="icon" :title="t('market.deleteSource')" @click="removeSource(source.id)">
          <Trash2 :size="13" />
        </UiButton>
      </div>
    </div>

    <div class="market-source-form">
      <UiLabel>{{ t('market.addSource') }}</UiLabel>
      <select v-model="sourceForm.kind" class="market-select">
        <option v-for="kind in sourceKindOptions" :key="kind" :value="kind">{{ sourceKindLabel(kind) }}</option>
      </select>
      <UiInput v-model="sourceForm.name" :placeholder="t('market.sourceName')" />
      <UiInput v-model="sourceForm.location" :placeholder="t('market.sourceLocation')" />
      <UiButton size="sm" :disabled="busy || !sourceForm.location" @click="addSource">
        <FolderPlus :size="14" />
        {{ t('market.add') }}
      </UiButton>
    </div>
  </aside>
</template>

<style scoped>
.market-rail {
  display: flex;
  flex-direction: column;
  gap: 16px;
  height: 100%;
  overflow-y: auto;
  padding: 16px;
}

.market-rail-title {
  display: flex;
  align-items: center;
  gap: 8px;
}

.market-rail-title h1 {
  font-size: 16px;
  font-weight: 600;
  margin: 0;
}

.market-rail-title p {
  font-size: 12px;
  color: var(--text-secondary);
  margin: 0;
}

.market-search {
  display: flex;
  align-items: center;
  gap: 8px;
}

.market-filter-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.market-filter-button.active {
  background: var(--surface-selected);
  color: var(--accent-primary);
}

.market-source-box {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.market-section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary);
}

.market-source-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: transparent;
  cursor: pointer;
  font-size: 13px;
  color: var(--text-primary);
  text-align: left;
}

.market-source-item.active {
  border-color: var(--accent-primary);
  background: var(--surface-hover);
}

.market-source-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: auto;
  padding-top: 16px;
  border-top: 1px solid var(--border-muted);
}

.market-select {
  height: 32px;
  padding: 0 8px;
  border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: var(--surface-input);
  color: var(--text-primary);
  font-size: 13px;
}
</style>
