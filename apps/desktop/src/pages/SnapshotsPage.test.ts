// @vitest-environment happy-dom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils'
import { setActivePinia, createPinia } from 'pinia'

vi.mock('vue-i18n', () => ({
  useI18n: () => ({ t: (_key: string, fallback?: string) => fallback ?? _key }),
}))

vi.mock('@/api', () => ({
  api: {
    listWorkspaceSnapshots: vi.fn(),
    listWorkspaceSnapshotFiles: vi.fn(),
    getWorkspaceSnapshotFileDiff: vi.fn(),
    restoreWorkspaceSnapshotFile: vi.fn(),
    restoreWorkspaceSnapshot: vi.fn(),
  },
}))

// Monaco cannot load under happy-dom, and the props handed to it are the claim worth testing.
vi.mock('@/components/git/DiffViewer.vue', () => ({
  default: {
    name: 'DiffViewerStub',
    props: ['originalContent', 'modifiedContent', 'filePath', 'binary', 'truncated'],
    template: '<div data-testid="diff-stub" :data-path="filePath" />',
  },
}))

import { api, type SnapshotDto, type WorkspaceFileChangeDto } from '@/api'
import { useProjectStore } from '@/stores/project'
import SnapshotsPage from './SnapshotsPage.vue'

const apiMock = vi.mocked(api, true)

const snapshot: SnapshotDto = {
  id: 'snap-1',
  tenant_id: 't',
  workspace_id: 'w',
  project_id: 'p1',
  kind: 'workspace',
  status: 'created',
  is_git: false,
  workspace_hash: 'aa'.repeat(32),
  content_hash: 'bb'.repeat(32),
  file_count: 4,
  created_at: '2026-09-22T00:00:00Z',
}

function row(overrides: Partial<WorkspaceFileChangeDto> & { path: string }): WorkspaceFileChangeDto {
  return {
    status: 'modified',
    restorable: true,
    before_sha256: `before-${overrides.path}`,
    after_sha256: `after-${overrides.path}`,
    ...overrides,
  }
}

async function mountWithRows(rows: WorkspaceFileChangeDto[]): Promise<VueWrapper> {
  setActivePinia(createPinia())
  useProjectStore().projects = [{ id: 'p1', name: 'P1' } as never]
  apiMock.listWorkspaceSnapshots.mockResolvedValue([snapshot])
  apiMock.listWorkspaceSnapshotFiles.mockResolvedValue(rows)
  const wrapper = mount(SnapshotsPage)
  await flushPromises()
  await wrapper.get('[data-testid="snapshot-review-btn"]').trigger('click')
  await flushPromises()
  return wrapper
}

afterEach(() => {
  document.body.innerHTML = ''
  vi.clearAllMocks()
})

describe('SnapshotsPage per-file review', () => {
  it('shows the rows that differ and drops the ones that do not', async () => {
    const wrapper = await mountWithRows([
      row({ path: 'edit.txt' }),
      row({ path: 'keep.txt', status: 'unchanged' }),
      row({ path: 'gone.txt', status: 'deleted', after_sha256: null, after_length: null }),
    ])

    const rows = wrapper.findAll('[data-testid="snapshot-review-row"]')
    expect(rows).toHaveLength(2)
    expect(wrapper.text()).not.toContain('keep.txt')
    expect(wrapper.text()).toContain('edit.txt')
  })

  it('undoes the file the user pointed at, with the hash that row was shown', async () => {
    apiMock.restoreWorkspaceSnapshotFile.mockResolvedValue(row({ path: 'edit.txt', status: 'unchanged' }))
    const wrapper = await mountWithRows([
      row({ path: 'edit.txt', after_sha256: 'LIVE-HASH' }),
      row({ path: 'gone.txt', status: 'deleted', after_sha256: null }),
    ])

    const undoButtons = wrapper.findAll('[data-testid="snapshot-undo-btn"]')
    expect(undoButtons).toHaveLength(2)
    await undoButtons[0]!.trigger('click')
    await flushPromises()
    expect(apiMock.restoreWorkspaceSnapshotFile).toHaveBeenCalledWith('snap-1', 'edit.txt', 'LIVE-HASH')

    // A deleted row has no live hash. "There was nothing here" is sent as the empty string, and
    // sending the row's *before* hash instead would ask Core to overwrite an absent file with a
    // value that matches nothing.
    await wrapper.get('[data-testid="snapshot-review-row"]:nth-child(2) [data-testid="snapshot-undo-btn"]')
      .trigger('click')
    await flushPromises()
    expect(apiMock.restoreWorkspaceSnapshotFile).toHaveBeenLastCalledWith('snap-1', 'gone.txt', '')
  })

  it('refuses to offer an undo for a row whose content the snapshot never held', async () => {
    const wrapper = await mountWithRows([
      row({ path: 'huge.bin', restorable: false }),
      row({ path: 'edit.txt' }),
    ])

    const rows = wrapper.findAll('[data-testid="snapshot-review-row"]')
    expect(rows[0]!.find('[data-testid="snapshot-undo-btn"]').exists()).toBe(false)
    expect(rows[0]!.find('[data-testid="snapshot-not-restorable"]').exists()).toBe(true)
    expect(rows[1]!.find('[data-testid="snapshot-undo-btn"]').exists()).toBe(true)
  })

  it('asks for the diff by that path and hands both bodies to the viewer', async () => {
    apiMock.getWorkspaceSnapshotFileDiff.mockResolvedValue({
      path: 'edit.txt',
      status: 'modified',
      restorable: true,
      before: { present: true, length: 6, sha256: 'b', binary: false, truncated: false, text: 'BEFORE' },
      after: { present: true, length: 6, sha256: 'a', binary: false, truncated: false, text: 'AFTER' },
    })
    const wrapper = await mountWithRows([row({ path: 'src/edit.txt' })])

    await wrapper.get('[data-testid="snapshot-diff-btn"]').trigger('click')
    await flushPromises()

    expect(apiMock.getWorkspaceSnapshotFileDiff).toHaveBeenCalledWith('snap-1', 'src/edit.txt')
    const diff = wrapper.findComponent({ name: 'DiffViewerStub' })
    expect(diff.props()).toMatchObject({
      filePath: 'src/edit.txt',
      originalContent: 'BEFORE',
      modifiedContent: 'AFTER',
      binary: false,
      truncated: false,
    })
  })
})
