// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AppSidebar from './AppSidebar.vue'
import type { ProjectDto, SessionDto } from '../api'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string) => key }),
}))

const { confirmMock } = vi.hoisted(() => ({ confirmMock: vi.fn(async () => true) }))
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
  id: 's-1',
  project_id: 'p-1',
  title: 'Demo session',
  status: 'ready',
  created_at: '2026-08-27T00:00:00Z',
  updated_at: '2026-08-27T00:00:00Z',
}

function factory(overrides: Record<string, unknown> = {}) {
  return mount(AppSidebar, {
    global: {
      stubs: { BrandLogo: true, TinadecCalligraphy: true },
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

function menuButtons(): HTMLButtonElement[] {
  return Array.from(document.body.querySelectorAll('.row-context-menu button'))
}

async function expandProject(wrapper: ReturnType<typeof factory>) {
  await wrapper.find('.project-row-main').trigger('click')
}

describe('AppSidebar lifecycle management', () => {
  beforeEach(() => {
    confirmMock.mockClear()
    document.body.querySelectorAll('.row-context-menu').forEach((node) => node.remove())
  })

  it('opens the context menu on project right-click and archives via the menu', async () => {
    const wrapper = factory()
    await wrapper.find('.project-row').trigger('contextmenu')
    expect(menuButtons().map((b) => b.textContent?.trim())).toEqual([
      'sidebar.rename',
      'sidebar.archive',
      'sidebar.moveToTrash',
    ])
    menuButtons()[1].dispatchEvent(new MouseEvent('click', { bubbles: true }))
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

  it('renames a project through the inline input', async () => {
    const wrapper = factory()
    await wrapper.find('.project-row-main').trigger('dblclick')
    const input = wrapper.find('.inline-rename-input')
    expect(input.exists()).toBe(true)
    await input.setValue('Renamed project')
    await input.trigger('keydown', { key: 'Enter' })
    await input.trigger('blur')
    expect(wrapper.emitted('rename-project')?.[0]).toEqual(['p-1', 'Renamed project'])
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
})
