// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, ref } from 'vue'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import zhCN from '@/locales/zh-CN'
import { __resetNotificationsForTests, useNotifications } from '@/composables/useNotifications'
import { resetHostAccessForTests, setGatewayConnectionLiveness, setHostAccessStatus } from '@/lib/hostAccess'

const controllerState = vi.hoisted(() => ({
  invokeError: null as { value: string | null } | null,
  draft: '',
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({
    push: vi.fn(),
    currentRoute: { value: { name: 'home', path: '/' } },
  }),
}))

vi.mock('@/controllers/HomeController', async () => {
  const { ref: vueRef } = await import('vue')
  const invokeErrorRef = vueRef<string | null>(null)
  controllerState.invokeError = invokeErrorRef
  return {
    homeController: {
      queuedMessages: vueRef([]),
      activeRuns: vueRef([]),
      invokeError: invokeErrorRef,
      selectedSessionId: vueRef<string | null>('sess-1'),
      currentSession: vueRef({ id: 'sess-1', conversation_template_slug: 'meeting' }),
      updateDraft: vi.fn((val: string) => {
        controllerState.draft = val
      }),
      sendMessage: vi.fn(async () => {}),
      createSession: vi.fn(async () => {}),
      steerQueued: vi.fn(async () => {}),
      promoteQueued: vi.fn(async () => {}),
      editQueued: vi.fn(async () => {}),
      dismissQueued: vi.fn(async () => {}),
    },
  }
})

vi.mock('@/composables/useDefaultModel', async () => {
  const { ref: vueRef } = await import('vue')
  return {
    useDefaultModel: () => ({
      current: vueRef(null),
      name: vueRef('gpt-5'),
      loaded: vueRef(true),
      busy: vueRef(false),
      models: vueRef([]),
      runtimes: vueRef([]),
      reasoningTarget: vueRef(null),
      reasoningSupported: vueRef(false),
      reasoningEffort: vueRef(''),
      load: vi.fn(async () => {}),
      choose: vi.fn(async () => {}),
      isCurrent: () => false,
      providerName: () => 'OpenAI',
      setReasoningEffort: vi.fn(),
    }),
  }
})

vi.mock('@/composables/useAgentModels', async () => {
  const { ref: vueRef } = await import('vue')
  return {
    useAgentModels: () => ({
      agents: vueRef([]),
      loaded: vueRef(true),
      error: vueRef(false),
      busy: vueRef(false),
      conversationAgent: vueRef(null),
      otherAgents: vueRef([]),
      load: vi.fn(async () => {}),
      effectiveChoice: () => null,
      ownStrategy: () => null,
      configuredStrategy: () => null,
      usageInMode: () => null,
      setModel: vi.fn(async () => true),
    }),
  }
})

vi.mock('@/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/api')>()
  return {
    ...actual,
    api: {
      ...actual.api,
      listModelProviders: vi.fn(async () => []),
      listAgentModeTopologies: vi.fn(async () => []),
      listDirectory: vi.fn(async () => ({ data: { entries: [] } })),
    },
  }
})

vi.mock('@/components/ui', () => ({
  UiButton: defineComponent({
    name: 'UiButton',
    props: { disabled: Boolean },
    emits: ['click'],
    setup(props, { slots, attrs, emit }) {
      return () =>
        h(
          'button',
          { ...attrs, type: 'button', disabled: props.disabled, onClick: (e: MouseEvent) => emit('click', e) },
          slots.default?.(),
        )
    },
  }),
  UiSelectField: defineComponent({
    name: 'UiSelectField',
    setup(_, { attrs }) {
      return () => h('select', attrs)
    },
  }),
}))

import ComposerBar from './ComposerBar.vue'

const wrappers: VueWrapper[] = []

function mountComposer(props: Record<string, unknown> = {}) {
  const i18n = createI18n({
    legacy: false,
    locale: 'en',
    messages: { en, 'zh-CN': zhCN },
  })
  const wrapper = mount(ComposerBar, {
    attachTo: document.body,
    props: {
      busy: false,
      modelValue: 'Hello agent',
      permission: 'default',
      sessionId: 'sess-1',
      settingsSaving: false,
      settingsError: null,
      ...props,
    },
    global: {
      plugins: [i18n],
      stubs: {
        Teleport: true,
        ComposerCommandPanel: defineComponent({
          name: 'ComposerCommandPanel',
          setup() {
            const page = ref('root')
            return { page, keydown: () => false, activate: () => {}, selectPage: (p: string) => { page.value = p } }
          },
          render: () => null,
        }),
        ImageLightbox: true,
      },
    },
  })
  wrappers.push(wrapper)
  return wrapper
}

beforeEach(() => {
  vi.clearAllMocks()
  __resetNotificationsForTests()
  resetHostAccessForTests()
  setHostAccessStatus({ state: 'ready', managed: true })
  if (controllerState.invokeError) controllerState.invokeError.value = null
})

afterEach(() => {
  wrappers.splice(0).forEach((w) => w.unmount())
  __resetNotificationsForTests()
  resetHostAccessForTests()
  document.body.innerHTML = ''
})

describe('ComposerBar notification unification', () => {
  it('routes offline send error to useNotifications with key composer-send & category connection and clears on draft edit', async () => {
    const { items } = useNotifications()
    const wrapper = mountComposer({ modelValue: 'Hello agent' })
    await flushPromises()

    controllerState.invokeError!.value = en.home.sendError.offline
    await flushPromises()

    // Never renders stuck inline .composer-error banner above the input
    expect(wrapper.find('.composer-error').exists()).toBe(false)
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)

    const sendNotif = items.value.find((item) => item.key === 'composer-send')
    expect(sendNotif).toBeDefined()
    expect(sendNotif?.category).toBe('connection')
    expect(sendNotif?.message).toBe(en.home.sendError.offline)

    // Editing the draft automatically clears 'composer-send'
    await wrapper.setProps({ modelValue: 'Hello agent updated' })
    await flushPromises()
    expect(items.value.some((item) => item.key === 'composer-send')).toBe(false)
  })

  it('clears composer-send and composer-settings automatically on resend and on canAccessBackend recovery', async () => {
    const { items } = useNotifications()
    const wrapper = mountComposer({
      hero: true,
      modelValue: 'Retry task',
      settingsError: 'Session settings save failed',
    })
    await flushPromises()

    expect(wrapper.find('.composer-error').exists()).toBe(false)
    expect(items.value.some((item) => item.key === 'composer-settings')).toBe(true)

    // Clearing settingsError prop clears 'composer-settings'
    await wrapper.setProps({ settingsError: null })
    await flushPromises()
    expect(items.value.some((item) => item.key === 'composer-settings')).toBe(false)

    // Trigger offline send + settings error while disconnected, then recover canAccessBackend
    controllerState.invokeError!.value = en.home.sendError.offline
    await flushPromises()
    expect(items.value.some((item) => item.key === 'composer-send')).toBe(true)

    setGatewayConnectionLiveness(true, false)
    await flushPromises()
    setGatewayConnectionLiveness(true, true)
    await flushPromises()

    expect(items.value.some((item) => item.key === 'composer-send')).toBe(false)

    // Trigger non-connection send error, then resend -> clears 'composer-send'
    controllerState.invokeError!.value = en.home.sendError.modelNotConfigured
    await flushPromises()
    expect(items.value.some((item) => item.key === 'composer-send')).toBe(true)

    await wrapper.get('[data-testid="composer-send"]').trigger('click')
    await flushPromises()
    expect(items.value.some((item) => item.key === 'composer-send')).toBe(false)
    expect(wrapper.emitted('welcome-submit')).toHaveLength(1)
  })
})
