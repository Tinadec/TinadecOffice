export type ComposerCommandId = 'stop' | 'new' | 'queue' | 'parallel'

export interface ComposerCommand {
  id: ComposerCommandId
  /** Typed form, without the leading slash. */
  name: string
  /** The command consumes the rest of the line as its payload. */
  needsArgument: boolean
  labelKey: string
  hintKey: string
}

/**
 * Every entry maps onto a control the composer already has: `/stop` is the stop
 * button, `/new` is the "new chat" action, `/queue` and `/parallel` are the two
 * dispatch modes of the send menu. Nothing here talks to Core — a command that
 * could only be *displayed* would be the same dead-button class as the pencil was.
 */
export const composerCommands: readonly ComposerCommand[] = [
  { id: 'stop', name: 'stop', needsArgument: false, labelKey: 'composer.cmdStop', hintKey: 'composer.cmdStopHint' },
  { id: 'new', name: 'new', needsArgument: false, labelKey: 'composer.cmdNew', hintKey: 'composer.cmdNewHint' },
  { id: 'queue', name: 'queue', needsArgument: true, labelKey: 'composer.cmdQueue', hintKey: 'composer.cmdQueueHint' },
  { id: 'parallel', name: 'parallel', needsArgument: true, labelKey: 'composer.cmdParallel', hintKey: 'composer.cmdParallelHint' },
]

export type ComposerParse =
  | { kind: 'run'; command: ComposerCommand; argument: string }
  /** The line is a known command with its argument still missing: never send it as text. */
  | { kind: 'incomplete'; command: ComposerCommand }
  | { kind: 'not-a-command' }

export function filterComposerCommands(text: string): ComposerCommand[] {
  if (!text.startsWith('/')) return []
  const query = text.slice(1).trimStart().split(/\s+/)[0]?.toLowerCase() ?? ''
  return composerCommands.filter((command) => command.name.startsWith(query))
}

export function parseComposerCommand(text: string): ComposerParse {
  const trimmed = text.trim()
  if (!trimmed.startsWith('/')) return { kind: 'not-a-command' }
  const [token, ...rest] = trimmed.slice(1).split(/\s+/)
  const command = composerCommands.find((item) => item.name === token?.toLowerCase())
  if (!command) return { kind: 'not-a-command' }
  const argument = rest.join(' ').trim()
  if (command.needsArgument && !argument) return { kind: 'incomplete', command }
  return { kind: 'run', command, argument }
}
