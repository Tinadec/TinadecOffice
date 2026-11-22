import type { AgentDirectoryItemDto, AgentViewDto, ModelProviderInstanceDto } from '@/api'
import { pickLocalized } from '@/lib/packI18n'
import { asChannel, channelForProtocol } from '@/runtimeCenterView'

export type AgentDirectoryViewMode = 'topology' | 'grouped' | 'list'
export type RuntimeChannelKind = 'acp' | 'cli' | 'tui' | 'http' | 'api'

export function providerRuntimeChannel(
  provider?: Pick<ModelProviderInstanceDto, 'protocol' | 'channel' | 'connection_kind' | 'capabilities' | 'driver'> | null,
): RuntimeChannelKind {
  if (!provider) return 'api'
  const byProto = channelForProtocol(provider.protocol) ?? asChannel(provider.channel)
  if (byProto) return byProto
  const caps = new Set((provider.capabilities ?? []).map((c) => c.toLowerCase()))
  if (caps.has('acp')) return 'acp'
  if (caps.has('tui')) return 'tui'
  const conn = (provider.connection_kind ?? '').trim().toLowerCase()
  if (conn === 'cli' || caps.has('cli')) return 'cli'
  if (
    conn === 'local-server'
    || conn === 'local_http'
    || conn === 'local-http'
    || (provider.protocol ?? '').trim().toLowerCase() === 'opencode-serve'
    || (provider.driver ?? '').trim().toLowerCase().startsWith('local-http')
  ) {
    return 'http'
  }
  return 'api'
}

export function isExternalHarnessChannel(channel: RuntimeChannelKind): boolean {
  return channel === 'acp' || channel === 'cli' || channel === 'tui'
}

export function runtimeChannelBadgeLabel(channel: RuntimeChannelKind): string {
  return channel.toUpperCase()
}

export function normalizeAgentLayer(layer: unknown): 'operation' | 'execution' {
  const v = String(layer ?? '').trim().toLowerCase()
  if (v === 'planning' || v === 'operation') return 'operation'
  return v === 'execution' ? 'execution' : 'operation'
}

export function agentDisplayName(
  agent: AgentViewDto,
  directoryItem: AgentDirectoryItemDto | undefined,
  locale: string,
): string {
  const baseline = agent.display_name || agent.name || agent.slug || agent.id
  return pickLocalized(agent.i18n ?? directoryItem?.i18n, locale, 'display_name', baseline) ?? baseline
}

export function agentDisplayDescription(
  agent: AgentViewDto,
  directoryItem: AgentDirectoryItemDto | undefined,
  locale: string,
): string {
  const baseline = agent.description ?? ''
  return pickLocalized(agent.i18n ?? directoryItem?.i18n, locale, 'description', baseline) ?? baseline
}

export function agentSourceKindFor(
  agentId: string,
  directoryById: Map<string, AgentDirectoryItemDto>,
  agents: AgentViewDto[],
): string {
  const item = directoryById.get(agentId)
  if (item?.source_kind) return item.source_kind
  return agents.find((a) => a.id === agentId)?.source_kind ?? 'custom'
}

export function isAgentReadOnly(
  agent: AgentViewDto | null,
  directoryById: Map<string, AgentDirectoryItemDto>,
  agents: AgentViewDto[],
): boolean {
  if (!agent) return true
  if (agent.is_built_in) return true
  const sourceKind = agentSourceKindFor(agent.id, directoryById, agents)
  return sourceKind === 'pack' || sourceKind === 'built_in'
}

export function sourceBadgeVariant(kind: string): 'default' | 'secondary' | 'outline' {
  if (kind === 'agent_generated') return 'default'
  if (kind === 'custom') return 'secondary'
  return 'outline'
}

export function agentSourceKindLabelKey(kind: string): string | null {
  if (kind === 'custom') return 'settings.agentSourceCustom'
  if (kind === 'pack') return 'settings.agentSourcePack'
  if (kind === 'agent_generated') return 'settings.agentSourceAgentGenerated'
  if (kind === 'bootstrap') return 'settings.agentSourceBootstrap'
  if (kind === 'missing_reference') return 'settings.agentSourceMissing'
  return null
}

const AGENT_TYPE_LABEL_KEYS: Record<string, string> = {
  session_coordinator: 'settings.agentTypeSessionCoordinator',
  task_executor: 'settings.agentTypeTaskExecutor',
  governance_reviewer: 'settings.agentTypeGovernanceReviewer',
  context_maintenance: 'settings.agentTypeContextCompressor',
  exploration_planner: 'settings.agentTypeExplorationPlanner',
  solo_master: 'settings.agentTypeSoloMaster',
  search: 'settings.agentTypeSearchSpecialist',
  global_engineering: 'settings.agentTypeCodeWriter',
  artex_director: 'settings.agentTypeArtexDirector',
  artex_planner: 'settings.agentTypeExplorationPlanner',
  artex_worker: 'settings.agentTypeTaskExecutor',
  artex_verifier: 'settings.agentTypeReviewExecutor',
  meeting: 'settings.agentTypeMeeting',
  'context-compressor': 'settings.agentTypeContextCompressor',
  'prompt-context-engineer': 'settings.agentTypePromptContextEngineer',
  evolver: 'settings.agentTypeEvolver',
  'tool-assistant': 'settings.agentTypeToolAssistant',
  supervisor: 'settings.agentTypeSupervisor',
  'skill-learner': 'settings.agentTypeSkillLearner',
  'task-planner': 'settings.agentTypeTaskPlanner',
  'test-multimodal': 'settings.agentTypeTestMultimodal',
  'code-explorer': 'settings.agentTypeCodeExplorer',
  'search-specialist': 'settings.agentTypeSearchSpecialist',
  'file-finder': 'settings.agentTypeFileFinder',
  'git-manager': 'settings.agentTypeGitManager',
  'code-writer': 'settings.agentTypeCodeWriter',
  designer: 'settings.agentTypeDesigner',
  'review-executor': 'settings.agentTypeReviewExecutor',
  'tool-packager': 'settings.agentTypeToolPackager',
  chair: 'settings.agentTypeMeeting',
  planner: 'settings.agentTypeTaskPlanner',
  'tool-manager': 'settings.agentTypeToolAssistant',
  'evolution-algorithm': 'settings.agentTypeEvolver',
  executor: 'settings.agentTypeCodeWriter',
  reviewer: 'settings.agentTypeSupervisor',
}

export function agentTypeLabelKey(type: string): string | null {
  return AGENT_TYPE_LABEL_KEYS[type] ?? null
}

export function agentLayerLabelKey(layer: string): string | null {
  if (layer === 'operation' || layer === 'planning') return 'settings.agentLayerPlanning'
  if (layer === 'execution') return 'settings.agentLayerExecution'
  if (layer === 'evolution') return 'settings.agentLayerEvolution'
  return null
}

export function agentAvatarKey(agent: Pick<AgentViewDto, 'id' | 'slug' | 'name'>): string {
  const slug = agent.slug?.trim()
  if (slug) return slug
  const name = agent.name?.trim()
  return name || agent.id
}
