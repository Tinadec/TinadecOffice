<script setup lang="ts">
import { computed, getCurrentInstance, nextTick, onBeforeUnmount, onMounted, provide, reactive, ref, shallowRef, watch } from 'vue'
import { type RouteLocationNormalizedLoaded, type Router, useRoute, useRouter } from 'vue-router'
import { Background } from '@vue-flow/background'
import {
  BaseEdge,
  Handle,
  MarkerType,
  Position,
  SelectionMode,
  VueFlow,
  useVueFlow,
  type Connection,
  type Edge as FlowEdge,
  type GraphNode,
  type Node as FlowNode,
  type NodeChange,
  type NodeDragEvent,
  type NodeMouseEvent,
} from '@vue-flow/core'
import { MiniMap } from '@vue-flow/minimap'
import { NodeResizer } from '@vue-flow/node-resizer'
import {
  Activity,
  AlertTriangle,
  ArrowUpRight,
  Bot,
  ChevronDown,
  Columns3,
  Eraser,
  FileCode2,
  Frame,
  Hand,
  Highlighter,
  ImagePlus,
  Layers,
  ListChecks,
  Map as MapIcon,
  Maximize,
  MessageCircle,
  MessageSquare,
  Minus,
  MousePointer2,
  Pen,
  Plus,
  Redo2,
  SlidersHorizontal,
  Sparkles,
  StickyNote,
  Type,
  Undo2,
  X,
} from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import {
  UieCanvas,
  UieCardHost,
  UieShell,
  arrangeSpace,
  computeSmartGuides,
  routeSpatialEdges,
  type SmartGuideLine,
  type SpatialChange,
  type SpatialDocObject,
  type SpatialLayout,
  type SpatialRect,
  type SpatialSeed,
} from '@tinadec/ui'
import ComposerBar from '@/components/ComposerBar.vue'
import SessionPlanPanel from '@/components/plan/SessionPlanPanel.vue'
import SpatialWorkCard from '@/components/spatial/SpatialWorkCard.vue'
import SpatialSectionNode from '@/components/spatial/SpatialSectionNode.vue'
import SpatialStickyNode from '@/components/spatial/SpatialStickyNode.vue'
import SpatialTextNode from '@/components/spatial/SpatialTextNode.vue'
import SpatialProjectBoardNode from '@/components/spatial/SpatialProjectBoardNode.vue'
import SpatialDrawingLayer from '@/components/spatial/SpatialDrawingLayer.vue'
import SpatialMediaFileNode from '@/components/spatial/SpatialMediaFileNode.vue'
import SpatialCommentPinsLayer from '@/components/spatial/SpatialCommentPinsLayer.vue'
import SpatialFreeConnectorEdge from '@/components/spatial/SpatialFreeConnectorEdge.vue'
import SpatialPresenceLayer, {
  type SpatialCollaboratorLike,
  type SpatialTeammateSteerEvent,
} from '@/components/spatial/SpatialPresenceLayer.vue'
import SpatialLiveDigestInspector, {
  type SpatialDigestFillSteerEvent,
} from '@/components/spatial/SpatialLiveDigestInspector.vue'
import SpatialSelectionBar, {
  type SpatialPromoteToBoardEvent,
} from '@/components/spatial/SpatialSelectionBar.vue'
import TeammateSteerPopover, {
  type TeammateSteerAnchorRect,
  type TeammateSteerTarget,
} from '@/components/teammate/TeammateSteerPopover.vue'
import {
  BoardTaskDrawer,
  buildSpatialFocusLocation,
  consumePendingSpatialFocus,
  stashPendingSpatialFocus,
  useProjectBoardView,
} from '@/components/board'
import {
  SPATIAL_SECTION_PALETTE,
  SPATIAL_SECTION_TONES,
  SPATIAL_STICKY_PALETTE,
  SPATIAL_STICKY_TONES,
  SPATIAL_TEXT_SCALES,
  SPATIAL_WHITEBOARD_DEFAULTS,
  computeSpatialFocusViewport,
  encodeMediaPathToken,
  isSectionTone,
  isStickyTone,
  isTextScale,
  sanitizeSpatialBoardWidgetProps,
  type SpatialAlignMode,
  type SpatialBoardWidgetProps,
  type SpatialCommentPin,
  type SpatialConnectorCurve,
  type SpatialDistributeAxis,
  type SpatialDrawingProps,
  type SpatialMediaKind,
  type SpatialSectionTone,
  type SpatialSelectionBounds,
  type SpatialStickyTone,
  type SpatialTextScale,
  type SpatialToolMode,
  type SpatialViewport,
} from '@/components/spatial/spatialWhiteboardTypes'
import { UiButton, UiCheckbox, UiSelect } from '@/components/ui'
import { homeController as c } from '@/controllers/HomeController'
import { ensureProductionUie } from '@/lib/uiEngine'
import {
  api,
  type ApproveProjectBoardTaskInput,
  type CreateProjectBoardTaskInput,
  type DispatchProjectBoardTaskInput,
  type ProjectBoardConflictResolution,
  type ProjectBoardTaskDto,
  type ProjectBoardTaskStatus,
  type SessionTopologyDto,
  type UpdateProjectBoardTaskInput,
} from '@/api'
import { selectionIdentity } from '@/lib/storageScope'
import { useHostAccess } from '@/lib/hostAccess'
import { useNotifications } from '@/composables/useNotifications'
import {
  buildSpatialSelectionPrompt,
  collectSpatialTeammates,
  computeCausalHighlight,
  projectSpatialObjects,
  projectSpatialRelations,
  type SpatialSelectionContextItem,
} from '@/lib/spatialObjects'
import { subscribeToSessionEvents } from '@/lib/sessionEventBus'
import { pickHostFiles, resolveWebGatewayBase } from '@/lib/hostConnection'
import { openDocument, openInEditor } from '@/lib/documentWorkspace'
import { readFileText, type ReadFileDataDto } from '@/lib/workspaceSearch'
import { useSpatialGit } from '@/composables/useSpatialGit'
import { useSpatialBoardSync } from '@/composables/useSpatialBoardSync'
import { selectPrimaryRunId, useLiveDigest, type LiveDigestTeammateThoughtDto } from '@/composables/useLiveDigest'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { usePageTransition } from '@/composables/usePageTransition'

const { t, te, locale } = useI18n()
const { canAccessBackend } = useHostAccess()
const { notify, dismissByKey } = useNotifications()

const SPATIAL_TOPOLOGY_ERROR_KEY = 'spatial-topology-load'

function resolveOptionalRoute(): RouteLocationNormalizedLoaded | null {
  try {
    const inst = getCurrentInstance()
    if (!inst) return null
    return useRoute() ?? null
  } catch {
    return null
  }
}

function resolveOptionalRouter(): Router | null {
  try {
    const inst = getCurrentInstance()
    if (!inst) return null
    return useRouter() ?? null
  } catch {
    return null
  }
}

const optionalRoute = resolveOptionalRoute()
const optionalRouter = resolveOptionalRouter()

function tr(key: string, fallback: string, params?: Record<string, unknown>): string {
  if (te(key)) return params ? t(key, params) : t(key)
  if (!params) return fallback
  return fallback.replace(/\{(\w+)\}/g, (_, k: string) => String(params[k] ?? ''))
}
const emit = defineEmits<{ ready: [] }>()
const uie = ensureProductionUie()

const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const materialStyle = computed(() => {
  const { backgroundColor, backdropFilter, WebkitBackdropFilter, ...tokens } = getPanelStyle()
  return tokens
})
const materialAttrs = computed(() => getPanelDataAttributes())

c.setViewMode('space')
const ready = ref(false)
const { transitionClasses } = usePageTransition({
  ready: uie.ready,
  skipExitWhen: () => !ready.value || c.viewMode.value !== 'space',
})

const surface = ref<HTMLElement | null>(null)
const composerDock = ref<HTMLElement | null>(null)
const hero = computed(() => c.messages.value.length === 0)

// User-approved resident collapsible bottom composer: expanded by default!
const composerCollapsed = ref(false)

// Figma UI3 chrome state
const layersOpen = ref(false)
const layersQuery = ref('')
const zoomMenuOpen = ref(false)
const showMinimap = ref(false)
const showDependencies = ref(true)
const inspectorOpen = ref(false)
const inspectorTab = ref<'details' | 'style' | 'plan' | 'digest'>('details')
const dragOverCanvas = ref(false)

// Direct single-teammate steer/pause popover state
const steerTarget = ref<(TeammateSteerTarget & { initialDraft?: string }) | null>(null)
const steerOpen = ref(false)
const pausedTeammates = ref<Record<string, boolean>>({})

// Tool dock state
const activeTool = ref<SpatialToolMode>('select')
const spacePressed = ref(false)
const activeStickyTone = ref<SpatialStickyTone>('butter')
const stickyPaletteOpen = ref(false)
const drawPopoverOpen = ref(false)
const activeInkColor = ref<string>(SPATIAL_WHITEBOARD_DEFAULTS.pen.color)
const activeStrokeWidth = ref<number>(SPATIAL_WHITEBOARD_DEFAULTS.pen.strokeWidth)
const connectorSourceId = ref<string | null>(null)
const activePinId = ref<string | null>(null)

const INK_SWATCHES = ['#3b82f6', '#8b5cf6', '#10b981', '#f59e0b', '#ef4444', '#64748b'] as const
const WORK_HANDLES = [Position.Top, Position.Bottom, Position.Left, Position.Right] as const
const IMAGE_EXTENSIONS = new Set(['.png', '.jpg', '.jpeg', '.webp', '.gif', '.avif', '.bmp', '.svg'])

// Live viewport & Semantic Macro LOD
const currentViewport = ref<SpatialViewport>({ x: 0, y: 0, zoom: 1 })
const isMacroLod = computed(() => currentViewport.value.zoom < 0.55)
const zoomPercent = computed(() => Math.round((currentViewport.value.zoom || 1) * 100))
provide('space:macro', isMacroLod)

const surfaceSize = reactive({ width: 1280, height: 800 })
let surfaceObserver: ResizeObserver | null = null

const gesturing = ref(false)
const dragGuides = ref<SmartGuideLine[]>([])
const topology = shallowRef<SessionTopologyDto | null>(null)
const loading = ref(false)
const sessionKey = computed(() => c.selectedSessionId.value ?? `draft:${c.selectedProjectId.value ?? 'free'}`)
const projectPath = computed(() =>
  c.currentSession.value
    ? c.projects.value.find((p) => p.id === c.currentSession.value?.project_id)?.path ?? ''
    : c.currentProject.value?.path ?? '',
)
const git = useSpatialGit(projectPath)
provide('space:git', git)

const {
  applyNodeChanges,
  updateNodeInternals,
  getNodes,
  setViewport,
  zoomIn,
  zoomOut,
} = useVueFlow({ id: 'session-space' })

// Multiplayer FigJam whiteboard sync
const board = useSpatialBoardSync({
  sessionKey,
  displayName: () => t('space.figma.youLabel'),
  onFollowViewport: (vp) => {
    currentViewport.value = { ...vp }
    void setViewport(vp)
  },
})

// Phase 2 Spatial <-> Project Board live bridge (SSE-backed task hydration, drawer, promotion & focus pulse)
const resolvedSpatialProjectId = computed<string | null>(() => {
  const fromSession = c.currentSession.value?.project_id
  if (fromSession) return selectionIdentity(fromSession).id
  const fromSelected = c.selectedProjectId.value
  if (fromSelected) return selectionIdentity(fromSelected).id
  return null
})

const activeSpatialProjects = computed(() =>
  c.projects.value
    .filter((p) => p.lifecycle_status !== 'archived' && p.lifecycle_status !== 'trashed')
    .map((p) => ({ id: p.id, name: p.name })),
)

const spatialProjectName = computed<string | null>(() => {
  if (!resolvedSpatialProjectId.value) return null
  return c.projects.value.find((p) => p.id === resolvedSpatialProjectId.value)?.name ?? null
})

const spatialProjectBoard = useProjectBoardView({
  scope: 'project',
  projectId: () => resolvedSpatialProjectId.value,
  projects: activeSpatialProjects,
})

const boardTasksById = computed<ReadonlyMap<string, ProjectBoardTaskDto>>(() => {
  const map = new Map<string, ProjectBoardTaskDto>()
  for (const item of spatialProjectBoard.tasks.value) {
    map.set(item.task_id, item)
  }
  return map
})

const boardTasksByKey = computed<ReadonlyMap<string, ProjectBoardTaskDto>>(() => {
  const map = new Map<string, ProjectBoardTaskDto>()
  for (const item of spatialProjectBoard.tasks.value) {
    map.set(item.task_key, item)
  }
  return map
})

provide('space:board-tasks-by-id', boardTasksById)
provide('space:board-tasks-by-key', boardTasksByKey)

const promotingToBoard = ref(false)
const boardDrawerOpen = ref(false)
const boardDrawerMode = ref<'create' | 'edit'>('edit')
const boardDrawerTaskId = ref<string | null>(null)
const boardDrawerFallbackTask = ref<ProjectBoardTaskDto | null>(null)
const boardDrawerDefaultProjectId = ref<string | null>(null)
const boardDrawerDefaultStatus = ref<ProjectBoardTaskStatus>('ready')
const pulsingObjectId = ref<string | null>(null)
let pulseTimer: ReturnType<typeof setTimeout> | null = null
let lastDroppedTaskGuard: { key: string; at: number } | null = null

const selectedBoardDrawerTask = computed<ProjectBoardTaskDto | null>(() => {
  if (!boardDrawerTaskId.value) return boardDrawerFallbackTask.value
  return boardTasksById.value.get(boardDrawerTaskId.value) ?? boardDrawerFallbackTask.value
})

function resolveLinkedBoardTask(propsRec: Record<string, unknown>): ProjectBoardTaskDto | null {
  const tid = typeof propsRec.board_task_id === 'string' ? propsRec.board_task_id.trim() : ''
  if (tid && boardTasksById.value.has(tid)) {
    return boardTasksById.value.get(tid) ?? null
  }
  const tkey = typeof propsRec.board_task_key === 'string' ? propsRec.board_task_key.trim() : ''
  if (tkey && boardTasksByKey.value.has(tkey)) {
    return boardTasksByKey.value.get(tkey) ?? null
  }
  return null
}

function triggerFocusPulse(objectId: string) {
  if (pulseTimer !== null) clearTimeout(pulseTimer)
  pulsingObjectId.value = objectId
  pulseTimer = setTimeout(() => {
    pulseTimer = null
    if (pulsingObjectId.value === objectId) {
      pulsingObjectId.value = null
    }
  }, 1800)
}

let alive = true
let generation = 0
let topologyAbort: AbortController | undefined
let topologyFlight: string | null = null
let topologyDirty = false
let refreshTimer: ReturnType<typeof setTimeout> | undefined
let unsubscribe: (() => void) | undefined

const focusedRun = ref('')
const selectedId = ref<string | null>(null)
const selectedIds = ref<string[]>([])
const terminalVisible = ref(false)
const visitedDetails = ref<string[]>([])
const focusedInstance = ref<string | null>(null)

const objects = computed(() =>
  projectSpatialObjects({
    sessionId: sessionKey.value,
    messages: c.messages.value,
    queuedMessages: c.queuedMessages.value,
    approvals: c.approvals.value,
    hasGitChanges: Boolean(git.state.value.preview.files?.length),
    topology: topology.value,
    orchestration: c.orchestration.value,
    turns: c.agentTurnActivities.value,
    streamingReply: c.streamingReply.value,
    streamingReplies: c.streamingReplies.value,
    streamingRunId: c.stoppableRunId.value,
  }),
)
const objectMap = computed(() => Object.fromEntries(objects.value.map((o) => [o.id, o])))
const runs = computed(() => objects.value.filter((o) => o.kind === 'run'))
provide('space:terminal-runs', computed(() => new Set(runs.value.flatMap((o) => (o.runId ? [o.runId] : [])))))
provide('space:terminal-visible', terminalVisible)

// Same-source Live Digest stream (ref-counted per runId, shared across sections, cursors & inspector)
const activeDigestRunId = computed<string | null>(() => {
  if (focusedRun.value && focusedRun.value !== '*') return focusedRun.value
  if (c.stoppableRunId.value) return c.stoppableRunId.value
  const primary = selectPrimaryRunId(c.runs.value)
  if (primary) return primary
  return runs.value[0]?.runId ?? null
})
const liveDigest = useLiveDigest(activeDigestRunId)

const selectedObject = computed(() => (selectedId.value ? objectMap.value[selectedId.value] : undefined))
const selectedWhiteboardObject = computed<SpatialDocObject | null>(() => {
  if (selectedId.value && board.objectsById.value[selectedId.value]) {
    return board.objectsById.value[selectedId.value]!
  }
  for (const id of selectedIds.value) {
    if (board.objectsById.value[id]) return board.objectsById.value[id]!
  }
  return null
})
const selectedSet = computed(() => new Set(selectedIds.value))

const compact = computed(() =>
  Object.fromEntries(objects.value.map((o) => [o.id, Boolean(uie.snapshot.value.space?.items[o.id]?.compact)])),
)
const relations = computed(() => projectSpatialRelations(objects.value))
const related = computed(() => relations.value.filter((r) => r.source === selectedId.value || r.target === selectedId.value))
const causalHighlight = computed(() => computeCausalHighlight(selectedSet.value, relations.value))
const instanceTasks = computed(() =>
  objects.value.filter((o) => o.kind === 'task' && o.runId === focusedRun.value && o.instanceId === focusedInstance.value),
)
const visibleObjects = computed(() =>
  objects.value.filter(
    (o) => !compact.value[o.id] || !o.runId || o.kind === 'run' || focusedRun.value === '*' || o.runId === focusedRun.value,
  ),
)
const visibleIds = computed(() => new Set(visibleObjects.value.map((o) => o.id)))
const pendingApprovals = computed(() =>
  c.approvals.value.filter((a) => a.session_id === c.selectedSessionId.value && a.status === 'pending'),
)
const incomplete = computed(() =>
  Boolean(
    topology.value &&
      (topology.value.runs_truncated ||
        topology.value.members_truncated ||
        topology.value.leases_truncated ||
        topology.value.runs.some((r) => r.tasks_truncated || r.instances_truncated)),
  ),
)
const statusText = (status?: string) => (status && te(`space.status.${status}`) ? t(`space.status.${status}`) : status)
const titleOf = (id: string) => objectMap.value[id]?.title || t(`space.kind.${objectMap.value[id]?.kind ?? 'task'}`)

provide('space:objects', objectMap)
provide('space:compact', compact)
provide('space:open', openDetails)
provide('space:locate', locate)
provide('space:focus-instance', (runId: string, instanceId: string) => {
  focusedRun.value = runId
  focusedInstance.value = instanceId
  const task = objects.value.find((o) => o.runId === runId && o.instanceId === instanceId)
  if (task) openDetails(task.id)
})
provide('space:open-terminal', () => {
  uie.dispatch({
    command: { type: 'openCard', scope: uie.scope.value, descriptorId: 'terminal', slotId: 'right', title: t('space.cluster.terminal') },
    source: 'user',
    expectedRevision: uie.snapshot.value.revision,
  })
  terminalVisible.value = true
  inspectorOpen.value = false
})

watch(runs, (value) => {
  if (!focusedRun.value && value.length) {
    focusedRun.value = value.find((o) => o.runId === c.stoppableRunId.value)?.runId ?? value[0].runId ?? '*'
  }
})

const measuredHeights = reactive(new Map<string, number>())
const seeds = computed<SpatialSeed[]>(() =>
  objects.value.map((o) => ({
    id: o.id,
    groupId: o.groupId,
    groupOrder: o.kind === 'meeting' ? 0 : o.runId ? 1 : 2,
    measuredHeight: measuredHeights.get(o.id),
    dependencyIds: o.dependencyIds,
    dependencyUnverified: o.dependencyUnverified,
    role: o.kind === 'run' || o.kind === 'meeting' ? 'anchor' : o.kind === 'task' ? 'task' : o.kind === 'result' ? 'result' : o.runId ? 'activity' : 'shared',
  })),
)

// Child counts per whiteboard section
const sectionChildCounts = computed<Record<string, number>>(() => {
  const counts: Record<string, number> = {}
  for (const obj of board.sortedObjects.value) {
    if (obj.parentId) {
      counts[obj.parentId] = (counts[obj.parentId] ?? 0) + 1
    }
  }
  return counts
})

// Collapsed section descendant hiding
const hiddenByCollapsedSection = computed<Set<string>>(() => {
  const collapsed = new Set(
    board.objectsByKind.value.section.filter((s) => Boolean(s.props.collapsed)).map((s) => s.id),
  )
  const hidden = new Set<string>()
  if (!collapsed.size) return hidden
  const byId = board.objectsById.value
  for (const obj of board.sortedObjects.value) {
    let cursor = obj.parentId
    let guard = 0
    while (cursor && guard++ < 32) {
      if (collapsed.has(cursor)) {
        hidden.add(obj.id)
        break
      }
      cursor = byId[cursor]?.parentId ?? null
    }
  }
  return hidden
})

function resolveSectionRunId(sec: SpatialDocObject): string | null {
  const explicit = typeof sec.props.runId === 'string' ? sec.props.runId.trim() : ''
  if (explicit) return explicit
  const space = uie.snapshot.value.space
  if (!space) return null
  const foundRunIds = new Set<string>()
  for (const obj of objects.value) {
    if (!obj.runId) continue
    const item = space.items[obj.id]
    if (!item) continue
    const cx = item.x + item.width / 2
    const cy = item.y + (measuredHeights.get(obj.id) ?? item.height) / 2
    if (cx >= sec.x && cx <= sec.x + sec.width && cy >= sec.y && cy <= sec.y + sec.height) {
      foundRunIds.add(obj.runId)
      if (foundRunIds.size > 1) return null
    }
  }
  return foundRunIds.size === 1 ? [...foundRunIds][0]! : null
}

const nodes = computed<FlowNode[]>((previous) => {
  if (gesturing.value) return previous ?? []
  const space = uie.snapshot.value.space
  const out: FlowNode[] = []
  const digestRunId = activeDigestRunId.value
  const digestHeadline = liveDigest.headline.value.trim()
  const digestWl = liveDigest.contextWaterline.value
  const digestUsagePct = digestWl && (digestWl.max_tokens > 0 || digestWl.usage_percent > 0)
    ? Math.max(0, Math.min(100, Math.round(digestWl.usage_percent)))
    : null
  const digestCompactions = digestWl?.compaction_count ?? 0
  const digestRunning = liveDigest.isRunning.value

  // 1. Sections first (behind cards)
  for (const sec of board.objectsByKind.value.section) {
    const isCollapsed = Boolean(sec.props.collapsed)
    const sectionRunId = resolveSectionRunId(sec)
    const matchesActiveDigest = Boolean(sectionRunId && digestRunId && sectionRunId === digestRunId)
    const linkedTask = resolveLinkedBoardTask(sec.props)
    out.push({
      id: sec.id,
      type: 'section',
      position: { x: sec.x, y: sec.y },
      width: sec.width,
      height: isCollapsed ? 44 : sec.height,
      zIndex: 1,
      class: [
        'space-node-section',
        selectedSet.value.has(sec.id) ? 'selected' : '',
        pulsingObjectId.value === sec.id ? 'space-focus-pulse' : '',
      ].filter(Boolean).join(' '),
      style: {
        width: `${sec.width}px`,
        height: isCollapsed ? '44px' : `${sec.height}px`,
        display: hiddenByCollapsedSection.value.has(sec.id) ? 'none' : undefined,
      },
      dragHandle: '.space-drag-handle',
      data: {
        ...sec.props,
        childCount: sectionChildCounts.value[sec.id] ?? 0,
        ...(linkedTask ? { linkedTask } : {}),
        ...(sectionRunId ? { runId: sectionRunId } : {}),
        ...(matchesActiveDigest
          ? {
              liveHeadline: digestHeadline || null,
              liveUsagePercent: digestUsagePct,
              liveCompactionCount: digestCompactions,
              liveRunning: digestRunning,
            }
          : {}),
      },
      connectable: true,
      deletable: false,
    })
  }

  // 2. Work cards (Runs, Tasks, Planning, Git, Approvals, Tools)
  for (const object of objects.value) {
    const item = space?.items[object.id]
    if (!item) continue
    const isCompact = compact.value[object.id]
    const isCausalFocus = causalHighlight.value.active && causalHighlight.value.nodeIds.has(object.id)
    const isCausalDim = causalHighlight.value.active && !isCausalFocus
    out.push({
      id: object.id,
      type: 'work',
      position: { x: item.x, y: item.y },
      width: item.width,
      height: isCompact || !item.autoHeight ? item.height : undefined,
      zIndex: 10,
      class: [
        !isCompact && item.autoHeight ? 'space-auto-height' : '',
        selectedSet.value.has(object.id) ? 'selected' : '',
        pulsingObjectId.value === object.id ? 'space-focus-pulse' : '',
        isCausalFocus ? 'space-causal-focus' : '',
        isCausalDim ? 'space-causal-dim' : '',
        isMacroLod.value ? 'space-node-macro' : '',
      ].filter(Boolean).join(' '),
      style: {
        width: `${item.width}px`,
        height: isCompact || !item.autoHeight ? `${item.height}px` : 'auto',
        display: visibleIds.value.has(object.id) ? undefined : 'none',
      },
      dragHandle: '.space-drag-handle',
      data: {},
      connectable: activeTool.value === 'connector',
      deletable: false,
    })
  }

  // 2b. Live Project Board widgets (`kind: 'board'`, zero task data copy in spatial props)
  for (const bObj of board.objectsByKind.value.board) {
    out.push({
      id: bObj.id,
      type: 'board',
      position: { x: bObj.x, y: bObj.y },
      width: bObj.width,
      height: bObj.height,
      zIndex: 12,
      class: [
        'space-node-board',
        selectedSet.value.has(bObj.id) ? 'selected' : '',
        pulsingObjectId.value === bObj.id ? 'space-focus-pulse' : '',
      ].filter(Boolean).join(' '),
      style: {
        width: `${bObj.width}px`,
        height: `${bObj.height}px`,
        display: hiddenByCollapsedSection.value.has(bObj.id) ? 'none' : undefined,
      },
      dragHandle: '.space-drag-handle',
      data: sanitizeSpatialBoardWidgetProps(bObj.props),
      connectable: true,
      deletable: false,
    })
  }

  // 3. Whiteboard stickies, text & media tiles
  for (const wbKind of ['media', 'sticky', 'text'] as const) {
    const zBase = wbKind === 'text' ? 16 : wbKind === 'sticky' ? 15 : 14
    for (const obj of board.objectsByKind.value[wbKind]) {
      const linkedTask = wbKind === 'sticky' || wbKind === 'text' ? resolveLinkedBoardTask(obj.props) : null
      out.push({
        id: obj.id,
        type: wbKind,
        position: { x: obj.x, y: obj.y },
        width: obj.width,
        height: obj.height,
        zIndex: zBase,
        class: [
          `space-node-${wbKind}`,
          selectedSet.value.has(obj.id) ? 'selected' : '',
          pulsingObjectId.value === obj.id ? 'space-focus-pulse' : '',
        ].filter(Boolean).join(' '),
        style: {
          width: `${obj.width}px`,
          height: `${obj.height}px`,
          display: hiddenByCollapsedSection.value.has(obj.id) ? 'none' : undefined,
        },
        dragHandle: '.space-drag-handle',
        data: {
          ...obj.props,
          ...(linkedTask ? { linkedTask } : {}),
        },
        connectable: true,
        deletable: false,
      })
    }
  }

  return out
})

const routes = computed(() => {
  if (!showDependencies.value) return []
  const liveNodes = new Map((gesturing.value ? getNodes?.value ?? [] : []).map((node) => [node.id, node]))
  const items = visibleObjects.value.flatMap((object) => {
    const item = uie.snapshot.value.space?.items[object.id]
    if (!item) return []
    const live = liveNodes.get(object.id)
    return [
      {
        ...item,
        ...(gesturing.value && live
          ? {
              ...live.position,
              width: live.dimensions.width || item.width,
              height: live.dimensions.height || item.height,
            }
          : {}),
      },
    ]
  })
  return routeSpatialEdges(
    items,
    relations.value.filter((r) => visibleIds.value.has(r.source) && visibleIds.value.has(r.target)),
  )
})
const routeMap = computed(() => Object.fromEntries(routes.value.map((route) => [route.id, route])))

const edges = computed<FlowEdge[]>(() => {
  const workEdges: FlowEdge[] = routes.value
    .filter((route) => route.kind !== 'blocked')
    .map((route) => {
      const highlighted = causalHighlight.value.active && causalHighlight.value.edgeIds.has(route.id)
      const dimmed = causalHighlight.value.active && !highlighted
      return {
        id: route.id,
        source: route.source,
        target: route.target,
        type: 'spatial',
        sourceHandle: route.sourceSide,
        targetHandle: route.targetSide,
        selectable: true,
        class: highlighted ? 'space-edge-causal-active' : dimmed ? 'space-edge-causal-dim' : '',
        data: { route, highlighted, dimmed },
        label: t('space.cluster.requires'),
        markerEnd: MarkerType.ArrowClosed,
      }
    })

  const freeEdges: FlowEdge[] = board.objectsByKind.value.connector
    .filter((conn) => {
      const src = String(conn.props.sourceId ?? '')
      const tgt = String(conn.props.targetId ?? '')
      return Boolean(src && tgt)
    })
    .map((conn) => ({
      id: conn.id,
      source: String(conn.props.sourceId),
      target: String(conn.props.targetId),
      type: 'free-connector',
      sourceHandle: typeof conn.props.sourceHandle === 'string' ? conn.props.sourceHandle : undefined,
      targetHandle: typeof conn.props.targetHandle === 'string' ? conn.props.targetHandle : undefined,
      selectable: true,
      selected: selectedSet.value.has(conn.id),
      label: typeof conn.props.label === 'string' ? conn.props.label : '',
      data: {
        curve: conn.props.curve === 'bezier' ? 'bezier' : 'smoothstep',
        label: typeof conn.props.label === 'string' ? conn.props.label : '',
        color: typeof conn.props.color === 'string' ? conn.props.color : undefined,
      },
    }))

  return [...workEdges, ...freeEdges]
})

const drawingStrokes = computed(() =>
  board.objectsByKind.value.drawing.map((d) => ({ id: d.id, props: d.props })),
)

// All bounding rects (work + whiteboard) for smart guides, presence selection frames, and selection bar
const workExternalRects = computed<Record<string, { x: number; y: number; width: number; height: number }>>(() => {
  const space = uie.snapshot.value.space
  const map: Record<string, { x: number; y: number; width: number; height: number }> = {}
  if (!space) return map
  for (const obj of visibleObjects.value) {
    const item = space.items[obj.id]
    if (!item) continue
    map[obj.id] = {
      x: item.x,
      y: item.y,
      width: item.width,
      height: measuredHeights.get(obj.id) ?? item.height,
    }
  }
  return map
})

const allObjectRects = computed<Record<string, { x: number; y: number; width: number; height: number }>>(() => {
  const map: Record<string, { x: number; y: number; width: number; height: number }> = {
    ...workExternalRects.value,
  }
  for (const obj of board.sortedObjects.value) {
    if (obj.kind === 'drawing' || obj.kind === 'connector') continue
    if (hiddenByCollapsedSection.value.has(obj.id)) continue
    map[obj.id] = {
      x: obj.x,
      y: obj.y,
      width: obj.width,
      height: obj.kind === 'section' && obj.props.collapsed ? 44 : obj.height,
    }
  }
  return map
})

const selectedBounds = computed<SpatialSelectionBounds | null>(() => {
  if (!selectedIds.value.length) return null
  const rects = selectedIds.value
    .map((id) => allObjectRects.value[id])
    .filter((r): r is NonNullable<typeof r> => Boolean(r))
  if (!rects.length) return null
  const minX = Math.min(...rects.map((r) => r.x))
  const minY = Math.min(...rects.map((r) => r.y))
  const maxX = Math.max(...rects.map((r) => r.x + r.width))
  const maxY = Math.max(...rects.map((r) => r.y + r.height))
  return { x: minX, y: minY, width: Math.max(1, maxX - minX), height: Math.max(1, maxY - minY) }
})

const visibleGuides = computed<SmartGuideLine[]>(() =>
  dragGuides.value.length ? dragGuides.value : board.activeGuides.value,
)

// Presence avatars: remote human/AI collaborators + active AI teammates + same-source Live Digest thoughts
const activeTeammates = computed(() => collectSpatialTeammates(objects.value))
const presenceCollaborators = computed<SpatialCollaboratorLike[]>(() => {
  const remote = board.collaborators.value.filter((item) => item.clientId !== board.clientId)
  const digestRunId = activeDigestRunId.value
  const thoughtByKey = new Map<string, LiveDigestTeammateThoughtDto>()
  for (const item of liveDigest.teammateThoughts.value) {
    if (item.handle) thoughtByKey.set(item.handle.trim().toLowerCase().replace(/^@/, ''), item)
    if (item.display_name) thoughtByKey.set(item.display_name.trim().toLowerCase().replace(/^@/, ''), item)
  }

  const findThought = (handle?: string | null, displayName?: string | null): LiveDigestTeammateThoughtDto | null => {
    for (const raw of [handle, displayName]) {
      if (!raw) continue
      const key = raw.trim().toLowerCase().replace(/^@/, '')
      const hit = thoughtByKey.get(key)
      if (hit) return hit
    }
    return null
  }

  const isPausedHandle = (handle?: string | null, status?: string | null): boolean => {
    const clean = (handle ?? '').trim().replace(/^@/, '')
    if (clean && pausedTeammates.value[clean] !== undefined) return Boolean(pausedTeammates.value[clean])
    return String(status ?? '').trim().toLowerCase() === 'paused'
  }

  const existingKeys = new Set<string>()
  const merged: SpatialCollaboratorLike[] = remote.map((item) => {
    existingKeys.add(item.displayName.toLowerCase())
    if (item.role !== 'ai') return item
    const thoughtDto = findThought(item.displayName, item.displayName)
    const handle = thoughtDto?.handle ?? item.displayName
    existingKeys.add(handle.toLowerCase())
    const paused = isPausedHandle(handle, thoughtDto?.status ?? item.status)
    return {
      ...item,
      handle,
      runId: digestRunId,
      status: paused ? 'paused' : (thoughtDto?.status ?? item.status ?? null),
      paused,
      thought: thoughtDto?.thought ?? null,
      currentTask: thoughtDto?.current_task ?? null,
    }
  })

  const palette = ['#8b5cf6', '#0ea5e9', '#10b981', '#f59e0b', '#ec4899']
  const rects = allObjectRects.value

  activeTeammates.value.forEach((tm, idx) => {
    const key = tm.name.toLowerCase()
    if (existingKeys.has(key)) return
    existingKeys.add(key)
    const thoughtDto = findThought(tm.name, tm.name)
    const handle = thoughtDto?.handle ?? tm.name
    existingKeys.add(handle.toLowerCase())
    const cardRect = tm.objectId ? rects[tm.objectId] : undefined
    const taskObj = tm.objectId ? objectMap.value[tm.objectId] : undefined
    const paused = isPausedHandle(handle, thoughtDto?.status ?? tm.status)
    merged.push({
      clientId: tm.id,
      displayName: thoughtDto?.display_name || tm.name,
      color: palette[idx % palette.length]!,
      role: 'ai',
      handle,
      runId: tm.runId ?? digestRunId,
      agentInstanceId: tm.instanceId ?? null,
      status: paused ? 'paused' : (thoughtDto?.status ?? tm.status),
      paused,
      thought: thoughtDto?.thought ?? null,
      currentTask: thoughtDto?.current_task ?? taskObj?.title ?? null,
      cursor: cardRect ? { x: cardRect.x + cardRect.width - 24, y: cardRect.y + 14 } : null,
      selection: tm.objectId ? [tm.objectId] : [],
    })
  })

  // Also surface active Live Digest teammates that are not yet reflected in topology tasks
  const runCardRect = digestRunId ? rects[`tasks:${digestRunId}`] : undefined
  liveDigest.teammateThoughts.value.forEach((tm, idx) => {
    const handleKey = tm.handle.trim().toLowerCase()
    const nameKey = (tm.display_name || tm.handle).trim().toLowerCase()
    if (existingKeys.has(handleKey) || existingKeys.has(nameKey)) return
    existingKeys.add(handleKey)
    existingKeys.add(nameKey)
    const paused = isPausedHandle(tm.handle, tm.status)
    merged.push({
      clientId: `digest:${tm.handle}`,
      displayName: tm.display_name || tm.handle,
      color: palette[(merged.length + idx) % palette.length]!,
      role: 'ai',
      handle: tm.handle,
      runId: digestRunId,
      status: paused ? 'paused' : tm.status,
      paused,
      thought: tm.thought || null,
      currentTask: tm.current_task ?? null,
      cursor: runCardRect
        ? { x: runCardRect.x + runCardRect.width - 24, y: runCardRect.y + 18 + idx * 44 }
        : { x: 180 + idx * 140, y: 140 },
    })
  })

  return merged
})

const panModeActive = computed(() => activeTool.value === 'pan' || spacePressed.value)
const isSelectTool = computed(() => activeTool.value === 'select' && !spacePressed.value)

provide('space:measure', (id: string, height: number) => {
  const rounded = Math.ceil(height)
  if (gesturing.value || compact.value[id] || measuredHeights.get(id) === rounded) return
  measuredHeights.set(id, rounded)
  syncObjects()
  void nextTick(() => updateNodeInternals([id]))
})

function syncObjects() {
  if (!ready.value || uie.snapshot.value.space?.sessionId !== sessionKey.value || !seeds.value.length) return
  uie.dispatch({
    command: { type: 'spaceSync', scope: uie.scope.value, seeds: seeds.value },
    source: 'route',
    expectedRevision: uie.snapshot.value.revision,
  })
}
watch(objects, syncObjects)

async function loadTopology() {
  const id = c.selectedSessionId.value
  if (id && topologyFlight === id) {
    topologyDirty = true
    return
  }
  const read = ++generation
  topologyAbort?.abort()
  const controller = new AbortController()
  topologyAbort = controller
  if (!id) {
    topology.value = null
    loading.value = false
    topologyFlight = null
    dismissByKey(SPATIAL_TOPOLOGY_ERROR_KEY)
    return
  }
  if (!canAccessBackend.value) {
    loading.value = false
    topologyFlight = null
    return
  }
  topologyFlight = id
  loading.value = true
  try {
    const data = await api.getSessionTopology(
      id,
      { include_finished: true, max_runs: 40, max_tasks: 200 },
      AbortSignal.any([controller.signal, AbortSignal.timeout(10000)]),
    )
    if (!alive || read !== generation || id !== c.selectedSessionId.value) return
    if (data.session_id !== id) throw new Error(t('space.cluster.scopeMismatch'))
    topology.value = data
    dismissByKey(SPATIAL_TOPOLOGY_ERROR_KEY)
  } catch (error) {
    if (alive && read === generation && !controller.signal.aborted) {
      notify.error(error, {
        title: t(topology.value ? 'space.cluster.stale' : 'space.cluster.loadFailed'),
        key: SPATIAL_TOPOLOGY_ERROR_KEY,
        action: {
          label: t('space.retry'),
          run: () => loadTopology(),
        },
      })
    }
  } finally {
    if (alive && read === generation) {
      loading.value = false
      topologyFlight = null
      if (topologyDirty) {
        topologyDirty = false
        void loadTopology()
      }
    }
  }
}

function enterSession() {
  if (!ready.value) return
  generation++
  gesturing.value = false
  dragGuides.value = []
  board.clearActiveGuides()
  clearTimeout(refreshTimer)
  refreshTimer = undefined
  topologyFlight = null
  topologyDirty = false
  topology.value = null
  dismissByKey(SPATIAL_TOPOLOGY_ERROR_KEY)
  measuredHeights.clear()
  focusedRun.value = ''
  selectedId.value = null
  selectedIds.value = []
  inspectorOpen.value = false
  terminalVisible.value = false
  focusedInstance.value = null
  visitedDetails.value = []
  activePinId.value = null
  connectorSourceId.value = null
  steerOpen.value = false
  steerTarget.value = null
  pausedTeammates.value = {}
  uie.showSpace(sessionKey.value)
  syncObjects()
  void loadTopology()
  void nextTick(restoreViewport)
}
watch(sessionKey, enterSession)

watch(canAccessBackend, (readyNow, wasReady) => {
  if (readyNow && wasReady === false && ready.value) {
    dismissByKey(SPATIAL_TOPOLOGY_ERROR_KEY)
    void loadTopology()
    void spatialProjectBoard.refresh()
    void git.refresh()
  }
})

function measureSurface() {
  const rect = surface.value?.getBoundingClientRect()
  if (rect && rect.width > 0 && rect.height > 0) {
    surfaceSize.width = Math.round(rect.width)
    surfaceSize.height = Math.round(rect.height)
  }
}

onMounted(async () => {
  c.start()
  await uie.ready
  if (!alive) return
  ready.value = true
  enterSession()
  measureSurface()
  if (typeof ResizeObserver !== 'undefined' && surface.value) {
    surfaceObserver = new ResizeObserver(() => measureSurface())
    surfaceObserver.observe(surface.value)
  }
  unsubscribe = subscribeToSessionEvents((event) => {
    if (event.session_id !== c.selectedSessionId.value || refreshTimer) return
    refreshTimer = setTimeout(() => {
      refreshTimer = undefined
      void loadTopology()
      if (event.type.startsWith('tool.execution.') || event.type === 'run.completed') void git.refresh()
    }, 800)
  })
  document.addEventListener('keydown', onDocumentKeydown)
  document.addEventListener('keyup', onDocumentKeyup)
  window.addEventListener('paste', onWindowPaste)
  await nextTick()
  await applyPendingOrRouteSpatialFocus()
  emit('ready')
})

onBeforeUnmount(() => {
  alive = false
  generation++
  topologyAbort?.abort()
  dismissByKey(SPATIAL_TOPOLOGY_ERROR_KEY)
  unsubscribe?.()
  clearTimeout(refreshTimer)
  if (pulseTimer !== null) clearTimeout(pulseTimer)
  surfaceObserver?.disconnect()
  document.removeEventListener('keydown', onDocumentKeydown)
  document.removeEventListener('keyup', onDocumentKeyup)
  window.removeEventListener('paste', onWindowPaste)
})

// ── Viewport, Camera & Coordinates ────────────────────────────────────────────

function clientToWorld(clientX: number, clientY: number): { x: number; y: number } {
  const rect = surface.value?.getBoundingClientRect()
  const left = rect?.left ?? 0
  const top = rect?.top ?? 0
  const zoom = Math.max(0.1, currentViewport.value.zoom || 1)
  return {
    x: (clientX - left - currentViewport.value.x) / zoom,
    y: (clientY - top - currentViewport.value.y) / zoom,
  }
}

function viewportCenterWorld(): { x: number; y: number } {
  const w = surfaceSize.width || 1280
  const h = surfaceSize.height || 800
  const zoom = Math.max(0.1, currentViewport.value.zoom || 1)
  return {
    x: (w / 2 - currentViewport.value.x) / zoom,
    y: (h / 2 - currentViewport.value.y) / zoom,
  }
}

function onViewportChange(vp: SpatialViewport) {
  currentViewport.value = { x: vp.x, y: vp.y, zoom: vp.zoom }
}

function camera(viewport: SpatialLayout['viewport']) {
  currentViewport.value = { x: viewport.x, y: viewport.y, zoom: viewport.zoom }
  if (ready.value) {
    uie.dispatch({
      command: { type: 'spaceViewport', scope: uie.scope.value, viewport },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
    board.sendPresence({ viewport })
  }
}

function restoreViewport() {
  const v = uie.snapshot.value.space?.viewport
  if (v) {
    currentViewport.value = { ...v }
    void setViewport(v)
  }
}

async function setZoomPreset(targetZoom: number) {
  zoomMenuOpen.value = false
  const clamped = Math.max(0.2, Math.min(2, targetZoom))
  const w = surfaceSize.width || 1280
  const h = surfaceSize.height || 800
  const center = viewportCenterWorld()
  const next: SpatialViewport = {
    x: w / 2 - center.x * clamped,
    y: h / 2 - center.y * clamped,
    zoom: clamped,
  }
  currentViewport.value = next
  await setViewport(next)
  camera(next)
}

function onCanvasWheel(event: WheelEvent) {
  if (!(event.ctrlKey || event.metaKey)) return
  event.preventDefault()
  const rect = surface.value?.getBoundingClientRect()
  if (!rect) return
  const px = event.clientX - rect.left
  const py = event.clientY - rect.top
  const oldZoom = currentViewport.value.zoom || 1
  const factor = event.deltaY < 0 ? 1.12 : 1 / 1.12
  const nextZoom = Math.max(0.2, Math.min(2, Number((oldZoom * factor).toFixed(3))))
  if (nextZoom === oldZoom) return
  const wx = (px - currentViewport.value.x) / oldZoom
  const wy = (py - currentViewport.value.y) / oldZoom
  const next: SpatialViewport = {
    x: px - wx * nextZoom,
    y: py - wy * nextZoom,
    zoom: nextZoom,
  }
  currentViewport.value = next
  void setViewport(next)
  camera(next)
}

function onSurfacePointerMove(event: PointerEvent) {
  const world = clientToWorld(event.clientX, event.clientY)
  board.sendPresence({ cursor: world, activeTool: activeTool.value })
}

async function fitItems(ids: string[]) {
  const session = sessionKey.value
  await nextTick()
  if (session !== sessionKey.value) return
  const items = ids
    .map((id) => allObjectRects.value[id] ?? uie.snapshot.value.space?.items[id])
    .filter((i): i is NonNullable<typeof i> => Boolean(i))
  const rect = surface.value?.getBoundingClientRect()
  if (!items.length || !rect?.width || !rect.height) return
  const side = inspectorOpen.value ? 390 : 0
  const bottom = composerCollapsed.value ? 64 : (composerDock.value?.getBoundingClientRect().height ?? 112) + 28
  const top = 104
  const leftInset = layersOpen.value ? 276 : 74
  const width = Math.max(120, rect.width - side - leftInset - 36)
  const height = Math.max(120, rect.height - bottom - top - 16)
  const x = Math.min(...items.map((i) => i.x))
  const y = Math.min(...items.map((i) => i.y))
  const w = Math.max(...items.map((i) => i.x + i.width)) - x
  const h = Math.max(...items.map((i) => i.y + i.height)) - y
  const zoom = Math.max(0.2, Math.min(1, width / Math.max(1, w), height / Math.max(1, h)))
  const viewport: SpatialViewport = {
    x: leftInset + (width - w * zoom) / 2 - x * zoom,
    y: top + (height - h * zoom) / 2 - y * zoom,
    zoom,
  }
  currentViewport.value = viewport
  await setViewport(viewport)
  if (session === sessionKey.value) camera(viewport)
}

async function panToPoint(wx: number, wy: number) {
  const w = surfaceSize.width || 1280
  const h = surfaceSize.height || 800
  const zoom = Math.max(0.65, currentViewport.value.zoom || 1)
  const next: SpatialViewport = { x: w / 2 - wx * zoom, y: h / 2 - wy * zoom, zoom }
  currentViewport.value = next
  await setViewport(next)
  camera(next)
}

function locate(id: string) {
  const object = objectMap.value[id]
  if (object?.runId) focusedRun.value = object.runId
  if (object || board.objectsById.value[id]) {
    setSelection([id])
    void fitItems([id])
  }
}

function overview() {
  zoomMenuOpen.value = false
  const allIds = [
    ...visibleObjects.value.map((o) => o.id),
    ...board.sortedObjects.value
      .filter((o) => o.kind !== 'drawing' && o.kind !== 'connector' && !hiddenByCollapsedSection.value.has(o.id))
      .map((o) => o.id),
  ]
  void fitItems(allIds)
}

function selectRun() {
  focusedInstance.value = null
  const ids =
    focusedRun.value === '*'
      ? visibleObjects.value.map((o) => o.id)
      : objects.value.filter((o) => o.runId === focusedRun.value).map((o) => o.id)
  void fitItems(ids)
}

function arrangeCurrent() {
  zoomMenuOpen.value = false
  const space = uie.snapshot.value.space
  if (!space || !focusedRun.value || focusedRun.value === '*') return
  commit(arrangeSpace(space, seeds.value, focusedRun.value))
  selectRun()
}

function wrapFocusedRunInSection() {
  zoomMenuOpen.value = false
  const runId = focusedRun.value
  const targetIds =
    runId && runId !== '*'
      ? objects.value.filter((o) => o.runId === runId && visibleIds.value.has(o.id)).map((o) => o.id)
      : visibleObjects.value.map((o) => o.id)
  if (!targetIds.length) return
  const runObj = runs.value.find((r) => r.runId === runId)
  const section = board.wrapSelectionInSection(targetIds, {
    title: runObj?.title || t('space.kind.run'),
    color: 'indigo',
    externalRects: workExternalRects.value,
  })
  if (section) {
    const resolvedRun = runId && runId !== '*' ? runId : (activeDigestRunId.value ?? null)
    if (resolvedRun) {
      board.updateObjectProps(section.id, { runId: resolvedRun })
    }
    setSelection([section.id])
  }
}

// ── Node Drag, Snap & Selection ───────────────────────────────────────────────

function setSelection(ids: string[]) {
  const unique = [...new Set(ids)]
  selectedIds.value = unique
  selectedId.value = unique.length > 0 ? unique[unique.length - 1]! : null
  board.sendPresence({ selection: unique })
  if (inspectorOpen.value && selectedId.value) {
    if (objectMap.value[selectedId.value]) {
      inspectorTab.value = 'details'
      if (!visitedDetails.value.includes(selectedId.value)) visitedDetails.value.push(selectedId.value)
    } else if (board.objectsById.value[selectedId.value]) {
      inspectorTab.value = 'style'
    }
  }
}

function clearSelection() {
  setSelection([])
  connectorSourceId.value = null
}

function onNodesChange(changes: NodeChange[]) {
  applyNodeChanges(changes)
  const selectChanges = changes.filter((ch): ch is Extract<NodeChange, { type: 'select' }> => ch.type === 'select')
  if (selectChanges.length > 0) {
    const next = new Set(selectedIds.value)
    for (const ch of selectChanges) {
      if (ch.selected) next.add(ch.id)
      else next.delete(ch.id)
    }
    setSelection([...next])
  }
}

function commit(changes: SpatialChange[]) {
  if (changes.length) {
    uie.dispatch({
      command: { type: 'spaceMove', scope: uie.scope.value, changes },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
  }
}

function onNodeDragStart() {
  gesturing.value = true
  dragGuides.value = []
}

function onNodeDrag(event: NodeDragEvent) {
  const dragged = event.node
  if (!dragged) return
  const draggingIds = new Set((event.nodes ?? [dragged]).map((n) => n.id))
  const rect = allObjectRects.value[dragged.id]
  const moving: SpatialRect = {
    id: dragged.id,
    x: dragged.position.x,
    y: dragged.position.y,
    width: dragged.dimensions?.width || rect?.width || 240,
    height: dragged.dimensions?.height || rect?.height || 160,
  }
  const others: SpatialRect[] = []
  for (const [id, r] of Object.entries(allObjectRects.value)) {
    if (draggingIds.has(id)) continue
    others.push({ id, x: r.x, y: r.y, width: r.width, height: r.height })
  }
  const snapped = computeSmartGuides(moving, others, 6)
  dragGuides.value = snapped.guides
}

function dragStop(event: { nodes: GraphNode[] }) {
  const movedNodes = event.nodes ?? []
  const movingIds = new Set(movedNodes.map((n) => n.id))
  const otherRects: SpatialRect[] = Object.entries(allObjectRects.value)
    .filter(([id]) => !movingIds.has(id))
    .map(([id, r]) => ({ id, x: r.x, y: r.y, width: r.width, height: r.height }))

  const workNodes = movedNodes.filter((n) => Boolean(objectMap.value[n.id]))
  if (workNodes.length > 0) {
    const changes: SpatialChange[] = workNodes.map((n) => {
      const baseRect = allObjectRects.value[n.id]
      const snap = computeSmartGuides(
        {
          id: n.id,
          x: n.position.x,
          y: n.position.y,
          width: n.dimensions?.width || baseRect?.width || 320,
          height: n.dimensions?.height || baseRect?.height || 180,
        },
        otherRects,
        6,
      )
      return { id: n.id, x: snap.x, y: snap.y }
    })
    commit(changes)
  }

  const wbNodes = movedNodes.filter((n) => Boolean(board.objectsById.value[n.id]))
  if (wbNodes.length > 0) {
    board.moveObjectsWithSnap(
      wbNodes.map((n) => ({ id: n.id, x: n.position.x, y: n.position.y })),
      { extraRects: otherRects, snap: true, moveSectionChildren: true },
    )
  }

  dragGuides.value = []
  board.clearActiveGuides()
  gesturing.value = false
}

function resizeStop(id: string, params: { x: number; y: number; width: number; height: number }) {
  if (objectMap.value[id]) {
    commit([{ id, ...params }])
  } else if (board.objectsById.value[id]) {
    board.updateObjectProps(id, {}, params)
  }
  dragGuides.value = []
  board.clearActiveGuides()
  gesturing.value = false
}

// ── Tool Dock Actions & Canvas Creation ───────────────────────────────────────

function selectTool(tool: SpatialToolMode) {
  activeTool.value = tool
  stickyPaletteOpen.value = tool === 'sticky'
  drawPopoverOpen.value = tool === 'pen' || tool === 'highlighter' || tool === 'eraser'
  if (tool !== 'connector') connectorSourceId.value = null
  if (tool !== 'comment') activePinId.value = null
  board.sendPresence({ activeTool: tool })
  if (tool === 'media') {
    void triggerInsertMedia()
  }
}

function onPaneClick(event: MouseEvent) {
  zoomMenuOpen.value = false
  stickyPaletteOpen.value = false
  drawPopoverOpen.value = false
  activePinId.value = null

  if (activeTool.value === 'select') {
    clearSelection()
    return
  }

  const pos = clientToWorld(event.clientX, event.clientY)
  if (activeTool.value === 'section') {
    const created = board.createSection({
      x: Math.round(pos.x - SPATIAL_WHITEBOARD_DEFAULTS.section.width / 2),
      y: Math.round(pos.y - SPATIAL_WHITEBOARD_DEFAULTS.section.height / 2),
      title: '',
      color: 'neutral',
    })
    setSelection([created.id])
    activeTool.value = 'select'
    return
  }

  if (activeTool.value === 'sticky') {
    const created = board.createSticky({
      x: Math.round(pos.x - SPATIAL_WHITEBOARD_DEFAULTS.sticky.width / 2),
      y: Math.round(pos.y - SPATIAL_WHITEBOARD_DEFAULTS.sticky.height / 2),
      tone: activeStickyTone.value,
      text: '',
    })
    setSelection([created.id])
    activeTool.value = 'select'
    return
  }

  if (activeTool.value === 'text') {
    const created = board.createText({
      x: Math.round(pos.x - SPATIAL_WHITEBOARD_DEFAULTS.text.width / 2),
      y: Math.round(pos.y - SPATIAL_WHITEBOARD_DEFAULTS.text.height / 2),
      scale: 'md',
      text: '',
    })
    setSelection([created.id])
    activeTool.value = 'select'
    return
  }

  if (activeTool.value === 'board') {
    const created = board.createBoardWidget({
      x: Math.round(pos.x - SPATIAL_WHITEBOARD_DEFAULTS.board.width / 2),
      y: Math.round(pos.y - SPATIAL_WHITEBOARD_DEFAULTS.board.height / 2),
      props: {
        project_id: resolvedSpatialProjectId.value,
        layout: 'kanban',
      },
    })
    setSelection([created.id])
    activeTool.value = 'select'
    return
  }

  if (activeTool.value === 'connector') {
    connectorSourceId.value = null
  }
}

function onNodeClick({ node, event }: NodeMouseEvent) {
  zoomMenuOpen.value = false
  if (activeTool.value === 'connector') {
    if (!connectorSourceId.value) {
      connectorSourceId.value = node.id
      setSelection([node.id])
      return
    }
    if (connectorSourceId.value !== node.id) {
      const created = board.createConnector({
        sourceId: connectorSourceId.value,
        targetId: node.id,
        curve: 'smoothstep',
      })
      connectorSourceId.value = null
      setSelection([created.id])
      activeTool.value = 'select'
    }
    return
  }

  const mouseEvent = event as MouseEvent | undefined
  if (mouseEvent?.shiftKey || mouseEvent?.metaKey || mouseEvent?.ctrlKey) {
    const next = new Set(selectedIds.value)
    if (next.has(node.id)) next.delete(node.id)
    else next.add(node.id)
    setSelection([...next])
  } else {
    setSelection([node.id])
  }
}

function onConnect(conn: Connection) {
  if (!conn.source || !conn.target || conn.source === conn.target) return
  const created = board.createConnector({
    sourceId: conn.source,
    targetId: conn.target,
    sourceHandle: conn.sourceHandle ?? null,
    targetHandle: conn.targetHandle ?? null,
    curve: 'smoothstep',
  })
  setSelection([created.id])
}

// ── Media / File Insertion (Unified #43 pickHostFiles + Drag-Drop + Paste) ────

function inferMediaKind(filePath: string, mimeType?: string): SpatialMediaKind {
  if (mimeType?.startsWith('image/')) return 'image'
  const lower = filePath.toLowerCase()
  const dot = lower.lastIndexOf('.')
  const ext = dot >= 0 ? lower.slice(dot) : ''
  return IMAGE_EXTENSIONS.has(ext) ? 'image' : 'file'
}

function inferLanguage(filePath: string): string | undefined {
  const dot = filePath.lastIndexOf('.')
  if (dot < 0) return undefined
  const ext = filePath.slice(dot + 1).toLowerCase()
  return ext && ext.length <= 8 ? ext : undefined
}

async function insertHostFilePath(filePath: string, worldPos?: { x: number; y: number }) {
  const center = worldPos ?? viewportCenterWorld()
  const kind = inferMediaKind(filePath)
  const normalized = filePath.replace(/\\/g, '/')
  const title = normalized.split('/').filter(Boolean).pop() || filePath

  if (kind === 'image') {
    const url = `${resolveWebGatewayBase()}/api/v1/fs/media?token=${encodeMediaPathToken(normalized)}`
    const tile = board.createMediaTile({
      x: Math.round(center.x - SPATIAL_WHITEBOARD_DEFAULTS.mediaImage.width / 2),
      y: Math.round(center.y - SPATIAL_WHITEBOARD_DEFAULTS.mediaImage.height / 2),
      mediaKind: 'image',
      path: filePath,
      url,
      title,
    })
    setSelection([tile.id])
    return
  }

  let snippet: string | undefined
  const cwd = projectPath.value || git.state.value.cwd
  if (cwd && typeof api?.readFile === 'function') {
    try {
      const res = await api.readFile(cwd, filePath, { start_row: 1, end_row: 40 })
      const data = res?.data as ReadFileDataDto | undefined
      if (res?.status !== 'failed' && data?.success !== false) {
        snippet = readFileText(data)
      }
    } catch {
      // Media node also lazily fetches snippet if needed.
    }
  }

  const tile = board.createMediaTile({
    x: Math.round(center.x - SPATIAL_WHITEBOARD_DEFAULTS.mediaFile.width / 2),
    y: Math.round(center.y - SPATIAL_WHITEBOARD_DEFAULTS.mediaFile.height / 2),
    mediaKind: 'file',
    path: filePath,
    title,
    snippet,
    language: inferLanguage(filePath),
  })
  setSelection([tile.id])
}

async function triggerInsertMedia() {
  activeTool.value = 'select'
  const picked = await pickHostFiles({
    title: t('space.figma.toolMedia'),
    multiple: true,
    initialPath: projectPath.value || undefined,
  })
  if (!picked.length) return
  const base = viewportCenterWorld()
  for (let i = 0; i < picked.length; i++) {
    await insertHostFilePath(picked[i]!, { x: base.x + i * 32, y: base.y + i * 32 })
  }
}

function readBlobAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result ?? ''))
    reader.onerror = () => reject(reader.error)
    reader.readAsDataURL(file)
  })
}

async function insertDroppedFiles(files: FileList | File[], worldPos: { x: number; y: number }) {
  const list = Array.from(files)
  for (let i = 0; i < list.length; i++) {
    const file = list[i]!
    const offset = { x: worldPos.x + i * 28, y: worldPos.y + i * 28 }
    const nativePath = (file as File & { path?: string }).path
    if (nativePath) {
      await insertHostFilePath(nativePath, offset)
      continue
    }
    const kind = inferMediaKind(file.name, file.type)
    if (kind === 'image') {
      try {
        const dataUrl = await readBlobAsDataUrl(file)
        const tile = board.createMediaTile({
          x: Math.round(offset.x - SPATIAL_WHITEBOARD_DEFAULTS.mediaImage.width / 2),
          y: Math.round(offset.y - SPATIAL_WHITEBOARD_DEFAULTS.mediaImage.height / 2),
          mediaKind: 'image',
          path: file.name,
          url: dataUrl,
          title: file.name,
        })
        setSelection([tile.id])
      } catch {
        // Ignore unreadable image blobs.
      }
    } else {
      try {
        const text = (await file.text()).split(/\r?\n/).slice(0, 40).join('\n')
        const tile = board.createMediaTile({
          x: Math.round(offset.x - SPATIAL_WHITEBOARD_DEFAULTS.mediaFile.width / 2),
          y: Math.round(offset.y - SPATIAL_WHITEBOARD_DEFAULTS.mediaFile.height / 2),
          mediaKind: 'file',
          path: file.name,
          title: file.name,
          snippet: text,
          language: inferLanguage(file.name),
        })
        setSelection([tile.id])
      } catch {
        // Ignore unreadable files.
      }
    }
  }
}

function dropLinkedTaskStickyAtClientPos(
  task: { task_id: string; task_key: string; title: string },
  clientX: number,
  clientY: number,
) {
  const now = Date.now()
  if (
    lastDroppedTaskGuard &&
    lastDroppedTaskGuard.key === task.task_id &&
    now - lastDroppedTaskGuard.at < 350
  ) {
    return
  }
  lastDroppedTaskGuard = { key: task.task_id, at: now }
  const pos = clientToWorld(clientX, clientY)
  const created = board.createSticky({
    x: Math.round(pos.x - SPATIAL_WHITEBOARD_DEFAULTS.sticky.width / 2),
    y: Math.round(pos.y - SPATIAL_WHITEBOARD_DEFAULTS.sticky.height / 2),
    tone: 'slate',
    text: task.title,
    board_task_id: task.task_id,
    board_task_key: task.task_key,
  })
  setSelection([created.id])
}

function onCanvasDrop(event: DragEvent) {
  dragOverCanvas.value = false
  const dt = event.dataTransfer
  if (!dt) return

  // If a BoardTaskCard is dragged out onto the canvas (outside any embedded board widget), drop a linked sticky.
  const targetEl = event.target as HTMLElement | null
  const insideBoardWidget = Boolean(targetEl?.closest?.('.spatial-board-node'))
  if (!insideBoardWidget) {
    const rawBoardJson = dt.getData('application/x-tinadec-board-task-json')
    if (rawBoardJson) {
      try {
        const parsed = JSON.parse(rawBoardJson) as {
          task_id?: string
          task_key?: string
          title?: string
        }
        if (parsed.task_id && parsed.task_key) {
          dropLinkedTaskStickyAtClientPos(
            {
              task_id: parsed.task_id,
              task_key: parsed.task_key,
              title: parsed.title ?? parsed.task_key,
            },
            event.clientX,
            event.clientY,
          )
          return
        }
      } catch {
        // Fallback below.
      }
    }
    const rawTaskId = dt.getData('text/x-tinadec-board-task')
    if (rawTaskId) {
      const matched = boardTasksById.value.get(rawTaskId)
      if (matched) {
        dropLinkedTaskStickyAtClientPos(matched, event.clientX, event.clientY)
        return
      }
    }
  }

  const pos = clientToWorld(event.clientX, event.clientY)
  if (dt.files?.length) {
    void insertDroppedFiles(dt.files, pos)
  }
}

function onWindowPaste(event: ClipboardEvent) {
  const target = event.target as HTMLElement | null
  if (target?.closest('input,textarea,select,[contenteditable=true]')) return
  const files = event.clipboardData?.files
  if (!files?.length) return
  event.preventDefault()
  void insertDroppedFiles(files, viewportCenterWorld())
}

function onMediaOpenFile(payload: { id: string; path: string; mediaKind: SpatialMediaKind }) {
  if (!payload.path) return
  const cwd = projectPath.value || git.state.value.cwd || undefined
  if (payload.mediaKind === 'file') {
    const opened = openInEditor({ path: payload.path, cwd }) || openDocument({ path: payload.path, cwd })
    if (!opened && selectedIds.value.length === 0) setSelection([payload.id])
  } else {
    setSelection([payload.id])
    inspectorTab.value = 'style'
    inspectorOpen.value = true
  }
}

// ── Multi-Selection Bar Actions ───────────────────────────────────────────────

function onAlignSelected(mode: SpatialAlignMode) {
  const aligned = board.alignSelected(selectedIds.value, mode, workExternalRects.value)
  const workMoves = aligned.filter((item) => Boolean(objectMap.value[item.id]))
  if (workMoves.length) commit(workMoves)
}

function onDistributeSelected(axis: SpatialDistributeAxis) {
  const distributed = board.distributeSelected(selectedIds.value, axis, workExternalRects.value)
  const workMoves = distributed.filter((item) => Boolean(objectMap.value[item.id]))
  if (workMoves.length) commit(workMoves)
}

function onWrapSelectionInSection() {
  const section = board.wrapSelectionInSection(selectedIds.value, {
    title: '',
    color: 'neutral',
    externalRects: workExternalRects.value,
  })
  if (section) {
    const selectedRuns = new Set(
      selectedIds.value
        .map((id) => objectMap.value[id]?.runId)
        .filter((rid): rid is string => Boolean(rid)),
    )
    if (selectedRuns.size === 1) {
      board.updateObjectProps(section.id, { runId: [...selectedRuns][0]! })
    }
    setSelection([section.id])
  }
}

function onDuplicateSelected() {
  const duplicated = board.duplicateObjects(selectedIds.value)
  if (duplicated.length) setSelection(duplicated.map((d) => d.id))
}

function onDeleteSelected() {
  const wbIds = selectedIds.value.filter((id) => Boolean(board.objectsById.value[id]))
  if (wbIds.length) board.deleteObjects(wbIds)
  clearSelection()
}

// ── Phase 2 Spatial <-> Project Board Bridge Actions ──────────────────────────

const selectedPromotableIds = computed(() =>
  selectedIds.value.filter((id) => {
    const kind = board.objectsById.value[id]?.kind
    return kind === 'sticky' || kind === 'text' || kind === 'section'
  }),
)

async function onPromoteSelectionToBoard(payload: SpatialPromoteToBoardEvent) {
  const pid = resolvedSpatialProjectId.value
  if (!pid || !payload.objectIds.length || promotingToBoard.value) return
  promotingToBoard.value = true
  try {
    const res = await spatialProjectBoard.promoteFromSpatial(pid, {
      session_id: sessionKey.value,
      object_ids: payload.objectIds,
      mode: payload.mode,
      target_status: payload.targetStatus,
      viewport_hint: {
        x: currentViewport.value.x,
        y: currentViewport.value.y,
        zoom: currentViewport.value.zoom,
      },
      writer_id: board.clientId,
    })
    for (const link of res.updated_object_links) {
      if (board.objectsById.value[link.object_id]) {
        board.updateObjectProps(link.object_id, {
          board_task_id: link.task_id,
          board_task_key: link.task_key,
        })
      }
    }
    notify.success(t('space.boardBridge.promotedToast', { count: res.tasks.length }))
  } catch (err) {
    notify.error(err, { title: t('board.errors.saveFailed') })
  } finally {
    promotingToBoard.value = false
  }
}

function openSpatialBoardCreateDrawer(payload: { projectId: string; status: ProjectBoardTaskStatus }) {
  boardDrawerMode.value = 'create'
  boardDrawerTaskId.value = null
  boardDrawerFallbackTask.value = null
  boardDrawerDefaultProjectId.value = payload.projectId || resolvedSpatialProjectId.value
  boardDrawerDefaultStatus.value = payload.status
  boardDrawerOpen.value = true
}

function openSpatialBoardTaskDrawer(task: ProjectBoardTaskDto) {
  boardDrawerMode.value = 'edit'
  boardDrawerTaskId.value = task.task_id
  boardDrawerFallbackTask.value = task
  boardDrawerDefaultProjectId.value = task.project_id
  boardDrawerOpen.value = true
}

async function onOpenCapsuleBoardTask(payload: {
  taskId: string | null
  taskKey: string | null
  task: ProjectBoardTaskDto | null
}) {
  if (payload.task) {
    openSpatialBoardTaskDrawer(payload.task)
    return
  }
  const byId = payload.taskId ? boardTasksById.value.get(payload.taskId) : undefined
  if (byId) {
    openSpatialBoardTaskDrawer(byId)
    return
  }
  const byKey = payload.taskKey ? boardTasksByKey.value.get(payload.taskKey) : undefined
  if (byKey) {
    openSpatialBoardTaskDrawer(byKey)
    return
  }
  if (payload.taskId && resolvedSpatialProjectId.value) {
    try {
      const fetched = await api.getProjectBoardTask(resolvedSpatialProjectId.value, payload.taskId)
      spatialProjectBoard.upsertTaskLocal(fetched)
      openSpatialBoardTaskDrawer(fetched)
    } catch (err) {
      notify.error(err, { title: t('board.errors.saveFailed') })
    }
  }
}

async function handleCreateBoardTask(projectId: string, input: CreateProjectBoardTaskInput) {
  try {
    const pid = projectId || resolvedSpatialProjectId.value || ''
    if (!pid) return
    const created = await spatialProjectBoard.createTask(pid, input)
    boardDrawerTaskId.value = created.task_id
    boardDrawerFallbackTask.value = created
    boardDrawerMode.value = 'edit'
  } catch (err) {
    notify.error(err, { title: t('board.errors.saveFailed') })
  }
}

async function handleSaveBoardTask(projectId: string, taskId: string, patch: UpdateProjectBoardTaskInput) {
  try {
    const saved = await spatialProjectBoard.updateTask(projectId, taskId, patch)
    boardDrawerFallbackTask.value = saved
  } catch (err) {
    notify.error(err, { title: t('board.errors.saveFailed') })
  }
}

async function handleDeleteBoardTask(projectId: string, taskId: string) {
  try {
    await spatialProjectBoard.deleteTask(projectId, taskId)
    boardDrawerOpen.value = false
    boardDrawerTaskId.value = null
    boardDrawerFallbackTask.value = null
  } catch (err) {
    notify.error(err, { title: t('board.errors.deleteFailed') })
  }
}

async function handleResolveBoardConflict(
  projectId: string,
  taskId: string,
  resolution: ProjectBoardConflictResolution,
) {
  try {
    const resolved = await spatialProjectBoard.resolveConflict(projectId, taskId, resolution)
    boardDrawerFallbackTask.value = resolved
  } catch (err) {
    notify.error(err, { title: t('board.errors.conflictResolveFailed') })
  }
}

async function handleDispatchBoardTask(
  projectId: string,
  taskId: string,
  input: DispatchProjectBoardTaskInput,
) {
  try {
    const dispatched = await spatialProjectBoard.dispatchTask(projectId, taskId, input)
    boardDrawerFallbackTask.value = dispatched
  } catch (err) {
    notify.error(err, { title: t('board.errors.dispatchFailed') })
  }
}

async function handleCancelBoardTask(projectId: string, taskId: string) {
  try {
    const cancelled = await spatialProjectBoard.cancelTask(projectId, taskId)
    boardDrawerFallbackTask.value = cancelled
  } catch (err) {
    notify.error(err, { title: t('board.errors.cancelFailed') })
  }
}

async function handleApproveBoardTask(
  projectId: string,
  taskId: string,
  input: ApproveProjectBoardTaskInput,
) {
  try {
    const approved = await spatialProjectBoard.approveTask(projectId, taskId, input)
    boardDrawerFallbackTask.value = approved
  } catch (err) {
    notify.error(err, { title: t('board.errors.approveFailed') })
  }
}

async function handleRequestBoardChanges(projectId: string, taskId: string, feedback: string) {
  try {
    const updated = await spatialProjectBoard.requestChanges(projectId, taskId, feedback)
    boardDrawerFallbackTask.value = updated
  } catch (err) {
    notify.error(err, { title: t('board.errors.requestChangesFailed') })
  }
}

async function handleOpenBoardSession(sessionId: string) {
  boardDrawerOpen.value = false
  await c.setSelectedSession(sessionId)
  if (optionalRouter) {
    await optionalRouter.push('/')
  }
}

async function focusSpatialObjectWithHint(
  objectId: string,
  viewportHint?: { x?: unknown; y?: unknown; zoom?: unknown } | Record<string, unknown> | null,
): Promise<boolean> {
  if (!objectId) return false
  await nextTick()
  const bounds = allObjectRects.value[objectId] ?? null
  const nextVp = computeSpatialFocusViewport({
    bounds,
    viewportHint,
    surfaceWidth: surfaceSize.width,
    surfaceHeight: surfaceSize.height,
    currentZoom: currentViewport.value.zoom,
  })
  if (!bounds && !nextVp) return false
  setSelection([objectId])
  triggerFocusPulse(objectId)
  if (nextVp) {
    currentViewport.value = nextVp
    await setViewport(nextVp)
    camera(nextVp)
  }
  return Boolean(bounds || nextVp)
}

const pendingFocusObjectId = ref<string | null>(null)
const pendingFocusViewportHint = ref<Record<string, unknown> | null>(null)

async function applyPendingOrRouteSpatialFocus() {
  const querySessionId =
    typeof optionalRoute?.query?.sessionId === 'string' ? optionalRoute.query.sessionId.trim() : ''
  const queryFocusObjectId =
    typeof optionalRoute?.query?.focusObjectId === 'string'
      ? optionalRoute.query.focusObjectId.trim()
      : ''

  const stashed = consumePendingSpatialFocus(querySessionId || undefined)
  const targetSessionId = querySessionId || stashed?.session_id || ''
  const targetObjectId = queryFocusObjectId || stashed?.object_id || ''
  const hint = (stashed?.viewport_hint as Record<string, unknown> | null | undefined) ?? null

  if (!targetSessionId && !targetObjectId) return

  if (targetSessionId && c.selectedSessionId.value !== targetSessionId) {
    await c.setSelectedSession(targetSessionId)
    await nextTick()
  }

  if (targetObjectId) {
    const focused = await focusSpatialObjectWithHint(targetObjectId, hint)
    if (!focused) {
      pendingFocusObjectId.value = targetObjectId
      pendingFocusViewportHint.value = hint
    } else {
      pendingFocusObjectId.value = null
      pendingFocusViewportHint.value = null
    }
  }

  if (optionalRouter && optionalRoute && (querySessionId || queryFocusObjectId)) {
    const nextQuery = { ...optionalRoute.query }
    delete nextQuery.focusObjectId
    await optionalRouter.replace({ path: optionalRoute.path, query: nextQuery }).catch(() => undefined)
  }
}

watch(
  () => [optionalRoute?.query?.sessionId, optionalRoute?.query?.focusObjectId, ready.value] as const,
  ([, , isReady]) => {
    if (isReady) {
      void applyPendingOrRouteSpatialFocus()
    }
  },
)

watch(
  () => Object.keys(allObjectRects.value).length,
  () => {
    if (!pendingFocusObjectId.value) return
    const targetId = pendingFocusObjectId.value
    if (allObjectRects.value[targetId]) {
      pendingFocusObjectId.value = null
      const hint = pendingFocusViewportHint.value
      pendingFocusViewportHint.value = null
      void focusSpatialObjectWithHint(targetId, hint)
    }
  },
)

async function handleJumpSpatialFromBoard(task: ProjectBoardTaskDto) {
  if (!task.spatial_ref) return
  boardDrawerOpen.value = false
  if (task.spatial_ref.session_id === sessionKey.value || task.spatial_ref.session_id === c.selectedSessionId.value) {
    await focusSpatialObjectWithHint(
      task.spatial_ref.object_id,
      (task.spatial_ref.viewport_hint as Record<string, unknown> | null | undefined) ?? null,
    )
    return
  }
  stashPendingSpatialFocus(task.spatial_ref)
  await c.setSelectedSession(task.spatial_ref.session_id)
  if (optionalRouter) {
    await optionalRouter.push(buildSpatialFocusLocation(task.spatial_ref))
  } else {
    await focusSpatialObjectWithHint(
      task.spatial_ref.object_id,
      (task.spatial_ref.viewport_hint as Record<string, unknown> | null | undefined) ?? null,
    )
  }
}

// ── Drawing & Comments ────────────────────────────────────────────────────────

function onStrokeComplete(stroke: SpatialDrawingProps) {
  board.addDrawingStroke(stroke)
}

function onStrokeErase(id: string) {
  board.eraseDrawingStroke(id)
}

async function onCreateCommentPin(payload: { x: number; y: number; body: string }) {
  const created = await board.addCommentPin(payload)
  activePinId.value = created.id
  activeTool.value = 'select'
}

async function onReplyCommentPin(payload: { pinId: string; body: string }) {
  await board.replyCommentPin(payload.pinId, payload.body)
}

async function onToggleResolvePin(payload: { pinId: string; resolved: boolean }) {
  await board.toggleResolveCommentPin(payload.pinId, payload.resolved)
}

async function onDeleteCommentPin(pinId: string) {
  if (activePinId.value === pinId) activePinId.value = null
  await board.deleteCommentPin(pinId)
}

// ── Composer Expand / Collapse & Team Dispatch ────────────────────────────────

function expandComposerAndFocus() {
  composerCollapsed.value = false
  void nextTick(() => {
    const textarea = composerDock.value?.querySelector<HTMLTextAreaElement>('textarea')
    textarea?.focus()
  })
}

function describeObjectForContext(id: string): SpatialSelectionContextItem | null {
  const work = objectMap.value[id]
  if (work) {
    return {
      id: work.id,
      kind: work.kind,
      label: work.title || t(`space.kind.${work.kind}`),
      snippet: work.body || work.status || work.owner,
    }
  }
  const wb = board.objectsById.value[id]
  if (wb) {
    const label =
      typeof wb.props.title === 'string' && wb.props.title.trim()
        ? wb.props.title.trim()
        : typeof wb.props.text === 'string' && wb.props.text.trim()
          ? wb.props.text.trim().slice(0, 60)
          : typeof wb.props.label === 'string' && wb.props.label.trim()
            ? wb.props.label.trim()
            : wb.kind
    const snippet =
      typeof wb.props.text === 'string'
        ? wb.props.text
        : typeof wb.props.path === 'string'
          ? wb.props.path
          : undefined
    return { id: wb.id, kind: wb.kind, label, snippet }
  }
  return null
}

function sendPromptToTeam(content: string) {
  const trimmed = content.trim()
  if (!trimmed) return
  if (typeof c.updateDraft === 'function') c.updateDraft(trimmed)
  const settings = c.composerSettings.value
  if (hero.value && typeof c.handleWelcomeSend === 'function') {
    void c.handleWelcomeSend({
      content: trimmed,
      permission_mode: c.currentPermission.value,
      mode_version_id: settings.mode_version_id ?? null,
      meeting_model_override: settings.meeting_model_override ?? null,
      space_options: settings.space_options ?? null,
    })
  } else if (typeof c.sendMessage === 'function') {
    void c.sendMessage({
      dispatch_mode: 'parallel',
      target_run_id: null,
      mode_version_id: settings.mode_version_id ?? null,
      meeting_model_override: settings.meeting_model_override ?? null,
      space_options: settings.space_options ?? null,
    })
  }
}

function dispatchSelectionToTeam(ids: string[]) {
  const contextItems = ids
    .map((id) => describeObjectForContext(id))
    .filter((item): item is SpatialSelectionContextItem => item !== null)
  if (!contextItems.length) return
  const existingDraft = c.draft.value.trim()
  const prompt = buildSpatialSelectionPrompt(contextItems, existingDraft)
  composerCollapsed.value = false
  if (existingDraft) {
    sendPromptToTeam(prompt)
    clearSelection()
  } else {
    if (typeof c.updateDraft === 'function') c.updateDraft(`${prompt}\n\n`)
    expandComposerAndFocus()
  }
}

function dispatchCommentPin(pin: SpatialCommentPin) {
  const replyLines = pin.replies.map((r) => `  - ${r.authorName}: ${r.body}`)
  const summary = [
    `[${t('space.figma.commentDispatchPrefix')}] ${pin.authorName}: ${pin.body}`,
    ...replyLines,
  ].join('\n')
  composerCollapsed.value = false
  sendPromptToTeam(summary)
  activePinId.value = null
}

// ── Details, Unified Inspector, Blockers & Plan Focus ─────────────────────────

function openDetails(id: string) {
  if (!objectMap.value[id]) return
  selectedId.value = id
  if (!selectedIds.value.includes(id)) setSelection([id])
  inspectorTab.value = 'details'
  inspectorOpen.value = true
  terminalVisible.value = false
  if (!visitedDetails.value.includes(id)) visitedDetails.value.push(id)
}

function closeInspector() {
  inspectorOpen.value = false
  surface.value?.querySelector<HTMLButtonElement>(`[data-open-object="${CSS.escape(selectedId.value ?? '')}"]`)?.focus()
}

function togglePlanInspector() {
  if (inspectorOpen.value && inspectorTab.value === 'plan') {
    inspectorOpen.value = false
  } else {
    inspectorTab.value = 'plan'
    inspectorOpen.value = true
    terminalVisible.value = false
  }
}

function toggleLiveDigestInspector() {
  if (inspectorOpen.value && inspectorTab.value === 'digest') {
    inspectorOpen.value = false
  } else {
    inspectorTab.value = 'digest'
    inspectorOpen.value = true
    terminalVisible.value = false
  }
}

function openLiveDigestForRun(runId?: string | null) {
  if (runId && runId !== '*') {
    focusedRun.value = runId
  }
  inspectorTab.value = 'digest'
  inspectorOpen.value = true
  terminalVisible.value = false
}

function openTeammateSteer(target: TeammateSteerTarget & { initialDraft?: string }) {
  if (!target.runId || !target.handle) return
  steerTarget.value = target
  steerOpen.value = true
}

function onPresenceSteerTeammate(payload: SpatialTeammateSteerEvent) {
  const runId = payload.runId || activeDigestRunId.value || ''
  if (!runId || !payload.handle) return
  openTeammateSteer({
    runId,
    handle: payload.handle,
    displayName: payload.displayName,
    status: payload.status,
    paused: payload.paused,
    taskSummary: payload.taskSummary,
    thought: payload.thought,
    anchorRect: payload.anchorRect,
  })
}

function onTeammateStatusChange(payload: { handle: string; status: string; paused: boolean }) {
  const clean = payload.handle.trim().replace(/^@/, '')
  if (!clean) return
  pausedTeammates.value = {
    ...pausedTeammates.value,
    [clean]: payload.paused,
  }
}

function onFillSteerFromDigest(payload: SpatialDigestFillSteerEvent) {
  const prompt = payload.prompt.trim()
  if (!prompt) return
  if (typeof c.updateDraft === 'function') c.updateDraft(prompt)
  expandComposerAndFocus()
}

function extractEventAnchorRect(event?: Event): TeammateSteerAnchorRect | null {
  const el = (event?.currentTarget as HTMLElement | null) ?? null
  const rect = el?.getBoundingClientRect?.()
  if (!rect || (rect.width === 0 && rect.height === 0)) return null
  return {
    top: rect.top,
    left: rect.left,
    width: rect.width,
    height: rect.height,
    right: rect.right,
    bottom: rect.bottom,
  }
}

function openSelectedObjectSteer(event?: Event) {
  const obj = selectedObject.value
  const runId = obj?.runId || activeDigestRunId.value || ''
  const handle = obj?.owner?.trim() || ''
  if (!obj || !runId || !handle) return
  const thought = liveDigest.teammateThoughtByHandle.value[handle]
  openTeammateSteer({
    runId,
    handle,
    displayName: thought?.display_name || handle,
    status: thought?.status ?? obj.status ?? null,
    paused: Boolean(pausedTeammates.value[handle] ?? (thought?.status === 'paused')),
    taskSummary: thought?.current_task ?? obj.title ?? null,
    thought: thought?.thought ?? null,
    anchorRect: extractEventAnchorRect(event),
  })
}

function openUnifiedInspector() {
  zoomMenuOpen.value = false
  if (selectedWhiteboardObject.value) inspectorTab.value = 'style'
  else if (selectedObject.value) inspectorTab.value = 'details'
  else inspectorTab.value = 'plan'
  inspectorOpen.value = true
  terminalVisible.value = false
}

function locateBlocker() {
  const blocker = objects.value.find(
    (o) =>
      (!o.runId || focusedRun.value === '*' || o.runId === focusedRun.value) &&
      (o.waitingFor?.length || ['blocked', 'failed', 'waiting_approval', 'awaiting_user', 'awaiting_approval'].includes(o.status ?? '')),
  )
  if (blocker) {
    openDetails(blocker.id)
    locate(blocker.id)
  } else if (pendingApprovals.value.length) {
    const card = objects.value.find((o) => o.kind === 'approval')
    if (card) {
      openDetails(card.id)
      locate(card.id)
    }
  }
}

const hasBlocker = computed(
  () =>
    pendingApprovals.value.length > 0 ||
    objects.value.some(
      (o) =>
        (!o.runId || focusedRun.value === '*' || o.runId === focusedRun.value) &&
        (o.waitingFor?.length || ['blocked', 'failed', 'waiting_approval', 'awaiting_user', 'awaiting_approval'].includes(o.status ?? '')),
    ),
)

function focusPlanNode(target: { taskKey?: string | null; agentSlug?: string | null }) {
  const taskKey = target.taskKey?.trim()
  const agentSlug = target.agentSlug?.trim()
  const hit = objects.value.find((o) => {
    const title = o.title ?? ''
    if (taskKey && (o.taskId === taskKey || o.id === taskKey || title.includes(taskKey))) return true
    if (agentSlug && (o.owner?.includes(agentSlug) || title.includes(agentSlug))) return true
    return false
  })
  if (hit) {
    openDetails(hit.id)
    locate(hit.id)
  }
}

function onPresenceAvatarClick(collab: SpatialCollaboratorLike, event?: Event) {
  if (collab.role === 'ai') {
    const runId = collab.runId || activeDigestRunId.value || ''
    const handle = (collab.handle || collab.displayName || '').trim()
    if (runId && handle) {
      openTeammateSteer({
        runId,
        handle,
        displayName: collab.displayName,
        status: collab.status ?? null,
        paused: Boolean(collab.paused),
        taskSummary: collab.currentTask ?? null,
        thought: collab.thought ?? null,
        anchorRect: extractEventAnchorRect(event),
      })
      const targetObjectId = collab.selection?.[0]
      if (targetObjectId) locate(targetObjectId)
      return
    }
  }
  const isRemote = board.collaborators.value.some((c) => c.clientId === collab.clientId)
  if (isRemote) {
    board.toggleFollowCollaborator(collab.clientId)
    return
  }
  const targetObjectId = collab.selection?.[0]
  if (targetObjectId) locate(targetObjectId)
}

// ── Layers / Outline Filtering ────────────────────────────────────────────────

const normalizedLayersQuery = computed(() => layersQuery.value.trim().toLowerCase())

const filteredWorkLayers = computed(() => {
  const q = normalizedLayersQuery.value
  return objects.value.filter((o) => {
    if (!q) return true
    const label = (o.title || t(`space.kind.${o.kind}`)).toLowerCase()
    return label.includes(q) || (o.owner?.toLowerCase().includes(q) ?? false)
  })
})

const filteredWhiteboardLayers = computed(() => {
  const q = normalizedLayersQuery.value
  return board.sortedObjects.value.filter((o) => {
    if (o.kind === 'drawing') return false
    if (!q) return true
    const label = layerLabelForWhiteboard(o).toLowerCase()
    return label.includes(q) || o.kind.toLowerCase().includes(q)
  })
})

const filteredCommentLayers = computed(() => {
  const q = normalizedLayersQuery.value
  return board.comments.value.filter((p) => {
    if (!q) return true
    return p.body.toLowerCase().includes(q) || p.authorName.toLowerCase().includes(q)
  })
})

function layerLabelForWhiteboard(obj: SpatialDocObject): string {
  if (obj.kind === 'section') {
    return (typeof obj.props.title === 'string' && obj.props.title.trim()) || t('space.whiteboard.sectionUntitled')
  }
  if (obj.kind === 'sticky') {
    const text = typeof obj.props.text === 'string' ? obj.props.text.trim() : ''
    return text ? text.slice(0, 40) : t('space.figma.kindSticky')
  }
  if (obj.kind === 'text') {
    const text = typeof obj.props.text === 'string' ? obj.props.text.trim() : ''
    return text ? text.slice(0, 40) : t('space.figma.kindText')
  }
  if (obj.kind === 'media') {
    return (typeof obj.props.title === 'string' && obj.props.title.trim()) || t('space.figma.kindMedia')
  }
  if (obj.kind === 'connector') {
    return (typeof obj.props.label === 'string' && obj.props.label.trim()) || t('space.figma.kindConnector')
  }
  if ((obj.kind as string) === 'board') {
    return (typeof obj.props.title === 'string' && obj.props.title.trim()) || t('space.figma.kindBoard')
  }
  return obj.kind
}

function wbKindLabel(kind: SpatialDocObject['kind'] | 'board'): string {
  switch (kind) {
    case 'section': return t('space.figma.kindSection')
    case 'sticky': return t('space.figma.kindSticky')
    case 'text': return t('space.figma.kindText')
    case 'board': return t('space.figma.kindBoard')
    case 'media': return t('space.figma.kindMedia')
    case 'drawing': return t('space.figma.kindDrawing')
    case 'connector': return t('space.figma.kindConnector')
    default: return t('space.kind.task')
  }
}

// ── Keyboard Shortcuts (Figma-style single-key + Space pan + `/` composer) ───

function isEditableTarget(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null
  if (!el || typeof el.closest !== 'function') return false
  return Boolean(el.closest('input,textarea,select,[contenteditable=true]'))
}

function onDocumentKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    if (steerOpen.value) { steerOpen.value = false; return }
    if (zoomMenuOpen.value) { zoomMenuOpen.value = false; return }
    if (stickyPaletteOpen.value || drawPopoverOpen.value) {
      stickyPaletteOpen.value = false
      drawPopoverOpen.value = false
      return
    }
    if (activePinId.value) { activePinId.value = null; return }
    if (activeTool.value !== 'select') { activeTool.value = 'select'; connectorSourceId.value = null; return }
    if (inspectorOpen.value) { closeInspector(); return }
    if (selectedIds.value.length > 0) { clearSelection(); return }
    return
  }

  if (isEditableTarget(event.target)) return

  // Undo / Redo
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') {
    event.preventDefault()
    if (event.shiftKey) uie.redo()
    else uie.undo()
    return
  }

  // Duplicate selected whiteboard nodes
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'd') {
    if (selectedIds.value.length > 0) {
      event.preventDefault()
      onDuplicateSelected()
    }
    return
  }

  if (event.ctrlKey || event.metaKey || event.altKey) return

  // `/` expands & focuses bottom ComposerBar
  if (event.key === '/') {
    event.preventDefault()
    expandComposerAndFocus()
    return
  }

  // Space hold -> temporary Hand Pan
  if (event.code === 'Space' || event.key === ' ') {
    if (!spacePressed.value) {
      event.preventDefault()
      spacePressed.value = true
    }
    return
  }

  // Delete / Backspace removes selected whiteboard items
  if (event.key === 'Delete' || event.key === 'Backspace') {
    const deletable = selectedIds.value.filter((id) => Boolean(board.objectsById.value[id]))
    if (deletable.length > 0) {
      event.preventDefault()
      onDeleteSelected()
    }
    return
  }

  const lower = event.key.toLowerCase()
  switch (lower) {
    case 'v': selectTool('select'); break
    case 'h': selectTool('pan'); break
    case 's': selectTool('section'); break
    case 'n': selectTool('sticky'); break
    case 't': selectTool('text'); break
    case 'b': selectTool('board'); break
    case 'p': selectTool('pen'); break
    case 'l': selectTool('connector'); break
    case 'i': selectTool('media'); break
    case 'c': selectTool('comment'); break
    case 'm': showMinimap.value = !showMinimap.value; break
    default: break
  }
}

function onDocumentKeyup(event: KeyboardEvent) {
  if (event.code === 'Space' || event.key === ' ') {
    spacePressed.value = false
  }
}

function onSurfaceKeydown(event: KeyboardEvent) {
  // Handled uniformly by document keydown; keep hook for direct surface test dispatch.
  if (event.target === surface.value) {
    onDocumentKeydown(event)
  }
}

// ── Tool Dock Definitions ─────────────────────────────────────────────────────

const toolItems: ReadonlyArray<{ id: SpatialToolMode; shortcut: string; labelKey: string; icon: unknown }> = [
  { id: 'select', shortcut: 'V', labelKey: 'space.figma.toolSelect', icon: MousePointer2 },
  { id: 'pan', shortcut: 'H', labelKey: 'space.figma.toolPan', icon: Hand },
  { id: 'section', shortcut: 'S', labelKey: 'space.figma.toolSection', icon: Frame },
  { id: 'sticky', shortcut: 'N', labelKey: 'space.figma.toolSticky', icon: StickyNote },
  { id: 'text', shortcut: 'T', labelKey: 'space.figma.toolText', icon: Type },
  { id: 'board', shortcut: 'B', labelKey: 'space.figma.toolBoard', icon: Columns3 },
  { id: 'pen', shortcut: 'P', labelKey: 'space.figma.toolPen', icon: Pen },
  { id: 'connector', shortcut: 'L', labelKey: 'space.figma.toolConnector', icon: ArrowUpRight },
  { id: 'media', shortcut: 'I', labelKey: 'space.figma.toolMedia', icon: ImagePlus },
  { id: 'comment', shortcut: 'C', labelKey: 'space.figma.toolComment', icon: MessageCircle },
]

function isToolActive(id: SpatialToolMode): boolean {
  if (id === 'pen') return ['pen', 'highlighter', 'eraser'].includes(activeTool.value)
  if (id === 'pan') return panModeActive.value
  return activeTool.value === id && !spacePressed.value
}

// Helpers for Style tab in Unified Inspector
const selectedSectionTone = computed<SpatialSectionTone>(() => {
  const raw = selectedWhiteboardObject.value?.props.color
  return isSectionTone(raw) ? raw : 'neutral'
})
const selectedStickyTone = computed<SpatialStickyTone>(() => {
  const raw = selectedWhiteboardObject.value?.props.tone
  return isStickyTone(raw) ? raw : 'butter'
})
const selectedTextScale = computed<SpatialTextScale>(() => {
  const raw = selectedWhiteboardObject.value?.props.scale
  return isTextScale(raw) ? raw : 'md'
})
const selectedConnectorCurve = computed<SpatialConnectorCurve>(() =>
  selectedWhiteboardObject.value?.props.curve === 'bezier' ? 'bezier' : 'smoothstep',
)
const selectedConnectorLabel = computed(() =>
  typeof selectedWhiteboardObject.value?.props.label === 'string' ? selectedWhiteboardObject.value.props.label : '',
)

function collaboratorInitial(name: string): string {
  const clean = name.trim()
  return clean ? clean[0]!.toUpperCase() : '?'
}
</script>

<template>
  <div class="spatial-page" :class="transitionClasses">
    <UieShell v-if="ready && c.viewMode.value === 'space'">
      <UieCanvas spatial full-bleed :spatial-panel="terminalVisible">
        <div
          ref="surface"
          class="space-surface"
          :class="{
            'is-pan-mode': panModeActive,
            'is-drag-over': dragOverCanvas,
            'is-macro-lod': isMacroLod,
          }"
          :style="materialStyle"
          v-bind="materialAttrs"
          tabindex="-1"
          @keydown="onSurfaceKeydown"
          @pointermove="onSurfacePointerMove"
          @dragover.prevent="dragOverCanvas = true"
          @dragleave.self="dragOverCanvas = false"
          @drop.prevent="onCanvasDrop"
        >
          <!-- Infinite Full-Bleed Canvas Stage -->
          <div class="space-flow-area" @wheel="onCanvasWheel">
            <VueFlow
              :key="sessionKey"
              id="session-space"
              :nodes="nodes"
              :edges="edges"
              :apply-default="false"
              :nodes-connectable="activeTool === 'connector'"
              :delete-key-code="null"
              :pan-on-drag="panModeActive ? true : [1]"
              :selection-key-code="isSelectTool ? true : null"
              :selection-mode="SelectionMode.Partial"
              :pan-on-scroll="true"
              :zoom-on-scroll="false"
              :zoom-on-pinch="true"
              :zoom-on-double-click="false"
              :min-zoom="0.2"
              :max-zoom="2"
              :only-render-visible-elements="true"
              @nodes-change="onNodesChange"
              @node-drag-start="onNodeDragStart"
              @node-drag="onNodeDrag"
              @node-drag-stop="dragStop"
              @node-click="onNodeClick"
              @pane-click="onPaneClick"
              @connect="onConnect"
              @edge-click="({ edge }) => edge.type === 'spatial' ? openDetails(edge.target) : setSelection([edge.id])"
              @pane-ready="restoreViewport"
              @viewport-change="onViewportChange"
              @viewport-change-end="camera"
            >
              <Background pattern-color="var(--border-muted)" :gap="24" :size="1" />

              <MiniMap
                v-if="showMinimap"
                position="bottom-right"
                pannable
                zoomable
                :node-border-radius="6"
                mask-color="color-mix(in srgb, var(--surface-section) 68%, transparent)"
                class="space-minimap"
                data-testid="space-minimap"
              />

              <template #edge-spatial="edge">
                <BaseEdge
                  :id="edge.id"
                  :path="edge.data.route.path"
                  :marker-end="edge.markerEnd"
                  :label="edge.label"
                  :label-x="edge.data.route.labelX || 0.001"
                  :label-y="edge.data.route.labelY || 0.001"
                  :style="edge.data.highlighted
                    ? { stroke: 'var(--accent-primary)', strokeWidth: 2.4, opacity: 1 }
                    : edge.data.dimmed
                      ? { stroke: 'var(--text-secondary)', strokeWidth: 1.2, opacity: 0.25 }
                      : undefined"
                  :label-style="{ fill: 'var(--text-secondary)', fontSize: 11 }"
                  :label-bg-style="{ fill: 'var(--surface-raised)' }"
                />
              </template>

              <template #edge-free-connector="edge">
                <SpatialFreeConnectorEdge
                  :id="edge.id"
                  :source-x="edge.sourceX"
                  :source-y="edge.sourceY"
                  :target-x="edge.targetX"
                  :target-y="edge.targetY"
                  :source-position="edge.sourcePosition"
                  :target-position="edge.targetPosition"
                  :label="typeof edge.label === 'string' ? edge.label : ''"
                  :selected="edge.selected"
                  :data="edge.data"
                  @update:label="({ id, label }) => board.updateObjectProps(id, { label })"
                  @update:curve="({ id, curve }) => board.updateObjectProps(id, { curve })"
                />
              </template>

              <template #node-section="{ id, data, selected }">
                <SpatialSectionNode
                  :id="id"
                  :data="data"
                  :selected="selected || selectedSet.has(id)"
                  @update:title="(title) => board.updateObjectProps(id, { title })"
                  @update:color="(color) => board.updateObjectProps(id, { color })"
                  @update:collapsed="(collapsed) => board.updateObjectProps(id, { collapsed })"
                  @open-live-digest="openLiveDigestForRun"
                  @open-board-task="onOpenCapsuleBoardTask"
                  @resize-start="gesturing = true"
                  @resize-end="(params) => resizeStop(id, params)"
                />
              </template>

              <template #node-board="{ id, data, selected }">
                <SpatialProjectBoardNode
                  :id="id"
                  :data="data"
                  :session-project-id="resolvedSpatialProjectId"
                  :project-name="spatialProjectName"
                  :projects="activeSpatialProjects"
                  :selected-task-id="boardDrawerTaskId"
                  :selected="selected || selectedSet.has(id)"
                  @update:widget-props="(patch: Partial<SpatialBoardWidgetProps>) => board.updateObjectProps(id, patch)"
                  @select-task="openSpatialBoardTaskDrawer"
                  @new-task="openSpatialBoardCreateDrawer"
                  @jump-spatial="handleJumpSpatialFromBoard"
                  @drop-task-to-canvas="({ task, clientX, clientY }) => dropLinkedTaskStickyAtClientPos(task, clientX, clientY)"
                  @resize-start="gesturing = true"
                  @resize-end="(params) => resizeStop(id, params)"
                />
              </template>

              <template #node-work="{ id, selected }">
                <template v-for="side in WORK_HANDLES" :key="side">
                  <Handle :id="side" type="target" :position="side" />
                  <Handle :id="side" type="source" :position="side" />
                </template>
                <NodeResizer
                  :min-width="320"
                  :min-height="160"
                  :max-width="1400"
                  :max-height="1200"
                  :is-visible="selected || selectedSet.has(id)"
                  @resize-start="gesturing = true"
                  @resize-end="({ params }) => resizeStop(id, params)"
                />
                <UieCardHost
                  :instance="{ id, descriptorId: 'spatialWork', title: '', state: { objectId: id } }"
                  :active="visibleIds.has(id)"
                />
              </template>

              <template #node-sticky="{ id, data, selected }">
                <SpatialStickyNode
                  :id="id"
                  :data="data"
                  :selected="selected || selectedSet.has(id)"
                  @update:text="(text) => board.updateObjectProps(id, { text })"
                  @update:tone="(tone) => board.updateObjectProps(id, { tone })"
                  @open-board-task="onOpenCapsuleBoardTask"
                  @resize-start="gesturing = true"
                  @resize-end="(params) => resizeStop(id, params)"
                />
              </template>

              <template #node-text="{ id, data, selected }">
                <SpatialTextNode
                  :id="id"
                  :data="data"
                  :selected="selected || selectedSet.has(id)"
                  @update:text="(text) => board.updateObjectProps(id, { text })"
                  @update:scale="(scale) => board.updateObjectProps(id, { scale })"
                  @open-board-task="onOpenCapsuleBoardTask"
                  @resize-start="gesturing = true"
                  @resize-end="(params) => resizeStop(id, params)"
                />
              </template>

              <template #node-media="{ id, data, selected }">
                <SpatialMediaFileNode
                  :id="id"
                  :data="data"
                  :cwd="projectPath || git.state.value.cwd"
                  :selected="selected || selectedSet.has(id)"
                  @open-file="onMediaOpenFile"
                  @snippet-loaded="({ id: mediaId, snippet }) => board.updateObjectProps(mediaId, { snippet })"
                  @resize-start="gesturing = true"
                  @resize-end="(params) => resizeStop(id, params)"
                />
              </template>
            </VueFlow>

            <!-- World-Coordinate SVG Smart Guides Overlay -->
            <svg
              v-if="visibleGuides.length > 0"
              class="space-smart-guides"
              aria-hidden="true"
              data-testid="space-smart-guides"
            >
              <g :transform="`translate(${currentViewport.x} ${currentViewport.y}) scale(${currentViewport.zoom})`">
                <template v-for="(guide, idx) in visibleGuides" :key="idx">
                  <line
                    v-if="guide.orientation === 'vertical'"
                    :x1="guide.position"
                    :x2="guide.position"
                    :y1="guide.start"
                    :y2="guide.end"
                    :class="['space-guide-line', `is-${guide.kind}`]"
                  />
                  <line
                    v-else
                    :x1="guide.start"
                    :x2="guide.end"
                    :y1="guide.position"
                    :y2="guide.position"
                    :class="['space-guide-line', `is-${guide.kind}`]"
                  />
                  <text
                    v-if="guide.label"
                    :x="guide.orientation === 'vertical' ? guide.position + 6 : (guide.start + guide.end) / 2"
                    :y="guide.orientation === 'vertical' ? (guide.start + guide.end) / 2 : guide.position - 6"
                    class="space-guide-label"
                    text-anchor="middle"
                  >
                    {{ guide.label }}
                  </text>
                </template>
              </g>
            </svg>

            <!-- Freehand Drawing / Highlighter / Eraser Overlay -->
            <SpatialDrawingLayer
              :strokes="drawingStrokes"
              :viewport="currentViewport"
              :tool="activeTool"
              :color="activeInkColor"
              :stroke-width="activeStrokeWidth"
              @stroke-complete="onStrokeComplete"
              @stroke-erase="onStrokeErase"
            />

            <!-- World-Space Comment Pins & Thread Popovers -->
            <SpatialCommentPinsLayer
              :pins="board.comments.value"
              :viewport="currentViewport"
              :active-pin-id="activePinId"
              :placing="activeTool === 'comment'"
              @select-pin="(pinId) => { activePinId = pinId }"
              @create-pin="onCreateCommentPin"
              @reply-pin="onReplyCommentPin"
              @toggle-resolve="onToggleResolvePin"
              @delete-pin="onDeleteCommentPin"
              @dispatch-comment="dispatchCommentPin"
            />

            <!-- Live Multiplayer Cursors, Remote Selections, AI Thought Micro-Bubbles & Follow HUD -->
            <SpatialPresenceLayer
              :collaborators="presenceCollaborators"
              :viewport="currentViewport"
              :local-client-id="board.clientId"
              :following-client-id="board.followingClientId.value"
              :object-rects="allObjectRects"
              :teammate-thoughts="liveDigest.teammateThoughtByHandle.value"
              :active-run-id="activeDigestRunId"
              @follow-user="(cid) => board.followCollaborator(cid)"
              @stop-follow="board.stopFollowing()"
              @steer-teammate="onPresenceSteerTeammate"
            />

            <!-- Multi-Select Contextual Action Bar -->
            <SpatialSelectionBar
              :selected-ids="selectedIds"
              :bounds="selectedBounds"
              :viewport="currentViewport"
              :surface-width="surfaceSize.width"
              :surface-height="surfaceSize.height"
              :project-id="resolvedSpatialProjectId"
              :promotable-ids="selectedPromotableIds"
              :can-promote-to-board="Boolean(resolvedSpatialProjectId) && selectedPromotableIds.length > 0"
              :promoting="promotingToBoard"
              @align="onAlignSelected"
              @distribute="onDistributeSelected"
              @wrap-section="onWrapSelectionInSection"
              @duplicate="onDuplicateSelected"
              @delete="onDeleteSelected"
              @dispatch-to-team="dispatchSelectionToTeam"
              @promote-to-board="onPromoteSelectionToBoard"
            />

            <!-- Active Connector Hint Banner -->
            <div v-if="activeTool === 'connector'" class="space-mode-pill" role="status">
              <ArrowUpRight :size="13" aria-hidden="true" />
              <span>{{ t('space.figma.connectorStartHint') }}</span>
            </div>

            <!-- Drag-Drop Canvas Hint -->
            <div v-if="dragOverCanvas" class="space-drop-overlay" role="status">
              <ImagePlus :size="20" aria-hidden="true" />
              <span>{{ t('space.figma.dropFileHint') }}</span>
            </div>
          </div>

          <!-- Floating Top-Left Island: Session / Run Selector + Layers Toggle + Sync -->
          <div class="space-island space-island-top-left" data-testid="space-island-top-left">
            <button
              type="button"
              class="space-island-icon-btn"
              :class="{ 'is-active': layersOpen }"
              :aria-expanded="layersOpen"
              :aria-label="t('space.figma.layersTitle')"
              :title="t('space.figma.layersTitle')"
              data-testid="space-layers-toggle"
              @click="layersOpen = !layersOpen"
            >
              <Layers :size="15" aria-hidden="true" />
            </button>

            <span class="space-island-sep" aria-hidden="true" />

            <UiSelect
              v-if="runs.length"
              :model-value="focusedRun"
              :display-value="focusedRun === '*'
                ? t('space.cluster.allGoals')
                : (runs.find((run) => run.runId === focusedRun)?.title || t('space.kind.run')) + ' · ' + statusText(runs.find((run) => run.runId === focusedRun)?.status)"
              :aria-label="t('space.cluster.currentGoal')"
              class="space-goal-select"
              @update:model-value="(value) => { focusedRun = value; selectRun() }"
            >
              <template #default="{ select, selectedValue }">
                <button
                  type="button"
                  class="space-select-option"
                  :class="{ 'space-select-option-active': selectedValue === '*' }"
                  @click="select('*')"
                >
                  {{ t('space.cluster.allGoals') }}
                </button>
                <button
                  v-for="run in runs"
                  :key="run.id"
                  type="button"
                  class="space-select-option"
                  :class="{ 'space-select-option-active': selectedValue === run.runId }"
                  @click="run.runId && select(run.runId)"
                >
                  <span>{{ run.title || t('space.kind.run') }}</span>
                  <small>{{ statusText(run.status) }}</small>
                </button>
              </template>
            </UiSelect>
            <span v-else class="space-session-title">{{ c.currentSession.value?.title || t('space.title') }}</span>

            <span class="space-sync-pill" :data-sync="board.syncStatus.value" data-testid="space-sync-badge">
              <span class="space-sync-dot" aria-hidden="true" />
              {{
                board.syncStatus.value === 'live'
                  ? t('space.figma.syncLive')
                  : board.syncStatus.value === 'syncing'
                    ? t('space.figma.syncSyncing')
                    : t('space.figma.syncLocal')
              }}
            </span>

            <span class="space-island-sep" aria-hidden="true" />

            <UiButton
              variant="ghost"
              size="icon"
              class="space-island-icon-btn"
              :aria-label="t('space.undo')"
              :title="t('space.undo')"
              :disabled="!uie.canUndo.value"
              @click="uie.undo()"
            >
              <Undo2 :size="14" />
            </UiButton>
            <UiButton
              variant="ghost"
              size="icon"
              class="space-island-icon-btn"
              :aria-label="t('space.redo')"
              :title="t('space.redo')"
              :disabled="!uie.canRedo.value"
              @click="uie.redo()"
            >
              <Redo2 :size="14" />
            </UiButton>

            <button
              v-if="hasBlocker"
              type="button"
              class="space-blocker-pill"
              :title="t('space.cluster.blockers')"
              @click="locateBlocker"
            >
              <AlertTriangle :size="13" aria-hidden="true" />
              <span>{{ t('space.cluster.blockers') }}</span>
            </button>
          </div>

          <!-- Compact unobtrusive telemetry strip (only shown when topology/loading active) -->
          <div
            v-if="topology || loading"
            class="space-data-status"
            role="status"
          >
            <span v-if="topology" :title="`${t('space.cluster.fetchedAt')} ${new Date(topology.generated_at).toLocaleTimeString(locale)}`">
              {{ t('space.figma.statusSummary', { runs: topology.runs.length, tasks: topology.runs.reduce((n, r) => n + r.tasks.length, 0) }) }}
            </span>
            <span v-if="incomplete">{{ t('space.cluster.truncated') }}</span>
            <span v-if="loading">{{ t('space.loading') }}</span>
          </div>

          <!-- Collapsible Layers / Outline Drawer -->
          <aside
            v-if="layersOpen"
            class="space-layers-drawer"
            :aria-label="t('space.figma.layersTitle')"
            data-testid="space-layers-drawer"
          >
            <header class="space-layers-header">
              <strong>{{ t('space.figma.layersTitle') }}</strong>
              <button
                type="button"
                class="space-island-icon-btn"
                :aria-label="t('common.close')"
                @click="layersOpen = false"
              >
                <X :size="14" />
              </button>
            </header>
            <div class="space-layers-search">
              <input
                v-model="layersQuery"
                type="text"
                :placeholder="t('space.figma.searchLayersPlaceholder')"
                :aria-label="t('space.figma.searchLayersPlaceholder')"
              />
            </div>
            <div class="space-layers-scroll">
              <div v-if="filteredWorkLayers.length" class="space-layer-group">
                <div class="space-layer-group-title">{{ t('space.figma.layersWorkGroup') }}</div>
                <button
                  v-for="item in filteredWorkLayers"
                  :key="item.id"
                  type="button"
                  class="space-layer-row"
                  :class="{ 'is-selected': selectedSet.has(item.id) }"
                  :data-layer-id="item.id"
                  @click="locate(item.id)"
                >
                  <span class="space-layer-kind">{{ t(`space.kind.${item.kind}`) }}</span>
                  <span class="space-layer-name">{{ item.title || t(`space.kind.${item.kind}`) }}</span>
                </button>
              </div>

              <div v-if="filteredWhiteboardLayers.length" class="space-layer-group">
                <div class="space-layer-group-title">{{ t('space.figma.layersWhiteboardGroup') }}</div>
                <button
                  v-for="item in filteredWhiteboardLayers"
                  :key="item.id"
                  type="button"
                  class="space-layer-row"
                  :class="{ 'is-selected': selectedSet.has(item.id) }"
                  :data-layer-id="item.id"
                  @click="locate(item.id)"
                >
                  <span class="space-layer-kind">{{ wbKindLabel(item.kind) }}</span>
                  <span class="space-layer-name">{{ layerLabelForWhiteboard(item) }}</span>
                </button>
              </div>

              <div v-if="filteredCommentLayers.length" class="space-layer-group">
                <div class="space-layer-group-title">{{ t('space.figma.layersCommentsGroup') }}</div>
                <button
                  v-for="pin in filteredCommentLayers"
                  :key="pin.id"
                  type="button"
                  class="space-layer-row"
                  :class="{ 'is-selected': activePinId === pin.id }"
                  :data-layer-pin="pin.id"
                  @click="activePinId = pin.id; panToPoint(pin.x, pin.y)"
                >
                  <span class="space-layer-kind">{{ t('space.figma.kindComment') }}</span>
                  <span class="space-layer-name">{{ pin.body }}</span>
                </button>
              </div>

              <p
                v-if="!filteredWorkLayers.length && !filteredWhiteboardLayers.length && !filteredCommentLayers.length"
                class="space-layers-empty"
              >
                {{ t('space.figma.layersEmpty') }}
              </p>
            </div>
          </aside>

          <!-- Floating Top-Right Island: Presence Avatars + Zoom/View Menu + Inspector Toggle -->
          <div class="space-island space-island-top-right" data-testid="space-island-top-right">
            <div
              v-if="presenceCollaborators.length"
              class="space-presence-strip"
              data-testid="space-presence-avatars"
            >
              <button
                v-for="collab in presenceCollaborators.slice(0, 6)"
                :key="collab.clientId"
                type="button"
                class="space-presence-chip"
                :class="{
                  'is-following': board.followingClientId.value === collab.clientId,
                  'is-ai': collab.role === 'ai',
                }"
                :style="{ '--avatar-color': collab.color }"
                :title="collab.role === 'ai'
                  ? tr('space.liveDigest.thoughtBubbleHint', 'Click to steer or pause {name}', { name: collab.displayName })
                  : t('space.whiteboard.followUser', { name: collab.displayName })"
                :data-presence-client="collab.clientId"
                @click="onPresenceAvatarClick(collab, $event)"
              >
                <Bot v-if="collab.role === 'ai'" :size="12" aria-hidden="true" />
                <span v-else>{{ collaboratorInitial(collab.displayName) }}</span>
              </button>
            </div>

            <span v-if="presenceCollaborators.length" class="space-island-sep" aria-hidden="true" />

            <!-- Zoom & View Dropdown -->
            <div class="space-zoom-wrap">
              <button
                type="button"
                class="space-zoom-trigger"
                :aria-expanded="zoomMenuOpen"
                :aria-label="t('space.figma.zoomMenu')"
                data-testid="space-zoom-trigger"
                @click.stop="zoomMenuOpen = !zoomMenuOpen"
              >
                <span class="space-zoom-value">{{ zoomPercent }}%</span>
                <span v-if="isMacroLod" class="space-lod-tag">{{ t('space.figma.macroLodHint') }}</span>
                <ChevronDown :size="13" aria-hidden="true" />
              </button>

              <div
                v-if="zoomMenuOpen"
                class="space-zoom-menu"
                role="menu"
                data-testid="space-zoom-menu"
                @click.stop
              >
                <div class="space-zoom-step-row">
                  <button type="button" class="space-zoom-step-btn" :aria-label="t('space.zoomOut')" @click="zoomOut()">
                    <Minus :size="13" />
                  </button>
                  <button type="button" class="space-zoom-preset" @click="setZoomPreset(0.5)">50%</button>
                  <button type="button" class="space-zoom-preset" @click="setZoomPreset(1)">100%</button>
                  <button type="button" class="space-zoom-preset" @click="setZoomPreset(1.5)">150%</button>
                  <button type="button" class="space-zoom-step-btn" :aria-label="t('space.zoomIn')" @click="zoomIn()">
                    <Plus :size="13" />
                  </button>
                </div>

                <div class="space-menu-divider" />

                <button type="button" class="space-menu-item" data-testid="space-menu-fit" @click="overview">
                  <Maximize :size="13" aria-hidden="true" />
                  <span>{{ t('space.figma.fitOverview') }}</span>
                </button>
                <button
                  type="button"
                  class="space-menu-item"
                  :disabled="!focusedRun || focusedRun === '*'"
                  :title="t('space.cluster.arrangeHint')"
                  data-testid="space-menu-arrange"
                  @click="arrangeCurrent"
                >
                  <Sparkles :size="13" aria-hidden="true" />
                  <span>{{ t('space.figma.arrangeDag') }}</span>
                </button>
                <button
                  v-if="runs.length"
                  type="button"
                  class="space-menu-item"
                  data-testid="space-menu-wrap-run"
                  @click="wrapFocusedRunInSection"
                >
                  <Frame :size="13" aria-hidden="true" />
                  <span>{{ t('space.figma.groupRunInSection') }}</span>
                </button>

                <div class="space-menu-divider" />

                <button
                  type="button"
                  class="space-menu-item"
                  data-testid="space-menu-minimap"
                  @click="showMinimap = !showMinimap"
                >
                  <MapIcon :size="13" aria-hidden="true" />
                  <span>{{ t('space.figma.toggleMinimap') }}</span>
                  <span class="space-menu-check">{{ showMinimap ? '✓' : '' }}</span>
                </button>

                <label class="space-menu-item space-relation-toggle" @click.stop>
                  <UiCheckbox v-model="showDependencies" :aria-label="t('space.cluster.dependencies')" />
                  <span>{{ t('space.cluster.dependencies') }}</span>
                </label>

                <button
                  type="button"
                  class="space-menu-item"
                  data-testid="space-menu-inspector"
                  @click="openUnifiedInspector"
                >
                  <SlidersHorizontal :size="13" aria-hidden="true" />
                  <span>{{ t('space.figma.openInspector') }}</span>
                </button>
              </div>
            </div>

            <span class="space-island-sep" aria-hidden="true" />

            <button
              type="button"
              class="space-island-icon-btn space-digest-toggle-btn"
              :class="{ 'is-active': inspectorOpen && inspectorTab === 'digest', 'is-live': liveDigest.isRunning.value }"
              :aria-expanded="inspectorOpen && inspectorTab === 'digest'"
              :aria-label="tr('space.liveDigest.inspectorToggle', 'Live Digest')"
              :title="tr('space.liveDigest.inspectorToggle', 'Live Digest')"
              data-testid="space-digest-toggle"
              @click="toggleLiveDigestInspector"
            >
              <Activity :size="15" aria-hidden="true" />
              <span v-if="liveDigest.isRunning.value" class="space-digest-live-dot" aria-hidden="true" />
            </button>

            <button
              type="button"
              class="space-island-icon-btn"
              :class="{ 'is-active': inspectorOpen && inspectorTab === 'plan' }"
              :aria-expanded="inspectorOpen && inspectorTab === 'plan'"
              :aria-label="te('sessionPlan.title') ? t('sessionPlan.title') : t('space.figma.planTab')"
              :title="te('sessionPlan.title') ? t('sessionPlan.title') : t('space.figma.planTab')"
              data-testid="space-plan-toggle"
              @click="togglePlanInspector"
            >
              <ListChecks :size="15" aria-hidden="true" />
            </button>

            <button
              type="button"
              class="space-island-icon-btn"
              :class="{ 'is-active': inspectorOpen }"
              :aria-expanded="inspectorOpen"
              :aria-label="t('space.figma.inspectorTitle')"
              :title="t('space.figma.inspectorTitle')"
              data-testid="space-inspector-toggle"
              @click="inspectorOpen ? closeInspector() : openUnifiedInspector()"
            >
              <SlidersHorizontal :size="15" aria-hidden="true" />
            </button>
          </div>

          <button
            v-if="terminalVisible"
            type="button"
            class="space-hide-terminal"
            @click="terminalVisible = false"
          >
            {{ t('space.cluster.hideTerminal') }}
          </button>

          <!-- Left-Center Vertical Figma Tool Dock -->
          <nav
            class="space-tool-dock"
            :aria-label="t('space.title')"
            data-testid="space-tool-dock"
          >
            <div v-for="tool in toolItems" :key="tool.id" class="space-tool-slot">
              <button
                type="button"
                class="space-tool-btn"
                :class="{ 'is-active': isToolActive(tool.id) }"
                :aria-label="t(tool.labelKey)"
                :aria-pressed="isToolActive(tool.id)"
                :title="t(tool.labelKey)"
                :data-tool="tool.id"
                @click="selectTool(tool.id)"
              >
                <component :is="tool.icon" :size="16" aria-hidden="true" />
                <kbd class="space-tool-kbd">{{ tool.shortcut }}</kbd>
              </button>

              <!-- Sticky 6-color dot popover -->
              <div
                v-if="tool.id === 'sticky' && stickyPaletteOpen"
                class="space-tool-popover"
                role="group"
                data-testid="space-sticky-popover"
                @click.stop
              >
                <button
                  v-for="tone in SPATIAL_STICKY_TONES"
                  :key="tone"
                  type="button"
                  class="space-color-dot"
                  :class="{ 'is-active': activeStickyTone === tone }"
                  :style="{ backgroundColor: SPATIAL_STICKY_PALETTE[tone].swatch }"
                  :aria-label="tone"
                  :data-sticky-preset="tone"
                  @click="activeStickyTone = tone; activeTool = 'sticky'"
                />
              </div>

              <!-- Draw sub-modes (pen / highlighter / eraser) + ink colors -->
              <div
                v-if="tool.id === 'pen' && drawPopoverOpen"
                class="space-tool-popover space-draw-popover"
                role="group"
                data-testid="space-draw-popover"
                @click.stop
              >
                <button
                  type="button"
                  class="space-draw-mode-btn"
                  :class="{ 'is-active': activeTool === 'pen' }"
                  :title="t('space.whiteboard.penMode')"
                  data-draw-mode="pen"
                  @click="activeTool = 'pen'; activeStrokeWidth = SPATIAL_WHITEBOARD_DEFAULTS.pen.strokeWidth"
                >
                  <Pen :size="13" />
                </button>
                <button
                  type="button"
                  class="space-draw-mode-btn"
                  :class="{ 'is-active': activeTool === 'highlighter' }"
                  :title="t('space.whiteboard.highlighterMode')"
                  data-draw-mode="highlighter"
                  @click="activeTool = 'highlighter'; activeStrokeWidth = SPATIAL_WHITEBOARD_DEFAULTS.highlighter.strokeWidth"
                >
                  <Highlighter :size="13" />
                </button>
                <button
                  type="button"
                  class="space-draw-mode-btn"
                  :class="{ 'is-active': activeTool === 'eraser' }"
                  :title="t('space.figma.toolEraser')"
                  data-draw-mode="eraser"
                  @click="activeTool = 'eraser'"
                >
                  <Eraser :size="13" />
                </button>
                <span class="space-island-sep" aria-hidden="true" />
                <button
                  v-for="swatch in INK_SWATCHES"
                  :key="swatch"
                  type="button"
                  class="space-color-dot"
                  :class="{ 'is-active': activeInkColor === swatch }"
                  :style="{ backgroundColor: swatch }"
                  :aria-label="swatch"
                  @click="activeInkColor = swatch"
                />
              </div>
            </div>
          </nav>

          <!-- Right On-Demand Unified Smart Inspector (Single clean contextual glass drawer) -->
          <aside
            v-show="inspectorOpen"
            class="space-inspector"
            role="complementary"
            :aria-label="t('space.figma.inspectorTitle')"
            data-testid="space-inspector"
          >
            <header class="space-inspector-header">
              <div class="space-inspector-tabs" role="tablist">
                <button
                  type="button"
                  role="tab"
                  class="space-inspector-tab"
                  :class="{ 'is-active': inspectorTab === 'details' }"
                  :aria-selected="inspectorTab === 'details'"
                  data-inspector-tab="details"
                  @click="inspectorTab = 'details'"
                >
                  {{ t('space.figma.detailsTab') }}
                </button>
                <button
                  type="button"
                  role="tab"
                  class="space-inspector-tab"
                  :class="{ 'is-active': inspectorTab === 'style' }"
                  :aria-selected="inspectorTab === 'style'"
                  data-inspector-tab="style"
                  @click="inspectorTab = 'style'"
                >
                  {{ t('space.figma.styleTab') }}
                </button>
                <button
                  type="button"
                  role="tab"
                  class="space-inspector-tab"
                  :class="{ 'is-active': inspectorTab === 'plan' }"
                  :aria-selected="inspectorTab === 'plan'"
                  data-inspector-tab="plan"
                  @click="inspectorTab = 'plan'"
                >
                  {{ t('space.figma.planTab') }}
                </button>
                <button
                  type="button"
                  role="tab"
                  class="space-inspector-tab"
                  :class="{ 'is-active': inspectorTab === 'digest' }"
                  :aria-selected="inspectorTab === 'digest'"
                  data-inspector-tab="digest"
                  @click="inspectorTab = 'digest'"
                >
                  {{ tr('space.liveDigest.tabLabel', 'Live Digest') }}
                </button>
              </div>
              <UiButton
                variant="ghost"
                size="icon"
                class="space-island-icon-btn"
                :aria-label="t('space.cluster.closeDetails')"
                :title="t('space.figma.closeInspector')"
                @click="closeInspector"
              >
                <X :size="14" />
              </UiButton>
            </header>

            <div class="space-inspector-body">
              <!-- Tab 1: Work Card Details (reuses visitedDetails DOM instances cleanly!) -->
              <div
                v-show="inspectorTab === 'details'"
                class="space-detail"
                role="region"
                :aria-label="t('space.cluster.details')"
              >
                <div v-if="selectedObject" class="space-detail-scroll">
                  <div
                    v-if="selectedObject.owner && (selectedObject.runId || activeDigestRunId)"
                    class="space-detail-steer-bar"
                  >
                    <button
                      type="button"
                      class="space-detail-steer-btn ui-pressable"
                      data-testid="space-detail-steer-btn"
                      @click="openSelectedObjectSteer($event)"
                    >
                      <MessageSquare :size="13" aria-hidden="true" />
                      <span>{{ tr('teammateControl.triggerTooltip', 'Steer or pause {name}', { name: selectedObject.owner }) }}</span>
                    </button>
                  </div>
                  <div v-if="focusedInstance && instanceTasks.length" class="space-related-list">
                    <strong>{{ t('space.cluster.instanceTasks') }}</strong>
                    <button
                      v-for="task in instanceTasks"
                      :key="task.id"
                      type="button"
                      @click="openDetails(task.id); locate(task.id)"
                    >
                      {{ task.title }}
                    </button>
                    <button type="button" @click="focusedInstance = null">{{ t('space.cluster.clearFocus') }}</button>
                  </div>
                  <template v-for="id in visitedDetails" :key="`${sessionKey}:${id}`">
                    <SpatialWorkCard v-if="objectMap[id]" v-show="selectedId === id" :object="objectMap[id]" detail />
                  </template>
                  <section class="space-related-list" :aria-label="t('space.cluster.relations')">
                    <strong>{{ t('space.cluster.relations') }}</strong>
                    <p>{{ t('space.cluster.constraintOnly') }}</p>
                    <div v-for="relation in related" :key="relation.id" class="space-relation-record">
                      <button type="button" @click="locate(relation.source)">{{ titleOf(relation.source) }}</button>
                      <span>{{ t('space.cluster.requires') }} →</span>
                      <button type="button" @click="locate(relation.target)">{{ titleOf(relation.target) }}</button>
                      <small>{{ t('space.cluster.source') }}: {{ relation.sourceField }}</small>
                      <small v-if="routeMap[relation.id]?.kind === 'blocked'">{{ t('space.cluster.routeBlocked') }}</small>
                    </div>
                    <p v-if="!related.length">{{ t('space.cluster.noRelations') }}</p>
                    <small>{{ selectedObject.runId || sessionKey }}<br />{{ selectedObject.taskId || selectedObject.id }}</small>
                  </section>
                </div>
                <p v-else class="space-inspector-empty">{{ t('space.figma.noSelectionInspector') }}</p>
              </div>

              <!-- Tab 2: Selected Whiteboard Style Properties -->
              <div v-if="inspectorTab === 'style'" class="space-style-panel" data-testid="space-style-panel">
                <template v-if="selectedWhiteboardObject">
                  <div class="space-style-heading">
                    <strong>{{ wbKindLabel(selectedWhiteboardObject.kind) }}</strong>
                    <small>{{ selectedWhiteboardObject.id.slice(0, 14) }}</small>
                  </div>

                  <div v-if="selectedWhiteboardObject.kind === 'section'" class="space-style-section">
                    <label class="space-style-label">{{ t('space.figma.colorTone') }}</label>
                    <div class="space-style-swatches">
                      <button
                        v-for="tone in SPATIAL_SECTION_TONES"
                        :key="tone"
                        type="button"
                        class="space-color-dot"
                        :class="{ 'is-active': selectedSectionTone === tone }"
                        :style="{ backgroundColor: SPATIAL_SECTION_PALETTE[tone].swatch }"
                        :aria-label="tone"
                        @click="board.updateObjectProps(selectedWhiteboardObject.id, { color: tone })"
                      />
                    </div>
                  </div>

                  <div v-if="selectedWhiteboardObject.kind === 'sticky'" class="space-style-section">
                    <label class="space-style-label">{{ t('space.figma.colorTone') }}</label>
                    <div class="space-style-swatches">
                      <button
                        v-for="tone in SPATIAL_STICKY_TONES"
                        :key="tone"
                        type="button"
                        class="space-color-dot"
                        :class="{ 'is-active': selectedStickyTone === tone }"
                        :style="{ backgroundColor: SPATIAL_STICKY_PALETTE[tone].swatch }"
                        :aria-label="tone"
                        @click="board.updateObjectProps(selectedWhiteboardObject.id, { tone })"
                      />
                    </div>
                  </div>

                  <div v-if="selectedWhiteboardObject.kind === 'text'" class="space-style-section">
                    <label class="space-style-label">{{ t('space.figma.textScale') }}</label>
                    <div class="space-style-pills">
                      <button
                        v-for="scale in SPATIAL_TEXT_SCALES"
                        :key="scale"
                        type="button"
                        class="space-style-pill"
                        :class="{ 'is-active': selectedTextScale === scale }"
                        @click="board.updateObjectProps(selectedWhiteboardObject.id, { scale })"
                      >
                        {{ scale.toUpperCase() }}
                      </button>
                    </div>
                  </div>

                  <div v-if="selectedWhiteboardObject.kind === 'connector'" class="space-style-section">
                    <label class="space-style-label">{{ t('space.figma.curveStyle') }}</label>
                    <div class="space-style-pills">
                      <button
                        type="button"
                        class="space-style-pill"
                        :class="{ 'is-active': selectedConnectorCurve === 'smoothstep' }"
                        @click="board.updateObjectProps(selectedWhiteboardObject.id, { curve: 'smoothstep' })"
                      >
                        {{ t('space.figma.curveSmoothstep') }}
                      </button>
                      <button
                        type="button"
                        class="space-style-pill"
                        :class="{ 'is-active': selectedConnectorCurve === 'bezier' }"
                        @click="board.updateObjectProps(selectedWhiteboardObject.id, { curve: 'bezier' })"
                      >
                        {{ t('space.figma.curveBezier') }}
                      </button>
                    </div>
                    <label class="space-style-label">{{ t('space.figma.connectorLabel') }}</label>
                    <input
                      type="text"
                      class="space-style-input"
                      :value="selectedConnectorLabel"
                      @input="board.updateObjectProps(selectedWhiteboardObject.id, { label: ($event.target as HTMLInputElement).value })"
                    />
                  </div>

                  <div v-if="selectedWhiteboardObject.kind === 'media'" class="space-style-section">
                    <div class="space-media-meta-row">
                      <FileCode2 :size="14" aria-hidden="true" />
                      <span>{{ String(selectedWhiteboardObject.props.path || selectedWhiteboardObject.props.title || '') }}</span>
                    </div>
                  </div>
                </template>
                <p v-else class="space-inspector-empty">{{ t('space.figma.noSelectionInspector') }}</p>
              </div>

              <!-- Tab 3: Session Plan Panel -->
              <div v-if="inspectorTab === 'plan'" class="space-plan-tab-body" data-testid="space-plan-drawer">
                <SessionPlanPanel :session-id="c.selectedSessionId.value" @focus-node="focusPlanNode" />
              </div>

              <!-- Tab 4: Same-Source Live Digest Panel -->
              <div v-if="inspectorTab === 'digest'" class="space-digest-tab-body" data-testid="space-digest-drawer">
                <SpatialLiveDigestInspector
                  :run-id="activeDigestRunId"
                  :paused-overrides="pausedTeammates"
                  @steer-teammate="openTeammateSteer"
                  @fill-steer="onFillSteerFromDigest"
                />
              </div>
            </div>
          </aside>

          <!-- Right Slide-Over Project Board Task Drawer (Shared with Flat Board) -->
          <BoardTaskDrawer
            v-model:open="boardDrawerOpen"
            :mode="boardDrawerMode"
            :task="selectedBoardDrawerTask"
            :project-tasks="spatialProjectBoard.tasks.value"
            :default-project-id="boardDrawerDefaultProjectId || resolvedSpatialProjectId"
            :default-status="boardDrawerDefaultStatus"
            :projects="activeSpatialProjects"
            @create-task="handleCreateBoardTask"
            @save-task="handleSaveBoardTask"
            @delete-task="handleDeleteBoardTask"
            @resolve-conflict="handleResolveBoardConflict"
            @dispatch-task="handleDispatchBoardTask"
            @cancel-task="handleCancelBoardTask"
            @approve-task="handleApproveBoardTask"
            @request-changes="handleRequestBoardChanges"
            @open-session="handleOpenBoardSession"
            @jump-spatial="handleJumpSpatialFromBoard"
          />

          <!-- Direct Single-Teammate Steer / Pause Popover -->
          <TeammateSteerPopover
            v-if="steerTarget"
            v-model:open="steerOpen"
            :run-id="steerTarget.runId"
            :handle="steerTarget.handle"
            :display-name="steerTarget.displayName"
            :status="steerTarget.status"
            :paused="steerTarget.paused"
            :task-summary="steerTarget.taskSummary"
            :thought="steerTarget.thought"
            :initial-draft="steerTarget.initialDraft"
            :anchor-rect="steerTarget.anchorRect"
            @status-change="onTeammateStatusChange"
          />

          <!-- Resident Collapsible Bottom ComposerBar (Expanded & bottom-centered by default!) -->
          <div
            ref="composerDock"
            class="space-composer-dock"
            :class="{ 'is-collapsed': composerCollapsed }"
            data-testid="space-composer-dock"
          >
            <!-- Floating Selection Context Chip Strip directly above ComposerBar -->
            <div
              v-if="selectedIds.length > 0"
              class="space-selected-context-strip"
              data-testid="space-selected-context-strip"
            >
              <span class="space-context-count">
                {{ t('space.figma.selectedContextCount', { count: selectedIds.length }) }}
              </span>
              <button
                type="button"
                class="space-context-dispatch"
                data-testid="space-composer-dispatch"
                @click="dispatchSelectionToTeam(selectedIds)"
              >
                <Sparkles :size="12" aria-hidden="true" />
                <span>{{ t('space.whiteboard.dispatchToTeam') }}</span>
              </button>
              <button
                type="button"
                class="space-context-clear"
                :aria-label="t('space.figma.clearSelection')"
                :title="t('space.figma.clearSelection')"
                data-testid="space-clear-selection"
                @click="clearSelection"
              >
                <X :size="12" aria-hidden="true" />
              </button>
            </div>

            <!-- Collapsed Floating Pill (`✦ 开始对话`) -->
            <button
              v-if="composerCollapsed"
              type="button"
              class="space-composer-pill"
              :aria-label="t('space.figma.expandComposer')"
              data-testid="space-composer-pill"
              @click="expandComposerAndFocus"
            >
              <span>{{ t('space.figma.startChatPill') }}</span>
              <kbd class="space-pill-hotkey">/</kbd>
            </button>

            <!-- Resident Expanded Bottom ComposerBar -->
            <div v-else class="space-composer-frame">
              <button
                type="button"
                class="space-composer-collapse-btn"
                :aria-label="t('space.figma.collapseComposer')"
                :title="t('space.figma.collapseComposer')"
                data-testid="space-composer-collapse"
                @click="composerCollapsed = true"
              >
                <ChevronDown :size="13" aria-hidden="true" />
              </button>
              <ComposerBar
                spatial
                :hero="hero"
                :busy="c.busy.value || c.working.value"
                :model-value="c.draft.value"
                :permission="c.currentPermission.value"
                :projects="c.projects.value"
                :selected-project-id="c.selectedProjectId.value"
                :session-id="c.currentSession.value?.id ?? null"
                :mode-version-id="c.composerSettings.value.mode_version_id"
                :meeting-model-override="c.composerSettings.value.meeting_model_override"
                :space-options="c.composerSettings.value.space_options"
                :settings-saving="c.settingsSaving.value"
                :settings-error="c.settingsError.value"
                :runs="c.runs.value"
                :can-stop="Boolean(c.stoppableRunId.value)"
                @select-mode="c.updateComposerSettings($event.mode_version_id ? { mode_version_id: $event.mode_version_id, space_options: $event.space_options } : { clear_mode_version: true, space_options: $event.space_options })"
                @update:space-options="c.updateComposerSettings({ space_options: $event })"
                @update:meeting-model-override="c.updateComposerSettings($event ? { meeting_model_override: $event } : { clear_meeting_model_override: true })"
                @update:model-value="c.updateDraft($event)"
                @update:permission="c.updatePermission($event)"
                @submit="c.sendMessage($event)"
                @welcome-submit="c.handleWelcomeSend($event)"
                @stop="c.stopRun()"
                @create-project="c.openProject()"
                @select-project="c.setSelectedProject($event)"
              >
                <template #capabilities>
                  <span class="space-meeting-label">
                    <Bot :size="14" aria-hidden="true" />
                    {{ t('space.kind.meeting') }}
                  </span>
                </template>
              </ComposerBar>
            </div>
          </div>
        </div>
      </UieCanvas>
    </UieShell>
  </div>
</template>

<style>
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/node-resizer/dist/style.css';
@import '@vue-flow/minimap/dist/style.css';

.spatial-page {
  position: relative;
  width: 100%;
  height: 100vh;
  overflow: hidden;
  background: transparent;
}

.space-surface {
  position: relative;
  height: 100%;
  min-height: 0;
  overflow: hidden;
}

.space-surface.is-pan-mode .vue-flow__pane {
  cursor: grab;
}

.space-surface.is-pan-mode .vue-flow__pane:active {
  cursor: grabbing;
}

.space-flow-area {
  position: absolute;
  inset: 0;
}

.space-flow-area .vue-flow__node-work {
  border-radius: var(--radius-island, 12px);
  background: transparent;
  box-shadow: var(--shadow-card-subtle);
  transition: opacity 150ms ease, filter 150ms ease;
}

.space-flow-area .vue-flow__node-work.selected {
  outline: 2px solid var(--accent-primary);
  outline-offset: 3px;
}

.space-flow-area .vue-flow__node-work.space-causal-dim {
  opacity: 0.42;
}

.space-flow-area .vue-flow__node-work.space-causal-focus {
  box-shadow: 0 0 0 1.5px color-mix(in srgb, var(--accent-primary) 55%, transparent), var(--shadow-card-subtle);
}

.space-flow-area .vue-flow__node-section,
.space-flow-area .vue-flow__node-board,
.space-flow-area .vue-flow__node-sticky,
.space-flow-area .vue-flow__node-text,
.space-flow-area .vue-flow__node-media {
  background: transparent;
  border: 0;
  padding: 0;
  box-shadow: none;
}

@keyframes space-monochrome-focus-pulse {
  0% {
    box-shadow: 0 0 0 0 color-mix(in srgb, var(--text-primary) 42%, transparent);
  }
  65% {
    box-shadow: 0 0 0 12px color-mix(in srgb, var(--text-primary) 0%, transparent);
  }
  100% {
    box-shadow: 0 0 0 0 transparent;
  }
}

.space-flow-area .space-focus-pulse {
  border-radius: 14px;
  animation: space-monochrome-focus-pulse 900ms cubic-bezier(0.2, 0, 0, 1) 2;
}

.space-auto-height .uie-card-host {
  height: auto;
}

.space-auto-height .spatial-work-card {
  height: auto;
  max-height: 560px;
}

.space-flow-area .vue-flow__handle {
  opacity: 0;
  pointer-events: none;
}

.space-flow-area .vue-flow__edge-path {
  stroke: var(--text-secondary);
  stroke-width: 1.5;
  transition: stroke 140ms ease, opacity 140ms ease;
}

/* Smart Guides World SVG Overlay */
.space-smart-guides {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  pointer-events: none;
  z-index: 5;
  overflow: visible;
}

.space-guide-line {
  stroke: var(--accent-primary, #7c3aed);
  stroke-width: 1.2;
  vector-effect: non-scaling-stroke;
}

.space-guide-line.is-center {
  stroke-dasharray: 4 3;
}

.space-guide-line.is-spacing {
  stroke: var(--accent-danger, #f43f5e);
  stroke-dasharray: 2 2;
}

.space-guide-label {
  fill: var(--accent-danger, #f43f5e);
  font-size: 11px;
  font-weight: 600;
  font-family: var(--font-mono, monospace);
}

/* Monochrome Translucent Minimap */
.space-minimap {
  margin: 0 16px 16px 0 !important;
  border-radius: 12px !important;
  border: 1px solid var(--material-floating-border, var(--border-muted)) !important;
  background: var(--material-floating-bg, var(--surface-raised)) !important;
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.14);
  overflow: hidden;
  z-index: 9;
}

/* Shared Frosted Glass Floating Islands — strictly respect top 40px window drag chrome! */
.space-island {
  position: absolute;
  top: 48px;
  z-index: 14;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  min-height: 36px;
  padding: 4px 6px;
  border-radius: 12px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 6px 22px rgba(15, 23, 42, 0.12);
  color: var(--text-primary);
}

.space-island-top-left {
  left: 14px;
  max-width: calc(56% - 20px);
}

.space-island-top-right {
  right: 14px;
}

.space-island-sep {
  width: 1px;
  height: 16px;
  background: var(--border-muted);
  margin: 0 2px;
  flex-shrink: 0;
}

.space-island-icon-btn {
  width: 28px;
  height: 28px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease;
}

.space-island-icon-btn:hover:not(:disabled) {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.space-island-icon-btn.is-active {
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
}

.space-island-icon-btn:disabled {
  opacity: 0.38;
  cursor: not-allowed;
}

.space-digest-toggle-btn {
  position: relative;
}

.space-digest-live-dot {
  position: absolute;
  top: 4px;
  right: 4px;
  width: 5px;
  height: 5px;
  border-radius: 50%;
  background: var(--accent-primary);
}

.space-digest-tab-body {
  flex: 1;
  min-height: 0;
  overflow: auto;
}

.space-detail-steer-bar {
  display: flex;
  padding: 10px 14px 0;
}

.space-detail-steer-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 10px;
  border-radius: var(--radius-control, 6px);
  border: 1px solid var(--border-default);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 11.5px;
  font-weight: 500;
  line-height: 1;
  cursor: pointer;
}

.space-detail-steer-btn:hover {
  background: var(--bg-hover);
}

.space-goal-select {
  max-width: 260px;
  min-width: 170px;
}

.space-select-option {
  display: flex;
  width: 100%;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  text-align: left;
  color: var(--text-primary);
  background: transparent;
  border: 0;
  cursor: pointer;
  font-size: 13px;
}

.space-select-option:hover,
.space-select-option-active {
  background: var(--surface-hover);
}

.space-select-option small {
  color: var(--text-muted);
  white-space: nowrap;
}

.space-session-title {
  padding: 0 8px;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 220px;
}

.space-sync-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 2px 8px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 500;
  white-space: nowrap;
}

.space-sync-dot {
  width: 6px;
  height: 6px;
  border-radius: 999px;
  background: var(--text-muted);
}

.space-sync-pill[data-sync='live'] .space-sync-dot {
  background: var(--accent-success, #22c55e);
}

.space-sync-pill[data-sync='syncing'] .space-sync-dot {
  background: var(--accent-warning, #f59e0b);
}

.space-blocker-pill {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 9px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-warning, #f59e0b) 42%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-warning, #f59e0b) 14%, transparent);
  color: var(--accent-warning, #f59e0b);
  font-size: 11.5px;
  font-weight: 600;
  cursor: pointer;
}

/* Unobtrusive Compact Status Pill */
.space-data-status {
  position: absolute;
  top: 90px;
  left: 14px;
  z-index: 12;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 9px;
  border-radius: 999px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  font-size: 11px;
  color: var(--text-secondary);
}

/* Collapsible Layers / Outline Drawer */
.space-layers-drawer {
  position: absolute;
  top: 94px;
  left: 72px;
  bottom: 98px;
  width: 248px;
  z-index: 15;
  display: flex;
  flex-direction: column;
  border-radius: 14px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 12px 32px rgba(15, 23, 42, 0.16);
  overflow: hidden;
}

.space-layers-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 8px 6px 14px;
  border-bottom: 1px solid var(--border-muted);
  font-size: 12.5px;
  color: var(--text-primary);
}

.space-layers-search {
  padding: 8px 10px 6px;
}

.space-layers-search input {
  width: 100%;
  padding: 5px 9px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 12px;
  outline: none;
}

.space-layers-search input:focus {
  border-color: var(--accent-primary);
}

.space-layers-scroll {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 4px 8px 10px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.space-layer-group {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.space-layer-group-title {
  padding: 2px 6px 4px;
  font-size: 10.5px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--text-muted);
}

.space-layer-row {
  display: flex;
  align-items: center;
  gap: 7px;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.space-layer-row:hover {
  background: var(--surface-hover);
}

.space-layer-row.is-selected {
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
}

.space-layer-kind {
  font-size: 10px;
  padding: 1px 5px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  flex-shrink: 0;
}

.space-layer-name {
  min-width: 0;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.space-layers-empty {
  margin: 18px 8px;
  text-align: center;
  font-size: 12px;
  color: var(--text-muted);
}

@keyframes space-drawer-in {
  from {
    opacity: 0;
    transform: translateX(-8px);
  }
  to {
    opacity: 1;
    transform: translateX(0);
  }
}

.space-layers-drawer {
  animation: space-drawer-in 150ms ease;
}

/* Presence Avatars & Zoom Menu */
.space-presence-strip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 0 2px;
}

.space-presence-chip {
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--avatar-color, var(--accent-primary)) 55%, transparent);
  background: color-mix(in srgb, var(--avatar-color, var(--accent-primary)) 22%, var(--surface-raised));
  color: var(--text-primary);
  font-size: 11px;
  font-weight: 700;
  cursor: pointer;
  transition: transform 120ms ease, box-shadow 120ms ease;
}

.space-presence-chip:hover {
  transform: translateY(-1px);
}

.space-presence-chip.is-following {
  box-shadow: 0 0 0 2px var(--surface-raised), 0 0 0 3.5px var(--avatar-color, var(--accent-primary));
}

.space-zoom-wrap {
  position: relative;
}

.space-zoom-trigger {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 8px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  cursor: pointer;
}

.space-zoom-trigger:hover {
  background: var(--surface-hover);
}

.space-lod-tag {
  padding: 1px 5px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 35%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-primary) 12%, transparent);
  color: var(--accent-primary);
  font-size: 10px;
  font-weight: 600;
}

.space-zoom-menu {
  position: absolute;
  top: calc(100% + 8px);
  right: 0;
  width: 236px;
  padding: 6px;
  border-radius: 12px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 14px 36px rgba(15, 23, 42, 0.22);
  display: flex;
  flex-direction: column;
  gap: 2px;
  z-index: 25;
}

.space-zoom-step-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 3px;
  padding: 2px;
}

.space-zoom-step-btn,
.space-zoom-preset {
  height: 26px;
  padding: 0 7px;
  border: 1px solid var(--border-muted);
  border-radius: 7px;
  background: var(--surface-section);
  color: var(--text-primary);
  font-size: 11.5px;
  font-weight: 600;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.space-zoom-step-btn:hover,
.space-zoom-preset:hover {
  background: var(--surface-hover);
}

.space-menu-divider {
  height: 1px;
  background: var(--border-muted);
  margin: 4px 2px;
}

.space-menu-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-primary);
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}

.space-menu-item:hover:not(:disabled) {
  background: var(--surface-hover);
}

.space-menu-item:disabled {
  opacity: 0.42;
  cursor: not-allowed;
}

.space-menu-check {
  margin-left: auto;
  color: var(--accent-primary);
  font-weight: 700;
}

.space-hide-terminal {
  position: absolute;
  top: 94px;
  right: 14px;
  z-index: 15;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  border-radius: 999px;
  padding: 5px 12px;
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-primary);
  font-size: 12px;
  cursor: pointer;
}

/* Left-Center Vertical Figma Tool Dock (centered without transform so inner popover backdrop-filter never breaks) */
.space-tool-dock {
  position: absolute;
  left: 14px;
  top: 0;
  bottom: 0;
  margin-block: auto;
  height: fit-content;
  z-index: 15;
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 6px;
  border-radius: 16px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 10px 28px rgba(15, 23, 42, 0.14);
}

.space-tool-slot {
  position: relative;
}

.space-tool-btn {
  position: relative;
  width: 36px;
  height: 36px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 1px solid transparent;
  border-radius: 10px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease, border-color 120ms ease;
}

.space-tool-btn:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.space-tool-btn.is-active {
  border-color: color-mix(in srgb, var(--accent-primary) 42%, transparent);
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
}

.space-tool-kbd {
  position: absolute;
  right: 3px;
  bottom: 2px;
  font-size: 8.5px;
  font-family: var(--font-mono, monospace);
  line-height: 1;
  opacity: 0.65;
  pointer-events: none;
}

.space-tool-popover {
  position: absolute;
  left: calc(100% + 10px);
  top: 0;
  bottom: 0;
  margin-block: auto;
  height: fit-content;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  border-radius: 999px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 8px 24px rgba(15, 23, 42, 0.16);
  z-index: 20;
  white-space: nowrap;
}

.space-draw-mode-btn {
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 999px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.space-draw-mode-btn.is-active {
  background: color-mix(in srgb, var(--accent-primary) 18%, transparent);
  color: var(--accent-primary);
}

.space-color-dot {
  width: 16px;
  height: 16px;
  border-radius: 999px;
  border: 1px solid rgba(0, 0, 0, 0.18);
  padding: 0;
  cursor: pointer;
  transition: transform 110ms ease;
}

.space-color-dot:hover {
  transform: scale(1.15);
}

.space-color-dot.is-active {
  box-shadow: 0 0 0 2px var(--surface-raised), 0 0 0 3.5px var(--accent-primary);
}

/* Mode & Drop Banners */
.space-mode-pill {
  position: absolute;
  top: 96px;
  left: 0;
  right: 0;
  margin-inline: auto;
  width: fit-content;
  z-index: 12;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 12px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 42%, var(--material-floating-border, var(--border-muted)));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-primary);
  font-size: 12px;
  font-weight: 500;
  pointer-events: none;
}

.space-drop-overlay {
  position: absolute;
  inset: 16px;
  z-index: 30;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  border-radius: 18px;
  border: 1.5px dashed var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 10%, var(--material-floating-bg, var(--surface-raised)));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--accent-primary);
  font-size: 14px;
  font-weight: 600;
  pointer-events: none;
}

/* Right On-Demand Unified Smart Inspector */
.space-inspector {
  position: absolute;
  top: 94px;
  right: 14px;
  bottom: 98px;
  width: min(390px, 44vw);
  min-width: 280px;
  z-index: 16;
  display: flex;
  flex-direction: column;
  border-radius: 14px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 14px 36px rgba(15, 23, 42, 0.18);
  overflow: hidden;
}

.space-inspector-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 8px 8px 8px 10px;
  border-bottom: 1px solid var(--border-muted);
}

.space-inspector-tabs {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px;
  border-radius: 9px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
}

.space-inspector-tab {
  border: 0;
  border-radius: 7px;
  padding: 4px 10px;
  background: transparent;
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
}

.space-inspector-tab.is-active {
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
  font-weight: 600;
}

.space-inspector-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.space-detail {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.space-detail-scroll {
  overflow: auto;
  min-height: 0;
  flex: 1;
}

.space-detail .spatial-work-card {
  height: auto;
  background: transparent;
  backdrop-filter: none;
}

.space-related-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 16px 18px;
  border-top: 1px solid var(--border-muted);
  color: var(--text-secondary);
  font-size: 12px;
}

.space-related-list p {
  margin: 0;
  line-height: 1.6;
}

.space-related-list button {
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--text-primary);
  cursor: pointer;
  padding: 4px;
  text-align: left;
  overflow-wrap: anywhere;
}

.space-related-list button:hover {
  background: var(--surface-hover);
}

.space-relation-record {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.space-style-panel {
  flex: 1;
  overflow: auto;
  padding: 14px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.space-style-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 13px;
  color: var(--text-primary);
}

.space-style-heading small {
  color: var(--text-muted);
  font-family: var(--font-mono, monospace);
}

.space-style-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.space-style-label {
  font-size: 11.5px;
  font-weight: 600;
  color: var(--text-secondary);
}

.space-style-swatches,
.space-style-pills {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.space-style-pill {
  padding: 4px 10px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-secondary);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
}

.space-style-pill.is-active {
  border-color: var(--accent-primary);
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
}

.space-style-input {
  width: 100%;
  padding: 6px 9px;
  border-radius: 8px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  color: var(--text-primary);
  font-size: 12.5px;
  outline: none;
}

.space-style-input:focus {
  border-color: var(--accent-primary);
}

.space-media-meta-row {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--text-secondary);
  overflow-wrap: anywhere;
}

.space-plan-tab-body {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 10px;
}

.space-inspector-empty {
  margin: 28px 16px;
  text-align: center;
  font-size: 12.5px;
  color: var(--text-muted);
  line-height: 1.5;
}

/* Resident Collapsible Bottom ComposerBar (centered without transform so inner glass surfaces sample cleanly) */
.space-composer-dock {
  position: absolute;
  bottom: 14px;
  left: 0;
  right: 0;
  margin-inline: auto;
  width: min(760px, calc(100% - 148px));
  z-index: 16;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  pointer-events: none;
}

.space-composer-dock.is-collapsed {
  width: fit-content;
}

.space-composer-frame {
  position: relative;
  width: 100%;
  pointer-events: auto;
}

.space-composer-frame .composer {
  width: 100%;
  margin: 0;
  padding: 0;
}

.space-composer-collapse-btn {
  position: absolute;
  top: -12px;
  right: 12px;
  z-index: 3;
  width: 24px;
  height: 24px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 999px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-secondary);
  cursor: pointer;
  box-shadow: 0 3px 10px rgba(0, 0, 0, 0.12);
}

.space-composer-collapse-btn:hover {
  color: var(--text-primary);
  border-color: var(--accent-primary);
}

.space-composer-pill {
  pointer-events: auto;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  height: 38px;
  padding: 0 16px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 42%, var(--material-floating-border, var(--border-muted)));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  color: var(--text-primary);
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  box-shadow: 0 10px 28px rgba(15, 23, 42, 0.18);
  transition: transform 130ms ease, border-color 130ms ease, box-shadow 130ms ease;
}

.space-composer-pill:hover {
  transform: translateY(-1px);
  border-color: var(--accent-primary);
}

.space-pill-hotkey {
  padding: 1px 6px;
  border-radius: 5px;
  border: 1px solid var(--border-muted);
  background: var(--surface-section);
  color: var(--text-muted);
  font-size: 11px;
  font-family: var(--font-mono, monospace);
}

.space-selected-context-strip {
  pointer-events: auto;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 4px 6px 4px 12px;
  border-radius: 999px;
  border: 1px solid var(--material-floating-border, var(--border-muted));
  background: var(--material-floating-bg, var(--surface-raised));
  backdrop-filter: var(--material-floating-filter, none);
  -webkit-backdrop-filter: var(--material-floating-filter, none);
  box-shadow: 0 8px 22px rgba(15, 23, 42, 0.14);
  font-size: 12px;
  color: var(--text-primary);
}

.space-context-count {
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.space-context-dispatch {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 24px;
  padding: 0 10px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--accent-primary) 48%, transparent);
  background: color-mix(in srgb, var(--accent-primary) 16%, transparent);
  color: var(--accent-primary);
  font-size: 11.5px;
  font-weight: 600;
  cursor: pointer;
}

.space-context-dispatch:hover {
  background: color-mix(in srgb, var(--accent-primary) 24%, transparent);
}

.space-context-clear {
  width: 22px;
  height: 22px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: 0;
  border-radius: 999px;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.space-context-clear:hover {
  background: var(--surface-hover);
  color: var(--text-primary);
}

.space-meeting-label {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--text-secondary);
  font-size: 12px;
}

@keyframes space-chip-in {
  from {
    opacity: 0;
    transform: translateY(4px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

.space-selected-context-strip {
  animation: space-chip-in 140ms ease;
}

@media (prefers-reduced-motion: reduce) {
  .space-layers-drawer,
  .space-selected-context-strip {
    animation: none;
  }
}

@media (max-width: 820px) {
  .space-goal-select {
    max-width: 180px;
    min-width: 130px;
  }
  .space-composer-dock {
    width: calc(100% - 96px);
  }
}
</style>
