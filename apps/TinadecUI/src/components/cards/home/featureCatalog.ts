import {
  Activity,
  Bot,
  GitBranch,
  Globe,
  Layers3,
  ShieldCheck,
  Stethoscope,
  TerminalSquare,
  Users,
  type LucideIcon,
} from '@lucide/vue'

// ---------------------------------------------------------------------------
// featureCatalog — the shared catalog of feature-panel pages.
//
// Single source of truth for the nine openable feature pages of the feature
// panel (right column). Consumed by:
//   - HomePickerCard.vue — the Home grid of feature cards,
//   - BrowserTabBar.vue — the "+" tab dropdown menu that lists every page,
//     like a browser's new-tab menu.
//
// A feature is identified by its UIE card descriptor id. The icon/color are
// presentation metadata; the actual opening behavior lives in the layout
// engine's openCard command (via descriptor id).
// ---------------------------------------------------------------------------

export interface FeatureCatalogEntry {
  /** UIE card descriptor id (registry key, e.g. 'git', 'browser'). */
  descriptorId: string
  titleKey: string
  descKey: string
  icon: LucideIcon
  /** Accent color used for the icon tile / hover border. */
  color: string
  /** Optional pending-count badge (set by consumers, e.g. the approval card). */
  badge?: () => number
}

export const FEATURE_CATALOG: FeatureCatalogEntry[] = [
  { descriptorId: 'agent', titleKey: 'context.homeAgent', descKey: 'context.homeAgentDesc', icon: Bot, color: '#58a6ff' },
  { descriptorId: 'terminal', titleKey: 'context.homeTerminal', descKey: 'context.homeTerminalDesc', icon: TerminalSquare, color: '#3fb950' },
  { descriptorId: 'git', titleKey: 'context.homeGit', descKey: 'context.homeGitDesc', icon: GitBranch, color: '#f1502f' },
  { descriptorId: 'approval', titleKey: 'context.homeApproval', descKey: 'context.homeApprovalDesc', icon: ShieldCheck, color: '#d29922' },
  { descriptorId: 'orchestration', titleKey: 'context.homeOrchestration', descKey: 'context.homeOrchestrationDesc', icon: Layers3, color: '#a371f7' },
  { descriptorId: 'organization', titleKey: 'context.homeOrganization', descKey: 'context.homeOrganizationDesc', icon: Users, color: '#db61a2' },
  { descriptorId: 'browser', titleKey: 'context.homePreview', descKey: 'context.homePreviewDesc', icon: Globe, color: '#58a6ff' },
  { descriptorId: 'events', titleKey: 'context.homeEvents', descKey: 'context.homeEventsDesc', icon: Activity, color: '#7d8590' },
  { descriptorId: 'doctor', titleKey: 'context.homeDoctor', descKey: 'context.homeDoctorDesc', icon: Stethoscope, color: '#3fb950' },
]

const FEATURE_ICON_BY_DESCRIPTOR = new Map(FEATURE_CATALOG.map((f) => [f.descriptorId, f.icon]))

/** The catalog icon of a feature descriptor, or `fallback` for non-feature cards. */
export function featureIconFor(descriptorId: string, fallback: LucideIcon): LucideIcon {
  return FEATURE_ICON_BY_DESCRIPTOR.get(descriptorId) ?? fallback
}
