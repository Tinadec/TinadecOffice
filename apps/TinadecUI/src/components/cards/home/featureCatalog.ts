import {
  Activity,
  Bot,
  FileCode,
  FileText,
  FolderTree,
  GitBranch,
  Globe,
  Layers3,
  MessagesSquare,
  PlugZap,
  ShieldCheck,
  Stethoscope,
  TerminalSquare,
  Users,
  type LucideIcon,
} from '@lucide/vue'
import type { UiePageId } from '../../../engine/types'

// ---------------------------------------------------------------------------
// featureCatalog — the shared catalog of feature-panel pages.
//
// Single source of truth for every openable feature page of the feature
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
  /**
   * UIE pages this entry belongs to. Defaults to `['home']`: a feature added to the
   * workspace grid must not appear on an observing page, and vice versa.
   */
  pages?: readonly UiePageId[]
  /** Optional pending-count badge (set by consumers, e.g. the approval card). */
  badge?: () => number
}

export const FEATURE_CATALOG: FeatureCatalogEntry[] = [
  { descriptorId: 'agent', titleKey: 'context.homeAgent', descKey: 'context.homeAgentDesc', icon: Bot, color: '#58a6ff' },
  { descriptorId: 'terminal', titleKey: 'context.homeTerminal', descKey: 'context.homeTerminalDesc', icon: TerminalSquare, color: '#3fb950' },
  // Documents are two entries: the viewer scans the workspace and shows its documents as a
  // shelf, the editor opens a folder tree and monaco. Both are usable with no prior context,
  // so both are tiles — a viewer you could only reach through a chat reference was a dead
  // end for anyone who just wanted to read what is in the workspace.
  { descriptorId: 'docReader', titleKey: 'context.homeDocReader', descKey: 'context.homeDocReaderDesc', icon: FileText, color: '#746294' },
  { descriptorId: 'codeEditor', titleKey: 'context.homeCodeEditor', descKey: 'context.homeCodeEditorDesc', icon: FileCode, color: '#746294' },
  { descriptorId: 'git', titleKey: 'context.homeGit', descKey: 'context.homeGitDesc', icon: GitBranch, color: '#f1502f' },
  { descriptorId: 'approval', titleKey: 'context.homeApproval', descKey: 'context.homeApprovalDesc', icon: ShieldCheck, color: '#d29922' },
  { descriptorId: 'orchestration', titleKey: 'context.homeOrchestration', descKey: 'context.homeOrchestrationDesc', icon: Layers3, color: '#a371f7' },
  { descriptorId: 'organization', titleKey: 'context.homeOrganization', descKey: 'context.homeOrganizationDesc', icon: Users, color: '#db61a2' },
  { descriptorId: 'browser', titleKey: 'context.homePreview', descKey: 'context.homePreviewDesc', icon: Globe, color: '#58a6ff' },
  { descriptorId: 'events', titleKey: 'context.homeEvents', descKey: 'context.homeEventsDesc', icon: Activity, color: '#7d8590' },
  { descriptorId: 'doctor', titleKey: 'context.homeDoctor', descKey: 'context.homeDoctorDesc', icon: Stethoscope, color: '#3fb950' },
  // The workspace card is the second — and only visual — entry point to the source-folder set.
  // It is appended rather than placed next to the document cards because it is the one tile
  // that changes what the agent is authorized on, not another way to read the workspace.
  { descriptorId: 'workspace', titleKey: 'context.homeWorkspace', descKey: 'context.homeWorkspaceDesc', icon: FolderTree, color: '#d29922' },
  // Companion-center cards. They belong to the observing page only: the Home grid is the
  // workspace, and a companion tile there would suggest a second workspace entry.
  { descriptorId: 'companionList', titleKey: 'context.companionList', descKey: 'context.companionListDesc', icon: Users, color: '#db61a2', pages: ['companionCenter'] },
  { descriptorId: 'companionFeed', titleKey: 'context.companionFeed', descKey: 'context.companionFeedDesc', icon: Activity, color: '#58a6ff', pages: ['companionCenter'] },
  { descriptorId: 'companionGroups', titleKey: 'context.companionGroups', descKey: 'context.companionGroupsDesc', icon: MessagesSquare, color: '#a371f7', pages: ['companionCenter'] },
  { descriptorId: 'companionConnections', titleKey: 'context.companionConnections', descKey: 'context.companionConnectionsDesc', icon: PlugZap, color: '#3fb950', pages: ['companionCenter'] },
]

/**
 * The entries a given UIE page may show. An entry without `pages` belongs to Home only;
 * a page filter that silently listed everything would put the partner cards on Home.
 */
export function catalogEntriesForPage(pageId: UiePageId): FeatureCatalogEntry[] {
  return FEATURE_CATALOG.filter((entry) => (entry.pages ?? ['home']).includes(pageId))
}

/**
 * Every descriptor that renders as a tab must be listed above: `featureIconFor` falls back
 * to a Globe for anything unknown, which is how a row of document tabs ended up looking
 * identical during the sidebar reader review. Adding a card means adding a catalog entry.
 */
const FEATURE_ICON_BY_DESCRIPTOR = new Map<string, LucideIcon>(
  FEATURE_CATALOG.map((f) => [f.descriptorId, f.icon]),
)

/** The catalog icon of a feature descriptor, or `fallback` for non-feature cards. */
export function featureIconFor(descriptorId: string, fallback: LucideIcon): LucideIcon {
  return FEATURE_ICON_BY_DESCRIPTOR.get(descriptorId) ?? fallback
}
