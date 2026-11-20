// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { defineComponent } from 'vue'
import { mount } from '@vue/test-utils'
import UiSelectField from './select-field.vue'

const options = [
  { value: 'queued', label: '排队（默认）' },
  { value: 'parallel', label: '并列' },
  { value: 'ask', label: '每次询问' },
] as const

describe('UiSelectField', () => {
  it('updates a v-model-bound parent when an option is chosen', async () => {
    const host = mount({
      components: { UiSelectField },
      data: () => ({ current: 'queued' }),
      template: `<UiSelectField v-model="current" :options="[
        { value: 'queued', label: '排队' },
        { value: 'parallel', label: '并列' },
      ]" />`,
    })
    await host.get('#model-reasoning, .ui-select-trigger').trigger('click')
    document.body.querySelector<HTMLButtonElement>('.ui-select-option[data-value="parallel"]')!.click()
    await host.vm.$nextTick()
    expect(host.vm.current).toBe('parallel')
    host.unmount()
  })

  it('shows the selected label and opens the styled listbox instead of a native select', async () => {
    const wrapper = mount(UiSelectField, { props: { value: 'queued', options } })
    expect(wrapper.find('select').exists()).toBe(false)
    expect(wrapper.get('.ui-select-label').text()).toBe('排队（默认）')
    expect(wrapper.find('.ui-select-menu').exists()).toBe(false)

    await wrapper.get('.ui-select-trigger').trigger('click')
    const menu = document.body.querySelector('.ui-select-menu')
    expect(menu).toBeTruthy()
    expect(menu!.textContent).toContain('并列')
    wrapper.unmount()
  })

  it('wires the trigger to the listbox with a stable id while open', async () => {
    const wrapper = mount(UiSelectField, { props: { value: 'queued', options } })
    const trigger = wrapper.get('.ui-select-trigger')
    expect(trigger.attributes('aria-controls')).toBeUndefined()
    expect(trigger.attributes('aria-expanded')).toBe('false')

    await trigger.trigger('click')
    const menuId = wrapper.get('.ui-select-trigger').attributes('aria-controls')
    expect(menuId).toBeTruthy()
    expect(document.body.querySelector('.ui-select-menu')?.id).toBe(menuId)
    expect(wrapper.get('.ui-select-trigger').attributes('aria-expanded')).toBe('true')
    wrapper.unmount()
  })

  it('points aria-activedescendant at the active option as the keyboard moves', async () => {
    const wrapper = mount(UiSelectField, { props: { value: 'queued', options } })
    await wrapper.get('.ui-select-trigger').trigger('click')
    const first = wrapper.get('.ui-select-trigger').attributes('aria-activedescendant')
    expect(first).toBeTruthy()
    expect(document.body.querySelector('.ui-select-menu')?.querySelector(`#${first}`)).not.toBeNull()

    await wrapper.get('.ui-select-trigger').trigger('keydown', { key: 'ArrowDown' })
    const second = wrapper.get('.ui-select-trigger').attributes('aria-activedescendant')
    expect(second).not.toBe(first)
    expect(document.body.querySelector('.ui-select-menu')?.querySelector(`#${second}`)?.getAttribute('data-value')).toBe('parallel')
    wrapper.unmount()
  })

  it('closes a teleported menu and blocks selection when the control becomes disabled', async () => {
    const wrapper = mount(UiSelectField, { props: { value: 'queued', options } })
    await wrapper.get('.ui-select-trigger').trigger('click')
    document.body.querySelector<HTMLButtonElement>('.ui-select-option[data-value="ask"]')!.click()
    expect(wrapper.emitted('change')).toHaveLength(1)

    await wrapper.setProps({ disabled: true })
    expect(document.body.querySelector('.ui-select-menu')).toBeNull()
    document.body.querySelector<HTMLButtonElement>('.ui-select-option[data-value="ask"]')?.click()
    expect(wrapper.emitted('change')).toHaveLength(1)
    wrapper.unmount()
  })

  it('exits the menu with Escape even when the key event originates on an option', async () => {
    const wrapper = mount(UiSelectField, { props: { value: 'queued', options } })
    await wrapper.get('.ui-select-trigger').trigger('click')
    const option = document.body.querySelector<HTMLButtonElement>('.ui-select-option[data-value="parallel"]')!
    option.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await wrapper.vm.$nextTick()
    expect(document.body.querySelector('.ui-select-menu')).toBeNull()
    wrapper.unmount()
  })

  it('emits value and change when an option is chosen, then closes', async () => {
    const wrapper = mount(UiSelectField, { props: { value: 'queued', options } })
    await wrapper.get('.ui-select-trigger').trigger('click')
    const items = document.body.querySelectorAll<HTMLButtonElement>('.ui-select-option')
    items[1].click()
    await wrapper.vm.$nextTick()

    expect(wrapper.emitted('update:value')?.at(-1)).toEqual(['parallel'])
    expect(wrapper.emitted('change')?.at(-1)).toEqual(['parallel'])
    expect(document.body.querySelector('.ui-select-menu')).toBeNull()
    wrapper.unmount()
  })

  it('is keyboard operable: arrows move the active option and Enter selects', async () => {
    const wrapper = mount(UiSelectField, { props: { value: 'queued', options } })
    await wrapper.get('.ui-select-trigger').trigger('keydown', { key: 'ArrowDown' })
    await wrapper.get('.ui-select-trigger').trigger('keydown', { key: 'ArrowDown' })
    await wrapper.get('.ui-select-trigger').trigger('keydown', { key: 'Enter' })

    expect(wrapper.emitted('change')?.at(-1)).toEqual(['parallel'])
    wrapper.unmount()
  })

  it('skips disabled options on click and in keyboard navigation', async () => {
    const withDisabled = [
      { value: 'a', label: 'A' },
      { value: 'b', label: 'B', disabled: true },
      { value: 'c', label: 'C' },
    ]
    const wrapper = mount(UiSelectField, { props: { value: 'a', options: withDisabled } })
    await wrapper.get('.ui-select-trigger').trigger('click')
    const items = document.body.querySelectorAll<HTMLButtonElement>('.ui-select-option')
    items[1].click()
    expect(wrapper.emitted('change')).toBeUndefined()

    await wrapper.get('.ui-select-trigger').trigger('keydown', { key: 'ArrowDown' })
    await wrapper.get('.ui-select-trigger').trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('change')?.at(-1)).toEqual(['c'])
    wrapper.unmount()
  })

  it('keeps the listbox inside an open native dialog', async () => {
    const dialog = document.createElement('dialog')
    dialog.setAttribute('open', '')
    document.body.append(dialog)
    const wrapper = mount(UiSelectField, { attachTo: dialog, props: { value: 'queued', options } })
    await wrapper.get('.ui-select-trigger').trigger('click')
    expect(dialog.querySelector('.ui-select-menu')).not.toBeNull()
    dialog.removeAttribute('open')
    dialog.dispatchEvent(new Event('close'))
    await wrapper.vm.$nextTick()
    expect(dialog.querySelector('.ui-select-menu')).toBeNull()
    wrapper.unmount()
    dialog.remove()
  })

  it('keeps the listbox inside an open role="dialog" container (e.g. reka-ui DialogContent)', async () => {
    const dialog = document.createElement('div')
    dialog.setAttribute('role', 'dialog')
    document.body.append(dialog)
    const wrapper = mount(UiSelectField, { attachTo: dialog, props: { value: 'queued', options } })
    await wrapper.get('.ui-select-trigger').trigger('click')
    expect(dialog.querySelector('.ui-select-menu')).not.toBeNull()
    wrapper.unmount()
    dialog.remove()
  })

  it('dismisses a floating listbox if its anchor moves with scrolling or resizing', async () => {
    const wrapper = mount(UiSelectField, { attachTo: document.body, props: { value: 'queued', options } })
    await wrapper.get('.ui-select-trigger').trigger('click')
    document.body.querySelector('.ui-select-menu')!.dispatchEvent(new Event('scroll'))
    await wrapper.vm.$nextTick()
    expect(document.body.querySelector('.ui-select-menu')).not.toBeNull()
    window.dispatchEvent(new Event('scroll'))
    await wrapper.vm.$nextTick()
    expect(document.body.querySelector('.ui-select-menu')).toBeNull()
    await wrapper.get('.ui-select-trigger').trigger('click')
    window.dispatchEvent(new Event('resize'))
    await wrapper.vm.$nextTick()
    expect(document.body.querySelector('.ui-select-menu')).toBeNull()
    wrapper.unmount()
  })

  it('does not open when disabled', async () => {
    const wrapper = mount(UiSelectField, { props: { value: 'queued', options, disabled: true } })
    await wrapper.get('.ui-select-trigger').trigger('click')
    expect(document.body.querySelector('.ui-select-menu')).toBeNull()
    wrapper.unmount()
  })
})
