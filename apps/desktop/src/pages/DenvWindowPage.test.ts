// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import zhCN from '@/locales/zh-CN'
import en from '@/locales/en'
import DenvWindowPage from './DenvWindowPage.vue'

vi.mock('@/composables/useTheme', () => ({
  useTheme: () => ({ theme: { value: 'dark' }, applyInitialTheme: vi.fn() }),
}))

vi.mock('@/composables/useAgentAvatars', () => ({
  useAgentAvatars: () => ({
    urlForSlug: () => '',
    resolveInstanceAvatar: async () => null,
    revision: { value: 0 },
  }),
}))

function mountDenv(locale: 'zh-CN' | 'en' = 'zh-CN') {
  const i18n = createI18n({ legacy: false, locale, messages: { 'zh-CN': zhCN, en } })
  return mount(DenvWindowPage, {
    attachTo: document.body,
    global: {
      plugins: [i18n],
      stubs: { BrandLogo: { template: '<span data-testid="brand" />' } },
    },
  })
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('Denv office window', () => {
  it('keeps navigation and context as Office-style islands around an immersive reading surface', () => {
    const wrapper = mountDenv()
    expect(wrapper.find('.app-rail').exists()).toBe(true)
    expect(wrapper.find('.denv-navigation').exists()).toBe(true)
    expect(wrapper.find('.primary-surface').exists()).toBe(true)
    expect(wrapper.find('.need-card').exists()).toBe(true)
    expect(wrapper.find('.home-conversation-list').classes()).toContain('home-conversation-list')
    wrapper.unmount()
  })

  it('opens on an office today surface rather than a channel tree', () => {
    const wrapper = mountDenv()
    expect(wrapper.find('.home-scroll').exists()).toBe(true)
    expect(wrapper.find('.surface-header h1').text()).toBe('我的今天')
    expect(wrapper.find('.section-heading h2').text()).toBe('优先处理')
    expect(wrapper.find('.home-conversation-list').text()).toContain('Denv 设计')
    expect(wrapper.findAll('.nav-section')[2].text()).toContain('团队空间')
    wrapper.unmount()
  })

  it('keeps team chats, direct messages and agents in one personal message tree', () => {
    const wrapper = mountDenv()
    const groups = wrapper.findAll('.nav-section')
    expect(groups[1].findAll('.chat-nav-row')).toHaveLength(5)
    expect(groups[1].text()).toContain('研究伙伴')
    expect(groups[1].text()).toContain('明昭')
    expect(groups[1].text()).toContain('发布公告')
    expect(groups[2].text()).toContain('团队空间')
    wrapper.unmount()
  })

  it('presents light conversation, explicit decisions and folded agent work', async () => {
    const wrapper = mountDenv()
    await wrapper.findAll('.chat-nav-row')[0].trigger('click')
    expect(wrapper.find('.message-stream').text()).toContain('固定频道可以稳定团队记忆')
    expect(wrapper.find('.result-block.decision').text()).toContain('团队决定')
    expect(wrapper.find('.linked-work').text()).toContain('来源与比较已整理')
    expect(wrapper.find('.message-tabs').text()).toContain('工作')
    await wrapper.find('.message-tabs button:nth-child(2)').trigger('click')
    expect(wrapper.find('.work-table').text()).toContain('确认导航结构')
    wrapper.unmount()
  })

  it('keeps private agent assistance visibly private', async () => {
    const wrapper = mountDenv()
    const agentChat = wrapper.findAll('.chat-nav-row').find((item) => item.text().includes('研究伙伴'))
    await agentChat!.trigger('click')
    expect(wrapper.find('.message-heading p').text()).toContain('仅你可见')
    expect(wrapper.find('.message-heading h1').text()).toBe('研究伙伴')
    expect(wrapper.find('.message-stream').text()).toContain('团队频道表达共同记忆')
    wrapper.unmount()
  })

  it('opens file context on demand without a permanent IDE-style panel', async () => {
    const wrapper = mountDenv()
    await wrapper.findAll('.chat-nav-row')[0].trigger('click')
    expect(wrapper.find('.context-panel').exists()).toBe(false)
    const fileButton = wrapper.findAll('.message-context-button').find((item) => item.text().includes('文件'))
    await fileButton!.trigger('click')
    expect(wrapper.find('.context-panel').text()).toContain('文件与知识')
    expect(wrapper.find('.context-panel').text()).toContain('Denv 界面框架')
    wrapper.unmount()
  })

  it('sends a local draft without leaving the conversation', async () => {
    const wrapper = mountDenv()
    await wrapper.findAll('.chat-nav-row')[0].trigger('click')
    const textarea = wrapper.find('.message-composer textarea')
    await textarea.setValue('我先补一条测试消息')
    await wrapper.find('.send-button').trigger('click')
    await flushPromises()
    expect(wrapper.find('.message-stream').text()).toContain('我先补一条测试消息')
    expect((textarea.element as HTMLTextAreaElement).value).toBe('')
    wrapper.unmount()
  })

  it('shows work and agents as first-class office areas', async () => {
    const wrapper = mountDenv()
    await wrapper.findAll('.app-nav button')[2].trigger('click')
    expect(wrapper.find('.work-table').text()).toContain('确认导航结构')
    await wrapper.findAll('.app-nav button')[3].trigger('click')
    expect(wrapper.find('.agents-scroll').text()).toContain('研究伙伴')
    expect(wrapper.find('.agents-scroll').text()).toContain('团队 Agent')
    wrapper.unmount()
  })

  it('switches all new office labels to English', () => {
    const wrapper = mountDenv('en')
    expect(wrapper.find('.surface-header h1').text()).toBe('My day')
    expect(wrapper.find('.navigation-header').text()).toContain('Product & design office')
    expect(wrapper.find('.home-scroll').text()).toContain('Recent conversations')
    wrapper.unmount()
  })
})
