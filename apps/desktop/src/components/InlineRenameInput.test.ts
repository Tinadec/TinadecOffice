// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import InlineRenameInput from './InlineRenameInput.vue'

describe('InlineRenameInput', () => {
  it('submits the trimmed value on Enter', async () => {
    const wrapper = mount(InlineRenameInput, { props: { modelValue: 'Old name' } })
    const input = wrapper.find('input')
    await input.setValue('  New name  ')
    await input.trigger('keydown', { key: 'Enter' })
    // Enter blurs the input, which performs the submit.
    await input.trigger('blur')
    expect(wrapper.emitted('submit')?.[0]).toEqual(['New name'])
  })

  it('cancels on Escape without submitting afterwards', async () => {
    const wrapper = mount(InlineRenameInput, { props: { modelValue: 'Old name' } })
    const input = wrapper.find('input')
    await input.setValue('Changed')
    await input.trigger('keydown', { key: 'Escape' })
    await input.trigger('blur')
    expect(wrapper.emitted('cancel')).toHaveLength(1)
    expect(wrapper.emitted('submit')).toBeUndefined()
  })

  it('cancels when the value is unchanged', async () => {
    const wrapper = mount(InlineRenameInput, { props: { modelValue: 'Same' } })
    const input = wrapper.find('input')
    await input.trigger('blur')
    expect(wrapper.emitted('cancel')).toHaveLength(1)
    expect(wrapper.emitted('submit')).toBeUndefined()
  })

  it('selects the text when mounted', () => {
    const wrapper = mount(InlineRenameInput, { props: { modelValue: 'Selectable' } })
    expect((wrapper.element as HTMLInputElement).value).toBe('Selectable')
  })
})
