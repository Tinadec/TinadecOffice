// @vitest-environment happy-dom
import { describe, expect, it, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { createAsyncSettingsComponent } from './createAsyncSettingsComponent'
import type { SettingsLoader } from './types'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (k: string) => k }),
}))

function createStubLoader(content: string): SettingsLoader {
  return async () => {
    const { defineComponent: define, h: createH } = await import('vue')
    return {
      default: define({
        name: 'StubModule',
        setup() {
          return () => createH('div', { 'data-testid': 'stub-module' }, content)
        },
      }),
    }
  }
}

function stubLoaderReturnsComponent(): SettingsLoader {
  return async () => ({
    name: 'DirectComponent',
    setup() {
      return () => null
    },
  })
}

const BoundaryStub = {
  template: '<div data-testid="boundary"><slot /></div>',
  props: ['sectionId'],
  emits: ['retry'],
}

const BoundaryWithRetryStub = {
  template:
    '<div data-testid="boundary"><slot /><button data-testid="retry-btn" @click="$emit(\'retry\')" /></div>',
  props: ['sectionId'],
  emits: ['retry'],
}

const LoadingStub = {
  template: '<div data-testid="loading" />',
}

const ErrorStub = {
  template: '<div data-testid="error-ui" />',
  props: ['sectionId'],
  emits: ['retry'],
}

describe('createAsyncSettingsComponent', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('creates a valid Vue component from a successful loader', async () => {
    const AsyncModule = createAsyncSettingsComponent({
      loader: createStubLoader('hello'),
      sectionId: 'model',
    })

    const wrapper = mount(AsyncModule, {
      props: { sectionId: 'model' },
      global: { stubs: { SettingsModuleBoundary: BoundaryStub, SettingsModuleLoading: LoadingStub } },
    })

    await flushPromises()

    expect(wrapper.find('[data-testid="boundary"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="stub-module"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('hello')
  })

  it('passes sectionId through the boundary component', async () => {
    const AsyncModule = createAsyncSettingsComponent({
      loader: createStubLoader('content'),
      sectionId: 'agents',
    })

    const wrapper = mount(AsyncModule, {
      props: { sectionId: 'agents' },
      global: {
        stubs: {
          SettingsModuleBoundary: {
            template: '<div data-testid="boundary" :data-section="sectionId"><slot /></div>',
            props: ['sectionId'],
            emits: ['retry'],
          },
          SettingsModuleLoading: LoadingStub,
        },
      },
    })

    await flushPromises()

    const boundary = wrapper.find('[data-testid="boundary"]')
    expect(boundary.exists()).toBe(true)
    expect(boundary.attributes('data-section')).toBe('agents')
  })

  it('wraps the async component inside SettingsModuleBoundary', async () => {
    const AsyncModule = createAsyncSettingsComponent({
      loader: createStubLoader('test'),
      sectionId: 'tools',
    })

    const wrapper = mount(AsyncModule, {
      props: { sectionId: 'tools' },
      global: { stubs: { SettingsModuleBoundary: BoundaryStub, SettingsModuleLoading: LoadingStub } },
    })

    await flushPromises()

    expect(wrapper.find('[data-testid="boundary"]').exists()).toBe(true)
  })

  it('handles a loader that returns a component directly (not { default })', async () => {
    const AsyncModule = createAsyncSettingsComponent({
      loader: stubLoaderReturnsComponent(),
      sectionId: 'about',
    })

    const wrapper = mount(AsyncModule, {
      props: { sectionId: 'about' },
      global: { stubs: { SettingsModuleBoundary: BoundaryStub, SettingsModuleLoading: LoadingStub } },
    })

    await flushPromises()
    expect(wrapper.find('[data-testid="boundary"]').exists()).toBe(true)
  })

  it('emits retry when the boundary retries', async () => {
    const AsyncModule = createAsyncSettingsComponent({
      loader: createStubLoader('content'),
      sectionId: 'appearance',
    })

    const wrapper = mount(AsyncModule, {
      props: { sectionId: 'appearance' },
      global: {
        stubs: {
          SettingsModuleBoundary: BoundaryWithRetryStub,
          SettingsModuleLoading: LoadingStub,
        },
      },
    })

    await flushPromises()

    await wrapper.find('[data-testid="retry-btn"]').trigger('click')

    const emitted = wrapper.emitted('retry')
    expect(emitted).toHaveLength(1)
    expect(emitted?.[0]).toEqual(['appearance'])
  })

  it('shows error UI when loader rejects, then retry reloads successfully', async () => {
    let shouldFail = true
    const callTracker = { count: 0 }

    const flakyLoader: SettingsLoader = async () => {
      callTracker.count++
      if (shouldFail) throw new Error('load failed')
      return {
        default: defineComponent({
          name: 'RecoveredModule',
          setup() {
            return () => h('div', { 'data-testid': 'recovered' }, 'recovered content')
          },
        }),
      }
    }

    const AsyncModule = createAsyncSettingsComponent({
      loader: flakyLoader,
      sectionId: 'model',
    })

    const wrapper = mount(AsyncModule, {
      props: { sectionId: 'model' },
      global: {
        stubs: {
          SettingsModuleBoundary: BoundaryWithRetryStub,
          SettingsModuleLoading: LoadingStub,
          SettingsModuleError: ErrorStub,
        },
      },
    })

    await flushPromises()

    expect(wrapper.find('[data-testid="error-ui"]').exists()).toBe(true)
    expect(callTracker.count).toBeGreaterThanOrEqual(1)

    shouldFail = false
    await wrapper.find('[data-testid="retry-btn"]').trigger('click')
    await flushPromises()

    expect(callTracker.count).toBeGreaterThanOrEqual(2)
    expect(wrapper.find('[data-testid="recovered"]').exists()).toBe(true)
  })

  it('calls onRetry callback when boundary emits retry', async () => {
    const onRetryFn = vi.fn()

    const AsyncModule = createAsyncSettingsComponent({
      loader: createStubLoader('content'),
      sectionId: 'tools',
      onRetry: onRetryFn,
    })

    const wrapper = mount(AsyncModule, {
      props: { sectionId: 'tools' },
      global: {
        stubs: {
          SettingsModuleBoundary: BoundaryWithRetryStub,
          SettingsModuleLoading: LoadingStub,
        },
      },
    })

    await flushPromises()

    await wrapper.find('[data-testid="retry-btn"]').trigger('click')

    expect(onRetryFn).toHaveBeenCalledTimes(1)
    expect(onRetryFn).toHaveBeenCalledWith('tools')
  })
})
