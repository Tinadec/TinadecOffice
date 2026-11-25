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

export type CommandGroup = 'run' | 'session' | 'view' | 'workspace'

/** Order the palette lists groups in. Unlisted groups sort last, in declaration order. */
export const commandGroupOrder: readonly CommandGroup[] = ['session', 'run', 'workspace', 'view']

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
  /** Open/close the current session's desktop partner window; no-op when no session is selected. */
  toggleSessionPartner?(): void
}

export interface AppCommand {
  id: string
  group: CommandGroup
  labelKey: string
  /** Optional direct display label for dynamic workspace commands not backed by an i18n key. */
  label?: string
  hintKey?: string
  /** Optional direct description/hint for dynamic workspace commands. */
  hint?: string
  /** Slash form shown in the composer, without the leading slash. */
  slash?: string
  /** The command consumes the rest of the line as its payload. */
  needsArgument?: boolean
  /** Markdown template payload for `.tinadec/commands/*.md` workspace commands. */
  template?: string
  /** Whether the template declares a `$ARGUMENTS` placeholder. */
  hasArgumentsPlaceholder?: boolean
  /** Origin of this command entry. */
  source?: 'builtin' | 'workspace'
  /**
   * Extra search terms for the palette, as i18n keys. A palette that only matched
   * English would be unusable in the other locale, and zh-CN is this app's default.
   */
  keywordKeys?: string[]
  /** Optional direct search keywords for dynamic workspace commands. */
  keywords?: string[]
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
 * One navigation entry per page. Written as a builder because the rows differ
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
    id: 'session.togglePartner',
    group: 'session',
    labelKey: 'composer.cmdTogglePartner',
    hintKey: 'composer.cmdTogglePartnerHint',
    keywordKeys: ['palette.keywordPartner'],
    run: (host) => host.toggleSessionPartner?.(),
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
  go('view.goSpace', 'space.title', 'space'),
  go('view.goCompanionCenter', 'space.companionCenter', 'companion-center'),
  {
    id: 'view.goCode',
    group: 'view',
    labelKey: 'palette.goCode',
    run: (host) => {
      if (host.routeName() !== 'home') host.navigate('home')
      if (typeof window !== 'undefined') {
        window.dispatchEvent(new CustomEvent('tinadec:focus-home-dock', { detail: { card: 'workspace' } }))
      }
    },
  },
  {
    id: 'view.goSnapshots',
    group: 'view',
    labelKey: 'palette.goSnapshots',
    run: (host) => {
      if (host.routeName() !== 'home') host.navigate('home')
      if (typeof window !== 'undefined') {
        window.dispatchEvent(new CustomEvent('tinadec:focus-home-dock', { detail: { card: 'git' } }))
      }
    },
  },
  {
    id: 'view.goGovernance',
    group: 'view',
    labelKey: 'palette.goGovernance',
    run: (host) => {
      if (host.routeName() !== 'home') host.navigate('home')
      if (typeof window !== 'undefined') {
        window.dispatchEvent(new CustomEvent('tinadec:focus-home-dock', { detail: { card: 'approval', approvalTab: 'pending' } }))
      }
    },
  },
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

export function resolveCommandLabel(command: AppCommand, t: (key: string) => string): string {
  return command.label ?? (command.labelKey ? t(command.labelKey) : command.slash ?? command.id)
}

export function resolveCommandHint(command: AppCommand, t: (key: string) => string): string | undefined {
  if (command.hint) return command.hint
  return command.hintKey ? t(command.hintKey) : undefined
}

/** Merge workspace `.tinadec/commands/*.md` entries without colliding on built-in slash triggers. */
export function mergeWorkspaceCommands(
  baseCommands: readonly AppCommand[],
  workspaceCommands: readonly AppCommand[],
): AppCommand[] {
  if (!workspaceCommands.length) return [...baseCommands]
  const usedSlashes = new Set(baseCommands.map((cmd) => cmd.slash?.toLowerCase()).filter(Boolean))
  const usedIds = new Set(baseCommands.map((cmd) => cmd.id))
  const merged = [...baseCommands]
  for (const cmd of workspaceCommands) {
    if (usedIds.has(cmd.id)) continue
    const slash = cmd.slash?.toLowerCase()
    if (slash && usedSlashes.has(slash)) {
      const prefixed = `ws:${slash}`
      if (!usedSlashes.has(prefixed)) {
        usedSlashes.add(prefixed)
        usedIds.add(cmd.id)
        merged.push({ ...cmd, slash: prefixed })
      }
      continue
    }
    if (slash) usedSlashes.add(slash)
    usedIds.add(cmd.id)
    merged.push(cmd)
  }
  return merged
}

export function availableCommands(host: CommandHost, extra: readonly AppCommand[] = []): AppCommand[] {
  return mergeWorkspaceCommands(appCommands, extra).filter((command) => isCommandAvailable(command, host))
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

export function slashCommands(extra: readonly AppCommand[] = []): AppCommand[] {
  return mergeWorkspaceCommands(appCommands, extra).filter((command) => command.slash)
}

export function filterSlashCommands(
  text: string,
  host: CommandHost,
  extra: readonly AppCommand[] = [],
): AppCommand[] {
  if (!text.startsWith('/')) return []
  const query = text.slice(1).trimStart().split(/\s+/)[0]?.toLowerCase() ?? ''
  return slashCommands(extra).filter(
    (command) => command.slash!.startsWith(query) && isCommandAvailable(command, host),
  )
}

export function parseSlashCommand(text: string, extra: readonly AppCommand[] = []): SlashParse {
  const trimmed = text.trim()
  if (!trimmed.startsWith('/')) return { kind: 'not-a-command' }
  const [token, ...rest] = trimmed.slice(1).split(/\s+/)
  const command = slashCommands(extra).find((item) => item.slash === token?.toLowerCase())
  if (!command) return { kind: 'not-a-command' }
  const argument = rest.join(' ').trim()
  if (command.needsArgument && !argument) return { kind: 'incomplete', command }
  return { kind: 'run', command, argument }
}
