// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import en from '@/locales/en'
import { setSelectedStorage } from '@/lib/storageScope'

const h = vi.hoisted(() => ({
  follows: false,
  docs: {} as Record<string, { text: string; content_hash: string }>,
  listStorageScopes: vi.fn(),
  getConfigurationDocument: vi.fn(),
  setWorkspaceModelSource: vi.fn(),
  confirm: vi.fn(async () => true),
  success: vi.fn(),
  error: vi.fn(),
}))
vi.mock('@/api', () => ({
  api: {
    listStorageScopes: h.listStorageScopes,
    getConfigurationDocument: h.getConfigurationDocument,
    setWorkspaceModelSource: h.setWorkspaceModelSource,
  },
}))
vi.mock('@/composables/useNotifications', () => ({
  useNotifications: () => ({ confirm: h.confirm, notify: { success: h.success, error: h.error } }),
}))
const { default: ModelScopeNotice } = await import('./ModelScopeNotice.vue')

const i18n = createI18n({ legacy: false, locale: 'en', messages: { en } })
const mountNotice = () => mount(ModelScopeNotice, { global: { plugins: [i18n] } })

beforeEach(() => {
  h.follows = false
  h.docs = { user: { text: 'user models', content_hash: 'u1' }, 'ws-1': { text: 'workspace copy', content_hash: 'w1' } }
  h.listStorageScopes.mockImplementation(async () => [{ storage_id: 'ws-1', scope_kind: 'project', project_root: '/home/me/demo', workspace: { name: 'Demo' }, follow_user_models: h.follows }])
  h.getConfigurationDocument.mockImplementation(async (storageId: string) => ({ document_id: 'models', ...h.docs[storageId] }))
  h.setWorkspaceModelSource.mockImplementation(async (_id: string, follow: boolean) => { h.follows = follow; return { storage_id: 'ws-1', follow_user_models: follow } })
  h.confirm.mockResolvedValue(true)
})
afterEach(() => {
  setSelectedStorage('user')
  vi.clearAllMocks()
})

describe('ModelScopeNotice', () => {
  it('says nothing in the user scope, which the model center edits directly', async () => {
    const wrapper = mountNotice()
    await flushPromises()
    expect(wrapper.find('[data-testid="model-scope-notice"]').exists()).toBe(false)
    expect(h.listStorageScopes).not.toHaveBeenCalled()
  })

  it('names a workspace that keeps its own copy, and whether that copy still matches', async () => {
    setSelectedStorage('ws-1')
    const wrapper = mountNotice()
    await flushPromises()
    const notice = wrapper.get('[data-testid="model-scope-notice"]')
    expect(notice.attributes('data-follows')).toBe('false')
    expect(notice.text()).toContain('“Demo”')
    expect(wrapper.find('[data-testid="model-scope-differs"]').exists()).toBe(true)

    h.docs['ws-1'] = { ...h.docs.user }
    await (wrapper.vm as unknown as { inspect: () => Promise<void> }).inspect()
    await flushPromises()
    expect(wrapper.find('[data-testid="model-scope-same"]').exists()).toBe(true)
  })

  it('switches the workspace to this configuration after confirmation, then shows it following', async () => {
    setSelectedStorage('ws-1')
    const wrapper = mountNotice()
    await flushPromises()
    await wrapper.get('[data-testid="model-scope-follow"]').trigger('click')
    await flushPromises()
    expect(h.confirm).toHaveBeenCalledOnce()
    expect(h.setWorkspaceModelSource).toHaveBeenCalledWith('ws-1', true)
    expect(h.success).toHaveBeenCalled()
    expect(wrapper.get('[data-testid="model-scope-notice"]').attributes('data-follows')).toBe('true')
    // A following workspace has no second copy to compare.
    expect(wrapper.find('[data-testid="model-scope-differs"]').exists()).toBe(false)
  })

  it('switches nothing when the confirmation is declined', async () => {
    h.confirm.mockResolvedValue(false)
    setSelectedStorage('ws-1')
    const wrapper = mountNotice()
    await flushPromises()
    await wrapper.get('[data-testid="model-scope-follow"]').trigger('click')
    await flushPromises()
    expect(h.setWorkspaceModelSource).not.toHaveBeenCalled()
  })

  it('lets a following workspace go back to its own copy, which needs no confirmation', async () => {
    h.follows = true
    setSelectedStorage('ws-1')
    const wrapper = mountNotice()
    await flushPromises()
    await wrapper.get('[data-testid="model-scope-detach"]').trigger('click')
    await flushPromises()
    expect(h.confirm).not.toHaveBeenCalled()
    expect(h.setWorkspaceModelSource).toHaveBeenCalledWith('ws-1', false)
  })

  it('reports a refused switch (work still running) and keeps showing the real state', async () => {
    h.setWorkspaceModelSource.mockRejectedValue(Object.assign(new Error('busy'), { status: 409 }))
    setSelectedStorage('ws-1')
    const wrapper = mountNotice()
    await flushPromises()
    await wrapper.get('[data-testid="model-scope-follow"]').trigger('click')
    await flushPromises()
    expect(h.error).toHaveBeenCalled()
    expect(wrapper.get('[data-testid="model-scope-notice"]').attributes('data-follows')).toBe('false')
  })

  it('offers no switch on a guess when the scope roster cannot be read', async () => {
    h.listStorageScopes.mockRejectedValue(new Error('offline'))
    setSelectedStorage('ws-1')
    const wrapper = mountNotice()
    await flushPromises()
    expect(wrapper.find('[data-testid="model-scope-notice"]').exists()).toBe(false)
  })
})
