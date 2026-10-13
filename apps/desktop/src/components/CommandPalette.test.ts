// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import type { Ref } from 'vue'
import CommandPalette from './CommandPalette.vue'
import { closePalette, openPalette, paletteIsOpen } from '@/composables/useCommandPalette'
import { PALETTE_COMBO, formatCombo } from '@/lib/keybindings'
import { __resetSpotlightForTests } from '@/lib/spotlight'
import type { SessionDto } from '@/api'
import { codeController } from '@/controllers/CodeController'
import { pendingWorkspaceFile } from '@/lib/pageRequests'
import { api } from '@/api'
import { usePanelStyles } from '@/composables/usePanelStyles'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string) => key }),
}))

vi.mock('@/api', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/api')>()
  return { ...original, api: { ...original.api,
    listStorageScopes: vi.fn(async () => [{ storage_id: 'user' }]),
    listProjects: vi.fn(async () => []), listSessions: vi.fn(async () => []),
    listModelProviders: vi.fn(async () => []), listAgents: vi.fn(async () => []),
    listAgentModes: vi.fn(async () => []), listPromptFragments: vi.fn(async () => []),
    searchTools: vi.fn(async () => []),
    grepContent: vi.fn(async () => ({ status: 'ok', data: { lines: [], file_hashes: {} } })),
  } }
})

/**
 * The refs are created inside the async factories: `vi.hoisted` runs before imports, so
 * `ref` is not reachable there. Assigning through the container is the seam the composer
 * test already uses.
 */
const routerMock = vi.hoisted(() => ({
  push: vi.fn(),
  currentRoute: null as unknown as Ref<{ name: string }>,
}))

vi.mock('vue-router', async () => {
  const { ref } = await import('vue')
  routerMock.currentRoute = ref({ name: 'home' })
  return { useRouter: () => routerMock }
})

const homeMock = vi.hoisted(() => ({
  stoppableRunId: null as unknown as Ref<string | null>,
  draft: null as unknown as Ref<string>,
  selectedProjectId: null as unknown as Ref<string | null>,
  sessions: null as unknown as Ref<SessionDto[]>,
  currentProject: null as unknown as Ref<{ path: string } | null>,
  updateDraft: vi.fn(),
  sendMessage: vi.fn(async () => {}),
  stopRun: vi.fn(async () => {}),
  createSession: vi.fn(async () => {}),
}))

vi.mock('@/controllers/HomeController', async () => {
  const { ref } = await import('vue')
  homeMock.stoppableRunId = ref<string | null>(null)
  homeMock.draft = ref('')
  homeMock.selectedProjectId = ref<string | null>(null)
  homeMock.sessions = ref([])
  homeMock.currentProject = ref(null)
  return { homeController: homeMock }
})
vi.mock('@/controllers/CodeController', async () => {
  const { ref } = await import('vue')
  return { codeController: { currentProject: ref<{ id: string; path: string } | null>(null) } }
})

async function mountOpen() {
  // Attached to the document on purpose: focus and `getElementById` only reach nodes
  // that are actually in the tree, and "the caret went into the field" is the claim
  // this component makes.
  const wrapper = mount(CommandPalette, { attachTo: document.body })
  openPalette()
  await flushPromises()
  await settleSearch()
  return wrapper
}

async function settleSearch() {
  await new Promise((resolve) => setTimeout(resolve, 220))
  await flushPromises()
}

function dialogOf(wrapper: ReturnType<typeof mount>) {
  return wrapper.find('dialog').element as HTMLDialogElement
}

function rowText(wrapper: ReturnType<typeof mount>, id: string): string | undefined {
  const row = wrapper.find(`[data-testid="palette-row-${id}"]`)
  return row.exists() ? row.text() : undefined
}

beforeEach(() => {
  closePalette()
  // The test double owns this ref; the production controller derives it from its catalog.
  ;(codeController.currentProject as Ref<{ id: string; path: string } | null>).value = null
  pendingWorkspaceFile.value = null
  homeMock.stoppableRunId.value = null
  homeMock.draft.value = ''
  homeMock.sessions.value = []
  homeMock.currentProject.value = null
  __resetSpotlightForTests()
  routerMock.currentRoute.value = { name: 'home' }
  routerMock.push.mockClear()
  homeMock.sendMessage.mockClear()
  homeMock.updateDraft.mockClear()
  homeMock.createSession.mockClear()
  homeMock.stopRun.mockClear()
  vi.mocked(api.listProjects).mockResolvedValue([])
  vi.mocked(api.listSessions).mockResolvedValue([])
  vi.mocked(api.listModelProviders).mockResolvedValue([])
  vi.mocked(api.listAgents).mockResolvedValue([])
  vi.mocked(api.listAgentModes).mockResolvedValue([])
  vi.mocked(api.listPromptFragments).mockResolvedValue([])
  vi.mocked(api.searchTools).mockResolvedValue([])
})

afterEach(() => {
  closePalette()
})

describe('CommandPalette', () => {
  it('shows the available commands and hides the ones the state cannot serve', async () => {
    const wrapper = await mountOpen()
    // No cancellable run, no draft: stop and both dispatch commands are not offered.
    expect(wrapper.find('[data-testid="palette-row-run.stop"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="palette-row-run.queue"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="palette-row-session.new"]').exists()).toBe(true)
    // The current page hides its own navigation row, and the others stay.
    expect(wrapper.find('[data-testid="palette-row-view.goChat"]').exists()).toBe(false)
    await wrapper.get('[data-testid="palette-more-command"]').trigger('click')
    expect(wrapper.find('[data-testid="palette-row-view.goSettings"]').exists()).toBe(true)

    homeMock.stoppableRunId.value = 'run-1'
    homeMock.draft.value = 'text to send'
    await settleSearch()
    expect(wrapper.find('[data-testid="palette-row-run.stop"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="palette-row-run.queue"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('opens through showModal, and puts the caret in the filter field', async () => {
    const wrapper = mount(CommandPalette, { attachTo: document.body })
    const element = wrapper.find('dialog').element as HTMLDialogElement
    // Spied on the receiver's own method: happy-dom also reports `open` when the
    // attribute is set by hand, so asserting the property alone would not tell a modal
    // dialog apart from a div styled open - and the focus trap only comes with the real
    // call.
    const showModal = vi.fn(() => element.setAttribute('open', ''))
    element.showModal = showModal
    openPalette()
    await flushPromises()
    expect(showModal).toHaveBeenCalledTimes(1)
    expect(element.open).toBe(true)
    expect(document.activeElement).toBe(wrapper.find('[data-testid="palette-input"]').element)
    wrapper.unmount()
  })

  it('filters as the query grows and says so when nothing matches', async () => {
    const wrapper = await mountOpen()
    const input = wrapper.find('[data-testid="palette-input"]')
    await input.setValue('settings')
    await settleSearch()
    const ids = wrapper.findAll('[data-testid^="palette-row-"]').map((row) => row.attributes('data-testid'))
    expect(ids.slice(0, 2)).toEqual(['palette-row-view.goSettings', 'palette-row-setting.personal'])
    expect(ids).toHaveLength(5)
    await wrapper.get('[data-testid="palette-more-setting"]').trigger('click')
    expect(wrapper.findAll('[data-testid^="palette-row-"]')).toHaveLength(12)

    await input.setValue('qqzzxx')
    await settleSearch()
    expect(wrapper.findAll('[data-testid^="palette-row-"]')).toHaveLength(0)
    expect(wrapper.find('[data-testid="palette-empty"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('moves the highlight with the arrows and wraps at both ends', async () => {
    const wrapper = await mountOpen()
    const list = wrapper.find('[data-testid="palette-list"]')
    const rows = () => list.findAll('[role="option"]')
    expect(rows()).toHaveLength(8)
    expect(rows()[0].classes()).toContain('is-active')

    await wrapper.find('[data-testid="palette-input"]').trigger('keydown', { key: 'ArrowDown' })
    expect(rows()[1].attributes('aria-selected')).toBe('true')

    await wrapper.find('[data-testid="palette-input"]').trigger('keydown', { key: 'ArrowUp' })
    await wrapper.find('[data-testid="palette-input"]').trigger('keydown', { key: 'ArrowUp' })
    // Wrapping upward lands on the last row rather than dead-stopping at the first.
    expect(rows()[rows().length - 1].attributes('aria-selected')).toBe('true')
    wrapper.unmount()
  })

  it('runs the highlighted command through the table and closes first', async () => {
    const wrapper = await mountOpen()
    const input = wrapper.find('[data-testid="palette-input"]')
    await input.setValue('new conversation')
    await settleSearch()
    const rows = wrapper.findAll('[data-testid^="palette-row-"]')
    expect(rows.map((row) => row.attributes('data-testid'))).toContain('palette-row-session.new')
    await input.trigger('keydown', { key: 'Enter' })
    await flushPromises()
    expect(homeMock.createSession).toHaveBeenCalledTimes(1)
    expect(paletteIsOpen()).toBe(false)
    expect(dialogOf(wrapper).open).toBe(false)
    wrapper.unmount()
  })

  it('sends the draft it is showing, not a copy of the composer', async () => {
    homeMock.draft.value = '排在后面这条'
    const wrapper = await mountOpen()
    const input = wrapper.find('[data-testid="palette-input"]')
    await input.setValue('queue')
    await settleSearch()
    const row = wrapper.find('[data-testid="palette-row-run.queue"]')
    expect(row.exists()).toBe(true)
    await row.trigger('click')
    await flushPromises()
    // The row must carry the text it advertised: a command that reads its argument from
    // somewhere else would send an empty message and look like it worked.
    expect(homeMock.updateDraft).toHaveBeenCalledWith('排在后面这条')
    expect(homeMock.sendMessage).toHaveBeenCalledWith({
      dispatch_mode: 'queued',
      target_run_id: null,
    })
    wrapper.unmount()
  })

  it('routes navigation by name rather than by a hand-written path', async () => {
    const wrapper = await mountOpen()
    const input = wrapper.find('[data-testid="palette-input"]')
    await input.setValue('market')
    await settleSearch()
    await wrapper.find('[data-testid="palette-row-view.goMarket"]').trigger('click')
    expect(routerMock.push).toHaveBeenCalledWith({ name: 'market' })
    wrapper.unmount()
  })

  it('keeps aria wiring on the rows it exposes', async () => {
    const wrapper = await mountOpen()
    const input = wrapper.find('[data-testid="palette-input"]')
    expect(input.attributes('role')).toBe('combobox')
    expect(input.attributes('aria-controls')?.split(' ')).toContain('palette-results-command')
    expect(input.attributes('aria-activedescendant')).toBe('command-palette-option-0')
    expect(document.getElementById('command-palette-option-0')).not.toBeNull()
    wrapper.unmount()
  })

  it('labels the gesture with the same constant the binding is registered under', async () => {
    const wrapper = await mountOpen()
    expect(wrapper.find('.command-palette-accelerator').text()).toBe(formatCombo(PALETTE_COMBO))
    wrapper.unmount()
  })

  it('follows a close that came from the dialog itself', async () => {
    const wrapper = await mountOpen()
    dialogOf(wrapper).close()
    await flushPromises()
    expect(paletteIsOpen()).toBe(false)
    wrapper.unmount()
  })

  it('previews large project groups, expands, collapses and filters without losing results', async () => {
    vi.mocked(api.listProjects).mockResolvedValue(Array.from({ length: 9 }, (_, i) => ({
      id: `project-${i}`, name: `Studio ${i}`, path: `C:/projects/studio-${i}`,
      created_at: '2026-10-06T00:00:00Z', updated_at: '2026-10-06T00:00:00Z',
    })) as Awaited<ReturnType<typeof api.listProjects>>)
    const wrapper = await mountOpen()
    const projectRows = () => wrapper.findAll('[data-testid^="palette-row-project."]')
    expect(projectRows()).toHaveLength(4)
    expect(wrapper.get('[data-testid="palette-kind-project"] .search-group-count').text()).toBe('9')
    await wrapper.get('[data-testid="palette-more-project"]').trigger('click')
    expect(projectRows()).toHaveLength(9)
    await wrapper.get('[data-testid="palette-more-project"]').trigger('click')
    expect(projectRows()).toHaveLength(4)
    await wrapper.get('[data-testid="palette-toggle-project"]').trigger('click')
    expect(projectRows()).toHaveLength(0)
    expect(wrapper.get('[data-testid="palette-toggle-project"]').attributes('aria-expanded')).toBe('false')
    await wrapper.get('[data-testid="palette-toggle-project"]').trigger('click')
    await wrapper.get('[data-testid="palette-filter-project"]').trigger('click')
    expect(wrapper.findAll('[role="option"]')).toHaveLength(4)
    expect(wrapper.find('[data-testid="palette-row-session.new"]').exists()).toBe(false)
    expect(projectRows()[0]!.find('svg').exists()).toBe(true)
    await projectRows()[0]!.trigger('click')
    expect(paletteIsOpen()).toBe(false)
    wrapper.unmount()
  })

  it('invalidates the old selection immediately when typing a new query', async () => {
    const wrapper = await mountOpen()
    const input = wrapper.get('[data-testid="palette-input"]')
    await input.setValue('market')
    await settleSearch()
    expect(wrapper.find('[data-testid="palette-row-view.goMarket"]').exists()).toBe(true)
    await input.setValue('next query')
    await input.trigger('keydown', { key: 'Enter' })
    expect(routerMock.push).not.toHaveBeenCalled()
    expect(paletteIsOpen()).toBe(true)
    wrapper.unmount()
  })

  it('searches the Code page project and keeps its identity when opening a hit', async () => {
    homeMock.currentProject.value = { path: 'C:/home-project' }
    routerMock.currentRoute.value = { name: 'code-editor' }
    ;(codeController.currentProject as Ref<{ id: string; path: string } | null>).value = { id: 'code-project', path: 'C:/code-project' }
    vi.mocked(api.grepContent).mockResolvedValue({ tool_id: 'file_search', status: 'completed', summary: '', evidence: [], requires_approval: false,
      data: { success: true, lines: [], file_hashes: { 'C:/code-project/hit.ts': 'hash' } } })
    const wrapper = await mountOpen()
    await wrapper.get('[data-testid="palette-input"]').setValue('needle')
    await settleSearch()
    expect(api.grepContent).toHaveBeenLastCalledWith('C:/code-project', 'needle', expect.anything(), expect.anything())
    ;(codeController.currentProject as Ref<{ id: string; path: string } | null>).value = { id: 'later-project', path: 'C:/later-project' }
    await wrapper.get('[data-testid="palette-row-resource.hit.ts"]').trigger('click')
    expect(pendingWorkspaceFile.value).toEqual({ path: 'hit.ts', projectId: 'code-project' })
    wrapper.unmount()
  })

  it('shows a failed source alongside useful local results and keeps window controls available', async () => {
    vi.mocked(api.listProjects).mockRejectedValue(new Error('Project source unavailable'))
    const wrapper = await mountOpen()
    expect(wrapper.get('[data-testid="palette-error-project"]').text()).toContain('palette.sourceUnavailable')
    expect(wrapper.find('[data-testid="palette-row-session.new"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="palette-filter-setting"]').exists()).toBe(true)
    expect(wrapper.findAll('.window-controls .window-btn')).toHaveLength(0)
    expect(wrapper.find('[data-testid="command-palette-button"]').exists()).toBe(false)
    expect(wrapper.get('dialog').classes()).not.toContain('command-palette--fullscreen')
    await wrapper.get('[data-testid="palette-fullscreen"]').trigger('click')
    expect(wrapper.get('dialog').classes()).toContain('command-palette--fullscreen')
    expect(wrapper.findAll('.window-controls .window-btn')).toHaveLength(3)
    await wrapper.get('[data-testid="palette-fullscreen"]').trigger('click')
    expect(wrapper.get('dialog').classes()).not.toContain('command-palette--fullscreen')
    wrapper.unmount()
  })
})
