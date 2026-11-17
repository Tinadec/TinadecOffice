import type { AgentViewDto } from '@/api'
import type { TeamStyle } from './teamShape'

export interface TeamTemplateDefinition {
  key: TeamStyle
  labelKey: string
  hintKey: string
  /** i18n key for the collaboration instructions written into the new mode. */
  instructionsKey?: string
  preferredMemberSlugs: string[]
}

export const TEAM_TEMPLATES: readonly TeamTemplateDefinition[] = [
  {
    key: 'team',
    labelKey: 'settings.teamTemplateTeam',
    hintKey: 'settings.teamTemplateTeamHint',
    instructionsKey: 'settings.teamTemplateTeamInstructions',
    preferredMemberSlugs: ['search', 'global_engineering'],
  },
  {
    key: 'pipeline',
    labelKey: 'settings.teamTemplatePipeline',
    hintKey: 'settings.teamTemplatePipelineHint',
    instructionsKey: 'settings.teamTemplatePipelineInstructions',
    preferredMemberSlugs: ['search', 'global_engineering'],
  },
  {
    key: 'solo',
    labelKey: 'settings.teamTemplateSolo',
    hintKey: 'settings.teamTemplateSoloHint',
    instructionsKey: 'settings.teamTemplateSoloInstructions',
    preferredMemberSlugs: [],
  },
]

function slugOf(agent: AgentViewDto): string {
  return agent.slug ?? agent.name ?? agent.id
}

export function pickLeader(operationAgents: AgentViewDto[]): string {
  return operationAgents.find(agent => slugOf(agent) === 'meeting')?.id ?? operationAgents[0]?.id ?? ''
}

export function pickMembers(template: TeamTemplateDefinition, executionAgents: AgentViewDto[]): string[] {
  const bySlug = new Map(executionAgents.map(agent => [slugOf(agent), agent]))
  const picked: string[] = []
  for (const slug of template.preferredMemberSlugs) {
    const agent = bySlug.get(slug)
    if (agent && !picked.includes(agent.id)) picked.push(agent.id)
  }
  if (picked.length === 0 && template.key !== 'solo') {
    picked.push(...executionAgents.slice(0, 2).map(agent => agent.id))
  }
  return picked
}
