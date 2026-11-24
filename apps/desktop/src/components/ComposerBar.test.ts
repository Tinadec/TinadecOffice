// @vitest-environment happy-dom
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
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

interface ParsedCssRule {
  selectors: string[]
  declarations: Record<string, string>
  atRule: string | null
}

function stripCssComments(css: string): string {
  return css.replace(/\/\*[\s\S]*?\*\//g, '')
}

function parseCssDeclarations(body: string): Record<string, string> {
  const out: Record<string, string> = {}
  for (const rawDecl of body.split(';')) {
    const colonIdx = rawDecl.indexOf(':')
    if (colonIdx <= 0) continue
    const prop = rawDecl.slice(0, colonIdx).trim().toLowerCase()
    const val = rawDecl.slice(colonIdx + 1).trim().replace(/\s+/g, ' ')
    if (prop) out[prop] = val
  }
  return out
}

function extractCssRules(rawCss: string, atRule: string | null = null): ParsedCssRule[] {
  const css = stripCssComments(rawCss)
  const rules: ParsedCssRule[] = []
  let i = 0
  while (i < css.length) {
    while (i < css.length && /\s/.test(css[i]!)) i++
    if (i >= css.length) break

    const headerStart = i
    while (i < css.length && css[i] !== '{' && css[i] !== ';') i++
    if (i >= css.length) break

    if (css[i] === ';') {
      // Top-level statement like @import or @source
      i++
      continue
    }

    const header = css.slice(headerStart, i).trim().replace(/\s+/g, ' ')
    i++ // consume '{'
    const bodyStart = i
    let depth = 1
    while (i < css.length && depth > 0) {
      if (css[i] === '{') depth++
      else if (css[i] === '}') depth--
      i++
    }
    const body = css.slice(bodyStart, i - 1)

    if (header.startsWith('@')) {
      if (/^@(?:media|container|layer|supports|starting-style)\b/i.test(header)) {
        rules.push(...extractCssRules(body, header))
      }
      continue
    }

    const selectors = header
      .split(',')
      .map((s) => s.trim().replace(/\s+/g, ' '))
      .filter(Boolean)
    rules.push({
      selectors,
      declarations: parseCssDeclarations(body),
      atRule,
    })
  }
  return rules
}

function findTopLevelDeclarations(rules: ParsedCssRule[], exactSelector: string): Record<string, string> {
  const merged: Record<string, string> = {}
  for (const rule of rules) {
    if (rule.atRule !== null) continue
    if (rule.selectors.includes(exactSelector)) {
      Object.assign(merged, rule.declarations)
    }
  }
  return merged
}

function resolveHorizontalPadding(declarations: Record<string, string>): { left: string; right: string } {
  let left = ''
  let right = ''
  if (declarations.padding) {
    const parts = declarations.padding.split(/\s+/).filter(Boolean)
    if (parts.length === 1) {
      left = parts[0]!
      right = parts[0]!
    } else if (parts.length === 2 || parts.length === 3) {
      left = parts[1]!
      right = parts[1]!
    } else if (parts.length >= 4) {
      right = parts[1]!
      left = parts[3]!
    }
  }
  if (declarations['padding-left']) left = declarations['padding-left']
  if (declarations['padding-right']) right = declarations['padding-right']
  return { left, right }
}

const currentDir = path.dirname(fileURLToPath(import.meta.url))
const composerVueSource = readFileSync(path.resolve(currentDir, './ComposerBar.vue'), 'utf8')
const globalStylesSource = readFileSync(path.resolve(currentDir, '../styles.css'), 'utf8')

describe('ComposerBar width & welcome-screen horizontal alignment guardrails', () => {
  it('does not declare scoped max-width: 100% on .composer or .composer-box in ComposerBar.vue', () => {
    const scopedMatch = composerVueSource.match(/<style\b[^>]*\bscoped\b[^>]*>([\s\S]*?)<\/style>/)
    expect(scopedMatch).not.toBeNull()

    const scopedRules = extractCssRules(scopedMatch![1]!)
    const composerRules = scopedRules.filter((r) =>
      r.selectors.some((sel) => sel === '.composer' || sel === '.composer-box'),
    )
    expect(composerRules.length).toBeGreaterThanOrEqual(2)

    for (const rule of composerRules) {
      expect(rule.declarations['max-width']).toBeUndefined()
    }

    // Mounted DOM keeps .composer wrapper and .composer-box.welcome-dialog without inline width overrides
    const wrapper = mountComposer({ hero: true })
    const root = wrapper.get('.composer')
    const box = wrapper.get('.composer-box')
    expect(root.classes()).toContain('composer--hero')
    expect(box.classes()).toContain('welcome-dialog')
    expect((root.element as HTMLElement).style.maxWidth).toBe('')
    expect((box.element as HTMLElement).style.maxWidth).toBe('')
  })

  it('preserves 852px outer wrapper and 820px inner dialog max-widths in styles.css', () => {
    const globalRules = extractCssRules(globalStylesSource)

    const composerDecl = findTopLevelDeclarations(globalRules, '.composer')
    const welcomeScreenDecl = findTopLevelDeclarations(globalRules, '.welcome-screen')
    const welcomeDialogDecl = findTopLevelDeclarations(globalRules, '.welcome-dialog')
    const welcomeContentDecl = findTopLevelDeclarations(globalRules, '.welcome-content')
    const heroConversationDecl = findTopLevelDeclarations(globalRules, '.conversation.composer-hero')

    expect(composerDecl['max-width']).toBe('min(852px, 100%)')
    expect(composerDecl.margin).toBe('0 auto')

    expect(welcomeScreenDecl['max-width']).toBe('min(852px, 100%)')
    expect(welcomeScreenDecl.margin).toBe('0 auto')

    expect(welcomeDialogDecl['max-width']).toBe('min(820px, 100%)')
    expect(welcomeDialogDecl.margin).toBe('0 auto')

    expect(welcomeContentDecl['max-width']).toBe('min(820px, 100%)')
    expect(welcomeContentDecl.gap).toBe('14px')
    expect(heroConversationDecl.gap).toBe('14px')
  })

  it('keeps horizontal padding strictly aligned between .welcome-screen and .composer across default, .chat-narrow, and .chat-ultra', () => {
    const globalRules = extractCssRules(globalStylesSource)

    const defaultWelcomePad = resolveHorizontalPadding(findTopLevelDeclarations(globalRules, '.welcome-screen'))
    const defaultComposerPad = resolveHorizontalPadding(findTopLevelDeclarations(globalRules, '.composer'))
    expect(defaultWelcomePad).toEqual({ left: '16px', right: '16px' })
    expect(defaultComposerPad).toEqual(defaultWelcomePad)

    const narrowWelcomePad = resolveHorizontalPadding(
      findTopLevelDeclarations(globalRules, '.conversation.chat-narrow .welcome-screen'),
    )
    const narrowComposerPad = resolveHorizontalPadding(
      findTopLevelDeclarations(globalRules, '.conversation.chat-narrow .composer'),
    )
    expect(narrowWelcomePad).toEqual({ left: '8px', right: '8px' })
    expect(narrowComposerPad).toEqual(narrowWelcomePad)

    const ultraWelcomePad = resolveHorizontalPadding(
      findTopLevelDeclarations(globalRules, '.conversation.chat-ultra .welcome-screen'),
    )
    const ultraComposerPad = resolveHorizontalPadding(
      findTopLevelDeclarations(globalRules, '.conversation.chat-ultra .composer'),
    )
    expect(ultraWelcomePad).toEqual({ left: '4px', right: '4px' })
    expect(ultraComposerPad).toEqual(ultraWelcomePad)
  })
})
