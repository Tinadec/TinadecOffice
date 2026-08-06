import type {
  PersistedCardInstance,
  WorkbenchColumn,
  WorkbenchLayoutSnapshot,
  WorkbenchPageId,
  WorkbenchSlotId,
  WorkbenchStack,
  WorkbenchStackId,
} from './types'
import { createEmptySnapshot } from './reducer'

// ---------------------------------------------------------------------------
// Built-in layout presets.
//
// Each page has a default preset. Home deliberately mirrors the pre-refactor
// geometry: left 260 / center auto / right 420, 8px gaps, floating panels with
// top insets 8 (left/center) and 48 (right, clears the window chrome).
// ---------------------------------------------------------------------------

export interface PresetContext {
  /** Factory for instance ids used by preset cards. */
  nextInstanceId: () => string
}

function card(id: string, descriptorId: string, title: string, state?: Record<string, unknown>): PersistedCardInstance {
  return {
    id,
    descriptorId,
    title,
    ...(state ? { state } : {}),
  }
}

function stack(id: WorkbenchStackId, tabIds: string[], activeTabId: string | null): WorkbenchStack {
  return { stackId: id, tabIds, activeTabId }
}

function column(
  slotId: WorkbenchSlotId,
  width: number,
  topInset: number,
  surfaceMode: 'float' | 'app',
  primaryTabIds: string[],
  activeTabId: string | null,
  collapsed = false,
): WorkbenchColumn {
  return {
    slotId,
    width,
    collapsed,
    surfaceMode,
    topInset,
    primary: stack('primary', primaryTabIds, activeTabId),
    secondary: null,
    splitRatio: null,
  }
}

export const HOME_GEOMETRY = {
  leftWidth: 260,
  rightWidth: 420,
  gap: 8,
  leftTopInset: 8,
  rightTopInset: 48,
} as const

function buildHomePreset(ctx: PresetContext): WorkbenchLayoutSnapshot {
  const n = ctx.nextInstanceId
  const snapshot = createEmptySnapshot('home')
  snapshot.gap = HOME_GEOMETRY.gap

  const nav = card(n(), 'nav', '项目')
  const chat = card(n(), 'chat', '聊天')
  const picker = card(n(), 'homePicker', 'Home')
  const git = card(n(), 'git', 'Git')
  const approval = card(n(), 'approval', '审批')
  const orchestration = card(n(), 'orchestration', '编排')
  const events = card(n(), 'events', '事件')
  const doctor = card(n(), 'doctor', 'Doctor')
  const browser = card(n(), 'browser', '浏览器')
  const agent = card(n(), 'agent', 'Agent')
  const terminal = card(n(), 'terminal', '终端')

  snapshot.cards = {
    [nav.id]: nav,
    [chat.id]: chat,
    [picker.id]: picker,
    [git.id]: git,
    [approval.id]: approval,
    [orchestration.id]: orchestration,
    [events.id]: events,
    [doctor.id]: doctor,
    [browser.id]: browser,
    [agent.id]: agent,
    [terminal.id]: terminal,
  }

  snapshot.columns = {
    left: column('left', HOME_GEOMETRY.leftWidth, HOME_GEOMETRY.leftTopInset, 'float', [nav.id], nav.id),
    center: column('center', 0, HOME_GEOMETRY.leftTopInset, 'float', [chat.id], chat.id),
    right: column(
      'right',
      HOME_GEOMETRY.rightWidth,
      HOME_GEOMETRY.rightTopInset,
      'float',
      [picker.id, git.id, approval.id, orchestration.id, events.id, doctor.id, browser.id, agent.id, terminal.id],
      picker.id,
    ),
  }
  snapshot.focusedCardId = chat.id
  return snapshot
}

function buildSettingsPreset(ctx: PresetContext): WorkbenchLayoutSnapshot {
  const n = ctx.nextInstanceId
  const snapshot = createEmptySnapshot('settings')
  snapshot.gap = 8

  const nav = card(n(), 'settingsNav', '设置')
  const content = card(n(), 'settingsContent', '内容')

  snapshot.cards = { [nav.id]: nav, [content.id]: content }
  snapshot.columns = {
    left: column('left', 220, 8, 'float', [nav.id], nav.id),
    center: column('center', 0, 8, 'float', [content.id], content.id),
    right: column('right', 0, 8, 'float', [], null, true),
  }
  snapshot.columnOrder = ['left', 'center', 'right']
  snapshot.focusedCardId = content.id
  return snapshot
}

function buildMarketPreset(ctx: PresetContext): WorkbenchLayoutSnapshot {
  const n = ctx.nextInstanceId
  const snapshot = createEmptySnapshot('market')
  snapshot.gap = 1

  const filter = card(n(), 'marketFilter', '筛选')
  const catalog = card(n(), 'marketCatalog', '目录')
  const detail = card(n(), 'marketDetail', '详情')

  snapshot.cards = { [filter.id]: filter, [catalog.id]: catalog, [detail.id]: detail }
  snapshot.columns = {
    left: column('left', 292, 0, 'app', [filter.id], filter.id),
    center: column('center', 0, 0, 'app', [catalog.id], catalog.id),
    right: column('right', 392, 0, 'app', [detail.id], detail.id),
  }
  snapshot.focusedCardId = catalog.id
  return snapshot
}

function buildCodePreset(ctx: PresetContext): WorkbenchLayoutSnapshot {
  const n = ctx.nextInstanceId
  const snapshot = createEmptySnapshot('code')
  snapshot.gap = 1

  const toolbar = card(n(), 'codeToolbar', '代码')
  const fileTree = card(n(), 'fileTree', '文件')
  const search = card(n(), 'search', '搜索')
  const editor = card(n(), 'editor', '编辑器')
  const patch = card(n(), 'patchDiff', '补丁')

  snapshot.cards = {
    [toolbar.id]: toolbar,
    [fileTree.id]: fileTree,
    [search.id]: search,
    [editor.id]: editor,
    [patch.id]: patch,
  }
  // toolbar pinned at top of center column; left = file tree + search, center = editor, right = patch.
  snapshot.columns = {
    left: column('left', 280, 0, 'app', [fileTree.id, search.id], fileTree.id),
    center: column('center', 0, 0, 'app', [toolbar.id, editor.id], editor.id),
    right: column('right', 480, 0, 'app', [patch.id], patch.id),
  }
  snapshot.focusedCardId = editor.id
  return snapshot
}

function buildDebugPreset(ctx: PresetContext): WorkbenchLayoutSnapshot {
  const n = ctx.nextInstanceId
  const snapshot = createEmptySnapshot('debug')
  snapshot.gap = 1

  const timeline = card(n(), 'debugTimeline', '时间线')
  const inspector = card(n(), 'debugInspector', 'Inspector')
  const graph = card(n(), 'debugGraph', 'Agent 图')
  const metrics = card(n(), 'debugMetrics', '指标')
  const diagnostics = card(n(), 'debugDiagnostics', '诊断')
  const preview = card(n(), 'debugPreview', '预览')
  const simulator = card(n(), 'debugSimulator', '模拟器')

  snapshot.cards = {
    [timeline.id]: timeline,
    [inspector.id]: inspector,
    [graph.id]: graph,
    [metrics.id]: metrics,
    [diagnostics.id]: diagnostics,
    [preview.id]: preview,
    [simulator.id]: simulator,
  }
  snapshot.columns = {
    left: column('left', 320, 0, 'app', [timeline.id, inspector.id], timeline.id),
    center: column('center', 0, 0, 'app', [graph.id, metrics.id], graph.id),
    right: column('right', 360, 0, 'app', [diagnostics.id, preview.id, simulator.id], diagnostics.id),
  }
  snapshot.focusedCardId = timeline.id
  return snapshot
}

export const PRESET_BUILDERS: Record<WorkbenchPageId, (ctx: PresetContext) => WorkbenchLayoutSnapshot> = {
  home: buildHomePreset,
  settings: buildSettingsPreset,
  market: buildMarketPreset,
  code: buildCodePreset,
  debug: buildDebugPreset,
}

export function buildPreset(pageId: WorkbenchPageId, ctx: PresetContext): WorkbenchLayoutSnapshot {
  const builder = PRESET_BUILDERS[pageId]
  if (!builder) throw new Error(`buildPreset: unknown page '${pageId}'`)
  return builder(ctx)
}
