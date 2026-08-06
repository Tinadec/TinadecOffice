// @vitest-environment happy-dom

import { defineComponent, h } from 'vue'
import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'

const calls = vi.hoisted(() => ({
  connect: vi.fn(),
  disconnect: vi.fn(),
  fetchTraces: vi.fn(async () => undefined),
  fetchDiagnostics: vi.fn(async () => undefined),
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    notify: { success: vi.fn() },
    confirm: vi.fn(async () => true),
  }),
}))
vi.mock('@/debug/composables/useDebugWebSocket', () => ({
  useDebugWebSocket: () => ({ connect: calls.connect, disconnect: calls.disconnect }),
}))
vi.mock('@/debug/composables/useTraceData', () => ({
  useTraceData: () => ({ fetchTraces: calls.fetchTraces }),
}))
vi.mock('@/debug/composables/useSimulation', () => ({
  useSimulation: () => ({ injectMessage: vi.fn(), forceApprovalDecision: vi.fn() }),
}))
vi.mock('@/debug/composables/useMetrics', () => ({
  useMetrics: () => ({ fetchDiagnostics: calls.fetchDiagnostics }),
}))

import {
  createDebugWorkbenchController,
  provideDebugWorkbench,
  useDebugWorkbench,
} from './debugController'

describe('Debug workbench controller', () => {
  it('starts one WebSocket and one data pipeline when multiple cards request the shared controller', () => {
    const Consumer = defineComponent({
      setup() {
        useDebugWorkbench().start()
        return () => h('span')
      },
    })
    const Host = defineComponent({
      setup() {
        const controller = createDebugWorkbenchController()
        provideDebugWorkbench(controller)
        return () => h('div', [h(Consumer), h(Consumer), h(Consumer)])
      },
    })

    const wrapper = mount(Host)
    expect(calls.connect).toHaveBeenCalledTimes(1)
    expect(calls.fetchTraces).toHaveBeenCalledTimes(1)
    expect(calls.fetchDiagnostics).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })
})
