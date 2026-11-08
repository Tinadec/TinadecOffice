// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import zh from '@/locales/zh-CN'
import AgentPetAvatar from './AgentPetAvatar.vue'
import CompanionStage from './CompanionStage.vue'

vi.mock('@/composables/useAgentAvatars', () => ({
  useAgentAvatars: () => ({
    urlForSlug: () => '',
    resolveInstanceAvatar: async () => null,
    revision: { value: 0 },
  }),
}))

function i18n() {
  return createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })
}

describe('avatar seam consumers', () => {
  it('keeps the sprite stage empty when no atlas resolves, exactly as before', () => {
    const wrapper = mount(CompanionStage, {
      props: { petSlug: 'panda' },
      global: { plugins: [i18n()] },
    })
    expect(wrapper.find('[data-testid="companion-stage-live2d-unavailable"]').exists()).toBe(false)
    expect(wrapper.find('.companion-stage-pet').exists()).toBe(false)
  })

  it('shows an explicit unready state instead of a sprite for a Live2D avatar', () => {
    const wrapper = mount(CompanionStage, {
      props: { petSlug: 'model-1', avatarKind: 'live2d' },
      global: { plugins: [i18n()] },
    })
    expect(wrapper.get('[data-testid="companion-stage-live2d-unavailable"]').text())
      .toBe(zh.settings.companions.live2d.unavailableShort)
    expect(wrapper.find('.companion-stage-pet').exists()).toBe(false)
  })

  it('renders a distinct glyph, never a sprite, in a compact Live2D avatar slot', () => {
    const wrapper = mount(AgentPetAvatar, {
      props: { petSlug: 'model-1', avatarKind: 'live2d', size: 24 },
      global: { plugins: [i18n()] },
    })
    expect(wrapper.find('.agent-pet-avatar-live2d').exists()).toBe(true)
    expect(wrapper.find('img').exists()).toBe(false)
  })
})
