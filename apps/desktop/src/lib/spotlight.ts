import i18n from '@/i18n'
import { loadSessionCatalog } from '@/lib/sessionRoster'
import { useHostAccess } from '@/lib/hostAccess'
/**
 * The spotlight's item model and its search engine.
 *
 * `appCommands` already answers "what can I do"; the spotlight adds "what can I
 * open": projects, conversations, configuration objects, tools and workspace
 * content, grouped so the list reads as categories instead of one flat soup.
 *
 * Configuration catalogs are cached for one opening. Tool discovery and current
 * workspace content use the component's debounced query; Core owns those searches.
 */
import {
  api,
  type AgentDirectoryItemDto,
  type AgentModeDto,
  type ModelProviderInstanceDto,
  type ProjectDto,
  type PromptFragmentDto,
  type SessionDto,
} from '@/api'
import { scoreMatch } from '@/lib/appCommands'
import { selectionKey } from '@/lib/storageScope'
import { searchedFilePaths, toWorkspaceRelative, type FileSearchDataDto } from '@/lib/workspaceSearch'

export type SpotlightKind = 'command' | 'project' | 'conversation' | 'model' | 'agent' | 'mode' | 'prompt' | 'tool' | 'setting' | 'resource'

export interface SpotlightItem {
  id: string
  kind: SpotlightKind
  /** The row's main text. */
  label: string
  /** Secondary context — a route, a provider driver, a path. */
  detail?: string
  /** Extra lowercase terms the label does not already carry. */
  keywords?: string
  action(): void
}

export interface SpotlightGroup {
  kind: SpotlightKind
  items: SpotlightItem[]
  /** Failed sources remain visible alongside any cached rows. */
  error?: string
  /** The backend bounded this source; this is not an exact total. */
  truncated?: boolean
}

/** Order the groups render in. Commands first: an open palette with no query is a launcher. */
export const spotlightKindOrder: readonly SpotlightKind[] = [
  'command',
  'project',
  'conversation',
  'resource',
  'agent',
  'mode',
  'prompt',
  'model',
  'tool',
  'setting',
]

export function spotlightKindLabel(kind: SpotlightKind): string {
  return `palette.kind${kind.charAt(0).toUpperCase()}${kind.slice(1)}`
}

// ── settings sections ────────────────────────────────────────────────

/**
 * One entry per settings section the spotlight can jump to. `key` and `labelKey`
 * mirror the page's `navItems`; if the page gains a section, a row belongs here
 * too, one keystroke of the same name. The keywords are this module's own — the
 * nav has no use for search terms.
 */
export interface SettingsSectionEntry {
  key: string
  labelKey: string
  keywordKeys: string[]
}

export const settingsSections: readonly SettingsSectionEntry[] = [
  { key: 'personal', labelKey: 'settings.personal', keywordKeys: ['palette.kwPersonal'] },
  // Language now lives inside General, so its search term rides the same row.
  { key: 'general', labelKey: 'settings.general', keywordKeys: ['palette.kwGeneral', 'palette.kwLanguage'] },
  { key: 'model', labelKey: 'settings.model', keywordKeys: ['palette.kwModel'] },
  { key: 'agentCenter', labelKey: 'settings.agentCenter', keywordKeys: ['palette.kwAgentCenter'] },
  { key: 'tools', labelKey: 'settings.toolLayer', keywordKeys: ['palette.kwTools'] },
  { key: 'tinachat', labelKey: 'tinaChat.manage', keywordKeys: ['palette.kwTinaChat'] },
  { key: 'archive', labelKey: 'settings.archiveTrash', keywordKeys: ['palette.kwArchive'] },
  { key: 'appearance', labelKey: 'settings.appearance', keywordKeys: ['palette.kwAppearance'] },
  { key: 'pets', labelKey: 'settings.pets', keywordKeys: ['palette.kwPets'] },
  { key: 'apiDocs', labelKey: 'settings.apiDocs', keywordKeys: ['palette.kwApiDocs'] },
  { key: 'about', labelKey: 'settings.about', keywordKeys: ['palette.kwAbout'] },
]

// ── dynamic snapshots ────────────────────────────────────────────────

interface Catalogs {
  sessions: SessionDto[]
  providers: ModelProviderInstanceDto[]
  projects: ProjectDto[]
  agents: AgentDirectoryItemDto[]
  modes: AgentModeDto[]
  prompts: PromptFragmentDto[]
}

interface CatalogResult<T> { rows: T[]; error?: string }
const catalogCache = new Map<keyof Catalogs, CatalogResult<unknown>>()
const catalogRequests = new Map<keyof Catalogs, { signal?: AbortSignal; promise: Promise<CatalogResult<unknown>> }>()
let snapshotGeneration = 0

/** Visible for tests. */
export function __resetSpotlightForTests(): void {
  refreshSpotlight()
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

/** A source owns a real fetch signal and cannot keep the search pending indefinitely. */
async function boundedSource<T>(load: (signal: AbortSignal) => Promise<T>, parent?: AbortSignal): Promise<T> {
  parent?.throwIfAborted()
  const controller = new AbortController()
  const cancel = () => controller.abort(parent?.reason)
  parent?.addEventListener('abort', cancel, { once: true })
  let rejectAbort!: (reason: unknown) => void
  const interrupted = new Promise<never>((_, reject) => { rejectAbort = reject })
  const onAbort = () => rejectAbort(controller.signal.reason)
  controller.signal.addEventListener('abort', onAbort, { once: true })
  const timer = setTimeout(() => controller.abort(new DOMException('Search source timed out.', 'TimeoutError')), 5000)
  try {
    return await Promise.race([load(controller.signal), interrupted])
  } finally {
    clearTimeout(timer)
    parent?.removeEventListener('abort', cancel)
    controller.signal.removeEventListener('abort', onAbort)
  }
}

/** Cache each catalog as soon as it completes, without coupling it to slower sources. */
async function loadCatalog<T>(key: keyof Catalogs, load: (signal: AbortSignal) => Promise<T[] | CatalogResult<T>>, signal?: AbortSignal): Promise<CatalogResult<T>> {
  const cached = catalogCache.get(key)
  if (cached) return cached as CatalogResult<T>
  const pending = catalogRequests.get(key)
  if (pending && pending.signal === signal && !signal?.aborted) return pending.promise as Promise<CatalogResult<T>>
  const generation = snapshotGeneration
  const request = (async () => {
    let result: CatalogResult<T>
    try {
      const loaded = await boundedSource(load, signal)
      result = Array.isArray(loaded) ? { rows: loaded } : loaded
      if (!result || !Array.isArray(result.rows)) throw new Error(i18n.global.t('palette.searchSourceInvalid'))
    } catch (error) {
      if (signal?.aborted) throw error
      result = { rows: [], error: errorMessage(error) }
    }
    if (generation === snapshotGeneration && !signal?.aborted) catalogCache.set(key, result)
    return result
  })()
  catalogRequests.set(key, { signal, promise: request })
  try {
    return await request
  } finally {
    if (catalogRequests.get(key)?.promise === request) catalogRequests.delete(key)
  }
}

/** Drop the cache so the next build re-reads. The palette calls this on open. */
export function refreshSpotlight(): void {
  snapshotGeneration += 1
  catalogCache.clear()
  catalogRequests.clear()
}

// ── item builders ────────────────────────────────────────────────────

/**
 * Every host callback the spotlight's actions need, plus the two pieces of
 * already-loaded state a snapshot should reuse instead of re-fetching. The
 * palette component owns the router, the dialog and the controllers; the
 * builders stay free of all three, which is what lets a test run a conversation
 * action without mounting a window or importing a controller's import chain.
 */
export interface SpotlightHost {
  navigate(routeName: string): void
  navigateSettings(section: string): void
  openSession(sessionId: string): void
  openProject(projectId: string): void
  openAgent(agentId: string): void
  openMode(modeId: string): void
  openPrompt(promptId: string): void
  openTool(toolId: string): void
  selectProvider(providerId: string): void
  openWorkspacePath(path: string, projectId?: string): void
  /** Current-project sessions used only as an explicitly degraded fallback. */
  loadedSessions(): SessionDto[]
  /** Workspace root for the resource leg; empty when no project is open. */
  workspaceRoot(): string
  workspaceProjectId?(): string | undefined
}

function projectItems(projects: ProjectDto[], host: SpotlightHost): SpotlightItem[] {
  return projects
    .filter((project) => (project.lifecycle_status ?? 'active') === 'active')
    .map((project) => ({
      id: `project.${selectionKey(project)}`,
      kind: 'project' as const,
      label: project.name || project.path,
      detail: project.path,
      keywords: i18n.global.t('palette.kwProjectRow'),
      action: () => host.openProject(selectionKey(project)),
    }))
}

function conversationItems(sessions: SessionDto[], projects: ProjectDto[], host: SpotlightHost, t: (key: string) => string): SpotlightItem[] {
  const projectNames = new Map(projects.map((project) => [selectionKey(project), project.name]))
  return sessions
    .filter((session) => (session.lifecycle_status ?? 'active') === 'active')
    .map((session) => ({
      id: `conversation.${selectionKey(session)}`,
      kind: 'conversation' as const,
      label: session.title || t('palette.untitledSession'),
      detail: [projectNames.get(selectionKey({ id: session.project_id ?? '', storage_id: session.storage_id })), new Date(session.updated_at).toLocaleDateString(i18n.global.locale.value)].filter(Boolean).join(' · '),
      keywords: i18n.global.t('palette.kwSessionRow'),
      action: () => host.openSession(selectionKey(session)),
    }))
}

function modelItems(providers: ModelProviderInstanceDto[], host: SpotlightHost, t: (key: string) => string): SpotlightItem[] {
  return providers.map((provider) => ({
    id: `model.${provider.id}`,
    kind: 'model' as const,
    label: provider.display_name || provider.id,
    detail: [provider.driver, ...new Set([provider.model, ...(provider.models ?? [])].filter(Boolean)), provider.status].filter(Boolean).join(' · '),
    keywords: [t('palette.kwModelProvider'), provider.protocol, provider.channel].filter(Boolean).join(' '),
    action: () => host.selectProvider(provider.id),
  }))
}

function agentItems(agents: AgentDirectoryItemDto[], host: SpotlightHost): SpotlightItem[] {
  return agents.filter((agent) => agent.source_kind !== 'missing_reference').map((agent) => ({
    id: `agent.${agent.id}`,
    kind: 'agent' as const,
    label: agent.display_name || agent.slug,
    detail: [agent.slug, agent.role, agent.layer].filter(Boolean).join(' · '),
    keywords: [i18n.global.t('palette.kwAgentRow'), agent.source_kind, ...(agent.mode_usages ?? []).map((usage) => usage.mode_slug)].join(' '),
    action: () => host.openAgent(agent.id),
  }))
}

function modeItems(modes: AgentModeDto[], host: SpotlightHost): SpotlightItem[] {
  return modes.map((mode) => ({
    id: `mode.${mode.id}`,
    kind: 'mode' as const,
    label: mode.display_name || mode.slug || mode.id,
    detail: mode.summary || mode.description || mode.slug,
    keywords: [i18n.global.t('palette.kwModeRow'), mode.slug, mode.description, mode.status].filter(Boolean).join(' '),
    action: () => host.openMode(mode.id),
  }))
}

function promptItems(prompts: PromptFragmentDto[], host: SpotlightHost): SpotlightItem[] {
  return prompts.map((prompt) => ({
    id: `prompt.${prompt.id}`,
    kind: 'prompt' as const,
    label: prompt.title || prompt.key || prompt.id,
    detail: [prompt.key, prompt.scope, prompt.category].filter(Boolean).join(' · '),
    keywords: [i18n.global.t('palette.kwPromptRow'), prompt.content, prompt.target_agent_id].filter(Boolean).join(' '),
    action: () => host.openPrompt(prompt.id),
  }))
}

/** Core owns tool discovery matching and ranking; do not re-score its results. */
async function toolGroup(query: string, host: SpotlightHost, signal?: AbortSignal): Promise<SpotlightGroup> {
  try {
    const results = await boundedSource((sourceSignal) => api.searchTools({ query: query.trim() || undefined, limit: 100 }, { signal: sourceSignal }), signal)
    // The current Core route forwards manifest entries directly. The legacy
    // desktop DTO describes nested discovery rows; read both without inventing
    // missing score, domain or provider-layer facts.
    const items = (results as unknown[]).map((result): SpotlightItem => {
      if (!result || typeof result !== 'object') throw new Error(i18n.global.t('palette.toolSearchInvalid'))
      const row = result as Record<string, unknown>
      const tool = row.tool && typeof row.tool === 'object' ? row.tool as Record<string, unknown> : row
      if (typeof tool.id !== 'string' || !tool.id) throw new Error(i18n.global.t('palette.toolSearchInvalid'))
      const id = tool.id
      return {
        id: `tool.${id}`,
        kind: 'tool',
        label: typeof tool.display_name === 'string' && tool.display_name ? tool.display_name : id,
        detail: typeof tool.description === 'string' ? tool.description : [id, tool.domain, tool.source].filter((part) => typeof part === 'string' && part).join(' · '),
        action: () => host.openTool(id),
      }
    })
    return { kind: 'tool', items }
  } catch (error) {
    if (signal?.aborted) throw error
    return { kind: 'tool', items: [], error: errorMessage(error) }
  }
}

function settingItems(host: SpotlightHost, t: (key: string) => string): SpotlightItem[] {
  return settingsSections.map((section) => ({
    id: `setting.${section.key}`,
    kind: 'setting' as const,
    label: t(section.labelKey),
    detail: t('palette.inSettings'),
    keywords: section.keywordKeys.map((key) => t(key)).join(' '),
    action: () => host.navigateSettings(section.key),
  }))
}

/**
 * Workspace files are demanded per query rather than held open: a large tree
 * cannot live in the snapshot, and a search that covers only already-visited
 * files is a search that lies. The debounce in the component is what
 * keeps one request per pause, not per keypress.
 */
async function workspaceGroup(query: string, host: SpotlightHost, signal?: AbortSignal): Promise<SpotlightGroup> {
  const root = host.workspaceRoot()
  const projectId = host.workspaceProjectId?.()
  if (!root || !query.trim()) return { kind: 'resource', items: [] }
  try {
    const result = await boundedSource((sourceSignal) => api.grepContent(root, query.trim(), { case_sensitive: false, fixed_strings: true, max_results: 100 }, { signal: sourceSignal }), signal)
    const data = result.data as FileSearchDataDto & { success?: boolean; error?: string }
    if (result.status === 'failed' || result.status === 'blocked' || data?.success === false) {
      return { kind: 'resource', items: [], error: data?.error || result.summary || i18n.global.t('palette.fileSearchFailed') }
    }
    const paths = searchedFilePaths(data, root)
    return {
      kind: 'resource',
      truncated: data?.truncated === true,
      items: paths.map((path) => ({
        id: `resource.${path}`,
        kind: 'resource' as const,
        label: path.split('/').pop() ?? path,
        detail: toWorkspaceRelative(path, root),
        keywords: i18n.global.t('palette.kwFileRow'),
        action: () => projectId ? host.openWorkspacePath(path, projectId) : host.openWorkspacePath(path),
      })),
    }
  } catch (error) {
    if (signal?.aborted) throw error
    return { kind: 'resource', items: [], error: errorMessage(error) }
  }
}

// ── ranking / grouping ───────────────────────────────────────────────

interface Scored {
  item: SpotlightItem
  score: number
}

function rank(query: string, items: SpotlightItem[]): SpotlightItem[] {
  const normalized = query.trim().toLowerCase()
  const scored: Scored[] = []
  for (const item of items) {
    // Same haystack grammar appCommands uses: id + label + extras, lowercase.
    const haystack = [item.id, item.label, item.detail ?? '', item.keywords ?? '']
      .filter((part) => part.length > 0)
      .join(' ')
      .toLowerCase()
    const score = scoreMatch(normalized, haystack)
    if (score >= 0) scored.push({ item, score })
  }
  return scored
    .sort((left, right) => left.score - right.score)
    .map((entry) => entry.item)
}

/**
 * Build the grouped result set. Static rows (commands, settings) answer
 * synchronously; the dynamic kinds read the snapshot / workspace and merge in.
 * Commands arrive pre-ranked from `filterCommands` and keep that order inside
 * their group.
 */
export async function searchSpotlight(
  query: string,
  host: SpotlightHost,
  t: (key: string) => string,
  commands: SpotlightItem[],
  options: { signal?: AbortSignal; onUpdate?: (groups: SpotlightGroup[]) => void } = {},
): Promise<SpotlightGroup[]> {
  options.signal?.throwIfAborted()
  const generation = snapshotGeneration
  const groups = new Map<SpotlightKind, SpotlightGroup>()
  const ordered = () => spotlightKindOrder.filter((kind) => groups.has(kind)).map((kind) => groups.get(kind)!)
  const publish = () => {
    if (!options.signal?.aborted && generation === snapshotGeneration) options.onUpdate?.(ordered())
  }
  const push = (group: SpotlightGroup) => {
    if (group.items.length || group.error || group.truncated) groups.set(group.kind, group)
    else groups.delete(group.kind)
  }

  if (!query.trim()) {
    push({ kind: 'command', items: commands })
    push({ kind: 'setting', items: settingItems(host, t) })
  } else {
    push({ kind: 'command', items: rank(query, commands) })
    push({ kind: 'setting', items: rank(query, settingItems(host, t)) })
  }
  publish()
  const access = useHostAccess()
  if (!access.canAccessBackend.value) {
    push({ kind: 'project', items: [], error: access.reason.value })
    return ordered()
  }

  let sessions: CatalogResult<SessionDto> | undefined
  let projects: ProjectDto[] = []
  const updateConversations = () => {
    if (!sessions) return
    const rows = sessions.rows.length || !sessions.error ? sessions.rows : host.loadedSessions()
    push({ kind: 'conversation', items: rank(query, conversationItems(rows, projects, host, t)), error: sessions.error })
  }

  const source = async (kind: SpotlightKind, load: () => Promise<void>) => {
    try {
      await load()
    } catch (error) {
      if (options.signal?.aborted) return
      push({ kind, items: [], error: errorMessage(error) })
    }
    publish()
  }
  const catalog = <T>(key: keyof Catalogs, kind: SpotlightKind, load: (signal: AbortSignal) => Promise<T[]>, build: (rows: T[]) => SpotlightItem[]) => source(kind, async () => {
    const result = await loadCatalog(key, load, options.signal)
    push({ kind, items: rank(query, build(result.rows)), error: result.error })
  })
  await Promise.all([
    source('conversation', async () => {
      sessions = await loadCatalog('sessions', async signal => {
        const result = await loadSessionCatalog({ signal, previous: host.loadedSessions() })
        return { rows: result.rows, error: result.failures.map(failure => failure.source.storageId + ': ' + failure.error.message).join('; ') || undefined }
      }, options.signal)
      updateConversations()
    }),
    source('project', async () => {
      const result = await loadCatalog('projects', (signal) => api.listProjects({ signal }), options.signal)
      projects = result.rows
      push({ kind: 'project', items: rank(query, projectItems(projects, host)), error: result.error })
      updateConversations()
    }),
    catalog('providers', 'model', (signal) => api.listModelProviders({ signal }), (rows) => modelItems(rows, host, t)),
    catalog('agents', 'agent', (signal) => api.listAgents({ signal }), (rows) => agentItems(rows, host)),
    catalog('modes', 'mode', (signal) => api.listAgentModes({ signal }), (rows) => modeItems(rows, host)),
    catalog('prompts', 'prompt', (signal) => api.listPromptFragments({}, { signal }), (rows) => promptItems(rows, host)),
    source('resource', async () => push(await workspaceGroup(query, host, options.signal))),
    source('tool', async () => push(await toolGroup(query, host, options.signal))),
  ])
  return ordered()
}
