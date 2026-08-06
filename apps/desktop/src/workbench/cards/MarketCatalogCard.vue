<script setup lang="ts">
import { Bot, Boxes, PlugZap, Terminal } from '@lucide/vue'
import { UiBadge } from '@/components/ui'
import { useMarketWorkbench } from '../marketController'

const market = useMarketWorkbench()
function icon(kind: string) { return kind === 'skill' ? Bot : kind === 'mcp-server' ? PlugZap : kind === 'acp-adapter' ? Terminal : Boxes }
</script>

<template>
  <div class="market-catalog-card">
    <button v-for="item in market.catalog.value" :key="item.catalog_id" :class="{ active: market.selectedCatalogId.value === item.catalog_id }" @click="market.selectedCatalogId.value = item.catalog_id">
      <component :is="icon(item.kind)" />
      <div><strong>{{ item.display_name }}</strong><p>{{ item.description }}</p><small>{{ market.kindLabel(item.kind) }} / {{ item.publisher }} / {{ item.version }}</small></div>
      <UiBadge :variant="market.statusVariant(item)">{{ market.statusLabel(item) }}</UiBadge>
    </button>
    <div v-if="!market.catalog.value.length" class="market-empty">No extensions match the current filters.</div>
  </div>
</template>

<style scoped>
.market-catalog-card { display: flex; flex-direction: column; gap: 6px; height: 100%; padding: 10px; overflow: auto; }
.market-catalog-card > button { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; align-items: start; gap: 10px; padding: 12px; border: 1px solid transparent; border-radius: 8px; color: var(--text-primary); background: var(--surface-raised); text-align: left; }
.market-catalog-card > button:hover, .market-catalog-card > button.active { border-color: var(--accent-primary); background: var(--surface-hover); }
.market-catalog-card > button > svg { width: 20px; color: var(--accent-primary); }
.market-catalog-card strong, .market-catalog-card p, .market-catalog-card small { display: block; margin: 0; }
.market-catalog-card p { margin: 4px 0; color: var(--text-secondary); font-size: 12px; }
.market-catalog-card small, .market-empty { color: var(--text-muted); }
.market-empty { display: grid; place-items: center; min-height: 180px; }
</style>

