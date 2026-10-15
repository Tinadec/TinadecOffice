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
export const composerSettings = [
  { id: 'model', label: 'commandPanel.model', hint: 'commandPanel.modelHint', keywords: 'model agent default 模型 智能体 默认模型', page: 'model' },
  { id: 'mode', label: 'commandPanel.mode', hint: 'commandPanel.modeHint', keywords: 'mode preset plan solo 模式 预设 计划', page: 'mode', surface: 'flat' },
  { id: 'permission', label: 'commandPanel.permission', hint: 'permission.nextRunHint', keywords: 'permission approval 权限 审批', page: 'permission' },
  { id: 'plan', label: 'commandPanel.plan', hint: 'commandPanel.planHint', keywords: 'plan planning 计划 规划', toggle: 'plan_first', surface: 'space' },
  { id: 'spec', label: 'commandPanel.spec', hint: 'commandPanel.specHint', keywords: 'spec specification 规范 需求 设计', toggle: 'spec_enabled', surface: 'space' },
  { id: 'agents', label: 'commandPanel.agents', hint: 'commandPanel.agentsHint', keywords: 'agents team collaboration 多智能体 协作 编排', toggle: 'multi_agent', surface: 'space' },
  { id: 'workflow', label: 'commandPanel.workflow', hint: 'commandPanel.workflowHint', keywords: 'workflow 工作流 流程', page: 'workflow', surface: 'space' },
  { id: 'bulletin', label: 'commandPanel.bulletin', hint: 'commandPanel.bulletinHint', keywords: 'bulletin board 公告板 协作信息', toggle: 'bulletin_board', surface: 'space' },
  { id: 'worktree', label: 'commandPanel.worktree', hint: 'commandPanel.worktreeHint', keywords: 'worktree git isolation 隔离 工作树', toggle: 'worktree', surface: 'space' },
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
