// @vitest-environment happy-dom
import { describe, expect, it, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import type { Ref } from 'vue'
import ComposerBar from './ComposerBar.vue'
import type { ProjectDto } from '@/api'
import type { AttachableFile, PendingAttachment } from '@/lib/pendingAttachments'

vi.mock('vue-i18n', () => ({
  // Returns the key so tests can assert which entry rendered. A named-parameter call
  // (t('x', { name })) gets the interpolation appended instead of the bare key, which is
  // what lets a test check the value actually reached the label rather than only that a
  // label exists.
  useI18n: () => ({
    t: (key: string, arg?: unknown) => {
      if (typeof arg === 'string' || typeof arg === 'number') return String(arg)
      const named = arg as Record<string, unknown> | undefined
      return named && Object.keys(named).length ? `${key} ${JSON.stringify(named)}` : key
    },
  }),
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: vi.fn() }),
}))

const homeMock = vi.hoisted(() => ({
  queuedMessages: [] as Array<{ id: string; content: string }>,
  activeRuns: [] as Array<{ id: string; status: string }>,
  steerQueued: vi.fn(),
  promoteQueued: vi.fn(),
  editQueued: vi.fn(),
  dismissQueued: vi.fn(),
  updateDraft: vi.fn(),
  sendMessage: vi.fn(async () => {}),
  createSession: vi.fn(async () => {}),
}))

const dispatchMock = vi.hoisted(() => ({
  getDispatchPref: vi.fn(() => 'queued' as 'queued' | 'parallel' | 'ask'),
}))

const apiMock = vi.hoisted(() => ({
  api: {
    listAgentModeTopologies: vi.fn(async () => [] as import('@/api').AgentModeTopologyDto[]),
    listDirectory: vi.fn(async (_cwd: string, _dir: string) => ({ data: { entries: [] as unknown[] } })),
  },
}))

vi.mock('@/controllers/HomeController', () => ({
  homeController: homeMock,
}))

/**
 * The composer's half of the attachment slice: the strip's state and its upload live in
 * lib/pendingAttachments, which is covered against the real api in its own test. Stubbing
 * the module here also keeps the @/api mock above from growing upload routes that no
 * case in this file exercises.
 *
 * The ref is created inside the async factory because vi.hoisted runs before imports, so
 * `ref` is not reachable there — assigning through the container is the seam.
 */
const attachMock = vi.hoisted(() => ({
  list: null as unknown as Ref<PendingAttachment[]>,
  attachFiles: vi.fn(async (_files: readonly AttachableFile[], _sessionId: string | null) => {}),
  removePendingAttachment: vi.fn(async (_clientId: string) => {}),
  reconcileSession: vi.fn(),
  formatAttachmentBytes: vi.fn((bytes: number) => `${bytes}B`),
  readyAttachmentCount: vi.fn(() =>
    // The same rule the real module applies, re-expressed here so a composer case can put a
    // chip in either state. Whether the rule is the right one is lib/pendingAttachments.test.ts.
    (attachMock.list?.value ?? []).filter((item) => item.status === 'ready' && item.storedRow !== null).length,
  ),
  MAX_ATTACHMENT_BYTES: 33_554_432,
  TOO_LARGE_CODE: 'attachment_too_large',
}))

vi.mock('@/lib/pendingAttachments', async () => {
  const { ref } = await import('vue')
  attachMock.list = ref<PendingAttachment[]>([])
  return {
    pendingAttachments: attachMock.list,
    attachFiles: attachMock.attachFiles,
    removePendingAttachment: attachMock.removePendingAttachment,
    reconcileSession: attachMock.reconcileSession,
    formatAttachmentBytes: attachMock.formatAttachmentBytes,
    readyAttachmentCount: attachMock.readyAttachmentCount,
    MAX_ATTACHMENT_BYTES: attachMock.MAX_ATTACHMENT_BYTES,
    TOO_LARGE_CODE: attachMock.TOO_LARGE_CODE,
  }
})

vi.mock('@/lib/dispatchPref', () => ({
  getDispatchPref: dispatchMock.getDispatchPref,
}))

vi.mock('@/api', () => apiMock)

function mountComposer(props: Partial<{
  busy: boolean
  canStop: boolean
  modelValue: string
  modeVersionId: string | null
  projects: ProjectDto[]
  selectedProjectId: string | null
  sessionId: string | null
  hero: boolean
}> = {}) {
  return mount(ComposerBar, {
    props: {
      busy: false,
      modelValue: '',
      permission: 'default',
      ...props,
    },
  })
}

describe('ComposerBar Codex shell contract', () => {
  it('idle composer reports data-composer-active=false', async () => {
    const wrapper = mountComposer()
    await flushPromises()
    expect(wrapper.find('.welcome-dialog').attributes('data-composer-active')).toBe('false')
    wrapper.unmount()
  })

  it('a non-empty draft flips the shell to active', async () => {
    const wrapper = mountComposer({ modelValue: 'hello' })
    await flushPromises()
    expect(wrapper.find('.welcome-dialog').attributes('data-composer-active')).toBe('true')
    wrapper.unmount()
  })

  it('busy state shows the spinner and keeps the shell active', async () => {
    const wrapper = mountComposer({ busy: true, modelValue: 'working' })
    await flushPromises()
    expect(wrapper.find('.welcome-dialog').attributes('data-composer-active')).toBe('true')
    expect(wrapper.find('.composer-send-spinner').exists()).toBe(true)
    wrapper.unmount()
    document.body.innerHTML = ''
  })
})

describe('ComposerBar hero variant (start page)', () => {
  it('renders mode selector and project trigger, and no queued cards', async () => {
    const wrapper = mount(ComposerBar, {
      props: {
        hero: true,
        busy: false,
        modelValue: '',
        mode: 'plan',
        permission: 'default',
        projects: [{ id: 'p1', name: 'Proj One' } as never],
        selectedProjectId: 'p1',
      },
    })
    await flushPromises()
    expect(wrapper.find('.mode-selector-trigger').exists()).toBe(true)
    expect(wrapper.find('.project-dropdown-trigger').exists()).toBe(true)
    expect(wrapper.find('.project-dropdown-label').text()).toContain('Proj One')
    expect(wrapper.find('.composer-queued').exists()).toBe(false)
    expect(wrapper.find('.composer--hero').exists()).toBe(true)
    wrapper.unmount()
    document.body.innerHTML = ''
  })

  it('enter key emits the welcome payload with content instead of dispatch submit', async () => {
    const wrapper = mount(ComposerBar, {
      props: {
        hero: true,
        busy: false,
        modelValue: 'hello world',
        permission: 'default',
      },
    })
    await flushPromises()
    await wrapper.find('.welcome-dialog-input').trigger('keydown', { key: 'Enter' })
    const emitted = wrapper.emitted('welcome-submit')
    expect(emitted).toHaveLength(1)
    // 六值 agent_mode 已从契约删除：欢迎发送只带内容、权限与（可空的）模式版本。
    expect(emitted![0]![0]).toEqual({ content: 'hello world', permission_mode: 'default', mode_version_id: null })
    expect(wrapper.emitted('submit')).toBeUndefined()
    wrapper.unmount()
    document.body.innerHTML = ''
  })

  it('docked ask dispatch teleports its menu to body (never clipped by the dialog)', async () => {
    dispatchMock.getDispatchPref.mockReturnValue('ask')
    const wrapper = mountComposer({ modelValue: 'hello' })
    await flushPromises()
    await wrapper.find('.welcome-dialog-send').trigger('click')
    await flushPromises()
    expect(document.querySelector('.ask-menu')).not.toBeNull()
    expect(wrapper.find('.ask-menu').exists()).toBe(false)
    wrapper.unmount()
    document.body.innerHTML = ''
    dispatchMock.getDispatchPref.mockReturnValue('queued')
  })
})

/**
 * pack 安装的 conversation.* 模式带 application_mode，工作区自建拓扑不带。
 * 前者由「对话模式」组承载（显示名取自这里），后者才进「工作区拓扑」组。
 */
const CONVERSATION_MODE = {
  id: 'am-conv-auto',
  display_name: '自动 (Auto)',
  status: 'published',
  application_mode: 'auto',
  latest_published_mode_version_id: 'mv-conv-auto',
  nodes: [],
  edges: [],
}
const WORKSPACE_TOPOLOGY = {
  id: 'am-custom',
  display_name: '自建评审流',
  status: 'published',
  application_mode: null,
  latest_published_mode_version_id: 'mv-custom-1',
  nodes: [],
  edges: [],
}

describe('ComposerBar mode selector (single source: the published modes)', () => {
  it('lists exactly the published modes, deduplicated by display name, and drops the rest', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([
      CONVERSATION_MODE,
      WORKSPACE_TOPOLOGY,
      // 同名的第二行（bootstrap + 包安装各一条）：按 display_name 去重。
      { ...CONVERSATION_MODE, id: 'am-conv-auto-2' },
      { id: 'am-draft', display_name: '草稿模式', status: 'draft', latest_published_mode_version_id: null, nodes: [], edges: [] },
      { id: 'am-no-version', display_name: '无版本模式', status: 'published', latest_published_mode_version_id: null, nodes: [], edges: [] },
    ] as never)
    const wrapper = mountComposer({})
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const items = [...document.querySelectorAll('.mode-selector-item')].map(b => b.textContent!.trim())
    expect(items[0]).toBe('chat.followDefault')
    // 已发布的去重后恰好两项；draft 与没有已发布版本的项都不出现。
    expect(items.filter(t => t.includes('自动 (Auto)'))).toHaveLength(1)
    expect(items.some(t => t.includes('自建评审流'))).toBe(true)
    expect(items.some(t => t.includes('草稿模式'))).toBe(false)
    expect(items.some(t => t.includes('无版本模式'))).toBe(false)
    expect(items).toHaveLength(3)
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })

  it('lists only follow-default when the workspace has no published mode', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
    const wrapper = mountComposer({})
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const items = [...document.querySelectorAll('.mode-selector-item')].map(b => b.textContent!.trim())
    expect(items).toEqual(['chat.followDefault'])
    // 没有可选项时不渲染分组标题。
    expect(document.querySelectorAll('.mode-selector-group')).toHaveLength(0)
    wrapper.unmount()
    document.body.innerHTML = ''
  })

  it('degrades to follow-default when the gateway is offline', async () => {
    apiMock.api.listAgentModeTopologies.mockRejectedValue(new Error('offline'))
    const wrapper = mountComposer({})
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const items = [...document.querySelectorAll('.mode-selector-item')].map(b => b.textContent!.trim())
    expect(items).toEqual(['chat.followDefault'])
    expect(wrapper.emitted('update:modeVersionId')).toBeUndefined()
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })

  it('selecting a mode emits its mode_version_id; follow-default clears it', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([WORKSPACE_TOPOLOGY, CONVERSATION_MODE] as never)
    const wrapper = mountComposer({})
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const items = [...document.querySelectorAll('.mode-selector-item')]
    ;(items.find(b => b.textContent!.includes('自建评审流')) as HTMLElement).click()
    await flushPromises()
    expect(wrapper.emitted('update:modeVersionId')![0]![0]).toBe('mv-custom-1')
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    ;(document.querySelector('.mode-selector-item') as HTMLElement).click()
    await flushPromises()
    expect(wrapper.emitted('update:modeVersionId')![1]![0]).toBeNull()
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })

  it('a stale modeVersionId marks the trigger and activates only follow-default', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([WORKSPACE_TOPOLOGY] as never)
    const wrapper = mountComposer({ modeVersionId: 'mv-gone' })
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const active = [...document.querySelectorAll('.mode-selector-item.active')].map(b => b.textContent!.trim())
    expect(active).toEqual(['chat.followDefault'])
    expect(wrapper.find('.mode-selector-stale').exists()).toBe(true)
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })

  it('selecting the bound mode highlights it and shows its display name', async () => {
    apiMock.api.listAgentModeTopologies.mockResolvedValue([WORKSPACE_TOPOLOGY] as never)
    const wrapper = mountComposer({ modeVersionId: 'mv-custom-1' })
    await flushPromises()
    await wrapper.find('.mode-selector-trigger').trigger('click')
    await flushPromises()
    const active = [...document.querySelectorAll('.mode-selector-item.active')].map(b => b.textContent!.trim())
    expect(active).toEqual(['自建评审流'])
    expect(wrapper.find('.mode-selector-label').text()).toBe('自建评审流')
    wrapper.unmount()
    document.body.innerHTML = ''
    apiMock.api.listAgentModeTopologies.mockResolvedValue([])
  })
})

describe('ComposerBar slash commands', () => {
  async function pressEnter(wrapper: ReturnType<typeof mountComposer>) {
    await wrapper.find('textarea').trigger('keydown', { key: 'Enter', shiftKey: false })
    await flushPromises()
  }

  afterEach(() => {
    // homeMock is module-level: without this, "not.toHaveBeenCalled" reads the
    // previous test's call.
    vi.clearAllMocks()
    document.body.innerHTML = ''
  })

  it('offers commands as soon as the line starts with a slash', async () => {
    const wrapper = mountComposer({ canStop: true, modelValue: '/' })
    await flushPromises()
    expect(document.querySelectorAll('[data-testid="composer-commands"] li')).toHaveLength(4)
    wrapper.unmount()
  })

  it('hides /stop when there is nothing to stop', async () => {
    const wrapper = mountComposer({ canStop: false, modelValue: '/st' })
    await flushPromises()
    expect(document.querySelector('[data-testid="composer-command-run.stop"]')).toBeNull()
    wrapper.unmount()
  })

  it('completes a half-typed argument command instead of sending it', async () => {
    const wrapper = mountComposer({ modelValue: '/queue' })
    await flushPromises()
    await pressEnter(wrapper)
    // The literal "/queue" must never reach the model as a prompt.
    expect(homeMock.sendMessage).not.toHaveBeenCalled()
    expect(wrapper.emitted('submit')).toBeUndefined()
    expect(homeMock.updateDraft).toHaveBeenCalledWith('/queue ')
    wrapper.unmount()
  })

  it('sends the payload of /queue as a one-shot queued dispatch', async () => {
    const wrapper = mountComposer({ modelValue: '/queue 帮我跑测试', modeVersionId: 'mv-1' })
    await flushPromises()
    await pressEnter(wrapper)
    expect(homeMock.updateDraft).toHaveBeenCalledWith('帮我跑测试')
    expect(homeMock.sendMessage).toHaveBeenCalledWith({
      dispatch_mode: 'queued',
      target_run_id: null,
      mode_version_id: 'mv-1',
      meeting_model_override: null,
    })
    wrapper.unmount()
  })

  it('leaves prose that merely starts with a slash on the normal send path', async () => {
    const wrapper = mountComposer({ modelValue: '/etc/hosts 里这一行是什么意思' })
    await flushPromises()
    await pressEnter(wrapper)
    expect(homeMock.sendMessage).not.toHaveBeenCalled()
    expect(wrapper.emitted('submit')).toBeTruthy()
    wrapper.unmount()
  })
})

describe('ComposerBar @ path completion', () => {
  const project = {
    id: 'p1',
    name: 'demo',
    path: 'C:/ws/demo',
    created_at: '2026-09-21T00:00:00Z',
  } as ProjectDto

  const rootEntries = [
    { name: 'README.md', type: 'file', size: 10 },
    { name: 'src', type: 'directory', size: 0 },
  ]
  const srcEntries = [{ name: 'main.ts', type: 'file', size: 5 }]

  function mockLs() {
    apiMock.api.listDirectory = vi.fn(async (_cwd: string, dir: string) => ({
      data: { entries: dir === '.' ? rootEntries : dir === 'src' ? srcEntries : [] },
    }))
  }

  afterEach(() => {
    vi.clearAllMocks()
    document.body.innerHTML = ''
  })

  it('lists the workspace root through ls when an @ appears', async () => {
    mockLs()
    const wrapper = mountComposer({ modelValue: '@', projects: [project], selectedProjectId: 'p1' })
    await flushPromises()
    expect(apiMock.api.listDirectory).toHaveBeenCalledWith('C:/ws/demo', '.')
    expect(document.querySelectorAll('[data-testid="composer-mentions"] li')).toHaveLength(2)
    wrapper.unmount()
  })

  it('descends into a directory instead of stopping at the first segment', async () => {
    mockLs()
    const wrapper = mountComposer({ modelValue: '@src/', projects: [project], selectedProjectId: 'p1' })
    await flushPromises()
    expect(apiMock.api.listDirectory).toHaveBeenCalledWith('C:/ws/demo', 'src')
    expect(document.querySelector('[data-testid="composer-mention-main.ts"]')).not.toBeNull()
    wrapper.unmount()
  })

  it('completes with Enter rather than sending a half-typed path', async () => {
    mockLs()
    const wrapper = mountComposer({ modelValue: '@RE', projects: [project], selectedProjectId: 'p1' })
    await flushPromises()
    await wrapper.find('textarea').trigger('keydown', { key: 'Enter', shiftKey: false })
    await flushPromises()
    expect(wrapper.emitted('submit')).toBeUndefined()
    expect(homeMock.sendMessage).not.toHaveBeenCalled()
    expect(homeMock.updateDraft).toHaveBeenCalledWith('@README.md ')
    wrapper.unmount()
  })

  it('says nothing and asks for nothing when no workspace is selected', async () => {
    mockLs()
    const wrapper = mountComposer({ modelValue: '@' })
    await flushPromises()
    expect(apiMock.api.listDirectory).not.toHaveBeenCalled()
    expect(document.querySelector('[data-testid="composer-mentions"]')).toBeNull()
    wrapper.unmount()
  })
})

/**
 * The plus menu used to emit add-image / add-file and nothing in the repo listened, so
 * the two entries looked like a feature and did nothing. These cases pin the replacement
 * to the three things a stub could still get wrong in silence: which picker opens with
 * which filter, which session the bytes are handed to, and whether the strip reflects
 * the uploader's state instead of the composer's local copy.
 */
describe('ComposerBar attachments', () => {
  function row(overrides: Partial<PendingAttachment> & { clientId: string }): PendingAttachment {
    return {
      fileName: 'notes.txt',
      size: 2048,
      mediaType: 'text/plain',
      status: 'ready',
      attachmentId: 'row-1',
      errorCode: null,
      storedRow: null,
      ...overrides,
    }
  }

  async function openPlusMenu(wrapper: ReturnType<typeof mountComposer>) {
    await wrapper.find('.welcome-dialog-plus').trigger('click')
    await flushPromises()
    return {
      image: document.querySelector('[data-testid="composer-attach-image"]') as HTMLButtonElement,
      file: document.querySelector('[data-testid="composer-attach-file"]') as HTMLButtonElement,
    }
  }

  function fileInput(wrapper: ReturnType<typeof mountComposer>): HTMLInputElement {
    return wrapper.find('[data-testid="composer-file-input"]').element as HTMLInputElement
  }

  afterEach(() => {
    attachMock.list.value = []
    vi.clearAllMocks()
    document.body.innerHTML = ''
  })

  it('opens the picker from the file entry with no type filter', async () => {
    const wrapper = mountComposer({ sessionId: 'sess-1' })
    await flushPromises()
    const input = fileInput(wrapper)
    const clickSpy = vi.spyOn(input, 'click')
    const { file } = await openPlusMenu(wrapper)
    expect(file.disabled).toBe(false)
    file.click()
    expect(clickSpy).toHaveBeenCalledTimes(1)
    expect(input.accept).toBe('')
    wrapper.unmount()
  })

  it('narrows the image entry to images', async () => {
    const wrapper = mountComposer({ sessionId: 'sess-1' })
    await flushPromises()
    const input = fileInput(wrapper)
    const clickSpy = vi.spyOn(input, 'click')
    const { image } = await openPlusMenu(wrapper)
    expect(image.disabled).toBe(false)
    image.click()
    expect(clickSpy).toHaveBeenCalledTimes(1)
    expect(input.accept).toBe('image/*')
    wrapper.unmount()
  })

  it('refuses to offer a picker before a session exists, and says why', async () => {
    const wrapper = mountComposer()
    await flushPromises()
    const input = fileInput(wrapper)
    const clickSpy = vi.spyOn(input, 'click')
    const { file } = await openPlusMenu(wrapper)
    expect(file.disabled).toBe(true)
    expect(file.title).toContain('composer.attachNeedsSession')
    file.click()
    expect(clickSpy).not.toHaveBeenCalled()
    expect(attachMock.reconcileSession).toHaveBeenCalledWith(null)
    wrapper.unmount()
  })

  it('hands the picked selection to the uploader under the current session', async () => {
    const wrapper = mountComposer({ sessionId: 'sess-1' })
    await flushPromises()
    const input = fileInput(wrapper)
    Object.defineProperty(input, 'files', {
      value: [{ name: 'a.txt', type: 'text/plain', size: 3 }],
      configurable: true,
    })
    await wrapper.find('[data-testid="composer-file-input"]').trigger('change')
    expect(attachMock.attachFiles).toHaveBeenCalledTimes(1)
    const [picked, session] = attachMock.attachFiles.mock.calls[0]!
    expect(picked[0]!.name).toBe('a.txt')
    expect(session).toBe('sess-1')
    wrapper.unmount()
  })

  it('releases the strip when the composer moves to another session', async () => {
    const wrapper = mountComposer({ sessionId: 'sess-1' })
    await flushPromises()
    await wrapper.setProps({ sessionId: 'sess-2' })
    expect(attachMock.reconcileSession).toHaveBeenLastCalledWith('sess-2')
    wrapper.unmount()
  })

  it('renders one chip per pending row with its size, state and named remove', async () => {
    attachMock.list.value = [
      row({ clientId: 'c1' }),
      row({ clientId: 'c2', fileName: 'shot.png', mediaType: 'image/png', status: 'uploading', attachmentId: null }),
      row({ clientId: 'c3', fileName: 'big.bin', status: 'failed', attachmentId: null, errorCode: 'attachment_too_large' }),
    ]
    const wrapper = mountComposer({ sessionId: 'sess-1' })
    await flushPromises()
    const chips = wrapper.findAll('[data-testid="attachment-chip"]')
    expect(chips).toHaveLength(3)
    expect(chips[0]!.text()).toContain('notes.txt')
    expect(chips[0]!.text()).toContain('2048B')
    expect(chips[0]!.find('.attachment-remove').attributes('aria-label')).toContain('notes.txt')
    // A ready row says nothing: the absence of a state line is the confirmation.
    expect(chips[0]!.find('.attachment-state').exists()).toBe(false)
    expect(chips[1]!.text()).toContain('composer.attachUploading')
    expect(chips[1]!.find('.attachment-spinner').attributes('aria-hidden')).toBe('true')
    expect(chips[2]!.classes()).toContain('is-failed')
    // The ceiling travels from the module constant into the label rather than a literal.
    expect(chips[2]!.text()).toContain('composer.attachTooLarge')
    expect(chips[2]!.text()).toContain('33554432B')
    expect(wrapper.find('.composer-attachments').attributes('aria-live')).toBe('polite')
    wrapper.unmount()
  })

  it('removes a chip through the module instead of editing a local copy', async () => {
    attachMock.list.value = [row({ clientId: 'c1' })]
    const wrapper = mountComposer({ sessionId: 'sess-1' })
    await flushPromises()
    await wrapper.findAll('[data-testid="attachment-chip"]')[0]!.find('.attachment-remove').trigger('click')
    expect(attachMock.removePendingAttachment).toHaveBeenCalledWith('c1')
    wrapper.unmount()
  })

  it('shows no strip at all while nothing is pending', async () => {
    const wrapper = mountComposer({ sessionId: 'sess-1' })
    await flushPromises()
    expect(wrapper.find('.composer-attachments').exists()).toBe(false)
    wrapper.unmount()
  })

  /**
   * A file with no words used to be unsendable: Send read the draft alone, so the user had
   * to invent a sentence to hang the upload on. Core now appends such a turn and starts no
   * run, so the button's rule is "text or a finished upload" - and an upload still in flight
   * must NOT unlock it, because it names no stored row yet.
   */
  describe('Send unlocks for an attachment-only turn', () => {
    function withStoredRow(clientId: string): PendingAttachment {
      return row({ clientId, storedRow: { id: 'row-1' } as PendingAttachment['storedRow'] })
    }

    it('stays disabled while the draft is empty and nothing is pending', async () => {
      attachMock.list.value = []
      const wrapper = mountComposer({ sessionId: 'sess-1', modelValue: '' })
      await flushPromises()
      expect(wrapper.find('.welcome-dialog-send').attributes('disabled')).toBeDefined()
      wrapper.unmount()
    })

    it('enables and emits submit on a ready chip with no text', async () => {
      attachMock.list.value = [withStoredRow('r1')]
      const wrapper = mountComposer({ sessionId: 'sess-1', modelValue: '' })
      await flushPromises()

      const send = wrapper.find('.welcome-dialog-send')
      expect(send.attributes('disabled')).toBeUndefined()
      await send.trigger('click')
      expect(wrapper.emitted('submit')).toHaveLength(1)
      wrapper.unmount()
      attachMock.list.value = []
    })

    it('stays disabled while the only chip is still uploading', async () => {
      attachMock.list.value = [row({ clientId: 'u1', status: 'uploading', attachmentId: null })]
      const wrapper = mountComposer({ sessionId: 'sess-1', modelValue: '' })
      await flushPromises()

      const send = wrapper.find('.welcome-dialog-send')
      expect(send.attributes('disabled')).toBeDefined()
      await send.trigger('click')
      expect(wrapper.emitted('submit')).toBeUndefined()
      wrapper.unmount()
      attachMock.list.value = []
    })
  })
})
