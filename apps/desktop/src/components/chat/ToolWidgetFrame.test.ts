// @vitest-environment happy-dom
import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import ToolWidgetFrame from './ToolWidgetFrame.vue'
import zh from '@/locales/zh-CN'

const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })

const registerToolWidget = vi.fn()
const releaseToolWidget = vi.fn()

function withBridge() {
  ;(window as unknown as { tinadec: unknown }).tinadec = { registerToolWidget, releaseToolWidget }
}
function withoutBridge() {
  delete (window as unknown as { tinadec?: unknown }).tinadec
}

describe('ToolWidgetFrame', () => {
  beforeEach(() => {
    registerToolWidget.mockReset()
    releaseToolWidget.mockReset()
    registerToolWidget.mockResolvedValue({ url: 'tinadec-widget://tok/', token: 'tok' })
    releaseToolWidget.mockResolvedValue(true)
  })
  afterEach(withoutBridge)

  it('mounts the frame only after the host has handed back a one-time URL', async () => {
    withBridge()
    const wrapper = mount(ToolWidgetFrame, { props: { html: '<p>hi</p>', title: 'tool' }, global: { plugins: [i18n] } })
    await vi.waitFor(() => expect(wrapper.find('[data-testid="tool-widget-frame"]').exists()).toBe(true))
    expect(registerToolWidget).toHaveBeenCalledWith('<p>hi</p>')
    // The renderer never holds the URL before main accepts the body, so a rejected payload has
    // no address to mount at all.
    expect(wrapper.get('[data-testid="tool-widget-frame"]').attributes('src')).toBe('tinadec-widget://tok/')
  })

  it('sandboxes without same-origin, so the frame cannot reach the host', async () => {
    withBridge()
    const wrapper = mount(ToolWidgetFrame, { props: { html: '<p>hi</p>', title: 'tool' }, global: { plugins: [i18n] } })
    await vi.waitFor(() => expect(wrapper.find('[data-testid="tool-widget-frame"]').exists()).toBe(true))
    const sandbox = wrapper.get('[data-testid="tool-widget-frame"]').attributes('sandbox')
    expect(sandbox).toBe('allow-scripts')
    // Both together would be no sandbox at all: the frame would share our origin.
    expect(sandbox).not.toContain('allow-same-origin')
    expect(wrapper.get('[data-testid="tool-widget-frame"]').attributes('referrerpolicy')).toBe('no-referrer')
  })

  it('shows the refusal rather than an empty frame when the host rejects the body', async () => {
    withBridge()
    registerToolWidget.mockResolvedValue(null)
    const wrapper = mount(ToolWidgetFrame, { props: { html: '<p>hi</p>', title: 'tool' }, global: { plugins: [i18n] } })
    await vi.waitFor(() => expect(wrapper.find('[role="alert"]').exists()).toBe(true))
    expect(wrapper.find('[data-testid="tool-widget-frame"]').exists()).toBe(false)
    // The real zh bundle is loaded here, so this asserts the key RESOLVED rather than that the
    // raw dotted string is on screen — a missing key would leave 'widget.refused' visible.
    expect(wrapper.text()).toContain(zh.widget.refused)
    expect(wrapper.text()).not.toContain('widget.refused')
  })

  it('mounts nothing without a bridge or a body', async () => {
    withoutBridge()
    const noBridge = mount(ToolWidgetFrame, { props: { html: '<p>hi</p>', title: 'tool' }, global: { plugins: [i18n] } })
    await Promise.resolve()
    expect(noBridge.find('[data-testid="tool-widget-frame"]').exists()).toBe(false)

    withBridge()
    const empty = mount(ToolWidgetFrame, { props: { html: '   ', title: 'tool' }, global: { plugins: [i18n] } })
    await Promise.resolve()
    expect(registerToolWidget).not.toHaveBeenCalled()
    expect(empty.find('[data-testid="tool-widget-frame"]').exists()).toBe(false)
  })

  it('revokes a widget whose frame never consumed it', async () => {
    withBridge()
    const wrapper = mount(ToolWidgetFrame, { props: { html: '<p>hi</p>', title: 'tool' }, global: { plugins: [i18n] } })
    await vi.waitFor(() => expect(wrapper.find('[data-testid="tool-widget-frame"]').exists()).toBe(true))
    wrapper.unmount()
    expect(releaseToolWidget).toHaveBeenCalledWith('tok')
  })
})
