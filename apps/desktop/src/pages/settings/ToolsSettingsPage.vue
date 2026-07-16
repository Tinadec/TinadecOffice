<script setup lang="ts">
import { Circle, Info, Search, Server, ShieldCheck } from '@lucide/vue'
import { UiBadge, UiButton, UiInput } from '@/components/ui'
import { useToolSettings } from './toolSettings'

const {
  codeSuiteToolList,
  codexPrimitiveTools,
  executionAgents,
  harnessManifest,
  loadToolCatalog,
  loadToolDiscovery,
  loading,
  manifestAgentLayers,
  manifestProviders,
  manifestRiskPolicies,
  projectTemplates,
  readinessVariant,
  settingsErrorLabel,
  sortedToolDiscoveryResults,
  supportedLanguages,
  t,
  toolCatalogError,
  toolDiscoveryError,
  toolDiscoveryLoading,
  toolDiscoveryQuery,
  toolDiscoveryRisk,
  toolDiscoverySource,
  toolLayerReadiness,
  toolRiskOptions,
  toolSourceOptions,
  warningToolLayerAgents,
  warningToolLayerTools,
} = useToolSettings()
</script>

<template>
  <div class="model-center-heading">
              <div>
                <h1>{{ t('settings.toolLayerTitle') }}</h1>
                <p>{{ t('settings.toolLayerSubtitle') }}</p>
              </div>
              <UiButton variant="outline" size="sm" :disabled="loading" @click="loadToolCatalog">
                <Server :size="14" />
                <span>{{ t('settings.refresh') }}</span>
              </UiButton>
            </div>

            <div v-if="toolCatalogError" class="center-message error">
              <Info :size="16" />
              <span>{{ settingsErrorLabel(toolCatalogError) }}</span>
              <UiButton variant="outline" size="sm" @click="loadToolCatalog">{{ t('settings.retry') }}</UiButton>
            </div>

            <template v-if="!toolCatalogError">

            <div v-if="harnessManifest" class="provider-status-note harness-manifest-note">
              <ShieldCheck :size="14" />
              <span>{{ harnessManifest.runtime }} · {{ harnessManifest.ownership_model }}</span>
            </div>

            <section v-if="toolLayerReadiness" class="tool-layer-readiness-panel">
              <div class="model-readiness-head">
                <div>
                  <h2>{{ t('settings.toolLayerReadiness') }}</h2>
                  <span>{{ toolLayerReadiness.receipt_id }}</span>
                </div>
                <UiBadge :variant="readinessVariant(toolLayerReadiness.status)">
                  <Circle :size="8" />
                  {{ toolLayerReadiness.status }}
                </UiBadge>
              </div>
              <div class="model-readiness-metrics">
                <div>
                  <strong>{{ toolLayerReadiness.tool_count }}</strong>
                  <span>{{ t('settings.toolsCount') }}</span>
                </div>
                <div>
                  <strong>{{ toolLayerReadiness.execution_agent_count }}</strong>
                  <span>{{ t('settings.executionAgents') }}</span>
                </div>
                <div>
                  <strong>{{ toolLayerReadiness.human_checkpoint_tool_count }}</strong>
                  <span>{{ t('settings.humanCheckpoints') }}</span>
                </div>
                <div>
                  <strong>{{ toolLayerReadiness.unresolved_scope_count }}</strong>
                  <span>{{ t('settings.unresolvedScopes') }}</span>
                </div>
              </div>
              <div v-if="warningToolLayerTools.length > 0 || warningToolLayerAgents.length > 0" class="tool-layer-readiness-grid">
                <div
                  v-for="tool in warningToolLayerTools.slice(0, 4)"
                  :key="tool.tool_id"
                  class="tool-layer-readiness-row"
                >
                  <div>
                    <strong>{{ tool.display_name }}</strong>
                    <span>{{ tool.source }} · {{ tool.provider_layer }} · {{ tool.risk }}</span>
                  </div>
                  <UiBadge :variant="readinessVariant(tool.status)">{{ tool.status }}</UiBadge>
                </div>
                <div
                  v-for="agent in warningToolLayerAgents.slice(0, 4)"
                  :key="agent.agent_id"
                  class="tool-layer-readiness-row"
                >
                  <div>
                    <strong>{{ agent.agent_name }}</strong>
                    <span>{{ t('settings.toolsCount') }} {{ agent.dispatchable_tool_count }} · {{ t('settings.unresolvedScopes') }} {{ agent.unresolved_scope_count }}</span>
                  </div>
                  <UiBadge :variant="readinessVariant(agent.status)">{{ agent.status }}</UiBadge>
                </div>
              </div>
            </section>

            <div v-if="harnessManifest?.tool_registry" class="model-section-header">
              <h2>{{ t('settings.toolRegistryGovernance') }}</h2>
              <UiBadge variant="outline">{{ harnessManifest.tool_registry.canonical_tool_count }}</UiBadge>
            </div>

            <div v-if="harnessManifest?.tool_registry" class="harness-registry-summary">
              <div class="harness-registry-metrics">
                <div>
                  <span>{{ t('settings.declaredTools') }}</span>
                  <strong>{{ harnessManifest.tool_registry.declared_tool_count }}</strong>
                </div>
                <div>
                  <span>{{ t('settings.canonicalTools') }}</span>
                  <strong>{{ harnessManifest.tool_registry.canonical_tool_count }}</strong>
                </div>
                <div>
                  <span>{{ t('settings.duplicateToolIds') }}</span>
                  <strong>{{ harnessManifest.tool_registry.duplicate_tool_id_count }}</strong>
                </div>
              </div>
              <p>{{ harnessManifest.tool_registry.selection_policy }}</p>
              <div class="model-capability-row compact">
                <span v-for="source in harnessManifest.tool_registry.source_precedence" :key="source">{{ source }}</span>
              </div>
              <div v-if="harnessManifest.tool_registry.duplicate_tool_ids.length > 0" class="model-capability-row compact">
                <span v-for="toolId in harnessManifest.tool_registry.duplicate_tool_ids" :key="toolId">{{ toolId }}</span>
              </div>
            </div>

            <div v-if="harnessManifest?.design_notes.length" class="model-section-header">
              <h2>{{ t('settings.harnessDesignNotes') }}</h2>
              <UiBadge variant="outline">{{ harnessManifest.design_notes.length }}</UiBadge>
            </div>

            <div v-if="harnessManifest?.design_notes.length" class="harness-design-notes">
              <span v-for="note in harnessManifest.design_notes" :key="note">{{ note }}</span>
            </div>

            <div v-if="manifestAgentLayers.length > 0" class="model-section-header">
              <h2>{{ t('settings.harnessAgentLayers') }}</h2>
              <UiBadge variant="outline">{{ manifestAgentLayers.length }}</UiBadge>
            </div>

            <div v-if="manifestAgentLayers.length > 0" class="harness-manifest-grid">
              <div v-for="layer in manifestAgentLayers" :key="layer.layer" class="harness-manifest-panel">
                <div class="harness-panel-head">
                  <span class="harness-panel-title">{{ layer.layer }}</span>
                  <UiBadge :variant="layer.approval_required ? 'secondary' : 'outline'">
                    {{ layer.enabled_agent_count }}/{{ layer.agent_count }}
                  </UiBadge>
                </div>
                <p class="harness-panel-meta">{{ layer.role }}</p>
                <div class="harness-panel-stats">
                  <span>{{ t('settings.maxParallel') }} {{ layer.max_parallel_executors }}</span>
                  <span>{{ layer.worktree_isolation ? t('settings.worktreeIsolated') : t('settings.sharedWorkspace') }}</span>
                </div>
                <div class="model-capability-row compact">
                  <span v-for="agentType in layer.agent_types" :key="agentType">{{ agentType }}</span>
                </div>
              </div>
            </div>

            <div v-if="manifestProviders.length > 0" class="model-section-header">
              <h2>{{ t('settings.toolProviders') }}</h2>
              <UiBadge variant="outline">{{ manifestProviders.length }}</UiBadge>
            </div>

            <div v-if="manifestProviders.length > 0" class="harness-manifest-grid">
              <div v-for="provider in manifestProviders" :key="provider.source" class="harness-manifest-panel">
                <div class="harness-panel-head">
                  <span class="harness-panel-title">{{ provider.display_name }}</span>
                  <UiBadge :variant="provider.status === 'active' ? 'secondary' : 'outline'">{{ provider.status }}</UiBadge>
                </div>
                <p class="harness-panel-meta">{{ provider.layer }} · {{ provider.source }}</p>
                <div class="harness-panel-stats">
                  <span>{{ t('settings.toolsCount') }} {{ provider.tool_count }}</span>
                  <span>{{ t('settings.approvalCount') }} {{ provider.approval_required_count }}</span>
                  <span>{{ t('settings.futureCount') }} {{ provider.future_tool_count }}</span>
                </div>
                <div class="model-capability-row compact">
                  <span v-for="prefix in provider.capability_prefixes" :key="prefix">{{ prefix }}</span>
                </div>
              </div>
            </div>

            <div v-if="manifestRiskPolicies.length > 0" class="model-section-header">
              <h2>{{ t('settings.riskPolicies') }}</h2>
              <UiBadge variant="outline">{{ manifestRiskPolicies.length }}</UiBadge>
            </div>

            <div v-if="manifestRiskPolicies.length > 0" class="agent-tool-grid manifest-risk-row">
              <article
                v-for="risk in manifestRiskPolicies"
                :key="risk.risk"
                class="agent-tool-chip"
                :class="{ risky: risk.requires_human_checkpoint }"
              >
                <span class="agent-tool-name">{{ risk.risk }}</span>
                <span class="agent-tool-risk">{{ risk.tool_count }} · {{ risk.policy_summary }}</span>
              </article>
            </div>

            <div class="model-section-header">
              <h2>{{ t('settings.toolDiscovery') }}</h2>
              <UiBadge variant="outline">{{ sortedToolDiscoveryResults.length }}</UiBadge>
            </div>

            <div class="tool-discovery-controls">
              <UiInput
                v-model="toolDiscoveryQuery"
                :placeholder="t('settings.toolDiscoveryPlaceholder')"
                @keyup.enter="loadToolDiscovery"
              />
              <select v-model="toolDiscoverySource" class="settings-select" @change="loadToolDiscovery">
                <option value="all">{{ t('settings.allSources') }}</option>
                <option v-for="source in toolSourceOptions" :key="source" :value="source">{{ source }}</option>
              </select>
              <select v-model="toolDiscoveryRisk" class="settings-select" @change="loadToolDiscovery">
                <option value="all">{{ t('settings.allRisks') }}</option>
                <option v-for="risk in toolRiskOptions" :key="risk" :value="risk">{{ risk }}</option>
              </select>
              <UiButton size="sm" :disabled="toolDiscoveryLoading" @click="loadToolDiscovery">
                <Search :size="14" />
                <span>{{ t('settings.search') }}</span>
              </UiButton>
            </div>

            <div v-if="toolDiscoveryError" class="center-message error">
              <Info :size="16" />
              <span>{{ settingsErrorLabel(toolDiscoveryError) }}</span>
              <UiButton variant="outline" size="sm" @click="loadToolDiscovery">{{ t('settings.retry') }}</UiButton>
            </div>

            <div class="tool-discovery-grid">
              <button
                v-for="result in sortedToolDiscoveryResults"
                :key="result.tool.id"
                class="tool-discovery-card"
                :class="{ risky: result.requires_human_checkpoint }"
              >
                <span class="tool-discovery-title">{{ result.tool.display_name }}</span>
                <span class="tool-discovery-meta">{{ result.tool.source }} · {{ result.provider_layer }} · {{ result.tool.risk }}</span>
                <span class="tool-discovery-meta">{{ result.approval_summary }}</span>
                <span class="tool-discovery-fields">
                  {{ t('settings.matchedFields') }} {{ result.matched_fields.join(', ') }}
                </span>
              </button>
            </div>
            <p v-if="!toolDiscoveryLoading && !toolDiscoveryError && sortedToolDiscoveryResults.length === 0" class="quiet">
              {{ t('settings.noToolSearchResults') }}
            </p>

            <div class="model-section-header">
              <h2>{{ t('settings.codeToolSuite') }}</h2>
              <UiBadge variant="secondary">{{ codeSuiteToolList.length }}</UiBadge>
            </div>

            <div v-if="supportedLanguages.length > 0" class="model-capability-row">
              <span v-for="language in supportedLanguages" :key="language">{{ language }}</span>
            </div>

            <div class="model-section-header">
              <h2>{{ t('settings.projectTemplates') }}</h2>
              <UiBadge variant="outline">{{ projectTemplates.length }}</UiBadge>
            </div>

            <div class="agent-tool-grid">
              <button
                v-for="template in projectTemplates"
                :key="template.id"
                class="agent-tool-chip"
              >
                <span class="agent-tool-name">{{ template.name }}</span>
                <span class="agent-tool-risk">{{ template.language }} · {{ template.package_manager }}</span>
              </button>
            </div>

            <div class="agent-tool-grid">
              <button
                v-for="tool in codeSuiteToolList"
                :key="tool.id"
                class="agent-tool-chip active"
                :class="{ risky: tool.requires_approval }"
              >
                <span class="agent-tool-name">{{ tool.display_name }}</span>
                <span class="agent-tool-risk">
                  {{ tool.requires_approval ? t('settings.approvalRequired') : t('settings.readOnlyTool') }} · {{ tool.risk }}
                </span>
              </button>
            </div>
            <p v-if="!toolCatalogError && codeSuiteToolList.length === 0" class="quiet">{{ t('settings.noTools') }}</p>

            <div class="model-section-header">
              <h2>{{ t('settings.codexPrimitiveTools') }}</h2>
              <UiBadge variant="outline">{{ codexPrimitiveTools.length }}</UiBadge>
            </div>

            <div class="agent-tool-grid">
              <button
                v-for="tool in codexPrimitiveTools"
                :key="tool.id"
                class="agent-tool-chip"
                :class="{ risky: tool.requires_approval }"
              >
                <span class="agent-tool-name">{{ tool.display_name }}</span>
                <span class="agent-tool-risk">{{ tool.source }} · {{ tool.risk }}</span>
              </button>
            </div>
            </template>
</template>
