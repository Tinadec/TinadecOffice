// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import type { Ref } from 'vue'
import CommandPalette from './CommandPalette.vue'
import { closePalette, openPalette, paletteIsOpen } from '@/composables/useCommandPalette'
import { PALETTE_COMBO, formatCombo } from '@/lib/keybindings'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string) => key }),
}))

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
  return { homeController: homeMock }
})

async function mountOpen() {
  // Attached to the document on purpose: focus and `getElementById` only reach nodes
  // that are actually in the tree, and "the caret went into the field" is the claim
  // this component makes.
  const wrapper = mount(CommandPalette, { attachTo: document.body })
  openPalette()
  await flushPromises()
  return wrapper
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
  homeMock.stoppableRunId.value = null
  homeMock.draft.value = ''
  routerMock.currentRoute.value = { name: 'home' }
  routerMock.push.mockClear()
  homeMock.sendMessage.mockClear()
  homeMock.updateDraft.mockClear()
  homeMock.createSession.mockClear()
  homeMock.stopRun.mockClear()
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
    expect(wrapper.find('[data-testid="palette-row-view.goSettings"]').exists()).toBe(true)

    homeMock.stoppableRunId.value = 'run-1'
    homeMock.draft.value = 'text to send'
    await flushPromises()
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
    const ids = wrapper.findAll('[data-testid^="palette-row-"]').map((row) => row.attributes('data-testid'))
    expect(ids).toEqual(['palette-row-view.goSettings'])

    await input.setValue('qqzzxx')
    expect(wrapper.findAll('[data-testid^="palette-row-"]')).toHaveLength(0)
    expect(wrapper.find('[data-testid="palette-empty"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('moves the highlight with the arrows and wraps at both ends', async () => {
    const wrapper = await mountOpen()
    const list = wrapper.find('[data-testid="palette-list"]')
    const rows = () => list.findAll('li[role="option"]')
    expect(rows()).toHaveLength(9)
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
      mode_version_id: null,
      meeting_model_override: null,
    })
    wrapper.unmount()
  })

  it('routes navigation by name rather than by a hand-written path', async () => {
    const wrapper = await mountOpen()
    const input = wrapper.find('[data-testid="palette-input"]')
    await input.setValue('market')
    await wrapper.find('[data-testid="palette-row-view.goMarket"]').trigger('click')
    expect(routerMock.push).toHaveBeenCalledWith({ name: 'market' })
    wrapper.unmount()
  })

  it('keeps aria wiring on the rows it exposes', async () => {
    const wrapper = await mountOpen()
    const input = wrapper.find('[data-testid="palette-input"]')
    expect(input.attributes('role')).toBe('combobox')
    expect(input.attributes('aria-controls')).toBe('command-palette-list')
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
})
