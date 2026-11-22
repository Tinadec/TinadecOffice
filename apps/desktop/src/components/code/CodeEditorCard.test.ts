// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { ref } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import CodeEditorCard from './CodeEditorCard.vue'

const doc = vi.hoisted(() => ({
  editorPath: null as unknown,
  editorFocus: null as unknown,
  openInEditor: vi.fn((_input: { path: string; cwd?: string | null }) => true),
  closeEditor: vi.fn(),
  syncDocumentWorkspace: vi.fn(),
}))

const dock = vi.hoisted(() => ({ focusDockCard: vi.fn() }))
vi.mock('@/lib/homeDockSync', () => ({ focusDockCard: dock.focusDockCard }))

const controller = vi.hoisted(() => ({
  selectedProjectId: { value: 'p1' as string | null },
  currentProject: { value: { path: '/w' } as { path: string } | null },
  approvals: { value: [{ id: 'a1', status: 'pending' }] as unknown[] },
  selectedSessionId: { value: 's1' as string | null },
  recordApproval: vi.fn(),
}))

vi.mock('@/lib/documentWorkspace', () => ({
  get editorPath() { return doc.editorPath },
  get editorFocus() { return doc.editorFocus },
  openInEditor: doc.openInEditor,
  closeEditor: doc.closeEditor,
  syncDocumentWorkspace: doc.syncDocumentWorkspace,
}))

vi.mock('@/controllers/HomeController', () => ({ homeController: controller }))

const files = vi.hoisted(() => ({ listWorkspaceDir: vi.fn() }))
vi.mock('@/lib/workspaceFiles', () => ({ listWorkspaceDir: files.listWorkspaceDir }))

const PaneStub = {
  props: ['path', 'cwd', 'line', 'revealNonce', 'approvals', 'selectedSessionId'],
  emits: ['close', 'select', 'saved', 'approval-requested'],
  template: '<div class="pane-stub" :data-path="path ?? \'\'" :data-line="line ?? \'\'" @click="$emit(\'select\', \'docs/next.md\')" />',
}

function render() {
  return mount(CodeEditorCard, {
    global: {
      plugins: [createI18n({
        legacy: false,
        locale: 'en',
        messages: { en: { context: { homeCodeEditor: 'Code editor' } } },
      })],
      stubs: {
        DocumentEditorPane: PaneStub,
        SearchPanel: { template: '<div data-testid="search-panel-stub" />' },
      },
    },
  })
}

beforeEach(() => {
  doc.editorPath = ref(null)
  doc.editorFocus = ref(null)
  doc.openInEditor.mockClear()
  doc.closeEditor.mockClear()
  doc.syncDocumentWorkspace.mockClear()
  dock.focusDockCard.mockClear()
  // An unknown listing must never close the file: "not listed yet" is not "missing".
  files.listWorkspaceDir.mockReset()
  files.listWorkspaceDir.mockResolvedValue(null)
  controller.currentProject.value = { path: '/w' }
})

describe('CodeEditorCard', () => {
  it('binds the workspace on mount and starts on the file browser', () => {
    const wrapper = render()
    expect(doc.syncDocumentWorkspace).toHaveBeenCalledWith('/w')
    expect(wrapper.get('.pane-stub').attributes('data-path')).toBe('')
    wrapper.unmount()
  })

  it('passes the file on screen, with the reference line, to the editor pane', () => {
    doc.editorPath = ref('docs/spec.md')
    doc.editorFocus = ref({ path: 'docs/spec.md', line: 12, nonce: 2 })
    const wrapper = render()

    expect(wrapper.get('.pane-stub').attributes('data-path')).toBe('docs/spec.md')
    expect(wrapper.get('.pane-stub').attributes('data-line')).toBe('12')
    wrapper.unmount()
  })

  it('ignores a reveal that belongs to another file', () => {
    doc.editorPath = ref('docs/spec.md')
    doc.editorFocus = ref({ path: 'src/app.ts', line: 12, nonce: 2 })
    const wrapper = render()

    expect(wrapper.get('.pane-stub').attributes('data-line')).toBe('')
    wrapper.unmount()
  })

  it('opens the file picked in the tree inside this card', async () => {
    const wrapper = render()
    await wrapper.get('.pane-stub').trigger('click')

    expect(doc.openInEditor).toHaveBeenCalledWith({ path: 'docs/next.md', cwd: '/w' })
    wrapper.unmount()
  })

  it('closes the file from the pane header', async () => {
    doc.editorPath = ref('docs/spec.md')
    const wrapper = render()
    wrapper.findComponent(PaneStub).vm.$emit('close')
    await wrapper.vm.$nextTick()

    expect(doc.closeEditor).toHaveBeenCalled()
    wrapper.unmount()
  })

  it('falls back to the browser when the remembered file no longer exists', async () => {
    files.listWorkspaceDir.mockResolvedValue([{ name: 'other.md', isDir: false, size: null, modifiedAt: null }])
    doc.editorPath = ref('docs/gone.md')
    render()
    await flushPromises()

    expect(files.listWorkspaceDir).toHaveBeenCalledWith('/w', 'docs')
    expect(doc.closeEditor).toHaveBeenCalled()
  })

  it('keeps the remembered file when the folder listing fails', async () => {
    files.listWorkspaceDir.mockResolvedValue(null)
    doc.editorPath = ref('docs/spec.md')
    render()
    await flushPromises()

    expect(doc.closeEditor).not.toHaveBeenCalled()
  })

  it('toggles inline workspace SearchPanel from the card toolbar', async () => {
    const wrapper = render()
    expect(wrapper.find('[data-testid="code-card-search-toggle"]').exists()).toBe(true)
    expect(wrapper.find('.pane-stub').exists()).toBe(true)

    await wrapper.get('[data-testid="code-card-search-toggle"]').trigger('click')
    expect(wrapper.find('.pane-stub').exists()).toBe(false)
    expect(wrapper.find('[data-testid="search-panel-stub"]').exists()).toBe(true)

    await wrapper.get('[data-testid="code-card-search-toggle"]').trigger('click')
    expect(wrapper.find('.pane-stub').exists()).toBe(true)
    wrapper.unmount()
  })

  it('routes active file Diff, File History, and Blame actions to the Git dock card', async () => {
    doc.editorPath = ref('src/app.ts')
    const wrapper = render()
    expect(wrapper.get('[data-testid="code-card-breadcrumb"]').text()).toBe('src/app.ts')

    await wrapper.get('[data-testid="code-git-diff-btn"]').trigger('click')
    expect(dock.focusDockCard).toHaveBeenLastCalledWith({
      card: 'git',
      reason: 'code',
      filePath: 'src/app.ts',
      gitTab: 'changes',
      gitSubView: 'diff',
    })

    await wrapper.get('[data-testid="code-git-history-btn"]').trigger('click')
    expect(dock.focusDockCard).toHaveBeenLastCalledWith({
      card: 'git',
      reason: 'code',
      filePath: 'src/app.ts',
      gitTab: 'changes',
      gitSubView: 'file_history',
    })

    await wrapper.get('[data-testid="code-git-blame-btn"]').trigger('click')
    expect(dock.focusDockCard).toHaveBeenLastCalledWith({
      card: 'git',
      reason: 'code',
      filePath: 'src/app.ts',
      gitTab: 'changes',
      gitSubView: 'blame',
    })

    wrapper.unmount()
  })
})
