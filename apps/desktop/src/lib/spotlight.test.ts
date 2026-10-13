// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import {
  __resetSpotlightForTests,
  refreshSpotlight,
  searchSpotlight,
  spotlightKindLabel,
  spotlightKindOrder,
  type SpotlightHost,
  type SpotlightItem,
  type SpotlightGroup,
} from './spotlight'
import en from '@/locales/en'
import zhCN from '@/locales/zh-CN'
import { api, type PromptFragmentDto, type SessionDto } from '@/api'

vi.mock('@/api', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/api')>()
  return {
    ...original,
    api: {
      ...original.api,
      listSessions: vi.fn(async () => []),
      listStorageScopes: vi.fn(async () => [{ storage_id: 'user', scope_kind: 'user', storage_root: 'test', backend: 'sqlite', external: false, paths: {} }]),
      listModelProviders: vi.fn(async () => []),
      listProjects: vi.fn(async () => []),
      listAgents: vi.fn(async () => []),
      listAgentModes: vi.fn(async () => []),
      listPromptFragments: vi.fn(async () => []),
      searchTools: vi.fn(async () => []),
      grepContent: vi.fn(async () => ({
        tool_id: 'file_search',
        status: 'ok',
        summary: '',
        evidence: [],
        requires_approval: false,
        data: { lines: [], file_hashes: {} },
      })),
    },
  }
})

// providerTemplates pulls one `?raw` SVG import per supplier; the spotlight
// never touches that module, because its snapshot takes listModelProviders()
// rows as-is rather than re-aggregating them the way Settings does.

interface HostState {
  sessions?: SessionDto[]
  workspaceRoot?: string
}

function hostWith(state: HostState = {}): SpotlightHost & { calls: string[] } {
  const calls: string[] = []
  const record = (name: string) => (...args: unknown[]) => {
    calls.push(`${name}(${args.map((arg) => JSON.stringify(arg)).join(',')})`)
  }
  return {
    calls,
    navigate: record('navigate') as (routeName: string) => void,
    navigateSettings: record('navigateSettings') as (section: string) => void,
    openSession: record('openSession') as (sessionId: string) => void,
    openProject: record('openProject') as (projectId: string) => void,
    openAgent: record('openAgent') as (agentId: string) => void,
    openMode: record('openMode') as (modeId: string) => void,
    openPrompt: record('openPrompt') as (promptId: string) => void,
    openTool: record('openTool') as (toolId: string) => void,
    selectProvider: record('selectProvider') as (providerId: string) => void,
    openWorkspacePath: record('openWorkspacePath') as (path: string) => void,
    loadedSessions: () => state.sessions ?? [],
    workspaceRoot: () => state.workspaceRoot ?? '',
  }
}

const t = (key: string) => key
const promptFragment = (id: string, title: string, content = ''): PromptFragmentDto => ({
  id, title, content, key: `fragment.${id}`, scope: 'global', category: 'system', priority: 1, enabled: true,
  is_builtin: false, created_at: '', updated_at: '',
})

function commandItem(id: string, label: string): SpotlightItem {
  return { id, kind: 'command', label, action: () => undefined }
}

beforeEach(() => {
  __resetSpotlightForTests()
  vi.mocked(api.listSessions).mockReset().mockResolvedValue([])
  vi.mocked(api.listModelProviders).mockReset().mockResolvedValue([])
  vi.mocked(api.listProjects).mockReset().mockResolvedValue([])
  vi.mocked(api.listAgents).mockReset().mockResolvedValue([])
  vi.mocked(api.listAgentModes).mockReset().mockResolvedValue([])
  vi.mocked(api.listPromptFragments).mockReset().mockResolvedValue([])
  vi.mocked(api.searchTools).mockReset().mockResolvedValue([])
  vi.mocked(api.grepContent).mockReset().mockResolvedValue({
    tool_id: 'file_search',
    status: 'ok',
    summary: '',
    evidence: [],
    requires_approval: false,
    data: { lines: [], file_hashes: {} },
  })
})

afterEach(() => {
  vi.useRealTimers()
})

describe('searchSpotlight', () => {
  it('with no query offers the command launcher and browsable settings', async () => {
    const host = hostWith()
    const groups = await searchSpotlight('', host, t, [commandItem('session.new', 'New conversation')])
    expect(groups.map((group) => group.kind)).toEqual(['command', 'setting'])
    expect(groups[0]!.kind).toBe('command')
    expect(groups[0]!.items[0]!.label).toBe('New conversation')
    expect(groups.find((group) => group.kind === 'setting')?.items).toHaveLength(11)
  })

  it('a settings label query groups settings and skips unrelated kinds', async () => {
    const host = hostWith()
    const groups = await searchSpotlight('appearance', host, t, [])
    const kinds = groups.map((group) => group.kind)
    expect(kinds).toContain('setting')
    expect(kinds).not.toContain('command')
  })

  it('a session title query matches a conversation whose row opens that session', async () => {
    const sessions: SessionDto[] = [
      {
        id: 's-1',
        project_id: null,
        title: 'pilot launcher seams',
        permission_mode: 'default', space_options: null, settings_revision: 0,
        status: 'idle',
        created_at: '2026-09-01T00:00:00Z',
        updated_at: '2026-09-10T00:00:00Z',
      },
    ]
    vi.mocked(api.listSessions).mockResolvedValue(sessions)
    const host = hostWith({ sessions })
    const groups = await searchSpotlight('launcher', host, t, [])
    const row = groups.find((group) => group.kind === 'conversation')?.items[0]
    expect(row?.label).toBe('pilot launcher seams')
    row?.action()
    expect(host.calls).toEqual(['openSession("user::s-1")'])
  })

  it('a provider display-name query matches a model row that selects that provider', async () => {
    vi.mocked(api.listModelProviders).mockResolvedValue([
      {
        id: 'p-9',
        driver: 'anthropic',
        protocol: null,
        channel: null,
        display_name: 'Work Claude',
        connection_kind: 'api-key',
        base_url: 'https://api.anthropic.com',
        model: 'claude-sonnet',
        models: ['claude-sonnet', 'pilot-reasoner'],
        has_api_key: true,
        capabilities: [],
        enabled: true,
        status: 'ready',
        status_message: 'ready',
        created_at: '',
        updated_at: '',
      },
    ])
    const host = hostWith()
    const groups = await searchSpotlight('claude', host, t, [])
    const row = groups.find((group) => group.kind === 'model')?.items[0]
    expect(row?.label).toBe('Work Claude')
    row?.action()
    expect(host.calls).toEqual(['selectProvider("p-9")'])
    const byModel = await searchSpotlight('pilot-reasoner', host, t, [])
    expect(byModel.find((group) => group.kind === 'model')?.items[0]?.id).toBe('model.p-9')
  })

  it('a workspace-content query surfaces files as resource rows through the code tool', async () => {
    vi.mocked(api.grepContent).mockResolvedValue({
      tool_id: 'file_search',
      status: 'ok',
      summary: '',
      evidence: [],
      requires_approval: false,
      data: {
        lines: [],
        file_hashes: { 'C:\\ws\\demo\\src\\spotlight.ts': 'x' },
      },
    })
    const host = hostWith({ workspaceRoot: 'C:/ws/demo' })
    const groups = await searchSpotlight('spotlight', host, t, [])
    const kinds = groups.map((group) => group.kind)
    expect(kinds).toContain('resource')
    const row = groups.find((group) => group.kind === 'resource')?.items[0]
    expect(row?.label).toBe('spotlight.ts')
    expect(row?.detail).toBe('src/spotlight.ts')
    row?.action()
    expect(host.calls).toEqual(['openWorkspacePath("src/spotlight.ts")'])
    expect(api.grepContent).toHaveBeenCalledWith('C:/ws/demo', 'spotlight', {
      case_sensitive: false,
      fixed_strings: true,
      max_results: 100,
    }, { signal: expect.any(AbortSignal) })
  })

  it('an empty keyword matches nothing rather than padding the list', async () => {
    const host = hostWith()
    const groups = await searchSpotlight('qqzzxx', host, t, [commandItem('session.new', 'New conversation')])
    expect(groups).toEqual([])
  })

  it('searches real project, agent, mode, prompt and tool metadata with distinct open actions', async () => {
    vi.mocked(api.listProjects).mockResolvedValue([
      { id: 'p1', name: 'pilot project', path: 'C:/pilot', created_at: '' },
      { id: 'p2', name: 'pilot trashed', path: 'C:/deleted', created_at: '', lifecycle_status: 'trashed' },
    ])
    vi.mocked(api.listAgents).mockResolvedValue([{
      id: 'a1', slug: 'pilot-agent', display_name: 'Engineering', layer: 'execution', role: 'developer',
      source_kind: 'pack', source_key: 'engineering', managed: true, writable: false, enabled: true,
      status: 'published', revision: 1, version: 1, configured_strategy: { kind: 'inherit' },
      mode_usages: [], effective_previews: {}, updated_at: '',
    }])
    vi.mocked(api.listAgentModes).mockResolvedValue([
      { id: 'm1', display_name: 'Team', description: 'pilot collaborators', slug: 'team' },
    ])
    vi.mocked(api.listPromptFragments).mockResolvedValue([
      promptFragment('pr1', 'System fragment', 'pilot instructions'),
    ])
    vi.mocked(api.searchTools).mockResolvedValue([{
      tool: {
        id: 'file_search', display_name: 'Search', domain: 'files', source: 'core', risk: 'low',
        requires_approval: false, execute_endpoint: '', capabilities: ['pilot capability'],
      },
      score: 1, matched_fields: ['capabilities'], provider_layer: 'execution',
      requires_human_checkpoint: false, approval_summary: '',
    }])
    const host = hostWith()
    const groups = await searchSpotlight('pilot', host, t, [])
    const catalogGroups = groups.filter((group) => ['project', 'agent', 'mode', 'prompt', 'tool'].includes(group.kind))
    expect(catalogGroups.map((group) => group.kind)).toEqual(['project', 'agent', 'mode', 'prompt', 'tool'])
    expect(groups.find((group) => group.kind === 'project')?.items).toHaveLength(1)
    expect(groups.find((group) => group.kind === 'prompt')?.items[0]?.label).toBe('System fragment')
    expect(api.listPromptFragments).toHaveBeenCalledWith({}, { signal: expect.any(AbortSignal) })
    catalogGroups.forEach((group) => group.items[0]?.action())
    expect(host.calls).toEqual(['openProject("p1")', 'openAgent("a1")', 'openMode("m1")', 'openPrompt("pr1")', 'openTool("file_search")'])
    expect(api.searchTools).toHaveBeenCalledWith({ query: 'pilot', limit: 100 }, { signal: expect.any(AbortSignal) })
  })

  it('loads sessions across projects and leaves every matching row available for UI expansion', async () => {
    const cached: SessionDto = {
      permission_mode: 'default', space_options: null, settings_revision: 0,
      id: 'current', project_id: 'p1', title: 'Current project', status: 'idle', created_at: '', updated_at: '',
    }
    const otherSessions = Array.from({ length: 12 }, (_, index): SessionDto => ({
      permission_mode: 'default', space_options: null, settings_revision: 0,
      id: `other-${index}`, project_id: 'p2', title: `Other project crossproject42 ${index}`, status: 'idle',
      created_at: '', updated_at: '',
    }))
    vi.mocked(api.listSessions).mockResolvedValue([cached, ...otherSessions])
    const groups = await searchSpotlight('crossproject42', hostWith({ sessions: [cached] }), t, [])
    expect(api.listSessions).toHaveBeenCalledWith(undefined, expect.any(AbortSignal), 'user', 'active')
    expect(groups.find((group) => group.kind === 'conversation')?.items).toHaveLength(12)
  })

  it('reuses catalog sources on keystrokes and refreshes them when the search opens again', async () => {
    const host = hostWith()
    await searchSpotlight('first', host, t, [])
    await searchSpotlight('second', host, t, [])
    expect(api.listProjects).toHaveBeenCalledTimes(1)
    expect(api.listSessions).toHaveBeenCalledTimes(1)
    refreshSpotlight()
    await searchSpotlight('third', host, t, [])
    expect(api.listProjects).toHaveBeenCalledTimes(2)
  })

  it('keeps independent source errors visible while successful results remain usable', async () => {
    vi.mocked(api.listAgents).mockRejectedValue(new Error('Agents are unavailable'))
    vi.mocked(api.listProjects).mockResolvedValue([{ id: 'p1', name: 'pilot', path: 'C:/pilot', created_at: '' }])
    const groups = await searchSpotlight('pilot', hostWith(), t, [])
    expect(groups.find((group) => group.kind === 'agent')).toEqual({ kind: 'agent', items: [], error: 'Agents are unavailable' })
    expect(groups.find((group) => group.kind === 'project')?.items[0]?.label).toBe('pilot')
  })

  it('labels cached conversations as degraded when the all-project session query fails', async () => {
    vi.mocked(api.listSessions).mockRejectedValue(new Error('Sessions unavailable'))
    const host = hostWith({ sessions: [{ id: 's1', project_id: 'p1', title: 'pilot', status: 'idle', created_at: '', updated_at: '', permission_mode: 'default', space_options: null, settings_revision: 0 }] })
    const group = (await searchSpotlight('pilot', host, t, [])).find((row) => row.kind === 'conversation')
    expect(group?.error).toBe('user: Sessions unavailable')
    expect(group?.items[0]?.id).toBe('conversation.s1')
  })

  it('does not let a previous opening overwrite the new catalog snapshot', async () => {
    let finishOld!: (rows: Awaited<ReturnType<typeof api.listProjects>>) => void
    vi.mocked(api.listProjects)
      .mockImplementationOnce(() => new Promise((resolve) => { finishOld = resolve }))
      .mockResolvedValue([{ id: 'new', name: 'New opening', path: 'C:/new', created_at: '' }])
    const host = hostWith()
    const oldSearch = searchSpotlight('', host, t, [])
    refreshSpotlight()
    const freshGroups = await searchSpotlight('', host, t, [])
    finishOld([{ id: 'old', name: 'Old opening', path: 'C:/old', created_at: '' }])
    await oldSearch
    const subsequentGroups = await searchSpotlight('', host, t, [])
    expect(freshGroups.find((group) => group.kind === 'project')?.items[0]?.id).toBe('project.new')
    expect(subsequentGroups.find((group) => group.kind === 'project')?.items[0]?.id).toBe('project.new')
    expect(api.listProjects).toHaveBeenCalledTimes(2)
  })

  it('keeps all returned hit files and reports the backend truncation without inventing a total', async () => {
    const fileHashes = Object.fromEntries(Array.from({ length: 15 }, (_, index) => [`C:/ws/pilot-${index}.ts`, 'hash']))
    vi.mocked(api.grepContent).mockResolvedValue({
      tool_id: 'file_search', status: 'completed', summary: '', evidence: [], requires_approval: false,
      data: { success: true, lines: [], file_hashes: fileHashes, truncated: true },
    })
    const groups = await searchSpotlight('literal[', hostWith({ workspaceRoot: 'C:/ws' }), t, [])
    const resources = groups.find((group) => group.kind === 'resource')
    expect(resources?.items).toHaveLength(15)
    expect(resources?.truncated).toBe(true)
    expect(api.grepContent).toHaveBeenCalledWith('C:/ws', 'literal[', expect.objectContaining({ fixed_strings: true }), { signal: expect.any(AbortSignal) })
  })

  it('reports tool-level content search failures instead of presenting a successful empty search', async () => {
    vi.mocked(api.grepContent).mockResolvedValue({
      tool_id: 'file_search', status: 'completed', summary: '', evidence: [], requires_approval: false,
      data: { success: false, error: 'Ripgrep unavailable' },
    })
    const resources = (await searchSpotlight('pilot', hostWith({ workspaceRoot: 'C:/ws' }), t, []))
      .find((group) => group.kind === 'resource')
    expect(resources).toEqual({ kind: 'resource', items: [], error: 'Ripgrep unavailable' })
  })

  it('reads the actual Core manifest search payload without requiring fictitious discovery metadata', async () => {
    vi.mocked(api.searchTools).mockResolvedValue([{
      id: 'file_search', description: 'Search file contents in the run workspace.', requires_approval: false,
      input_schema: { type: 'object' }, risk: 'low', mutates_workspace: false, retry_safety: 'safe', confirmation_fields: [],
    }] as unknown as Awaited<ReturnType<typeof api.searchTools>>)
    const host = hostWith()
    const tools = (await searchSpotlight('file', host, t, [])).find((group) => group.kind === 'tool')
    expect(tools?.error).toBeUndefined()
    expect(tools?.items[0]?.label).toBe('file_search')
    expect(tools?.items[0]?.detail).toBe('Search file contents in the run workspace.')
    tools?.items[0]?.action()
    expect(host.calls).toEqual(['openTool("file_search")'])
  })

  it('publishes local rows immediately and each successful source while another source is pending', async () => {
    let finishPrompts!: (rows: Awaited<ReturnType<typeof api.listPromptFragments>>) => void
    vi.mocked(api.listPromptFragments).mockImplementationOnce(() => new Promise((resolve) => { finishPrompts = resolve }))
    vi.mocked(api.listProjects).mockResolvedValue([{ id: 'p1', name: 'settings project', path: 'C:/settings', created_at: '' }])
    const updates: SpotlightGroup[][] = []
    const search = searchSpotlight('settings', hostWith(), t, [commandItem('settings.open', 'Settings')], { onUpdate: (groups) => updates.push(groups) })
    expect(updates[0]?.map((group) => group.kind)).toEqual(['command', 'setting'])
    await flushPromises()
    expect(updates.at(-1)?.find((group) => group.kind === 'project')?.items[0]?.id).toBe('project.p1')
    expect(updates.at(-1)?.some((group) => group.kind === 'prompt')).toBe(false)
    finishPrompts([promptFragment('pr1', 'Settings prompt')])
    const final = await search
    expect(final.find((group) => group.kind === 'prompt')?.items[0]?.id).toBe('prompt.pr1')
  })

  it('bounds a stalled source to five seconds and aborts its transport while preserving successful sources', async () => {
    vi.useFakeTimers()
    let sourceSignal: AbortSignal | undefined
    vi.mocked(api.listAgents).mockImplementationOnce((options) => {
      sourceSignal = options?.signal
      return new Promise(() => undefined)
    })
    vi.mocked(api.listProjects).mockResolvedValue([{ id: 'p1', name: 'pilot project', path: 'C:/pilot', created_at: '' }])
    const search = searchSpotlight('pilot', hostWith(), t, [])
    await vi.advanceTimersByTimeAsync(5000)
    const groups = await search
    expect(sourceSignal?.aborted).toBe(true)
    expect(groups.find((group) => group.kind === 'agent')?.error).toBe('Search source timed out.')
    expect(groups.find((group) => group.kind === 'project')?.items[0]?.id).toBe('project.p1')
  })

  it('aborts query transports and rejects late source writes to the next query cache', async () => {
    let finishOld!: (rows: Awaited<ReturnType<typeof api.listProjects>>) => void
    let oldSignal: AbortSignal | undefined
    vi.mocked(api.listProjects)
      .mockImplementationOnce((options) => {
        oldSignal = options?.signal
        return new Promise((resolve) => { finishOld = resolve })
      })
      .mockResolvedValue([{ id: 'fresh', name: 'Fresh project', path: 'C:/fresh', created_at: '' }])
    vi.mocked(api.grepContent).mockImplementationOnce(() => new Promise(() => undefined))
    vi.mocked(api.searchTools).mockImplementationOnce(() => new Promise(() => undefined))
    const controller = new AbortController()
    const onUpdate = vi.fn()
    const oldSearch = searchSpotlight('old', hostWith({ workspaceRoot: 'C:/ws' }), t, [], { signal: controller.signal, onUpdate })
    await flushPromises()
    controller.abort()
    const updateCount = onUpdate.mock.calls.length
    await oldSearch
    expect(oldSignal?.aborted).toBe(true)
    expect(vi.mocked(api.grepContent).mock.calls[0]?.[3]?.signal?.aborted).toBe(true)
    expect(vi.mocked(api.searchTools).mock.calls[0]?.[1]?.signal?.aborted).toBe(true)
    const freshGroups = await searchSpotlight('', hostWith(), t, [])
    finishOld([{ id: 'old', name: 'Old project', path: 'C:/old', created_at: '' }])
    await flushPromises()
    const cachedGroups = await searchSpotlight('', hostWith(), t, [])
    expect(onUpdate).toHaveBeenCalledTimes(updateCount)
    expect(freshGroups.find((group) => group.kind === 'project')?.items[0]?.id).toBe('project.fresh')
    expect(cachedGroups.find((group) => group.kind === 'project')?.items[0]?.id).toBe('project.fresh')
    expect(api.listProjects).toHaveBeenCalledTimes(2)
  })
})

describe('spotlight keys', () => {
  it('every kind label and every section keyword resolves in both bundles', () => {
    const bundles = [en, zhCN] as Array<Record<string, unknown>>
    const lookup = (bundle: Record<string, unknown>, dotted: string) =>
      dotted.split('.').reduce<unknown>((node, part) => {
        if (node && typeof node === 'object') return (node as Record<string, unknown>)[part]
        return undefined
      }, bundle)
    const missing: string[] = []
    const paletteKeys = [
      ...spotlightKindOrder.map(spotlightKindLabel),
      'palette.spotlightPlaceholder',
      'palette.searching',
      'palette.untitledSession',
      'palette.inSettings',
      'palette.kwModelProvider',
      'palette.kwPersonal',
      'palette.kwGeneral',
      'palette.kwModel',
      'palette.kwAgentCenter',
      'palette.kwTools',
      'palette.kwTinaChat',
      'palette.kwArchive',
      'palette.kwAppearance',
      'palette.kwPets',
      'palette.kwApiDocs',
      'palette.kwAbout',
    ]
    for (const key of paletteKeys) {
      for (const bundle of bundles) {
        const value = lookup(bundle, key)
        if (typeof value !== 'string' || !value.trim()) missing.push(key)
      }
    }
    expect(missing).toEqual([])
  })
})

describe('search transport', () => {
  it('passes cancellation through catalog and content HTTP calls without putting transport options in tool arguments', async () => {
    const original = await vi.importActual<typeof import('@/api')>('@/api')
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => new Response('[]', { status: 200 }))
    const controller = new AbortController()
    try {
      await Promise.all([
        original.api.listProjects({ signal: controller.signal }),
        original.api.listSessions(undefined, controller.signal),
        original.api.listModelProviders({ signal: controller.signal }),
        original.api.listAgents({ signal: controller.signal }),
        original.api.listAgentModes({ signal: controller.signal }),
        original.api.listPromptFragments({}, { signal: controller.signal }),
        original.api.grepContent('C:/ws', 'literal[', { fixed_strings: true }, { signal: controller.signal }),
      ])
      expect(fetchSpy).toHaveBeenCalledTimes(7)
      expect(fetchSpy.mock.calls.every(([, init]) => init?.signal === controller.signal)).toBe(true)
      const contentRequest = fetchSpy.mock.calls.find(([url]) => String(url).endsWith('/code/tools/file_search/execute'))
      expect(JSON.parse(String(contentRequest?.[1]?.body))).toEqual({ cwd: 'C:/ws', arguments: { pattern: 'literal[', fixed_strings: true } })
    } finally {
      fetchSpy.mockRestore()
    }
  })
})
