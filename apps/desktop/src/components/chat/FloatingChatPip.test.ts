// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import { defineComponent, h, nextTick, ref, type Ref } from 'vue'
import { createI18n } from 'vue-i18n'
import zhCN from '@/locales/zh-CN'

type DockSide = 'left' | 'center' | 'right'
type PipSize = { width: number; heightPct: number }

const mockMaximizeState = vi.hoisted(() => ({
  maximized: null as Ref<boolean> | null,
  pipOpen: null as Ref<boolean> | null,
  dockSide: null as Ref<DockSide> | null,
  pipSize: null as Ref<PipSize> | null,
  exitMaximize: vi.fn(),
  openPip: vi.fn(),
  minimizePip: vi.fn(),
  setDockSide: vi.fn(),
  setPipSize: vi.fn(),
}))

const mockControllerState = vi.hoisted(() => ({
  working: null as Ref<boolean> | null,
  busy: null as Ref<boolean> | null,
  activeRuns: null as Ref<Array<{ id: string; status: string }>> | null,
  approvals: null as Ref<Array<{ id: string; status: string; summary?: string }>> | null,
  messages: null as Ref<Array<{ id: string; role: string; content: string }>> | null,
  selectedSessionId: null as Ref<string | null> | null,
  currentSession: null as Ref<{ id: string; title: string } | null> | null,
}))

vi.mock('@/composables/useFeaturePanelMaximize', async () => {
  const { ref: vueRef } = await import('vue')
  const maximized = vueRef(true)
  const pipOpen = vueRef(false)
  const dockSide = vueRef<DockSide>('right')
  const pipSize = vueRef<PipSize>({ width: 420, heightPct: 62 })

  mockMaximizeState.maximized = maximized
  mockMaximizeState.pipOpen = pipOpen
  mockMaximizeState.dockSide = dockSide
  mockMaximizeState.pipSize = pipSize

  mockMaximizeState.exitMaximize.mockImplementation(() => {
    maximized.value = false
    pipOpen.value = false
  })
  mockMaximizeState.openPip.mockImplementation(() => {
    pipOpen.value = true
  })
  mockMaximizeState.minimizePip.mockImplementation(() => {
    pipOpen.value = false
  })
  mockMaximizeState.setDockSide.mockImplementation((side: DockSide) => {
    dockSide.value = side
  })
  mockMaximizeState.setPipSize.mockImplementation((size: PipSize) => {
    pipSize.value = { ...size }
  })

  return {
    useFeaturePanelMaximize: () => ({
      maximized,
      pipOpen,
      dockSide,
      pipSize,
      exitMaximize: mockMaximizeState.exitMaximize,
      openPip: mockMaximizeState.openPip,
      minimizePip: mockMaximizeState.minimizePip,
      setDockSide: mockMaximizeState.setDockSide,
      setPipSize: mockMaximizeState.setPipSize,
    }),
  }
})

vi.mock('@/controllers/HomeController', async () => {
  const { ref: vueRef } = await import('vue')
  const working = vueRef(false)
  const busy = vueRef(false)
  const activeRuns = vueRef<Array<{ id: string; status: string }>>([])
  const approvals = vueRef<Array<{ id: string; status: string; summary?: string }>>([])
  const messages = vueRef<Array<{ id: string; role: string; content: string }>>([])
  const selectedSessionId = vueRef<string | null>('sess-1')
  const currentSession = vueRef<{ id: string; title: string } | null>({ id: 'sess-1', title: '修复代码导航' })

  mockControllerState.working = working
  mockControllerState.busy = busy
  mockControllerState.activeRuns = activeRuns
  mockControllerState.approvals = approvals
  mockControllerState.messages = messages
  mockControllerState.selectedSessionId = selectedSessionId
  mockControllerState.currentSession = currentSession

  return {
    homeController: {
      working,
      busy,
      activeRuns,
      approvals,
      messages,
      selectedSessionId,
      currentSession,
    },
  }
})

vi.mock('../../../../TinadecUI/src/components/cards/home/ChatCard.vue', () => ({
  default: defineComponent({
    name: 'ChatCardStub',
    props: { compactPip: Boolean },
    setup(props) {
      return () =>
        h('div', { 'data-testid': 'chat-card-stub', 'data-compact-pip': props.compactPip ? 'true' : 'false' }, [
          h('textarea', { class: 'welcome-dialog-input', 'data-testid': 'pip-composer-textarea' }),
        ])
    },
  }),
}))

import FloatingChatPip from './FloatingChatPip.vue'

function createTestI18n() {
  return createI18n({
    legacy: false,
    locale: 'zh-CN',
    messages: { 'zh-CN': zhCN as never },
    missingWarn: false,
    fallbackWarn: false,
  })
}

function mountPip(bounds = { x: 240, y: 0, width: 1000, height: 800 }): VueWrapper {
  return mount(FloatingChatPip, {
    attachTo: document.body,
    props: { bounds },
    global: {
      plugins: [createTestI18n()],
    },
  })
}

function dispatchPointer(el: Element, type: string, init: Partial<PointerEventInit> = {}) {
  const EventCtor = typeof PointerEvent !== 'undefined' ? PointerEvent : MouseEvent
  const event = new EventCtor(type, {
    bubbles: true,
    cancelable: true,
    button: 0,
    pointerId: 1,
    clientX: 0,
    clientY: 0,
    ...init,
  } as PointerEventInit)
  el.dispatchEvent(event)
}

describe('FloatingChatPip', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockMaximizeState.maximized!.value = true
    mockMaximizeState.pipOpen!.value = false
    mockMaximizeState.dockSide!.value = 'right'
    mockMaximizeState.pipSize!.value = { width: 420, heightPct: 62 }

    mockControllerState.working!.value = false
    mockControllerState.busy!.value = false
    mockControllerState.activeRuns!.value = []
    mockControllerState.approvals!.value = []
    mockControllerState.messages!.value = []
    mockControllerState.selectedSessionId!.value = 'sess-1'
    mockControllerState.currentSession!.value = { id: 'sess-1', title: '修复代码导航' }
  })

  it('renders a 48x48 circular orb aligned inside bounds with right/center/left dock geometry and no ancestor transform', async () => {
    const wrapper = mountPip({ x: 240, y: 0, width: 1000, height: 800 })
    const layer = wrapper.get('[data-testid="floating-chat-pip-layer"]')
    expect(layer.attributes('style')).toContain('left: 240px')
    expect(layer.attributes('style')).toContain('width: 1000px')
    expect(layer.attributes('style')).not.toContain('transform')

    const orb = wrapper.get('[data-testid="floating-chat-orb"]')
    expect(orb.attributes('style')).toContain('width: 48px')
    expect(orb.attributes('style')).toContain('height: 48px')
    expect(orb.attributes('style')).toContain('bottom: 18px')
    expect(orb.attributes('style')).toContain('border-radius: 999px')
    // Right dock: 1000 - 48 - 18 = 934px
    expect(orb.attributes('style')).toContain('left: 934px')
    expect(orb.attributes('style')).not.toContain('transform')

    mockMaximizeState.dockSide!.value = 'center'
    await nextTick()
    // Center dock: (1000 - 48) / 2 = 476px
    expect(orb.attributes('style')).toContain('left: 476px')

    mockMaximizeState.dockSide!.value = 'left'
    await nextTick()
    expect(orb.attributes('style')).toContain('left: 18px')

    wrapper.unmount()
  })

  it('shows breathing pulse ring when working, shows badge for pending approvals and unread assistant replies, and clears unread on open', async () => {
    const wrapper = mountPip()

    expect(wrapper.find('[data-testid="floating-chat-orb-pulse"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="floating-chat-orb-badge"]').exists()).toBe(false)

    // Working pulse ring
    mockControllerState.working!.value = true
    await nextTick()
    expect(wrapper.find('[data-testid="floating-chat-orb-pulse"]').exists()).toBe(true)

    // Pending approval + unread assistant reply while collapsed
    mockControllerState.approvals!.value = [
      { id: 'ap-1', status: 'pending', summary: 'Run tests' },
      { id: 'ap-2', status: 'approved', summary: 'Old' },
    ]
    mockControllerState.messages!.value = [
      { id: 'm-1', role: 'assistant', content: '已完成排查' },
    ]
    await nextTick()

    const badge = wrapper.get('[data-testid="floating-chat-orb-badge"]')
    expect(badge.text()).toBe('2')
    expect(badge.classes()).toContain('is-urgent')

    // Hover chip reflects urgent pending approval count
    await wrapper.get('.floating-chat-orb-btn').trigger('mouseenter')
    expect(wrapper.get('[data-testid="floating-chat-hover-chip"]').text()).toContain('1 项待审批')

    // Click opens PiP once, clears unread count, locks fixed inner geometry during morph,
    // and focuses composer textarea with { preventScroll: true }
    const focusSpy = vi.spyOn(HTMLTextAreaElement.prototype, 'focus')
    await wrapper.get('.floating-chat-orb-btn').trigger('click')
    await nextTick()
    await nextTick()

    expect(mockMaximizeState.openPip).toHaveBeenCalledTimes(1)
    expect(wrapper.find('[data-testid="floating-chat-pip-window"]').exists()).toBe(true)
    const contentWrap = wrapper.get('.floating-chat-window-content')
    expect(contentWrap.attributes('style')).toContain('width: 418px')
    expect(contentWrap.attributes('style')).toContain('height: 494px')
    const chatCardStub = wrapper.get('[data-testid="chat-card-stub"]')
    expect(chatCardStub.attributes('data-compact-pip')).toBe('true')
    const textarea = wrapper.get('[data-testid="pip-composer-textarea"]').element
    expect(document.activeElement).toBe(textarea)
    expect(focusSpy).toHaveBeenCalledTimes(1)
    expect(focusSpy).toHaveBeenCalledWith({ preventScroll: true })
    focusSpy.mockRestore()

    // Minimize back to orb: unread message was cleared, only 1 pending approval remains
    await wrapper.get('[data-testid="floating-chat-minimize-orb"]').trigger('click')
    await nextTick()
    expect(mockMaximizeState.minimizePip).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-testid="floating-chat-orb-badge"]').text()).toBe('1')

    wrapper.unmount()
  })

  it('shows fallback hover label when session has no title and no active status', async () => {
    mockControllerState.currentSession!.value = null
    const wrapper = mountPip()

    await wrapper.get('.floating-chat-orb-btn').trigger('mouseenter')
    expect(wrapper.get('[data-testid="floating-chat-hover-chip"]').text()).toContain('展开对话小窗')

    wrapper.unmount()
  })

  it('supports horizontal orb drag-to-snap without opening PiP, while clicks (<5px) open PiP', async () => {
    const wrapper = mountPip({ x: 240, y: 0, width: 1000, height: 800 })
    const btn = wrapper.get('.floating-chat-orb-btn').element

    // Drag orb from right (left=934) toward left edge (dx = -850)
    dispatchPointer(btn, 'pointerdown', { clientX: 950, clientY: 750, pointerId: 7 })
    dispatchPointer(btn, 'pointermove', { clientX: 100, clientY: 750, pointerId: 7 })
    await nextTick()

    // Ghosts appear during drag and left slot is active
    expect(wrapper.get('[data-testid="floating-chat-drop-ghost-left"]').attributes('data-active')).toBe('true')
    expect(wrapper.get('[data-testid="floating-chat-orb"]').classes()).toContain('is-dragging')

    dispatchPointer(btn, 'pointerup', { clientX: 100, clientY: 750, pointerId: 7 })
    await nextTick()

    expect(mockMaximizeState.setDockSide).toHaveBeenCalledWith('left')
    expect(mockMaximizeState.openPip).not.toHaveBeenCalled()

    // Small movement (< 5px) opens PiP
    dispatchPointer(btn, 'pointerdown', { clientX: 40, clientY: 750, pointerId: 8 })
    dispatchPointer(btn, 'pointermove', { clientX: 42, clientY: 751, pointerId: 8 })
    dispatchPointer(btn, 'pointerup', { clientX: 42, clientY: 751, pointerId: 8 })
    await nextTick()

    expect(mockMaximizeState.openPip).toHaveBeenCalledTimes(1)

    wrapper.unmount()
  })

  it('clamps PiP window geometry safely inside narrow and tall bounds and exposes dock/restore/minimize buttons', async () => {
    mockMaximizeState.pipOpen!.value = true
    mockMaximizeState.pipSize!.value = { width: 900, heightPct: 95 }

    // Standard bounds: width clamped to 580, height clamped to round(800 * 0.78) = 624
    const wrapper = mountPip({ x: 240, y: 0, width: 1000, height: 800 })
    const win = wrapper.get('[data-testid="floating-chat-pip-window"]')
    expect(win.attributes('style')).toContain('width: 580px')
    expect(win.attributes('style')).toContain('height: 624px')
    expect(win.attributes('style')).toContain('border-radius: 14px')

    // Narrow bounds (width 400): width clamped to 400 - 36 = 364
    await wrapper.setProps({ bounds: { x: 240, y: 0, width: 400, height: 500 } })
    expect(win.attributes('style')).toContain('width: 364px')
    expect(win.attributes('style')).toContain('height: 390px')
    expect(win.attributes('style')).toContain('left: 18px')

    // Dock buttons switch dockSide
    await wrapper.get('[data-testid="floating-chat-dock-left"]').trigger('click')
    expect(mockMaximizeState.setDockSide).toHaveBeenCalledWith('left')
    await wrapper.get('[data-testid="floating-chat-dock-center"]').trigger('click')
    expect(mockMaximizeState.setDockSide).toHaveBeenCalledWith('center')
    await wrapper.get('[data-testid="floating-chat-dock-right"]').trigger('click')
    expect(mockMaximizeState.setDockSide).toHaveBeenCalledWith('right')

    // Restore 3-col calls exitMaximize
    await wrapper.get('[data-testid="floating-chat-restore-3col"]').trigger('click')
    expect(mockMaximizeState.exitMaximize).toHaveBeenCalledTimes(1)

    wrapper.unmount()
  })

  it('shows 3 landing ghost outlines while dragging PiP header and snaps to nearest dock slot on release', async () => {
    mockMaximizeState.pipOpen!.value = true
    mockMaximizeState.dockSide!.value = 'right'
    const wrapper = mountPip({ x: 240, y: 0, width: 1000, height: 800 })

    const header = wrapper.get('[data-testid="floating-chat-pip-header"]').element
    // Starting right slot left = 1000 - 420 - 18 = 562; center slot left = 290 (dx = -272)
    dispatchPointer(header, 'pointerdown', { clientX: 800, clientY: 200, pointerId: 11 })
    dispatchPointer(header, 'pointermove', { clientX: 530, clientY: 200, pointerId: 11 })
    await nextTick()

    expect(wrapper.find('[data-testid="floating-chat-drop-ghost-left"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="floating-chat-drop-ghost-center"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="floating-chat-drop-ghost-right"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="floating-chat-drop-ghost-center"]').attributes('data-active')).toBe('true')
    expect(wrapper.get('[data-testid="floating-chat-pip-window"]').classes()).toContain('is-dragging')

    dispatchPointer(header, 'pointerup', { clientX: 530, clientY: 200, pointerId: 11 })
    await nextTick()

    expect(mockMaximizeState.setDockSide).toHaveBeenCalledWith('center')
    expect(wrapper.find('[data-testid="floating-chat-drop-ghost-center"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="floating-chat-pip-window"]').classes()).not.toContain('is-dragging')

    wrapper.unmount()
  })

  it('supports edge resizing on top and left/right handles with safe clamping via setPipSize', async () => {
    mockMaximizeState.pipOpen!.value = true
    mockMaximizeState.dockSide!.value = 'right'
    mockMaximizeState.pipSize!.value = { width: 420, heightPct: 60 }
    const wrapper = mountPip({ x: 240, y: 0, width: 1000, height: 800 })

    // Left edge drag leftward by 100px increases width from 420 -> 520
    const leftHandle = wrapper.get('[data-testid="floating-chat-resize-left"]').element
    dispatchPointer(leftHandle, 'pointerdown', { clientX: 560, clientY: 400, pointerId: 21 })
    dispatchPointer(leftHandle, 'pointermove', { clientX: 460, clientY: 400, pointerId: 21 })
    await nextTick()
    expect(mockMaximizeState.setPipSize).toHaveBeenLastCalledWith({ width: 520, heightPct: 60 })

    // Excessive drag leftward clamps at max 580
    dispatchPointer(leftHandle, 'pointermove', { clientX: 100, clientY: 400, pointerId: 21 })
    await nextTick()
    expect(mockMaximizeState.setPipSize).toHaveBeenLastCalledWith({ width: 580, heightPct: 60 })
    dispatchPointer(leftHandle, 'pointerup', { clientX: 100, clientY: 400, pointerId: 21 })

    // Top edge drag upward by 80px increases height from 480 (60%) -> 560 (70%)
    const topHandle = wrapper.get('[data-testid="floating-chat-resize-top"]').element
    dispatchPointer(topHandle, 'pointerdown', { clientX: 700, clientY: 300, pointerId: 22 })
    dispatchPointer(topHandle, 'pointermove', { clientX: 700, clientY: 220, pointerId: 22 })
    await nextTick()
    expect(mockMaximizeState.setPipSize).toHaveBeenLastCalledWith({ width: 580, heightPct: 70 })

    // Excessive upward drag clamps at 78%
    dispatchPointer(topHandle, 'pointermove', { clientX: 700, clientY: -200, pointerId: 22 })
    await nextTick()
    expect(mockMaximizeState.setPipSize).toHaveBeenLastCalledWith({ width: 580, heightPct: 78 })
    dispatchPointer(topHandle, 'pointerup', { clientX: 700, clientY: -200, pointerId: 22 })

    wrapper.unmount()
  })

  it('hides the overlay cleanly when maximized becomes false', async () => {
    const wrapper = mountPip()
    expect(wrapper.find('[data-testid="floating-chat-pip-layer"]').exists()).toBe(true)

    mockMaximizeState.maximized!.value = false
    await nextTick()
    expect(wrapper.find('[data-testid="floating-chat-pip-layer"]').exists()).toBe(false)

    wrapper.unmount()
  })
})
