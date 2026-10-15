// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, inject, ref, toValue, type Ref } from 'vue'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import UieCardHost from '../../../TinadecUI/src/components/UieCardHost.vue'

const componentFor = vi.hoisted(() => vi.fn())
vi.mock('../../../TinadecUI/src/components/useUie', () => ({
  useUie: () => ({ componentFor }),
}))

describe('UIE card activation', () => {
  let wrapper: ReturnType<typeof mount> | undefined
  afterEach(() => { wrapper?.unmount(); vi.clearAllMocks() })

  it('defers unopened content and retains its identity, state and visibility when revisited', async () => {
    const setup = vi.fn()
    const card = defineComponent({
      setup() {
        setup()
        const count = ref(0)
        const active = inject<Ref<boolean>>('uie:active')!
        const instanceId = inject('uie:instanceId')
        return () => h('button', {
          'data-active': toValue(active), 'data-id': instanceId,
          onClick: () => count.value++,
        }, String(count.value))
      },
    })
    componentFor.mockReturnValue(card)
    wrapper = mount(UieCardHost, {
      props: { instance: { id: 'terminal-1', descriptorId: 'terminal', title: 'Terminal', state: {} }, active: false },
      global: { plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] },
    })
    expect(setup).not.toHaveBeenCalled()
    expect(wrapper.find('button').exists()).toBe(false)
    await wrapper.setProps({ active: true })
    const button = wrapper.get('button').element
    await wrapper.get('button').trigger('click')
    expect(wrapper.get('button').text()).toBe('1')
    expect(wrapper.get('button').attributes('data-id')).toBe('terminal-1')
    await wrapper.setProps({ active: false })
    expect(wrapper.attributes('aria-hidden')).toBe('true')
    expect(wrapper.get('button').attributes('data-active')).toBe('false')
    await wrapper.setProps({ active: true })
    expect(wrapper.get('button').element).toBe(button)
    expect(wrapper.get('button').text()).toBe('1')
    expect(wrapper.get('button').attributes('data-active')).toBe('true')
    expect(setup).toHaveBeenCalledTimes(1)
  })

  it('renders the unknown-card fallback when that tab is first activated', async () => {
    componentFor.mockReturnValue(undefined)
    wrapper = mount(UieCardHost, {
      props: { instance: { id: 'missing-1', descriptorId: 'missing', title: 'Missing', state: {} }, active: false },
      global: { plugins: [createI18n({ legacy: false, locale: 'en', messages: { en } })] },
    })
    expect(wrapper.find('.uie-card-unknown').exists()).toBe(false)
    await wrapper.setProps({ active: true })
    expect(wrapper.get('.uie-card-unknown').text()).toContain('missing')
  })
})
