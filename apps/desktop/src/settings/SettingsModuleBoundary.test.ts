// @vitest-environment happy-dom
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { defineComponent, h, nextTick } from 'vue'
import SettingsModuleBoundary from './components/SettingsModuleBoundary.vue'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (k: string) => k }),
}))

const GoodChild = defineComponent({
  name: 'GoodChild',
  setup() {
    return () => h('div', { 'data-testid': 'good-child' }, 'child content')
  },
})

const ThrowOnRender = defineComponent({
  name: 'ThrowOnRender',
  setup() {
    return () => {
      throw new Error('render-time bomb')
    }
  },
})

const ThrowOnSetup = defineComponent({
  name: 'ThrowOnSetup',
  setup() {
    throw new Error('setup-time bomb')
    return () => null
  },
})

function mountBoundary(slot: ReturnType<typeof h>, props = { sectionId: 'model' as const }) {
  return mount(SettingsModuleBoundary, {
    props,
    global: {
      stubs: {
        SettingsModuleError: {
          template: '<div data-testid="error-fallback"><slot name="default" /></div>',
          props: ['sectionId'],
          emits: ['retry'],
        },
      },
    },
    slots: { default: () => slot },
  })
}

describe('SettingsModuleBoundary', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('renders its child slot content when no error occurs', () => {
    const wrapper = mountBoundary(h(GoodChild))
    expect(wrapper.find('[data-testid="good-child"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('child content')
  })

  it('catches a descendant render-time error and shows the error fallback', async () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {})
    const wrapper = mountBoundary(h(ThrowOnRender))

    await nextTick()
    await flushPromises()

    expect(wrapper.find('[data-testid="error-fallback"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="good-child"]').exists()).toBe(false)
    consoleSpy.mockRestore()
  })

  it('catches a setup-time error and shows the error fallback', async () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {})
    const wrapper = mountBoundary(h(ThrowOnSetup))

    await nextTick()
    await flushPromises()

    expect(wrapper.find('[data-testid="error-fallback"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="good-child"]').exists()).toBe(false)
    consoleSpy.mockRestore()
  })

  it('emits retry with the section id and remounts the slot', async () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {})
    const wrapper = mount(SettingsModuleBoundary, {
      props: { sectionId: 'model' as const },
      global: {
        stubs: {
          SettingsModuleError: {
            template: '<div data-testid="error-fallback" @click="$emit(\'retry\')" />',
            props: ['sectionId'],
            emits: ['retry'],
          },
        },
      },
      slots: { default: () => h(ThrowOnRender) },
    })

    await nextTick()
    await flushPromises()

    expect(wrapper.find('[data-testid="error-fallback"]').exists()).toBe(true)

    await wrapper.find('[data-testid="error-fallback"]').trigger('click')

    const emitted = wrapper.emitted('retry')
    expect(emitted).toBeDefined()
    expect(emitted).toHaveLength(1)
    if (emitted) {
      expect(emitted[0]).toEqual(['model'])
    }
    consoleSpy.mockRestore()
  })

  it('does not propagate the error to parent onErrorCaptured handlers', async () => {
    const parentCapture = vi.fn(() => true)
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {})

    const Parent = defineComponent({
      name: 'Parent',
      setup() {
        return () =>
          h(SettingsModuleBoundary, { sectionId: 'agents' }, () => h(ThrowOnRender))
      },
    })

    mount(Parent, {
      global: {
        plugins: [],
        stubs: {
          SettingsModuleError: {
            template: '<div data-testid="error-fallback" />',
            props: ['sectionId'],
            emits: ['retry'],
          },
        },
      },
      errorCaptured: parentCapture,
    })

    await nextTick()
    await flushPromises()

    expect(parentCapture).not.toHaveBeenCalled()
    consoleSpy.mockRestore()
  })

  it('isolates sibling boundaries: one failing does not affect the other', async () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {})

    const SiblingPair = defineComponent({
      name: 'SiblingPair',
      setup() {
        return () =>
          h('div', [
            h(SettingsModuleBoundary, { sectionId: 'tools' as const }, () => h(ThrowOnRender)),
            h(
              SettingsModuleBoundary,
              { sectionId: 'model' as const },
              () => h('button', { 'data-testid': 'sibling-btn' }, 'click me'),
            ),
          ])
      },
    })

    const wrapper = mount(SiblingPair, {
      global: {
        stubs: {
          SettingsModuleError: {
            template: '<div data-testid="error-fallback" />',
            props: ['sectionId'],
            emits: ['retry'],
          },
        },
      },
    })

    await nextTick()
    await flushPromises()

    expect(wrapper.find('[data-testid="error-fallback"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="sibling-btn"]').exists()).toBe(true)

    await wrapper.find('[data-testid="sibling-btn"]').trigger('click')

    expect(wrapper.find('[data-testid="sibling-btn"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="error-fallback"]').exists()).toBe(true)
    consoleSpy.mockRestore()
  })
})
