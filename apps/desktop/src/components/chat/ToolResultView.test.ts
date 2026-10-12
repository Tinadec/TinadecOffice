// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import ToolResultView from './ToolResultView.vue'
import zh from '@/locales/zh-CN'

const { getResult, loadDiff } = vi.hoisted(() => ({ getResult: vi.fn(), loadDiff: vi.fn() }))
vi.mock('@/api', () => ({ api: { getToolExecutionResult: getResult } }))
vi.mock('@/components/git/DiffViewer.vue', () => {
  loadDiff()
  return { __esModule: true, default: { props: ['diffText', 'filePath'], template: '<pre data-testid="lazy-diff" :data-file="filePath">{{ diffText }}</pre>' } }
})
const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })
const wrappers: ReturnType<typeof mount>[] = []
const register = vi.fn()
const release = vi.fn().mockResolvedValue(true)
function render() {
  const wrapper = mount(ToolResultView, { props: { executionId: 'exec-1', toolId: 'read_file', allowWidget: true }, global: { plugins: [i18n] } })
  wrappers.push(wrapper)
  return wrapper
}
beforeEach(() => {
  register.mockReset()
  getResult.mockResolvedValue({ body: 'plain result survives', widget_html: '<p>widget</p>', tool_id: 'read_file' })
})
afterEach(() => {
  wrappers.splice(0).forEach((wrapper) => wrapper.unmount())
  delete (window as unknown as { tinadec?: unknown }).tinadec
})

describe('ToolResultView widget fallback', () => {
  it.each(['missing bridge', 'refused', 'registration failure'])('retains the stored result for %s', async (mode) => {
    if (mode !== 'missing bridge') {
      ;(window as unknown as { tinadec: unknown }).tinadec = { registerToolWidget: register, releaseToolWidget: release }
      if (mode === 'refused') register.mockResolvedValue(null)
      else register.mockRejectedValue(new Error('registration failed'))
    }
    const wrapper = render()
    await flushPromises()
    expect(wrapper.get('.tool-result-raw').text()).toBe('plain result survives')
  })

  it('replaces text only after the frame loads, and restores it on a frame error', async () => {
    ;(window as unknown as { tinadec: unknown }).tinadec = { registerToolWidget: register, releaseToolWidget: release }
    register.mockResolvedValue({ token: 'tok', url: 'tinadec-widget://tok/' })
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.tool-result-raw').exists()).toBe(true)
    await wrapper.get('iframe').trigger('load')
    expect(wrapper.find('.tool-result-raw').exists()).toBe(false)
    await wrapper.get('iframe').trigger('error')
    expect(wrapper.get('.tool-result-raw').text()).toBe('plain result survives')
    expect(release).toHaveBeenCalledWith('tok')
  })

  it('loads the diff editor only when a stored result contains a diff', async () => {
    const wrapper = render()
    await flushPromises()
    expect(wrapper.get('.tool-result-raw').text()).toBe('plain result survives')
    expect(loadDiff).not.toHaveBeenCalled()
    const patch = '--- a/readme.txt\n+++ b/readme.txt\n@@ -1 +1 @@\n-old\n+new'
    getResult.mockResolvedValue({ body: JSON.stringify({ patch }), tool_id: 'replace_lines' })
    await wrapper.setProps({ executionId: 'exec-diff' })
    await vi.waitFor(() => expect(wrapper.find('[data-testid="lazy-diff"]').exists()).toBe(true))
    expect(loadDiff).toHaveBeenCalledOnce()
    expect(wrapper.get('[data-testid="lazy-diff"]').text()).toBe(patch)
    expect(wrapper.get('[data-testid="lazy-diff"]').attributes('data-file')).toBe('replace_lines')
  })
})
