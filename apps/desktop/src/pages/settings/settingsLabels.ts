import { useI18n } from 'vue-i18n'
import type { AgentModeDto, CenterDiagnosticDto, ModelCenterSupplierDto } from '@/api'
import { findTemplate } from '@/providerTemplates'

export function useSettingsLabels() {
  const { t } = useI18n()

  function settingsErrorLabel(error: unknown) {
    const message = error instanceof Error ? error.message : typeof error === 'string' ? error : ''
    return /bad gateway|\b502\b/i.test(message) ? t('settings.backendUnavailable') : message || t('settings.centerLoadFailed')
  }

  function centerDiagnosticLabel(diagnostic: CenterDiagnosticDto) {
    if (diagnostic.code === 'CORE_CAPABILITY_UNAVAILABLE') {
      return t('settings.optionalCapabilityUnavailable', {
        source: diagnostic.source ?? 'Core',
        status: diagnostic.status ?? '-',
      })
    }
    if (diagnostic.code === 'LEGACY_SHARED_ROUTE') {
      return t('settings.sharedRouteDiagnostic', {
        purpose: diagnostic.route_purpose ?? '-',
        count: diagnostic.agent_ids?.length ?? 0,
      })
    }
    return diagnostic.message
  }

  function configuredModelSourceLabel(source: string) {
    if (source === 'provider_default') return t('settings.modelSourceProviderDefault')
    if (source === 'route_override') return t('settings.modelSourceRouteOverride')
    return source
  }

  function connectionKindLabel(kind: string) {
    if (kind === 'cli') return t('settings.connectionKindCli')
    if (kind === 'local-server') return t('settings.connectionKindLocal')
    if (kind === 'public-api') return t('settings.connectionKindPublicApi')
    return t('settings.connectionKindApiKey')
  }

  function acpRuntimeSourceLabel(source: string) {
    return source === 'legacy_provider' ? t('settings.legacyProvider') : t('settings.acpAdapter')
  }

  function modelCatalogModeLabel(mode?: string) {
    return mode === 'configured_only' ? t('settings.configuredOnly') : mode ?? t('settings.configuredOnly')
  }

  function agentTypeLabel(type: string) {
    const map: Record<string, string> = {
      meeting: t('settings.agentTypeMeeting'),
      'context-compressor': t('settings.agentTypeContextCompressor'),
      'prompt-context-engineer': t('settings.agentTypePromptContextEngineer'),
      evolver: t('settings.agentTypeEvolver'),
      'tool-assistant': t('settings.agentTypeToolAssistant'),
      supervisor: t('settings.agentTypeSupervisor'),
      'skill-learner': t('settings.agentTypeSkillLearner'),
      'task-planner': t('settings.agentTypeTaskPlanner'),
      'test-multimodal': t('settings.agentTypeTestMultimodal'),
      'code-explorer': t('settings.agentTypeCodeExplorer'),
      'search-specialist': t('settings.agentTypeSearchSpecialist'),
      'file-finder': t('settings.agentTypeFileFinder'),
      'git-manager': t('settings.agentTypeGitManager'),
      'code-writer': t('settings.agentTypeCodeWriter'),
      designer: t('settings.agentTypeDesigner'),
      'review-executor': t('settings.agentTypeReviewExecutor'),
      'tool-packager': t('settings.agentTypeToolPackager'),
      chair: t('settings.agentTypeMeeting'),
      planner: t('settings.agentTypeTaskPlanner'),
      'tool-manager': t('settings.agentTypeToolAssistant'),
      'evolution-algorithm': t('settings.agentTypeEvolver'),
      executor: t('settings.agentTypeCodeWriter'),
      reviewer: t('settings.agentTypeSupervisor'),
    }
    return map[type] ?? type
  }

  function agentLayerLabel(layer: string) {
    const map: Record<string, string> = {
      planning: t('settings.agentLayerPlanning'),
      execution: t('settings.agentLayerExecution'),
      evolution: t('settings.agentLayerEvolution'),
    }
    return map[layer] ?? layer
  }

  function agentModeLabel(mode: string) {
    const map: Record<string, string> = {
      balanced: t('settings.agentModeBalanced'),
      'plan-first': t('settings.agentModePlanFirst'),
      parallel: t('settings.agentModeParallel'),
      'safe-research': t('settings.agentModeSafeResearch'),
      chat: t('settings.agentModeChat'),
      plan: t('settings.agentModePlan'),
      execute: t('settings.agentModeExecute'),
      review: t('settings.agentModeReview'),
    }
    return map[mode] ?? mode
  }

  function agentModeSummary(mode: AgentModeDto) {
    const map: Record<string, string> = {
      balanced: t('settings.agentModeBalancedHint'),
      'plan-first': t('settings.agentModePlanFirstHint'),
      parallel: t('settings.agentModeParallelHint'),
      'safe-research': t('settings.agentModeSafeResearchHint'),
    }
    return map[mode.id] ?? mode.summary
  }

  function agentPolicyLabel(policy: string) {
    const map: Record<string, string> = {
      balanced: t('settings.policyBalanced'),
      strict: t('settings.policyStrict'),
      performance: t('settings.policyPerformance'),
    }
    return map[policy] ?? policy
  }

  function supplierTransportLabel(kind: string) {
    const map: Record<string, string> = {
      http_json: t('settings.transportCloudApi'),
      local_http: t('settings.transportLocalService'),
      cli: 'CLI',
      acp: 'ACP',
    }
    return map[kind] ?? kind
  }

  function supplierCredentialLabel(kind: string) {
    const map: Record<string, string> = {
      api_key: t('settings.apiKey'),
      'api-key': t('settings.apiKey'),
      cli: t('settings.localCredential'),
      none: t('settings.noCredential'),
    }
    return map[kind] ?? kind
  }

  function supplierSummary(supplier: ModelCenterSupplierDto) {
    const template = findTemplate(supplier.driver)
    if (template) return t(template.summary_key)
    if (supplier.transport_kind === 'local_http') return t('settings.supplierLocalSummary')
    if (supplier.transport_kind === 'cli') return t('settings.supplierCliSummary')
    if (supplier.transport_kind === 'acp') return t('settings.supplierAcpSummary')
    return t('settings.supplierCloudSummary')
  }

  function candidateStatusLabel(status: string) {
    return status === 'proposed' ? t('settings.candidateProposed') : status
  }

  function statusLabel(status: string) {
    if (status === 'ready') return t('settings.statusReady')
    if (status === 'needs_key') return t('settings.statusNeedsKey')
    if (status === 'disabled') return t('settings.statusDisabled')
    if (status === 'cooldown') return t('settings.statusCooldown')
    if (status === 'not_configured' || !status) return t('settings.statusNotConfigured')
    return status
  }

  function statusVariant(status: string): 'default' | 'secondary' | 'destructive' | 'outline' {
    if (status === 'ready') return 'default'
    if (status === 'needs_key' || status === 'not_configured') return 'destructive'
    if (status === 'disabled') return 'secondary'
    return 'outline'
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

  return { acpRuntimeSourceLabel, agentLayerLabel, agentModeLabel, agentModeSummary, agentPolicyLabel, agentTypeLabel, candidateStatusLabel, centerDiagnosticLabel, configuredModelSourceLabel, connectionKindLabel, modelCatalogModeLabel, readinessStatusLabel, readinessVariant, settingsErrorLabel, statusLabel, statusVariant, supplierCredentialLabel, supplierSummary, supplierTransportLabel, t }
}
