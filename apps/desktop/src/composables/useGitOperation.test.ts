/**
 * #34 — switching projects in the Git panel.
 *
 * The panel used to reset its approval ids only, so the previous repository's branch, diff
 * preview and file selection stayed on screen next to the new cwd. "Stage selected" then
 * built a tool action from project A's paths and project B's repository, and a slow reply for
 * A could overwrite B's data after the switch. These cases pin both halves: the stale state
 * must be dropped on the switch, and a late reply must never be written.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope, nextTick, ref } from 'vue'
import { createPinia, setActivePinia } from 'pinia'

const { apiMock } = vi.hoisted(() => ({
  apiMock: {
    executeCodeTool: vi.fn(),
    gitLog: vi.fn(),
  },
}))

vi.mock('../api', () => ({
  api: apiMock,
  createUserToolActionForPath: vi.fn(),
}))

vi.mock('vue-i18n', async (importOriginal) => ({ ...(await importOriginal<typeof import('vue-i18n')>()),
  useI18n: () => ({ t: (key: string) => key, te: () => false }),
}))

vi.mock('./useNotifications', () => ({
  useNotifications: () => ({
    notify: { error: vi.fn(), warning: vi.fn(), success: vi.fn(), info: vi.fn() },
  }),
}))

import { useGitOperation } from './useGitOperation'

const REPO_A = 'C:/work/repo-a'
const REPO_B = 'C:/work/repo-b'

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((r) => { resolve = r })
  return { promise, resolve }
}

/** Let every microtask queued behind a resolved promise run. */
async function flush() {
  await new Promise((resolve) => setTimeout(resolve, 0))
  await nextTick()
}

function preview(branch: string, path: string) {
  return { data: { branch, upstream: null, ahead: 0, behind: 0, files: [{ path, status: 'M' }], sections: [] } }
}

function mountPanel() {
  const cwd = ref(REPO_A)
  const scope = effectScope()
  const panel = scope.run(() => useGitOperation(() => cwd.value, () => 'session-1', () => []))!
  return { cwd, scope, panel }
}

describe('useGitOperation project switches', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    apiMock.executeCodeTool.mockReset()
    apiMock.gitLog.mockReset()
    apiMock.gitLog.mockResolvedValue({ data: {} })
  })

  it('drops a late reply for the project the user just left', async () => {
    const slowA = deferred<ReturnType<typeof preview>>()
    apiMock.executeCodeTool.mockImplementation(async (_tool: string, args: { cwd: string; arguments: { action: string } }) => {
      if (args.arguments.action !== 'diff_preview') return { data: {} }
      return args.cwd === REPO_A ? slowA.promise : preview('b-branch', 'b-only.ts')
    })

    const { cwd, panel } = mountPanel()
    await nextTick()

    // Switch to B while A's diff preview is still in flight.
    cwd.value = REPO_B
    await flush()
    expect(panel.repoSummary.value.branch).toBe('b-branch')

    // A finally answers. Writing it would pair A's file list with B's cwd.
    slowA.resolve(preview('a-branch', 'a-only.ts'))
    await flush()

    expect(panel.repoSummary.value.branch).toBe('b-branch')
    expect(panel.statusFiles.value.map((f) => f.path)).toEqual(['b-only.ts'])
    expect([...panel.selectedPaths.value]).toEqual(['b-only.ts'])
  })

  it('clears the previous repository state as soon as the project changes', async () => {
    const slowB = deferred<ReturnType<typeof preview>>()
    apiMock.executeCodeTool.mockImplementation(async (_tool: string, args: { cwd: string; arguments: { action: string } }) => {
      if (args.arguments.action !== 'diff_preview') return { data: {} }
      return args.cwd === REPO_A ? preview('a-branch', 'a-only.ts') : slowB.promise
    })

    const { cwd, panel } = mountPanel()
    await flush()
    expect(panel.repoSummary.value.branch).toBe('a-branch')

    panel.commitMessage.value = 'chore: something for repo A'
    panel.selectAllFiles()
    expect(panel.selectedPaths.value.size).toBe(1)

    cwd.value = REPO_B
    await nextTick()

    // B has not answered yet — nothing may still describe A.
    expect(panel.commitMessage.value).toBe('')
    expect(panel.selectedPaths.value.size).toBe(0)
    expect(panel.previewData.value.branch).toBeUndefined()
    expect(panel.statusFiles.value).toEqual([])

    slowB.resolve(preview('b-branch', 'b-only.ts'))
    await flush()
    expect(panel.repoSummary.value.branch).toBe('b-branch')
  })
})
