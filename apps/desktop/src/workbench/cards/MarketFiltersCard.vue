<script setup lang="ts">
import { Bot, Boxes, FolderPlus, PlugZap, RefreshCw, Search, Store, Terminal } from '@lucide/vue'
import { UiButton, UiInput, UiLabel } from '@/components/ui'
import { useMarketWorkbench } from '../marketController'

const market = useMarketWorkbench()
const kinds = [
  { key: 'all', label: 'All', icon: Boxes }, { key: 'skill', label: 'Skill', icon: Bot },
  { key: 'mcp-server', label: 'MCP', icon: PlugZap }, { key: 'acp-adapter', label: 'ACP', icon: Terminal },
]
const sourceKinds = ['local-directory', 'local-archive', 'github', 'git', 'https-archive', 'marketplace-url', 'mcpb', 'dxt']
</script>

<template>
  <div class="market-filter-card">
    <div class="market-card-search">
      <Search />
      <UiInput v-model="market.query.value" placeholder="Search extensions" @keyup.enter="market.loadCatalog" />
      <UiButton variant="ghost" size="icon" title="Refresh catalog" @click="market.loadCatalog"><RefreshCw /></UiButton>
    </div>
    <div class="market-kind-grid">
      <UiButton v-for="kind in kinds" :key="kind.key" variant="ghost" size="sm" :class="{ active: market.kindFilter.value === kind.key }" @click="market.kindFilter.value = kind.key">
        <component :is="kind.icon" data-icon="inline-start" />{{ kind.label }}
      </UiButton>
    </div>
    <div class="market-source-list">
      <button v-for="source in market.sources.value" :key="source.id" :class="{ active: market.sourceFilter.value === source.id }" @click="market.sourceFilter.value = source.id">
        <Store /><span>{{ source.name }}</span>
        <UiButton variant="ghost" size="icon" title="Refresh source" @click.stop="market.refreshSource(source.id)"><RefreshCw /></UiButton>
      </button>
    </div>
    <div class="market-source-form">
      <UiLabel>Add source</UiLabel>
      <select v-model="market.sourceForm.kind"><option v-for="kind in sourceKinds" :key="kind">{{ kind }}</option></select>
      <UiInput v-model="market.sourceForm.name" placeholder="Source name" />
      <UiInput v-model="market.sourceForm.location" placeholder="Source location" />
      <UiButton size="sm" :disabled="market.busy.value || !market.sourceForm.location" @click="market.addSource"><FolderPlus data-icon="inline-start" />Add</UiButton>
    </div>
  </div>
</template>

<style scoped>
.market-filter-card { display: flex; flex-direction: column; gap: 10px; height: 100%; padding: 10px; overflow: auto; }
.market-card-search { display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: 6px; }
.market-card-search > svg { width: 15px; }
.market-kind-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 4px; }
.market-kind-grid :deep(button.active) { background: var(--surface-selected); color: var(--accent-primary); }
.market-source-list { display: flex; flex-direction: column; gap: 3px; }
.market-source-list > button { display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: 7px; min-height: 34px; padding: 2px 4px 2px 9px; border: 0; border-radius: 6px; color: var(--text-secondary); background: transparent; text-align: left; }
.market-source-list > button.active, .market-source-list > button:hover { background: var(--surface-hover); color: var(--text-primary); }
.market-source-list svg { width: 14px; }
.market-source-form { display: flex; flex-direction: column; gap: 7px; margin-top: auto; padding-top: 10px; border-top: 1px solid var(--border-muted); }
.market-source-form select { min-height: 34px; border: 1px solid var(--border-default); border-radius: 6px; color: var(--text-primary); background: var(--surface-input); }
</style>

