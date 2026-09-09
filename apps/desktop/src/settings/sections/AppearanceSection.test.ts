// @vitest-environment happy-dom
/**
 * AppearanceSection regression tests.
 *
 * These pin the bugs found in the 2026-09 audit:
 * - the opacity slider must span the stored 0–100 percent domain (it used to
 *   range 0–1, so one drag wrote 0.5 into a percent field and App.vue divided
 *   again, leaving the background at 0.5% opacity);
 * - the blur slider must not offer values its clamp rejects;
 * - the HTML background type must show markup guidance, not video formats;
 * - the custom accent picker must commit the exact hex the user typed.
 */
import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import AppearanceSection from './AppearanceSection.vue'
import { __resetPanelStylesForTests } from '@/composables/usePanelStyles'
import { __resetThemeForTests } from '@/composables/useTheme'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (key: string) => key, locale: { value: 'zh-CN' } }),
}))

vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ notify: { success: vi.fn(), error: vi.fn() } }),
}))

const STORAGE_KEYS = [
  'tinadec-theme',
  'tinadec-accent-color',
  'tinadec-custom-accent',
  'tinadec-background',
  'tinadec-panel-style',
  'tinadec-dynamic-palette',
]

function mountSection() {
  // No component stubs: the section's real children (including the Vapor
  // primitives) must mount, since the tests assert their rendered markup.
  return mount(AppearanceSection)
}

beforeEach(() => {
  localStorage.clear()
  for (const key of STORAGE_KEYS) localStorage.removeItem(key)
  __resetPanelStylesForTests()
  __resetThemeForTests()
})

describe('AppearanceSection background sliders', () => {
  it('ranges opacity over the stored 0–100 percent domain', async () => {
    const wrapper = mountSection()
    await wrapper.findAll('.bg-type-option')[1]!.trigger('click') // image

    const opacity = wrapper.find('#bg-opacity').element as HTMLInputElement
    expect(opacity.min).toBe('0')
    expect(opacity.max).toBe('100')
    // Default is 100% and must be representable by the control.
    expect(opacity.value).toBe('100')
  })

  it('ranges blur over the 0–20 domain its clamp enforces', async () => {
    const wrapper = mountSection()
    await wrapper.findAll('.bg-type-option')[1]!.trigger('click')

    const blur = wrapper.find('#bg-blur').element as HTMLInputElement
    expect(blur.min).toBe('0')
    expect(blur.max).toBe('20')
  })

  it('writes a percent value when the opacity slider moves', async () => {
    const wrapper = mountSection()
    await wrapper.findAll('.bg-type-option')[1]!.trigger('click')

    const opacity = wrapper.find('#bg-opacity')
    await opacity.setValue(50)

    // Stored percent (not 0.5) — App.vue divides by 100 at the CSS boundary.
    expect(JSON.parse(localStorage.getItem('tinadec-background')!).opacity).toBe(50)
    expect(wrapper.find('#bg-opacity + .param-value').text()).toBe('50%')
  })
})

describe('AppearanceSection background source', () => {
  it('shows HTML guidance for the HTML type instead of video formats', async () => {
    const wrapper = mountSection()
    await wrapper.findAll('.bg-type-option')[3]!.trigger('click') // html

    expect(wrapper.find('.bg-format-hint').text()).toBe('settings.bgHtmlHint')
    expect(wrapper.find('.bg-html-input').exists()).toBe(true)
  })

  it('uses a single-line input for image and video sources', async () => {
    const wrapper = mountSection()
    await wrapper.findAll('.bg-type-option')[1]!.trigger('click')
    expect(wrapper.find('.bg-source-input').exists()).toBe(true)
    expect(wrapper.find('.bg-html-input').exists()).toBe(false)
  })
})

describe('AppearanceSection custom accent picker', () => {
  it('opens with the stored custom hex and commits it exactly', async () => {
    localStorage.setItem('tinadec-custom-accent', '#123456')
    const wrapper = mountSection()

    await wrapper.find('[data-testid="accent-custom"]').trigger('click')
    const hexField = wrapper.find('.custom-accent-hexfield')
    expect((hexField.element as HTMLInputElement).value).toBe('#123456')

    await hexField.setValue('#abcdef')
    const applyButton = wrapper.findAll('.custom-accent-actions button').at(-1)!
    await applyButton.trigger('click')

    expect(localStorage.getItem('tinadec-custom-accent')).toBe('#abcdef')
    expect(localStorage.getItem('tinadec-accent-color')).toBe('custom')
  })

  it('warns when the drafted color has low contrast', async () => {
    const wrapper = mountSection()
    await wrapper.find('[data-testid="accent-custom"]').trigger('click')

    await wrapper.find('.custom-accent-hexfield').setValue('#0b0b0b')
    expect(wrapper.find('[data-testid="contrast-warning"]').exists()).toBe(true)

    await wrapper.find('.custom-accent-hexfield').setValue('#ffffff')
    expect(wrapper.find('[data-testid="contrast-warning"]').exists()).toBe(false)
  })

  it('ignores an invalid hex instead of committing garbage', async () => {
    localStorage.setItem('tinadec-custom-accent', '#123456')
    const wrapper = mountSection()
    await wrapper.find('[data-testid="accent-custom"]').trigger('click')

    await wrapper.find('.custom-accent-hexfield').setValue('not-a-color')
    const applyButton = wrapper.findAll('.custom-accent-actions button').at(-1)!
    await applyButton.trigger('click')

    // Falls back to the slider-derived color, never the invalid string.
    expect(localStorage.getItem('tinadec-custom-accent')).toMatch(/^#[0-9a-f]{6}$/)
  })
})

describe('AppearanceSection accent swatches', () => {
  it('renders one swatch per preset plus the custom entry, with no text labels', () => {
    const wrapper = mountSection()
    const swatches = wrapper.findAll('.accent-color-swatch')
    // 8 presets + 1 custom entry.
    expect(swatches).toHaveLength(9)
    // Labels were removed in favour of title/aria-label to keep the row compact.
    expect(wrapper.find('.accent-color-label').exists()).toBe(false)
    expect(swatches[0]!.attributes('title')).toBe('accentColors.blue')
  })
})
