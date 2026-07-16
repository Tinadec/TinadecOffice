<script setup lang="ts">
import { Bot, Cpu, Info, LayoutGrid, List, MoreHorizontal, PanelRight, Plus, RefreshCw, Save, Search, Server, Settings2, ShieldCheck, Terminal, Workflow, X } from '@lucide/vue'
import { UiBadge, UiButton, UiInput, UiLabel, UiSkeleton, UiSwitch } from '@/components/ui'
import AgentTopologyCanvas from '@/components/AgentTopologyCanvas.vue'
import { useAgentSettings } from './agentSettings'

const {
  acpRuntimeSourceLabel,
  addAgentCapability,
  agentCandidates,
  agentCenterDiagnostics,
  agentCenterError,
  agentCenterLoading,
  agentCenterOverview,
  agentEditCapabilities,
  agentEditDescription,
  agentEditSystemPrompt,
  agentEditTools,
  agentLayerLabel,
  agentModeLabel,
  agentModeSummary,
  agentModes,
  agentNewCapability,
  agentPolicyLabel,
  agentRuntimeAcpId,
  agentRuntimeAcpQuery,
  agentRuntimeBindings,
  agentRuntimeBusy,
  agentRuntimeCliId,
  agentRuntimeCliQuery,
  agentRuntimeModelKey,
  agentRuntimeModelQuery,
  agentRuntimeNotice,
  agentRuntimeProviderId,
  agentRuntimeProviderQuery,
  agentRuntimeSelection,
  agentTypeLabel,
  agentViewMode,
  agents,
  availableTools,
  busy,
  candidateStatusLabel,
  centerDiagnosticLabel,
  closeAgentConfig,
  configuredAgentMode,
  configuringAgent,
  configuringLegacyWarning,
  configuringRuntimeBinding,
  executionAgents,
  filteredRuntimeAcpOptions,
  filteredRuntimeCliOptions,
  filteredRuntimeModels,
  filteredRuntimeProviders,
  loadAgentCenter,
  modelOptionKey,
  openAgentConfig,
  openAgentConfigById,
  planningAgents,
  providers,
  removeAgentCapability,
  routes,
  runtimeBindingInput,
  runtimeBindingWritable,
  runtimeSourceSummary,
  saveAgentProfile,
  saveAgentRuntimeBinding,
  selectedAgent,
  selectedAgentId,
  setAgentEnabled,
  settingsErrorLabel,
  statusLabel,
  t,
  toggleAgentTool,
  topologyAgentLabels,
  topologyCandidateLabels,
  updateAgentMode,
} = useAgentSettings()
</script>

<template>
  <div class="center-page agent-center-page">
            <div class="center-command-bar">
              <div>
                <span class="center-kicker">{{ t('settings.agents') }}</span>
                <h1>{{ t('settings.agentCenter') }}</h1>
                <p>{{ t('settings.agentCenterSubtitle') }}</p>
              </div>
              <div class="center-command-actions">
                <div class="agent-view-toggle">
                  <button
                    :class="['agent-view-btn', { active: agentViewMode === 'topology' }]"
                    :title="t('settings.topologyView')"
                    :aria-label="t('settings.topologyView')"
                    :aria-pressed="agentViewMode === 'topology'"
                    @click="agentViewMode = 'topology'"
                  >
                    <LayoutGrid :size="15" />
                  </button>
                  <button
                    :class="['agent-view-btn', { active: agentViewMode === 'list' }]"
                    :title="t('settings.listView')"
                    :aria-label="t('settings.listView')"
                    :aria-pressed="agentViewMode === 'list'"
                    @click="agentViewMode = 'list'"
                  >
                    <List :size="15" />
                  </button>
                </div>
                <UiButton variant="outline" size="sm" :disabled="agentCenterLoading || agentRuntimeBusy" @click="loadAgentCenter">
                  <RefreshCw :size="14" />
                  <span>{{ t('settings.refresh') }}</span>
                </UiButton>
              </div>
            </div>

            <section v-if="!agentCenterError || agentCenterOverview" class="center-overview-receipt agent-overview-receipt" :aria-label="t('settings.centerOverview')">
              <div class="center-receipt-item ready">
                <Settings2 :size="17" />
                <div>
                  <span>{{ t('settings.agentProfilesWritable') }}</span>
                  <strong>{{ t('settings.agentProfilesWritableHint') }}</strong>
                </div>
                <UiBadge variant="default">{{ t('settings.writable') }}</UiBadge>
              </div>
              <div class="center-receipt-item preview" :class="{ ready: agentCenterOverview?.capabilities.agent_runtime_binding_write }">
                <Workflow :size="17" />
                <div>
                  <span>{{ t('settings.runtimePreviewOnly') }}</span>
                  <strong>{{ t('settings.runtimePreviewOnlyHint') }}</strong>
                </div>
                <UiBadge :variant="agentCenterOverview?.capabilities.agent_runtime_binding_write ? 'default' : 'secondary'">
                  {{ agentCenterOverview?.capabilities.agent_runtime_binding_write ? t('settings.writable') : t('settings.previewOnly') }}
                </UiBadge>
              </div>
              <div class="center-receipt-item configured">
                <Bot :size="17" />
                <div>
                  <span>{{ t('settings.activeAgents') }}</span>
                  <strong>{{ agents.filter(agent => agent.enabled).length }} / {{ agents.length }}</strong>
                </div>
                <UiBadge variant="outline">{{ planningAgents.length }} + {{ executionAgents.length }}</UiBadge>
              </div>
            </section>

            <div v-if="agentCenterLoading && !agentCenterOverview" class="center-loading-state" aria-live="polite">
              <UiSkeleton v-for="index in 3" :key="index" class="center-loading-line" />
            </div>

            <div v-if="agentCenterError" class="center-message error">
              <Info :size="16" />
              <span>{{ settingsErrorLabel(agentCenterError) }}</span>
              <UiButton variant="outline" size="sm" @click="loadAgentCenter">{{ t('settings.retry') }}</UiButton>
            </div>
            <div v-if="agentCenterDiagnostics.length > 0" class="center-message warning center-diagnostics-message">
              <Info :size="16" />
              <div class="center-message-content">
                <strong>{{ t('settings.centerDiagnostics') }}</strong>
                <ul>
                  <li v-for="diagnostic in agentCenterDiagnostics" :key="`${diagnostic.code}:${diagnostic.source ?? ''}:${diagnostic.route_purpose ?? ''}:${diagnostic.agent_ids?.join(',') ?? ''}:${diagnostic.status ?? ''}`">
                    {{ centerDiagnosticLabel(diagnostic) }}
                  </li>
                </ul>
              </div>
              <UiButton variant="outline" size="sm" :disabled="agentCenterLoading" @click="loadAgentCenter">{{ t('settings.retry') }}</UiButton>
            </div>

            <div v-if="!agentCenterLoading && !agentCenterError && agents.length === 0" class="center-empty-state center-empty-state-prominent">
              <Bot :size="22" />
              <div>
                <strong>{{ t('settings.noAgents') }}</strong>
                <span>{{ t('settings.noAgentsHint') }}</span>
              </div>
            </div>

            <div v-if="!agentCenterError || agentCenterOverview" class="center-workbench agent-workbench" :class="`view-${agentViewMode}`">
            <aside class="center-resource-rail" :aria-label="t('settings.centerResources')">
              <div class="center-pane-heading">
                <div>
                  <span>{{ t('settings.centerResources') }}</span>
                  <strong>{{ t('settings.agentProfiles') }}</strong>
                </div>
              </div>

              <section class="agent-column compact">
                <div class="model-section-header">
                  <h2>{{ t('settings.planningLayer') }}</h2>
                  <UiBadge variant="secondary">{{ planningAgents.length }}</UiBadge>
                </div>
                <article
                  v-for="agent in planningAgents"
                  :key="agent.id"
                  class="agent-card"
                  :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
                >
                  <button class="agent-card-select" @click="openAgentConfig(agent)">
                    <div class="agent-card-icon"><Workflow :size="17" /></div>
                    <div class="agent-card-main">
                      <strong>{{ agentTypeLabel(agent.agent_type) }}</strong>
                      <span>{{ agentModeLabel(agent.mode) }} · {{ agent.id }}</span>
                      <small :title="runtimeSourceSummary(agentRuntimeBindings[agent.id])">{{ runtimeSourceSummary(agentRuntimeBindings[agent.id]) || t('settings.runtimeUnresolved') }}</small>
                    </div>
                    <UiBadge :variant="agent.enabled ? 'default' : 'secondary'">
                      {{ agent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
                    </UiBadge>
                  </button>
                  <button class="agent-card-more" :title="t('settings.openAgentConfig')" @click.stop="openAgentConfig(agent)">
                    <MoreHorizontal :size="16" />
                  </button>
                </article>
              </section>

              <section class="agent-column compact">
                <div class="model-section-header">
                  <h2>{{ t('settings.executionLayer') }}</h2>
                  <UiBadge variant="secondary">{{ executionAgents.length }}</UiBadge>
                </div>
                <article
                  v-for="agent in executionAgents"
                  :key="agent.id"
                  class="agent-card"
                  :class="{ active: selectedAgentId === agent.id, disabled: !agent.enabled }"
                >
                  <button class="agent-card-select" @click="openAgentConfig(agent)">
                    <div class="agent-card-icon execution"><Cpu :size="17" /></div>
                    <div class="agent-card-main">
                      <strong>{{ agentTypeLabel(agent.agent_type) }}</strong>
                      <span>{{ agentModeLabel(agent.mode) }} · {{ agent.id }}</span>
                      <small :title="runtimeSourceSummary(agentRuntimeBindings[agent.id])">{{ runtimeSourceSummary(agentRuntimeBindings[agent.id]) || t('settings.runtimeUnresolved') }}</small>
                    </div>
                    <UiBadge :variant="agent.enabled ? 'default' : 'secondary'">
                      {{ agent.enabled ? t('settings.defaultEnabled') : t('settings.statusDisabled') }}
                    </UiBadge>
                  </button>
                  <button class="agent-card-more" :title="t('settings.openAgentConfig')" @click.stop="openAgentConfig(agent)">
                    <MoreHorizontal :size="16" />
                  </button>
                </article>
              </section>
            </aside>

            <main class="center-resource-stage">

            <div v-if="agentViewMode === 'topology'" class="agent-topology-section">
              <AgentTopologyCanvas
                :agents="agents"
                :candidates="agentCandidates"
                :providers="providers"
                :routes="routes"
                :runtime-bindings="agentRuntimeBindings"
                :selected-agent-id="selectedAgentId"
                :agent-labels="topologyAgentLabels"
                :candidate-labels="topologyCandidateLabels"
                @select-agent="openAgentConfigById"
                @configure-agent="openAgentConfigById"
              />
            </div>

            <div v-if="agentViewMode === 'list'" class="agent-list-summary">
              <PanelRight :size="16" />
              <span>{{ selectedAgent ? agentTypeLabel(selectedAgent.agent_type) : t('settings.pleaseOpenAgentConfig') }}</span>
              <UiButton v-if="selectedAgent" variant="outline" size="sm" @click="openAgentConfig(selectedAgent)">
                <Settings2 :size="14" />
                {{ t('settings.openAgentConfig') }}
              </UiButton>
            </div>
            </main>

            <aside class="center-inspector agent-inspector" :aria-label="t('settings.centerInspector')">
              <div class="center-pane-heading">
                <div>
                  <span>{{ t('settings.centerInspector') }}</span>
                  <strong>{{ configuringAgent ? agentTypeLabel(configuringAgent.agent_type) : t('settings.agentConfiguration') }}</strong>
                </div>
                <PanelRight :size="16" />
              </div>
              <div v-if="configuringAgent" class="agent-detail-panel">
                <div class="agent-detail-head">
                  <div class="agent-card-icon" :class="{ execution: configuringAgent.layer === 'execution' }">
                    <component :is="configuringAgent.layer === 'planning' ? Workflow : Cpu" :size="20" />
                  </div>
                  <div>
                    <h2>{{ agentTypeLabel(configuringAgent.agent_type) }}</h2>
                    <p>{{ agentTypeLabel(configuringAgent.agent_type) }} · {{ agentLayerLabel(configuringAgent.layer) }}</p>
                  </div>
                  <UiButton variant="ghost" size="icon" :title="t('settings.closeConfig')" @click="closeAgentConfig">
                    <X :size="16" />
                  </UiButton>
                </div>

                <!-- 启用开关 -->
                <div class="agent-config-switch">
                  <div>
                    <strong>{{ t('settings.agentEnabled') }}</strong>
                    <span>{{ configuringAgent.is_built_in ? t('settings.builtInAgent') : configuringAgent.id }}</span>
                  </div>
                  <UiSwitch
                    :model-value="configuringAgent.enabled"
                    :disabled="busy"
                    @update:model-value="setAgentEnabled(configuringAgent, $event)"
                  />
                </div>

                <!-- 运行模式 -->
                <div class="agent-config-section">
                  <div class="agent-config-section-title">{{ t('settings.agentModeTitle') }}</div>
                  <div class="agent-mode-grid">
                    <button
                      v-for="mode in agentModes"
                      :key="mode.id"
                      class="agent-mode-card"
                      :class="{ active: configuringAgent.mode === mode.id }"
                      @click="updateAgentMode(configuringAgent, mode.id)"
                    >
                      <strong>{{ agentModeLabel(mode.id) }}</strong>
                      <span>{{ agentModeSummary(mode) }}</span>
                      <small>
                        {{ t('settings.parallelExecutors') }} {{ mode.max_parallel_executors }}
                        · {{ mode.worktree_isolation ? t('settings.worktreeOn') : t('settings.worktreeOff') }}
                      </small>
                    </button>
                  </div>
                  <div v-if="configuredAgentMode" class="agent-policy-strip">
                    <ShieldCheck :size="16" />
                    <span>
                      {{ configuredAgentMode.approval_required ? t('settings.approvalGateOn') : t('settings.approvalGateOff') }}
                      · {{ agentPolicyLabel(configuredAgentMode.budget_policy) }}
                    </span>
                  </div>
                </div>

                <!-- 运行来源 -->
                <div class="agent-config-section">
                  <div class="agent-config-section-title">{{ t('settings.agentRuntimeSource') }}</div>
                  <div class="agent-detail-grid">
                    <div>
                      <span>{{ t('settings.routePurpose') }}</span>
                      <strong>{{ configuringRuntimeBinding?.route_purpose ?? configuringAgent.model_route_purpose }}</strong>
                    </div>
                    <div>
                      <span>{{ t('settings.effectiveRuntime') }}</span>
                      <strong>{{ runtimeSourceSummary(configuringRuntimeBinding) || t('settings.runtimeUnresolved') }}</strong>
                    </div>
                  </div>

                  <div v-if="configuringLegacyWarning" class="runtime-binding-warning">
                    <Info :size="16" />
                    <div>
                      <strong>{{ t('settings.sharedLegacyRoute', { purpose: configuringLegacyWarning.purpose }) }}</strong>
                      <span>{{ t('settings.sharedLegacyRouteHint', { count: configuringLegacyWarning.agent_ids.length }) }}</span>
                    </div>
                  </div>

                  <div class="runtime-source-grid">
                    <button :class="{ active: agentRuntimeSelection === 'inherit' }" :aria-pressed="agentRuntimeSelection === 'inherit'" @click="agentRuntimeSelection = 'inherit'">
                      <Workflow :size="16" />
                      <strong>{{ t('settings.runtimeInherit') }}</strong>
                      <span>{{ t('settings.runtimeInheritHint') }}</span>
                    </button>
                    <button :class="{ active: agentRuntimeSelection === 'fixed_model' }" :aria-pressed="agentRuntimeSelection === 'fixed_model'" @click="agentRuntimeSelection = 'fixed_model'">
                      <Cpu :size="16" />
                      <strong>{{ t('settings.runtimeFixedModel') }}</strong>
                      <span>{{ t('settings.runtimeFixedModelHint') }}</span>
                    </button>
                    <button :class="{ active: agentRuntimeSelection === 'provider_auto' }" :aria-pressed="agentRuntimeSelection === 'provider_auto'" @click="agentRuntimeSelection = 'provider_auto'">
                      <Server :size="16" />
                      <strong>{{ t('settings.runtimeProviderAuto') }}</strong>
                      <span>{{ t('settings.runtimeProviderAutoHint') }}</span>
                    </button>
                    <button :class="{ active: agentRuntimeSelection === 'cli' }" :aria-pressed="agentRuntimeSelection === 'cli'" @click="agentRuntimeSelection = 'cli'">
                      <Terminal :size="16" />
                      <strong>CLI</strong>
                      <span>{{ t('settings.runtimeCliHint') }}</span>
                    </button>
                    <button :class="{ active: agentRuntimeSelection === 'acp' }" :aria-pressed="agentRuntimeSelection === 'acp'" @click="agentRuntimeSelection = 'acp'">
                      <Bot :size="16" />
                      <strong>ACP</strong>
                      <span>{{ t('settings.runtimeAcpHint') }}</span>
                    </button>
                  </div>

                  <div v-if="agentRuntimeSelection === 'inherit'" class="runtime-source-current">
                    <ShieldCheck :size="16" />
                    <span>{{ t('settings.runtimeInheritedCurrent', { source: runtimeSourceSummary(configuringRuntimeBinding) || t('settings.runtimeUnresolved') }) }}</span>
                  </div>
                  <div v-else-if="agentRuntimeSelection === 'fixed_model'" class="settings-field runtime-source-picker">
                    <UiLabel>{{ t('settings.runtimeFixedModel') }}</UiLabel>
                    <div class="runtime-source-search">
                      <Search :size="14" />
                      <UiInput v-model="agentRuntimeModelQuery" :placeholder="t('settings.runtimeSearchPlaceholder', { kind: t('settings.centerModels') })" />
                    </div>
                    <select v-model="agentRuntimeModelKey" class="settings-select">
                      <option value="" disabled>{{ t('settings.selectModel') }}</option>
                      <option v-for="model in filteredRuntimeModels" :key="model.id" :value="modelOptionKey(model.provider_instance_id, model.model_id)">
                        {{ model.model_id }} · {{ model.provider_display_name ?? model.provider_instance_id }} · {{ statusLabel(model.status) }}
                      </option>
                    </select>
                    <p v-if="filteredRuntimeModels.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
                  </div>
                  <div v-else-if="agentRuntimeSelection === 'provider_auto'" class="settings-field runtime-source-picker">
                    <UiLabel>{{ t('settings.routeProvider') }}</UiLabel>
                    <div class="runtime-source-search">
                      <Search :size="14" />
                      <UiInput v-model="agentRuntimeProviderQuery" :placeholder="t('settings.runtimeSearchPlaceholder', { kind: t('settings.centerApiConnections') })" />
                    </div>
                    <select v-model="agentRuntimeProviderId" class="settings-select">
                      <option value="" disabled>{{ t('settings.selectProvider') }}</option>
                      <option v-for="provider in filteredRuntimeProviders" :key="provider.provider_instance_id" :value="provider.provider_instance_id">
                        {{ provider.display_name }} · {{ statusLabel(provider.status) }}
                      </option>
                    </select>
                    <p v-if="filteredRuntimeProviders.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
                    <p class="agent-config-hint">{{ t('settings.providerAutoOwnedByCore') }}</p>
                  </div>
                  <div v-else-if="agentRuntimeSelection === 'cli'" class="settings-field runtime-source-picker">
                    <UiLabel>CLI</UiLabel>
                    <div class="runtime-source-search">
                      <Search :size="14" />
                      <UiInput v-model="agentRuntimeCliQuery" :placeholder="t('settings.runtimeSearchPlaceholder', { kind: 'CLI' })" />
                    </div>
                    <select v-model="agentRuntimeCliId" class="settings-select">
                      <option value="" disabled>{{ t('settings.selectCliRuntime') }}</option>
                      <option v-for="runtime in filteredRuntimeCliOptions" :key="runtime.runtime_id" :value="runtime.runtime_id">
                        {{ runtime.display_name }} · {{ statusLabel(runtime.status) }}
                      </option>
                    </select>
                    <p v-if="filteredRuntimeCliOptions.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
                  </div>
                  <div v-else class="settings-field runtime-source-picker">
                    <UiLabel>ACP</UiLabel>
                    <div class="runtime-source-search">
                      <Search :size="14" />
                      <UiInput v-model="agentRuntimeAcpQuery" :placeholder="t('settings.runtimeSearchPlaceholder', { kind: 'ACP' })" />
                    </div>
                    <select v-model="agentRuntimeAcpId" class="settings-select">
                      <option value="" disabled>{{ t('settings.selectAcpRuntime') }}</option>
                      <option v-for="runtime in filteredRuntimeAcpOptions" :key="runtime.runtime_id" :value="runtime.runtime_id">
                        {{ runtime.display_name }} · {{ acpRuntimeSourceLabel(runtime.source) }} · {{ statusLabel(runtime.status) }}
                      </option>
                    </select>
                    <p v-if="filteredRuntimeAcpOptions.length === 0" class="agent-config-hint">{{ t('settings.noRuntimeMatches') }}</p>
                  </div>

                  <div v-if="!runtimeBindingWritable" class="runtime-binding-readonly">
                    <Info :size="16" />
                    <div>
                      <strong>{{ t('settings.runtimeBindingPendingCore') }}</strong>
                      <span>{{ t('settings.runtimeBindingPendingCoreHint') }}</span>
                    </div>
                  </div>
                  <div v-if="agentRuntimeNotice" class="runtime-binding-warning">
                    <Info :size="16" />
                    <span>{{ agentRuntimeNotice }}</span>
                  </div>
                  <div class="modal-actions compact">
                    <UiButton :disabled="agentRuntimeBusy || !runtimeBindingWritable || !runtimeBindingInput()" size="sm" @click="saveAgentRuntimeBinding(configuringAgent)">
                      <Save :size="14" />
                      <span>{{ t('settings.saveRuntimeBinding') }}</span>
                    </UiButton>
                  </div>
                </div>

                <!-- 描述 -->
                <div class="agent-config-section">
                  <div class="agent-config-section-title">{{ t('settings.agentDescription') }}</div>
                  <div class="settings-field">
                    <textarea
                      v-model="agentEditDescription"
                      class="settings-textarea"
                      rows="2"
                      :placeholder="t('settings.agentDescriptionPlaceholder')"
                    ></textarea>
                  </div>
                </div>

                <!-- 工具绑定 -->
                <div class="agent-config-section">
                  <div class="agent-config-section-title">{{ t('settings.agentTools') }}</div>
                  <p class="agent-config-hint">{{ t('settings.agentToolsHint') }}</p>
                  <div class="agent-tool-grid">
                    <button
                      v-for="tool in availableTools"
                      :key="tool.id"
                      class="agent-tool-chip"
                      :class="{
                        active: agentEditTools.includes(tool.id),
                        risky: tool.requires_approval
                      }"
                      @click="toggleAgentTool(tool.id)"
                    >
                      <span class="agent-tool-name">{{ tool.display_name }}</span>
                      <span class="agent-tool-risk">{{ tool.risk }}</span>
                    </button>
                  </div>
                </div>

                <!-- 能力标签 -->
                <div class="agent-config-section">
                  <div class="agent-config-section-title">{{ t('settings.agentCapabilities') }}</div>
                  <p class="agent-config-hint">{{ t('settings.agentCapabilitiesHint') }}</p>
                  <div class="agent-capability-list">
                    <span v-for="cap in agentEditCapabilities" :key="cap" class="agent-cap-tag">
                      {{ cap }}
                      <button class="agent-cap-remove" @click="removeAgentCapability(cap)">×</button>
                    </span>
                  </div>
                  <div class="agent-cap-add-row">
                    <UiInput v-model="agentNewCapability" :placeholder="t('settings.newCapabilityPlaceholder')" @keydown.enter="addAgentCapability" />
                    <UiButton variant="outline" size="sm" :disabled="!agentNewCapability.trim()" @click="addAgentCapability">
                      <Plus :size="14" />
                      {{ t('settings.addCapability') }}
                    </UiButton>
                  </div>
                </div>

                <!-- System Prompt -->
                <div class="agent-config-section">
                  <div class="agent-config-section-title">{{ t('settings.agentSystemPrompt') }}</div>
                  <p class="agent-config-hint">{{ t('settings.agentSystemPromptOverrideHint') }}</p>
                  <div class="settings-field">
                    <textarea
                      v-model="agentEditSystemPrompt"
                      class="settings-textarea prompt-editor"
                      rows="6"
                      :placeholder="t('settings.agentSystemPromptPlaceholder')"
                    ></textarea>
                  </div>
                </div>

                <!-- 保存按钮 -->
                <div class="agent-save-bar">
                  <UiButton :disabled="busy" @click="saveAgentProfile">
                    <Save :size="14" />
                    <span>{{ t('settings.saveAgent') }}</span>
                  </UiButton>
                </div>
              </div>
              <div v-else class="center-empty-state inspector-empty">
                <PanelRight :size="20" />
                <span>{{ agents.length > 0 ? t('settings.pleaseOpenAgentConfig') : t('settings.noAgents') }}</span>
              </div>

              <details class="center-diagnostics-panel agent-candidates-panel">
                <summary>
                  <span>{{ t('settings.evolutionCandidates') }}</span>
                  <UiBadge variant="outline">{{ agentCandidates.length }}</UiBadge>
                </summary>
                <div class="agent-candidate-list compact">
                  <article v-for="candidate in agentCandidates" :key="candidate.id" class="agent-candidate-row">
                    <div>
                      <strong>{{ candidate.name }}</strong>
                      <span>{{ agentLayerLabel(candidate.layer) }} · {{ agentTypeLabel(candidate.agent_type) }} · {{ candidateStatusLabel(candidate.status) }}</span>
                    </div>
                    <UiBadge variant="secondary">{{ t('settings.generatedByEvolution') }}</UiBadge>
                  </article>
                </div>
              </details>
            </aside>
            </div>
            </div>
</template>
