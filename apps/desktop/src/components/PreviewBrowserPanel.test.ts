// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import zhCN from '@/locales/zh-CN'
import PreviewBrowserPanel from './PreviewBrowserPanel.vue'

function mountPanel(initialUrl = 'http://localhost:3000') {
  const i18n = createI18n({
    legacy: false,
    locale: 'zh-CN',
    messages: { 'zh-CN': zhCN },
  })
  return mount(PreviewBrowserPanel, {
    props: { initialUrl },
    global: { plugins: [i18n] },
  })
}

describe('PreviewBrowserPanel custom viewport', () => {
  it('supports switching between preset viewports and editable custom dimensions', async () => {
    const wrapper = mountPanel()

    expect(wrapper.find('[data-testid="preview-custom-viewport-bar"]').exists()).toBe(false)

    await wrapper.get('[data-testid="preview-viewport-custom"]').trigger('click')
    expect(wrapper.find('[data-testid="preview-custom-viewport-bar"]').exists()).toBe(true)

    const shell = wrapper.get('.preview-frame-shell')
    expect(shell.classes()).toContain('is-custom')
    expect(shell.attributes('style')).toContain('width: 1280px')
    expect(shell.attributes('style')).toContain('max-width: 100%')
    expect(shell.attributes('style')).toContain('height: 100%')

    const widthInput = wrapper.get('[data-testid="preview-custom-width"]')
    await widthInput.setValue('1024')
    await widthInput.trigger('change')
    expect(wrapper.get('.preview-frame-shell').attributes('style')).toContain('width: 1024px')

    // Disable auto-height and apply explicit pixel height
    await wrapper.get('[data-testid="preview-custom-auto-height"]').trigger('click')
    const heightInput = wrapper.get('[data-testid="preview-custom-height"]')
    await heightInput.setValue('640')
    await heightInput.trigger('change')
    expect(wrapper.get('.preview-frame-shell').attributes('style')).toContain('height: 640px')

    // Swap orientation (1024 x 640 -> 640 x 1024)
    await wrapper.get('[data-testid="preview-custom-rotate"]').trigger('click')
    expect(wrapper.get('.preview-frame-shell').attributes('style')).toContain('width: 640px')
    expect(wrapper.get('.preview-frame-shell').attributes('style')).toContain('height: 1024px')

    wrapper.unmount()
  })
})
