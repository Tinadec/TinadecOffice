<script setup lang="ts">
import { Bot, Boxes, PlugZap, Terminal } from '@lucide/vue'
import { computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import type { MarketCatalogItemDto } from '@/api'
import { UiBadge } from '@/components/ui'
import { marketController } from '@/controllers/MarketController'

const { t } = useI18n()

const {
  catalog: _catalog, selectedCatalogId: _selectedId, installedByExtensionId, start,
} = marketController
// ponytail: vapor template does not auto-unwrap Ref when destructured from controller — expose plain-typed computed so vue-tsc sees correct brands (single reactivity identity via tsconfig paths)
const catalog = computed(() => _catalog.value) as unknown as MarketCatalogItemDto[]
const selectedCatalogId = computed({
  get: () => _selectedId.value,
  set: (v: string) => { _selectedId.value = v },
}) as unknown as string

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

<template vapor>
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
</template>

<style scoped>
.market-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
  height: 100%;
  overflow-y: auto;
  padding: 16px;
}

.market-list-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.market-list-head h2 {
  font-size: 16px;
  font-weight: 600;
  margin: 0;
}

.market-list-head p {
  font-size: 12px;
  color: var(--text-secondary);
  margin: 0;
}

.market-card {
  display: flex;
  gap: 12px;
  padding: 12px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  cursor: pointer;
  text-align: left;
}

.market-card.active {
  border-color: var(--accent-primary);
}

.market-card-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.market-card-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.market-chip-row {
  display: flex;
  gap: 8px;
  font-size: 11px;
  color: var(--text-secondary);
}

.market-empty {
  padding: 32px;
  text-align: center;
  color: var(--text-secondary);
}
</style>
