// @vitest-environment happy-dom
import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { compileTemplate, parse } from '@vue/compiler-sfc'
import * as Vue from 'vue'
import { createApp, defineComponent, h, nextTick, ref } from 'vue'
import { describe, expect, it, vi } from 'vitest'

const here = path.dirname(fileURLToPath(import.meta.url))
const tabBarPath = path.resolve(here, 'BrowserTabBar.vue')
const stylesPath = path.resolve(here, '../../../desktop/src/styles.css')

const tabBarSource = readFileSync(tabBarPath, 'utf8')
const stylesSource = readFileSync(stylesPath, 'utf8')
const parsedSfc = parse(tabBarSource, { filename: 'BrowserTabBar.vue' })

const PassthroughSlot = defineComponent({
  name: 'PassthroughSlot',
  setup(_, { slots }) {
    return () => slots.default?.()
  },
})

const IconStub = defineComponent({
  name: 'IconStub',
  setup() {
    return () => h('svg', { 'aria-hidden': 'true' })
  },
})

function compileTabBarTemplateRender() {
  const templateContent = parsedSfc.descriptor.template?.content
  if (!templateContent) {
    throw new Error('BrowserTabBar.vue template block missing')
  }
  const compiled = compileTemplate({
    source: templateContent,
    filename: 'BrowserTabBar.vue',
    id: 'browser-tab-bar-test',
    compilerOptions: {
      mode: 'function',
    },
  })
  expect(compiled.errors).toEqual([])
  // Compile Vue's function-mode render output against the imported Vue runtime.
  const factory = new Function('Vue', compiled.code) as (vueRuntime: typeof Vue) => Vue.RenderFunction
  return factory(Vue)
}

function extractCssRule(css: string, selector: string): string {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const re = new RegExp(`${escaped}\\s*\\{([^}]*)\\}`)
  const match = css.match(re)
  if (!match) {
    throw new Error(`CSS rule not found for selector: ${selector}`)
  }
  return match[1]
}

describe('BrowserTabBar structure, scroll isolation, and glass-safe styles', () => {
  it('splits into .browser-tab-strip + pinned .browser-tab-actions under 15+ tabs without scrolling actions', async () => {
    const render = compileTabBarTemplateRender()
    const homeInstance = { id: 'tab-home', descriptorId: 'homePicker', title: 'Home' }
    const featureInstances = Array.from({ length: 15 }, (_, idx) => ({
      id: `tab-feature-${idx + 1}`,
      descriptorId: 'terminal',
      title: `Feature ${idx + 1}`,
    }))
    const detachedTabs = [
      { tabId: 'det-1', windowId: 101, type: 'browser', title: 'Detached 1' },
      { tabId: 'det-2', windowId: 102, type: 'git', title: 'Detached 2' },
    ]

    const tabStripRef = ref<HTMLElement | null>(null)
    const addButtonRef = ref<HTMLElement | null>(null)
    const canScrollLeft = ref(false)
    const canScrollRight = ref(true)
    const menuOpen = ref(false)
    const activeTabId = ref(featureInstances[14]!.id)
    const onStripWheel = vi.fn()
    const updateOverflowState = vi.fn()
    const onTabBarDblClick = vi.fn()

    const Host = defineComponent({
      name: 'BrowserTabBarTemplateHost',
      components: {
        HomeIcon: IconStub,
        ExternalLink: IconStub,
        X: IconStub,
        Plus: IconStub,
        PanelsTopLeft: IconStub,
        Minimize2: IconStub,
        Maximize2: IconStub,
        PanelRightClose: IconStub,
        TooltipProvider: PassthroughSlot,
        TooltipRoot: PassthroughSlot,
        TooltipTrigger: PassthroughSlot,
        TooltipPortal: PassthroughSlot,
        TooltipContent: PassthroughSlot,
        MotionTransition: PassthroughSlot,
      },
      setup() {
        return {
          tabStripRef,
          addButtonRef,
          canScrollLeft,
          canScrollRight,
          menuOpen,
          activeTabId,
          homeInstance,
          featureInstances,
          detachedTabs,
          canRestore: true,
          showMaximizeAction: true,
          maximized: false,
          maximizeLabel: 'Maximize panel',
          collapseLabel: 'Collapse panel',
          menuStyle: {},
          menuFeatures: [],
          t: (key: string) => key,
          cardTitle: (inst: { title?: string; descriptorId: string }) => inst.title ?? inst.descriptorId,
          iconFor: () => IconStub,
          dockDrag: { isDraggingTab: () => false },
          emit: vi.fn(),
          onTabBarDblClick,
          onStripWheel,
          updateOverflowState,
          onHomeAuxClick: vi.fn(),
          onTabClick: vi.fn(),
          onTabMouseDown: vi.fn(),
          onTabAuxClick: vi.fn(),
          onTabContextMenu: vi.fn(),
          toggleMenu: vi.fn(),
          onSelect: vi.fn(),
        }
      },
      render,
    })

    const container = document.createElement('div')
    document.body.appendChild(container)
    const app = createApp(Host)
    app.mount(container)
    await nextTick()

    try {
      const tabBar = container.querySelector<HTMLElement>('.browser-tab-bar')
      expect(tabBar).not.toBeNull()

      const directChildren = Array.from(tabBar!.children)
      expect(directChildren.map((el) => el.className.split(' ')[0])).toEqual([
        'browser-tab-strip',
        'browser-tab-actions',
      ])

      const tabStrip = tabBar!.querySelector<HTMLElement>('.browser-tab-strip')!
      const tabActions = tabBar!.querySelector<HTMLElement>('.browser-tab-actions')!

      // All tabs (1 pinned Home + 15 feature + 2 detached = 18) live inside .browser-tab-strip.
      expect(tabStrip.querySelectorAll('.browser-tab-home')).toHaveLength(1)
      expect(tabStrip.querySelectorAll('.browser-tab')).toHaveLength(18)
      expect(tabStrip.querySelectorAll('.browser-tab-detached')).toHaveLength(2)

      // Pinned action controls live strictly inside .browser-tab-actions, never inside .browser-tab-strip.
      expect(tabStrip.querySelector('.browser-tab-add')).toBeNull()
      expect(tabStrip.querySelector('[data-testid="feature-panel-maximize-btn"]')).toBeNull()
      expect(tabStrip.querySelector('.browser-tab-collapse')).toBeNull()

      const actionButtons = Array.from(tabActions.querySelectorAll<HTMLButtonElement>('button'))
      expect(actionButtons).toHaveLength(4)
      expect(actionButtons[0]!.classList.contains('browser-tab-add')).toBe(true)
      expect(actionButtons[1]!.getAttribute('aria-label')).toBe('context.restoreDock')
      expect(actionButtons[2]!.getAttribute('data-testid')).toBe('feature-panel-maximize-btn')
      expect(actionButtons[3]!.getAttribute('aria-label')).toBe('Collapse panel')

      // Scrolling the tab strip never scrolls the outer tab bar or moves pinned action buttons.
      Object.defineProperty(tabStrip, 'scrollWidth', { value: 960, configurable: true })
      Object.defineProperty(tabStrip, 'clientWidth', { value: 320, configurable: true })
      tabStrip.scrollLeft = 240
      tabStrip.dispatchEvent(new Event('scroll'))
      await nextTick()

      expect(tabStrip.scrollLeft).toBe(240)
      expect(tabBar!.scrollLeft).toBe(0)
      expect(tabActions.scrollLeft).toBe(0)
      expect(tabStrip.contains(tabActions)).toBe(false)
      expect(updateOverflowState).toHaveBeenCalled()

      // Dynamic overflow edge fade classes bind to .browser-tab-strip only.
      expect(tabStrip.classList.contains('has-overflow-right')).toBe(true)
      expect(tabStrip.classList.contains('has-overflow-left')).toBe(false)
      canScrollLeft.value = true
      await nextTick()
      expect(tabStrip.classList.contains('has-overflow-left')).toBe(true)
      expect(tabStrip.classList.contains('has-overflow-right')).toBe(true)

      // Wheel listener is attached to .browser-tab-strip.
      tabStrip.dispatchEvent(new WheelEvent('wheel', { deltaY: 120, cancelable: true }))
      expect(onStripWheel).toHaveBeenCalledTimes(1)

      // Double-click on the tab bar still reaches onTabBarDblClick.
      tabBar!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))
      expect(onTabBarDblClick).toHaveBeenCalledTimes(1)
    } finally {
      app.unmount()
      container.remove()
    }
  })

  it('wires wheel vertical-to-horizontal scroll, smooth active-tab scrollIntoView, and ResizeObserver overflow checks in script setup', () => {
    const script = parsedSfc.descriptor.scriptSetup?.content ?? ''
    expect(script).toContain('function onStripWheel(event: WheelEvent)')
    expect(script).toContain('if (el.scrollWidth <= el.clientWidth) return')
    expect(script).toContain('event.preventDefault()')
    expect(script).toContain('el.scrollLeft = next')

    expect(script).toContain('function scrollActiveTabIntoView()')
    expect(script).toContain("inline: 'nearest'")
    expect(script).toContain("block: 'nearest'")
    expect(script).toContain("behavior: reducedMotion ? 'auto' : 'smooth'")
    expect(script).toContain('() => props.activeTabId')

    expect(script).toContain('function updateOverflowState()')
    expect(script).toContain('new ResizeObserver')
    expect(script).toContain('stripResizeObserver?.disconnect()')
  })

  it('removes scrollbar-width: thin and configures overflow-hidden bar, scrollbar-free strip, glass-safe mask-image, and pinned actions in styles.css', () => {
    const tabBarSectionStart = stylesSource.indexOf('/* ===== Browser-style floating tab bar ===== */')
    const previewSectionStart = stylesSource.indexOf('/* ===== Responsive: Preview browser toolbar ===== */')
    expect(tabBarSectionStart).toBeGreaterThan(-1)
    expect(previewSectionStart).toBeGreaterThan(tabBarSectionStart)

    const tabBarCss = stylesSource.slice(tabBarSectionStart, previewSectionStart)

    // Zero thin scrollbar anywhere in .browser-tab-* styling.
    expect(tabBarCss).not.toMatch(/scrollbar-width\s*:\s*thin/)

    // Outer .browser-tab-bar never scrolls.
    const barRule = extractCssRule(tabBarCss, '.browser-tab-bar')
    expect(barRule).toMatch(/display\s*:\s*flex/)
    expect(barRule).toMatch(/align-items\s*:\s*center/)
    expect(barRule).toMatch(/overflow\s*:\s*hidden/)
    expect(barRule).not.toMatch(/overflow-x\s*:\s*auto/)

    // Scrollable .browser-tab-strip hides all native scrollbars across Chromium/Firefox.
    const stripRule = extractCssRule(tabBarCss, '.browser-tab-strip')
    expect(stripRule).toMatch(/flex\s*:\s*1\s+1\s+auto/)
    expect(stripRule).toMatch(/min-width\s*:\s*0/)
    expect(stripRule).toMatch(/overflow-x\s*:\s*auto/)
    expect(stripRule).toMatch(/overflow-y\s*:\s*hidden/)
    expect(stripRule).toMatch(/scrollbar-width\s*:\s*none/)
    expect(stripRule).not.toMatch(/transform\s*:/)

    const stripWebkitScrollbar = extractCssRule(tabBarCss, '.browser-tab-strip::-webkit-scrollbar')
    expect(stripWebkitScrollbar).toMatch(/display\s*:\s*none/)
    expect(stripWebkitScrollbar).toMatch(/width\s*:\s*0/)
    expect(stripWebkitScrollbar).toMatch(/height\s*:\s*0/)

    // Glass-safe edge fade uses mask-image (never transform or solid background overlay).
    const fadeRight = extractCssRule(tabBarCss, '.browser-tab-strip.has-overflow-right')
    const fadeLeft = extractCssRule(tabBarCss, '.browser-tab-strip.has-overflow-left')
    const fadeBoth = extractCssRule(tabBarCss, '.browser-tab-strip.has-overflow-left.has-overflow-right')
    for (const rule of [fadeRight, fadeLeft, fadeBoth]) {
      expect(rule).toMatch(/mask-image\s*:\s*linear-gradient/)
      expect(rule).toMatch(/-webkit-mask-image\s*:\s*linear-gradient/)
      expect(rule).not.toMatch(/transform\s*:/)
      expect(rule).not.toMatch(/background\s*:/)
    }

    // Pinned right-side .browser-tab-actions never shrinks and resets child collapse margin.
    const actionsRule = extractCssRule(tabBarCss, '.browser-tab-actions')
    expect(actionsRule).toMatch(/flex-shrink\s*:\s*0/)
    expect(actionsRule).toMatch(/display\s*:\s*flex/)
    expect(actionsRule).toMatch(/align-items\s*:\s*center/)
    expect(actionsRule).toMatch(/margin-left\s*:\s*auto/)

    const actionsCollapseRule = extractCssRule(tabBarCss, '.browser-tab-actions .browser-tab-collapse')
    expect(actionsCollapseRule).toMatch(/margin-left\s*:\s*0/)

    // Ultra-compact mode must keep the pinned '+' button visible instead of hiding it.
    expect(tabBarCss).not.toMatch(/\.uie-stack\.mode-ultra\s+\.browser-tab-add\s*\{\s*display\s*:\s*none/)
  })
})
