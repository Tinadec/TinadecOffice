<script setup lang="ts">
import { Check, ChevronRight, Circle, Cpu, Database, Edit3, Info, PanelRight, Plus, RefreshCw, Search, Server, Settings2, Terminal, Trash2, Workflow, X } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiSkeleton } from '@/components/ui'
import { useModelSettings } from './modelSettings'

const {
  acpRuntimeSourceLabel,
  blockedModelRoutes,
  catalogReadinessByDriver,
  centerDiagnosticLabel,
  configuredModelSourceLabel,
  confirmDeleteId,
  connectionKindLabel,
  deleteProvider,
  filteredModelCenterRows,
  firstNeedsKeyProvider,
  focusModelProviderList,
  loadModelCenter,
  modelCatalogModeLabel,
  modelCatalogReadiness,
  modelCenterBusy,
  modelCenterDiagnostics,
  modelCenterError,
  modelCenterIssueCount,
  modelCenterLoading,
  modelCenterNotice,
  modelCenterOverview,
  modelCenterRows,
  modelCenterSection,
  modelCenterSections,
  modelDiagnosticsRef,
  modelProviderFilter,
  modelProviderListRef,
  modelProviderQuery,
  modelReadiness,
  openAddModal,
  openEditModal,
  openProviderById,
  openModelDiagnostics,
  probeAcpRuntime,
  providerById,
  providerPresentation,
  providerTemplateFromSupplier,
  providers,
  readinessStatusLabel,
  readinessVariant,
  refreshProviderModels,
  router,
  selectedProviderDetail,
  selectedProviderDetailId,
  settingsErrorLabel,
  statusLabel,
  statusVariant,
  supplierCredentialLabel,
  supplierSummary,
  supplierTransportLabel,
  t,
  toggleProviderDetail,
  toggleProviderEnabled,
  warningCatalogTemplates,
} = useModelSettings()
</script>

<template>
  <div class="center-page model-center-page">
            <div class="center-command-bar">
              <div>
                <span class="center-kicker">{{ t('settings.model') }}</span>
                <h1>{{ t('settings.modelCenter') }}</h1>
                <p>{{ t('settings.modelCenterSubtitle') }}</p>
              </div>
              <div class="center-command-actions">
                <UiButton variant="outline" size="sm" :disabled="modelCenterLoading || Boolean(modelCenterError && !modelCenterOverview)" @click="focusModelProviderList('available')">
                  <Plus :size="14" />
                  <span>{{ t('settings.addProvider') }}</span>
                </UiButton>
                <UiButton variant="outline" size="sm" :disabled="modelCenterLoading || modelCenterBusy" @click="loadModelCenter">
                  <RefreshCw :size="14" />
                  <span>{{ t('settings.refresh') }}</span>
                </UiButton>
              </div>
            </div>

            <section v-if="!modelCenterError || modelCenterOverview" class="center-overview-receipt" :aria-label="t('settings.centerOverview')">
              <div class="center-receipt-item" :class="{ ready: modelCenterOverview?.capabilities.provider_crud }">
                <Database :size="17" />
                <div>
                  <span>{{ t('settings.modelProviderManagement') }}</span>
                  <strong>{{ t('settings.modelProviderManagementHint') }}</strong>
                </div>
                <UiBadge :variant="modelCenterOverview?.capabilities.provider_crud ? 'default' : 'secondary'">
                  {{ modelCenterOverview?.capabilities.provider_crud ? t('settings.writable') : t('settings.readOnly') }}
                </UiBadge>
              </div>
              <div class="center-receipt-item configured">
                <Cpu :size="17" />
                <div>
                  <span>{{ t('settings.modelCatalogScope') }}</span>
                  <strong>{{ t('settings.configuredModelsOnly') }}</strong>
                </div>
                <UiBadge variant="outline">{{ modelCatalogModeLabel(modelCenterOverview?.capabilities.model_catalog_mode) }}</UiBadge>
              </div>
              <div class="center-receipt-item" :class="{ ready: modelCenterOverview?.capabilities.live_model_discovery, unavailable: !modelCenterOverview?.capabilities.live_model_discovery }">
                <Search :size="17" />
                <div>
                  <span>{{ t('settings.liveDiscovery') }}</span>
                  <strong>{{ modelCenterOverview?.capabilities.live_model_discovery ? t('settings.available') : t('settings.pendingCore') }}</strong>
                </div>
                <UiBadge :variant="modelCenterOverview?.capabilities.live_model_discovery ? 'default' : 'secondary'">
                  {{ modelCenterOverview?.capabilities.live_model_discovery ? t('settings.available') : t('settings.unavailable') }}
                </UiBadge>
              </div>
            </section>

            <div v-if="modelCenterLoading && !modelCenterOverview" class="center-loading-state" aria-live="polite">
              <UiSkeleton v-for="index in 3" :key="index" class="center-loading-line" />
            </div>

            <div v-if="modelCenterError" class="center-message error">
              <Info :size="16" />
              <span>{{ settingsErrorLabel(modelCenterError) }}</span>
              <UiButton variant="outline" size="sm" @click="loadModelCenter">{{ t('settings.retry') }}</UiButton>
            </div>
            <div v-if="modelCenterNotice" class="center-message warning">
              <Info :size="16" />
              <span>{{ modelCenterNotice }}</span>
            </div>
            <div v-if="modelCenterDiagnostics.length > 0" class="center-message warning center-diagnostics-message">
              <Info :size="16" />
              <div class="center-message-content">
                <strong>{{ t('settings.centerDiagnostics') }}</strong>
                <ul>
                  <li v-for="diagnostic in modelCenterDiagnostics" :key="`${diagnostic.code}:${diagnostic.source ?? ''}:${diagnostic.status ?? ''}`">
                    {{ centerDiagnosticLabel(diagnostic) }}
                  </li>
                </ul>
              </div>
              <UiButton variant="outline" size="sm" :disabled="modelCenterLoading" @click="loadModelCenter">{{ t('settings.retry') }}</UiButton>
            </div>

            <div v-if="!modelCenterError || modelCenterOverview" class="center-workbench model-workbench">
            <aside class="center-inspector" :aria-label="t('settings.centerInspector')">
              <div class="center-pane-heading">
                <div>
                  <span>{{ t('settings.centerInspector') }}</span>
                  <strong>{{ t('settings.modelHealth') }}</strong>
                </div>
                <PanelRight :size="16" />
              </div>

            <section v-if="modelReadiness || modelCatalogReadiness" class="model-health-overview">
              <div class="model-health-head">
                <div>
                  <h2>{{ t('settings.modelHealth') }}</h2>
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
                <div>
                  <span>{{ t('settings.readyTemplatesMetric') }}</span>
                  <strong>{{ modelCatalogReadiness ? `${modelCatalogReadiness.ready_template_count}/${modelCatalogReadiness.template_count}` : '—' }}</strong>
                </div>
                <div>
                  <span>{{ t('settings.runtimeModulesMetric') }}</span>
                  <strong>{{ modelCatalogReadiness?.runtime_module_count ?? '—' }}</strong>
                </div>
              </div>
              <div v-if="modelReadiness && modelReadiness.status !== 'ready'" class="model-health-alert">
                <Info :size="16" />
                <div>
                  <strong>
                    {{ firstNeedsKeyProvider
                      ? t('settings.missingKeySummary', { name: firstNeedsKeyProvider.display_name })
                      : t('settings.modelIssuesSummary') }}
                  </strong>
                  <span>{{ t('settings.modelIssueHint') }}</span>
                </div>
                <UiButton v-if="firstNeedsKeyProvider" variant="outline" size="sm" @click="openEditModal(firstNeedsKeyProvider)">
                  {{ t('settings.configureNow') }}
                </UiButton>
                <UiButton v-else-if="modelCenterIssueCount > 0" variant="outline" size="sm" @click="focusModelProviderList('issues')">
                  {{ t('settings.viewIssues') }}
                </UiButton>
                <UiButton v-else variant="outline" size="sm" @click="openModelDiagnostics">
                  {{ t('settings.advancedDiagnostics') }}
                </UiButton>
              </div>
            </section>

            <details v-if="modelReadiness || modelCatalogReadiness" ref="modelDiagnosticsRef" class="model-diagnostics">
              <summary>
                <span>{{ t('settings.advancedDiagnostics') }}</span>
                <ChevronRight :size="14" />
              </summary>
              <div class="model-diagnostics-grid">
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
                  <ul v-if="modelReadiness.design_notes.length > 0" class="model-diagnostic-notes">
                    <li v-for="note in modelReadiness.design_notes" :key="note">{{ note }}</li>
                  </ul>
                </section>
                <section v-if="modelCatalogReadiness" class="model-diagnostic-section">
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
                  <ul v-if="modelCatalogReadiness.design_notes.length > 0" class="model-diagnostic-notes">
                    <li v-for="note in modelCatalogReadiness.design_notes" :key="note">{{ note }}</li>
                  </ul>
                </section>
              </div>
            </details>

              <section v-if="selectedProviderDetail" class="inspector-provider-detail">
                <div class="provider-detail-head compact">
                  <span
                    class="provider-brand-icon"
                    :style="{ color: providerPresentation(selectedProviderDetail.driver)?.brand_color, backgroundColor: providerPresentation(selectedProviderDetail.driver)?.brand_bg }"
                  >
                    <span v-if="providerPresentation(selectedProviderDetail.driver)?.icon" class="provider-brand-mark" v-html="providerPresentation(selectedProviderDetail.driver)?.icon"></span>
                    <Database v-else :size="16" />
                  </span>
                  <div class="provider-detail-info">
                    <strong>{{ selectedProviderDetail.display_name }}</strong>
                    <span class="provider-detail-driver">{{ selectedProviderDetail.driver }} · {{ connectionKindLabel(selectedProviderDetail.connection_kind) }}</span>
                  </div>
                  <UiBadge :variant="statusVariant(selectedProviderDetail.status)">
                    <Circle :size="8" />
                    {{ statusLabel(selectedProviderDetail.status) }}
                  </UiBadge>
                </div>
                <div class="provider-detail-grid compact">
                  <div v-if="selectedProviderDetail.base_url" class="provider-detail-cell">
                    <span class="provider-detail-label">{{ t('settings.baseUrl') }}</span>
                    <span class="provider-detail-value provider-detail-mono">{{ selectedProviderDetail.base_url }}</span>
                  </div>
                  <div v-if="selectedProviderDetail.model" class="provider-detail-cell">
                    <span class="provider-detail-label">{{ t('settings.modelLabel') }}</span>
                    <span class="provider-detail-value provider-detail-mono">{{ selectedProviderDetail.model }}</span>
                  </div>
                  <div class="provider-detail-cell">
                    <span class="provider-detail-label">{{ t('settings.apiKey') }}</span>
                    <span class="provider-detail-value">
                      <span :class="['provider-key-indicator', selectedProviderDetail.has_api_key ? 'has-key' : 'no-key']"></span>
                      {{ selectedProviderDetail.has_api_key ? t('settings.apiKeyStored') : t('settings.apiKeyNotSet') }}
                    </span>
                  </div>
                  <div class="provider-detail-cell">
                    <span class="provider-detail-label">{{ t('settings.connectionKind') }}</span>
                    <span class="provider-detail-value">{{ connectionKindLabel(selectedProviderDetail.connection_kind) }}</span>
                  </div>
                </div>
                <div v-if="selectedProviderDetail.status_message" class="provider-status-note compact">
                  <Terminal :size="14" />
                  <span>{{ selectedProviderDetail.status_message }}</span>
                </div>
                <div class="provider-detail-actions compact">
                  <UiButton variant="outline" size="sm" @click="openEditModal(selectedProviderDetail)">
                    <Edit3 :size="14" />
                    <span>{{ t('settings.editConfig') }}</span>
                  </UiButton>
                  <UiButton variant="outline" size="sm" :disabled="modelCenterBusy" @click="toggleProviderEnabled(selectedProviderDetail)">
                    <component :is="selectedProviderDetail.enabled ? X : Check" :size="14" />
                    <span>{{ selectedProviderDetail.enabled ? t('settings.disable') : t('settings.enable') }}</span>
                  </UiButton>
                  <UiButton v-if="confirmDeleteId !== selectedProviderDetail.id" variant="ghost" size="sm" class="provider-delete-btn" @click="confirmDeleteId = selectedProviderDetail.id">
                    <Trash2 :size="14" />
                    <span>{{ t('settings.delete') }}</span>
                  </UiButton>
                  <template v-else>
                    <span class="delete-confirm-text">{{ t('settings.confirmDeleteProvider') }}</span>
                    <UiButton variant="destructive" size="sm" :disabled="modelCenterBusy" @click="deleteProvider(selectedProviderDetail.id)">{{ t('settings.confirmDelete') }}</UiButton>
                    <UiButton variant="ghost" size="sm" @click="confirmDeleteId = ''">{{ t('settings.cancel') }}</UiButton>
                  </template>
                </div>
              </section>
            </aside>

            <aside class="center-resource-rail model-resource-navigation" :aria-label="t('settings.centerResources')">
              <div class="center-pane-heading">
                <div>
                  <span>{{ t('settings.centerResources') }}</span>
                  <strong>{{ t('settings.modelCenterResources') }}</strong>
                </div>
              </div>
            <div class="model-center-tabs" role="tablist" :aria-label="t('settings.modelCenterResources')">
              <button
                v-for="section in modelCenterSections"
                :key="section.key"
                role="tab"
                :aria-selected="modelCenterSection === section.key"
                :class="{ active: modelCenterSection === section.key }"
                @click="modelCenterSection = section.key"
              >
                <span>{{ section.label }}</span>
                <UiBadge variant="secondary">{{ section.count }}</UiBadge>
              </button>
            </div>
            </aside>

            <main class="center-resource-stage">
            <section v-if="modelCenterSection === 'suppliers'" ref="modelProviderListRef" class="center-resource-section">
              <div class="center-resource-heading">
                <div>
                  <h2>{{ t('settings.centerSuppliers') }}</h2>
                  <p>{{ t('settings.suppliersHint') }}</p>
                </div>
                <UiBadge variant="outline">{{ t('settings.coreCatalog') }}</UiBadge>
              </div>
              <div class="center-resource-grid supplier-grid supplier-list">
                <article v-for="supplier in modelCenterOverview?.suppliers ?? []" :key="supplier.supplier_id" class="center-resource-card">
                  <div class="center-resource-card-head">
                      <span
                        class="provider-brand-icon"
                        :style="{ color: providerPresentation(supplier.driver)?.brand_color, backgroundColor: providerPresentation(supplier.driver)?.brand_bg }"
                      >
                        <span v-if="providerPresentation(supplier.driver)?.icon" class="provider-brand-mark" v-html="providerPresentation(supplier.driver)?.icon"></span>
                        <Database v-else :size="16" />
                      </span>
                    <div>
                      <strong>{{ supplier.display_name }}</strong>
                      <span>{{ supplier.provider_family }} · {{ supplier.driver }}</span>
                    </div>
                    <UiBadge v-if="catalogReadinessByDriver.get(supplier.driver)?.status !== 'ready'" :variant="readinessVariant(catalogReadinessByDriver.get(supplier.driver)?.status ?? 'ready')">
                      {{ readinessStatusLabel(catalogReadinessByDriver.get(supplier.driver)?.status ?? 'ready') }}
                    </UiBadge>
                  </div>
                  <p>{{ supplierSummary(supplier) }}</p>
                  <div class="center-resource-meta">
                      <span>{{ supplierTransportLabel(supplier.transport_kind) }}</span>
                      <span>{{ supplierCredentialLabel(supplier.credential_kind) }}</span>
                    <span v-if="supplier.default_model">{{ supplier.default_model }}</span>
                  </div>
                  <div class="center-resource-actions">
                    <UiButton variant="ghost" size="sm" @click="openAddModal(providerTemplateFromSupplier(supplier))">
                      <Plus :size="14" />
                      {{ t('settings.addProvider') }}
                    </UiButton>
                  </div>
                </article>
              </div>
              <div v-if="(modelCenterOverview?.suppliers.length ?? 0) === 0" class="center-empty-state">
                <Server :size="20" />
                <span>{{ t('settings.noSuppliers') }}</span>
              </div>
            </section>

            <section v-if="modelCenterSection === 'models'" class="center-resource-section">
              <div class="center-resource-heading">
                <div>
                  <h2>{{ t('settings.centerModels') }}</h2>
                  <p>{{ t('settings.configuredModelsHint') }}</p>
                </div>
                <UiBadge variant="outline">{{ modelCatalogModeLabel(modelCenterOverview?.capabilities.model_catalog_mode) }}</UiBadge>
              </div>
              <div class="center-resource-list">
                <article v-for="model in modelCenterOverview?.models ?? []" :key="model.id" class="center-resource-list-row">
                  <div class="center-resource-primary">
                    <Cpu :size="17" />
                    <div>
                      <strong>{{ model.model_id }}</strong>
                      <span>{{ model.provider_display_name ?? model.provider_instance_id }}</span>
                    </div>
                  </div>
                  <div class="center-resource-meta">
                    <span v-for="source in model.configuration_sources" :key="source">{{ configuredModelSourceLabel(source) }}</span>
                    <span v-for="purpose in model.route_purposes" :key="purpose">{{ purpose }}</span>
                  </div>
                  <UiBadge :variant="statusVariant(model.status)">{{ statusLabel(model.status) }}</UiBadge>
                  <UiButton
                    variant="outline"
                    size="sm"
                    :disabled="modelCenterBusy || !modelCenterOverview?.capabilities.model_discovery_refresh"
                    :title="modelCenterOverview?.capabilities.model_discovery_refresh ? t('settings.refreshModels') : t('settings.modelDiscoveryUnsupported')"
                    @click="refreshProviderModels(model.provider_instance_id)"
                  >
                    <Server :size="14" />
                    {{ t('settings.refreshModels') }}
                  </UiButton>
                </article>
              </div>
              <div v-if="(modelCenterOverview?.models.length ?? 0) === 0" class="center-empty-state">
                <Cpu :size="20" />
                <span>{{ t('settings.noConfiguredModels') }}</span>
              </div>
            </section>

            <section v-if="modelCenterSection === 'cli'" class="center-resource-section">
              <div class="center-resource-heading">
                <div>
                  <h2>CLI</h2>
                  <p>{{ t('settings.cliRuntimeHint') }}</p>
                </div>
              </div>
              <div class="center-resource-list">
                <article v-for="runtime in modelCenterOverview?.cli_runtimes ?? []" :key="runtime.runtime_id" class="center-resource-list-row">
                  <div class="center-resource-primary">
                    <Terminal :size="17" />
                    <div>
                      <strong>{{ runtime.display_name }}</strong>
                      <span>{{ runtime.driver }} · {{ runtime.runtime_id }}</span>
                    </div>
                  </div>
                  <div class="center-resource-paths">
                    <code>{{ runtime.binary_path || t('settings.pathNotConfigured') }}</code>
                    <code>{{ runtime.home_path || t('settings.workspaceNotConfigured') }}</code>
                  </div>
                  <UiBadge :variant="statusVariant(runtime.status)">{{ statusLabel(runtime.status) }}</UiBadge>
                  <UiButton
                    v-if="providerById(runtime.provider_instance_id)"
                    variant="outline"
                    size="sm"
                    @click="openProviderById(runtime.provider_instance_id)"
                  >
                    <Settings2 :size="14" />
                    {{ t('settings.editConfig') }}
                  </UiButton>
                </article>
              </div>
              <div v-if="(modelCenterOverview?.cli_runtimes.length ?? 0) === 0" class="center-empty-state">
                <Terminal :size="20" />
                <span>{{ t('settings.noCliRuntimes') }}</span>
              </div>
            </section>

            <section v-if="modelCenterSection === 'acp'" class="center-resource-section">
              <div class="center-resource-heading">
                <div>
                  <h2>ACP</h2>
                  <p>{{ t('settings.acpRuntimeHint') }}</p>
                </div>
                <UiButton variant="outline" size="sm" @click="router.push('/market')">
                  <Plus :size="14" />
                  {{ t('settings.manageInMarketplace') }}
                </UiButton>
              </div>
              <div class="center-resource-list">
                <article v-for="runtime in modelCenterOverview?.acp_runtimes ?? []" :key="runtime.runtime_id" class="center-resource-list-row">
                  <div class="center-resource-primary">
                    <Workflow :size="17" />
                    <div>
                      <strong>{{ runtime.display_name }}</strong>
                      <span>{{ acpRuntimeSourceLabel(runtime.source) }} · {{ runtime.runtime_id }}</span>
                    </div>
                  </div>
                  <div class="center-resource-meta">
                    <span v-for="capability in runtime.capabilities.slice(0, 4)" :key="capability">{{ capability }}</span>
                  </div>
                  <UiBadge :variant="statusVariant(runtime.status)">{{ statusLabel(runtime.status) }}</UiBadge>
                  <UiButton
                    v-if="runtime.adapter_id"
                    variant="outline"
                    size="sm"
                    :disabled="modelCenterBusy || !modelCenterOverview?.capabilities.acp_probe"
                    @click="probeAcpRuntime(runtime)"
                  >
                    <Server :size="14" />
                    {{ t('settings.probe') }}
                  </UiButton>
                </article>
              </div>
              <div v-if="(modelCenterOverview?.acp_runtimes.length ?? 0) === 0" class="center-empty-state">
                <Workflow :size="20" />
                <span>{{ t('settings.noAcpRuntimes') }}</span>
              </div>
            </section>

            <section v-if="modelCenterSection === 'api'" ref="modelProviderListRef" class="model-provider-section">
              <div class="model-provider-toolbar">
                <div class="model-provider-search">
                  <Search :size="15" />
                  <UiInput v-model="modelProviderQuery" :placeholder="t('settings.providerSearchPlaceholder')" />
                </div>
                <div class="model-provider-filters" role="group" :aria-label="t('settings.providerFilters')">
                  <button :class="{ active: modelProviderFilter === 'all' }" :aria-pressed="modelProviderFilter === 'all'" @click="modelProviderFilter = 'all'">
                    {{ t('settings.filterAll') }}
                  </button>
                  <button :class="{ active: modelProviderFilter === 'issues' }" :aria-pressed="modelProviderFilter === 'issues'" @click="modelProviderFilter = 'issues'">
                    {{ t('settings.filterIssues') }}
                    <span v-if="modelCenterIssueCount > 0">{{ modelCenterIssueCount }}</span>
                  </button>
                  <button :class="{ active: modelProviderFilter === 'configured' }" :aria-pressed="modelProviderFilter === 'configured'" @click="modelProviderFilter = 'configured'">
                    {{ t('settings.filterConfigured') }}
                  </button>
                </div>
                <span class="model-provider-count">{{ t('settings.providerResultCount', { visible: filteredModelCenterRows.length, total: modelCenterRows.length }) }}</span>
              </div>

              <div class="model-provider-table">
                <div class="model-provider-table-head" aria-hidden="true">
                  <span>{{ t('settings.providerName') }}</span>
                  <span>{{ t('settings.connectionKind') }}</span>
                  <span>{{ t('settings.modelLabel') }}</span>
                  <span>{{ t('settings.status') }}</span>
                  <span>{{ t('settings.actions') }}</span>
                </div>
                <template v-for="row in filteredModelCenterRows" :key="row.key">
                  <div class="model-provider-row" :class="{ issue: row.kind === 'instance' && ['blocked', 'warning'].includes(row.readiness?.status ?? '') }">
                    <button
                      class="model-provider-identity"
                      :aria-expanded="row.kind === 'instance' ? selectedProviderDetailId === row.provider.id : undefined"
                      @click="row.kind === 'instance' ? toggleProviderDetail(row.provider.id) : openAddModal(row.template)"
                    >
                      <span
                        class="provider-brand-icon"
                        :style="{ color: row.template?.brand_color, backgroundColor: row.template?.brand_bg }"
                      >
                        <span v-if="row.template?.icon" class="provider-brand-mark" v-html="row.template?.icon"></span>
                        <Database v-else :size="16" />
                      </span>
                      <span>
                        <strong :title="row.display_name">{{ row.display_name }}</strong>
                        <small :title="row.driver">{{ row.driver }}</small>
                      </span>
                    </button>
                    <span class="model-provider-cell model-provider-connection">{{ connectionKindLabel(row.connection_kind) }}</span>
                    <span class="model-provider-cell model-provider-model" :title="row.model || t('settings.noModel')">{{ row.model || t('settings.noModel') }}</span>
                    <div class="model-provider-status">
                      <UiBadge v-if="row.kind === 'instance'" :variant="statusVariant(row.provider.status)">
                        <Circle :size="8" />
                        {{ statusLabel(row.provider.status) }}
                      </UiBadge>
                      <UiBadge v-else variant="outline">{{ t('settings.notAdded') }}</UiBadge>
                    </div>
                    <div class="model-provider-actions">
                      <template v-if="row.kind === 'instance'">
                        <UiButton
                          v-if="row.template"
                          variant="ghost"
                          size="icon"
                          :title="t('settings.addSameProvider')"
                          @click="openAddModal(row.template)"
                        >
                          <Plus :size="14" />
                        </UiButton>
                        <UiButton variant="ghost" size="icon" :title="t('settings.editConfig')" @click="openEditModal(row.provider)">
                          <Settings2 :size="14" />
                        </UiButton>
                        <UiButton
                          variant="ghost"
                          size="icon"
                          :title="selectedProviderDetailId === row.provider.id ? t('settings.collapseDetails') : t('settings.expandDetails')"
                          :aria-expanded="selectedProviderDetailId === row.provider.id"
                          @click="toggleProviderDetail(row.provider.id)"
                        >
                          <ChevronRight :size="14" class="provider-chevron" :class="{ open: selectedProviderDetailId === row.provider.id }" />
                        </UiButton>
                      </template>
                      <UiButton v-else variant="outline" size="sm" @click="openAddModal(row.template)">
                        <Plus :size="14" />
                        {{ t('settings.addProvider') }}
                      </UiButton>
                    </div>
                    <span class="model-provider-mobile-meta">
                      {{ connectionKindLabel(row.connection_kind) }} · {{ row.model || row.driver }}
                    </span>
                  </div>

                </template>
                <div v-if="filteredModelCenterRows.length === 0" class="model-provider-empty">
                  <Search :size="18" />
                  <span>{{ t('settings.noProviderResults') }}</span>
                </div>
              </div>
            </section>
            </main>
            </div>
            </div>
</template>
