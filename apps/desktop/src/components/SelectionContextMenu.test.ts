// @vitest-environment happy-dom
import { mount, flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import SelectionContextMenu from './SelectionContextMenu.vue'

/**
 * Behaviour snapshot for the selection right-click menu. The menu listens on
 * document and resolves its action matrix from the click target, so these tests
 * drive it the way a user does: build a selectable DOM node, select text, and
 * dispatch a real contextmenu event.
 */

const t = (key: string) => key
const writeText = vi.fn().mockResolvedValue(undefined)
const readText = vi.fn().mockResolvedValue('pasted text')

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t }),
}))
vi.mock('@/composables/usePanelStyles', () => ({
  usePanelStyles: () => ({
    getPanelStyle: () => ({}),
    getPanelDataAttributes: () => ({}),
  }),
}))
vi.mock('@/composables/useTerminal', () => ({
  useTerminal: () => ({ terminals: { value: [] } }),
}))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ notify: { error: vi.fn() } }),
}))

function mountMenu() {
  return mount(SelectionContextMenu, { attachTo: document.body })
}

/** Select the text of an element so window.getSelection() reports it. */
function selectElementText(el: HTMLElement) {
  const range = document.createRange()
  range.selectNodeContents(el)
  const selection = window.getSelection()!
  selection.removeAllRanges()
  selection.addRange(range)
  return selection
}

function rightClick(target: Element) {
  target.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 40, clientY: 40 }))
}

function menuItems(): string[] {
  return Array.from(document.body.querySelectorAll('.selection-menu__item'))
    .map((el) => el.textContent?.replace(/\s+/g, ' ').trim() ?? '')
}

/** Item labels with the trailing shortcut chip stripped, so assertions stay readable. */
function menuLabels(): string[] {
  return Array.from(document.body.querySelectorAll('.selection-menu__label'))
    .map((el) => el.textContent?.trim() ?? '')
}

describe('SelectionContextMenu', () => {
  beforeEach(() => {
    document.body.innerHTML = ''
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText, readText },
    })
    writeText.mockClear()
    readText.mockClear()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows content actions when right-clicking a text selection', async () => {
    const wrapper = mountMenu()
    const content = document.createElement('div')
    content.className = 'message-content'
    content.textContent = 'selected message'
    document.body.appendChild(content)
    selectElementText(content)

    rightClick(content)
    await flushPromises()

    expect(menuLabels()).toEqual(['contextMenu.copy', 'contextMenu.selectAll'])
    // Shortcut hints are shown for the standard actions.
    expect(menuItems()[0]).toContain('Ctrl+C')
    wrapper.unmount()
  })

  it('offers cut and paste inside an editable field', async () => {
    const wrapper = mountMenu()
    const input = document.createElement('input')
    input.value = 'hello world'
    document.body.appendChild(input)
    input.focus()
    input.setSelectionRange(0, 5)

    rightClick(input)
    await flushPromises()

    expect(menuLabels()).toEqual([
      'contextMenu.copy',
      'contextMenu.cut',
      'contextMenu.paste',
      'contextMenu.selectAll',
    ])
    wrapper.unmount()
  })

  it('does not open over plain chrome with no selection', async () => {
    const wrapper = mountMenu()
    const button = document.createElement('button')
    button.textContent = 'Save'
    document.body.appendChild(button)
    window.getSelection()?.removeAllRanges()

    rightClick(button)
    await flushPromises()

    expect(document.body.querySelector('.selection-menu')).toBeNull()
    wrapper.unmount()
  })

  it('copies the selection and closes on click', async () => {
    const wrapper = mountMenu()
    const content = document.createElement('div')
    content.className = 'markdown-body'
    content.textContent = 'copy me'
    document.body.appendChild(content)
    selectElementText(content)

    rightClick(content)
    await flushPromises()

    const copy = document.body.querySelector<HTMLElement>('.selection-menu__item')!
    await copy.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()

    expect(writeText).toHaveBeenCalledWith('copy me')
    expect(document.body.querySelector('.selection-menu')).toBeNull()
    wrapper.unmount()
  })

  it('closes on Escape', async () => {
    const wrapper = mountMenu()
    const content = document.createElement('div')
    content.className = 'detail-dialog'
    content.textContent = 'dialog text'
    document.body.appendChild(content)
    selectElementText(content)

    rightClick(content)
    await flushPromises()
    expect(document.body.querySelector('.selection-menu')).not.toBeNull()

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    await flushPromises()
    expect(document.body.querySelector('.selection-menu')).toBeNull()
    wrapper.unmount()
  })

  it('closes when clicking outside the menu', async () => {
    const wrapper = mountMenu()
    const code = document.createElement('code')
    code.textContent = 'const a = 1'
    document.body.appendChild(code)
    selectElementText(code)

    rightClick(code)
    await flushPromises()
    expect(document.body.querySelector('.selection-menu')).not.toBeNull()

    document.body.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))
    await flushPromises()
    expect(document.body.querySelector('.selection-menu')).toBeNull()
    wrapper.unmount()
  })

  it('offers link actions for an anchor selection', async () => {
    const wrapper = mountMenu()
    const anchor = document.createElement('a')
    anchor.href = 'https://tinadec.dev/docs'
    anchor.textContent = 'docs'
    document.body.appendChild(anchor)
    selectElementText(anchor)

    rightClick(anchor)
    await flushPromises()

    expect(menuLabels()).toContain('contextMenu.openLink')
    expect(menuLabels()).toContain('contextMenu.copyLink')
    wrapper.unmount()
  })

  it('stays out of the way when another component already handled the right-click', async () => {
    const wrapper = mountMenu()
    const row = document.createElement('div')
    row.className = 'message-content'
    row.textContent = 'row'
    row.addEventListener('contextmenu', (event) => event.preventDefault())
    document.body.appendChild(row)
    selectElementText(row)

    // RowContextMenu-style consumers call preventDefault in a bubble handler;
    // this menu must not also open.
    rightClick(row)
    await flushPromises()

    expect(document.body.querySelector('.selection-menu')).toBeNull()
    wrapper.unmount()
  })
})
