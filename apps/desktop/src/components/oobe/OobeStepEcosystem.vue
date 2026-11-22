<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { CheckCircle2, Download, Loader2, Package, Search, Shield } from '@lucide/vue'
import { api, type ExternalCliDiscoveredItemDto, type ExternalCliDiscoveryDto } from '@/api'
import { UiBadge, UiButton, UiCheckbox } from '@/components/ui'
import { ensureGraphSeedPack, graphSeedPackState } from '@/agentPacks/graphSeedPackBootstrap'

const { t, te } = useI18n()

function label(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? String(t(key, params ?? {})) : fallback
}

const seedInstalling = ref(false)
const scanning = ref(false)
const importing = ref(false)
const discovery = ref<ExternalCliDiscoveryDto | null>(null)
const selectedIds = ref<string[]>([])
const importResultCount = ref<number | null>(null)
const discoveryError = ref('')

const seedReady = computed(
  () => graphSeedPackState.value.phase === 'up_to_date' || graphSeedPackState.value.phase === 'newer_installed',
)
const discoverableItems = computed<ExternalCliDiscoveredItemDto[]>(() => discovery.value?.items ?? [])

async function installSeedPack(): Promise<void> {
  seedInstalling.value = true
  try {
    await ensureGraphSeedPack()
  } finally {
    seedInstalling.value = false
  }
}

async function scanLocalEcosystem(): Promise<void> {
  if (typeof api.discoverExternalCliTools !== 'function') return
  scanning.value = true
  discoveryError.value = ''
  importResultCount.value = null
  try {
    const result = await api.discoverExternalCliTools()
    discovery.value = result
    selectedIds.value = (result.items ?? [])
      .filter((item) => !item.already_imported)
      .map((item) => item.item_id)
  } catch (err) {
    discoveryError.value = err instanceof Error ? err.message : String(err)
  } finally {
    scanning.value = false
  }
}

function toggleItem(itemId: string, checked: boolean): void {
  if (checked) {
    if (!selectedIds.value.includes(itemId)) {
      selectedIds.value = [...selectedIds.value, itemId]
    }
  } else {
    selectedIds.value = selectedIds.value.filter((id) => id !== itemId)
  }
}

async function importSelected(): Promise<void> {
  if (!selectedIds.value.length || typeof api.installToolSkill !== 'function') return
  importing.value = true
  discoveryError.value = ''
  try {
    const res = await api.installToolSkill({
      source_type: 'external_cli_import',
      item_ids: selectedIds.value,
      // Security default: untrusted stdio MCP servers remain disabled until explicitly enabled.
      enable_stdio_mcp: false,
    })
    const installedCount =
      (res.installed_skills?.length ?? 0) +
      (res.installed_mcp_servers?.length ?? 0) +
      (res.installed_commands?.length ?? 0)
    importResultCount.value = installedCount
    await scanLocalEcosystem()
  } catch (err) {
    discoveryError.value = err instanceof Error ? err.message : String(err)
  } finally {
    importing.value = false
  }
}

onMounted(() => {
  void scanLocalEcosystem()
})
</script>

<template>
  <div class="oobe-step" data-testid="oobe-step-ecosystem">
    <div class="oobe-step-hero">
      <h3>{{ label('oobe.ecosystem.title', 'Agent Pack & Local Tools') }}</h3>
      <p>{{ label('oobe.ecosystem.subtitle', 'Ensure the built-in GraphSeedPack is ready and optionally import existing local CLI tools.') }}</p>
    </div>

    <div class="oobe-eco-card">
      <div class="oobe-eco-head">
        <div class="oobe-eco-title">
          <Package :size="16" aria-hidden="true" />
          <strong>{{ label('oobe.ecosystem.seedPackTitle', 'Built-in GraphSeedPack') }}</strong>
        </div>
        <UiBadge v-if="seedReady" variant="default">
          <CheckCircle2 :size="12" />
          <span>{{ label('oobe.ecosystem.seedPackReady', 'Installed & up to date') }}</span>
        </UiBadge>
        <UiButton v-else variant="outline" size="sm" :disabled="seedInstalling" @click="installSeedPack">
          <Loader2 v-if="seedInstalling" :size="13" class="spinning" />
          <Download v-else :size="13" />
          <span>{{ label('oobe.ecosystem.seedPackInstall', 'Install / Update Pack') }}</span>
        </UiButton>
      </div>
    </div>

    <div class="oobe-eco-card">
      <div class="oobe-eco-head">
        <div class="oobe-eco-title">
          <Search :size="16" aria-hidden="true" />
          <strong>{{ label('oobe.ecosystem.localDiscoverTitle', 'Local External CLI Ecosystem') }}</strong>
        </div>
        <div class="oobe-eco-actions">
          <UiButton variant="outline" size="sm" :disabled="scanning" @click="scanLocalEcosystem">
            <Loader2 v-if="scanning" :size="13" class="spinning" />
            <span>{{ label('oobe.ecosystem.localDiscoverScan', 'Scan local tools') }}</span>
          </UiButton>
          <UiButton
            v-if="discoverableItems.length > 0"
            size="sm"
            :disabled="importing || selectedIds.length === 0"
            @click="importSelected"
          >
            <Loader2 v-if="importing" :size="13" class="spinning" />
            <span>{{ label('oobe.ecosystem.localImportSelected', 'Import selected') }} ({{ selectedIds.length }})</span>
          </UiButton>
        </div>
      </div>

      <p v-if="importResultCount !== null" class="oobe-eco-success">
        {{ label('oobe.ecosystem.localImportDone', `Imported ${importResultCount} items`, { count: importResultCount }) }}
      </p>
      <p v-if="discoveryError" class="oobe-eco-error">{{ discoveryError }}</p>

      <div v-if="discoverableItems.length === 0 && !scanning" class="oobe-eco-empty">
        {{ label('oobe.ecosystem.localDiscoverEmpty', 'No external CLI configurations detected yet.') }}
      </div>

      <ul v-else class="oobe-eco-list">
        <li v-for="item in discoverableItems" :key="item.item_id" class="oobe-eco-item">
          <UiCheckbox
            :model-value="selectedIds.includes(item.item_id)"
            :disabled="item.already_imported"
            :aria-label="item.display_name || item.name"
            @update:model-value="toggleItem(item.item_id, Boolean($event))"
          />
          <div class="oobe-eco-item__body">
            <div class="oobe-eco-item__top">
              <strong>{{ item.display_name || item.name }}</strong>
              <UiBadge variant="secondary">{{ item.ecosystem }}</UiBadge>
              <UiBadge variant="outline">{{ item.kind }}</UiBadge>
              <UiBadge v-if="item.has_secrets" variant="secondary">
                <Shield :size="10" />
                <span>redacted</span>
              </UiBadge>
            </div>
            <p v-if="item.description">{{ item.description }}</p>
          </div>
        </li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.oobe-step { display: flex; flex-direction: column; gap: 12px; }
.oobe-step-hero h3 { margin: 0 0 4px; font-size: 17px; color: var(--text-primary); }
.oobe-step-hero p { margin: 0; font-size: 13px; color: var(--text-secondary); line-height: 1.5; }
.oobe-eco-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border-radius: 11px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}
.oobe-eco-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  flex-wrap: wrap;
}
.oobe-eco-title { display: flex; align-items: center; gap: 8px; color: var(--text-primary); font-size: 13px; }
.oobe-eco-actions { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.oobe-eco-empty { font-size: 12px; color: var(--text-secondary); padding: 6px 0; }
.oobe-eco-success { margin: 0; font-size: 12px; color: var(--accent-success, #22c55e); }
.oobe-eco-error { margin: 0; font-size: 12px; color: var(--accent-danger); }
.oobe-eco-list {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 210px;
  overflow: auto;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.oobe-eco-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 8px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
}
.oobe-eco-item__body { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 3px; }
.oobe-eco-item__top { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; font-size: 12px; }
.oobe-eco-item__body p { margin: 0; font-size: 11.5px; color: var(--text-secondary); line-height: 1.4; }
.spinning { animation: oobe-spin 1s linear infinite; }
@keyframes oobe-spin { to { transform: rotate(360deg); } }
</style>
