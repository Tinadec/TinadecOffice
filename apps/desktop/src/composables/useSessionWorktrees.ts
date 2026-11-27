import { computed, onScopeDispose, ref, shallowRef, toValue, watch, type MaybeRefOrGetter } from 'vue'
import {
  api,
  type OrchestrationSnapshotDto,
  type SessionTopologyDto,
  type ToolExecutionTimelineItemDto,
} from '@/api'
import type {
  GitDiffSection,
  GitPreviewData,
  GitStatusFile,
} from '@/composables/useGitOperation'
import type { ConversationFeed } from '@/composables/useConversationFeed'
import type { SubAgent } from '@/lib/conversation/reducer'
import type { TurnRunInput } from '@/lib/conversation/turns'

export interface RawWorktreeEntry {
  path: string
  branch?: string | null
  detached?: string | null
  head?: string | null
  commit?: string | null
  is_current?: boolean
  current?: boolean
  is_main?: boolean
  main?: boolean
}

export interface WorktreeDiffFile {
  path: string
  previousPath?: string | null
  changeType: string
  statusBadge: 'A' | 'M' | 'D'
  additions: number
  deletions: number
  binary: boolean
  truncated: boolean
  diffText: string
}

export interface WorktreeDiffCounts {
  added: number
  modified: number
  deleted: number
  totalFiles: number
  additions: number
  deletions: number
}

export interface WorktreeLineageStep {
  taskId: string
  handle: string | null
  agentSlug: string | null
  label: string
}

export interface MainWorkspaceWorkerRef {
  runId: string
  taskId: string
  handle: string | null
  agentSlug: string | null
  label: string
  status: string
}

export interface WorkerWorktreeGroup {
  id: string
  scope: 'main' | 'isolated'
  isMain: boolean
  isCurrent: boolean
  worktreePath: string
  branch: string
  detached: string | null
  head: string | null
  runId: string | null
  taskId: string | null
  taskKey: string | null
  workerHandle: string | null
  agentSlug: string | null
  workerLabel: string | null
  taskTitle: string | null
  taskStatus: string | null
  lineage: WorktreeLineageStep[]
  sharedWorkers: MainWorkspaceWorkerRef[]
  counts: WorktreeDiffCounts
  files: WorktreeDiffFile[]
  rawDiff: string
  canCleanup: boolean
}

export interface WorktreeDiffSnapshot {
  branch?: string | null
  upstream?: string | null
  ahead?: number
  behind?: number
  files: GitStatusFile[]
  sections: GitDiffSection[]
}

export function normalizeGitPath(raw: string | null | undefined): string {
  if (!raw) return ''
  const forward = String(raw).trim().replace(/\\/g, '/').replace(/\/+/g, '/')
  if (forward.length > 1 && forward.endsWith('/')) return forward.slice(0, -1)
  return forward
}

export function pathComparisonKey(raw: string | null | undefined): string {
  const norm = normalizeGitPath(raw)
  return /^[A-Za-z]:\//.test(norm) ? norm.toLowerCase() : norm
}

/** True when `candidate` equals `root` or resolves cleanly underneath `root`. */
export function isPathWithinRoot(
  candidate: string | null | undefined,
  root: string | null | undefined,
): boolean {
  const candKey = pathComparisonKey(candidate)
  const rootKey = pathComparisonKey(root)
  if (!candKey || !rootKey) return false
  if (candKey === rootKey) return true
  const prefix = rootKey.endsWith('/') ? rootKey : `${rootKey}/`
  return candKey.startsWith(prefix)
}

function normalizeUuidHex(raw: string | null | undefined): string {
  return (raw ?? '').trim().toLowerCase().replace(/[^a-f0-9]/g, '')
}

function isManagedWorktreePath(path: string, mainRootPath: string | null): boolean {
  const norm = pathComparisonKey(path)
  if (!norm) return false
  if (mainRootPath && norm === pathComparisonKey(mainRootPath)) return false
  return norm.includes('/.tinadec/worktrees/')
}

/**
 * Split a unified git diff bundle (`diff --git a/... b/...`) into per-file sections so
 * 1-click file diff inspection focuses straight on the clicked path.
 */
export function splitUnifiedDiffByFile(diffText: string | null | undefined): Map<string, string> {
  const map = new Map<string, string>()
  if (!diffText) return map
  const lines = diffText.split('\n')
  let currentPaths: string[] = []
  let buffer: string[] = []

  const flush = () => {
    if (!buffer.length || !currentPaths.length) return
    const chunk = buffer.join('\n')
    for (const key of currentPaths) {
      if (key && !map.has(key)) map.set(key, chunk)
    }
  }

  for (const line of lines) {
    const header = line.match(/^diff --git a\/(.+?) b\/(.+)$/)
    if (header) {
      flush()
      buffer = [line]
      currentPaths = [normalizeGitPath(header[2]), normalizeGitPath(header[1])]
    } else {
      if (!buffer.length && line.startsWith('--- a/')) {
        currentPaths = [normalizeGitPath(line.slice(6))]
      }
      buffer.push(line)
    }
  }
  flush()
  return map
}

function statusBadgeFor(changeType?: string | null, status?: string | null, untracked?: boolean): 'A' | 'M' | 'D' {
  const type = (changeType ?? '').toLowerCase()
  const st = (status ?? '').trim().toUpperCase()
  if (untracked || type === 'added' || type === 'untracked' || st === 'A' || st === '??') return 'A'
  if (type === 'deleted' || st === 'D') return 'D'
  return 'M'
}

function emptyCounts(): WorktreeDiffCounts {
  return { added: 0, modified: 0, deleted: 0, totalFiles: 0, additions: 0, deletions: 0 }
}

/** Compute `+A / ~M / -D` file counts, line stats, and inspectable file list from git preview data. */
export function summarizeWorktreeDiff(snapshot: Partial<WorktreeDiffSnapshot> | GitPreviewData | null | undefined): {
  counts: WorktreeDiffCounts
  files: WorktreeDiffFile[]
  rawDiff: string
} {
  if (!snapshot) return { counts: emptyCounts(), files: [], rawDiff: '' }

  const sections = Array.isArray(snapshot.sections) ? snapshot.sections : []
  const statusFiles = Array.isArray(snapshot.files) ? snapshot.files : []

  const rawDiff = sections.map((s: GitDiffSection) => s.diff).filter(Boolean).join('\n')
  const diffByPath = splitUnifiedDiffByFile(rawDiff)
  const fileMap = new Map<string, WorktreeDiffFile>()

  for (const section of sections) {
    for (const file of section.files ?? []) {
      const path = normalizeGitPath(file.path)
      if (!path) continue
      const existing = fileMap.get(path)
      const diffChunk = diffByPath.get(path) ?? diffByPath.get(normalizeGitPath(file.previous_path)) ?? ''
      const badge = statusBadgeFor(file.change_type)
      if (existing) {
        existing.additions += file.additions ?? 0
        existing.deletions += file.deletions ?? 0
        if (!existing.diffText && diffChunk) existing.diffText = diffChunk
      } else {
        fileMap.set(path, {
          path,
          previousPath: file.previous_path ?? null,
          changeType: file.change_type || 'modified',
          statusBadge: badge,
          additions: file.additions ?? 0,
          deletions: file.deletions ?? 0,
          binary: Boolean(file.binary),
          truncated: Boolean(file.truncated),
          diffText: diffChunk,
        })
      }
    }
  }

  for (const statusFile of statusFiles) {
    const path = normalizeGitPath(statusFile.path)
    if (!path || fileMap.has(path)) continue
    const rawStatus = statusFile.status ?? statusFile.unstaged_status ?? statusFile.staged_status ?? ''
    const badge = statusBadgeFor(undefined, rawStatus, statusFile.is_untracked)
    fileMap.set(path, {
      path,
      previousPath: statusFile.previous_path ?? null,
      changeType: badge === 'A' ? 'added' : badge === 'D' ? 'deleted' : 'modified',
      statusBadge: badge,
      additions: 0,
      deletions: 0,
      binary: false,
      truncated: false,
      diffText: diffByPath.get(path) ?? '',
    })
  }

  const files = Array.from(fileMap.values())
  const counts = emptyCounts()
  counts.totalFiles = files.length
  for (const file of files) {
    if (file.statusBadge === 'A') counts.added += 1
    else if (file.statusBadge === 'D') counts.deleted += 1
    else counts.modified += 1
    counts.additions += file.additions
    counts.deletions += file.deletions
  }

  return { counts, files, rawDiff }
}

interface InternalTaskRecord {
  runId: string
  taskId: string
  taskKey: string | null
  title: string | null
  status: string
  handle: string | null
  agentSlug: string | null
  dispatchedByTaskId: string | null
  dispatchedByHandle: string | null
  isMain: boolean
  hintedWorktreePath: string | null
}

function buildLineage(
  task: InternalTaskRecord | null,
  byTaskId: ReadonlyMap<string, InternalTaskRecord>,
  byRunAndHandle: ReadonlyMap<string, InternalTaskRecord>,
  resolveName: (slugOrHandle: string | null | undefined) => string,
): WorktreeLineageStep[] {
  if (!task) return []
  const chain: WorktreeLineageStep[] = []
  const visited = new Set<string>()
  let cursor: InternalTaskRecord | undefined = task
  let depth = 0
  while (cursor && depth < 8 && !visited.has(cursor.taskId)) {
    visited.add(cursor.taskId)
    const display = resolveName(cursor.handle ?? cursor.agentSlug) || cursor.taskKey || cursor.taskId
    chain.unshift({
      taskId: cursor.taskId,
      handle: cursor.handle,
      agentSlug: cursor.agentSlug,
      label: display,
    })
    if (cursor.dispatchedByTaskId) {
      cursor = byTaskId.get(cursor.dispatchedByTaskId)
    } else if (cursor.dispatchedByHandle) {
      cursor = byRunAndHandle.get(`${cursor.runId}:${cursor.dispatchedByHandle}`)
    } else {
      cursor = undefined
    }
    depth += 1
  }
  return chain
}

export interface BuildWorkerWorktreeGroupsInput {
  mainRootPath: string | null
  worktrees: RawWorktreeEntry[]
  mainPreview?: WorktreeDiffSnapshot | GitPreviewData | null
  diffByWorktreePath?: ReadonlyMap<string, WorktreeDiffSnapshot>
  topology?: SessionTopologyDto | null
  feed?: ConversationFeed | null
  runs?: readonly TurnRunInput[]
  toolExecutions?: readonly ToolExecutionTimelineItemDto[]
  resolveAgentName?: (slugOrHandle: string | null | undefined) => string
}

/**
 * Pure builder combining git worktrees, diff previews, topology task/lease graphs,
 * and live conversation feed state into worker-grouped worktree view models.
 */
export function buildWorkerWorktreeGroups(input: BuildWorkerWorktreeGroupsInput): WorkerWorktreeGroup[] {
  const resolveName = input.resolveAgentName ?? ((s) => s ?? '')
  const mainKey = pathComparisonKey(input.mainRootPath)
  const taskById = new Map<string, InternalTaskRecord>()
  const taskByHexId = new Map<string, InternalTaskRecord>()
  const taskByRunAndHandle = new Map<string, InternalTaskRecord>()

  const upsertTask = (next: InternalTaskRecord) => {
    const prev = taskById.get(next.taskId)
    const merged: InternalTaskRecord = prev
      ? {
          runId: prev.runId || next.runId,
          taskId: prev.taskId,
          taskKey: next.taskKey ?? prev.taskKey,
          title: next.title ?? prev.title,
          status: next.status || prev.status,
          handle: next.handle ?? prev.handle,
          agentSlug: next.agentSlug ?? prev.agentSlug,
          dispatchedByTaskId: next.dispatchedByTaskId ?? prev.dispatchedByTaskId,
          dispatchedByHandle: next.dispatchedByHandle ?? prev.dispatchedByHandle,
          isMain: prev.isMain || next.isMain,
          hintedWorktreePath: next.hintedWorktreePath ?? prev.hintedWorktreePath,
        }
      : next
    taskById.set(merged.taskId, merged)
    const hex = normalizeUuidHex(merged.taskId)
    if (hex) taskByHexId.set(hex, merged)
    if (merged.handle) taskByRunAndHandle.set(`${merged.runId}:${merged.handle}`, merged)
  }

  for (const run of input.topology?.runs ?? []) {
    for (const task of run.tasks ?? []) {
      upsertTask({
        runId: run.run_id,
        taskId: task.task_id,
        taskKey: task.task_key,
        title: task.title,
        status: task.status,
        handle: task.handle ?? null,
        agentSlug: task.agent_slug ?? null,
        dispatchedByTaskId: task.dispatched_by_task_id ?? null,
        dispatchedByHandle: null,
        isMain: false,
        hintedWorktreePath: null,
      })
    }
  }

  if (input.feed) {
    void input.feed.revision.value
    for (const [runId, activity] of input.feed.state.value.runs) {
      const dispatchParents = new Map<string, string | null>()
      const entryWorktrees = new Map<string, string>()
      for (const entry of activity.entries) {
        if (entry.kind === 'dispatch') {
          dispatchParents.set(entry.taskId, entry.parentTaskId)
        } else if (entry.kind === 'tool' && entry.taskId) {
          if (entry.cwd && isManagedWorktreePath(entry.cwd, input.mainRootPath)) {
            entryWorktrees.set(entry.taskId, normalizeGitPath(entry.cwd))
          }
        }
      }
      for (const sub of activity.tasks.values()) {
        upsertTask({
          runId,
          taskId: sub.taskId,
          taskKey: sub.taskKey,
          title: sub.title,
          status: sub.status,
          handle: sub.handle,
          agentSlug: sub.agentSlug,
          dispatchedByTaskId: dispatchParents.get(sub.taskId) ?? null,
          dispatchedByHandle: sub.dispatchedBy,
          isMain: sub.isMain,
          hintedWorktreePath: entryWorktrees.get(sub.taskId) ?? null,
        })
      }
    }
  }

  const worktreeTaskLease = new Map<string, { runId: string | null; taskId: string | null; path: string }>()
  for (const lease of input.topology?.leases ?? []) {
    if (lease.kind !== 'worktree' || !lease.resource_key) continue
    const key = pathComparisonKey(lease.resource_key)
    worktreeTaskLease.set(key, {
      runId: lease.run_id ?? null,
      taskId: lease.task_id ?? null,
      path: normalizeGitPath(lease.resource_key),
    })
  }

  const normalizedEntries = new Map<string, RawWorktreeEntry>()
  for (const raw of input.worktrees) {
    const path = normalizeGitPath(raw.path)
    if (!path) continue
    const key = pathComparisonKey(path)
    if (!normalizedEntries.has(key)) {
      normalizedEntries.set(key, { ...raw, path })
    }
  }

  // Ensure worktree leases and feed task worktree cwds are visible even before git worktrees lists them.
  for (const [key, lease] of worktreeTaskLease) {
    if (!normalizedEntries.has(key) && lease.path) {
      normalizedEntries.set(key, {
        path: lease.path,
        branch: null,
        is_main: mainKey ? key === mainKey : false,
        is_current: false,
      })
    }
  }
  for (const task of taskById.values()) {
    if (!task.hintedWorktreePath) continue
    const key = pathComparisonKey(task.hintedWorktreePath)
    if (!normalizedEntries.has(key)) {
      normalizedEntries.set(key, {
        path: task.hintedWorktreePath,
        branch: null,
        is_main: false,
        is_current: false,
      })
    }
  }
  if (input.mainRootPath && !normalizedEntries.has(mainKey)) {
    normalizedEntries.set(mainKey, {
      path: normalizeGitPath(input.mainRootPath),
      branch: input.mainPreview?.branch ?? null,
      is_main: true,
      is_current: true,
    })
  }

  const assignedTaskIds = new Set<string>()
  const isolatedGroups: WorkerWorktreeGroup[] = []
  let mainGroup: WorkerWorktreeGroup | null = null

  for (const [key, entry] of normalizedEntries) {
    const isMain = entry.is_main === true || entry.main === true || (Boolean(mainKey) && key === mainKey)
    const isCurrent = entry.is_current === true || entry.current === true || isMain

    const lease = worktreeTaskLease.get(key)
    let matchedTask: InternalTaskRecord | null = (lease?.taskId && taskById.get(lease.taskId)) || null
    if (!matchedTask) {
      for (const task of taskById.values()) {
        if (task.hintedWorktreePath && pathComparisonKey(task.hintedWorktreePath) === key) {
          matchedTask = task
          break
        }
      }
    }
    if (!matchedTask) {
      const taskMatch = key.match(/\/\.tinadec\/worktrees\/task-([a-f0-9-]+)/i)
      if (taskMatch?.[1]) {
        matchedTask = taskByHexId.get(normalizeUuidHex(taskMatch[1])) ?? null
      }
    }
    if (!matchedTask && lease?.runId) {
      const runTasks = Array.from(taskById.values()).filter((t) => t.runId === lease.runId && !t.isMain)
      matchedTask = runTasks[0] ?? null
    }
    if (matchedTask && !isMain) assignedTaskIds.add(matchedTask.taskId)

    const diffSnapshot = input.diffByWorktreePath?.get(key) ?? (isMain ? input.mainPreview : undefined)
    const diffSummary = summarizeWorktreeDiff(diffSnapshot)
    const branchName =
      entry.branch ??
      diffSnapshot?.branch ??
      entry.detached ??
      (entry.head || entry.commit ? String(entry.head || entry.commit).slice(0, 7) : '')
    const lineage = matchedTask && !isMain ? buildLineage(matchedTask, taskById, taskByRunAndHandle, resolveName) : []
    const workerLabel = matchedTask
      ? (resolveName(matchedTask.handle ?? matchedTask.agentSlug) || matchedTask.taskKey || matchedTask.taskId)
      : null

    const group: WorkerWorktreeGroup = {
      id: `${isMain ? 'main' : 'isolated'}:${key}`,
      scope: isMain ? 'main' : 'isolated',
      isMain,
      isCurrent,
      worktreePath: normalizeGitPath(entry.path),
      branch: branchName,
      detached: entry.detached ?? null,
      head: entry.head ?? entry.commit ?? null,
      runId: matchedTask?.runId ?? lease?.runId ?? null,
      taskId: matchedTask?.taskId ?? lease?.taskId ?? null,
      taskKey: matchedTask?.taskKey ?? null,
      workerHandle: matchedTask?.handle ?? null,
      agentSlug: matchedTask?.agentSlug ?? null,
      workerLabel,
      taskTitle: matchedTask?.title ?? null,
      taskStatus: matchedTask?.status ?? null,
      lineage,
      sharedWorkers: [],
      counts: diffSummary.counts,
      files: diffSummary.files,
      rawDiff: diffSummary.rawDiff,
      canCleanup: !isMain && !isCurrent,
    }

    if (isMain) mainGroup = group
    else isolatedGroups.push(group)
  }

  // Workers that share the main project root (not bound to any isolated worktree)
  if (mainGroup) {
    const shared: MainWorkspaceWorkerRef[] = []
    for (const task of taskById.values()) {
      if (task.isMain || assignedTaskIds.has(task.taskId)) continue
      shared.push({
        runId: task.runId,
        taskId: task.taskId,
        handle: task.handle,
        agentSlug: task.agentSlug,
        label: resolveName(task.handle ?? task.agentSlug) || task.taskKey || task.taskId,
        status: task.status,
      })
    }
    mainGroup.sharedWorkers = shared
  }

  const isActiveStatus = (s: string | null) => s === 'running' || s === 'awaiting_approval' || s === 'pending'
  isolatedGroups.sort((a, b) => Number(isActiveStatus(b.taskStatus)) - Number(isActiveStatus(a.taskStatus)))

  return mainGroup ? [...isolatedGroups, mainGroup] : isolatedGroups
}

export interface UseSessionWorktreesOptions {
  projectPath: MaybeRefOrGetter<string | null | undefined>
  sessionId: MaybeRefOrGetter<string | null | undefined>
  runs?: MaybeRefOrGetter<readonly TurnRunInput[] | undefined>
  feed?: MaybeRefOrGetter<ConversationFeed | undefined>
  orchestration?: MaybeRefOrGetter<OrchestrationSnapshotDto | null | undefined>
  toolExecutions?: MaybeRefOrGetter<readonly ToolExecutionTimelineItemDto[] | undefined>
  mainPreviewData?: MaybeRefOrGetter<WorktreeDiffSnapshot | GitPreviewData | null | undefined>
  externalWorktrees?: MaybeRefOrGetter<RawWorktreeEntry[] | undefined>
  deferInitialProbe?: boolean
  resolveAgentName?: (slugOrHandle: string | null | undefined) => string
}

const MAX_WORKTREE_DIFF_PROBES = 8
const REFRESH_DEBOUNCE_MS = 250

const inFlightWorktreeLists = new Map<string, Promise<unknown>>()
const inFlightTopologies = new Map<string, Promise<SessionTopologyDto | null>>()
const inFlightDiffProbes = new Map<string, Promise<unknown>>()

function coalesceAsync<T>(map: Map<string, Promise<T>>, key: string, factory: () => Promise<T>): Promise<T> {
  const existing = map.get(key)
  if (existing) return existing
  const promise = factory().finally(() => {
    if (map.get(key) === promise) map.delete(key)
  })
  map.set(key, promise)
  return promise
}

export function useSessionWorktrees(options: UseSessionWorktreesOptions) {
  const worktrees = ref<RawWorktreeEntry[]>([])
  const topology = shallowRef<SessionTopologyDto | null>(null)
  const diffByWorktreePath = shallowRef<Map<string, WorktreeDiffSnapshot>>(new Map())
  const loading = ref(false)
  let generation = 0
  let probeGeneration = 0
  let debounceTimer: ReturnType<typeof setTimeout> | null = null
  let disposed = false

  function extractRawWorktrees(data: Record<string, unknown> | undefined): RawWorktreeEntry[] {
    if (!data || !Array.isArray(data.worktrees)) return []
    const items: RawWorktreeEntry[] = []
    for (const row of data.worktrees) {
      if (!row || typeof row !== 'object') continue
      const record = row as Record<string, unknown>
      const path = String(record.path ?? record.worktree ?? '').trim()
      if (!path) continue
      items.push({
        path,
        branch: typeof record.branch === 'string' ? record.branch : null,
        detached: typeof record.detached === 'string' ? record.detached : null,
        head: typeof record.head === 'string' ? record.head : typeof record.commit === 'string' ? record.commit : null,
        is_current: record.is_current === true || record.current === true,
        is_main: record.is_main === true || record.main === true,
      })
    }
    return items
  }

  async function probeWorktreeDiffs(
    rootPath: string | null,
    rawList: RawWorktreeEntry[],
    topoRes: SessionTopologyDto | null,
  ): Promise<void> {
    const readProbe = ++probeGeneration
    const mainKey = pathComparisonKey(rootPath)
    const probePaths = new Map<string, string>()
    for (const wt of rawList) {
      const key = pathComparisonKey(wt.path)
      if (!wt.is_main && key && key !== mainKey && rootPath && isPathWithinRoot(wt.path, rootPath)) {
        probePaths.set(key, wt.path)
      }
    }
    for (const lease of topoRes?.leases ?? []) {
      if (lease.kind === 'worktree' && lease.resource_key) {
        const key = pathComparisonKey(lease.resource_key)
        if (key && key !== mainKey && !probePaths.has(key) && rootPath && isPathWithinRoot(lease.resource_key, rootPath)) {
          probePaths.set(key, lease.resource_key)
        }
      }
    }
    if (rootPath && !toValue(options.mainPreviewData) && !options.deferInitialProbe && mainKey) {
      probePaths.set(mainKey, rootPath)
    }

    const targets = Array.from(probePaths.entries()).slice(0, MAX_WORKTREE_DIFF_PROBES)
    if (!targets.length || typeof api?.executeCodeTool !== 'function') {
      diffByWorktreePath.value = new Map()
      return
    }
    const results = await Promise.all(
      targets.map(async ([key, wtPath]) => {
        const lookupCwd = rootPath || wtPath
        const isSubWorktree = Boolean(rootPath && pathComparisonKey(wtPath) !== mainKey)
        const probeCacheKey = `${lookupCwd}|${isSubWorktree ? wtPath : ''}`
        const res = await coalesceAsync(
          inFlightDiffProbes,
          probeCacheKey,
          () => api
            .executeCodeTool('git_worktree_manager', {
              cwd: lookupCwd,
              arguments: isSubWorktree
                ? { action: 'diff_preview', repository_path: wtPath, max_files: 80, max_diff_bytes: 120000 }
                : { action: 'diff_preview', max_files: 80, max_diff_bytes: 120000 },
            })
            .catch(() => null),
        ) as { data?: Record<string, unknown> } | null
        return [key, res?.data] as const
      }),
    )
    if (disposed || readProbe !== probeGeneration) return
    const nextMap = new Map<string, WorktreeDiffSnapshot>()
    for (const [key, data] of results) {
      if (!data) continue
      nextMap.set(key, {
        branch: typeof data.branch === 'string' ? data.branch : null,
        upstream: typeof data.upstream === 'string' ? data.upstream : null,
        ahead: typeof data.ahead === 'number' ? data.ahead : 0,
        behind: typeof data.behind === 'number' ? data.behind : 0,
        files: Array.isArray(data.files) ? (data.files as GitStatusFile[]) : [],
        sections: Array.isArray(data.sections) ? (data.sections as GitDiffSection[]) : [],
      })
    }
    diffByWorktreePath.value = nextMap
  }

  async function refresh(includeWorktreeQuery = true): Promise<void> {
    if (disposed) return
    const rootPath = toValue(options.projectPath) ?? null
    const sessionId = toValue(options.sessionId) ?? null
    if (!rootPath && !sessionId) {
      worktrees.value = []
      topology.value = null
      diffByWorktreePath.value = new Map()
      return
    }
    const readGen = ++generation
    loading.value = true
    try {
      const shouldQueryWorktrees = includeWorktreeQuery && (!options.deferInitialProbe || !toValue(options.externalWorktrees))
      const [wtRes, topoRes] = await Promise.all([
        rootPath && shouldQueryWorktrees && typeof api?.executeCodeTool === 'function'
          ? coalesceAsync(
              inFlightWorktreeLists,
              pathComparisonKey(rootPath),
              () => api.executeCodeTool('git_worktree_manager', { cwd: rootPath, arguments: { action: 'worktrees' } }).catch(() => null),
            ) as Promise<{ data?: Record<string, unknown> } | null>
          : Promise.resolve(null),
        sessionId && typeof api?.getSessionTopology === 'function'
          ? coalesceAsync(
              inFlightTopologies,
              sessionId,
              () => api.getSessionTopology(sessionId, { include_finished: true, max_runs: 25, max_tasks: 120 }).catch(() => null),
            )
          : Promise.resolve(null),
      ])
      if (disposed || readGen !== generation) return

      const rawList = shouldQueryWorktrees
        ? extractRawWorktrees(wtRes?.data as Record<string, unknown> | undefined)
        : (toValue(options.externalWorktrees) ?? [])
      worktrees.value = rawList
      topology.value = topoRes
      await probeWorktreeDiffs(rootPath, rawList, topoRes)
    } finally {
      if (!disposed && readGen === generation) loading.value = false
    }
  }

  function scheduleRefresh(includeWorktreeQuery = true): void {
    if (disposed) return
    if (debounceTimer !== null) clearTimeout(debounceTimer)
    debounceTimer = setTimeout(() => {
      debounceTimer = null
      void refresh(includeWorktreeQuery)
    }, REFRESH_DEBOUNCE_MS)
  }

  watch(
    [() => toValue(options.projectPath), () => toValue(options.sessionId)],
    () => {
      if (options.deferInitialProbe) {
        // Still load session topology on session switch, without double-querying git on initial mount.
        void refresh(false)
      } else {
        void refresh(true)
      }
    },
    { immediate: true },
  )

  watch(
    () => toValue(options.externalWorktrees),
    (next) => {
      if (!Array.isArray(next)) return
      worktrees.value = next
      const rootPath = toValue(options.projectPath) ?? null
      const mainKey = pathComparisonKey(rootPath)
      if (rootPath && next.some((wt) => !wt.is_main && pathComparisonKey(wt.path) !== mainKey && isPathWithinRoot(wt.path, rootPath))) {
        void probeWorktreeDiffs(rootPath, next, topology.value)
      }
    },
  )

  watch(
    [
      () => toValue(options.feed)?.revision.value,
      () => toValue(options.toolExecutions)?.length,
      () => toValue(options.orchestration)?.run?.status,
    ],
    () => {
      scheduleRefresh(!options.deferInitialProbe)
    },
  )

  const groups = computed<WorkerWorktreeGroup[]>(() =>
    buildWorkerWorktreeGroups({
      mainRootPath: toValue(options.projectPath) ?? null,
      worktrees: worktrees.value.length ? worktrees.value : (toValue(options.externalWorktrees) ?? []),
      mainPreview: toValue(options.mainPreviewData) ?? null,
      diffByWorktreePath: diffByWorktreePath.value,
      topology: topology.value,
      feed: toValue(options.feed) ?? null,
      runs: toValue(options.runs),
      toolExecutions: toValue(options.toolExecutions),
      resolveAgentName: options.resolveAgentName,
    }),
  )

  const isolatedGroups = computed(() => groups.value.filter((group) => !group.isMain))
  const mainGroup = computed(() => groups.value.find((group) => group.isMain) ?? null)

  onScopeDispose(() => {
    disposed = true
    if (debounceTimer !== null) clearTimeout(debounceTimer)
    debounceTimer = null
  })

  return {
    worktrees,
    topology,
    groups,
    isolatedGroups,
    mainGroup,
    loading,
    refresh,
  }
}

export type { SubAgent }
