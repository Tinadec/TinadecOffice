// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, nextTick, ref } from 'vue'
import { mount } from '@vue/test-utils'
import { Attachment, AttachmentContent, AttachmentDescription, AttachmentMedia, AttachmentTitle } from './attachment'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from './collapsible'
import { Marker, MarkerContent, MarkerIcon } from './marker'
import { Spinner } from './spinner'
import { useDelayedLoading } from '@/composables/useDelayedLoading'

const STATES = ['idle', 'uploading', 'processing', 'error', 'done'] as const

describe('Attachment', () => {
  it.each(STATES)('renders the %s state for styling and assistive tech', (state) => {
    const wrapper = mount(() =>
      h(Attachment, { state }, () => [
        h(AttachmentMedia, { variant: 'image' }, () => (state === 'uploading' || state === 'processing' ? h(Spinner) : h('img', { alt: '' }))),
        h(AttachmentContent, null, () => [h(AttachmentTitle, null, () => 'report.final.pdf'), h(AttachmentDescription, null, () => (state === 'error' ? 'Upload failed' : '12 KB'))]),
      ]),
    )
    const root = wrapper.find('[data-slot="attachment"]')
    expect(root.attributes('data-state')).toBe(state)
    expect(wrapper.find('[data-slot="attachment-media"]').attributes('data-variant')).toBe('image')
    expect(wrapper.find('[role="status"]').exists()).toBe(state === 'uploading' || state === 'processing')
    expect(wrapper.text()).toContain('report.final.pdf')
  })
})

describe('Spinner with delayed loading', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())

  function harness() {
    const loading = ref(false)
    const Harness = defineComponent({
      setup() {
        const visible = useDelayedLoading(loading)
        return () => h('div', [visible.value ? h(Spinner, { label: 'Saving' }) : null])
      },
    })
    return { loading, wrapper: mount(Harness) }
  }

  it('stays hidden for work that finishes within 150ms', async () => {
    const { loading, wrapper } = harness()
    loading.value = true
    await nextTick()
    vi.advanceTimersByTime(120)
    loading.value = false
    await nextTick()
    vi.advanceTimersByTime(500)
    await nextTick()
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })

  it('appears after 150ms and, once shown, stays at least 400ms', async () => {
    const { loading, wrapper } = harness()
    loading.value = true
    await nextTick()
    vi.advanceTimersByTime(149)
    await nextTick()
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
    vi.advanceTimersByTime(1)
    await nextTick()
    expect(wrapper.find('[role="status"]').attributes('aria-label')).toBe('Saving')
    vi.advanceTimersByTime(50)
    loading.value = false
    await nextTick()
    vi.advanceTimersByTime(349)
    await nextTick()
    expect(wrapper.find('[role="status"]').exists()).toBe(true)
    vi.advanceTimersByTime(1)
    await nextTick()
    expect(wrapper.find('[role="status"]').exists()).toBe(false)
  })

  it('keeps the spinner when work resumes during its minimum display', async () => {
    const { loading, wrapper } = harness()
    loading.value = true
    await nextTick()
    vi.advanceTimersByTime(200)
    loading.value = false
    await nextTick()
    vi.advanceTimersByTime(100)
    loading.value = true
    await nextTick()
    vi.advanceTimersByTime(1000)
    await nextTick()
    expect(wrapper.find('[role="status"]').exists()).toBe(true)
  })
})

describe('Marker', () => {
  it('passes the role through, so a live marker can be a status region', () => {
    const live = mount(() => h(Marker, { role: 'status' }, () => [h(MarkerIcon, null, () => h(Spinner)), h(MarkerContent, null, () => 'Compacting conversation')]))
    expect(live.find('[data-slot="marker"]').attributes('role')).toBe('status')
    const separator = mount(() => h(Marker, { variant: 'separator', role: 'separator' }, () => h(MarkerContent, null, () => 'Today')))
    const root = separator.find('[data-slot="marker"]')
    expect(root.attributes('data-variant')).toBe('separator')
    expect(root.attributes('role')).toBe('separator')
    expect(root.classes()).toContain('cn-marker-variant-separator')
  })
})

describe('Collapsible', () => {
  it('unmounts its content when closed and wires the trigger for assistive tech', async () => {
    const open = ref(false)
    const wrapper = mount(() =>
      h(Collapsible, { open: open.value, 'onUpdate:open': (value: boolean) => { open.value = value } }, () => [
        h(CollapsibleTrigger, null, () => 'Toggle'),
        h(CollapsibleContent, null, () => h('p', { class: 'body' }, 'Hidden work')),
      ]),
      { attachTo: document.body },
    )
    const trigger = wrapper.find('[data-slot="collapsible-trigger"]')
    expect(trigger.attributes('aria-expanded')).toBe('false')
    expect(wrapper.find('.body').exists()).toBe(false)
    await trigger.trigger('click')
    await nextTick()
    expect(open.value).toBe(true)
    expect(trigger.attributes('aria-expanded')).toBe('true')
    expect(trigger.attributes('aria-controls')).toBeTruthy()
    expect(wrapper.find('.body').exists()).toBe(true)
    await trigger.trigger('click')
    await nextTick()
    await new Promise((resolve) => setTimeout(resolve, 0))
    expect(wrapper.find('.body').exists()).toBe(false)
    wrapper.unmount()
  })
})
