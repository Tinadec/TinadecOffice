// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import type { Ref } from 'vue'
import zh from '@/locales/zh-CN'
import type { CompanionSummary } from '@/lib/companionApi'
import BuddyPage from './BuddyPage.vue'

const store = vi.hoisted(() => ({
  buddyCompanion: null as unknown as Ref<CompanionSummary | null>,
  loading: null as unknown as Ref<boolean>,
}))

vi.mock('@/lib/companionStore', async () => {
  const { ref } = await import('vue')
  store.buddyCompanion = ref(null)
  store.loading = ref(false)
  return {
    useCompanionCenter: () => ({
      buddyCompanion: store.buddyCompanion,
      loading: store.loading,
    }),
  }
})

vi.mock('@/components/companion/BuddyChatPanel.vue', () => ({
  default: { name: 'BuddyChatPanel', props: ['companion'], template: '<div data-testid="buddy-chat-stub">{{ companion.name }}</div>' },
}))

vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }) }))

function mountPage() {
  return mount(BuddyPage, {
    global: {
      plugins: [createI18n({ locale: 'zh', legacy: false, messages: { zh } })],
      stubs: { UiButton: { template: '<button><slot /></button>' }, Bot: true, Settings: true },
    },
  })
}

afterEach(() => {
  store.buddyCompanion.value = null
  store.loading.value = false
})

describe('BuddyPage', () => {
  it('points at settings when no buddy is designated', async () => {
    const wrapper = mountPage()
    await flushPromises()
    expect(wrapper.find('[data-testid="buddy-chat-stub"]').exists()).toBe(false)
    expect(wrapper.text()).toContain(zh.buddy.empty)
  })

  it('renders the buddy chat once a buddy is designated', async () => {
    store.buddyCompanion.value = { id: 'c1', name: '小搭', avatar: null } as CompanionSummary
    const wrapper = mountPage()
    await flushPromises()
    const chat = wrapper.find('[data-testid="buddy-chat-stub"]')
    expect(chat.exists()).toBe(true)
    expect(chat.text()).toContain('小搭')
  })
})