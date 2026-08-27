// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ComposerBar from './ComposerBar.vue'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: vi.fn() }),
}))

const homeMock = vi.hoisted(() => ({
  queuedMessages: [] as Array<{ id: string; content: string }>,
  activeRuns: [] as Array<{ id: string; status: string }>,
  steerQueued: vi.fn(),
  promoteQueued: vi.fn(),
  editQueued: vi.fn(),
  dismissQueued: vi.fn(),
}))

vi.mock('@/controllers/HomeController', () => ({
  homeController: homeMock,
}))

function mountComposer(props: Partial<{ busy: boolean; modelValue: string }> = {}) {
  return mount(ComposerBar, {
    props: {
      busy: false,
      modelValue: '',
      permission: 'default',
      ...props,
    },
  })
}

describe('ComposerBar Codex shell contract', () => {
  it('idle composer reports data-composer-active=false', async () => {
    const wrapper = mountComposer()
    await flushPromises()
    expect(wrapper.find('.welcome-dialog').attributes('data-composer-active')).toBe('false')
    wrapper.unmount()
  })

  it('a non-empty draft flips the shell to active', async () => {
    const wrapper = mountComposer({ modelValue: 'hello' })
    await flushPromises()
    expect(wrapper.find('.welcome-dialog').attributes('data-composer-active')).toBe('true')
    wrapper.unmount()
  })

  it('busy state shows the spinner and keeps the shell active', async () => {
    const wrapper = mountComposer({ busy: true, modelValue: 'working' })
    await flushPromises()
    expect(wrapper.find('.welcome-dialog').attributes('data-composer-active')).toBe('true')
    expect(wrapper.find('.composer-send-spinner').exists()).toBe(true)
    wrapper.unmount()
    document.body.innerHTML = ''
  })
})
