// @vitest-environment happy-dom
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import { createI18n } from 'vue-i18n'
import StorageSection from './StorageSection.vue'
import zh from '@/locales/zh-CN'
import { ApiError } from '@/lib/apiError'
import { setHostAccessStatus } from '@/lib/hostAccess'

const mocks = vi.hoisted(() => ({
  listStorageScopes: vi.fn(), getStorageStats: vi.fn(), getStorageDiagnostics: vi.fn(), getConfigurationDocument: vi.fn(),
  validateConfigurationDocument: vi.fn(), saveConfigurationDocument: vi.fn(), configureStorage: vi.fn(),
  previewStorageCleanup: vi.fn(), cleanupStorage: vi.fn(), confirm: vi.fn(async () => true),
  previewContentCollection: vi.fn(), collectContent: vi.fn(), previewStorageDeletion: vi.fn(), deleteStorage: vi.fn(),
  closeStorageScope: vi.fn(), hostAction: vi.fn(),
}))
vi.mock('@/api', () => ({ api: mocks }))
vi.mock('@/composables/useNotifications', async importOriginal => ({ ...(await importOriginal<typeof import('@/composables/useNotifications')>()), useNotifications: () => ({ confirm: mocks.confirm }) }))
const chinese = () => ({ global: { plugins: [createI18n({ legacy: false, locale: 'zh-CN', messages: { 'zh-CN': zh } })] } })

const user = { storage_id: 'user', scope_kind: 'user', storage_root: 'C:/user', backend: 'sqlite', external: false, paths: { config: 'C:/user/config', cache: 'C:/user/cache' } }
const project = { ...user, storage_id: '1'.repeat(32), scope_kind: 'project', project_root: 'C:/project', storage_root: 'C:/project/.tinadec', allow_storage_write: false }
const config = { document_id: 'runtime', path: 'C:/user/config/runtime.toml', text: 'enabled = true\n', content_hash: 'old-hash', diagnostics: [], version: 1 }

beforeEach(() => {
  vi.clearAllMocks()
  setHostAccessStatus({ state: 'ready', managed: true })
  mocks.listStorageScopes.mockResolvedValue([user, project])
  mocks.getStorageStats.mockImplementation(async (id: string) => ({ storage_id: id, categories: [{ category: 'cache', path: 'C:/user/cache', size_bytes: 1024, file_count: 2, clearable: true }], diagnostics: [] }))
  mocks.getStorageDiagnostics.mockImplementation(async (id: string) => ({ storage_id: id, diagnostics: [{ code: 'credential_unbound', message: '凭据引用需要重新绑定', severity: 'warning' }] }))
  mocks.getConfigurationDocument.mockResolvedValue(config)
  mocks.validateConfigurationDocument.mockResolvedValue({ valid: true, diagnostics: [] })
  mocks.saveConfigurationDocument.mockRejectedValue(Object.assign(new Error('conflict'), { status: 412 }))
  mocks.confirm.mockResolvedValue(true)
  mocks.hostAction.mockResolvedValue({})
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { storageAction: mocks.hostAction } })
})

async function selectScope(wrapper: VueWrapper, storageId: string) {
  await wrapper.get('[data-testid="storage-scope"]').trigger('click')
  const option = Array.from(document.body.querySelectorAll<HTMLButtonElement>('.ui-select-option'))
    .find(item => item.dataset.value === storageId)
  if (!option) throw new Error(`scope option missing: ${storageId}`)
  option.click()
  await flushPromises()
}

it('waits for scope closing and lets the user cancel the captured request', async () => {
  let signal!: AbortSignal
  mocks.closeStorageScope.mockImplementation((_scope: string, closeSignal: AbortSignal) => new Promise((_resolve, reject) => {
    signal = closeSignal; signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')))
  }))
  const wrapper = mount(StorageSection, chinese()); await flushPromises()
  await selectScope(wrapper, project.storage_id)
  await wrapper.findAll('button').find(button => button.text() === '关闭项目存储')!.trigger('click'); await flushPromises()
  expect(mocks.closeStorageScope).toHaveBeenCalledWith(project.storage_id, signal)
  expect(wrapper.text()).toContain('正在等待项目运行和连接结束')
  await wrapper.findAll('button').find(button => button.text() === '取消关闭')!.trigger('click'); await flushPromises()
  expect(signal.aborted).toBe(true); expect(wrapper.text()).toContain('关闭已取消')
  expect(wrapper.get('[data-testid="storage-scope"]').text()).toContain('C:/project')
  wrapper.unmount()
})
it('preserves CAS diagnostics and trace alongside the retained draft', async () => {
  mocks.saveConfigurationDocument.mockRejectedValueOnce(new ApiError('configuration conflict', 412, { code: 'configuration_conflict', trace_id: 'cas-trace', actions: ['reload'], diagnostics: [{ code: 'configuration_hash_mismatch', message: 'changed file', severity: 'error' }] }))
  const wrapper = mount(StorageSection, chinese()); await flushPromises()
  await wrapper.get('textarea').setValue('enabled = false\n')
  await wrapper.findAll('button').find(button => button.text() === '保存配置')!.trigger('click'); await flushPromises()
  expect(wrapper.text()).toContain('cas-trace')
  expect(wrapper.text()).toContain('changed file')
  expect(wrapper.text()).toContain('configuration_hash_mismatch')
  expect(wrapper.text()).toContain('草稿保留')
  wrapper.unmount()
})
it('displays actual paths and preserves TOML drafts after a CAS conflict', async () => {
  const wrapper = mount(StorageSection, chinese()); await flushPromises()
  expect(wrapper.text()).toContain('C:/user/config')
  expect(wrapper.text()).toContain('凭据引用需要重新绑定')
  const textarea = wrapper.get('textarea'); await textarea.setValue('enabled = false\n')
  await wrapper.findAll('button').find(button => button.text() === '保存配置')!.trigger('click'); await flushPromises()
  expect(mocks.saveConfigurationDocument).toHaveBeenCalledWith('user', 'runtime', 'enabled = false\n', 'old-hash')
  expect((textarea.element as HTMLTextAreaElement).value).toBe('enabled = false\n')
  expect(wrapper.text()).toContain('草稿保留')
  wrapper.unmount()
})
it('executes exactly the displayed cleanup preview and obtains write grants through IPC', async () => {
  const grant = vi.fn(async () => ({ storage_id: project.storage_id, allow_storage_write: true }))
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { setStorageWritePolicy: grant, storageAction: mocks.hostAction } })
  mocks.previewStorageCleanup.mockResolvedValue({ preview_id: 'preview-1', storage_id: project.storage_id, category: 'cache', path: 'C:/project/.tinadec/cache', file_count: 2, size_bytes: 1024, expires_at: '2026-10-09T20:00:00Z' })
  mocks.cleanupStorage.mockResolvedValue({})
  const wrapper = mount(StorageSection, chinese()); await flushPromises()
  await selectScope(wrapper, project.storage_id)
  await wrapper.get('input[type="checkbox"]').setValue(true); await flushPromises()
  expect(grant).toHaveBeenCalledWith(project.storage_id, true)
  await wrapper.findAll('button').find(button => button.text() === '预览清理')!.trigger('click'); await flushPromises()
  expect(wrapper.text()).toContain('C:/project/.tinadec/cache')
  await wrapper.findAll('button').find(button => button.text() === '确认执行此预览')!.trigger('click'); await flushPromises()
  expect(mocks.hostAction).toHaveBeenCalledWith(project.storage_id, 'cleanup', { preview_id: 'preview-1' })
  expect(mocks.cleanupStorage).not.toHaveBeenCalled()
  wrapper.unmount()
})
it('persists a user root switch in the host bootstrap and shows restart as the final step', async () => {
  const persist = vi.fn(async () => ({ user_root: 'D:/new-root', restart_required: true }))
  Object.defineProperty(window, 'tinadec', { configurable: true, value: { saveUserStorageRoot: persist, storageAction: mocks.hostAction } })
  mocks.hostAction.mockResolvedValue({ ...user, restart_required: true, requested_storage_root: 'D:/new-root' })
  const wrapper = mount(StorageSection, chinese()); await flushPromises()
  await wrapper.findAll('input').find(input => input.attributes('type') !== 'checkbox')!.setValue('D:/new-root')
  await wrapper.findAll('button').find(button => button.text() === '应用存储配置')!.trigger('click'); await flushPromises()
  expect(persist).toHaveBeenCalledWith('D:/new-root')
  expect(wrapper.text()).toContain('重启以应用配置')
  wrapper.unmount()
})
it('separates content collection from whole-storage deletion and uses the reviewed tokens', async () => {
  mocks.previewContentCollection.mockResolvedValue({ preview_id: 'content-preview', storage_id: project.storage_id, file_count: 1, size_bytes: 128, references: ['content/tenants/a/hash'], expires_at: '2026-10-09T20:00:00Z' })
  mocks.collectContent.mockResolvedValue({})
  mocks.previewStorageDeletion.mockResolvedValue({ preview_id: 'delete-preview', storage_id: project.storage_id, category: 'project_storage', path: 'C:/project/.tinadec', file_count: 3, size_bytes: 256, expires_at: '2026-10-09T20:00:00Z' })
  mocks.deleteStorage.mockResolvedValue({})
  const wrapper = mount(StorageSection, chinese()); await flushPromises()
  await selectScope(wrapper, project.storage_id)
  await wrapper.findAll('button').find(button => button.text() === '预览内容回收')!.trigger('click'); await flushPromises()
  expect(wrapper.text()).toContain('content/tenants/a/hash')
  await wrapper.findAll('button').find(button => button.text() === '确认回收此预览')!.trigger('click'); await flushPromises()
  expect(mocks.hostAction).toHaveBeenCalledWith(project.storage_id, 'content-collect', { preview_id: 'content-preview' })
  await wrapper.findAll('button').find(button => button.text() === '预览删除整个项目存储')!.trigger('click'); await flushPromises()
  await wrapper.findAll('button').find(button => button.text() === '确认删除此项目存储')!.trigger('click'); await flushPromises()
  expect(mocks.confirm).toHaveBeenLastCalledWith(expect.objectContaining({ message: expect.stringContaining('C:/project/.tinadec'), destructive: true }))
  expect(mocks.hostAction).toHaveBeenCalledWith(project.storage_id, 'storage-delete', { preview_id: 'delete-preview' })
  expect(mocks.collectContent).not.toHaveBeenCalled(); expect(mocks.deleteStorage).not.toHaveBeenCalled()
  wrapper.unmount()
})
it('explains why a web client cannot execute maintenance without a trusted host', async () => {
  Object.defineProperty(window, 'tinadec', { configurable: true, value: {} })
  mocks.previewStorageCleanup.mockResolvedValue({ preview_id: 'preview-1', storage_id: 'user', category: 'cache', path: 'C:/user/cache', file_count: 2, size_bytes: 1024, expires_at: '2026-10-09T20:00:00Z' })
  const wrapper = mount(StorageSection, chinese()); await flushPromises()
  await wrapper.findAll('button').find(button => button.text() === '预览清理')!.trigger('click'); await flushPromises()
  await wrapper.findAll('button').find(button => button.text() === '确认执行此预览')!.trigger('click'); await flushPromises()
  expect(wrapper.text()).toContain('需要可信主窗口宿主')
  expect(mocks.hostAction).not.toHaveBeenCalled(); expect(mocks.cleanupStorage).not.toHaveBeenCalled()
  wrapper.unmount()
})

it.each(['cleanup', 'content', 'delete'] as const)('discards a late %s preview after reconnecting into another scope', async kind => {
  let resolve!: (value: unknown) => void
  const pending = new Promise(result => { resolve = result })
  const mock = kind === 'cleanup' ? mocks.previewStorageCleanup : kind === 'content' ? mocks.previewContentCollection : mocks.previewStorageDeletion
  mock.mockReturnValueOnce(pending)
  const wrapper = mount(StorageSection, chinese()); await flushPromises()
  await selectScope(wrapper, project.storage_id)
  const label = kind === 'cleanup' ? '预览清理' : kind === 'content' ? '预览内容回收' : '预览删除整个项目存储'
  await wrapper.findAll('button').find(button => button.text() === label)!.trigger('click'); await flushPromises()
  setHostAccessStatus({ state: 'unavailable', managed: true }); await flushPromises()
  setHostAccessStatus({ state: 'ready', managed: true }); await flushPromises()
  await selectScope(wrapper, 'user')
  resolve({ preview_id: 'late-preview', storage_id: project.storage_id, category: 'cache', path: 'C:/stale-project', file_count: 1, size_bytes: 128, references: ['stale-reference'], expires_at: '' }); await flushPromises()
  expect(wrapper.text()).not.toContain('C:/stale-project'); expect(wrapper.text()).not.toContain('stale-reference')
  expect(wrapper.findAll('button').some(button => button.text().startsWith('确认') && button.text().includes('预览'))).toBe(false)
  expect(mocks.hostAction).not.toHaveBeenCalled()
  wrapper.unmount()
})
it('does not apply late maintenance completion or stats to a newly selected scope', async () => {
  mocks.previewContentCollection.mockResolvedValue({ preview_id: 'pending-collection', storage_id: project.storage_id, file_count: 1, size_bytes: 128, references: [], expires_at: '' })
  let resolve!: () => void; mocks.hostAction.mockReturnValueOnce(new Promise<void>(result => { resolve = result }))
  const wrapper = mount(StorageSection, chinese()); await flushPromises()
  await selectScope(wrapper, project.storage_id)
  await wrapper.findAll('button').find(button => button.text() === '预览内容回收')!.trigger('click'); await flushPromises()
  await wrapper.findAll('button').find(button => button.text() === '确认回收此预览')!.trigger('click'); await flushPromises()
  setHostAccessStatus({ state: 'unavailable', managed: true }); await flushPromises()
  setHostAccessStatus({ state: 'ready', managed: true }); await flushPromises()
  await selectScope(wrapper, 'user')
  const reads = mocks.getStorageStats.mock.calls.length; resolve(); await flushPromises()
  expect(wrapper.get('[data-testid="storage-scope"]').text()).toContain('C:/user')
  expect(wrapper.text()).not.toContain('未被引用的内容已回收')
  expect(mocks.getStorageStats).toHaveBeenCalledTimes(reads)
  wrapper.unmount()
})
