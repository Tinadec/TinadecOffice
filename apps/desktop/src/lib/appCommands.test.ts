// @vitest-environment node
import { describe, expect, it } from 'vitest'
import routerSource from '../router.ts?raw'
import en from '@/locales/en'
import zhCN from '@/locales/zh-CN'
import {
  appCommands,
  availableCommands,
  filterCommands,
  filterSlashCommands,
  haystackOf,
  implicitArgument,
  parseSlashCommand,
  scoreMatch,
  slashCommands,
  type CommandHost,
  type RankedCommand,
} from './appCommands'

/** The state a command can observe; everything else is a recorded call. */
interface HostState {
  canStop?: boolean
  draftText?: string
  route?: string | null
}

/** Records every host call so a command's behaviour is asserted, not assumed. */
function hostWith(state: HostState = {}): CommandHost & { calls: string[] } {
  const calls: string[] = []
  const record = (name: string) => (...args: unknown[]) => {
    calls.push(`${name}(${args.map((arg) => JSON.stringify(arg)).join(',')})`)
  }
  return {
    calls,
    canStop: () => state.canStop ?? false,
    draft: () => state.draftText ?? '',
    setDraft: record('setDraft') as (value: string) => void,
    send: record('send') as (text: string, dispatch: 'queued' | 'parallel') => void,
    stopRun: record('stopRun') as () => void,
    newSession: record('newSession') as () => void,
    navigate: record('navigate') as (routeName: string) => void,
    routeName: () => state.route ?? null,
  }
}

function commandById(id: string) {
  const found = appCommands.find((command) => command.id === id)
  if (!found) throw new Error(`no command with id ${id} - the table lost an entry`)
  return found
}

describe('appCommands table', () => {
  it('gives every command a unique id and a unique slash form', () => {
    const ids = appCommands.map((command) => command.id)
    expect(new Set(ids).size).toBe(ids.length)
    const slashes = slashCommands().map((command) => command.slash)
    expect(new Set(slashes).size).toBe(slashes.length)
    expect(slashes).toEqual(['new', 'stop', 'queue', 'parallel'])
  })

  it('names only keys, and every key exists in both bundles', () => {
    const bundles = [en as unknown as Record<string, unknown>, zhCN as unknown as Record<string, unknown>]
    const lookup = (bundle: Record<string, unknown>, dotted: string) =>
      dotted.split('.').reduce<unknown>((node, part) => {
        if (node && typeof node === 'object') return (node as Record<string, unknown>)[part]
        return undefined
      }, bundle)
    const missing: string[] = []
    for (const command of appCommands) {
      for (const key of [command.labelKey, command.hintKey, ...(command.keywordKeys ?? [])]) {
        if (!key) continue
        for (const [name, bundle] of bundles.entries()) {
          const value = lookup(bundle, key)
          if (typeof value !== 'string' || !value.trim()) {
            missing.push(`${command.id} -> ${key} (bundle ${name === 0 ? 'en' : 'zh-CN'})`)
          }
        }
      }
    }
    // A missing key is how chat.edit rendered as the literal string "chat.edit".
    expect(missing, `keys with no translation: ${missing.join(', ')}`).toEqual([])
  })

  it('lists only routes the router actually declares', () => {
    const unbacked = appCommands
      .filter((command) => command.route)
      .filter((command) => !routerSource.includes(`name: '${command.route}'`))
      .map((command) => `${command.id} -> ${command.route}`)
    expect(unbacked, `commands pointing at routes that do not exist: ${unbacked.join(', ')}`).toEqual([])
  })
})

describe('command behaviour', () => {
  it('clears the draft before stopping, and stops through the host', () => {
    const host = hostWith({ canStop: true })
    commandById('run.stop').run(host, '')
    expect(host.calls).toEqual(['setDraft("")', 'stopRun()'])
  })

  it('clears the draft before opening a session', () => {
    const host = hostWith()
    commandById('session.new').run(host, '')
    expect(host.calls).toEqual(['setDraft("")', 'newSession()'])
  })

  it('sends the parsed argument with the dispatch mode the command names', () => {
    const queued = hostWith()
    commandById('run.queue').run(queued, '帮我跑一遍测试')
    expect(queued.calls).toEqual(['send("帮我跑一遍测试","queued")'])

    const parallel = hostWith()
    commandById('run.parallel').run(parallel, '换个方向')
    expect(parallel.calls).toEqual(['send("换个方向","parallel")'])
  })

  it('navigates by route name, and never by a hand-written path', () => {
    const host = hostWith()
    commandById('view.goSettings').run(host, '')
    expect(host.calls).toEqual(['navigate("settings")'])
  })

  it('hides stop unless a run is cancellable, and hides send without text', () => {
    expect(availableCommands(hostWith({ canStop: false })).map((c) => c.id)).not.toContain('run.stop')
    expect(availableCommands(hostWith({ canStop: true })).map((c) => c.id)).toContain('run.stop')

    const empty = availableCommands(hostWith({ draftText: '' }))
    expect(empty.map((c) => c.id)).not.toContain('run.queue')
    const typed = availableCommands(hostWith({ draftText: 'hi' }))
    expect(typed.map((c) => c.id)).toContain('run.queue')
  })

  it('sends without a session, because the controller creates one', () => {
    // Regression guard for the tempting filter: handleSend() creates a session when
    // none is selected, so a "needs a session" rule would remove a working path.
    const host = hostWith({ draftText: '第一条消息' })
    expect(availableCommands(host).map((c) => c.id)).toContain('run.parallel')
    expect(implicitArgument(commandById('run.parallel'), host)).toBe('第一条消息')
  })

  it('hides a navigation command for the page the host is already on', () => {
    const ids = availableCommands(hostWith({ route: 'settings' })).map((c) => c.id)
    expect(ids).not.toContain('view.goSettings')
    expect(ids).toContain('view.goCode')
  })
})

describe('parseSlashCommand', () => {
  it('runs an argument-free command', () => {
    const parsed = parseSlashCommand('/stop')
    expect(parsed.kind).toBe('run')
    if (parsed.kind === 'run') expect(parsed.command.id).toBe('run.stop')
  })

  it('hands the rest of the line to an argument command', () => {
    const parsed = parseSlashCommand('  /queue 帮我跑一遍测试  ')
    expect(parsed).toEqual({ kind: 'run', command: commandById('run.queue'), argument: '帮我跑一遍测试' })
  })

  it('treats a bare argument command as incomplete instead of sending it as prose', () => {
    // "/queue" with nothing after it is a half-typed command. Letting Enter fall
    // through would put the literal "/queue" in front of the model.
    expect(parseSlashCommand('/queue').kind).toBe('incomplete')
  })

  it('leaves unknown slashes and ordinary text alone', () => {
    expect(parseSlashCommand('/nonsense do this').kind).toBe('not-a-command')
    expect(parseSlashCommand('为什么 1/2 不等于 2/1？').kind).toBe('not-a-command')
  })

  it('matches commands case-insensitively', () => {
    expect(parseSlashCommand('/QUEUE x').kind).toBe('run')
  })
})

describe('filterSlashCommands', () => {
  it('suggests nothing outside a slash', () => {
    expect(filterSlashCommands('', hostWith())).toEqual([])
    expect(filterSlashCommands('hello', hostWith())).toEqual([])
  })

  it('lists every slash command on a bare slash and narrows by prefix', () => {
    // draftText is the line being typed, exactly as the composer reports it: while the
    // menu is open the draft always holds the slash, so the two send commands stay listed.
    const host = hostWith({ canStop: true, draftText: '/' })
    expect(filterSlashCommands('/', host)).toHaveLength(slashCommands().length)
    expect(filterSlashCommands('/q', host).map((c) => c.id)).toEqual(['run.queue'])
    expect(filterSlashCommands('/p', host).map((c) => c.id)).toEqual(['run.parallel'])
    expect(filterSlashCommands('/nope', host)).toEqual([])
  })

  it('drops stop while nothing is running', () => {
    const host = hostWith({ canStop: false, draftText: '/' })
    const listed = filterSlashCommands('/', host).map((c) => c.id)
    expect(listed).not.toContain('run.stop')
    expect(listed).toHaveLength(slashCommands().length - 1)
  })
})

describe('filterCommands', () => {
  const ranked = (commands: RankedCommand['command'][] = [...appCommands]): RankedCommand[] =>
    commands.map((command) => ({
      command,
      haystack: haystackOf(command, command.labelKey, command.keywordKeys ?? []),
    }))

  it('ranks a prefix above a substring and a substring above a scattered match', () => {
    const entries = ranked()
    expect(scoreMatch('set', 'settings palette.gosettings')).toBe(0)
    expect(scoreMatch('ting', 'settings palette')).toBe(1)
    expect(scoreMatch('stg', 'settings')).toBe(2)
    expect(scoreMatch('zzz', 'settings')).toBe(-1)

    const results = filterCommands('set', entries).map((c) => c.id)
    expect(results[0]).toBe('view.goSettings')
  })

  it('drops commands that match nothing rather than padding the list', () => {
    expect(filterCommands('qqzzxx', ranked())).toEqual([])
  })

  it('searches the slash form, so a palette and a composer agree on names', () => {
    const results = filterCommands('parallel', ranked()).map((c) => c.id)
    expect(results).toContain('run.parallel')
  })

  it('keeps the whole table when the query is empty, grouped in declared order', () => {
    const results = filterCommands('', ranked())
    expect(results).toHaveLength(appCommands.length)
    const groups = results.map((c) => c.group)
    expect(groups).toEqual([...groups].sort((a, b) => {
      const order = ['session', 'run', 'view']
      return order.indexOf(a) - order.indexOf(b)
    }))
  })
})
