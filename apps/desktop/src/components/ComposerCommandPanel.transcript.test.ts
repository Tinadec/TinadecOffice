// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import ComposerCommandPanel from './ComposerCommandPanel.vue'
import zh from '@/locales/zh-CN'

const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })

vi.mock('@/controllers/HomeController', () => ({
  homeController: { currentSession: { value: null } },
}))
vi.mock('@/api', () => ({ api: { listModelProviders: vi.fn().mockResolvedValue([]) } }))

function mountPanel(props: Record<string, unknown> = {}) {
  return mount(ComposerCommandPanel, {
    props: {
      open: true,
      anchor: null,
      spatial: false,
      permission: 'default',
      transcriptTemplate: 'standard',
      dispatchPref: 'queued',
      slashQuery: null,
      settingsSaving: false,
      // Required by the panel: the attachment entry point and the command host its slash
      // commands filter against. The host is a set of callbacks, so this is a real one.
      canAttach: true,
      commandHost: {
        canStop: () => false,
        draft: () => '',
        setDraft: () => {},
        send: () => {},
        stopRun: () => {},
        newSession: () => {},
        navigate: () => {},
        routeName: () => 'home',
      },
      ...props,
    } as never,
    global: { plugins: [i18n], stubs: { Teleport: true } },
  })
}

/**
 * The conversation template is reachable only from the command panel — the composer toolbar used
 * to carry its own trigger and deliberately no longer does. That makes this row the single entry
 * point, so its presence here is worth pinning rather than assuming.
 */
describe('ComposerCommandPanel conversation template', () => {
  it('offers the template as a row that opens its own page', async () => {
    const wrapper = mountPanel()
    const row = wrapper.get('[data-testid="command-panel-transcript"]')
    // The row reports the current value rather than only naming itself.
    expect(row.text()).toContain(zh.transcript.standard)
    await row.trigger('click')
    const options = wrapper.findAll('[data-testid^="command-panel-"]')
      .filter(node => ['concise', 'standard', 'detailed', 'information'].includes(node.attributes('data-testid')?.replace('command-panel-', '') ?? ''))
    expect(options).toHaveLength(4)
  })

  it('emits the chosen template so the session can freeze it into the run', async () => {
    const wrapper = mountPanel()
    await wrapper.get('[data-testid="command-panel-transcript"]').trigger('click')
    await wrapper.get('[data-testid="command-panel-information"]').trigger('click')
    expect(wrapper.emitted('update:transcriptTemplate')?.[0]).toEqual(['information'])
  })

  it('marks the active template rather than only listing the options', async () => {
    const wrapper = mountPanel({ transcriptTemplate: 'detailed' })
    // The root row reports the current value before you open it.
    expect(wrapper.get('[data-testid="command-panel-transcript"]').text()).toContain(zh.transcript.detailed)
    await wrapper.get('[data-testid="command-panel-transcript"]').trigger('click')
    // Inside the page, the active option is the one marked pressed.
    const rows = wrapper.findAll('[data-testid^="command-panel-"]')
    const rowFor = (id: string) => rows.find(node => node.attributes('data-testid') === `command-panel-${id}`)
    expect(rowFor('detailed')?.attributes('aria-pressed')).toBe('true')
    expect(rowFor('concise')?.attributes('aria-pressed')).toBe('false')
    expect(rowFor('information')?.attributes('aria-pressed')).toBe('false')
  })
})

/**
 * The send default moved here from Settings › General. It is read on the send path, and this panel
 * is where a reader already is when they care about it.
 */
describe('ComposerCommandPanel send default', () => {
  it('offers the send behaviour and reports the current one', async () => {
    const wrapper = mountPanel({ dispatchPref: 'parallel' })
    const row = wrapper.get('[data-testid="command-panel-send"]')
    expect(row.text()).toContain(zh.settings.enterParallel)
    await row.trigger('click')
    const rows = wrapper.findAll('[data-testid^="command-panel-"]')
    const rowFor = (id: string) => rows.find(node => node.attributes('data-testid') === `command-panel-${id}`)
    expect(rowFor('queued')).toBeDefined()
    expect(rowFor('parallel')?.attributes('aria-pressed')).toBe('true')
    expect(rowFor('ask')).toBeDefined()
  })

  it('emits the chosen behaviour so the composer can persist it', async () => {
    const wrapper = mountPanel()
    await wrapper.get('[data-testid="command-panel-send"]').trigger('click')
    await wrapper.get('[data-testid="command-panel-ask"]').trigger('click')
    expect(wrapper.emitted('update:dispatchPref')?.[0]).toEqual(['ask'])
  })
})
