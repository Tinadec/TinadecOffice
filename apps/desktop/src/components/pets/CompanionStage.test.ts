// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import zh from '@/locales/zh-CN'
import { __resetLive2dLoaderForTests, sanitizeLive2dRelativeAssetPath } from '@/pets/live2dRuntimeLoader'
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

afterEach(() => {
  delete (window as unknown as { Live2DCubismCore?: unknown }).Live2DCubismCore
  __resetLive2dLoaderForTests()
  vi.restoreAllMocks()
})

describe('avatar seam consumers', () => {
  it('keeps the sprite stage empty when no atlas resolves, exactly as before', () => {
    const wrapper = mount(CompanionStage, {
      props: { petSlug: 'panda' },
      global: { plugins: [i18n()] },
    })
    expect(wrapper.find('[data-testid="companion-stage-live2d-unavailable"]').exists()).toBe(false)
    expect(wrapper.find('.companion-stage-pet').exists()).toBe(false)
    wrapper.unmount()
  })

  it('shows an explicit unready state instead of a sprite when Live2D SDK is not loaded', () => {
    const wrapper = mount(CompanionStage, {
      props: { petSlug: 'model-1', avatarKind: 'live2d' },
      global: { plugins: [i18n()] },
    })
    expect(wrapper.get('[data-testid="companion-stage-live2d-unavailable"]').text())
      .toBe(zh.settings.companions.live2d.unavailableShort)
    expect(wrapper.find('[data-testid="companion-stage-live2d-canvas"]').exists()).toBe(false)
    expect(wrapper.find('.companion-stage-pet').exists()).toBe(false)
    wrapper.unmount()
  })

  it('mounts the Live2D canvas, drives lip-sync ParamMouthOpenY, and releases WASM model on unmount when BYO SDK is present', async () => {
    const paramValues = new Float32Array([0, 0, 1])
    const releaseSpy = vi.fn()
    const updateSpy = vi.fn()
    ;(window as unknown as { Live2DCubismCore: unknown }).Live2DCubismCore = {
      Moc: {
        fromArrayBuffer: vi.fn(() => ({ release: vi.fn() })),
      },
      Model: {
        fromMoc: vi.fn(() => ({
          parameters: {
            count: 3,
            ids: ['ParamBreath', 'ParamMouthOpenY', 'ParamEyeLOpen'],
            minimumValues: new Float32Array([0, 0, 0]),
            maximumValues: new Float32Array([1, 1, 1]),
            defaultValues: new Float32Array([0, 0, 1]),
            values: paramValues,
          },
          update: updateSpy,
          release: releaseSpy,
        })),
      },
    }
    __resetLive2dLoaderForTests()

    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      const url = String(input)
      if (url.endsWith('/model3.json')) {
        return new Response(JSON.stringify({ FileReferences: { Moc: 'haru.moc3', Textures: [] } }), { status: 200 })
      }
      return new Response(new Uint8Array([77, 79, 67, 51]), { status: 200 })
    })

    const wrapper = mount(CompanionStage, {
      props: { petSlug: 'model-1', avatarKind: 'live2d', speaking: false },
      global: { plugins: [i18n()] },
    })
    await flushPromises()

    expect(wrapper.find('[data-testid="companion-stage-live2d-canvas"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="companion-stage-live2d-unavailable"]').exists()).toBe(false)
    expect(paramValues[1]).toBe(0)

    await wrapper.setProps({ speaking: true, mouthOpen: 0.75 })
    expect(paramValues[1]).toBeCloseTo(0.75, 3)
    expect(updateSpy).toHaveBeenCalled()

    wrapper.unmount()
    expect(releaseSpy).toHaveBeenCalledTimes(1)
  })

  it('rejects traversal and script files in Live2D relative asset paths', () => {
    expect(() => sanitizeLive2dRelativeAssetPath('../secret.txt')).toThrow()
    expect(() => sanitizeLive2dRelativeAssetPath('sub/exploit.js')).toThrow()
    expect(sanitizeLive2dRelativeAssetPath('textures/texture_00.png')).toBe('textures/texture_00.png')
  })

  it('renders a distinct glyph, never a sprite, in a compact Live2D avatar slot', () => {
    const wrapper = mount(AgentPetAvatar, {
      props: { petSlug: 'model-1', avatarKind: 'live2d', size: 24 },
      global: { plugins: [i18n()] },
    })
    expect(wrapper.find('.agent-pet-avatar-live2d').exists()).toBe(true)
    expect(wrapper.find('img').exists()).toBe(false)
    wrapper.unmount()
  })
})
