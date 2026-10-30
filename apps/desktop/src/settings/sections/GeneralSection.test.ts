// @vitest-environment happy-dom
import { defineComponent } from 'vue'
import { mount, flushPromises } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import zh from '@/locales/zh-CN'
import {
  CONVERSATION_FLOW_KEY,
  conversationFlowPreset,
  resetConversationFlowStyleForTests,
} from '@/lib/conversationFlowStyle'
import GeneralSection from './GeneralSection.vue'

/**
 * The window/tray switch is one of two views of `window.close_to_tray` — the tray checkbox is the
 * other. These cases cover the renderer half: it reads the stored value, writes through IPC, and
 * follows a change made from the tray while the page is open.
 */

const notifications = vi.hoisted(() => ({
  items: { value: [] as Array<{ id: string; key?: string }> },
  notify: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
  banner: { warning: vi.fn() },
  status: { error: vi.fn() },
}))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({
    items: notifications.items,
    notify: notifications.notify,
    banner: notifications.banner,
    status: notifications.status,
    confirm: vi.fn(),
    dismiss: vi.fn(),
    dismissByKey: vi.fn(),
  }),
}))
vi.mock('@/api', () => ({ api: { gatewayUrl: 'http://127.0.0.1:48730' } }))
vi.mock('@/lib/storageScope', () => ({ scopedApi: (api: unknown) => api }))
vi.mock('@/i18n', () => ({ SUPPORTED_LOCALES: ['zh-CN'], setInterfaceLocale: vi.fn(async () => true) }))
// UI primitives use Vapor; their own tests cover rendering. This fixture exercises settings state.
vi.mock('@/components/ui', () => ({
  UiBadge: { template: '<span><slot /></span>' },
  UiButton: { template: '<button v-bind="$attrs"><slot /></button>' },
  UiInput: defineComponent({ props: ['modelValue'], emits: ['update:modelValue'], template: '<input :value="modelValue" />' }),
  UiLabel: { template: '<label><slot /></label>' },
  UiSelectField: { props: ['value'], template: '<span class="ui-select" />' },
  UiSwitch: {
    props: ['modelValue', 'disabled'],
    emits: ['update:modelValue'],
    template: '<button type="button" role="switch" :aria-checked="String(!!modelValue)" :disabled="disabled" @click="$emit(\'update:modelValue\', !modelValue)" />',
  },
}))

let trayListener: ((value: { close_to_tray: boolean }) => void) | undefined
const bridge = {
  getAppConfig: vi.fn(),
  saveCloseToTray: vi.fn(),
  saveCompanionMode: vi.fn(),
  saveCompanionCapsuleStyle: vi.fn(),
  saveCompanionCapsuleReveal: vi.fn(),
  onCloseToTrayChanged: vi.fn((callback: (value: { close_to_tray: boolean }) => void) => {
    trayListener = callback
    return () => { trayListener = undefined }
  }),
}

function create() {
  return mount(GeneralSection, {
    global: {
      plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })],
    },
  })
}

function switchState(wrapper: ReturnType<typeof create>): string | undefined {
  return wrapper.get('[data-testid="close-to-tray-switch"]').attributes('aria-checked')
}

beforeEach(() => {
  vi.clearAllMocks()
  trayListener = undefined
  localStorage.clear()
  resetConversationFlowStyleForTests()
  bridge.getAppConfig.mockResolvedValue({ gateway_url: 'http://127.0.0.1:48730', source: 'default', managed: false, close_to_tray: false, companion_mode_enabled: false })
  bridge.saveCloseToTray.mockImplementation(async (enabled: boolean) => ({ close_to_tray: enabled }))
  bridge.saveCompanionMode.mockImplementation(async (enabled: boolean) => ({ companion_mode_enabled: enabled }))
  bridge.saveCompanionCapsuleStyle.mockImplementation(async (style: string) => ({ companion_capsule_style: style }))
  bridge.saveCompanionCapsuleReveal.mockImplementation(async (reveal: number) => ({ companion_capsule_reveal: reveal }))
  vi.stubGlobal('tinadec', bridge)
})

describe('window and tray settings', () => {
  it('shows the stored preference instead of a local default', async () => {
    bridge.getAppConfig.mockResolvedValue({ gateway_url: 'http://127.0.0.1:48730', source: 'default', managed: false, close_to_tray: true })
    const wrapper = create()
    await flushPromises()
    expect(switchState(wrapper)).toBe('true')
  })

  it('writes the preference through IPC and keeps the stored answer', async () => {
    const wrapper = create()
    await flushPromises()
    expect(switchState(wrapper)).toBe('false')

    await wrapper.get('[data-testid="close-to-tray-switch"]').trigger('click')
    await flushPromises()
    expect(bridge.saveCloseToTray).toHaveBeenCalledWith(true)
    expect(switchState(wrapper)).toBe('true')
  })

  it('rolls back and reports when the write fails', async () => {
    bridge.saveCloseToTray.mockRejectedValue(new Error('disk full'))
    const wrapper = create()
    await flushPromises()

    await wrapper.get('[data-testid="close-to-tray-switch"]').trigger('click')
    await flushPromises()
    expect(switchState(wrapper)).toBe('false')
    expect(notifications.notify.error).toHaveBeenCalledTimes(1)
  })

  it('follows a change made from the tray menu while the page is open', async () => {
    const wrapper = create()
    await flushPromises()

    trayListener?.({ close_to_tray: true })
    await flushPromises()
    expect(switchState(wrapper)).toBe('true')

    wrapper.unmount()
    // Unmounting must drop the IPC listener so a hidden window does not keep a stale closure alive.
    expect(bridge.onCloseToTrayChanged).toHaveBeenCalledTimes(1)
    expect(trayListener).toBeUndefined()
  })
})

/**
 * `desktop.toml [companion] enabled` is the only switch for the docked capsule. The renderer half:
 * read the stored flag, write it through IPC, and turn a window that cannot be shown into a visible
 * failure instead of a switch that appears to stay on.
 */
describe('companion mode', () => {
  it('shows the stored preference instead of a local default', async () => {
    bridge.getAppConfig.mockResolvedValue({ gateway_url: 'http://127.0.0.1:48730', source: 'default', managed: false, companion_mode_enabled: true })
    const wrapper = create()
    await flushPromises()
    expect(wrapper.get('[data-testid="companion-mode-switch"]').attributes('aria-checked')).toBe('true')
  })

  it('writes the preference through IPC and keeps the stored answer', async () => {
    const wrapper = create()
    await flushPromises()
    expect(wrapper.get('[data-testid="companion-mode-switch"]').attributes('aria-checked')).toBe('false')

    await wrapper.get('[data-testid="companion-mode-switch"]').trigger('click')
    await flushPromises()
    expect(bridge.saveCompanionMode).toHaveBeenCalledWith(true)
    expect(wrapper.get('[data-testid="companion-mode-switch"]').attributes('aria-checked')).toBe('true')
  })

  it('rolls back and reports when the window cannot be shown', async () => {
    bridge.saveCompanionMode.mockRejectedValue(new Error('companion window unavailable'))
    const wrapper = create()
    await flushPromises()

    await wrapper.get('[data-testid="companion-mode-switch"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="companion-mode-switch"]').attributes('aria-checked')).toBe('false')
    expect(notifications.notify.error).toHaveBeenCalledTimes(1)
  })
})

/**
 * The conversation-flow picker is one segmented switch of five density words. What a step *does*
 * is the consumers' business (lib/grouping, SubAgentCard, TurnWork); this file only has to prove
 * the page shows the stored step, describes only that step, writes a new one, and behaves like a
 * radio group on the keys.
 */
describe('conversation flow', () => {
  it('offers five density words and marks the stored step', async () => {
    const wrapper = create()
    await flushPromises()
    const segments = wrapper.findAll('[data-testid^="flow-style-"][role="radio"]')
    expect(segments.map((segment) => segment.attributes('data-testid'))).toEqual([
      'flow-style-minimal',
      'flow-style-concise',
      'flow-style-balanced',
      'flow-style-detailed',
      'flow-style-rich',
    ])
    // The word is the whole label: no shape names, no second line per step.
    expect(segments.map((segment) => segment.text())).toEqual(['极简', '简洁', '适中', '详细', '丰富'])
    expect(segments.filter((segment) => segment.attributes('aria-checked') === 'true').map((segment) => segment.attributes('data-testid'))).toEqual(['flow-style-rich'])
    expect(wrapper.get('[data-testid="conversation-flow-options"]').attributes('aria-orientation')).toBe('horizontal')
  })

  it('describes only the chosen step, one line at a time', async () => {
    const wrapper = create()
    await flushPromises()
    const description = wrapper.get('[data-testid="flow-style-description"]')
    expect(description.text()).toContain('所有步骤都留在流里')
    // Five parallel descriptions read as five recommendations; the other four must not be there.
    expect(description.text()).not.toContain('只留一行总结')
    expect(wrapper.findAll('[data-testid="flow-style-description"]')).toHaveLength(1)
  })

  it('stores the picked step instead of only restyling the page', async () => {
    const wrapper = create()
    await flushPromises()

    await wrapper.get('[data-testid="flow-style-concise"]').trigger('click')
    expect(localStorage.getItem(CONVERSATION_FLOW_KEY)).toBe('concise')
    expect(conversationFlowPreset.value).toBe('concise')
    expect(wrapper.get('[data-testid="flow-style-concise"]').attributes('aria-checked')).toBe('true')
    expect(wrapper.get('[data-testid="flow-style-rich"]').attributes('aria-checked')).toBe('false')
    // The description follows the choice, so the reader learns what the new step does in place.
    expect(wrapper.get('[data-testid="flow-style-description"]').text()).toContain('更早的折成一行')
  })

  it('moves the choice and the focus together on the arrow keys, as a radio group does', async () => {
    const wrapper = create()
    await flushPromises()

    const first = wrapper.get('[data-testid="flow-style-minimal"]')
    await first.trigger('keydown', { key: 'ArrowRight' })
    expect(localStorage.getItem(CONVERSATION_FLOW_KEY)).toBe('concise')
    expect(wrapper.get('[data-testid="flow-style-concise"]').attributes('aria-checked')).toBe('true')

    // Wrapping: left from the first step lands on the last, not on nothing.
    await wrapper.get('[data-testid="flow-style-minimal"]').trigger('keydown', { key: 'ArrowLeft' })
    expect(conversationFlowPreset.value).toBe('rich')

    // A key the group does not own must not steal the choice.
    await wrapper.get('[data-testid="flow-style-rich"]').trigger('keydown', { key: 'a' })
    expect(conversationFlowPreset.value).toBe('rich')
  })
})

describe('companion capsule style', () => {
  it('offers the two capsule styles and writes the picked one', async () => {
    const wrapper = create()
    await flushPromises()

    const options = wrapper.findAll('button[data-testid^="companion-style-"]')
    expect(options.map((option) => option.attributes('aria-checked'))).toEqual(['true', 'false'])
    expect(options.map((option) => option.text())).toEqual([
      zh.settings.companionCapsuleStylePeek,
      zh.settings.companionCapsuleStyleGrip,
    ])

    await wrapper.get('[data-testid="companion-style-grip"]').trigger('click')
    await flushPromises()

    expect(bridge.saveCompanionCapsuleStyle).toHaveBeenCalledWith('grip')
    expect(wrapper.get('[data-testid="companion-style-grip"]').attributes('aria-checked')).toBe('true')
  })

  it('rolls the capsule style back and reports when it cannot be stored', async () => {
    bridge.saveCompanionCapsuleStyle.mockRejectedValue(new Error('desktop.toml is read-only'))
    const wrapper = create()
    await flushPromises()

    await wrapper.get('[data-testid="companion-style-grip"]').trigger('click')
    await flushPromises()

    // Never show a style the capsule is not drawing.
    expect(wrapper.get('[data-testid="companion-style-peek"]').attributes('aria-checked')).toBe('true')
    expect(notifications.notify.error).toHaveBeenCalled()
  })

  it('offers the three reveal steps, defaulting to the middle one', async () => {
    const wrapper = create()
    await flushPromises()

    const options = wrapper.findAll('button[data-testid^="companion-reveal-"]')
    expect(options.map((option) => option.attributes('aria-checked'))).toEqual(['false', 'true', 'false'])
    expect(options.map((option) => option.text())).toEqual(['10%', '50%', '100%'])
  })

  it('writes the picked reveal and follows the stored step back', async () => {
    const wrapper = create()
    await flushPromises()

    await wrapper.get('[data-testid="companion-reveal-10"]').trigger('click')
    await flushPromises()

    expect(bridge.saveCompanionCapsuleReveal).toHaveBeenCalledWith(10)
    expect(wrapper.get('[data-testid="companion-reveal-10"]').attributes('aria-checked')).toBe('true')
  })

  it('shows the stored reveal instead of a local default', async () => {
    bridge.getAppConfig.mockResolvedValue({
      gateway_url: 'http://127.0.0.1:48730',
      source: 'default',
      managed: false,
      close_to_tray: false,
      companion_capsule_style: 'peek',
      companion_capsule_reveal: 100,
    })
    const wrapper = create()
    await flushPromises()

    expect(wrapper.get('[data-testid="companion-reveal-100"]').attributes('aria-checked')).toBe('true')
  })

  it('hides the reveal steps for a grip, which has no pet to reveal', async () => {
    bridge.getAppConfig.mockResolvedValue({
      gateway_url: 'http://127.0.0.1:48730',
      source: 'default',
      managed: false,
      close_to_tray: false,
      companion_capsule_style: 'grip',
    })
    const wrapper = create()
    await flushPromises()

    expect(wrapper.find('[data-testid="companion-reveal-options"]').exists()).toBe(false)
  })

  it('rolls the reveal back and reports when it cannot be stored', async () => {
    bridge.saveCompanionCapsuleReveal.mockRejectedValue(new Error('desktop.toml is read-only'))
    const wrapper = create()
    await flushPromises()

    await wrapper.get('[data-testid="companion-reveal-10"]').trigger('click')
    await flushPromises()

    // Never show a step the capsule is not parked at.
    expect(wrapper.get('[data-testid="companion-reveal-50"]').attributes('aria-checked')).toBe('true')
    expect(notifications.notify.error).toHaveBeenCalled()
  })
})
