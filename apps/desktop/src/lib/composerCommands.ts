import type { PermissionLevel } from '@/types/mode'
import type { SpaceOptionsDto } from '@/api'
import type { Component } from 'vue'
import { Eye, Shield, ShieldAlert, ShieldCheck, UserCheck, Users } from '@lucide/vue'

export type ComposerPage = 'root' | 'model' | 'model-agents' | 'model-agent' | 'mode' | 'permission' | 'workflow'
/** Pages one level below another; every other page sits right under the command list. */
export const parentPage: Partial<Record<ComposerPage, ComposerPage>> = { 'model-agents': 'model', 'model-agent': 'model-agents' }
export const pageDepth = (page: ComposerPage): number => page === 'root' ? 0 : 1 + (parentPage[page] ? pageDepth(parentPage[page]!) : 0)
export type SpaceToggle = Exclude<keyof SpaceOptionsDto, 'workflow_mode_version_id'>

// Metadata only: session ownership and all mutations remain in HomeController.
// The model row is the model of the agent that answers the user, the same per-agent setting as
// the agent center; the other agents' models sit one page further down.
// Existing run/session commands still come from appCommands and their shared handlers.
// label, hint and keywords are translation keys.
export const composerSettings = [
  { id: 'model', label: 'commandPanel.model', hint: 'commandPanel.modelHint', keywords: 'commandPanel.kw.model', page: 'model' },
  { id: 'mode', label: 'commandPanel.mode', hint: 'commandPanel.modeHint', keywords: 'commandPanel.kw.mode', page: 'mode', surface: 'flat' },
  { id: 'permission', label: 'commandPanel.permission', hint: 'permission.nextRunHint', keywords: 'commandPanel.kw.permission', page: 'permission' },
  { id: 'plan', label: 'commandPanel.plan', hint: 'commandPanel.planHint', keywords: 'commandPanel.kw.plan', toggle: 'plan_first', surface: 'space' },
  { id: 'spec', label: 'commandPanel.spec', hint: 'commandPanel.specHint', keywords: 'commandPanel.kw.spec', toggle: 'spec_enabled', surface: 'space' },
  { id: 'agents', label: 'commandPanel.agents', hint: 'commandPanel.agentsHint', keywords: 'commandPanel.kw.agents', toggle: 'multi_agent', surface: 'space' },
  { id: 'workflow', label: 'commandPanel.workflow', hint: 'commandPanel.workflowHint', keywords: 'commandPanel.kw.workflow', page: 'workflow', surface: 'space' },
  { id: 'bulletin', label: 'commandPanel.bulletin', hint: 'commandPanel.bulletinHint', keywords: 'commandPanel.kw.bulletin', toggle: 'bulletin_board', surface: 'space' },
  { id: 'worktree', label: 'commandPanel.worktree', hint: 'commandPanel.worktreeHint', keywords: 'commandPanel.kw.worktree', toggle: 'worktree', surface: 'space' },
] as const

export type PermissionRisk = 'neutral' | 'low' | 'medium' | 'high'
export const permissionChoices: ReadonlyArray<{ value: PermissionLevel; label: string; hint: string; icon: Component; risk: PermissionRisk }> = [
  { value: 'default', label: 'permission.default', hint: 'permission.defaultHint', icon: Shield, risk: 'neutral' },
  { value: 'auto-approve', label: 'permission.autoApprove', hint: 'permission.autoApproveHint', icon: ShieldCheck, risk: 'medium' },
  { value: 'full-access', label: 'permission.fullAccess', hint: 'permission.fullAccessHint', icon: ShieldAlert, risk: 'high' },
  { value: 'delegate-conversation', label: 'permission.delegateConversation', hint: 'permission.delegateConversationHint', icon: UserCheck, risk: 'medium' },
  { value: 'delegate-reviewer', label: 'permission.delegateReviewer', hint: 'permission.delegateReviewerHint', icon: Eye, risk: 'low' },
  { value: 'delegate-both', label: 'permission.delegateBoth', hint: 'permission.delegateBothHint', icon: Users, risk: 'low' },
]

// Only the first token is a command. Absolute paths and prose containing slashes
// remain ordinary text; a command-like unknown token requires explicit send-as-text.
export function composerSlashQuery(text: string): string | null {
  if (!/^\/[^/\\\r\n\s]*(?:\s|$)/u.test(text)) return null
  return text.slice(1).split(/[\s]/u)[0] ?? ''
}
