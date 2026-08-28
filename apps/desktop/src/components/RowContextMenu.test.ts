// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import RowContextMenu, { type RowMenuItem } from './RowContextMenu.vue'

const items: RowMenuItem[] = [
  { key: 'rename', label: 'Rename' },
  { key: 'archive', label: 'Archive' },
  { key: 'trash', label: 'Delete', danger: true },
]

function factory(props: Partial<InstanceType<typeof RowContextMenu>['$props']> = {}) {
  return mount(RowContextMenu, {
    props: { visible: true, x: 10, y: 10, items, ...props },
  })
}

describe('RowContextMenu', () => {
  it('renders nothing while hidden', () => {
    factory({ visible: false })
    expect(document.body.querySelector('.row-context-menu')).toBeNull()
  })

  it('renders every item and emits select plus close on click', async () => {
    const wrapper = factory()
    const menu = document.body.querySelector('.row-context-menu')
    expect(menu).not.toBeNull()
    const buttons = Array.from(menu!.querySelectorAll('button'))
    expect(buttons.map((b) => b.textContent?.trim())).toEqual(['Rename', 'Archive', 'Delete'])
    expect(menu!.querySelector('.danger')).not.toBeNull()

    await buttons[1].dispatchEvent(new MouseEvent('click', { bubbles: true }))
    expect(wrapper.emitted('select')?.[0]).toEqual(['archive'])
    expect(wrapper.emitted('close')).toBeTruthy()
  })

  it('emits close on Escape', () => {
    const wrapper = factory()
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }))
    expect(wrapper.emitted('close')).toBeTruthy()
  })

  it('emits close when clicking outside the menu', () => {
    const wrapper = factory()
    document.body.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }))
    expect(wrapper.emitted('close')).toBeTruthy()
  })
})
