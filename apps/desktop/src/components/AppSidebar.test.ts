// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, nextTick } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import AppSidebar from './AppSidebar.vue'
import sidebarSource from './AppSidebar.vue?raw'
import type { ProjectDto, SessionDto } from '../api'
import { useDebugStudio } from '@/composables/useDebugStudio'
import { freeWorkspaceKey, workspaceListStorageKey } from '@/composables/useWorkspaceList'
import { auth } from '@/lib/auth'
import { setNickname } from '@/lib/userProfile'

vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key, te: () => false, locale: { value: 'en' } }),
}))

const { confirmMock, listRunsMock } = vi.hoisted(() => ({
  confirmMock: vi.fn(async (..._args: unknown[]) => true),
  listRunsMock: vi.fn(async (..._args: unknown[]): Promise<unknown[]> => []),
}))

vi.mock('../api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api')>()
  return {
    ...actual,
    api: {
      ...actual.api,
      listRuns: (...args: unknown[]) => listRunsMock(...args),
    },
  }
})

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

beforeEach(() => {
  listRunsMock.mockReset()
  listRunsMock.mockResolvedValue([])
})

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
  it('reorders the project rows without letting the free conversation leave its block', async () => {
    localStorage.clear()
    const second = { ...project, id: 'p-2', name: 'Second' }
    const wrapper = factory({ projects: [project, second] })
    const keys = () => wrapper.findAll('.project-group').map(node => node.attributes('data-workspace-key'))
    expect(keys()).toEqual([freeWorkspaceKey, 'p-1', 'p-2'])

    // The free conversation is a block of its own: its arrows belong to the option order above,
    // so a row-level reorder has to leave the list exactly as it was.
    await wrapper.get('[data-workspace-key="user::free"] .project-row-main').trigger('keydown', { key: 'ArrowDown', altKey: true })
    expect(keys()).toEqual([freeWorkspaceKey, 'p-1', 'p-2'])
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).order).toEqual([freeWorkspaceKey, 'p-1', 'p-2'])

    // Projects still reorder among themselves.
    await wrapper.get('[data-workspace-key="p-2"] .project-row-main').trigger('keydown', { key: 'ArrowUp', altKey: true })
    expect(keys()).toEqual([freeWorkspaceKey, 'p-2', 'p-1'])
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).order).toEqual([freeWorkspaceKey, 'p-2', 'p-1'])

    // A plain arrow press must not reorder; it stays available to the row's own behaviour.
    await wrapper.get('[data-workspace-key="p-1"] .project-row-main').trigger('keydown', { key: 'ArrowDown' })
    expect(keys()).toEqual([freeWorkspaceKey, 'p-2', 'p-1'])
    wrapper.unmount()
  })

  it('reorders the option blocks by dragging a row handle', async () => {
    localStorage.clear()
    localStorage.setItem(workspaceListStorageKey, JSON.stringify({ collapsed: false, collapsedKeys: [], allKeys: [], order: [], recent: true, projectConversations: true, freeConversation: true }))
    const wrapper = factory()
    const blocks = () => Array.from(wrapper.get('.sidebar-list').element.children).map((node) => {
      const element = node as HTMLElement
      return element.dataset.workspaceKey
        ?? (element.classList.contains('recent-conversations') ? 'recent' : element.classList.contains('activity-group') ? 'activity' : '?')
    })
    const labels = () => wrapper.findAll('.sidebar-option-label').map(node => node.text())

    // Default order mirrors the shipped list: recents on top, then the free block, then projects,
    // then the two activity views the option panel appends.
    expect(blocks()).toEqual(['recent', freeWorkspaceKey, 'p-1'])
    expect(labels()).toEqual(['sidebar.recentConversations', 'sidebar.recentActivity', 'chat.freeConversation', 'sidebar.projectConversations', 'sidebar.allActivity', 'sidebar.projectActivity'])

    // Drag 自由对话 onto the upper half of 最近对话: the handle is the drag source, the row is the
    // target, and the panel behind the popover follows the same list.
    await wrapper.get('[data-section="free"] .sidebar-option-grip').trigger('dragstart')
    await wrapper.get('[data-section="recent"]').trigger('dragover')
    await wrapper.get('[data-section="recent"]').trigger('drop')
    expect(blocks()).toEqual([freeWorkspaceKey, 'recent', 'p-1'])
    expect(labels()).toEqual(['chat.freeConversation', 'sidebar.recentConversations', 'sidebar.recentActivity', 'sidebar.projectConversations', 'sidebar.allActivity', 'sidebar.projectActivity'])
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).sectionOrder).toEqual(['free', 'recent', 'activity', 'projects', 'allActivity', 'projectActivity'])

    // Dropping on the lower half lands the block after the one under the pointer.
    await wrapper.get('[data-section="free"] .sidebar-option-grip').trigger('dragstart')
    await wrapper.get('[data-section="projects"]').trigger('dragover', { clientY: 10 })
    await wrapper.get('[data-section="projects"]').trigger('drop')
    expect(blocks()).toEqual(['recent', 'p-1', freeWorkspaceKey])
    wrapper.unmount()
  })

  it('moves an option block with the keyboard, and a click on the handle is not a toggle', async () => {
    localStorage.clear()
    const wrapper = factory()
    await wrapper.get('[data-section="activity"] [role="checkbox"]').trigger('keydown', { key: 'ArrowUp', altKey: true })
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).sectionOrder).toEqual(['activity', 'recent', 'free', 'projects', 'allActivity', 'projectActivity'])
    expect(wrapper.findAll('.sidebar-option-label').map(node => node.text())[0]).toBe('sidebar.recentActivity')

    // The grip is a drag affordance, not a second toggle.
    await wrapper.get('[data-section="free"] .sidebar-option-grip').trigger('click')
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).freeConversation).toBe(true)
    wrapper.unmount()
  })

  it('returns a hidden block to its own slot, not to the bottom', async () => {
    localStorage.setItem(workspaceListStorageKey, JSON.stringify({
      collapsed: false, collapsedKeys: [], allKeys: [], order: [],
      sectionOrder: ['free', 'projects', 'recent', 'activity'],
      recent: false, projectConversations: true, freeConversation: true,
    }))
    const wrapper = factory()
    const blocks = () => Array.from(wrapper.get('.sidebar-list').element.children)
      .map(node => (node as HTMLElement).dataset.workspaceKey ?? 'recent')
    expect(blocks()).toEqual([freeWorkspaceKey, 'p-1'])
    await wrapper.get('[data-section="recent"]').trigger('click')
    expect(blocks()).toEqual([freeWorkspaceKey, 'p-1', 'recent'])
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

  it('keeps the section action inside the same hover region as the workspace list', () => {
    // The reveal is pure CSS (`.workspace-section:hover`), which happy-dom cannot evaluate.
    // What a DOM test can pin is the region itself: the "new workspace" action and the
    // scrollport must share one ancestor, otherwise hovering a workspace stops surfacing
    // the button and it falls back to being permanently visible.
    const wrapper = factory()
    const section = wrapper.get('.workspace-section')
    expect(section.find('.workspace-section-add').exists()).toBe(true)
    expect(section.find('#sidebar-workspaces').exists()).toBe(true)
    expect(section.element.contains(wrapper.get('[data-workspace-key="p-1"]').element)).toBe(true)
    wrapper.unmount()
  })

  it('turns the cross-workspace recent list on from the workspace options picker', async () => {
    localStorage.clear()
    const second = { ...project, id: 'p-2', name: 'Second' }
    const free: SessionDto = { ...session, id: 's-free', project_id: null, title: 'Older free chat', updated_at: '2026-08-01T00:00:00Z' }
    const newest: SessionDto = { ...session, id: 's-new', project_id: 'p-2', title: 'Newest work', updated_at: '2026-09-05T00:00:00Z' }
    const wrapper = factory({ projects: [project, second], sessions: [session, free, newest], selectedSessionId: 's-new' }, document.body)

    // Off by default: the workspace rows stay the only navigation surface until it is asked for.
    expect(wrapper.find('.recent-conversations').exists()).toBe(false)
    const trigger = wrapper.get<HTMLButtonElement>('.workspace-section-option')
    const menu = wrapper.get<HTMLDivElement>('.sidebar-workspace-options')
    expect(trigger.attributes('popovertarget')).toBe(menu.attributes('id'))
    expect(trigger.attributes('aria-controls')).toBe(menu.attributes('id'))
    expect(trigger.attributes('aria-expanded')).toBe('false')

    await trigger.trigger('click')
    dispatchViewToggle(menu.element, 'open')
    await flushPromises()
    expect(trigger.attributes('aria-expanded')).toBe('true')

    const option = wrapper.get('.sidebar-option')
    const checkbox = option.get('[role="checkbox"]')
    expect(checkbox.attributes('aria-checked')).toBe('false')
    await option.trigger('click')
    expect(checkbox.attributes('aria-checked')).toBe('true')

    // Every workspace contributes — the free conversation included — newest first, and each row
    // names the workspace it came from so a flat list never loses "where am I".
    const rows = wrapper.findAll('.recent-conversation-row')
    expect(rows.map(row => row.get('.session-title').text())).toEqual(['Newest work', 'Demo session', 'Older free chat'])
    expect(rows.map(row => row.get('.recent-conversation-source').text())).toEqual(['Second', 'Demo project', 'chat.freeConversation'])
    expect(rows[0]!.classes()).toContain('active')

    // A checkbox list stays open so more options can be toggled.
    expect(trigger.attributes('aria-expanded')).toBe('true')
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).recent).toBe(true)

    // A recents row opens that conversation without expanding its workspace first.
    await rows[2]!.get('.session-item').trigger('click')
    expect(wrapper.emitted('select-session')?.at(-1)).toEqual(['s-free'])

    await option.trigger('click')
    expect(wrapper.find('.recent-conversations').exists()).toBe(false)
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).recent).toBe(false)
    wrapper.unmount()
  })

  it('hides project conversation groups without disabling projects or the recent shortcut', async () => {
    localStorage.setItem(workspaceListStorageKey, JSON.stringify({ collapsed: false, collapsedKeys: [], allKeys: [], order: [], recent: true, projectConversations: true }))
    const second = { ...project, id: 'p-2', name: 'Second' }
    const free: SessionDto = { ...session, id: 's-free', project_id: null, title: 'Free chat', updated_at: '2026-09-05T00:00:00Z' }
    const projectSession: SessionDto = { ...session, id: 's-p2', project_id: 'p-2', title: 'Project chat', updated_at: '2026-09-04T00:00:00Z' }
    const wrapper = factory({ projects: [project, second], sessions: [session, free, projectSession] })

    const projectOption = () => wrapper.get('[aria-label="sidebar.projectConversations"]')
    expect(projectOption().attributes('aria-checked')).toBe('true')
    expect(wrapper.find('[data-workspace-key="p-2"]').exists()).toBe(true)

    await projectOption().trigger('click')
    expect(projectOption().attributes('aria-checked')).toBe('false')
    expect(wrapper.find('[data-workspace-key="p-1"]').exists()).toBe(false)
    expect(wrapper.find('[data-workspace-key="p-2"]').exists()).toBe(false)
    expect(wrapper.find('[data-workspace-key="user::free"]').exists()).toBe(true)

    // Only the grouped list is hidden: the flat recent list still reaches project chats,
    // and the project feature itself stays registered (no unregister/trash action).
    expect(wrapper.findAll('.recent-conversation-row .session-title').map(node => node.text())).toContain('Project chat')
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).projectConversations).toBe(false)
    expect(wrapper.emitted('unregister-workspace')).toBeUndefined()
    expect(wrapper.emitted('trash-project')).toBeUndefined()

    await projectOption().trigger('click')
    expect(wrapper.find('[data-workspace-key="p-2"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('hides the free conversation row independently', async () => {
    localStorage.setItem(workspaceListStorageKey, JSON.stringify({ collapsed: false, collapsedKeys: [], allKeys: [], order: [], recent: false, projectConversations: true, freeConversation: true }))
    const second = { ...project, id: 'p-2', name: 'Second' }
    const wrapper = factory({ projects: [project, second] })

    const freeOption = () => wrapper.get('[aria-label="chat.freeConversation"]')
    expect(freeOption().attributes('aria-checked')).toBe('true')
    await freeOption().trigger('click')
    expect(freeOption().attributes('aria-checked')).toBe('false')
    expect(wrapper.find('[data-workspace-key="user::free"]').exists()).toBe(false)
    expect(wrapper.find('[data-workspace-key="p-1"]').exists()).toBe(true)
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).freeConversation).toBe(false)

    await freeOption().trigger('click')
    expect(wrapper.find('[data-workspace-key="user::free"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('comes back with the recent list already on after a remount', () => {
    localStorage.setItem(workspaceListStorageKey, JSON.stringify({ collapsed: false, collapsedKeys: [], allKeys: [], order: [], recent: true }))
    const wrapper = factory()
    expect(wrapper.get('.sidebar-option [role="checkbox"]').attributes('aria-checked')).toBe('true')
    expect(wrapper.findAll('.recent-conversation-row')).toHaveLength(1)
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

  it('offers the companion center as a third destination without changing the session view', async () => {
    const wrapper = factory({}, document.body)
    const trigger = wrapper.get<HTMLButtonElement>('[title="space.switchView"]')
    const menu = wrapper.get<HTMLDivElement>('.sidebar-view-menu')
    const hide = vi.fn(() => dispatchViewToggle(menu.element, 'closed'))
    Object.defineProperty(menu.element, 'hidePopover', { configurable: true, value: hide })
    await trigger.trigger('click')
    dispatchViewToggle(menu.element, 'open')

    const choices = menu.findAll<HTMLButtonElement>('button')
    expect(choices).toHaveLength(3)
    expect(choices[2]!.text()).toBe('space.companionCenter')

    choices[2]!.element.focus()
    await choices[2]!.trigger('click')
    expect(wrapper.emitted('go-companion-center')).toHaveLength(1)
    // Choosing the observing page is not a flat/space switch, and the popover closes.
    expect(wrapper.emitted('change-view')).toBeUndefined()
    expect(hide).toHaveBeenCalledTimes(1)
    expect(document.activeElement).toBe(trigger.element)
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

describe('AppSidebar footer identity', () => {
  // Since Logto owns the identity, the footer names a person only for a signed-in
  // account; signed out it reads "not logged in" and the portrait opens login.
  // The auth module keeps a module-level ref, so every case resets it.
  beforeEach(() => { setNickname(''); auth.loggedIn.value = true })
  afterEach(() => { setNickname(''); auth.loggedIn.value = false })

  it('keeps the profile portrait left of the shell actions and opens the personal module', async () => {
    const wrapper = factory()
    const children = Array.from(wrapper.get('.sidebar-footer').element.children)
    expect(children.map((node) => node.className.split(' ')[0])).toEqual([
      'sidebar-profile',
      'sidebar-footer-actions',
    ])

    const profile = wrapper.get('[title="sidebar.personal"]')
    expect(profile.find('.personal-avatar').exists()).toBe(true)
    await profile.trigger('click')
    expect(wrapper.emitted('go-personal')).toHaveLength(1)
    wrapper.unmount()
  })

  // happy-dom runs no layout engine: clientWidth is 0 and every rect is empty, so the
  // measurement is fed the numbers a real footer row reports before each assertion.
  function stubRow(footer: HTMLElement, actions: HTMLElement, probe: HTMLElement, footerWidth: number, nameWidth: number) {
    const rect = (width: number) => () => ({ width, height: 14, top: 0, left: 0, right: width, bottom: 14, x: 0, y: 0, toJSON: () => ({}) })
    footer.style.paddingLeft = '12px'
    footer.style.paddingRight = '12px'
    footer.style.columnGap = '8px'
    Object.defineProperty(footer, 'clientWidth', { configurable: true, value: footerWidth })
    Object.defineProperty(actions, 'getBoundingClientRect', { configurable: true, value: rect(88) })
    Object.defineProperty(probe, 'getBoundingClientRect', { configurable: true, value: rect(nameWidth) })
  }

  async function settle(wrapper: ReturnType<typeof factory>) {
    await nextTick()
    await nextTick()
    await wrapper.vm.$nextTick()
  }

  it('fades the nickname in beside the portrait once the footer can hold it whole', async () => {
    // Attached: happy-dom only resolves computed style (inline padding/gap here) for
    // elements that are actually in the document.
    const wrapper = factory({}, document.body)
    const footer = wrapper.get<HTMLElement>('.sidebar-footer').element
    const actions = wrapper.get<HTMLElement>('.sidebar-footer-actions').element
    const probe = wrapper.get<HTMLElement>('.sidebar-profile-name-probe').element
    const named = () => wrapper.get('.sidebar-profile').classes().includes('sidebar-profile--named')
    const name = () => wrapper.get('.sidebar-profile-name')

    // No layout yet: the name must not be claimed on a 0px row. It is rendered (so the
    // fade has something to animate) but collapsed to zero width by the style below.
    setNickname('风眠')
    await settle(wrapper)
    expect(named()).toBe(false)
    expect(name().classes()).not.toContain('sidebar-profile-name--shown')
    expect(name().text()).toBe('风眠')
    expect(probe.textContent).toBe('风眠')

    // 24 padding + 6+20+6 portrait row + 48 name + 10 clear space + 8 gap + 88 actions = 210.
    stubRow(footer, actions, probe, 260, 48)
    setNickname('风眠鸣')
    await settle(wrapper)
    expect(named()).toBe(true)
    expect(name().classes()).toContain('sidebar-profile-name--shown')
    // The reveal is capped at the measured width, so the fade cannot stop mid-word.
    expect(name().attributes('style')).toContain('--profile-name-max: 48px')
    expect(name().text()).toBe('风眠鸣')
    // The name is up, so the tooltip goes back to naming the destination only.
    expect(wrapper.get('.sidebar-profile').attributes('title')).toBe('sidebar.personal')

    // The same name stops fitting once the column is dragged narrower than 210px.
    stubRow(footer, actions, probe, 200, 48)
    setNickname('风眠鸣语')
    await settle(wrapper)
    expect(named()).toBe(false)
    expect(name().classes()).not.toContain('sidebar-profile-name--shown')
    // Hidden, but still discoverable: the tooltip names the person the portrait belongs to.
    expect(wrapper.get('.sidebar-profile').attributes('title')).toBe('风眠鸣语 · sidebar.personal')
    // The visible label is aria-hidden, so the control's own name carries the identity.
    expect(wrapper.get('.sidebar-profile').attributes('aria-label')).toBe('风眠鸣语 · sidebar.personal')
    wrapper.unmount()
  })

  it('renders no name at all when the profile has no nickname', async () => {
    const wrapper = factory()
    await settle(wrapper)
    expect(wrapper.find('.sidebar-profile-name').exists()).toBe(false)
    wrapper.unmount()
  })

  it('names the signed-out state instead of a person and opens login from the portrait', async () => {
    auth.loggedIn.value = false
    const wrapper = factory()
    await settle(wrapper)
    expect(wrapper.get('.sidebar-profile-name').text()).toBe('settings.personalNotLoggedIn')
    // Signed out the portrait is the login entry, not the personal module.
    await wrapper.get('.sidebar-profile').trigger('click')
    expect(wrapper.emitted('go-personal')).toBeUndefined()
    wrapper.unmount()
  })

  it('keeps the portrait visible above the stacked actions on the collapsed rail', () => {
    const wrapper = factory({ collapsed: true })
    const footer = wrapper.get('.sidebar-footer')
    expect(footer.find('.sidebar-profile').exists()).toBe(true)
    expect(footer.find('.sidebar-footer-actions-collapsed').exists()).toBe(true)
    wrapper.unmount()
  })

  it('never shows the nickname on the collapsed rail, however wide the row reports itself', async () => {
    setNickname('风眠')
    const wrapper = factory({ collapsed: true }, document.body)
    const footer = wrapper.get<HTMLElement>('.sidebar-footer').element
    const actions = wrapper.get<HTMLElement>('.sidebar-footer-actions').element
    const probe = wrapper.get<HTMLElement>('.sidebar-profile-name-probe').element
    // A rail that would otherwise pass the width test: only the collapsed guard hides the name.
    stubRow(footer, actions, probe, 900, 24)
    setNickname('风眠鸣')
    await settle(wrapper)
    expect(wrapper.get('.sidebar-profile').classes()).not.toContain('sidebar-profile--named')
    wrapper.unmount()
  })
})

describe('AppSidebar material surfaces', () => {
  it('applies the raised material filter to its top-layer surfaces', () => {
    // The panel root's backdrop-filter cannot reach native popovers/dialogs in the top layer.
    expect(sidebarSource).toMatch(/\.sidebar-view-menu, \.sidebar-workspace-options, \.sidebar-migration-dialog \{[^}]*backdrop-filter: var\(--material-filter-raised, none\);[^}]*\}/)
  })
})

describe('AppSidebar activity view', () => {
  const runRow = (sessionId: string, status: string, createdAt: string, updatedAt = createdAt) => ({
    id: `${sessionId}-run`, session_id: sessionId, status, summary: '', created_at: createdAt, updated_at: updatedAt,
  })

  it('reads no run rosters while the activity view is off', async () => {
    localStorage.clear()
    const wrapper = factory()
    await flushPromises()
    expect(wrapper.find('.activity-group').exists()).toBe(false)
    expect(listRunsMock).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('shows each recent conversation as a card with its outcome, span and workspace', async () => {
    localStorage.clear()
    const second = { ...project, id: 'p-2', name: 'Second' }
    const free: SessionDto = { ...session, id: 's-free', project_id: null, title: 'Free chat', updated_at: '2026-08-01T00:00:00Z' }
    const newest: SessionDto = { ...session, id: 's-new', project_id: 'p-2', title: 'Newest work', updated_at: '2026-09-05T00:00:00Z' }
    listRunsMock.mockImplementation(async (sessionId: unknown) => {
      if (sessionId === 's-new') return [runRow('s-new', 'awaiting_user', '2026-09-05T10:00:00Z', '2026-09-05T10:00:00Z')]
      if (sessionId === 's-1') return [runRow('s-1', 'completed', '2026-09-01T10:00:00Z', '2026-09-01T10:03:00Z')]
      return [runRow('s-free', 'failed', '2026-08-01T09:00:00Z', '2026-08-01T09:01:00Z')]
    })
    const wrapper = factory({ projects: [project, second], sessions: [session, free, newest] })
    await flushPromises()

    // The option is off by default and lives in the workspace options panel.
    expect(wrapper.find('.activity-card').exists()).toBe(false)
    await wrapper.get('[aria-label="sidebar.recentActivity"]').trigger('click')
    await flushPromises()

    const cards = wrapper.findAll('.activity-card')
    expect(cards.map(card => card.get('.activity-title').text())).toEqual(['Newest work', 'Demo session', 'Free chat'])
    // State keeps the canonical outcome, colour-coded, icon left of the word.
    expect(cards.map(card => card.get('.activity-state-label').text())).toEqual([
      'sidebar.activityState.awaiting',
      'sidebar.activityState.completed',
      'sidebar.activityState.failed',
    ])
    expect(cards[0]!.get('.activity-state').classes()).toContain('activity-state--awaiting')
    expect(cards[1]!.get('.activity-state').classes()).toContain('activity-state--completed')
    expect(cards[0]!.find('.activity-state-icon').exists()).toBe(true)
    // Facts sit right: the run's own span, then the workspace the conversation belongs to.
    expect(cards[1]!.get('.activity-duration').text()).toBe('3m')
    expect(cards.map(card => card.get('.activity-workspace').text())).toEqual(['Second', 'Demo project', 'chat.freeConversation'])

    await cards[0]!.trigger('click')
    expect(wrapper.emitted('select-session')?.at(-1)).toEqual(['s-new'])
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).activity).toBe(true)
    wrapper.unmount()
  })

  it('lists every conversation as a card in the 所有活动 block', async () => {
    localStorage.clear()
    localStorage.setItem(workspaceListStorageKey, JSON.stringify({ collapsed: false, collapsedKeys: [], allKeys: [], order: [], recent: false, activity: false, allActivity: true }))
    // More than 最近活动's five: the extra conversations are the whole point of the wide view.
    const many = Array.from({ length: 7 }, (_, index) => ({
      ...session,
      id: `s-${index}`,
      title: `Chat ${index}`,
      updated_at: new Date(Date.UTC(2026, 8, 1, 0, index)).toISOString(),
    }))
    const wrapper = factory({ sessions: many })
    await flushPromises()

    expect(wrapper.findAll('.activity-group')).toHaveLength(1)
    expect(wrapper.get('.activity-group-heading').text()).toBe('sidebar.allActivity')
    const cards = wrapper.findAll('.activity-card')
    expect(cards).toHaveLength(many.length)
    expect(cards.map(card => card.get('.activity-title').text())).toEqual(['Chat 6', 'Chat 5', 'Chat 4', 'Chat 3', 'Chat 2', 'Chat 1', 'Chat 0'])
    // The card names the workspace, because the flat list has no group header to do it for it.
    expect(cards[0]!.get('.activity-workspace').text()).toBe('Demo project')
    // One read per conversation and no more: the block reads exactly what it draws.
    expect(listRunsMock).toHaveBeenCalledTimes(many.length)
    expect(JSON.parse(localStorage.getItem(workspaceListStorageKey)!).allActivity).toBe(true)
    wrapper.unmount()
  })

  it('draws a project as activity cards in the 项目活动 block', async () => {
    localStorage.clear()
    localStorage.setItem(workspaceListStorageKey, JSON.stringify({ collapsed: false, collapsedKeys: [], allKeys: [], order: [], recent: false, activity: false, projectActivity: true, projectConversations: false }))
    const wrapper = factory({ sessions: [session] })
    await flushPromises()

    // Still organised by project: the row is the group, the conversations inside it are cards.
    const group = wrapper.get('[data-workspace-key="p-1"]')
    expect(group.get('.project-row-main').text()).toContain('Demo project')
    const cards = group.findAll('.activity-card')
    expect(cards).toHaveLength(1)
    expect(cards[0]!.get('.activity-title').text()).toBe('Demo session')
    // The group header already names the workspace, so the nested card does not repeat it.
    expect(cards[0]!.find('.activity-workspace').exists()).toBe(false)
    expect(group.find('.session-row').exists()).toBe(false)
    expect(listRunsMock).toHaveBeenCalledTimes(1)

    await cards[0]!.trigger('click')
    expect(wrapper.emitted('select-session')?.at(-1)).toEqual(['s-1'])
    wrapper.unmount()
  })

  it('offers all six blocks in the picker, in the order the list renders them', () => {
    localStorage.clear()
    const wrapper = factory()
    expect(wrapper.findAll('.sidebar-option-label').map(node => node.text())).toEqual([
      'sidebar.recentConversations',
      'sidebar.recentActivity',
      'chat.freeConversation',
      'sidebar.projectConversations',
      'sidebar.allActivity',
      'sidebar.projectActivity',
    ])
    wrapper.unmount()
  })
})
