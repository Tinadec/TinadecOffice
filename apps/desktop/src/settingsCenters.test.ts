import { describe, expect, it } from 'vitest'
import topologyCanvas from './components/AgentTopologyCanvas.vue?raw'
import zhCn from './locales/zh-CN.ts?raw'
import agentSettings from './pages/settings/agentSettings.ts?raw'
import agentSettingsPage from './pages/settings/AgentsSettingsPage.vue?raw'
import modelSettings from './pages/settings/modelSettings.ts?raw'
import modelSettingsPage from './pages/settings/ModelSettingsPage.vue?raw'
import settingsNavigation from './pages/settings/settingsNavigation.ts?raw'
import toolSettings from './pages/settings/toolSettings.ts?raw'

const settingsAggregate = [modelSettingsPage, agentSettingsPage, modelSettings, agentSettings, toolSettings, settingsNavigation].join('\n')

describe('settings centers presentation contract', () => {
  it('renders the rewritten model workbench contract', () => {
    expect(settingsAggregate).toContain('class="center-page model-center-page"')
    expect(settingsAggregate).toContain('class="center-command-bar"')
    expect(settingsAggregate).toContain('class="center-workbench model-workbench"')
    expect(settingsAggregate).toContain('v-if="!modelCenterError || modelCenterOverview"')
    expect(settingsAggregate).toContain('class="center-resource-rail"')
    expect(settingsAggregate).toContain('class="center-inspector"')
    expect(settingsAggregate).toContain("modelCenterOverview?.capabilities.provider_crud")
    expect(settingsAggregate).toContain("modelCenterOverview?.capabilities.model_catalog_mode")
    expect(settingsAggregate).toContain("modelCenterOverview?.capabilities.live_model_discovery")
    expect(settingsAggregate).toContain('v-html="providerPresentation(supplier.driver)?.icon"')
    expect(settingsAggregate).toContain('v-html="providerPresentation(selectedProviderDetail.driver)?.icon"')
    expect(settingsAggregate).toContain('v-html="row.template?.icon"')
    expect(settingsAggregate).toContain('<RefreshCw')
    expect(settingsAggregate).toContain('supplierSummary(supplier)')
    expect(settingsAggregate).toContain('class="center-resource-rail model-resource-navigation"')
    expect(settingsAggregate).toContain('class="center-resource-grid supplier-grid supplier-list"')
    expect(settingsAggregate).toContain('?? providers.value[0] ?? null')
  })

  it('preserves the agent inspector and runtime preview contract', () => {
    expect(settingsAggregate).toContain('class="center-page agent-center-page"')
    expect(settingsAggregate).toContain('class="center-workbench agent-workbench"')
    expect(settingsAggregate).toContain('class="center-inspector agent-inspector"')
    expect(settingsAggregate).toContain("t('settings.agentProfilesWritable')")
    expect(settingsAggregate).toContain("t('settings.runtimePreviewOnly')")
    expect(settingsAggregate).toContain('v-if="!agentCenterLoading && !agentCenterError && agents.length === 0"')
    expect(settingsAggregate).toContain('v-if="!agentCenterError || agentCenterOverview"')
    expect(settingsAggregate).toContain(':disabled="agentRuntimeBusy || !runtimeBindingWritable || !runtimeBindingInput()"')
    expect(settingsAggregate).toContain('<PanelRight')
    expect(settingsAggregate).toContain("const agentViewMode = ref<'topology' | 'list'>('list')")
    expect(settingsAggregate).toContain(':class="`view-${agentViewMode}`"')
    expect(settingsAggregate).toContain('@click="openAgentConfig(agent)"')
    expect(settingsAggregate).toContain('openAgentConfig(activeAgent)')
    expect(settingsAggregate).not.toContain('class="git-manager-rail-card"')
    expect(settingsAggregate).toContain('@click="closeAgentConfig"')
    expect(settingsAggregate).toContain("selectedAgentId.value = ''")
    expect(settingsAggregate).toContain("configuringAgentId.value = ''")
    expect(settingsAggregate).toContain("t('settings.pleaseOpenAgentConfig')")
    expect(zhCn).toContain("pleaseOpenAgentConfig: '请打开智能体配置'")
  })

  it('keeps center states localized, compact, and accessible', () => {
    expect(settingsAggregate).toContain('class="center-loading-state"')
    expect(settingsAggregate).toContain('role="tablist"')
    expect(settingsAggregate).toContain(':aria-pressed="agentRuntimeSelection ===')
    expect(settingsAggregate).toContain("t('settings.centerOverview')")
    expect(settingsAggregate).toContain("t('settings.centerResources')")
    expect(settingsAggregate).toContain("t('settings.centerInspector')")
    expect(settingsAggregate).toContain("t('settings.centerDiagnostics')")
    expect(settingsAggregate).toContain('settings.agentEvolution')
    expect(settingsAggregate).toContain('settings.promptContext')
    expect(settingsAggregate).toContain('settings.promptEngineering')
  })

  it('keeps the topology readable and the inspector beside the work surface', () => {
    expect(topologyCanvas).not.toContain('<canvas')
    expect(topologyCanvas).toContain('class="agent-topology-layer planning"')
    expect(settingsAggregate).toContain(':agent-labels="topologyAgentLabels"')
  })
})
