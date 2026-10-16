import { effectScope, nextTick, ref } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
const execute = vi.hoisted(() => vi.fn())
vi.mock('@/api', () => ({ api: { executeCodeTool: execute } }))
import { useSpatialGit } from './useSpatialGit'
import i18n from '@/i18n'
afterEach(() => execute.mockReset())

describe('space Git read ownership', () => {
  it('does not report a blocked tool call as an empty successful repository', async () => {
    execute.mockResolvedValue({ status: 'awaiting_approval', summary: 'Approval needed', data: {} })
    const scope = effectScope()
    const git = scope.run(() => useSpatialGit(ref('/project')))!
    await vi.waitFor(() => expect(git.state.value.loading).toBe(false))
    expect(git.state.value.loaded).toBe(false)
    // In words for the card; the tool's own sentence stays as the detail.
    expect(git.state.value.error).toBe(i18n.global.t('space.gitAwaitingApproval'))
    expect(git.state.value.errorDetail).toBe('Approval needed')
    scope.stop()
  })
  it('aborts and ignores a late read after the conversation project changed', async () => {
    const finish: ((v: unknown) => void)[] = []
    execute.mockImplementation((_tool, input) => input.cwd === '/old'
      ? new Promise(resolve => finish.push(resolve)) : Promise.resolve({ status: 'completed', data: { branch: 'new', files: [], commits: [] } }))
    const scope = effectScope(), cwd = ref('/old')
    const git = scope.run(() => useSpatialGit(cwd))!
    const oldSignal: AbortSignal = execute.mock.calls[0][2].signal
    cwd.value = '/new'
    await nextTick()
    await vi.waitFor(() => expect(git.state.value.preview.branch).toBe('new'))
    for (const resolve of finish) resolve({ status: 'completed', data: { branch: 'old', files: [], commits: [] } })
    await nextTick()
    expect(oldSignal.aborted).toBe(true)
    expect(git.state.value.preview.branch).toBe('new')
    scope.stop()
  })
})
