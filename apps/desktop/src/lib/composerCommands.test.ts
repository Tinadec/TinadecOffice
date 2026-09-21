import { describe, expect, it } from 'vitest'
import { composerCommands, filterComposerCommands, parseComposerCommand } from './composerCommands'

describe('parseComposerCommand', () => {
  it('runs an argument-free command', () => {
    const parsed = parseComposerCommand('/stop')
    expect(parsed.kind).toBe('run')
    if (parsed.kind === 'run') expect(parsed.command.id).toBe('stop')
  })

  it('hands the rest of the line to an argument command', () => {
    const parsed = parseComposerCommand('  /queue 帮我跑一遍测试  ')
    expect(parsed).toEqual({ kind: 'run', command: composerCommands[2], argument: '帮我跑一遍测试' })
  })

  it('treats a bare argument command as incomplete instead of sending it as prose', () => {
    // "/queue" with nothing after it is a half-typed command. Letting Enter fall
    // through would put the literal "/queue" in front of the model.
    expect(parseComposerCommand('/queue').kind).toBe('incomplete')
  })

  it('leaves unknown slashes and ordinary text alone', () => {
    expect(parseComposerCommand('/nonsense do this').kind).toBe('not-a-command')
    expect(parseComposerCommand('为什么 1/2 不等于 2/1？').kind).toBe('not-a-command')
  })

  it('matches commands case-insensitively', () => {
    const parsed = parseComposerCommand('/QUEUE x')
    expect(parsed.kind).toBe('run')
  })
})

describe('filterComposerCommands', () => {
  it('suggests nothing outside a slash', () => {
    expect(filterComposerCommands('')).toEqual([])
    expect(filterComposerCommands('hello')).toEqual([])
  })

  it('lists every command on a bare slash and narrows by prefix', () => {
    expect(filterComposerCommands('/')).toHaveLength(composerCommands.length)
    expect(filterComposerCommands('/q').map((command) => command.id)).toEqual(['queue'])
    expect(filterComposerCommands('/p').map((command) => command.id)).toEqual(['parallel'])
    expect(filterComposerCommands('/nope')).toEqual([])
  })
})
