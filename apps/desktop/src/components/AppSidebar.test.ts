// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AppSidebar from './AppSidebar.vue'
import type { ProjectDto, SessionDto } from '../api'
import { useDebugStudio } from '@/composables/useDebugStudio'
import { freeWorkspaceKey, workspaceListStorageKey } from '@/composables/useWorkspaceList'

vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key }),
}))

const { confirmMock } = vi.hoisted(() => ({ confirmMock: vi.fn(async (..._args: unknown[]) => true) }))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    confirm: (...args: unknown[]) => confirmMock(...args),
    notify: { error: vi.fn(), success: vi.fn() },
    banner: { error: vi.fn() },
    dismissByKey: vi.fn(),
  }),
}))

const project: ProjectDto = { id: 'p-1', name: 'Demo project', path: 'C:/demo', created_at: '2026-08-27T00:00:00Z' }
const session: SessionDto = {
  permission_mode: 'default', space_options: null, settings_revision: 0,
  id: 's-1',
  project_id: 'p-1',
  title: 'Demo session',
  status: 'ready',
  created_at: '2026-08-27T00:00:00Z',
  updated_at: '2026-08-27T00:00:00Z',
}

function factory(overrides: Record<string, unknown> = {}, attachTo?: Element) {
  return mount(AppSidebar, {
    attachTo,
    global: {
      // RowContextMenu wraps its panel in <Transition>; test-utils stubs it by
      // default, which would render the menu as an empty stub and break every
      // menu assertion below.
      stubs: { BrandLogo: true, TinadecCalligraphy: true, transition: false },
    },
    props: {
      projects: [project],
      sessions: [session],
      selectedProjectId: 'p-1',
      selectedSessionId: 's-1',
      busy: false,
      ...overrides,
    },
  })
}

describe('Debug Studio opt-in navigation', () => {
  it('hides the entry by default and follows the host preference after an explicit save', async () => {
    const getAppConfig = vi.fn(async () => ({ debug_studio_enabled: false }))
    const saveDebugStudioEnabled = vi.fn(async (value: boolean) => ({ debug_studio_enabled: value }))
    const openDebugStudio = vi.fn(async () => true)
    const previousHost = window.tinadec
    Object.defineProperty(window, 'tinadec', { configurable: true, value: { getAppConfig, saveDebugStudioEnabled, openDebugStudio } })
    const wrapper = factory()
    try {
      await flushPromises()
      expect(wrapper.find('[title="sidebar.debugStudio"]').exists()).toBe(false)
      await useDebugStudio().saveEnabled(true)
      await flushPromises()
      await wrapper.get('[title="sidebar.debugStudio"]').trigger('click')
      expect(openDebugStudio).toHaveBeenCalledOnce()
      await useDebugStudio().saveEnabled(false)
      await flushPromises()
      expect(wrapper.find('[title="sidebar.debugStudio"]').exists()).toBe(false)
    } finally {
      wrapper.unmount()
      Object.defineProperty(window, 'tinadec', { configurable: true, value: previousHost })
    }
  })
})

function menuButtons(): HTMLButtonElement[] {
  return Array.from(document.body.querySelectorAll('.row-context-menu button'))
}

async function expandProject(wrapper: ReturnType<typeof factory>) {
  const trigger = wrapper.get('[data-workspace-key=\"p-1\"] .project-row-main')
  if (trigger.attributes('aria-expanded') !== 'true') await trigger.trigger('click')
}

// happy-dom does not implement native Popover/ToggleEvent. Exercise Vue's
// real state listener with the browser event's fields; Chromium acceptance
// separately verifies the native default action and rendered transitions.
function dispatchViewToggle(element: Element, newState: 'open' | 'closed') {
  const event = new Event('beforetoggle')
  Object.defineProperty(event, 'newState', { value: newState })
  element.dispatchEvent(event)
}

describe('AppSidebar lifecycle management', () => {
  it('reorders workspaces, including the free conversation, and persists the new order', async () => {
    localStorage.clear()
    const second = { ...project, id: 'p-2', name: 'Second' }
    const wrapper = factory({ projects: [project, second] })
    const keys = () => wrapper.findAll('.project-group').map(node => node.attributes('data-workspace-key'))
    expect(keys()).toEqual([freeWorkspaceKey, 'p-1', 'p-2'])

    // Move the free conversation below the first project: it is no longer pinned to the top.
    await wrapper.get('[data-workspace-key="user::free"] .project-row-main').trigger('keydown', { key: 'ArrowDown', altKey: true })
    expect(keys()).toEqual(['p-1', freeWorkspaceKey, 'p-2'])

    await wrapper.get('[data-workspace-key="p-2"] .project-row-main').trigger('keydown', { key: 'ArrowUp', altKey: true })
    expect(keys()).toEqual(['p-1', 'p-2', freeWorkspaceKey])

    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).order).toEqual(['p-1', 'p-2', freeWorkspaceKey])

    // A plain arrow press must not reorder; it stays available to the row's own behaviour.
    await wrapper.get('[data-workspace-key="p-1"] .project-row-main').trigger('keydown', { key: 'ArrowDown' })
    expect(keys()).toEqual(['p-1', 'p-2', freeWorkspaceKey])
    wrapper.unmount()
  })

  it('shows only reordering actions for the free conversation', async () => {
    localStorage.clear()
    const wrapper = factory()
    await wrapper.get('[data-workspace-key="user::free"] .project-row').trigger('contextmenu')
    expect(menuButtons().map(node => node.textContent?.trim())).toEqual(['sidebar.moveUp', 'sidebar.moveDown'])
    wrapper.unmount()
  })

  it('folds without navigating, and isolates plus and section actions', async () => {
    localStorage.clear()
    const wrapper = factory()
    const row = wrapper.get('[data-workspace-key="p-1"] .project-row-main')
    await row.trigger('click')
    expect(row.attributes('aria-expanded')).toBe('false')
    expect(wrapper.emitted('select-project')).toBeUndefined()
    await wrapper.get('[data-workspace-key="p-1"] button[title="sidebar.newChat"]').trigger('click')
    expect(wrapper.emitted('create-session')).toEqual([['p-1']])
    expect(row.attributes('aria-expanded')).toBe('false')
    await wrapper.get('.workspace-section-add').trigger('click')
    expect(wrapper.emitted('open-project')).toHaveLength(1)
    expect(wrapper.get('.workspace-section-toggle').attributes('aria-expanded')).toBe('true')
    await wrapper.get('.workspace-section-toggle').trigger('keydown', { key: 'Enter' })
    await wrapper.get('.workspace-section-toggle').trigger('click')
    expect(wrapper.get('.workspace-section-toggle').attributes('aria-expanded')).toBe('false')
    wrapper.unmount()
  })

  it('keeps five recent conversations and the selected older one, then shows all', async () => {
    localStorage.clear()
    const sessions = Array.from({ length: 9 }, (_, index) => ({ ...session, id: `s-${index}`, updated_at: new Date(index * 1000).toISOString() }))
    const wrapper = factory({ sessions, selectedSessionId: 's-0' })
    expect(wrapper.findAll('.session-row')).toHaveLength(6)
    await wrapper.get('.workspace-show-more').trigger('click')
    expect(wrapper.findAll('.session-row')).toHaveLength(9)
    expect(wrapper.get('.workspace-show-more').text()).toBe('sidebar.showFewer')
    await wrapper.get('.workspace-show-more').trigger('click')
    expect(wrapper.findAll('.session-row')).toHaveLength(6)
    wrapper.unmount()
  })

  it('retains manual order when project activity refreshes and inserts new workspaces after free', async () => {
    localStorage.clear()
    const second = { ...project, id: 'p-2', name: 'Second' }
    const wrapper = factory({ projects: [project, second] })
    await wrapper.get('[data-workspace-key="p-2"] .project-row').trigger('contextmenu')
    menuButtons()[1].dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()
    expect(wrapper.findAll('.project-group').slice(1).map(node => node.attributes('data-workspace-key'))).toEqual(['p-2', 'p-1'])
    const refreshed = { ...project, updated_at: '2099-01-01' }
    await wrapper.setProps({ projects: [refreshed, second, { ...project, id: 'p-3' }] })
    expect(wrapper.findAll('.project-group').slice(1).map(node => node.attributes('data-workspace-key'))).toEqual(['p-3', 'p-2', 'p-1'])
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).order).toEqual([freeWorkspaceKey, 'p-3', 'p-2', 'p-1'])
    wrapper.unmount()
  })
  it('opens the native view chooser without changing the actual session view', async () => {
    const wrapper = factory()
    const trigger = wrapper.get<HTMLButtonElement>('[title="space.switchView"]')
    const menu = wrapper.get('.sidebar-view-menu')
    expect(trigger.attributes('popovertarget')).toBe(menu.attributes('id'))
    expect(trigger.attributes('popovertargetaction')).toBe('toggle')
    expect(trigger.attributes('aria-controls')).toBe(menu.attributes('id'))
    expect(trigger.attributes('aria-expanded')).toBe('false')
    await trigger.trigger('click')
    dispatchViewToggle(menu.element, 'open')
    await flushPromises()
    expect(trigger.attributes('aria-expanded')).toBe('true')
    expect(wrapper.emitted('change-view')).toBeUndefined()
    expect(menu.get('button[aria-pressed="true"]').text()).toBe('space.flat')
    dispatchViewToggle(menu.element, 'closed')
    await flushPromises()
    expect(trigger.attributes('aria-expanded')).toBe('false')
    wrapper.unmount()
  })

  it('switches immediately on each choice and returns focus without waiting for animation', async () => {
    const wrapper = factory({}, document.body)
    const trigger = wrapper.get<HTMLButtonElement>('[title="space.switchView"]')
    const menu = wrapper.get<HTMLDivElement>('.sidebar-view-menu')
    const hide = vi.fn(() => dispatchViewToggle(menu.element, 'closed'))
    Object.defineProperty(menu.element, 'hidePopover', { configurable: true, value: hide })
    await trigger.trigger('click')
    dispatchViewToggle(menu.element, 'open')
    const choices = menu.findAll<HTMLButtonElement>('button')
    choices[1]!.element.focus()
    await choices[1]!.trigger('click')
    expect(wrapper.emitted('change-view')).toEqual([['space']])
    expect(hide).toHaveBeenCalledTimes(1)
    expect(document.activeElement).toBe(trigger.element)
    await wrapper.setProps({ spaceActive: true })
    await trigger.trigger('click')
    choices[0]!.element.focus()
    await choices[0]!.trigger('click')
    expect(wrapper.emitted('change-view')).toEqual([['space'], ['flat']])
    expect(document.activeElement).toBe(trigger.element)
    expect(trigger.attributes('aria-expanded')).toBe('false')
    wrapper.unmount()
  })

  it('gives separate sidebars unique native popover targets', () => {
    // Vue useId is scoped to one app; two separately mounted test apps both
    // begin at v-0. Real UIE sidebars share a single renderer app.
    const wrapper = mount(defineComponent({
      components: { AppSidebar },
      template: '<div><AppSidebar :projects="[]" :sessions="[]" :selected-project-id="null" :selected-session-id="null" :busy="false" /><AppSidebar :projects="[]" :sessions="[]" :selected-project-id="null" :selected-session-id="null" :busy="false" space-active /></div>',
    }), { global: { stubs: { BrandLogo: true, TinadecCalligraphy: true } } })
    const menus = wrapper.findAll('.sidebar-view-menu')
    expect(menus[0]!.attributes('id')).not.toBe(menus[1]!.attributes('id'))
    expect(menus[1]!.get('button[aria-pressed="true"]').text()).toBe('space.title')
    wrapper.unmount()
  })

  it('keeps the observer-side navigation clickable while a run is busy', async () => {
    // 这条原本是聊天室的用例（它是唯一刻意不接 :disabled="busy" 的按钮）；聊天室界面删除后，
    // 同一个保证落在市场按钮上——busy 期间挡住导航就是运行中连页面都切不了。
    const wrapper = factory({ busy: true, collapsed: true })
    expect(wrapper.find('[title="sidebar.commandCenter"]').exists()).toBe(false)
    expect(wrapper.find('[title="space.switchView"]').exists()).toBe(true)
    const button = wrapper.get('[title="sidebar.market"]')
    expect(button.attributes('disabled')).toBeUndefined()
    await button.trigger('click')
    expect(wrapper.emitted('go-market')).toHaveLength(1)
    wrapper.unmount()
  })

  beforeEach(() => {
    localStorage.clear()
    confirmMock.mockClear()
    document.body.querySelectorAll('.row-context-menu').forEach((node) => node.remove())
  })

  it('opens the context menu on project right-click and archives via the menu', async () => {
    const wrapper = factory()
    await wrapper.find('[data-workspace-key=\"p-1\"] .project-row').trigger('contextmenu')
    expect(menuButtons().map((b) => b.textContent?.trim())).toEqual([
      'sidebar.editWorkspace',
      'sidebar.moveUp',
      'sidebar.moveDown',
      'sidebar.archive',
      'sidebar.moveToTrash',
    ])
    menuButtons()[3].dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()
    expect(wrapper.emitted('archive-project')?.[0]).toEqual(['p-1'])
  })

  it('moves a session to the trash only after confirmation', async () => {
    const wrapper = factory()
    await expandProject(wrapper)
    await wrapper.find('.session-row').trigger('contextmenu')
    menuButtons()[2].dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()
    expect(confirmMock).toHaveBeenCalledTimes(1)
    expect(wrapper.emitted('trash-session')?.[0]).toEqual(['s-1'])
  })

  it('does not trash the session when the confirmation is rejected', async () => {
    confirmMock.mockResolvedValueOnce(false)
    const wrapper = factory()
    await expandProject(wrapper)
    await wrapper.find('.session-row').trigger('contextmenu')
    menuButtons()[2].dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()
    expect(wrapper.emitted('trash-session')).toBeUndefined()
  })

  it('edits a workspace from its menu without selecting or folding', async () => {
    const wrapper = factory()
    await wrapper.get('[data-workspace-key="p-1"] .project-row').trigger('contextmenu')
    menuButtons()[0].dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()
    expect(wrapper.emitted('edit-workspace')?.[0]).toEqual(['p-1'])
    expect(wrapper.emitted('select-project')).toBeUndefined()
    expect(wrapper.get('[data-workspace-key="p-1"] .project-row-main').attributes('aria-expanded')).toBe('true')
    wrapper.unmount()
  })

  it('renames a session through the inline input', async () => {
    const wrapper = factory()
    await expandProject(wrapper)
    await wrapper.find('.session-item').trigger('dblclick')
    const input = wrapper.find('.inline-rename-input')
    expect(input.exists()).toBe(true)
    await input.setValue('Renamed session')
    await input.trigger('keydown', { key: 'Enter' })
    await input.trigger('blur')
    expect(wrapper.emitted('rename-session')?.[0]).toEqual(['s-1', 'Renamed session'])
  })

  it('opens the same menu from the hover more button', async () => {
    const wrapper = factory()
    await expandProject(wrapper)
    await wrapper.find('.session-more').trigger('click')
    expect(menuButtons()).toHaveLength(3)
  })

  it('renames a free conversation through the inline input', async () => {
    const free: SessionDto = {
      permission_mode: 'default', space_options: null, settings_revision: 0,
      id: 's-free', project_id: null, title: 'Free chat', status: 'ready',
      created_at: '2026-09-10T00:00:00Z', updated_at: '2026-09-10T00:00:00Z',
    }
    const wrapper = factory({ sessions: [free], selectedProjectId: null, selectedSessionId: null })
    await wrapper.find('.free-conversation-group .session-item').trigger('dblclick')
    const input = wrapper.find('.free-conversation-group .inline-rename-input')
    expect(input.exists()).toBe(true)
    await input.setValue('Renamed free chat')
    await input.trigger('keydown', { key: 'Enter' })
    await input.trigger('blur')
    expect(wrapper.emitted('rename-session')?.[0]).toEqual(['s-free', 'Renamed free chat'])
    wrapper.unmount()
  })

  it('offers only available active workspaces as migration targets and rejects a target that becomes unavailable', async () => {
    const free = { ...session, id: 's-free', project_id: null, storage_id: 'user' }
    const eligible = { ...project, storage_id: 'scope-active', lifecycle_status: 'active' as const, availability: 'ready' as const }
    const invalid: ProjectDto[] = [
      { ...project, id: 'broken', storage_id: 'scope-broken', lifecycle_status: 'active', availability: 'error' },
      { ...project, id: 'unknown', storage_id: 'scope-unknown', lifecycle_status: null },
      { ...project, id: 'unread', storage_id: 'scope-unread' },
      { ...project, id: 'archived', storage_id: 'scope-archived', lifecycle_status: 'archived' },
      { ...project, id: 'trashed', storage_id: 'scope-trashed', lifecycle_status: 'trashed' },
    ]
    const wrapper = factory({ projects: [eligible, ...invalid], sessions: [free], selectedProjectId: null, selectedSessionId: null })
    const dialog = wrapper.get<HTMLDialogElement>('.sidebar-migration-dialog')
    Object.defineProperty(dialog.element, 'showModal', { configurable: true, value: vi.fn() })
    Object.defineProperty(dialog.element, 'close', { configurable: true, value: vi.fn() })
    await wrapper.get('.free-conversation-group .session-row').trigger('contextmenu')
    menuButtons().find(button => button.textContent?.trim() === 'sidebar.migrateSession')?.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()
    const select = dialog.get('.ui-select-trigger')
    await select.trigger('click')
    expect(Array.from(document.body.querySelectorAll('.ui-select-option')).map(option => option.getAttribute('data-value'))).toEqual(['scope-active::p-1'])
    await select.trigger('keydown', { key: 'Escape' })

    await wrapper.setProps({ projects: [{ ...eligible, availability: 'error' }, ...invalid] })
    await dialog.get('form').trigger('submit')
    expect(wrapper.emitted('migrate-session')).toBeUndefined()
    wrapper.unmount()
  })

  it('lists a freshly created free conversation before its first message', () => {
    // A new conversation carries the default title until its first message
    // generates one; hiding that title made every fresh free conversation
    // invisible in the sidebar.
    const fresh: SessionDto = {
      permission_mode: 'default', space_options: null, settings_revision: 0,
      id: 's-free',
      project_id: null,
      title: 'Tinadec session',
      status: 'ready',
      created_at: '2026-09-10T00:00:00Z',
      updated_at: '2026-09-10T00:00:00Z',
    }

    const wrapper = factory({
      sessions: [session, fresh],
      selectedProjectId: null,
      selectedSessionId: null,
    })

    const freeRows = wrapper.findAll('.free-conversation-group .session-item')
    expect(freeRows).toHaveLength(1)
    expect(freeRows[0]!.text()).toContain('Tinadec session')
  })
})
