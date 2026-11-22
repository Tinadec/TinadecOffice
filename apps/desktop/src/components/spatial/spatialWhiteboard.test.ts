// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { Position } from '@vue-flow/core'

const mocks = vi.hoisted(() => ({
  readFile: vi.fn(),
}))

vi.mock('@/api', () => ({
  api: {
    readFile: mocks.readFile,
  },
}))

vi.mock('vue-i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({
    t: (key: string, params?: Record<string, unknown>) => {
      if (!params || Object.keys(params).length === 0) return key
      return `${key} ${Object.values(params).join(' ')}`
    },
    te: () => true,
  }),
}))

vi.mock('@vue-flow/core', async () => {
  const actual = await vi.importActual<typeof import('@vue-flow/core')>('@vue-flow/core')
  return {
    ...actual,
    Handle: {
      name: 'Handle',
      props: ['id', 'type', 'position'],
      template: '<span class="mock-handle" :data-handle="id" />',
    },
    BaseEdge: {
      name: 'BaseEdge',
      props: ['id', 'path', 'markerEnd', 'style'],
      template: '<path class="mock-base-edge" :data-edge-id="id" :d="path" :marker-end="markerEnd" />',
    },
    EdgeLabelRenderer: {
      name: 'EdgeLabelRenderer',
      template: '<div class="mock-edge-label-renderer"><slot /></div>',
    },
  }
})

vi.mock('@vue-flow/node-resizer', () => ({
  NodeResizer: {
    name: 'NodeResizer',
    props: ['isVisible', 'minWidth', 'minHeight', 'maxWidth', 'maxHeight'],
    emits: ['resizeStart', 'resizeEnd'],
    template: '<div class="mock-node-resizer" :data-visible="String(Boolean(isVisible))" />',
  },
}))

import {
  buildSmoothStrokePath,
  encodeMediaPathToken,
  extractMentions,
  hasAiOrTeamMention,
  isPointNearStroke,
  type SpatialCommentPin,
} from './spatialWhiteboardTypes'
import SpatialSectionNode from './SpatialSectionNode.vue'
import SpatialStickyNode from './SpatialStickyNode.vue'
import SpatialTextNode from './SpatialTextNode.vue'
import SpatialDrawingLayer from './SpatialDrawingLayer.vue'
import SpatialMediaFileNode from './SpatialMediaFileNode.vue'
import SpatialCommentPinsLayer from './SpatialCommentPinsLayer.vue'
import SpatialFreeConnectorEdge from './SpatialFreeConnectorEdge.vue'
import SpatialPresenceLayer from './SpatialPresenceLayer.vue'
import SpatialSelectionBar from './SpatialSelectionBar.vue'
import { useSpatialBoardSync } from '@/composables/useSpatialBoardSync'

const wrappers: VueWrapper[] = []

function track<T extends VueWrapper>(wrapper: T): T {
  wrappers.push(wrapper)
  return wrapper
}

beforeEach(() => {
  vi.clearAllMocks()
})

afterEach(() => {
  wrappers.splice(0).forEach((w) => w.unmount())
})

describe('spatialWhiteboardTypes helpers', () => {
  it('builds smooth quadratic bezier paths and detects eraser hits accurately', () => {
    expect(buildSmoothStrokePath([])).toBe('')
    expect(buildSmoothStrokePath([[10, 20]])).toContain('M 10.00 20.00')
    const curve = buildSmoothStrokePath([
      [0, 0],
      [20, 10],
      [40, 0],
      [60, 12],
    ])
    expect(curve).toContain('M 0.00 0.00')
    expect(curve).toContain('Q ')
    expect(curve).toContain('L 60.00 12.00')

    expect(isPointNearStroke(20, 12, [[0, 0], [40, 20]], 6)).toBe(true)
    expect(isPointNearStroke(20, 45, [[0, 0], [40, 20]], 6)).toBe(false)
  })

  it('extracts mentions and encodes URL-safe media path tokens', () => {
    expect(extractMentions('Please @AI review with @team and @engineering')).toEqual([
      'AI',
      'team',
      'engineering',
    ])
    expect(hasAiOrTeamMention({ body: 'Ask @AI to check', mentions: [], replies: [] })).toBe(true)
    expect(hasAiOrTeamMention({ body: 'Plain review note', mentions: [], replies: [] })).toBe(false)
    const token = encodeMediaPathToken('C:\\repo\\assets\\diagram.png')
    expect(token).not.toContain('+')
    expect(token).not.toContain('/')
    expect(token).not.toContain('=')
  })
})

describe('SpatialSectionNode', () => {
  it('supports double-click inline rename, color dot switching, and collapse toggle', async () => {
    const wrapper = track(mount(SpatialSectionNode, {
      props: {
        id: 'sec-1',
        title: 'Architecture',
        color: 'indigo',
        collapsed: false,
        childCount: 3,
        selected: true,
      },
    }))

    expect(wrapper.get('[data-testid="section-title-label"]').text()).toBe('Architecture')
    expect(wrapper.get('[data-testid="section-child-count"]').text()).toBe('3')

    await wrapper.get('.spatial-section-header').trigger('dblclick')
    const input = wrapper.get('[data-testid="section-title-input"]')
    await input.setValue('Sprint Scope')
    await input.trigger('keydown.enter')
    expect(wrapper.emitted('update:title')?.[0]).toEqual(['Sprint Scope'])

    await wrapper.get('[data-section-color-dot="emerald"]').trigger('click')
    expect(wrapper.emitted('update:color')?.[0]).toEqual(['emerald'])

    await wrapper.get('[data-testid="section-collapse-btn"]').trigger('click')
    expect(wrapper.emitted('update:collapsed')?.[0]).toEqual([true])
  })

  it('renders restrained single-line live headline + context waterline micro-badge and emits open-live-digest', async () => {
    const wrapper = track(mount(SpatialSectionNode, {
      props: {
        id: 'sec-run-1',
        title: 'Run #1',
        color: 'indigo',
        runId: 'run-42',
        liveHeadline: 'Auditing API schema parity',
        liveUsagePercent: 64,
        liveRunning: true,
      },
    }))

    expect(wrapper.get('[data-testid="section-live-headline"]').text()).toBe('Auditing API schema parity')
    expect(wrapper.get('[data-testid="section-live-waterline"]').text()).toContain('64%')

    await wrapper.get('[data-testid="section-live-digest"]').trigger('click')
    expect(wrapper.emitted('open-live-digest')?.[0]).toEqual(['run-42'])
  })
})

describe('SpatialStickyNode', () => {
  it('edits text inline on double click and switches 6 FigJam paper tones', async () => {
    const wrapper = track(mount(SpatialStickyNode, {
      props: {
        id: 'sticky-1',
        text: 'Initial idea',
        tone: 'butter',
        authorName: 'Architect',
        authorKind: 'ai',
        selected: true,
      },
    }))

    expect(wrapper.get('[data-testid="sticky-text"]').text()).toBe('Initial idea')
    expect(wrapper.text()).toContain('space.whiteboard.aiTeammateBadge')

    await wrapper.trigger('dblclick')
    const textarea = wrapper.get('[data-testid="sticky-textarea"]')
    await textarea.setValue('Refined idea')
    await textarea.trigger('blur')
    expect(wrapper.emitted('update:text')?.[0]).toEqual(['Refined idea'])

    await wrapper.get('[data-sticky-tone-btn="lavender"]').trigger('click')
    expect(wrapper.emitted('update:tone')?.[0]).toEqual(['lavender'])
  })
})

describe('SpatialTextNode', () => {
  it('edits borderless text inline and cycles S/M/L/XL scales', async () => {
    const wrapper = track(mount(SpatialTextNode, {
      props: {
        id: 'txt-1',
        text: 'Canvas heading',
        scale: 'md',
        selected: true,
      },
    }))

    expect(wrapper.attributes('data-text-scale')).toBe('md')
    await wrapper.get('[data-text-scale-btn="xl"]').trigger('click')
    expect(wrapper.emitted('update:scale')?.[0]).toEqual(['xl'])

    await wrapper.trigger('dblclick')
    const input = wrapper.get('[data-testid="text-node-input"]')
    await input.setValue('Updated heading')
    await input.trigger('blur')
    expect(wrapper.emitted('update:text')?.[0]).toEqual(['Updated heading'])
  })
})

describe('SpatialDrawingLayer', () => {
  it('captures world-coordinate pen strokes and erases strokes in eraser mode', async () => {
    const wrapper = track(mount(SpatialDrawingLayer, {
      props: {
        strokes: [
          {
            id: 'draw-1',
            props: {
              points: [[10, 10], [50, 10]],
              color: '#3b82f6',
              strokeWidth: 3,
              mode: 'pen',
            },
          },
        ],
        viewport: { x: 20, y: 10, zoom: 2 },
        tool: 'pen',
        color: '#ef4444',
        strokeWidth: 4,
      },
    }))

    expect(wrapper.get('[data-testid="spatial-drawing-world"]').attributes('transform')).toBe(
      'translate(20 10) scale(2)',
    )
    const svg = wrapper.get('svg')
    await svg.trigger('pointerdown', { button: 0, clientX: 40, clientY: 30, pointerId: 1 })
    await svg.trigger('pointermove', { clientX: 80, clientY: 50, pointerId: 1 })
    await svg.trigger('pointerup', { pointerId: 1 })

    const completed = wrapper.emitted('stroke-complete')?.[0]?.[0] as {
      points: [number, number][]
      color: string
      strokeWidth: number
      mode: string
    }
    expect(completed).toBeTruthy()
    expect(completed.mode).toBe('pen')
    expect(completed.color).toBe('#ef4444')
    expect(completed.points[0]).toEqual([10, 10])
    expect(completed.points[1]).toEqual([30, 20])

    await wrapper.setProps({ tool: 'eraser' })
    await wrapper.get('[data-stroke-id="draw-1"]').trigger('click')
    expect(wrapper.emitted('stroke-erase')?.[0]).toEqual(['draw-1'])
  })
})

describe('SpatialMediaFileNode', () => {
  it('renders image preview with encoded /api/v1/fs/media URL and lazy-loads code file snippet', async () => {
    const imgWrapper = track(mount(SpatialMediaFileNode, {
      props: {
        id: 'media-img',
        mediaKind: 'image',
        path: '/repo/wireframe.png',
        title: 'wireframe.png',
      },
    }))
    const img = imgWrapper.get('[data-testid="media-image-el"]')
    expect(img.attributes('src')).toContain('/api/v1/fs/media?token=')

    mocks.readFile.mockResolvedValueOnce({
      status: 'completed',
      data: {
        success: true,
        all_contents: [
          { content: { Content: 'export const answer = 42', LineNumber: 1 } },
          { content: { Content: 'console.log(answer)', LineNumber: 2 } },
        ],
      },
    })

    const fileWrapper = track(mount(SpatialMediaFileNode, {
      props: {
        id: 'media-file',
        mediaKind: 'file',
        path: 'src/answer.ts',
        title: 'answer.ts',
        language: 'ts',
        cwd: '/repo',
      },
    }))
    await flushPromises()
    expect(mocks.readFile).toHaveBeenCalledWith('/repo', 'src/answer.ts', { start_row: 1, end_row: 40 })
    expect(fileWrapper.get('[data-testid="media-code-snippet"]').text()).toContain('export const answer = 42')

    await fileWrapper.get('[data-testid="media-open-btn"]').trigger('click')
    expect(fileWrapper.emitted('open-file')?.[0]).toEqual([
      { id: 'media-file', path: 'src/answer.ts', mediaKind: 'file' },
    ])
  })
})

describe('SpatialCommentPinsLayer', () => {
  it('renders pins, creates comments in placing mode, replies, resolves, and dispatches @AI mentions', async () => {
    const pin: SpatialCommentPin = {
      id: 'pin-1',
      x: 120,
      y: 80,
      authorId: 'u1',
      authorName: 'Alice',
      authorKind: 'human',
      body: '@AI please tighten the latency budget here',
      mentions: ['AI'],
      replies: [],
      resolved: false,
      createdAtUnixMs: 1000,
      updatedAtUnixMs: 1000,
    }

    const wrapper = track(mount(SpatialCommentPinsLayer, {
      props: {
        pins: [pin],
        viewport: { x: 0, y: 0, zoom: 1 },
        activePinId: 'pin-1',
        placing: true,
      },
    }))

    expect(wrapper.get('[data-testid="comment-body"]').text()).toContain('@AI please tighten')

    await wrapper.get('[data-testid="comment-dispatch-btn"]').trigger('click')
    expect(wrapper.emitted('dispatch-comment')?.[0]?.[0]).toEqual(pin)

    await wrapper.get('[data-testid="comment-reply-input"]').setValue('Done in commit abc')
    await wrapper.get('.spatial-comment-reply-form').trigger('submit')
    expect(wrapper.emitted('reply-pin')?.[0]).toEqual([{ pinId: 'pin-1', body: 'Done in commit abc' }])

    await wrapper.get('[data-testid="comment-resolve-btn"]').trigger('click')
    expect(wrapper.emitted('toggle-resolve')?.[0]).toEqual([{ pinId: 'pin-1', resolved: true }])

    await wrapper.get('[data-testid="comment-delete-btn"]').trigger('click')
    expect(wrapper.emitted('delete-pin')?.[0]).toEqual(['pin-1'])

    await wrapper.get('[data-testid="spatial-comments-layer"]').trigger('click', { clientX: 250, clientY: 140 })
    await wrapper.get('[data-testid="comment-create-input"]').setValue('@team check edge cases')
    await wrapper.get('[data-testid="comment-create-popover"]').trigger('submit')
    expect(wrapper.emitted('create-pin')?.[0]).toEqual([{ x: 250, y: 140, body: '@team check edge cases' }])
  })
})

describe('SpatialFreeConnectorEdge', () => {
  it('supports bezier and smoothstep paths plus double-click inline label editing', async () => {
    const wrapper = track(mount(SpatialFreeConnectorEdge, {
      props: {
        id: 'conn-1',
        sourceX: 0,
        sourceY: 0,
        targetX: 200,
        targetY: 120,
        sourcePosition: Position.Right,
        targetPosition: Position.Left,
        selected: true,
        data: { curve: 'smoothstep', label: 'depends on' },
      },
    }))

    expect(wrapper.attributes('data-connector-curve')).toBe('smoothstep')
    expect(wrapper.get('.mock-base-edge').attributes('d')).toBeTruthy()
    expect(wrapper.get('[data-testid="connector-label-btn"]').text()).toBe('depends on')

    await wrapper.get('[data-testid="connector-curve-toggle"]').trigger('click')
    expect(wrapper.emitted('update:curve')?.[0]).toEqual([{ id: 'conn-1', curve: 'bezier' }])

    await wrapper.get('[data-testid="connector-hit-path"]').trigger('dblclick')
    const input = wrapper.get('[data-testid="connector-label-input"]')
    await input.setValue('triggers')
    await input.trigger('keydown.enter')
    expect(wrapper.emitted('update:label')?.[0]).toEqual([{ id: 'conn-1', label: 'triggers' }])
  })
})

describe('SpatialPresenceLayer', () => {
  it('renders remote human & AI cursors, selection frames, and following HUD banner', async () => {
    const wrapper = track(mount(SpatialPresenceLayer, {
      props: {
        localClientId: 'me',
        followingClientId: 'ai-1',
        viewport: { x: 10, y: 20, zoom: 1 },
        objectRects: {
          'sticky-1': { x: 100, y: 100, width: 220, height: 180 },
        },
        collaborators: [
          {
            clientId: 'me',
            displayName: 'Local',
            color: '#000000',
            role: 'human',
            cursor: { x: 5, y: 5 },
          },
          {
            clientId: 'ai-1',
            displayName: 'Codex Reviewer',
            color: '#8b5cf6',
            role: 'ai',
            cursor: { x: 140, y: 90 },
            selection: ['sticky-1'],
          },
        ],
      },
    }))

    expect(wrapper.find('[data-cursor-client="me"]').exists()).toBe(false)
    expect(wrapper.find('[data-cursor-client="ai-1"]').exists()).toBe(true)
    expect(wrapper.find('[data-remote-selection="ai-1:sticky-1"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="spatial-following-banner"]').text()).toContain('Codex Reviewer')

    await wrapper.get('[data-testid="spatial-stop-follow-btn"]').trigger('click')
    expect(wrapper.emitted('stop-follow')).toHaveLength(1)
  })

  it('renders translucent AI teammate thought micro-bubble and emits steer-teammate on click', async () => {
    const wrapper = track(mount(SpatialPresenceLayer, {
      props: {
        localClientId: 'me',
        activeRunId: 'run-9',
        viewport: { x: 0, y: 0, zoom: 1 },
        teammateThoughts: {
          'impl#1': {
            handle: 'impl#1',
            display_name: 'impl#1',
            status: 'working',
            current_task: 'Patch middleware',
            thought: 'Checking retry backoff timing',
            updated_at: '2026-11-23T02:20:00Z',
          },
        },
        collaborators: [
          {
            clientId: 'ai:impl#1',
            displayName: 'impl#1',
            handle: 'impl#1',
            color: '#8b5cf6',
            role: 'ai',
            status: 'working',
            cursor: { x: 200, y: 120 },
          },
        ],
      },
    }))

    const bubble = wrapper.get('[data-testid="spatial-thought-bubble"]')
    expect(bubble.text()).toContain('Checking retry backoff timing')

    await bubble.trigger('click')
    expect(wrapper.emitted('steer-teammate')?.[0]?.[0]).toMatchObject({
      runId: 'run-9',
      handle: 'impl#1',
      thought: 'Checking retry backoff timing',
      taskSummary: 'Patch middleware',
    })
  })
})

describe('SpatialSelectionBar', () => {
  it('emits align, distribute, wrap-section, duplicate, delete, and dispatch-to-team actions', async () => {
    const wrapper = track(mount(SpatialSelectionBar, {
      props: {
        selectedIds: ['n1', 'n2', 'n3'],
        bounds: { x: 100, y: 120, width: 400, height: 220 },
        viewport: { x: 0, y: 0, zoom: 1 },
        surfaceWidth: 1200,
        surfaceHeight: 800,
      },
    }))

    await wrapper.get('[data-align="left"]').trigger('click')
    expect(wrapper.emitted('align')?.[0]).toEqual(['left'])

    await wrapper.get('[data-distribute="horizontal"]').trigger('click')
    expect(wrapper.emitted('distribute')?.[0]).toEqual(['horizontal'])

    await wrapper.get('[data-testid="selection-wrap-section"]').trigger('click')
    expect(wrapper.emitted('wrap-section')).toHaveLength(1)

    await wrapper.get('[data-testid="selection-duplicate"]').trigger('click')
    expect(wrapper.emitted('duplicate')).toHaveLength(1)

    await wrapper.get('[data-testid="selection-delete"]').trigger('click')
    expect(wrapper.emitted('delete')).toHaveLength(1)

    await wrapper.get('[data-testid="selection-dispatch-team"]').trigger('click')
    expect(wrapper.emitted('dispatch-to-team')?.[0]).toEqual([['n1', 'n2', 'n3']])
  })
})

class FakeWebSocket {
  static instances: FakeWebSocket[] = []
  readyState = 0
  sent: string[] = []
  onopen: ((ev: Event) => void) | null = null
  onmessage: ((ev: MessageEvent) => void) | null = null
  onerror: ((ev: Event) => void) | null = null
  onclose: ((ev: CloseEvent) => void) | null = null

  constructor(public readonly url: string) {
    FakeWebSocket.instances.push(this)
  }

  send(data: string) {
    this.sent.push(data)
  }

  close() {
    this.readyState = 3
    this.onclose?.({} as CloseEvent)
  }

  triggerOpen() {
    this.readyState = 1
    this.onopen?.({} as Event)
  }

  triggerMessage(payload: Record<string, unknown>) {
    this.onmessage?.({ data: JSON.stringify(payload) } as MessageEvent)
  }
}

describe('useSpatialBoardSync composable', () => {
  beforeEach(() => {
    FakeWebSocket.instances = []
    window.localStorage.clear()
  })

  it('degrades cleanly to localStorage for draft sessions and supports all whiteboard mutations', async () => {
    const wsFactory = vi.fn((url: string) => new FakeWebSocket(url) as unknown as WebSocket)
    const board = useSpatialBoardSync({
      sessionKey: 'draft:project-alpha',
      displayName: 'Designer',
      webSocketFactory: wsFactory,
    })

    expect(board.syncStatus.value).toBe('local')
    expect(wsFactory).not.toHaveBeenCalled()

    const stickyA = board.createSticky({ x: 100, y: 100, width: 200, height: 180, text: 'Note A', tone: 'butter' })
    const stickyB = board.createSticky({ x: 340, y: 140, width: 200, height: 180, text: 'Note B', tone: 'mint' })
    const stickyC = board.createSticky({ x: 640, y: 120, width: 200, height: 180, text: 'Note C', tone: 'sky' })
    const textNode = board.createText({ x: 100, y: 40, text: 'Roadmap', scale: 'lg' })
    const stroke = board.addDrawingStroke({ points: [[10, 20], [50, 80]], mode: 'pen', color: '#3b82f6' })
    const media = board.createMediaTile({ mediaKind: 'file', path: 'src/index.ts', snippet: 'export {}' })
    const conn = board.createConnector({ sourceId: stickyA.id, targetId: stickyB.id, label: 'leads to' })

    expect(board.objectsByKind.value.sticky).toHaveLength(3)
    expect(board.objectsByKind.value.text).toHaveLength(1)
    expect(board.objectsByKind.value.drawing).toHaveLength(1)
    expect(board.objectsByKind.value.media).toHaveLength(1)
    expect(board.objectsByKind.value.connector).toHaveLength(1)

    // Align and distribute
    board.alignSelected([stickyA.id, stickyB.id, stickyC.id], 'top')
    expect(board.objectsById.value[stickyB.id]!.y).toBe(100)
    expect(board.objectsById.value[stickyC.id]!.y).toBe(100)

    board.distributeSelected([stickyA.id, stickyB.id, stickyC.id], 'horizontal')
    expect(board.objectsById.value[stickyB.id]!.x).toBe(370)

    // Wrap selection in section + verify fractional ordering & descendant drag
    const section = board.wrapSelectionInSection([stickyA.id, stickyB.id], { title: 'Phase 1', color: 'indigo' })
    expect(section).not.toBeNull()
    expect(board.objectsById.value[stickyA.id]!.parentId).toBe(section!.id)
    expect(board.objectsById.value[stickyB.id]!.parentId).toBe(section!.id)
    expect(section!.sortKey < board.objectsById.value[stickyA.id]!.sortKey).toBe(true)

    // Reject self/descendant reparent cycles
    expect(board.reparentObject(section!.id, stickyA.id)).toBe(false)

    // Move section translates children atomically
    const prevChildX = board.objectsById.value[stickyA.id]!.x
    board.moveObjectsWithSnap([{ id: section!.id, x: section!.x + 50, y: section!.y + 20 }], { snap: false })
    expect(board.objectsById.value[stickyA.id]!.x).toBe(prevChildX + 50)

    // Smart guides snapping near another object's edge
    const snapResult = board.moveObjectsWithSnap([{ id: textNode.id, x: stickyC.x + 3, y: 40 }], { threshold: 6 })
    expect(snapResult.changes[0]!.x).toBe(stickyC.x)
    expect(board.activeGuides.value.length).toBeGreaterThan(0)
    board.clearActiveGuides()
    expect(board.activeGuides.value).toHaveLength(0)

    // Erase stroke, duplicate, delete, and comment pin persistence
    board.eraseDrawingStroke(stroke.id)
    expect(board.objectsByKind.value.drawing).toHaveLength(0)

    const dupes = board.duplicateObjects([media.id])
    expect(dupes).toHaveLength(1)
    expect(board.objectsByKind.value.media).toHaveLength(2)

    board.deleteObjects([conn.id])
    expect(board.objectsByKind.value.connector).toHaveLength(0)

    const pin = await board.addCommentPin({ x: 200, y: 200, body: '@AI review this section' })
    expect(pin.mentions).toEqual(['AI'])
    await board.replyCommentPin(pin.id, 'Looks solid')
    await board.toggleResolveCommentPin(pin.id, true)
    expect(board.comments.value[0]!.replies).toHaveLength(1)
    expect(board.comments.value[0]!.resolved).toBe(true)

    board.dispose()

    // Rehydrates cleanly from localStorage on next session mount
    const restored = useSpatialBoardSync({
      sessionKey: 'draft:project-alpha',
      webSocketFactory: wsFactory,
    })
    expect(restored.objectsByKind.value.sticky).toHaveLength(3)
    expect(restored.objectsByKind.value.section).toHaveLength(1)
    expect(restored.comments.value).toHaveLength(1)
    expect(restored.comments.value[0]!.replies).toHaveLength(1)
    await restored.deleteCommentPin(pin.id)
    expect(restored.comments.value).toHaveLength(0)
    restored.dispose()
  })

  it('syncs live WebSocket ops, throttles presence, and applies followed collaborator viewport', async () => {
    vi.useFakeTimers()
    const onFollowViewport = vi.fn()
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({}), { status: 200 }))
    const sessionUuid = '11111111-2222-3333-4444-555555555555'

    const board = useSpatialBoardSync({
      sessionKey: sessionUuid,
      displayName: 'Lead',
      onFollowViewport,
      webSocketFactory: (url) => new FakeWebSocket(url) as unknown as WebSocket,
      fetchImpl: fetchMock as unknown as typeof fetch,
    })

    expect(FakeWebSocket.instances).toHaveLength(1)
    const ws = FakeWebSocket.instances[0]!
    expect(ws.url).toContain(`/api/v1/sessions/${sessionUuid}/spatial-board/ws`)

    ws.triggerOpen()
    expect(board.syncStatus.value).toBe('live')
    expect(JSON.parse(ws.sent[0]!).type).toBe('hello')

    // Receive snapshot with an AI teammate collaborator
    ws.triggerMessage({
      type: 'snapshot',
      session_id: sessionUuid,
      seq: 5,
      objects: {
        'sticky:remote': {
          id: 'sticky:remote',
          kind: 'sticky',
          parent_id: null,
          sort_key: 'V',
          x: 50,
          y: 60,
          width: 220,
          height: 220,
          props: { text: 'From peer', tone: 'sky' },
          prop_timestamps: { x: { lamport: 5, writer_id: 'peer-1' } },
        },
      },
      comments: [],
      collaborators: [
        {
          client_id: 'ai-agent-1',
          display_name: 'Architect AI',
          color: '#8b5cf6',
          role: 'ai',
          cursor_x: 300,
          cursor_y: 200,
          selection: ['sticky:remote'],
          viewport: { x: -120, y: -40, zoom: 0.9 },
        },
      ],
    })

    expect(board.objectsById.value['sticky:remote']?.props.text).toBe('From peer')
    expect(board.collaborators.value).toHaveLength(1)

    // Follow collaborator -> immediately applies viewport and tracks updates
    board.toggleFollowCollaborator('ai-agent-1')
    expect(board.followingClientId.value).toBe('ai-agent-1')
    expect(onFollowViewport).toHaveBeenLastCalledWith({ x: -120, y: -40, zoom: 0.9 })

    ws.triggerMessage({
      type: 'presence_update',
      session_id: sessionUuid,
      collaborators: [
        {
          client_id: 'ai-agent-1',
          display_name: 'Architect AI',
          color: '#8b5cf6',
          role: 'ai',
          cursor_x: 340,
          cursor_y: 220,
          selection: [],
          viewport: { x: -240, y: -80, zoom: 1.1 },
        },
      ],
    })
    expect(onFollowViewport).toHaveBeenLastCalledWith({ x: -240, y: -80, zoom: 1.1 })

    board.stopFollowing()
    expect(board.followingClientId.value).toBeNull()

    // Coalesce bursty cursor presence sends within 50ms
    vi.advanceTimersByTime(120)
    const sentBefore = ws.sent.length
    board.sendPresence({ cursor: { x: 10, y: 10 } })
    board.sendPresence({ cursor: { x: 20, y: 20 } })
    board.sendPresence({ cursor: { x: 30, y: 30 } })
    expect(ws.sent.length - sentBefore).toBe(1)

    vi.advanceTimersByTime(80)
    const presenceFrames = ws.sent
      .slice(sentBefore)
      .map((raw) => JSON.parse(raw) as Record<string, unknown>)
      .filter((frame) => frame.type === 'presence')
    expect(presenceFrames).toHaveLength(2)
    expect(presenceFrames[1]!.cursor).toEqual({ x: 30, y: 30 })

    // Local mutation sends ops over WS and accepts ops_ack
    const created = board.createSticky({ x: 400, y: 300, text: 'Synced note' })
    const opFrame = ws.sent
      .map((raw) => JSON.parse(raw) as Record<string, unknown>)
      .reverse()
      .find((frame) => frame.type === 'ops')
    expect(opFrame).toBeTruthy()
    const firstOp = (opFrame!.ops as Array<Record<string, unknown>>)[0]!
    expect(firstOp.object_id).toBe(created.id)

    ws.triggerMessage({
      type: 'ops_ack',
      seq: 6,
      accepted_op_ids: [String(firstOp.client_op_id)],
      rejected_ops: [],
      objects: {},
    })
    expect(board.doc.value.serverSeq).toBe(6)
    expect(board.doc.value.hasPendingOps()).toBe(false)

    board.dispose()
    vi.useRealTimers()
  })
})
