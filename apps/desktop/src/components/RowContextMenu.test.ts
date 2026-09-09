// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'
import RowContextMenu, { type RowMenuItem } from './RowContextMenu.vue'

const items: RowMenuItem[] = [
  { key: 'rename', label: 'Rename' },
  { key: 'archive', label: 'Archive' },
  { key: 'trash', label: 'Delete', danger: true },
]

function factory(props: Partial<InstanceType<typeof RowContextMenu>['$props']> = {}) {
  return mount(RowContextMenu, {
    props: { visible: true, x: 10, y: 10, items, ...props },
    // The menu is wrapped in <Transition>; test-utils stubs it by default,
    // which renders nothing. Assert against the real DOM instead.
    global: { stubs: { transition: false } },
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

  it('animates the panel with a short, scale-from-trigger transition', () => {
    // The open/close transition was removed once (commit 3a81a1c) because
    // Teleport + <Transition> was believed to crash under happy-dom. It does
    // not: classic Teleport + Transition mounts fine. Pin the restored motion
    // so a future "simplification" cannot silently drop it again.
    const source = readFileSync(
      resolve(dirname(fileURLToPath(import.meta.url)), 'RowContextMenu.vue'),
      'utf-8',
    )
    expect(source).toContain('<Transition name="row-context-menu">')
    // Origin at the pointer corner, not the centre — the menu is anchored there.
    expect(source).toMatch(/\.row-context-menu\s*\{[^}]*transform-origin:\s*top left;/s)
    // Enter 130ms / leave 90ms: a menu opened many times a day must feel instant.
    expect(source).toMatch(/\.row-context-menu-enter-active\s*\{[^}]*130ms/s)
    expect(source).toMatch(/\.row-context-menu-leave-active\s*\{[^}]*90ms/s)
    // Never scale from 0.
    expect(source).toMatch(/\.row-context-menu-enter-from,[\s\S]*?scale\(0\.96\)/)
    // Motion must respect the reduced-motion preference.
    expect(source).toContain('prefers-reduced-motion: reduce')
  })
})
