import { describe, expect, it } from 'vitest'
import topologyCanvas from './components/AgentTopologyCanvas.vue?raw'
import zhCn from './locales/zh-CN.ts?raw'
import settingsPage from './pages/SettingsPage.vue?raw'

describe('settings centers presentation contract', () => {
  it('renders the rewritten model workbench contract', () => {
    expect(settingsPage).toContain('class="center-page model-center-page"')
    expect(settingsPage).toContain('class="center-command-bar"')
    expect(settingsPage).toContain('class="center-workbench model-workbench"')
    expect(settingsPage).toContain('class="center-resource-rail"')
    expect(settingsPage).toContain('class="center-inspector"')
    expect(settingsPage).toContain("modelCenterOverview?.capabilities.provider_crud")
    expect(settingsPage).toContain("modelCenterOverview?.capabilities.model_catalog_mode")
    expect(settingsPage).toContain("modelCenterOverview?.capabilities.live_model_discovery")
    expect(settingsPage).toContain('v-html="providerPresentation(supplier.driver)?.icon"')
    expect(settingsPage).toContain('v-html="providerPresentation(selectedProviderDetail.driver)?.icon"')
    expect(settingsPage).toContain('v-html="row.template?.icon"')
    expect(settingsPage).toContain('v-html="currentTemplate?.icon"')
    expect(settingsPage).toContain('<RefreshCw')
    expect(settingsPage).toContain('supplierSummary(supplier)')
    expect(settingsPage).toContain('class="center-resource-rail model-resource-navigation"')
    expect(settingsPage).toContain('class="center-resource-grid supplier-grid supplier-list"')
    expect(settingsPage).toContain('?? providers.value[0] ?? null')
  })

  it('preserves the agent inspector and runtime preview contract', () => {
    expect(settingsPage).toContain('class="center-page agent-center-page"')
    expect(settingsPage).toContain('class="center-workbench agent-workbench"')
    expect(settingsPage).toContain('class="center-inspector agent-inspector"')
    expect(settingsPage).toContain("t('settings.agentProfilesWritable')")
    expect(settingsPage).toContain("t('settings.runtimePreviewOnly')")
    expect(settingsPage).toContain('v-if="!agentCenterLoading && agents.length === 0"')
    expect(settingsPage).toContain(':disabled="agentRuntimeBusy || !runtimeBindingWritable || !runtimeBindingInput()"')
    expect(settingsPage).toContain('<PanelRight')
    expect(settingsPage).toContain("const agentViewMode = ref<'topology' | 'list'>('list')")
    expect(settingsPage).toContain(':class="`view-${agentViewMode}`"')
    expect(settingsPage).toContain('@click="openAgentConfig(agent)"')
    expect(settingsPage).toContain('openAgentConfig(activeAgent)')
    expect(settingsPage).not.toContain('class="git-manager-rail-card"')
    expect(settingsPage).toContain('@click="closeAgentConfig"')
    expect(settingsPage).toContain("selectedAgentId.value = ''")
    expect(settingsPage).toContain("configuringAgentId.value = ''")
    expect(settingsPage).toContain("t('settings.pleaseOpenAgentConfig')")
    expect(zhCn).toContain("pleaseOpenAgentConfig: '请打开智能体配置'")
  })

  it('keeps center states localized, compact, and accessible', () => {
    expect(settingsPage).toContain('class="center-loading-state"')
    expect(settingsPage).toContain('role="tablist"')
    expect(settingsPage).toContain(':aria-pressed="agentRuntimeSelection ===')
    expect(settingsPage).toContain("t('settings.centerOverview')")
    expect(settingsPage).toContain("t('settings.centerResources')")
    expect(settingsPage).toContain("t('settings.centerInspector')")
    expect(settingsPage).toContain("t('settings.centerDiagnostics')")
    expect(settingsPage).toContain("label: t('settings.agentEvolution')")
    expect(settingsPage).toContain("label: t('settings.promptContext')")
    expect(settingsPage).toContain("label: t('settings.promptEngineering')")
  })

  it('keeps the topology readable and the inspector beside the work surface', () => {
    expect(topologyCanvas).not.toContain('<canvas')
    expect(topologyCanvas).toContain('class="agent-topology-layer planning"')
    expect(settingsPage).toContain(':agent-labels="topologyAgentLabels"')
  })
})
