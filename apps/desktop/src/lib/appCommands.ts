/**
 * The command table both command surfaces read from: the composer's `/slash`
 * menu and the window-wide palette.
 *
 * Before this file the palette would have needed its own copy of every handler,
 * which is how the pencil button ended up wired to a stub: a second surface that
 * displayed an action nobody had implemented once. So a command's `run` lives here
 * and only here, and the two surfaces differ solely in the `CommandHost` they pass -
 * the composer's host knows about its textarea and its mode props, the palette's
 * host knows about the router. Neither one gets to decide what a command does.
 *
 * Nothing in this module touches the controller, the router, or i18n: `labelKey` is
 * a key, and every side effect goes through the host. That is what keeps the table
 * testable without mounting a component, and what keeps `src/lib` free of a second
 * owner for application state.
 */

export type CommandGroup = 'run' | 'session' | 'view'

/** Order the palette lists groups in. Unlisted groups sort last, in declaration order. */
export const commandGroupOrder: readonly CommandGroup[] = ['session', 'run', 'view']

/**
 * Everything a command may ask the surface to do. Each method is called at most once
 * per run; a surface that cannot serve a command reports it through `canStop` /
 * `draft` rather than by ignoring the call.
 */
export interface CommandHost {
  /** A cancellable run exists right now. */
  canStop(): boolean
  /** Current composer text. The palette has no argument field, so this is its payload. */
  draft(): string
  setDraft(value: string): void
  /**
   * Send `text` now with a one-shot dispatch mode. The persisted Enter preference the
   * send menu owns is deliberately not touched, and the surface keeps its own mode
   * version and model override, which is why this is a host call rather than a
   * controller call.
   */
  send(text: string, dispatch: 'queued' | 'parallel'): void
  stopRun(): void
  newSession(): void
  navigate(routeName: string): void
  /** Route name the surface is on, so a navigation command can hide itself. */
  routeName(): string | null
}

export interface AppCommand {
  id: string
  group: CommandGroup
  labelKey: string
  hintKey?: string
  /** Slash form shown in the composer, without the leading slash. */
  slash?: string
  /** The command consumes the rest of the line as its payload. */
  needsArgument?: boolean
  /**
   * Extra search terms for the palette, as i18n keys. A palette that only matched
   * English would be unusable in the other locale, and zh-CN is this app's default.
   */
  keywordKeys?: string[]
  /**
   * Route the command jumps to, declared as data rather than only inside `run`: the
   * test that proves no palette entry points at a route the router never registered
   * reads this field, and a string buried in a closure cannot be enumerated.
   */
  route?: string
  isAvailable?(host: CommandHost): boolean
  run(host: CommandHost, argument: string): void
}

/**
 * Queue and parallel are listed only when there is text to send: in the composer the
 * typed line itself is the argument, so the condition is what the menu already
 * required; in the palette it is the draft, because the palette offers no other way
 * to supply a payload. They are NOT gated on a session existing - `handleSend` creates
 * one when there is none, so gating here would remove a path that works today.
 */
/**
 * One navigation entry per page. Written as a builder because the nine rows differ
 * only by name: an inline `run` closure per page would hide the route from
 * `command.route`, which is the field the router-declaration test enumerates.
 */
function go(id: string, labelKey: string, route: string, keywordKeys?: string[]): AppCommand {
  return {
    id,
    group: 'view',
    labelKey,
    keywordKeys,
    route,
    isAvailable: (host) => host.routeName() !== route,
    run: (host) => host.navigate(route),
  }
}

export const appCommands: readonly AppCommand[] = [
  {
    id: 'session.new',
    group: 'session',
    labelKey: 'composer.cmdNew',
    hintKey: 'composer.cmdNewHint',
    keywordKeys: ['palette.keywordConversation'],
    slash: 'new',
    run: (host) => {
      host.setDraft('')
      host.newSession()
    },
  },
  {
    id: 'run.stop',
    group: 'run',
    labelKey: 'composer.cmdStop',
    hintKey: 'composer.cmdStopHint',
    keywordKeys: ['palette.keywordCancel'],
    slash: 'stop',
    isAvailable: (host) => host.canStop(),
    run: (host) => {
      host.setDraft('')
      host.stopRun()
    },
  },
  {
    id: 'run.queue',
    group: 'run',
    labelKey: 'composer.cmdQueue',
    hintKey: 'composer.cmdQueueHint',
    keywordKeys: ['palette.keywordWaitTurn'],
    slash: 'queue',
    needsArgument: true,
    isAvailable: (host) => Boolean(host.draft().trim()),
    run: (host, argument) => host.send(argument, 'queued'),
  },
  {
    id: 'run.parallel',
    group: 'run',
    labelKey: 'composer.cmdParallel',
    hintKey: 'composer.cmdParallelHint',
    keywordKeys: ['palette.keywordSteer'],
    slash: 'parallel',
    needsArgument: true,
    isAvailable: (host) => Boolean(host.draft().trim()),
    run: (host, argument) => host.send(argument, 'parallel'),
  },
  go('view.goChat', 'palette.goChat', 'home'),
  go('view.goWorkbench', 'palette.goWorkbench', 'workbench'),
  go('view.goCode', 'palette.goCode', 'code-editor'),
  go('view.goChatroom', 'palette.goChatroom', 'chatroom'),
  go('view.goLibrary', 'palette.goLibrary', 'library'),
  go('view.goSnapshots', 'palette.goSnapshots', 'snapshots'),
  go('view.goGovernance', 'palette.goGovernance', 'governance-board'),
  go('view.goMemory', 'palette.goMemory', 'memory', ['palette.keywordMemory']),
  go('view.goMarket', 'palette.goMarket', 'market'),
  go('view.goSettings', 'palette.goSettings', 'settings', ['palette.keywordPreferences']),
]

/** The i18n key a group's label lives under, so a row tag and a test agree on it. */
export function formatGroup(group: CommandGroup): string {
  return `palette.group${group.charAt(0).toUpperCase()}${group.slice(1)}`
}

export function isCommandAvailable(command: AppCommand, host: CommandHost): boolean {
  return command.isAvailable?.(host) ?? true
}

/** What a command runs with when the surface has no typed argument to hand it. */
export function implicitArgument(command: AppCommand, host: CommandHost): string {
  return command.needsArgument ? host.draft().trim() : ''
}

export function availableCommands(host: CommandHost): AppCommand[] {
  return appCommands.filter((command) => isCommandAvailable(command, host))
}

/**
 * `label` is resolved by the caller because only the caller knows which language the
 * user is reading; this module never imports i18n.
 */
export function haystackOf(command: AppCommand, label: string, keywords: string[]): string {
  return [command.slash ?? '', command.id, label, ...keywords, command.group]
    .filter((part) => part.length > 0)
    .join(' ')
    .toLowerCase()
}

/**
 * 0 = the query is a prefix, 1 = a substring, 2 = its characters appear in order.
 * Anything weaker is not a match: a palette that shows unrelated rows is worse than
 * one that shows none, because the user reads the list as "these are my options".
 */
export function scoreMatch(query: string, haystack: string): number {
  if (!query) return 1
  if (haystack.startsWith(query)) return 0
  if (haystack.includes(query)) return 1
  let at = 0
  for (const char of query) {
    const found = haystack.indexOf(char, at)
    if (found === -1) return -1
    at = found + 1
  }
  return 2
}

export interface RankedCommand {
  command: AppCommand
  haystack: string
}

export function filterCommands(
  query: string,
  entries: readonly RankedCommand[],
): AppCommand[] {
  const normalized = query.trim().toLowerCase()
  return entries
    .map((entry) => ({ command: entry.command, score: scoreMatch(normalized, entry.haystack) }))
    .filter((entry) => entry.score >= 0)
    .sort((left, right) => {
      if (left.score !== right.score) return left.score - right.score
      return (
        commandGroupOrder.indexOf(left.command.group) - commandGroupOrder.indexOf(right.command.group)
      )
    })
    .map((entry) => entry.command)
}

// ── composer slash surface ───────────────────────────────────────────

export type SlashParse =
  | { kind: 'run'; command: AppCommand; argument: string }
  /** The line is a known command with its argument still missing: never send it as text. */
  | { kind: 'incomplete'; command: AppCommand }
  | { kind: 'not-a-command' }

export function slashCommands(): AppCommand[] {
  return appCommands.filter((command) => command.slash)
}

export function filterSlashCommands(text: string, host: CommandHost): AppCommand[] {
  if (!text.startsWith('/')) return []
  const query = text.slice(1).trimStart().split(/\s+/)[0]?.toLowerCase() ?? ''
  return slashCommands().filter(
    (command) => command.slash!.startsWith(query) && isCommandAvailable(command, host),
  )
}

export function parseSlashCommand(text: string): SlashParse {
  const trimmed = text.trim()
  if (!trimmed.startsWith('/')) return { kind: 'not-a-command' }
  const [token, ...rest] = trimmed.slice(1).split(/\s+/)
  const command = slashCommands().find((item) => item.slash === token?.toLowerCase())
  if (!command) return { kind: 'not-a-command' }
  const argument = rest.join(' ').trim()
  if (command.needsArgument && !argument) return { kind: 'incomplete', command }
  return { kind: 'run', command, argument }
}
