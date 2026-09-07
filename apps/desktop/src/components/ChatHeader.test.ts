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
    { id: 'run-bbbb', status: 'executing' },
    { id: 'run-cccc', status: 'awaiting_approval' },
    { id: 'run-dddd', status: 'awaiting_user' },
  ] as Array<{ id: string; status: string }>,
  selectedRunId: null as string | null,
  select: vi.fn(),
}))

vi.mock('@/stores/run', () => ({
  useRunStore: () => runMock,
}))

function mountHeader() {
  return mount(ChatHeader, {
    props: { currentSession: { id: 's1', title: 'Test session', project_id: 'p1', status: 'active', created_at: '', updated_at: '' } },
  })
}

describe('ChatHeader run pills', () => {
  it('shows non-terminal runs (incl. awaiting_user) and marks running vs waiting', async () => {
    setActivePinia(createPinia())
    const wrapper = mountHeader()
    await flushPromises()

    // completed 被排除；executing / awaiting_approval / awaiting_user 均露出。
    // awaiting_user（监督升级等待决策）必须可见——回归钉住，避免再次被排除后聊天头部空白。
    const pills = wrapper.findAll('.run-pill')
    expect(pills.length).toBe(3)
    expect(pills[0]!.text()).toContain('executing')
    expect(pills[0]!.classes().join(' ')).toContain('run-pill--running')
    expect(pills[1]!.classes().join(' ')).toContain('run-pill--waiting')
    expect(pills[2]!.text()).toContain('awaiting user')
    expect(pills[2]!.classes().join(' ')).toContain('run-pill--waiting')

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
