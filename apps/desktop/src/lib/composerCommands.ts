import type { PermissionLevel } from '@/types/mode'
import type { SpaceOptionsDto } from '@/api'
import type { Component } from 'vue'
import { Eye, Shield, ShieldAlert, ShieldCheck, UserCheck, Users } from '@lucide/vue'

export type ComposerPage = 'root' | 'model' | 'model-agents' | 'model-agent' | 'mode' | 'permission' | 'context-strategy' | 'workflow'
/** Pages one level below another; every other page sits right under the command list. */
export const parentPage: Partial<Record<ComposerPage, ComposerPage>> = { 'model-agents': 'model', 'model-agent': 'model-agents' }
export const pageDepth = (page: ComposerPage): number => page === 'root' ? 0 : 1 + (parentPage[page] ? pageDepth(parentPage[page]!) : 0)
export type SpaceToggle = Exclude<keyof SpaceOptionsDto, 'workflow_mode_version_id' | 'context_strategy' | 'context_advanced'>

export interface ComposerSettingEntry {
  readonly id: string
  readonly label: string
  readonly hint: string
  readonly keywords: string
  readonly page?: ComposerPage
  readonly toggle?: SpaceToggle
  readonly surface?: 'space'
}

// Metadata only: session ownership and all mutations remain in HomeController.
// The model row is the model of the agent that answers the user, the same per-agent setting as
// the agent center; the other agents' models sit one page further down.
// Existing run/session commands still come from appCommands and their shared handlers.
// label, hint and keywords are translation keys.
export const composerSettings = [
  { id: 'model', label: 'commandPanel.model', hint: 'commandPanel.modelHint', keywords: 'commandPanel.kw.model', page: 'model' },
  { id: 'mode', label: 'commandPanel.mode', hint: 'commandPanel.modeHint', keywords: 'commandPanel.kw.mode', page: 'mode' },
  { id: 'permission', label: 'commandPanel.permission', hint: 'permission.nextRunHint', keywords: 'commandPanel.kw.permission', page: 'permission' },
  { id: 'context-strategy', label: 'commandPanel.contextStrategy', hint: 'commandPanel.contextStrategyHint', keywords: 'commandPanel.kw.contextStrategy', page: 'context-strategy' },
  { id: 'plan', label: 'commandPanel.plan', hint: 'commandPanel.planHint', keywords: 'commandPanel.kw.plan', toggle: 'plan_first' },
  { id: 'spec', label: 'commandPanel.spec', hint: 'commandPanel.specHint', keywords: 'commandPanel.kw.spec', toggle: 'spec_enabled' },
  { id: 'agents', label: 'commandPanel.agents', hint: 'commandPanel.agentsHint', keywords: 'commandPanel.kw.agents', toggle: 'multi_agent' },
  { id: 'workflow', label: 'commandPanel.workflow', hint: 'commandPanel.workflowHint', keywords: 'commandPanel.kw.workflow', page: 'workflow', surface: 'space' },
  { id: 'bulletin', label: 'commandPanel.bulletin', hint: 'commandPanel.bulletinHint', keywords: 'commandPanel.kw.bulletin', toggle: 'bulletin_board' },
  { id: 'worktree', label: 'commandPanel.worktree', hint: 'commandPanel.worktreeHint', keywords: 'commandPanel.kw.worktree', toggle: 'worktree' },
] as const satisfies readonly ComposerSettingEntry[]

export type PermissionRisk = 'neutral' | 'low' | 'medium' | 'high'
export type PermissionGroup = 'standard' | 'delegated'

export interface PermissionChoice {
  readonly value: PermissionLevel
  readonly label: string
  readonly hint: string
  readonly icon: Component
  readonly risk: PermissionRisk
  readonly group: PermissionGroup
}

export const permissionChoices: ReadonlyArray<PermissionChoice> = [
  { value: 'default', label: 'permission.default', hint: 'permission.defaultHint', icon: Shield, risk: 'neutral', group: 'standard' },
  { value: 'auto-approve', label: 'permission.autoApprove', hint: 'permission.autoApproveHint', icon: ShieldCheck, risk: 'medium', group: 'standard' },
  { value: 'full-access', label: 'permission.fullAccess', hint: 'permission.fullAccessHint', icon: ShieldAlert, risk: 'high', group: 'standard' },
  { value: 'delegate-conversation', label: 'permission.delegateConversation', hint: 'permission.delegateConversationHint', icon: UserCheck, risk: 'medium', group: 'delegated' },
  { value: 'delegate-reviewer', label: 'permission.delegateReviewer', hint: 'permission.delegateReviewerHint', icon: Eye, risk: 'low', group: 'delegated' },
  { value: 'delegate-both', label: 'permission.delegateBoth', hint: 'permission.delegateBothHint', icon: Users, risk: 'low', group: 'delegated' },
]

// Only the first token is a command. Absolute paths and prose containing slashes
// remain ordinary text; a command-like unknown token requires explicit send-as-text.
export function composerSlashQuery(text: string): string | null {
  if (!/^\/[^/\\\r\n\s]*(?:\s|$)/u.test(text)) return null
  return text.slice(1).split(/[\s]/u)[0] ?? ''
}
