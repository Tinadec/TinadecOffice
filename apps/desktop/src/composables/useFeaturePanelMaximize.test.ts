// @vitest-environment happy-dom
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'

vi.mock('@/components/chat/FloatingChatPip.vue', () => ({
  default: defineComponent({
    name: 'FloatingChatPipStub',
    props: {
      bounds: {
        type: Object,
        required: true,
      },
    },
    setup(props) {
      return () =>
        h('div', {
          'data-testid': 'floating-chat-pip-stub',
          'data-bounds-x': String((props.bounds as { x: number }).x),
          'data-bounds-y': String((props.bounds as { y: number }).y),
          'data-bounds-width': String((props.bounds as { width: number }).width),
          'data-bounds-height': String((props.bounds as { height: number }).height),
        })
    },
  }),
}))

import {
  UieCanvas,
  __resetUieForTests,
  createCardRegistry,
  initUie,
  type UieStore,
} from '@tinadec/ui'
import { homeController } from '@/controllers/HomeController'
import {
  DEFAULT_DOCK_SIDE,
  DEFAULT_PIP_SIZE,
  FLOATING_CHAT_PIP_STORAGE_KEY,
  MAX_PIP_HEIGHT_PCT,
  MAX_PIP_WIDTH,
  MIN_PIP_HEIGHT_PCT,
  MIN_PIP_WIDTH,
  NEW_CONVERSATION_EVENT,
  __resetFeaturePanelMaximizeForTests,
  useFeaturePanelMaximize,
} from './useFeaturePanelMaximize'

const StubCard = defineComponent({
  name: 'StubCard',
  setup() {
    return () => h('div', { class: 'stub-card' })
  },
})

function makeTestUie(): UieStore {
  const registry = createCardRegistry()
  registry.register({
    type: 'nav',
    component: StubCard,
    minWidth: 200,
    minHeight: 200,
    singleton: true,
    movable: false,
    closable: false,
    detachable: false,
    defaultTitle: 'Nav',
  })
  registry.register({
    type: 'chat',
    component: StubCard,
    minWidth: 320,
    minHeight: 200,
    singleton: true,
    movable: false,
    closable: false,
    detachable: false,
    defaultTitle: 'Chat',
  })
  registry.register({
    type: 'homePicker',
    component: StubCard,
    minWidth: 280,
    minHeight: 200,
    singleton: true,
    movable: false,
    closable: false,
    detachable: false,
    defaultTitle: 'Home',
  })
  registry.register({
    type: 'git',
    component: StubCard,
    minWidth: 280,
    minHeight: 200,
    singleton: true,
    movable: true,
    closable: true,
    detachable: true,
    defaultTitle: 'Git',
  })
  registry.register({
    type: 'globalBoard',
    component: StubCard,
    minWidth: 320,
    minHeight: 200,
    singleton: true,
    movable: false,
    closable: false,
    detachable: false,
    defaultTitle: 'Board',
    titlebarMode: 'hidden',
  })
  registry.register({
    type: 'boardTaskInspector',
    component: StubCard,
    minWidth: 340,
    minHeight: 220,
    singleton: true,
    movable: true,
    closable: true,
    detachable: true,
    defaultTitle: 'Inspector',
  })
  const uie = initUie({
    registry,
    componentFor: () => StubCard,
  })
  uie.setContainerSize({ width: 1440, height: 900 })
  return uie
}

function makeTestI18n() {
  return createI18n({
    legacy: false,
    locale: 'zh-CN',
    messages: {
      'zh-CN': {
        app: {
          close: '关闭',
          collapse: '收起',
          expand: '展开',
        },
        context: {
          homeTitle: '首页',
          homeTabLabel: '首页',
          homeNewTab: '新标签页',
          collapsePanel: '收起面板',
          maximizePanel: '最大化面板',
          restorePanel: '还原三栏布局',
          restoreDock: '合并全部窗格',
          mergePanel: '合并此窗格到主面板',
          detachTab: '拆卸为独立窗口',
        },
      },
    },
  })
}

describe('useFeaturePanelMaximize', () => {
  beforeEach(() => {
    localStorage.clear()
    homeController.selectedSessionId.value = null
    __resetUieForTests()
    __resetFeaturePanelMaximizeForTests()
  })

  afterEach(() => {
    __resetFeaturePanelMaximizeForTests()
    __resetUieForTests()
    localStorage.clear()
  })

  it('starts unmaximized with default dockSide="right" and pipSize={420, 62}', () => {
    const state = useFeaturePanelMaximize()
    expect(state.maximized.value).toBe(false)
    expect(state.pipOpen.value).toBe(false)
    expect(state.dockSide.value).toBe(DEFAULT_DOCK_SIDE)
    expect(state.pipSize.value).toEqual(DEFAULT_PIP_SIZE)
  })

  it('forces pipOpen=false on EVERY enterMaximize() even when PiP was left open before exit', () => {
    const state = useFeaturePanelMaximize()

    state.enterMaximize()
    expect(state.maximized.value).toBe(true)
    expect(state.pipOpen.value).toBe(false)

    state.openPip()
    expect(state.pipOpen.value).toBe(true)

    state.exitMaximize()
    expect(state.maximized.value).toBe(false)

    // Re-entering maximize MUST reset pipOpen back to false (48px orb first).
    state.enterMaximize()
    expect(state.maximized.value).toBe(true)
    expect(state.pipOpen.value).toBe(false)

    // Calling enterMaximize() directly when pipOpen was mutated to true also resets it.
    state.openPip()
    expect(state.pipOpen.value).toBe(true)
    state.enterMaximize()
    expect(state.pipOpen.value).toBe(false)
  })

  it('never mutates persisted UieLayoutSnapshot widths, dock tree, or revision', () => {
    const uie = makeTestUie()
    const beforeRevision = uie.snapshot.value.revision
    const beforeRightWidth = uie.snapshot.value.columns.right.width
    const beforeLeftWidth = uie.snapshot.value.columns.left.width
    const beforeSnapshotJson = JSON.stringify(uie.snapshot.value)

    const state = useFeaturePanelMaximize()
    state.enterMaximize()
    expect(state.maximized.value).toBe(true)
    expect(uie.snapshot.value.revision).toBe(beforeRevision)
    expect(uie.snapshot.value.columns.right.width).toBe(beforeRightWidth)
    expect(uie.snapshot.value.columns.left.width).toBe(beforeLeftWidth)
    expect(JSON.stringify(uie.snapshot.value)).toBe(beforeSnapshotJson)

    state.exitMaximize()
    expect(JSON.stringify(uie.snapshot.value)).toBe(beforeSnapshotJson)
  })

  it('persists dockSide and clamped pipSize in localStorage["tinadec:floating-chat-pip"] and hydrates on reload', () => {
    const state = useFeaturePanelMaximize()

    state.setDockSide('left')
    state.setPipSize({ width: 9999, heightPct: 99 })

    expect(state.dockSide.value).toBe('left')
    expect(state.pipSize.value).toEqual({
      width: MAX_PIP_WIDTH,
      heightPct: MAX_PIP_HEIGHT_PCT,
    })

    const raw = localStorage.getItem(FLOATING_CHAT_PIP_STORAGE_KEY)
    expect(raw).toBeTruthy()
    expect(JSON.parse(raw!)).toEqual({
      dockSide: 'left',
      pipSize: {
        width: MAX_PIP_WIDTH,
        heightPct: MAX_PIP_HEIGHT_PCT,
      },
    })

    state.setPipSize({ width: 120, heightPct: 5 })
    expect(state.pipSize.value).toEqual({
      width: MIN_PIP_WIDTH,
      heightPct: MIN_PIP_HEIGHT_PCT,
    })

    state.enterMaximize()
    state.openPip()

    // Simulate renderer reload without clearing localStorage:
    // dockSide & pipSize hydrate from storage, while maximized & pipOpen reset to false.
    __resetFeaturePanelMaximizeForTests()
    const reloaded = useFeaturePanelMaximize()
    expect(reloaded.maximized.value).toBe(false)
    expect(reloaded.pipOpen.value).toBe(false)
    expect(reloaded.dockSide.value).toBe('left')
    expect(reloaded.pipSize.value).toEqual({
      width: MIN_PIP_WIDTH,
      heightPct: MIN_PIP_HEIGHT_PCT,
    })
  })

  it('auto-exits maximize immediately when homeController.selectedSessionId changes', () => {
    homeController.selectedSessionId.value = 'session-a'
    const state = useFeaturePanelMaximize()

    state.enterMaximize()
    expect(state.maximized.value).toBe(true)

    // Switch to another session -> immediate auto-exit.
    homeController.selectedSessionId.value = 'session-b'
    expect(state.maximized.value).toBe(false)

    // Re-enter and start new conversation (session -> null) -> immediate auto-exit.
    state.enterMaximize()
    expect(state.maximized.value).toBe(true)
    homeController.selectedSessionId.value = null
    expect(state.maximized.value).toBe(false)
  })

  it('auto-exits maximize on "tinadec:new-conversation" and notifyNewConversation() even when already on an empty session', () => {
    homeController.selectedSessionId.value = null
    const state = useFeaturePanelMaximize()

    state.enterMaximize()
    expect(state.maximized.value).toBe(true)

    window.dispatchEvent(new CustomEvent(NEW_CONVERSATION_EVENT))
    expect(state.maximized.value).toBe(false)

    state.enterMaximize()
    expect(state.maximized.value).toBe(true)
    state.notifyNewConversation()
    expect(state.maximized.value).toBe(false)
  })

  it('auto-exits maximize when right column collapses or UIE pageId leaves home', () => {
    const uie = makeTestUie()
    const state = useFeaturePanelMaximize()

    state.enterMaximize()
    expect(state.maximized.value).toBe(true)

    // Collapsing the right feature panel auto-exits maximize.
    uie.dispatch({
      command: {
        type: 'collapseColumn',
        scope: uie.scope.value,
        slotId: 'right',
        collapsed: true,
      },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
    expect(state.maximized.value).toBe(false)

    // Uncollapse right column, re-enter maximize, then navigate away from home -> auto-exit.
    uie.dispatch({
      command: {
        type: 'collapseColumn',
        scope: uie.scope.value,
        slotId: 'right',
        collapsed: false,
      },
      source: 'user',
      expectedRevision: uie.snapshot.value.revision,
    })
    state.enterMaximize()
    expect(state.maximized.value).toBe(true)

    uie.showPage('board')
    expect(state.maximized.value).toBe(false)
  })

  it('toggles maximize with Ctrl/Cmd+Shift+M on Home without breaking textarea focus', () => {
    const uie = makeTestUie()
    const state = useFeaturePanelMaximize()

    const textarea = document.createElement('textarea')
    document.body.appendChild(textarea)
    textarea.focus()

    try {
      textarea.dispatchEvent(
        new KeyboardEvent('keydown', {
          key: 'M',
          code: 'KeyM',
          ctrlKey: true,
          shiftKey: true,
          bubbles: true,
          cancelable: true,
        }),
      )
      expect(state.maximized.value).toBe(true)
      expect(document.activeElement).toBe(textarea)

      window.dispatchEvent(
        new KeyboardEvent('keydown', {
          key: 'm',
          code: 'KeyM',
          metaKey: true,
          shiftKey: true,
          bubbles: true,
          cancelable: true,
        }),
      )
      expect(state.maximized.value).toBe(false)

      // Shortcut does nothing when away from Home.
      uie.showPage('board')
      window.dispatchEvent(
        new KeyboardEvent('keydown', {
          key: 'M',
          code: 'KeyM',
          ctrlKey: true,
          shiftKey: true,
          bubbles: true,
          cancelable: true,
        }),
      )
      expect(state.maximized.value).toBe(false)
    } finally {
      textarea.remove()
    }
  })

  it('expands UieCanvas 3-col <-> 2-col non-destructively and bounds FloatingChatPip strictly to maximizedRect', async () => {
    const uie = makeTestUie()
    const state = useFeaturePanelMaximize()
    const i18n = makeTestI18n()

    const wrapper = mount(UieCanvas, {
      global: {
        plugins: [i18n],
      },
    })

    // UieCanvas.measure() in jsdom reads 0x0 from getBoundingClientRect; set realistic viewport size.
    uie.setContainerSize({ width: 1440, height: 900 })
    await nextTick()

    const baseLeftGeom = { ...uie.geometry.value.columns.left }
    const baseCenterGeom = { ...uie.geometry.value.columns.center }
    const baseRightGeom = { ...uie.geometry.value.columns.right }
    const gap = uie.snapshot.value.gap

    const maxBtn = wrapper.find('[data-testid="feature-panel-maximize-btn"]')
    expect(maxBtn.exists()).toBe(true)
    expect(maxBtn.attributes('aria-pressed')).toBe('false')
    expect(wrapper.find('[data-testid="floating-chat-pip-stub"]').exists()).toBe(false)
    expect(wrapper.find('.uie-slot-right .uie-column-resizer').exists()).toBe(true)

    // Click maximize button -> smooth 3-col -> 2-col expansion.
    await maxBtn.trigger('click')
    await nextTick()

    expect(state.maximized.value).toBe(true)
    expect(maxBtn.attributes('aria-pressed')).toBe('true')

    const expectedMaxWidth = baseCenterGeom.width + gap + baseRightGeom.width
    const rightColEl = wrapper.find('.uie-slot-right')
    const centerColEl = wrapper.find('.uie-slot-center')
    const leftColEl = wrapper.find('.uie-slot-left')

    expect(rightColEl.attributes('style')).toContain(`left: ${baseCenterGeom.x}px`)
    expect(rightColEl.attributes('style')).toContain(`width: ${expectedMaxWidth}px`)
    expect(rightColEl.find('.uie-column-resizer').exists()).toBe(false)

    // Center chat column is hidden via opacity/pointer-events class (zero ancestor transform).
    expect(centerColEl.classes()).toContain('uie-column--maximized-hidden')
    expect(centerColEl.attributes('style') ?? '').not.toContain('transform')

    // Left nav column remains untouched.
    expect(leftColEl.attributes('style')).toContain(`left: ${baseLeftGeom.x}px`)
    expect(leftColEl.attributes('style')).toContain(`width: ${baseLeftGeom.width}px`)

    // FloatingChatPip receives exact maximizedRect bounds and NEVER overlaps left nav rail.
    const pipStub = wrapper.find('[data-testid="floating-chat-pip-stub"]')
    expect(pipStub.exists()).toBe(true)
    const pipX = Number(pipStub.attributes('data-bounds-x'))
    const pipW = Number(pipStub.attributes('data-bounds-width'))
    expect(pipX).toBe(baseCenterGeom.x)
    expect(pipX).toBeGreaterThanOrEqual(baseLeftGeom.x + baseLeftGeom.width + gap)
    expect(pipW).toBe(expectedMaxWidth)

    // Double-clicking empty tabbar background toggles back to 3-column layout.
    await wrapper.find('.uie-slot-right .browser-tab-bar').trigger('dblclick')
    await nextTick()

    expect(state.maximized.value).toBe(false)
    expect(maxBtn.attributes('aria-pressed')).toBe('false')
    expect(wrapper.find('[data-testid="floating-chat-pip-stub"]').exists()).toBe(false)
    expect(centerColEl.classes()).not.toContain('uie-column--maximized-hidden')
    expect(rightColEl.attributes('style')).toContain(`left: ${baseRightGeom.x}px`)
    expect(rightColEl.attributes('style')).toContain(`width: ${baseRightGeom.width}px`)
    expect(rightColEl.find('.uie-column-resizer').exists()).toBe(true)

    wrapper.unmount()
  })
})
