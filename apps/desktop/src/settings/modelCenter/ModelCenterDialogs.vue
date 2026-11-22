<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Circle, Cpu, Database, Plus, Save, Search, Server, Terminal, X } from '@lucide/vue'
import {
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogOverlay,
  DialogPortal,
  DialogRoot,
  DialogTitle,
} from 'reka-ui'
import { UiBadge, UiButton, UiCard, UiCheckbox, UiInput } from '@/components/ui'
import {
  PROVIDER_CATEGORIES,
  PROVIDER_TEMPLATES,
  findTemplate,
  type ProviderCategory,
  type ProviderTemplate,
} from '@/providerTemplates'
import type {
  ModelCatalogReadinessReceiptDto,
  ModelCenterApiConnectionDto,
  ModelCenterOverviewDto,
  ModelReadinessReceiptDto,
  RuntimeReadinessReceiptDto,
} from '@/api'
import { readinessReason } from '@/lib/readinessText'

const props = defineProps<{
  overview: ModelCenterOverviewDto | null
  modelReadiness: ModelReadinessReceiptDto | null
  modelCatalogReadiness: ModelCatalogReadinessReceiptDto | null
  readinessReceipt: RuntimeReadinessReceiptDto | null
  showDiagnostics: boolean
  showModelModal: boolean
  modelModalProvider: ModelCenterApiConnectionDto | null
  modelModalProviderId: string
  modelModalManual: string
  modelModalSelected: string[]
  modelModalVisibleRows: Array<{ id: string; label: string; saved: boolean }>
  modelModalQuery: string
  modelModalBusy: boolean
  modelModalError: string
  modelModalFetched: boolean
  showTemplatePicker: boolean
  connectionKindLabel: (kind: string) => string
}>()

const emit = defineEmits<{
  'update:showDiagnostics': [open: boolean]
  'close-model-modal': []
  'update:modelModalManual': [value: string]
  'update:modelModalQuery': [value: string]
  'fetch-discovered-models': []
  'add-manual-model': []
  'toggle-model-selection': [id: string]
  'select-all-visible-models': []
  'clear-selected-models': []
  'save-model-modal': []
  'update:showTemplatePicker': [open: boolean]
  'pick-template': [template: ProviderTemplate]
}>()

const { t, te } = useI18n()

function safeLabel(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? String(t(key, params ?? {})) : fallback
}

function modelFamilyOf(modelId: string): string {
  const lower = modelId.toLowerCase()
  if (/(^|\/)(gpt-|o1|o3|o4|chatgpt)/.test(lower)) return 'OpenAI / GPT'
  if (/(^|\/)claude/.test(lower)) return 'Claude'
  if (/(^|\/)gemini/.test(lower)) return 'Gemini'
  if (/(^|\/)deepseek/.test(lower)) return 'DeepSeek'
  if (/(^|\/)(qwen|qwq)/.test(lower)) return 'Qwen'
  if (/(^|\/)(glm|chatglm)/.test(lower)) return 'GLM'
  if (/(^|\/)(kimi|moonshot)/.test(lower)) return 'Kimi / Moonshot'
  if (/(^|\/)minimax/.test(lower)) return 'MiniMax'
  if (/(^|\/)(doubao|ep-)/.test(lower)) return 'Doubao'
  if (/(^|\/)(llama|meta-llama)/.test(lower)) return 'Llama'
  if (/(^|\/)(mistral|codestral|mixtral)/.test(lower)) return 'Mistral'
  return 'Other'
}

const groupedModelRows = computed(() => {
  const map = new Map<string, Array<{ id: string; label: string; saved: boolean }>>()
  for (const row of props.modelModalVisibleRows) {
    const family = modelFamilyOf(row.id)
    const bucket = map.get(family)
    if (bucket) bucket.push(row)
    else map.set(family, [row])
  }
  return Array.from(map.entries()).map(([family, rows]) => ({ family, rows }))
})

const selectableVisibleCount = computed(() =>
  props.modelModalVisibleRows.filter((row) => !row.saved).length,
)

const templatePickerQuery = ref('')

const filteredPickerTemplates = computed(() => {
  const query = templatePickerQuery.value.trim().toLocaleLowerCase()
  return PROVIDER_TEMPLATES.filter((template) => !query || [
    safeLabel(template.display_name_key, template.driver),
    template.driver,
    template.connection_kind,
    template.default_model ?? '',
  ].some((value) => value.toLocaleLowerCase().includes(query)))
})

const pickerTemplateGroups = computed(() => {
  const groups: { category: ProviderCategory; labelKey: string; templates: ProviderTemplate[] }[] = []
  for (const category of PROVIDER_CATEGORIES) {
    const templates = filteredPickerTemplates.value.filter((template) => template.category === category.key)
    if (templates.length > 0) groups.push({ category: category.key, labelKey: category.labelKey, templates })
  }
  return groups
})

const blockedModelRoutes = computed(() =>
  (props.modelReadiness?.routes ?? []).filter((route) => route.status === 'blocked'),
)
const warningCatalogTemplates = computed(() =>
  (props.modelCatalogReadiness?.templates ?? []).filter((template) => template.status !== 'ready'),
)
const modelReadinessNotes = computed(() => props.readinessReceipt
  ? [...new Set((props.readinessReceipt.items ?? [])
      .filter((item) => item.id.startsWith('model_') && item.status !== 'ready')
      .flatMap((item) => readinessReason(item, { t, te }) ?? []))]
  : props.modelReadiness?.design_notes ?? [])

function providerPresentation(driver: string) {
  return findTemplate(driver)
}

function modelCatalogModeLabel(mode?: string) {
  return mode === 'configured_only' ? t('settings.configuredOnly') : mode ?? t('settings.configuredOnly')
}

function readinessVariant(status: string): 'default' | 'secondary' | 'destructive' | 'outline' {
  if (status === 'ready') return 'default'
  if (status === 'blocked') return 'destructive'
  if (status === 'warning') return 'outline'
  return 'secondary'
}

function readinessStatusLabel(status: string) {
  if (status === 'ready') return t('settings.readinessReady')
  if (status === 'blocked') return t('settings.readinessBlocked')
  if (status === 'warning') return t('settings.readinessWarning')
  return status
}
</script>

<template>
  <!-- 1. Advanced Diagnostics Dialog -->
  <DialogRoot :open="showDiagnostics" @update:open="(open) => { if (!open) emit('update:showDiagnostics', false) }">
    <DialogPortal>
      <DialogOverlay class="model-diagnostics-overlay" />
      <DialogContent class="model-diagnostics-dialog">
        <header class="model-diagnostics-header">
          <div>
            <DialogTitle class="model-diagnostics-title">{{ t('settings.advancedDiagnostics') }}</DialogTitle>
            <DialogDescription class="model-diagnostics-description">{{ t('settings.diagnosticsDescription') }}</DialogDescription>
          </div>
          <DialogClose as-child>
            <UiButton variant="ghost" size="icon" :aria-label="t('common.close')">
              <X :size="16" />
            </UiButton>
          </DialogClose>
        </header>

        <div class="model-diagnostics-body">
          <section class="center-overview-receipt" :aria-label="t('settings.centerOverview')">
            <div class="center-receipt-item" :class="{ ready: overview?.capabilities.provider_crud }">
              <Database :size="17" />
              <div>
                <span>{{ t('settings.modelProviderManagement') }}</span>
                <strong>{{ t('settings.modelProviderManagementHint') }}</strong>
              </div>
              <UiBadge :variant="overview?.capabilities.provider_crud ? 'default' : 'secondary'">
                {{ overview?.capabilities.provider_crud ? t('settings.writable') : t('settings.readOnly') }}
              </UiBadge>
            </div>
            <div class="center-receipt-item configured">
              <Cpu :size="17" />
              <div>
                <span>{{ t('settings.modelCatalogScope') }}</span>
                <strong>{{ t('settings.configuredModelsOnly') }}</strong>
              </div>
              <UiBadge variant="outline">{{ modelCatalogModeLabel(overview?.capabilities.model_catalog_mode) }}</UiBadge>
            </div>
            <div class="center-receipt-item" :class="{ ready: overview?.capabilities.live_model_discovery, unavailable: !overview?.capabilities.live_model_discovery }">
              <Search :size="17" />
              <div>
                <span>{{ t('settings.liveDiscovery') }}</span>
                <strong>{{ overview?.capabilities.live_model_discovery ? t('settings.available') : t('settings.pendingCore') }}</strong>
              </div>
              <UiBadge :variant="overview?.capabilities.live_model_discovery ? 'default' : 'secondary'">
                {{ overview?.capabilities.live_model_discovery ? t('settings.available') : t('settings.unavailable') }}
              </UiBadge>
            </div>
          </section>

          <section v-if="modelReadiness || modelCatalogReadiness" class="model-health-overview">
            <div class="model-health-head">
              <div>
                <h3>{{ t('settings.modelHealth') }}</h3>
                <span>{{ t('settings.modelHealthHint') }}</span>
              </div>
              <UiBadge v-if="modelReadiness" :variant="readinessVariant(modelReadiness.status)">
                <Circle :size="8" />
                {{ readinessStatusLabel(modelReadiness.status) }}
              </UiBadge>
            </div>
            <div class="model-health-metrics">
              <div>
                <span>{{ t('settings.readyProvidersMetric') }}</span>
                <strong>{{ modelReadiness ? `${modelReadiness.ready_provider_count}/${modelReadiness.provider_count}` : '—' }}</strong>
              </div>
              <div :class="{ attention: (modelReadiness?.blocked_route_count ?? 0) > 0 }">
                <span>{{ t('settings.blockedRoutesMetric') }}</span>
                <strong>{{ modelReadiness?.blocked_route_count ?? '—' }}</strong>
              </div>
              <div v-if="modelCatalogReadiness && (modelCatalogReadiness.template_count > 0 || (modelCatalogReadiness.templates?.length ?? 0) > 0)">
                <span>{{ t('settings.readyTemplatesMetric') }}</span>
                <strong>{{ `${modelCatalogReadiness.ready_template_count}/${modelCatalogReadiness.template_count}` }}</strong>
              </div>
              <div v-if="modelCatalogReadiness && (modelCatalogReadiness.runtime_module_count ?? 0) > 0">
                <span>{{ t('settings.runtimeModulesMetric') }}</span>
                <strong>{{ modelCatalogReadiness.runtime_module_count }}</strong>
              </div>
            </div>
          </section>

          <div
            v-if="modelReadiness || (modelCatalogReadiness && (modelCatalogReadiness.template_count > 0 || (modelCatalogReadiness.templates?.length ?? 0) > 0 || warningCatalogTemplates.length > 0))"
            class="model-diagnostics-grid"
          >
            <section v-if="modelReadiness" class="model-diagnostic-section">
              <div class="model-diagnostic-head">
                <div>
                  <strong>{{ t('settings.providerReceipt') }}</strong>
                  <span>{{ modelReadiness.receipt_id }}</span>
                </div>
                <UiBadge :variant="readinessVariant(modelReadiness.status)">{{ readinessStatusLabel(modelReadiness.status) }}</UiBadge>
              </div>
              <p class="model-diagnostic-meta">{{ t('settings.generatedAt') }} · {{ modelReadiness.generated_at }}</p>
              <div class="model-diagnostic-list">
                <strong>{{ t('settings.blockedRoutes') }}</strong>
                <div v-if="blockedModelRoutes.length > 0" class="model-readiness-routes">
                  <span v-for="route in blockedModelRoutes" :key="route.purpose">
                    {{ route.purpose }} · {{ route.provider_display_name ?? route.provider_instance_id }}
                  </span>
                </div>
                <span v-else class="quiet">{{ t('settings.noBlockedRoutes') }}</span>
              </div>
              <ul v-if="modelReadinessNotes.length > 0" class="model-diagnostic-notes">
                <li v-for="note in modelReadinessNotes" :key="note">{{ note }}</li>
              </ul>
            </section>
            <section
              v-if="modelCatalogReadiness && (modelCatalogReadiness.template_count > 0 || (modelCatalogReadiness.templates?.length ?? 0) > 0 || warningCatalogTemplates.length > 0)"
              class="model-diagnostic-section"
            >
              <div class="model-diagnostic-head">
                <div>
                  <strong>{{ t('settings.catalogReceipt') }}</strong>
                  <span>{{ modelCatalogReadiness.receipt_id }}</span>
                </div>
                <UiBadge :variant="readinessVariant(modelCatalogReadiness.status)">{{ readinessStatusLabel(modelCatalogReadiness.status) }}</UiBadge>
              </div>
              <p class="model-diagnostic-meta">{{ t('settings.generatedAt') }} · {{ modelCatalogReadiness.generated_at }}</p>
              <div class="model-diagnostic-list">
                <strong>{{ t('settings.catalogWarnings') }}</strong>
                <div v-if="warningCatalogTemplates.length > 0" class="catalog-readiness-rows">
                  <div v-for="template in warningCatalogTemplates" :key="template.driver" class="catalog-readiness-row">
                    <div>
                      <strong>{{ template.display_name }}</strong>
                      <span>{{ template.runtime_module_family }} · {{ template.live_discovery_policy }}</span>
                    </div>
                    <UiBadge :variant="readinessVariant(template.status)">{{ template.runtime_module_status }}</UiBadge>
                  </div>
                </div>
                <span v-else class="quiet">{{ t('settings.noCatalogWarnings') }}</span>
              </div>
              <ul v-if="readinessReceipt?.status === 'blocked'" class="model-diagnostic-notes">
                <li>{{ t('settings.readinessBlockedHint') }}</li>
              </ul>
            </section>
          </div>
        </div>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>

  <!-- 2. Add / Discover Models Dialog -->
  <DialogRoot :open="showModelModal" @update:open="(open) => { if (!open) emit('close-model-modal') }">
    <DialogPortal>
      <DialogOverlay class="model-provider-modal-overlay" />
      <DialogContent class="model-provider-modal model-provider-dialog">
        <UiCard class="model-provider-modal-content">
          <template #header>
            <div class="modal-header-row">
              <div class="modal-header-left">
                <span
                  class="modal-provider-logo"
                  :style="{ color: providerPresentation(modelModalProvider?.driver ?? '')?.brand_color, backgroundColor: providerPresentation(modelModalProvider?.driver ?? '')?.brand_bg }"
                >
                  <span v-if="providerPresentation(modelModalProvider?.driver ?? '')?.icon" class="provider-brand-mark" v-html="providerPresentation(modelModalProvider?.driver ?? '')?.icon"></span>
                  <Cpu v-else :size="18" />
                </span>
                <div class="modal-header-info">
                  <DialogTitle as-child><h3>{{ t('settings.addModelTitle') }}</h3></DialogTitle>
                  <DialogDescription as-child>
                    <span class="modal-header-sub">{{ modelModalProvider?.display_name ?? modelModalProviderId }}</span>
                  </DialogDescription>
                </div>
              </div>
              <DialogClose as-child>
                <UiButton variant="ghost" size="icon" :aria-label="t('common.close')">
                  <X :size="16" />
                </UiButton>
              </DialogClose>
            </div>
          </template>

          <template #content>
            <div class="modal-form-section">
              <div class="modal-form-section-title">{{ t('settings.fetchModelsFromProvider') }}</div>
              <div class="model-modal-fetch-row">
                <UiButton
                  variant="outline"
                  size="sm"
                  :disabled="modelModalBusy || !overview?.capabilities.model_discovery_refresh"
                  :title="overview?.capabilities.model_discovery_refresh ? undefined : t('settings.modelDiscoveryUnsupported')"
                  @click="emit('fetch-discovered-models')"
                >
                  <Server :size="14" />
                  {{ modelModalBusy ? t('settings.fetchingModels') : (modelModalFetched ? t('settings.refreshModels') : t('settings.fetchModels')) }}
                </UiButton>
              </div>
              <div v-if="modelModalError" class="model-provider-note">
                <Terminal :size="14" />
                <span>{{ modelModalError }}</span>
              </div>
            </div>

            <div class="modal-form-section">
              <div class="modal-form-section-title">{{ t('settings.manualModel') }}</div>
              <div class="model-manual-row">
                <UiInput
                  :model-value="modelModalManual"
                  :placeholder="t('settings.modelNamePlaceholder')"
                  @update:model-value="emit('update:modelModalManual', String($event))"
                  @keydown.enter="emit('add-manual-model')"
                />
                <UiButton variant="outline" :disabled="!modelModalManual.trim()" @click="emit('add-manual-model')">
                  <Plus :size="14" />
                  {{ t('settings.addModel') }}
                </UiButton>
              </div>
            </div>

            <div class="modal-form-section">
              <div class="model-modal-search">
                <Search :size="14" aria-hidden="true" />
                <UiInput
                  :model-value="modelModalQuery"
                  :placeholder="t('settings.modelSearchPlaceholder')"
                  :aria-label="t('settings.modelSearchPlaceholder')"
                  @update:model-value="emit('update:modelModalQuery', String($event))"
                />
              </div>
              <div v-if="modelModalVisibleRows.length" class="model-modal-batch-bar">
                <span class="quiet">
                  {{ safeLabel('settings.selectedCount', `${modelModalSelected.length} selected`, { count: modelModalSelected.length }) }}
                </span>
                <div class="model-modal-batch-actions">
                  <UiButton
                    v-if="selectableVisibleCount > 0"
                    variant="ghost"
                    size="sm"
                    @click="emit('select-all-visible-models')"
                  >
                    {{ safeLabel('settings.selectAllVisible', 'Select all visible') }}
                  </UiButton>
                  <UiButton
                    v-if="modelModalSelected.length > 0"
                    variant="ghost"
                    size="sm"
                    @click="emit('clear-selected-models')"
                  >
                    {{ safeLabel('settings.clearSelection', 'Clear selection') }}
                  </UiButton>
                </div>
              </div>
              <div v-if="modelModalVisibleRows.length" class="model-discovered-list">
                <div v-for="group in groupedModelRows" :key="group.family" class="model-discovered-family">
                  <div v-if="groupedModelRows.length > 1" class="model-discovered-family-head">
                    <span>{{ group.family }}</span>
                    <UiBadge variant="secondary">{{ group.rows.length }}</UiBadge>
                  </div>
                  <label v-for="row in group.rows" :key="row.id" class="model-discovered-row" :class="{ saved: row.saved }">
                    <UiCheckbox
                      :model-value="row.saved || modelModalSelected.includes(row.id)"
                      :disabled="row.saved"
                      :aria-label="row.id"
                      @update:model-value="emit('toggle-model-selection', row.id)"
                    />
                    <strong>{{ row.id }}</strong>
                    <span v-if="row.label !== row.id" class="quiet model-discovered-alias">{{ row.label }}</span>
                    <span v-if="row.saved" class="provider-cap-tag">{{ t('settings.modelAdded') }}</span>
                  </label>
                </div>
              </div>
              <div v-else class="center-empty-state">
                <Cpu :size="20" />
                <span>{{ modelModalQuery.trim()
                  ? t('settings.modelFilterEmpty')
                  : modelModalFetched ? t('settings.fetchModelsEmpty') : t('settings.noProviderModels') }}</span>
              </div>
            </div>
          </template>

          <template #footer>
            <div class="modal-actions">
              <UiButton variant="outline" @click="emit('close-model-modal')">
                {{ t('settings.cancel') }}
              </UiButton>
              <UiButton :disabled="modelModalBusy || !modelModalSelected.length" @click="emit('save-model-modal')">
                <Save :size="14" />
                <span>{{ t('settings.save') }}</span>
              </UiButton>
            </div>
          </template>
        </UiCard>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>

  <!-- 3. Provider Template Picker Dialog -->
  <DialogRoot :open="showTemplatePicker" @update:open="(open) => emit('update:showTemplatePicker', open)">
    <DialogPortal>
      <DialogOverlay class="model-provider-modal-overlay" />
      <DialogContent class="model-provider-modal template-picker-dialog">
        <UiCard class="model-provider-modal-content template-picker-content">
          <template #header>
            <div class="modal-header-row">
              <div class="modal-header-left">
                <span class="modal-provider-logo" :style="{ color: '#8b949e', backgroundColor: 'rgba(139,148,158,0.10)' }">
                  <Plus :size="18" />
                </span>
                <div class="modal-header-info">
                  <DialogTitle as-child><h3>{{ t('settings.addProviderTemplate') }}</h3></DialogTitle>
                  <DialogDescription as-child>
                    <span class="modal-header-sub">{{ t('settings.templatePickerHint') }}</span>
                  </DialogDescription>
                </div>
              </div>
              <DialogClose as-child>
                <UiButton variant="ghost" size="icon" :aria-label="t('common.close')">
                  <X :size="16" />
                </UiButton>
              </DialogClose>
            </div>
          </template>

          <template #content>
            <div class="template-picker-search">
              <Search :size="15" />
              <UiInput v-model="templatePickerQuery" :placeholder="t('settings.templateSearchPlaceholder')" />
            </div>

            <div v-if="pickerTemplateGroups.length === 0" class="template-picker-empty">
              <Server :size="20" />
              <span>{{ t('settings.templatePickerEmpty') }}</span>
            </div>

            <div v-for="group in pickerTemplateGroups" :key="group.category" class="template-picker-group">
              <div class="template-picker-group-title">{{ t(group.labelKey) }}</div>
              <div class="template-picker-grid">
                <button
                  v-for="template in group.templates"
                  :key="template.driver"
                  type="button"
                  class="template-picker-card"
                  :style="{ '--template-accent': template.brand_color }"
                  @click="emit('pick-template', template)"
                >
                  <span class="provider-brand-icon" :style="{ color: template.brand_color, backgroundColor: template.brand_bg }">
                    <span v-if="template.icon" class="provider-brand-mark" v-html="template.icon"></span>
                    <Database v-else :size="16" />
                  </span>
                  <span class="template-picker-card-body">
                    <strong :title="safeLabel(template.display_name_key, template.driver)">{{ safeLabel(template.display_name_key, template.driver) }}</strong>
                    <small :title="template.driver">{{ template.driver }}</small>
                  </span>
                  <span class="template-picker-card-meta">
                    <span>{{ connectionKindLabel(template.connection_kind) }}</span>
                    <span v-if="template.default_model" :title="template.default_model">{{ template.default_model }}</span>
                  </span>
                </button>
              </div>
            </div>
          </template>
        </UiCard>
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>

<style scoped>
.model-modal-batch-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  margin-top: 6px;
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}
.template-picker-card:focus-visible {
  outline: var(--focus-ring-width, 2px) solid var(--focus-ring-color, var(--accent-primary));
  outline-offset: 2px;
}
.model-modal-batch-actions {
  display: flex;
  align-items: center;
  gap: 4px;
}
.model-discovered-family {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.model-discovered-family-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  padding: 6px 4px 2px;
  font-size: 11px;
  font-weight: 600;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.03em;
}
</style>
