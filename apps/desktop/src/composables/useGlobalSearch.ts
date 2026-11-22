import { ref } from 'vue'
import {
  api,
  type MessageDto,
  type OrchestrationSnapshotDto,
  type ProjectDto,
  type SessionDto,
  type SnapshotDto,
} from '@/api'

type AttachmentRow = Awaited<ReturnType<typeof api.listAttachments>>[number]
import { selectionIdentity, selectionKey } from '@/lib/storageScope'
import type { SpotlightGroup, SpotlightHost, SpotlightItem } from '@/lib/spotlight'

const MIN_DEEP_QUERY_LENGTH = 2
const MAX_SESSIONS_TO_SCAN = 12
const MAX_PROJECTS_TO_SCAN = 8
const MAX_HITS_PER_KIND = 24
const SESSION_CONCURRENCY = 4

export interface GlobalSearchCache {
  generation: number
  messages: Map<string, MessageDto[]>
  attachments: Map<string, AttachmentRow[]>
  orchestrations: Map<string, OrchestrationSnapshotDto | null>
  exposures: Map<string, AttachmentRow[]>
  snapshots: Map<string, SnapshotDto[]>
}

let cache: GlobalSearchCache = createCache(0)

function createCache(generation: number): GlobalSearchCache {
  return {
    generation,
    messages: new Map(),
    attachments: new Map(),
    orchestrations: new Map(),
    exposures: new Map(),
    snapshots: new Map(),
  }
}

export function resetGlobalSearchCache(generation = cache.generation + 1): void {
  cache = createCache(generation)
}

function makeSnippet(text: string, needle: string, radius = 44): string {
  const flat = text.replace(/\s+/g, ' ').trim()
  if (!flat) return ''
  const idx = flat.toLowerCase().indexOf(needle)
  if (idx < 0) return flat.slice(0, 110)
  const start = Math.max(0, idx - radius)
  const end = Math.min(flat.length, idx + needle.length + radius)
  return `${start > 0 ? '…' : ''}${flat.slice(start, end)}${end < flat.length ? '…' : ''}`
}

async function runPool<T>(
  items: readonly T[],
  limit: number,
  worker: (item: T) => Promise<void>,
  signal?: AbortSignal,
): Promise<void> {
  let cursor = 0
  const runners = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (cursor < items.length) {
      if (signal?.aborted) return
      const item = items[cursor++]!
      await worker(item)
    }
  })
  await Promise.all(runners)
}

function dispatchOrchestrationFocus(detail: {
  target: string
  taskId?: string
  taskKey?: string
  handle?: string
  agentSlug?: string
}) {
  if (typeof window === 'undefined' || typeof window.dispatchEvent !== 'function') return
  window.dispatchEvent(new CustomEvent('tinadec:focus-orchestration-node', { detail }))
}

async function getSessionMessages(session: SessionDto, signal?: AbortSignal): Promise<MessageDto[]> {
  if (typeof api.listMessages !== 'function') return []
  const key = selectionKey(session)
  const cached = cache.messages.get(key)
  if (cached) return cached
  try {
    const rows = await api.listMessages(selectionIdentity(key).id, signal)
    const list = Array.isArray(rows) ? rows : []
    cache.messages.set(key, list)
    return list
  } catch {
    return []
  }
}

async function getSessionAttachments(session: SessionDto): Promise<AttachmentRow[]> {
  if (typeof api.listAttachments !== 'function') return []
  const key = selectionKey(session)
  const cached = cache.attachments.get(key)
  if (cached) return cached
  try {
    const rows = await api.listAttachments(selectionIdentity(key).id)
    const list = Array.isArray(rows) ? rows : []
    cache.attachments.set(key, list)
    return list
  } catch {
    return []
  }
}

async function getSessionOrchestration(session: SessionDto, signal?: AbortSignal): Promise<OrchestrationSnapshotDto | null> {
  if (typeof api.getOrchestrationSnapshot !== 'function') return null
  const key = selectionKey(session)
  if (cache.orchestrations.has(key)) return cache.orchestrations.get(key) ?? null
  try {
    const snapshot = await api.getOrchestrationSnapshot(selectionIdentity(key).id, signal)
    const resolved = snapshot && typeof snapshot === 'object' ? snapshot : null
    cache.orchestrations.set(key, resolved)
    return resolved
  } catch {
    return null
  }
}

async function getProjectExposures(project: ProjectDto): Promise<AttachmentRow[]> {
  if (typeof api.listProjectExposures !== 'function') return []
  const key = selectionKey(project)
  const cached = cache.exposures.get(key)
  if (cached) return cached
  try {
    const rows = await api.listProjectExposures(selectionIdentity(key).id)
    const list = Array.isArray(rows) ? rows : []
    cache.exposures.set(key, list)
    return list
  } catch {
    return []
  }
}

async function getProjectSnapshots(project: ProjectDto): Promise<SnapshotDto[]> {
  if (typeof api.listWorkspaceSnapshots !== 'function') return []
  const key = selectionKey(project)
  const cached = cache.snapshots.get(key)
  if (cached) return cached
  try {
    const rows = await api.listWorkspaceSnapshots(selectionIdentity(key).id)
    const list = Array.isArray(rows) ? rows : []
    cache.snapshots.set(key, list)
    return list
  } catch {
    return []
  }
}

export interface GlobalSearchResults {
  messages: SpotlightGroup
  artifacts: SpotlightGroup
  tasks: SpotlightGroup
}

/**
 * Cross-project / cross-session search across session messages, artifacts (attachments,
 * project exposures, snapshots), and orchestration task records.
 *
 * Requires at least 2 characters so opening Spotlight with an empty query incurs zero
 * extra backend fan-out.
 */
export async function searchGlobalRecords(
  query: string,
  sessions: readonly SessionDto[],
  projects: readonly ProjectDto[],
  host: SpotlightHost,
  t: (key: string) => string,
  options: { signal?: AbortSignal } = {},
): Promise<GlobalSearchResults> {
  const empty: GlobalSearchResults = {
    messages: { kind: 'message', items: [] },
    artifacts: { kind: 'artifact', items: [] },
    tasks: { kind: 'task', items: [] },
  }
  const needle = query.trim().toLowerCase()
  if (needle.length < MIN_DEEP_QUERY_LENGTH) return empty
  options.signal?.throwIfAborted()

  const activeProjects = projects
    .filter((p) => (p.lifecycle_status ?? 'active') === 'active')
    .slice(0, MAX_PROJECTS_TO_SCAN)
  const projectNameByKey = new Map(activeProjects.map((p) => [selectionKey(p), p.name || p.path]))
  const projectNameById = new Map(activeProjects.map((p) => [p.id, p.name || p.path]))

  const activeSessions = [...sessions]
    .filter((s) => (s.lifecycle_status ?? 'active') === 'active')
    .sort((a, b) => Date.parse(b.updated_at || '') - Date.parse(a.updated_at || ''))
    .slice(0, MAX_SESSIONS_TO_SCAN)

  const messageItems: SpotlightItem[] = []
  const artifactItems: SpotlightItem[] = []
  const taskItems: SpotlightItem[] = []
  const seenArtifactIds = new Set<string>()
  const seenTaskIds = new Set<string>()

  const resolveProjectLabel = (session: SessionDto): string => {
    if (!session.project_id) return ''
    return (
      projectNameByKey.get(selectionKey({ id: session.project_id, storage_id: session.storage_id }))
      ?? projectNameById.get(session.project_id)
      ?? ''
    )
  }

  // 1. Scan recent active sessions across projects for messages, session attachments, and task records
  await runPool(
    activeSessions,
    SESSION_CONCURRENCY,
    async (session) => {
      if (options.signal?.aborted) return
      const sessionKey = selectionKey(session)
      const sessionTitle = session.title || t('palette.untitledSession')
      const projectLabel = resolveProjectLabel(session)

      const [messages, attachments, orchestration] = await Promise.all([
        messageItems.length < MAX_HITS_PER_KIND ? getSessionMessages(session, options.signal) : Promise.resolve([]),
        artifactItems.length < MAX_HITS_PER_KIND ? getSessionAttachments(session) : Promise.resolve([]),
        taskItems.length < MAX_HITS_PER_KIND ? getSessionOrchestration(session, options.signal) : Promise.resolve(null),
      ])
      if (options.signal?.aborted) return

      // Session messages
      for (const msg of messages) {
        if (messageItems.length >= MAX_HITS_PER_KIND) break
        const body = typeof msg.content === 'string' ? msg.content : ''
        if (!body || !body.toLowerCase().includes(needle)) continue
        messageItems.push({
          id: `message.${sessionKey}.${msg.id}`,
          kind: 'message',
          label: makeSnippet(body, needle),
          detail: [projectLabel, sessionTitle, msg.role].filter(Boolean).join(' · '),
          keywords: t('palette.kwMessageRow'),
          action: () => host.openSession(sessionKey),
        })
      }

      // Session attachments (artifacts)
      for (const att of attachments) {
        if (artifactItems.length >= MAX_HITS_PER_KIND) break
        const id = `artifact.att.${att.id}`
        if (seenArtifactIds.has(id)) continue
        const hay = `${att.file_name} ${att.media_type} ${att.source_path ?? ''} ${sessionTitle}`.toLowerCase()
        if (!hay.includes(needle)) continue
        seenArtifactIds.add(id)
        artifactItems.push({
          id,
          kind: 'artifact',
          label: att.file_name,
          detail: [projectLabel, sessionTitle, att.media_type].filter(Boolean).join(' · '),
          keywords: t('palette.kwArtifactRow'),
          action: () => {
            if (att.source_path) host.openWorkspacePath(att.source_path, session.project_id ?? undefined)
            else host.openSession(sessionKey)
          },
        })
      }

      // Orchestration task nodes & step results
      if (orchestration) {
        const nodes = Array.isArray(orchestration.nodes) ? orchestration.nodes : []
        for (const node of nodes) {
          if (taskItems.length >= MAX_HITS_PER_KIND) break
          const tid = `task.${sessionKey}.${node.id}`
          if (seenTaskIds.has(tid)) continue
          const hay = [
            node.title,
            node.description,
            node.lane_key,
            node.status,
            ...(node.success_criteria ?? []),
          ].filter(Boolean).join(' ').toLowerCase()
          if (!hay.includes(needle)) continue
          seenTaskIds.add(tid)
          taskItems.push({
            id: tid,
            kind: 'task',
            label: node.title || node.id,
            detail: [projectLabel, sessionTitle, node.lane_key, node.status].filter(Boolean).join(' · '),
            keywords: t('palette.kwTaskRow'),
            action: () => {
              host.openSession(sessionKey)
              dispatchOrchestrationFocus({
                target: node.id,
                taskId: node.id,
                taskKey: node.lane_key || undefined,
              })
            },
          })
        }

        const stepResults = Array.isArray(orchestration.step_results) ? orchestration.step_results : []
        for (const step of stepResults) {
          if (taskItems.length >= MAX_HITS_PER_KIND) break
          const tid = `task.${sessionKey}.${step.task_node_id || step.id}`
          if (seenTaskIds.has(tid)) continue
          const hay = [
            step.summary,
            step.agent_id,
            step.status,
            ...(step.evidence ?? []),
          ].filter(Boolean).join(' ').toLowerCase()
          if (!hay.includes(needle)) continue
          seenTaskIds.add(tid)
          taskItems.push({
            id: tid,
            kind: 'task',
            label: makeSnippet(step.summary || step.agent_id || step.id, needle),
            detail: [projectLabel, sessionTitle, step.agent_id, step.status].filter(Boolean).join(' · '),
            keywords: t('palette.kwTaskRow'),
            action: () => {
              host.openSession(sessionKey)
              dispatchOrchestrationFocus({
                target: step.task_node_id || step.id,
                taskId: step.task_node_id || undefined,
                agentSlug: step.agent_id || undefined,
              })
            },
          })
        }
      }
    },
    options.signal,
  )

  // 2. Scan active projects for published artifact exposures & workspace snapshots
  if (artifactItems.length < MAX_HITS_PER_KIND) {
    await runPool(
      activeProjects,
      SESSION_CONCURRENCY,
      async (project) => {
        if (options.signal?.aborted || artifactItems.length >= MAX_HITS_PER_KIND) return
        const projKey = selectionKey(project)
        const projLabel = project.name || project.path
        const [exposures, snapshots] = await Promise.all([
          getProjectExposures(project),
          getProjectSnapshots(project),
        ])
        if (options.signal?.aborted) return

        for (const exp of exposures) {
          if (artifactItems.length >= MAX_HITS_PER_KIND) break
          const id = `artifact.exp.${exp.id}`
          if (seenArtifactIds.has(id)) continue
          const hay = `${exp.file_name} ${exp.media_type} ${exp.source_path ?? ''} ${projLabel}`.toLowerCase()
          if (!hay.includes(needle)) continue
          seenArtifactIds.add(id)
          artifactItems.push({
            id,
            kind: 'artifact',
            label: exp.file_name,
            detail: [projLabel, exp.media_type].filter(Boolean).join(' · '),
            keywords: t('palette.kwArtifactRow'),
            action: () => {
              if (exp.source_path) host.openWorkspacePath(exp.source_path, projKey)
              else host.openProject(projKey)
            },
          })
        }

        for (const snap of snapshots) {
          if (artifactItems.length >= MAX_HITS_PER_KIND) break
          const id = `artifact.snap.${snap.id}`
          if (seenArtifactIds.has(id)) continue
          const hay = `${snap.kind} ${snap.status} ${snap.workspace_hash} ${snap.id} ${projLabel}`.toLowerCase()
          if (!hay.includes(needle)) continue
          seenArtifactIds.add(id)
          artifactItems.push({
            id,
            kind: 'artifact',
            label: `${snap.kind} · ${snap.id.slice(0, 8)}`,
            detail: [projLabel, snap.status, `${snap.file_count} files`].filter(Boolean).join(' · '),
            keywords: t('palette.kwArtifactRow'),
            action: () => host.navigate('snapshots'),
          })
        }
      },
      options.signal,
    )
  }

  return {
    messages: { kind: 'message', items: messageItems, truncated: messageItems.length >= MAX_HITS_PER_KIND },
    artifacts: { kind: 'artifact', items: artifactItems, truncated: artifactItems.length >= MAX_HITS_PER_KIND },
    tasks: { kind: 'task', items: taskItems, truncated: taskItems.length >= MAX_HITS_PER_KIND },
  }
}

export function useGlobalSearch() {
  const searching = ref(false)
  const results = ref<GlobalSearchResults>({
    messages: { kind: 'message', items: [] },
    artifacts: { kind: 'artifact', items: [] },
    tasks: { kind: 'task', items: [] },
  })

  async function run(
    query: string,
    sessions: readonly SessionDto[],
    projects: readonly ProjectDto[],
    host: SpotlightHost,
    t: (key: string) => string,
    signal?: AbortSignal,
  ): Promise<GlobalSearchResults> {
    searching.value = true
    try {
      const next = await searchGlobalRecords(query, sessions, projects, host, t, { signal })
      results.value = next
      return next
    } finally {
      searching.value = false
    }
  }

  return {
    searching,
    results,
    search: run,
    resetCache: resetGlobalSearchCache,
  }
}
