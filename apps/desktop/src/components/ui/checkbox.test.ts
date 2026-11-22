// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import UiCheckbox from './checkbox.vue'
import source from './checkbox.vue?raw'

describe('UiCheckbox', () => {
  it('emits the toggled value', async () => {
    const wrapper = mount(UiCheckbox, { props: { modelValue: false } })
    await wrapper.get('button').trigger('click')
    expect(wrapper.emitted('update:modelValue')).toEqual([[true]])
    wrapper.unmount()
  })

  it('keeps a visible border despite the global button reset', () => {
    // `button { border: 0 }` is unlayered, so it wins over Tailwind's layered utility.
    expect(source).toContain('border: 1px solid var(--border-default)')
    expect(source).not.toContain('border: 1px solid hsl(var(--primary))')
  })
})
