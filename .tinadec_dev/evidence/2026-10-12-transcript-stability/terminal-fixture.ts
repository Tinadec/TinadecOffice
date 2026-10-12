// Actual production terminal components and xterm; only PTY/Core transport is replaced.
import { createApp, h, nextTick, ref, defineComponent } from 'vue'
import { createI18n } from 'vue-i18n'
import zh from '@/locales/zh-CN'
import ChatPanel from '@/components/ChatPanel.vue'
import { provideMessageScroller } from '@/components/ui/messageScroller/useMessageScroller'
import TerminalView from '@/components/TerminalView.vue'
import TerminalCallBlock from '@/components/chat/TerminalCallBlock.vue'
import { useTerminal } from '@/composables/useTerminal'

const errors: string[] = []
const records: Record<string, unknown>[] = []
const feeds = new Set<(event: unknown) => void>()
const data = new Set<(text: string) => void>()
const exits = new Set<(code: number) => void>()
const snapshots: Array<(value: unknown) => void> = []
const registry: Array<(value: unknown) => void> = []
const controls: unknown[] = []
const i18n = createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })
let releasedRegistrySignals = 0
const delay = (ms: number) => new Promise(resolve => setTimeout(resolve, ms))
function assert(value: unknown, message: string) { if (!value) throw new Error(message) }
addEventListener('error', event => { if (event.error) errors.push(event.error.stack ?? event.message) })
addEventListener('unhandledrejection', event => errors.push(String(event.reason?.stack ?? event.reason)))
const scope = window as unknown as Record<string, any>
scope.__terminalBoundary = {
  events: { value: [] },
  onEvent: (handler: (event: unknown) => void) => { feeds.add(handler); return () => feeds.delete(handler) },
  listTerminalSessions: (_run: string, signal: AbortSignal) => new Promise(resolve => {
    signal.addEventListener('abort', () => { releasedRegistrySignals++ }, { once: true })
    registry.push(resolve)
  }),
  controlRun: (...args: unknown[]) => { controls.push(args); return Promise.resolve() },
  sendTerminalStdin: () => Promise.resolve(), killTerminalSession: () => Promise.resolve(),
}
scope.tinadec = { terminal: {
  create: async () => ({ id: 'fixture-local', shell: 'fixture', title: 'Isolated terminal' }),
  getShells: async () => [], write: () => {}, resize: () => {}, destroy: () => {},
  snapshot: () => new Promise(resolve => snapshots.push(resolve)),
  onData: (_id: string, handler: (text: string) => void) => { data.add(handler); return () => data.delete(handler) },
  onExit: (_id: string, handler: (code: number) => void) => { exits.add(handler); return () => exits.delete(handler) },
} }
const manager = useTerminal()
function host() {
  const node = document.createElement('div')
  node.style.cssText = 'width:720px;height:240px;margin:12px;border:1px solid #888'
  document.body.append(node)
  return node
}
function mount(component: any, props: Record<string, unknown>) {
  const node = host()
  const app = createApp({ render: () => h(component, props) })
  app.config.errorHandler = error => errors.push(String((error as Error)?.stack ?? error))
  app.use(i18n)
  app.mount(node)
  return { node, unmount() { app.unmount(); node.remove() } }
}
function visibleBuffer(term: any) {
  const buffer = term.buffer.active
  return Array.from({ length: buffer.length }, (_, index) => buffer.getLine(index)?.translateToString(true) ?? '').join('\n')
}
function terminalEvent(seq: number, payload: Record<string, unknown>) {
  return { v: '1', type: 'terminal.stdout', seq, payload, capabilities: [], ts: new Date().toISOString(), request_id: '', trace_id: '' }
}

async function run() {
  await manager.createTerminal()
  // The previous snapshot must not be delivered to the next attachment.
  const first = mount(TerminalView, { terminalId: 'fixture-local' })
  await nextTick()
  for (const callback of data) callback('OLD-HELD')
  const oldSnapshot = snapshots.shift()!
  manager.getTerminal('fixture-local')!.term!.options.theme = { background: '#111111' }
  first.unmount()
  const second = mount(TerminalView, { terminalId: 'fixture-local' })
  const current = manager.getTerminal('fixture-local')!.term!
  oldSnapshot({ replay: 'OLD-SNAPSHOT', exited: true })
  snapshots.shift()!({ replay: 'current replay\r\n', exited: false })
  await delay(40)
  assert(!visibleBuffer(current).includes('OLD-'), 'stale snapshot reached the next attachment')
  assert(!manager.getTerminal('fixture-local')!.exited, 'stale exit reached the next attachment')
  assert(current.cols > 80 && current.rows > 1, 'terminal did not fit the real DOM')
  records.push({ step: 'snapshot_attachment_identity', cols: current.cols, rows: current.rows, stale_output: false })
  for (let i = 0; i < 60; i++) for (const callback of data) callback(`output line ${i}\r\n`)
  await delay(100)
  assert(second.node.querySelector('.xterm-scrollable-element'), 'xterm 6 scrollbar is absent')
  scope.__terminalScreenshotReady = true
  await delay(150)
  second.unmount()

  for (let cycle = 0; cycle < 100; cycle++) {
    const view = mount(TerminalView, { terminalId: 'fixture-local' })
    await nextTick()
    const term = manager.getTerminal('fixture-local')!.term!
    document.documentElement.setAttribute('data-theme', cycle % 2 ? 'dark' : 'light')
    document.documentElement.style.setProperty('--bg-primary', cycle % 2 ? '#111111' : '#ffffff')
    document.documentElement.style.setProperty('--text-primary', cycle % 2 ? '#ffffff' : '#222222')
    manager.refreshTerminalThemes()
    term.resize(90 + cycle % 8, 12 + cycle % 4)
    view.node.style.display = 'none'
    manager.fitAllTerminals()
    assert(term.rows > 1, 'hidden terminal fit shrank the renderer')
    view.node.style.display = ''
    view.node.style.zoom = cycle % 2 ? '0.9' : '1.1'
    view.node.style.width = `${500 + cycle}px`
    manager.fitAllTerminals()
    if (cycle % 3 === 0) await delay(1)
    const snapshot = snapshots.shift()!
    view.unmount()
    snapshot({ replay: 'late snapshot', exited: false })
    await delay(1)
    assert(data.size === 0 && exits.size === 0, 'local terminal listener leaked')
  }
  await delay(150)
  records.push({ step: 'local_lifecycle', cycles: 100, theme_zoom_resize_fit_dispose: true, listeners: data.size + exits.size })

  for (let cycle = 0; cycle < 100; cycle++) {
    const executionId = `exec-${cycle}`
    const view = mount(TerminalCallBlock, { executionId, command: 'fixture command', runId: 'old-task-key', status: 'running' })
    await nextTick()
    const pending = registry.shift()!
    if (cycle % 2 === 0) {
      const event = terminalEvent(cycle + 1, { execution_id: executionId, run_id: 'actual-run', terminal_session_id: `ts-${cycle}`, data: `agent output ${cycle}\r\n` })
      for (const handler of [...feeds]) handler(event)
      await nextTick()
      ;(view.node.querySelector('button') as HTMLButtonElement).click()
      await delay(1)
      assert((controls.at(-1) as string[])[0] === 'actual-run', 'task key used for run control')
    }
    view.unmount()
    pending([{ terminal_session_id: 'late', execution_id: executionId, live: true }])
    await delay(1)
    assert(feeds.size === 0, 'agent source listener leaked')
  }
  await delay(150)
  records.push({ step: 'agent_lifecycle', cycles: 100, disposed_registry_requests: releasedRegistrySignals, listeners: feeds.size })

  // Keep a real terminal open while the production ChatPanel crosses hero/send/stream/history.
  const terminal = mount(TerminalView, { terminalId: 'fixture-local' })
  snapshots.shift()!({ replay: 'terminal kept open during chat\r\n', exited: false })
  const started = performance.now()
  for (let cycle = 0; cycle < 100; cycle++) {
    const props = ref<Record<string, any>>({ messages: [], sessions: [], projects: [], currentSession: { id: `fixture-chat-${cycle}`, storage_id: 'user', mode_version_id: null }, currentProject: null, selectedProjectId: null, modelName: 'Fixture', orchestration: null, busy: false, draft: '', permission: 'default', thinkingSteps: [], toolCalls: [], streamingReply: '' })
    const node = host()
    node.style.height = '550px'
    const app = createApp({ render: () => h(ChatPanel, props.value) })
    app.config.errorHandler = error => errors.push(String((error as Error)?.stack ?? error))
    app.use(i18n); app.mount(node)
    await nextTick()
    const message = (id: string, role: string, content: string, sequence: number) => ({ id, session_id: props.value.currentSession.id, run_id: role === 'assistant' ? 'fixture-run' : null, attachments: [], role, content, created_at: new Date().toISOString(), sequence })
    props.value.messages = [message('pending-1', 'user', '介绍项目', 1)]
    props.value.busy = true
    await nextTick()
    props.value.messages = [message('user-1', 'user', '介绍项目', 1)]
    props.value.thinkingSteps = [{ id: 'thought-1', type: 'reasoning', title: '分析项目', description: '检查项目结构', status: 'running', timestamp: '', durationMs: null, seq: 1 }]
    await nextTick()
    for (let delta = 0; delta < 10; delta++) { props.value.streamingReply = `**项目介绍**\n\n${'这是一段项目说明。'.repeat(delta + 1)}`; await nextTick() }
    props.value.messages = [...props.value.messages, message('assistant-1', 'assistant', props.value.streamingReply, 2)]
    props.value.streamingReply = ''; props.value.busy = false
    await nextTick()
    assert(node.querySelectorAll('.message-content').length >= 2, 'ChatPanel did not render the first answer')
    assert(!node.querySelector('[data-testid="activity-details"]'), 'settled activity remained mounted')
    app.unmount(); node.remove()
    if (cycle % 10 === 0) await delay(1)
  }
  await delay(150)
  terminal.unmount()
  records.push({ step: 'production_chatpanel_first_send', cycles: 100, streaming_deltas_per_cycle: 10, existing_terminal: true, elapsed_ms: Math.round(performance.now() - started), boundaries: ['ComposerBar/ChatHeader/WelcomeScreen peripheral UI', 'controller/network fixture'], settled_hidden_activity: false })

  let engine!: ReturnType<typeof provideMessageScroller>
  const owner = mount(defineComponent({ setup() { engine = provideMessageScroller({ defaultScrollPosition: 'start' }); return () => h('div') } }), {})
  const viewport = document.createElement('div')
  viewport.style.cssText = 'position:relative;width:600px;height:500px;overflow:auto;scroll-behavior:auto;overflow-anchor:none'
  const content = document.createElement('div')
  const spacer = document.createElement('div')
  viewport.append(content); document.body.append(viewport)
  const rows: HTMLElement[] = []
  const addRow = (id: string) => {
    const row = document.createElement('div')
    row.dataset.messageId = id; row.dataset.scrollAnchor = 'true'; row.style.height = '100px'; row.textContent = id
    return row
  }
  for (let i = 0; i < 5000; i++) { const row = addRow(`m${i}`); rows.push(row); content.append(row); engine.registerMessage(`m${i}`, row, null) }
  content.append(spacer)
  engine.context.setViewportElement(viewport); engine.context.setContentElement(content); engine.context.setSpacerElement(spacer); engine.context.handleContentChange()
  await delay(100)
  viewport.scrollTop = 499000
  engine.context.userScrollIntent(); engine.context.syncAfterScroll()
  await delay(100)
  let reads = 0
  const original = Element.prototype.getBoundingClientRect
  Element.prototype.getBoundingClientRect = function () { reads++; return original.call(this) }
  for (let i = 0; i < 100; i++) engine.context.syncAfterScroll()
  await delay(100)
  const scrollReads = reads
  Element.prototype.getBoundingClientRect = original
  assert(scrollReads <= 20, `5000-row scroll measured ${scrollReads} rectangles`)
  const anchor = rows[4990]
  const before = anchor.getBoundingClientRect().top
  for (let i = 99; i >= 0; i--) { const id = `older${i}`; const row = addRow(id); content.prepend(row); engine.registerMessage(id, row, null) }
  engine.context.handleContentChange()
  await delay(100)
  const after = anchor.getBoundingClientRect().top
  assert(Math.abs(after - before) <= 1, `prepend moved visible anchor by ${after - before}px`)
  // Production paging/error controls sit before the content; Chromium's native
  // overflow anchoring owns their height changes, which do not resize the content.
  viewport.style.overflowAnchor = 'auto'
  await delay(100)
  const beforeHeader = anchor.getBoundingClientRect().top
  const pagingHeader = document.createElement('div')
  pagingHeader.style.height = '120px'; pagingHeader.textContent = 'Retry older messages'
  viewport.prepend(pagingHeader)
  await delay(100)
  const withHeader = anchor.getBoundingClientRect().top
  assert(Math.abs(withHeader - beforeHeader) <= 1, `paging header moved visible anchor by ${withHeader - beforeHeader}px`)
  pagingHeader.remove()
  await delay(100)
  const withoutHeader = anchor.getBoundingClientRect().top
  assert(Math.abs(withoutHeader - beforeHeader) <= 1, `removing paging header moved visible anchor by ${withoutHeader - beforeHeader}px`)
  engine.context.syncAfterScroll(); owner.unmount(); viewport.remove()
  await delay(100)
  records.push({ step: 'production_scroller_real_geometry', rows: 5000, coalesced_scroll_events: 100, bounding_rect_reads: scrollReads, prepend_rows: 100, anchor_displacement_px: after - before, native_paging_header_add_px: withHeader - beforeHeader, native_paging_header_remove_px: withoutHeader - beforeHeader })
  manager.closeAllTerminals()
  assert(errors.length === 0, errors.join('\n'))
  scope.__terminalResult = { passed: true, real_production_components: ['TerminalView', 'TerminalCallBlock', 'useTerminal', 'useTerminalSource', 'ChatPanel', 'MessageList', 'MessageItem', 'LiveTurnBlock', 'MarkdownRender', 'TurnTimeline', 'useMessageScroller'], transport: 'isolated fixture, no real PTY/Core', xterm: '6.0.0', records, errors }
}
run().catch(error => { scope.__terminalResult = { passed: false, error: error.stack, errors, records } })
