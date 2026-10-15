// @vitest-environment happy-dom
import { createI18n } from 'vue-i18n'
import zh from '@/locales/zh-CN'
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import WorkspaceEditorDialog from './WorkspaceEditorDialog.vue'
const h = vi.hoisted(() => ({ preview: vi.fn(), read: vi.fn(), complete: vi.fn(), pick: vi.fn() }))
vi.mock('@/controllers/HomeController', async () => {
  const { ref } = await import('vue')
  return { homeController: { workspaceEditor: ref<{ open: boolean; projectKey: string | null }>({ open: false, projectKey: null }), completeWorkspace: h.complete } }
})
import { homeController } from '@/controllers/HomeController'
const editor = homeController.workspaceEditor
vi.mock('@/api', () => ({ api: { previewWorkspace: h.preview, readWorkspace: h.read } }))
vi.mock('@/composables/usePanelStyles', () => ({ usePanelStyles: () => ({ getPanelStyle: () => ({}), getPanelDataAttributes: () => ({}) }) }))
const previousHost = window.tinadec
beforeEach(() => {
  h.preview.mockReset().mockResolvedValue({ exists: false }); h.read.mockReset(); h.complete.mockReset().mockResolvedValue(undefined); h.pick.mockReset()
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { selectWorkspaceFolders: h.pick } })
  editor.value = { open: false, projectKey: null }
})
afterEach(() => Object.defineProperty(window, 'tinadec', { configurable: true, value: previousHost }))
function buttons(text: string) { return Array.from(document.body.querySelectorAll<HTMLButtonElement>('.workspace-dialog button')).filter(button => button.textContent?.trim() === text) }
async function open() { const wrapper = mount(WorkspaceEditorDialog, { attachTo: document.body, global: { plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })] } }); editor.value = { open: true, projectKey: null }; await flushPromises(); return wrapper }
async function add(paths: string[]) { h.pick.mockResolvedValueOnce(paths); buttons('添加源文件夹')[0]!.click(); await flushPromises() }
describe('workspace editor', () => {
  it('selects multiple folders without initializing, retains a custom name, and cancels without creating', async () => {
    const wrapper = await open(); await add(['C:/first', 'C:/second'])
    expect(h.preview).toHaveBeenCalledTimes(2); expect(h.complete).not.toHaveBeenCalled()
    const input = document.querySelector<HTMLInputElement>('.workspace-dialog input')!; expect(input.value).toBe('first')
    input.value = 'Custom'; input.dispatchEvent(new Event('input', { bubbles: true })); await flushPromises()
    buttons('设为主要')[0]!.click(); await flushPromises(); expect(input.value).toBe('Custom')
    expect(document.querySelectorAll('.workspace-source-list li')).toHaveLength(2)
    buttons('取消')[0]!.click(); await flushPromises(); expect(editor.value.open).toBe(false); expect(h.complete).not.toHaveBeenCalled(); wrapper.unmount()
  })
  it('requires an explicit existing-workspace open and merges repeated submits', async () => {
    const saved = { name: 'Saved', roots: [{ id: 'main', path: 'C:/saved' }], primary_root_id: 'main', icon: 'code', color: 'blue', content_hash: 'hash' }
    h.preview.mockResolvedValueOnce({ exists: true, workspace: saved, project_path: 'C:/saved', storage_root: 'C:/saved/.tinadec' })
    const wrapper = await open(); await add(['C:/saved']); expect(h.complete).not.toHaveBeenCalled()
    let finish!: () => void; h.complete.mockImplementationOnce(() => new Promise<void>(resolve => { finish = resolve }))
    buttons('打开已有工作区')[0]!.click(); await flushPromises()
    document.querySelector('.workspace-dialog form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); await flushPromises()
    expect(h.complete).toHaveBeenCalledExactlyOnceWith(saved, null, '', 'C:/saved'); finish(); await flushPromises(); wrapper.unmount()
  })
  it('rejects duplicate folders and never removes the last or primary reference', async () => {
    const wrapper = await open(); await add(['C:/first']); await add(['c:/FIRST/'])
    expect(document.querySelectorAll('.workspace-source-list li')).toHaveLength(1)
    expect(document.querySelector('.workspace-error')?.textContent).toContain('文件夹已添加')
    expect(document.querySelector<HTMLButtonElement>('[aria-label="移除 first"]')?.disabled).toBe(true); expect(h.complete).not.toHaveBeenCalled(); wrapper.unmount()
  })
  it('keeps an edited draft after a failed save and a read retry, showing diagnostics', async () => {
    const { ApiError } = await import('@/lib/apiError')
    const saved = { name: 'Saved', roots: [{ id: 'main', path: 'C:/saved' }], primary_root_id: 'main', icon: 'code', color: 'blue', content_hash: 'hash' }
    h.read.mockResolvedValue(saved)
    const wrapper = mount(WorkspaceEditorDialog, { attachTo: document.body, global: { plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })] } })
    editor.value = { open: true, projectKey: 'scope::project' }; await flushPromises()
    const input = document.querySelector<HTMLInputElement>('.workspace-dialog input')!
    input.value = 'My draft'; input.dispatchEvent(new Event('input', { bubbles: true })); await flushPromises()
    h.complete.mockRejectedValueOnce(new ApiError('Configuration changed', 412, { code: 'configuration_conflict', actions: ['reload'], diagnostics: [{ code: 'name', severity: 'error', message: 'name conflict', line: 2 }], trace_id: 'trace-save' }))
    buttons('保存')[0]!.click(); await flushPromises()
    expect(document.querySelector('.workspace-error')?.textContent).toContain('name conflict')
    expect(document.querySelector('.workspace-error')?.textContent).toContain('trace-save')
    document.querySelector<HTMLButtonElement>('.workspace-error-actions button')!.click(); await flushPromises()
    expect(h.read).toHaveBeenCalledTimes(2)
    expect(input.value).toBe('My draft')
    expect(h.complete).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })

})
