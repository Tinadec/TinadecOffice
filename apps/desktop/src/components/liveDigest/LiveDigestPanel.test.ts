// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { effectScope, nextTick, ref } from 'vue'
import zhCN from '@/locales/zh-CN'
import {
  applyLiveDigestDelta,
  openLiveDigestStream,
  type LiveDigestSnapshotDto,
} from '@/lib/liveDigestApi'
import { resetLiveDigestHubForTests, useLiveDigest } from '@/composables/useLiveDigest'
import LiveDigestPanel from './LiveDigestPanel.vue'
import LiveDigestHeartbeatStrip from './LiveDigestHeartbeatStrip.vue'

vi.mock('@/lib/hostAccess', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/hostAccess')>()),
  assertHostAccess: vi.fn(async () => undefined),
}))

vi.mock('@/components/teammate/TeammateSteerPopover.vue', () => ({
  default: {
    name: 'TeammateSteerPopover',
    props: ['open', 'runId', 'handle', 'displayName', 'status', 'paused', 'taskSummary', 'thought', 'anchorRect'],
    emits: ['update:open', 'close', 'status-change'],
    template: '<div v-if="open" data-testid="mock-teammate-popover" :data-handle="handle">{{ displayName }}</div>',
  },
}))

function makeI18n() {
  return createI18n({
    legacy: false,
    locale: 'zh-CN',
    messages: { 'zh-CN': zhCN as never },
  })
}

function makeSnapshot(overrides: Partial<LiveDigestSnapshotDto> = {}): LiveDigestSnapshotDto {
  return {
    run_id: 'run-digest-1',
    session_id: 'session-1',
    status: 'running',
    mode: 'auto',
    explainer_active: true,
    explainer_model: 'gemini-2.5-flash',
    seq: 8,
    headline: '正在重构实时概要面板并验证 SSE 增量合并逻辑',
    current_phase: 'verification',
    context_waterline: {
      used_tokens: 42_500,
      max_tokens: 128_000,
      usage_percent: 33.2,
      compaction_count: 1,
      compaction_points: [
        {
          seq: 4,
          before_tokens: 96_000,
          after_tokens: 24_000,
          occurred_at: '2026-11-23T02:20:00Z',
          summary: '已折叠早期文件探索输出',
        },
      ],
    },
    governance_signals: [
      {
        id: 'sig-1',
        code: 'SAME_ARGS_RETRY',
        severity: 'warning',
        summary: '工具 vitest 连续以相同参数重试 2 次',
        occurred_at: '2026-11-23T02:21:00Z',
      },
      {
        id: 'sig-2',
        code: 'CONTEXT_PRESSURE',
        severity: 'critical',
        summary: '上下文一度超过 75% 触发自动压缩',
        occurred_at: '2026-11-23T02:22:00Z',
      },
    ],
    teammate_thoughts: [
      {
        handle: 'backend-api',
        display_name: 'backend-api',
        status: 'working',
        current_task: '校验 SSE 断点续传接口',
        thought: '正在补齐 Last-Event-ID 续传单测',
        updated_at: '2026-11-23T02:23:00Z',
      },
      {
        handle: 'qa-verifier',
        display_name: 'qa-verifier',
        status: 'paused',
        current_task: '等待前端构建门禁',
        thought: '待前端落盘后立即跑双端构建',
        updated_at: '2026-11-23T02:23:05Z',
      },
    ],
    chapters: [
      {
        id: 'ch-1',
        index: 1,
        phase: 'hypothesis',
        title: '对齐前后端 Live Digest 契约',
        narrative: '根据治理层契约建立独立只读旁路客户端，复用 storageScope 避免改动共享 api.ts。',
        source: 'l1_narrator',
        raw_thought_excerpt: '<thought>Check ScopedEventSource cursor rules; integer seq needs dedicated SSE stream client.</thought>',
        scout_note: '确认 Gateway 采用无缓冲透传，客户端需识别 done 帧并停止重连。',
        tool_calls: [
          {
            tool_call_id: 'call-1',
            tool_name: 'read_file',
            status: 'succeeded',
            compact_summary: '读取 scopedEventSource.ts (112 行)',
          },
          {
            tool_call_id: 'call-2',
            tool_name: 'write_file',
            status: 'running',
            compact_summary: '写入 liveDigestApi.ts',
          },
        ],
        touched_files: [
          { path: 'apps/desktop/src/lib/liveDigestApi.ts', added_lines: 260, removed_lines: 0 },
        ],
        started_at: '2026-11-23T02:18:00Z',
        updated_at: '2026-11-23T02:21:00Z',
      },
      {
        id: 'ch-2',
        index: 2,
        phase: 'verification',
        title: '验证自动追尾与哨兵纠偏交互',
        narrative: '确保手动上滑历史不抢焦点，并可在哨兵预警下将纠偏指令一键填入输入框。',
        source: 'l0_structural',
        raw_thought_excerpt: null,
        scout_note: null,
        tool_calls: [],
        touched_files: [
          { path: 'apps/desktop/src/components/liveDigest/LiveDigestPanel.vue', added_lines: 420, removed_lines: 12 },
        ],
        started_at: '2026-11-23T02:21:30Z',
        updated_at: '2026-11-23T02:23:00Z',
      },
    ],
    touched_files: [
      { path: 'apps/desktop/src/lib/liveDigestApi.ts', added_lines: 260, removed_lines: 0 },
      { path: 'apps/desktop/src/components/liveDigest/LiveDigestPanel.vue', added_lines: 420, removed_lines: 12 },
    ],
    sentinel_alerts: [
      {
        id: 'alert-1',
        code: 'SAME_ARGS_RETRY',
        severity: 'warning',
        title: '检测到重复运行同一测试命令',
        message: '建议先查看报错堆栈中的导出签名，再重新触发单测。',
        suggested_steer_prompt: '请先检查 liveDigestApi.ts 的导出类型后再重试测试',
        teammate_handle: 'backend-api',
        created_at: '2026-11-23T02:23:10Z',
      },
    ],
    updated_at: '2026-11-23T02:23:15Z',
    ...overrides,
  }
}

function makeSseResponse(chunks: string[]): Response {
  const encoder = new TextEncoder()
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(encoder.encode(chunk))
      }
      controller.close()
    },
  })
  return new Response(stream, {
    status: 200,
    headers: { 'content-type': 'text/event-stream' },
  })
}

afterEach(() => {
  resetLiveDigestHubForTests()
  vi.restoreAllMocks()
})

describe('LiveDigestPanel & LiveDigestHeartbeatStrip', () => {
  it('renders top status bar, context waterline with compaction markers, and governance signals', async () => {
    const wrapper = mount(LiveDigestPanel, {
      props: {
        runId: 'run-digest-1',
        snapshotOverride: makeSnapshot(),
      },
      global: { plugins: [makeI18n()] },
    })

    expect(wrapper.get('[data-testid="live-digest-headline"]').text()).toContain('正在重构实时概要面板')
    expect(wrapper.get('[data-testid="live-digest-mode-badge"]').text()).toContain('gemini-2.5-flash')
    expect(wrapper.get('[data-testid="live-digest-compaction-count"]').text()).toContain('1')

    // Click compaction marker reveals before -> after summary
    await wrapper.get('[data-testid="live-digest-compaction-marker"]').trigger('click')
    expect(wrapper.get('[data-testid="live-digest-compaction-detail"]').text()).toContain('96.0k → 24.0k')
    expect(wrapper.get('[data-testid="live-digest-compaction-detail"]').text()).toContain('已折叠早期文件探索输出')

    // Governance signal badges render and expand details on click
    const signalBadges = wrapper.findAll('[data-testid="live-digest-signal-badge"]')
    expect(signalBadges).toHaveLength(2)
    await signalBadges[0]!.trigger('click')
    expect(wrapper.text()).toContain('工具 vitest 连续以相同参数重试 2 次')
  })

  it('renders pure L0 structured badge when mode is off and explainer is inactive', () => {
    const wrapper = mount(LiveDigestPanel, {
      props: {
        runId: 'run-l0',
        snapshotOverride: makeSnapshot({
          run_id: 'run-l0',
          mode: 'off',
          explainer_active: false,
          explainer_model: null,
        }),
      },
      global: { plugins: [makeI18n()] },
    })

    expect(wrapper.get('[data-testid="live-digest-mode-badge"]').text()).toContain('L0')
  })

  it('renders multi-teammate thoughts strip and opens TeammateSteerPopover on click', async () => {
    const wrapper = mount(LiveDigestPanel, {
      props: {
        runId: 'run-digest-1',
        snapshotOverride: makeSnapshot(),
      },
      global: { plugins: [makeI18n()] },
    })

    const cards = wrapper.findAll('[data-testid="live-digest-teammate-card"]')
    expect(cards).toHaveLength(2)
    expect(cards[0]!.text()).toContain('backend-api')
    expect(cards[0]!.text()).toContain('正在补齐 Last-Event-ID 续传单测')

    await cards[0]!.trigger('click')
    expect(wrapper.emitted('select-teammate')?.[0]?.[0]).toMatchObject({
      teammate: { handle: 'backend-api' },
    })
    const popover = wrapper.find('[data-testid="mock-teammate-popover"]')
    expect(popover.exists()).toBe(true)
    expect(popover.attributes('data-handle')).toBe('backend-api')
  })

  it('expands chapter evidence (<thought>, scout note, tool calls) and emits orchestration/trajectory jumps', async () => {
    const wrapper = mount(LiveDigestPanel, {
      props: {
        runId: 'run-digest-1',
        snapshotOverride: makeSnapshot(),
        embedded: true,
      },
      global: { plugins: [makeI18n()] },
    })

    const toggles = wrapper.findAll('[data-testid="live-digest-chapter-toggle"]')
    expect(toggles).toHaveLength(2)
    expect(toggles[0]!.attributes('aria-expanded')).toBe('false')

    await toggles[0]!.trigger('click')
    expect(toggles[0]!.attributes('aria-expanded')).toBe('true')
    expect(wrapper.get('[data-testid="live-digest-scout-note"]').text()).toContain('无缓冲透传')
    expect(wrapper.get('[data-testid="live-digest-raw-thought"]').text()).toContain('ScopedEventSource')
    expect(wrapper.get('[data-testid="live-digest-tool-calls"]').text()).toContain('read_file')

    await wrapper.get('[data-testid="live-digest-jump-orchestration"]').trigger('click')
    await wrapper.get('[data-testid="live-digest-jump-trajectory"]').trigger('click')
    expect(wrapper.emitted('open-orchestration')?.length).toBe(1)
    expect(wrapper.emitted('open-trajectory')?.length).toBe(1)
  })

  it('disengages Live Streaming Tail when user scrolls up and restores on clicking Back-to-Live pill', async () => {
    const wrapper = mount(LiveDigestPanel, {
      props: {
        runId: 'run-digest-1',
        snapshotOverride: makeSnapshot(),
      },
      global: { plugins: [makeI18n()] },
    })

    expect(wrapper.find('[data-testid="live-digest-back-to-tail"]').exists()).toBe(false)

    const scrollEl = wrapper.get('[data-testid="live-digest-scroll-container"]').element as HTMLElement
    const scrollToSpy = vi.fn()
    Object.defineProperty(scrollEl, 'scrollHeight', { value: 900, configurable: true })
    Object.defineProperty(scrollEl, 'clientHeight', { value: 300, configurable: true })
    Object.defineProperty(scrollEl, 'scrollTop', { value: 180, writable: true, configurable: true })
    scrollEl.scrollTo = scrollToSpy

    await wrapper.get('[data-testid="live-digest-scroll-container"]').trigger('scroll')
    await nextTick()

    expect((wrapper.vm as unknown as { autoFollow: boolean }).autoFollow).toBe(false)
    const backBtn = wrapper.find('[data-testid="live-digest-back-to-tail"]')
    expect(backBtn.exists()).toBe(true)

    await backBtn.trigger('click')
    await nextTick()
    expect(scrollToSpy).toHaveBeenCalledWith({ top: 900, behavior: 'smooth' })
    expect(wrapper.find('[data-testid="live-digest-back-to-tail"]').exists()).toBe(false)
  })

  it('dispatches composer fill event and emits fill-steer when sentinel action button is clicked', async () => {
    const receivedEvents: CustomEvent[] = []
    const listener = (event: Event) => receivedEvents.push(event as CustomEvent)
    window.addEventListener('tinadec:live-digest-fill-steer', listener)

    try {
      const wrapper = mount(LiveDigestPanel, {
        props: {
          runId: 'run-digest-1',
          snapshotOverride: makeSnapshot(),
        },
        global: { plugins: [makeI18n()] },
      })

      await wrapper.get('[data-testid="live-digest-fill-steer-btn"]').trigger('click')

      expect(wrapper.emitted('fill-steer')?.[0]?.[0]).toMatchObject({
        prompt: '请先检查 liveDigestApi.ts 的导出类型后再重试测试',
      })
      expect(receivedEvents).toHaveLength(1)
      expect(receivedEvents[0]!.detail).toMatchObject({
        prompt: '请先检查 liveDigestApi.ts 的导出类型后再重试测试',
        runId: 'run-digest-1',
        alertId: 'alert-1',
      })
    } finally {
      window.removeEventListener('tinadec:live-digest-fill-steer', listener)
    }
  })

  it('renders compact heartbeat strip and dispatches focus-home-dock for live-digest on click', async () => {
    const dockEvents: CustomEvent[] = []
    const listener = (event: Event) => dockEvents.push(event as CustomEvent)
    window.addEventListener('tinadec:focus-home-dock', listener)

    try {
      const wrapper = mount(LiveDigestHeartbeatStrip, {
        props: {
          runId: 'run-digest-1',
          snapshotOverride: makeSnapshot(),
        },
        global: { plugins: [makeI18n()] },
      })

      expect(wrapper.get('[data-testid="live-digest-heartbeat-headline"]').text()).toContain('正在重构实时概要面板')
      expect(wrapper.get('[data-testid="live-digest-heartbeat-waterline"]').text()).toContain('33%')
      expect(wrapper.get('[data-testid="live-digest-heartbeat-alerts"]').text()).toContain('1')
      expect(wrapper.get('[data-testid="live-digest-heartbeat-teammates"]').text()).toContain('2')

      await wrapper.get('[data-testid="live-digest-heartbeat-trigger"]').trigger('click')
      expect(wrapper.emitted('open-panel')?.[0]).toEqual(['run-digest-1'])
      expect(dockEvents).toHaveLength(1)
      expect(dockEvents[0]!.detail).toMatchObject({ card: 'live-digest' })
    } finally {
      window.removeEventListener('tinadec:focus-home-dock', listener)
    }
  })

  it('folds l0_delta and l1_narration SSE events cleanly and closes on done frame', async () => {
    const snap = makeSnapshot({ seq: 1, chapters: [], sentinel_alerts: [] })
    const delta = {
      seq: 2,
      run_id: 'run-digest-1',
      headline: '阶段推进到结论收敛',
      current_phase: 'conclusion' as const,
      chapter_upsert: {
        id: 'ch-final',
        index: 3,
        phase: 'conclusion' as const,
        title: '完成验证',
        narrative: '所有单测通过。',
        source: 'l1_narrator' as const,
        tool_calls: [],
        touched_files: [],
        started_at: '2026-11-23T02:24:00Z',
        updated_at: '2026-11-23T02:24:10Z',
      },
    }
    const folded = applyLiveDigestDelta(snap, delta)
    expect(folded.seq).toBe(2)
    expect(folded.headline).toBe('阶段推进到结论收敛')
    expect(folded.current_phase).toBe('conclusion')
    expect(folded.chapters).toHaveLength(1)
    expect(folded.chapters[0]?.id).toBe('ch-final')

    const seen: string[] = []
    const donePayload = makeSnapshot({ seq: 3, status: 'completed' })
    const mockFetch = vi.fn(async () =>
      makeSseResponse([
        ': heartbeat\n\n',
        `id: 1\nevent: snapshot\ndata: ${JSON.stringify(snap)}\n\n`,
        `id: 2\nevent: l1_narration\ndata: ${JSON.stringify(delta)}\n\n`,
        `id: 3\nevent: done\ndata: ${JSON.stringify(donePayload)}\n\n`,
      ]),
    )

    const stream = openLiveDigestStream(
      'run-digest-1',
      {
        onSnapshot: () => seen.push('snapshot'),
        onL1Narration: () => seen.push('l1_narration'),
        onDone: () => seen.push('done'),
      },
      { fetchImpl: mockFetch as unknown as typeof fetch },
    )

    await vi.waitFor(() => {
      expect(seen).toEqual(['snapshot', 'l1_narration', 'done'])
    })
    expect(stream.closed).toBe(true)
    expect(stream.lastSeq).toBe(3)
    expect(mockFetch).toHaveBeenCalledTimes(1)
  })

  it('shares a single SSE subscription across multiple useLiveDigest consumers for the same runId', async () => {
    const snap = makeSnapshot({ seq: 5, status: 'completed' })
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      const url = String(input)
      if (url.includes('/live-digest/stream')) {
        return makeSseResponse([
          `id: 5\nevent: done\ndata: ${JSON.stringify(snap)}\n\n`,
        ])
      }
      return new Response(JSON.stringify(snap), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      })
    })

    const scope = effectScope()
    let consumerA!: ReturnType<typeof useLiveDigest>
    let consumerB!: ReturnType<typeof useLiveDigest>

    scope.run(() => {
      const runId = ref('run-shared-42')
      consumerA = useLiveDigest(runId)
      consumerB = useLiveDigest(runId)
    })

    await vi.waitFor(() => {
      expect(consumerA.snapshot.value?.seq).toBe(5)
      expect(consumerB.snapshot.value?.seq).toBe(5)
    })

    expect(consumerA.teammateThoughtByHandle.value['backend-api']?.thought).toBe('正在补齐 Last-Event-ID 续传单测')
    const streamCalls = fetchSpy.mock.calls.filter(([url]) => String(url).includes('/live-digest/stream'))
    expect(streamCalls).toHaveLength(1)

    scope.stop()
  })
})
