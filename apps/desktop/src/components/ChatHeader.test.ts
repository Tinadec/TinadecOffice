// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import ChatHeader from './ChatHeader.vue'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}))

const runMock = vi.hoisted(() => ({
  runs: [
    { id: 'run-aaaa', status: 'completed' },
    { id: 'run-bbbb', status: 'running' },
    { id: 'run-cccc', status: 'awaiting_approval' },
  ] as Array<{ id: string; status: string }>,
  selectedRunId: null as string | null,
  select: vi.fn(),
}))

vi.mock('@/stores/run', () => ({
  useRunStore: () => runMock,
}))

function mountHeader() {
  return mount(ChatHeader, {
    props: { currentSession: { id: 's1', title: 'Test session', project_id: 'p1', created_at: '', updated_at: '' } },
  })
}

describe('ChatHeader run pills', () => {
  it('shows only active runs and marks the running one', async () => {
    setActivePinia(createPinia())
    const wrapper = mountHeader()
    await flushPromises()

    const pills = wrapper.findAll('.run-pill')
    expect(pills.length).toBe(2)
    expect(pills[0]!.text()).toContain('running')
    expect(pills[1]!.classes().join(' ')).toContain('run-pill--waiting')

    wrapper.unmount()
  })

  it('clicking a pill selects that run', async () => {
    setActivePinia(createPinia())
    const wrapper = mountHeader()
    await flushPromises()

    await wrapper.find('.run-pill').trigger('click')
    expect(runMock.select).toHaveBeenCalledWith('run-bbbb')

    wrapper.unmount()
    document.body.innerHTML = ''
  })
})
