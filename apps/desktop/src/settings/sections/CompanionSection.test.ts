// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { defineComponent, h } from 'vue'
import zh from '@/locales/zh-CN'
import { ApiError } from '@/lib/apiError'
import type {
  CompanionAutonomyTickResult,
  CompanionConnection,
  CompanionDetail,
  CompanionMemory,
  CompanionSettings,
  CompanionSummary,
} from '@/lib/companionApi'
import CompanionSection from './CompanionSection.vue'

/**
 * The companion settings surface: one read on entry, conditional writes on every save, and a
 * conflict that is reported instead of overwritten. Each panel's list is the server's answer, so
 * every mutation re-reads rather than editing its own copy.
 */

const mocks = vi.hoisted(() => ({
  getSettings: vi.fn(),
  patchSettings: vi.fn(),
  listCompanions: vi.fn(),
  getCompanion: vi.fn(),
  createCompanion: vi.fn(),
  patchCompanion: vi.fn(),
  archiveCompanion: vi.fn(),
  listMemories: vi.fn(),
  addMemory: vi.fn(),
  patchMemory: vi.fn(),
  deleteMemory: vi.fn(),
  listSkills: vi.fn(),
  addSkill: vi.fn(),
  patchSkill: vi.fn(),
  deleteSkill: vi.fn(),
  listConnections: vi.fn(),
  addConnection: vi.fn(),
  patchConnection: vi.fn(),
  deleteConnection: vi.fn(),
  tickAutonomy: vi.fn(),
  listAgentPacks: vi.fn(),
  confirm: vi.fn(),
  notifyError: vi.fn(),
  notifySuccess: vi.fn(),
}))

vi.mock('@/lib/companionApi', () => ({
  companionApi: {
    getSettings: mocks.getSettings,
    patchSettings: mocks.patchSettings,
    listCompanions: mocks.listCompanions,
    getCompanion: mocks.getCompanion,
    createCompanion: mocks.createCompanion,
    patchCompanion: mocks.patchCompanion,
    archiveCompanion: mocks.archiveCompanion,
    listMemories: mocks.listMemories,
    addMemory: mocks.addMemory,
    patchMemory: mocks.patchMemory,
    deleteMemory: mocks.deleteMemory,
    listSkills: mocks.listSkills,
    addSkill: mocks.addSkill,
    patchSkill: mocks.patchSkill,
    deleteSkill: mocks.deleteSkill,
    listConnections: mocks.listConnections,
    addConnection: mocks.addConnection,
    patchConnection: mocks.patchConnection,
    deleteConnection: mocks.deleteConnection,
    tickAutonomy: mocks.tickAutonomy,
  },
}))

vi.mock('@/api', () => ({ api: { listAgentPacks: mocks.listAgentPacks } }))

// UiBadge is a vapor SFC; the vapor-in-vdom interop crashes under VTU's CJS runtime, so the
// badge renders as a span here. Its slot text is all these assertions read.
vi.mock('@/components/ui', async (importOriginal) => {
  const actual = await importOriginal<Record<string, unknown>>()
  const { defineComponent, h } = await import('vue')
  const display = defineComponent({ setup: (_, { slots }) => () => h('span', null, slots.default?.()) })
  return { ...actual, UiBadge: display }
})

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    confirm: mocks.confirm,
    notify: {
      error: mocks.notifyError,
      success: mocks.notifySuccess,
      info: vi.fn(),
      warning: vi.fn(),
    },
  }),
}))

/** UiBadge is mocked to a span above; no vapor component is rendered by this suite. */

function settings(over: Partial<CompanionSettings> = {}): CompanionSettings {
  return { main_companion_id: 'c1', autonomy_paused: false, revision: 1, ...over }
}

function summary(id: string, name: string, over: Partial<CompanionSummary> = {}): CompanionSummary {
  return {
    id,
    name,
    tagline: null,
    avatar: null,
    status: 'active',
    model_purpose: 'chat',
    model: null,
    model_provider_id: null,
    revision: 1,
    autonomy_enabled: false,
    autonomy_paused: false,
    current_activity: 'idle',
    last_activity_at: null,
    memory_count: 0,
    skill_count: 0,
    connection_count: 0,
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
    ...over,
  }
}

function detail(id: string, name: string, over: Partial<CompanionDetail> = {}): CompanionDetail {
  return {
    id,
    name,
    tagline: null,
    avatar: null,
    persona_document: 'You are a careful companion.',
    status: 'active',
    model_purpose: 'chat',
    model: null,
    model_provider_id: null,
    revision: 1,
    autonomy: { enabled: false, paused: false, interval_minutes: 30, daily_action_budget: 3 },
    autonomy_enabled: false,
    autonomy_paused: false,
    current_activity: 'idle',
    last_activity_at: null,
    memory_count: 0,
    skill_count: 0,
    connection_count: 0,
    created_at: '2026-01-01T00:00:00Z',
    updated_at: '2026-01-01T00:00:00Z',
    ...over,
  }
}

function memory(over: Partial<CompanionMemory> = {}): CompanionMemory {
  return {
    id: 'm1',
    companion_id: 'c1',
    content: 'alpha memory',
    source_kind: 'user',
    source_conversation_id: null,
    source_message_id: null,
    index_status: 'indexed',
    revision: 1,
    created_at: '2026-01-02T00:00:00Z',
    updated_at: '2026-01-02T00:00:00Z',
    ...over,
  }
}

function connection(over: Partial<CompanionConnection> = {}): CompanionConnection {
  return {
    id: 'x1',
    companion_id: 'c1',
    kind: 'mcp',
    name: 'Local MCP',
    endpoint: null,
    authorization: 'authorized',
    runtime_status: 'unsupported',
    enabled: true,
    last_checked_at: null,
    revision: 3,
    ...over,
  }
}

const mounted: Array<{ unmount(): void }> = []

function mountSection(props: { companionRequest?: { id: string; seq: number; tab?: 'identity' | 'memory' | 'skills' | 'connections' | 'autonomy' } | null } = {}) {
  const wrapper = mount(CompanionSection, {
    props,
    global: {
      plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })],
    },
  })
  mounted.push(wrapper)
  return wrapper
}

function conflict(): ApiError {
  return new ApiError('revision conflict', 412, { code: 'companion_revision_conflict' })
}

beforeEach(() => {
  vi.clearAllMocks()
  mocks.getSettings.mockResolvedValue(settings())
  mocks.listCompanions.mockResolvedValue([summary('c1', 'Alpha'), summary('c2', 'Beta')])
  mocks.getCompanion.mockImplementation(async (id: string) => (
    id === 'c2' ? detail('c2', 'Beta') : detail('c1', 'Alpha')
  ))
  mocks.patchSettings.mockImplementation(async (revision: number, patch: Partial<CompanionSettings>) => (
    settings({ ...patch, revision: revision + 1 })
  ))
  mocks.patchCompanion.mockImplementation(async (id: string, revision: number, patch: Record<string, unknown>) => (
    detail(id, String(patch.name ?? 'Alpha'), { ...patch, revision: revision + 1 } as Partial<CompanionDetail>)
  ))
  mocks.listMemories.mockResolvedValue([])
  mocks.listSkills.mockResolvedValue([])
  mocks.listConnections.mockResolvedValue([])
  mocks.listAgentPacks.mockResolvedValue([])
  mocks.confirm.mockResolvedValue(true)
})

afterEach(() => {
  mounted.splice(0).forEach((wrapper) => wrapper.unmount())
})

describe('companion settings section', () => {
  it('reads the switchboard and the list once when it is first entered', async () => {
    const wrapper = mountSection()
    await flushPromises()
    expect(mocks.getSettings).toHaveBeenCalledTimes(1)
    expect(mocks.listCompanions).toHaveBeenCalledTimes(1)
    expect(mocks.listCompanions).toHaveBeenCalledWith(true)
    expect(wrapper.get('[data-testid="companion-main-companion"]').element).toBeTruthy()
  })

  it('opens the companion named by a deep link instead of the main companion', async () => {
    mountSection({ companionRequest: { id: 'c2', seq: 1 } })
    await flushPromises()
    expect(mocks.getCompanion).toHaveBeenCalledWith('c2')
    expect(mocks.getCompanion).not.toHaveBeenCalledWith('c1')
  })

  it('opens the editor panel a deep link asked for, not the first tab', async () => {
    const wrapper = mountSection({ companionRequest: { id: 'c2', seq: 1, tab: 'memory' } })
    await flushPromises()

    expect(wrapper.get('[data-testid="companion-tab-memory"]').classes()).toContain('active')
    expect(wrapper.get('[data-testid="companion-tab-identity"]').classes()).not.toContain('active')
  })

  it('saves identity with the revision it read and keeps the returned detail', async () => {
    const wrapper = mountSection()
    await flushPromises()
    await wrapper.get('[data-testid="companion-identity-name"]').setValue('Renamed')
    await wrapper.get('[data-testid="companion-identity-save"]').trigger('click')
    await flushPromises()
    expect(mocks.patchCompanion).toHaveBeenCalledWith('c1', 1, expect.objectContaining({
      name: 'Renamed',
      // The editor writes the pinned model, not the route purpose: `model: ''` means "follow the
      // tenant's default route" and is what an untouched form must send back.
      model: '',
      persona_document: 'You are a careful companion.',
    }))
    expect(wrapper.get('[data-testid="companion-identity-name"]').element).toHaveProperty('value', 'Renamed')
    expect(wrapper.get('[data-testid="companion-editor-revision"]').text()).toContain('2')
  })

  it('reports a 412 and keeps the local draft instead of overwriting it', async () => {
    const wrapper = mountSection()
    await flushPromises()
    await wrapper.get('[data-testid="companion-identity-name"]').setValue('Local edit')
    mocks.patchCompanion.mockRejectedValueOnce(conflict())
    await wrapper.get('[data-testid="companion-identity-save"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="companion-conflict"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="companion-conflict"]').text()).toContain(zh.settings.companions.conflictBody)
    expect(wrapper.get('[data-testid="companion-identity-name"]').element).toHaveProperty('value', 'Local edit')
    expect(wrapper.find('[data-testid="companion-editor-reload"]').exists()).toBe(true)
    expect(mocks.notifyError).not.toHaveBeenCalled()
  })

  it('adds and deletes memories, and passes the search text to the server', async () => {
    let rows = [memory()]
    mocks.listMemories.mockImplementation(async (_id: string, q?: string) => (
      q ? rows.filter((row) => row.content.includes(q)) : rows
    ))
    mocks.addMemory.mockImplementation(async (_id: string, input: { content: string }) => {
      const created = memory({ id: 'm2', content: input.content, revision: 1 })
      rows = [...rows, created]
      return created
    })
    mocks.deleteMemory.mockImplementation(async (_id: string, memoryId: string) => {
      rows = rows.filter((row) => row.id !== memoryId)
    })

    const wrapper = mountSection()
    await flushPromises()
    await wrapper.get('[data-testid="companion-tab-memory"]').trigger('click')
    await flushPromises()
    expect(mocks.listMemories).toHaveBeenCalledWith('c1', undefined)
    expect(wrapper.find('[data-testid="companion-memory-row-m1"]').exists()).toBe(true)

    await wrapper.get('[data-testid="companion-memory-search"]').setValue('alpha')
    await wrapper.get('[data-testid="companion-memory-search-submit"]').trigger('click')
    await flushPromises()
    expect(mocks.listMemories).toHaveBeenLastCalledWith('c1', 'alpha')

    // The list is always the server's answer for the current query, so a new row is looked up
    // through a plain (unfiltered) read rather than appended locally.
    await wrapper.get('[data-testid="companion-memory-search"]').setValue('')
    await wrapper.get('[data-testid="companion-memory-search-submit"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="companion-memory-draft"]').setValue('beta memory')
    await wrapper.get('[data-testid="companion-memory-add"]').trigger('click')
    await flushPromises()
    expect(mocks.addMemory).toHaveBeenCalledWith('c1', { content: 'beta memory', source_kind: 'user' })
    expect(wrapper.find('[data-testid="companion-memory-row-m2"]').exists()).toBe(true)

    await wrapper.get('[data-testid="companion-memory-delete-m1"]').trigger('click')
    await flushPromises()
    expect(mocks.deleteMemory).toHaveBeenCalledWith('c1', 'm1', 1)
    expect(wrapper.find('[data-testid="companion-memory-row-m1"]').exists()).toBe(false)
  })

  it('revokes a connection with its revision and shows the revoked state', async () => {
    mocks.listConnections.mockResolvedValue([connection()])
    mocks.patchConnection.mockImplementation(async (
      _id: string, _bindingId: string, revision: number, patch: Partial<CompanionConnection>,
    ) => connection({ ...patch, revision: revision + 1 }))

    const wrapper = mountSection()
    await flushPromises()
    await wrapper.get('[data-testid="companion-tab-connections"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="companion-connection-runtime-x1"]').text()).toContain(zh.settings.companions.connection.runtimeUnsupported)
    expect(wrapper.get('[data-testid="companion-connection-auth-x1"]').text()).toContain(zh.settings.companions.connection.authAuthorized)

    await wrapper.get('[data-testid="companion-connection-revoke-x1"]').trigger('click')
    await flushPromises()
    expect(mocks.confirm).toHaveBeenCalled()
    expect(mocks.patchConnection).toHaveBeenCalledWith('c1', 'x1', 3, { authorization: 'revoked' })
    expect(wrapper.get('[data-testid="companion-connection-auth-x1"]').text()).toContain(zh.settings.companions.connection.authRevoked)
    expect(wrapper.get('[data-testid="companion-connection-auth-x1"]').text()).not.toContain(zh.companion.center.connectionRuntimeOnline)
  })

  it('clamps the autonomy interval and budget before saving, and shows Core\'s refusal reason', async () => {
    const tick: CompanionAutonomyTickResult = {
      acted: false,
      action: 'none',
      reason: 'model_unavailable',
      activity: null,
      conversation: null,
      message: null,
    }
    mocks.tickAutonomy.mockResolvedValue(tick)

    const wrapper = mountSection()
    await flushPromises()
    await wrapper.get('[data-testid="companion-tab-autonomy"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="companion-autonomy-interval"]').setValue('1')
    await wrapper.get('[data-testid="companion-autonomy-budget"]').setValue('999')
    await wrapper.get('[data-testid="companion-autonomy-save"]').trigger('click')
    await flushPromises()
    expect(mocks.patchCompanion).toHaveBeenCalledWith('c1', 1, {
      autonomy: {
        enabled: false,
        paused: false,
        interval_minutes: 5,
        daily_action_budget: 50,
      },
    })
    expect(wrapper.get('[data-testid="companion-autonomy-interval"]').element).toHaveProperty('value', '5')
    expect(wrapper.get('[data-testid="companion-autonomy-budget"]').element).toHaveProperty('value', '50')

    await wrapper.get('[data-testid="companion-autonomy-tick"]').trigger('click')
    await flushPromises()
    expect(mocks.tickAutonomy).toHaveBeenCalledWith('c1')
    expect(wrapper.get('[data-testid="companion-autonomy-result"]').text())
      .toContain(zh.settings.companions.autonomy.reason.model_unavailable)
  })
})
