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
import { __resetDynamicPaletteForTests } from '@/composables/useDynamicPalette'

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
  __resetDynamicPaletteForTests()
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
  it('gives every picker control an accessible name', async () => {
    const wrapper = mountSection()
    await wrapper.find('[data-testid="accent-custom"]').trigger('click')

    // The hex field has no visible <label>, so it needs its own name or screen
    // readers announce an unlabelled edit box.
    const hexField = wrapper.find('.custom-accent-hexfield')
    expect(hexField.attributes('aria-label')).toBe('settings.accentHexLabel')
    // The hue slider is labelled by its visible <label for=…>.
    expect(wrapper.find('label[for="ca-hue"]').exists()).toBe(true)
    // The 2D field is a single composite control: it must expose a name and the
    // saturation/lightness pair as its spoken value.
    const field = wrapper.find('[role="slider"]')
    expect(field.attributes('aria-label')).toBe('settings.accentCustom')
    expect(field.attributes('aria-valuetext')).toContain('settings.saturation')
    expect(field.attributes('aria-valuetext')).toContain('settings.lightness')
  })

  it('drives saturation and lightness from a pointer press on the field', async () => {
    const wrapper = mountSection()
    await wrapper.find('[data-testid="accent-custom"]').trigger('click')

    const field = wrapper.find('[role="slider"]')
    const element = field.element as HTMLElement
    // happy-dom reports a zero rect, so stub the geometry the handler reads.
    element.getBoundingClientRect = () =>
      ({ left: 0, top: 0, width: 200, height: 100, right: 200, bottom: 100, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect

    // Bottom-right corner → full saturation, zero lightness.
    await field.trigger('pointerdown', { button: 0, clientX: 200, clientY: 100, pointerId: 1 })
    expect(field.attributes('aria-valuenow')).toBe('100')
    expect(field.attributes('aria-valuetext')).toContain('settings.lightness 0%')

    // Top-left corner → zero saturation, full lightness.
    await field.trigger('pointerdown', { button: 0, clientX: 0, clientY: 0, pointerId: 2 })
    expect(field.attributes('aria-valuenow')).toBe('0')
    expect(field.attributes('aria-valuetext')).toContain('settings.lightness 100%')
  })

  it('supports keyboard adjustment of the field', async () => {
    const wrapper = mountSection()
    await wrapper.find('[data-testid="accent-custom"]').trigger('click')

    const field = wrapper.find('[role="slider"]')
    const before = Number(field.attributes('aria-valuenow'))
    await field.trigger('keydown', { key: 'ArrowRight' })
    expect(Number(field.attributes('aria-valuenow'))).toBe(Math.min(100, before + 1))

    await field.trigger('keydown', { key: 'Home' })
    expect(field.attributes('aria-valuenow')).toBe('0')
  })

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

describe('AppearanceSection custom swatch states', () => {
  function customDot(wrapper: ReturnType<typeof mountSection>) {
    return wrapper.find('[data-testid="accent-custom"] .accent-color-dot')
  }

  it('shows the hue wheel and a bold plus while no custom color is active', () => {
    const wrapper = mountSection()
    // Nothing stored → the swatch must advertise "pick anything", not a color.
    expect(customDot(wrapper).classes()).toContain('accent-color-dot--spectrum')
    const plus = wrapper.find('[data-testid="accent-custom"] .custom-accent-plus')
    expect(plus.exists()).toBe(true)
    // Enlarged + thickened so it reads against the saturated wheel.
    expect(plus.attributes('width')).toBe('16')
    expect(plus.attributes('stroke-width')).toBe('3')
  })

  it('replaces the wheel with the committed color once custom is selected', async () => {
    localStorage.setItem('tinadec-custom-accent', '#123456')
    const wrapper = mountSection()
    await wrapper.find('[data-testid="accent-custom"]').trigger('click')
    await wrapper.findAll('.custom-accent-actions button').at(-1)!.trigger('click')

    expect(localStorage.getItem('tinadec-accent-color')).toBe('custom')
    expect(customDot(wrapper).classes()).not.toContain('accent-color-dot--spectrum')
    // The dot still carries the chosen color through --swatch-color.
    expect(wrapper.find('[data-testid="accent-custom"]').attributes('style'))
      .toContain('#123456')
    expect(wrapper.find('[data-testid="accent-custom"] .custom-accent-plus').exists()).toBe(false)
  })

  it('returns to the wheel when a preset is picked afterwards', async () => {
    localStorage.setItem('tinadec-accent-color', 'custom')
    const wrapper = mountSection()
    expect(customDot(wrapper).classes()).not.toContain('accent-color-dot--spectrum')

    await wrapper.findAll('.accent-color-swatch')[0]!.trigger('click')
    expect(customDot(wrapper).classes()).toContain('accent-color-dot--spectrum')
  })
})

describe('AppearanceSection material section', () => {
  it('exposes exactly one reset affordance — the control header button', () => {
    const wrapper = mountSection()
    // The duplicate "重置材质样式" button next to the control is gone; the
    // control's own header reset already restores the same defaults.
    expect(wrapper.find('.panel-styles-reset').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('settings.resetPanelStyles')
    expect(wrapper.findAll('.control-reset')).toHaveLength(1)
  })

  it('lets the control fill the section width now that nothing shares its row', () => {
    const wrapper = mountSection()
    // No grid wrapper remains between the group body and the control, so the
    // control stretches to the section's full width instead of a 1fr track.
    const control = wrapper.find('.panel-style-control')
    expect(control.exists()).toBe(true)
    expect(control.element.parentElement?.classList.contains('panel-styles-grid')).toBe(false)
  })
})

describe('AppearanceSection follow-background option', () => {
  it('shows a single extracted swatch, not a role strip', async () => {
    localStorage.setItem(
      'tinadec-dynamic-palette',
      JSON.stringify({
        source: 'file:///wall.jpg',
        sourceColor: 0xff2ec4b6,
        dark: { '--accent-primary': '#7fd9cf' },
        light: { '--accent-primary': '#00695f' },
      }),
    )
    __resetDynamicPaletteForTests()
    const wrapper = mountSection()

    const option = wrapper.find('[data-testid="accent-dynamic"]')
    expect(option.exists()).toBe(true)
    // Exactly one swatch; the previous five-circle role strip must be gone.
    expect(option.findAll('.accent-dynamic-swatch')).toHaveLength(1)
    expect(option.find('.accent-dynamic-circle').exists()).toBe(false)
    expect(wrapper.findAll('.accent-dynamic-circle')).toHaveLength(0)
  })

  it('paints that swatch with the accent the palette applies', async () => {
    localStorage.setItem(
      'tinadec-dynamic-palette',
      JSON.stringify({
        source: 'file:///wall.jpg',
        sourceColor: 0xff2ec4b6,
        dark: { '--accent-primary': '#7fd9cf' },
        light: { '--accent-primary': '#00695f' },
      }),
    )
    __resetDynamicPaletteForTests()
    const wrapper = mountSection()

    const swatch = wrapper.find('[data-testid="accent-dynamic"] .accent-dynamic-swatch')
    // The swatch takes the dark accent — the same role the preset swatches show.
    expect(swatch.attributes('style')!.replace(/\s/g, '').toLowerCase()).toContain('#7fd9cf')
  })

  it('sits inside the preset swatch row so it right-aligns with them', () => {
    const wrapper = mountSection()
    const grid = wrapper.find('[data-testid="accent-colors"]')
    // Direct child of the row (not a block below it) is what lets
    // `margin-left: auto` push it to the row's right edge.
    expect(grid.element.querySelector(':scope > [data-testid="accent-dynamic"]')).not.toBeNull()
  })
})
