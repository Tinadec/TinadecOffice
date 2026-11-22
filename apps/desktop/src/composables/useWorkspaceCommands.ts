import { computed, ref, watch, type MaybeRefOrGetter, toValue } from 'vue'
import { api } from '@/api'
import { useHostAccess } from '@/lib/hostAccess'
import { readFileText, toDirEntryView, type DirEntryDto, type ReadFileDataDto } from '@/lib/workspaceSearch'
import type { AppCommand } from '@/lib/appCommands'

const COMMANDS_DIR = '.tinadec/commands'
const APM_PROMPTS_DIR = '.apm/prompts'
const CACHE_TTL_MS = 30_000
const MAX_WORKSPACE_COMMANDS = 50

const ARGUMENTS_PLACEHOLDER_RE = /\$ARGUMENTS\b/g
const APM_INPUT_PLACEHOLDER_RE = /\$\{input(?::[^}]*)?\}/g

function hasTemplatePlaceholder(text: string): boolean {
  return /\$ARGUMENTS\b|\$\{input(?::[^}]*)?\}/.test(text)
}

export interface WorkspaceSlashCommand {
  id: string
  name: string
  slash: string
  description: string
  hint?: string
  body: string
  hasArgumentsPlaceholder: boolean
  path: string
  sourceKind?: 'tinadec' | 'apm'
}

interface CacheEntry {
  at: number
  commands: WorkspaceSlashCommand[]
}

const commandCache = new Map<string, CacheEntry>()
const inFlight = new Map<string, Promise<WorkspaceSlashCommand[]>>()
const sharedWorkspaceRoot = ref('')

function normalizeRoot(cwd: string): string {
  return (cwd ?? '').trim().replace(/\\/g, '/').replace(/\/+$/, '')
}

function unquoteYamlValue(raw: string): string {
  const trimmed = raw.trim()
  if (
    (trimmed.startsWith('"') && trimmed.endsWith('"') && trimmed.length >= 2)
    || (trimmed.startsWith("'") && trimmed.endsWith("'") && trimmed.length >= 2)
  ) {
    return trimmed.slice(1, -1)
  }
  return trimmed
}

/**
 * Parse optional YAML frontmatter (`description`, `hint`, `name`, `title`, `slash`)
 * plus markdown body from a `.tinadec/commands/*.md` or `.apm/prompts/*.prompt.md` file.
 */
export function parseWorkspaceCommandMarkdown(fileNameOrPath: string, rawContent: string): WorkspaceSlashCommand {
  const normalizedPath = fileNameOrPath.replace(/\\/g, '/').replace(/^\.\//, '')
  const isApmPrompt = /^\.apm\/prompts\//i.test(normalizedPath) || /\.prompt\.md$/i.test(normalizedPath)
  const relPath = normalizedPath.includes('/')
    ? normalizedPath
    : `${isApmPrompt ? APM_PROMPTS_DIR : COMMANDS_DIR}/${normalizedPath}`
  const baseSlug = normalizedPath
    .replace(new RegExp(`^${COMMANDS_DIR.replace('.', '\\.')}/`, 'i'), '')
    .replace(new RegExp(`^${APM_PROMPTS_DIR.replace('.', '\\.')}/`, 'i'), '')
    .replace(/\.prompt\.md$/i, '')
    .replace(/\.md$/i, '')
    .replace(/\//g, ':')
    .trim()

  const content = (rawContent ?? '').replace(/\r\n/g, '\n')
  const frontmatter: Record<string, string> = {}
  let body = content

  const fmMatch = /^---\s*\n([\s\S]*?)\n---\s*(?:\n|$)/.exec(content)
  if (fmMatch) {
    const fmBlock = fmMatch[1] ?? ''
    for (const line of fmBlock.split('\n')) {
      const pair = /^\s*([A-Za-z0-9_-]+)\s*:\s*(.*)$/.exec(line)
      if (!pair) continue
      const key = pair[1]!.toLowerCase()
      const value = unquoteYamlValue(pair[2] ?? '')
      if (value) frontmatter[key] = value
    }
    body = content.slice(fmMatch[0].length)
  }

  const trimmedBody = body.trim()
  const firstHeadingOrLine = trimmedBody
    .split('\n')
    .map((line) => line.replace(/^#+\s*/, '').trim())
    .find((line) => line.length > 0) ?? ''

  const slashRaw = (frontmatter.slash || baseSlug || 'custom')
    .replace(/^\/+/, '')
    .toLowerCase()
    .replace(/\s+/g, '-')
  const name = frontmatter.name || frontmatter.title || baseSlug || slashRaw
  const description = frontmatter.description || frontmatter.hint || firstHeadingOrLine.slice(0, 120)
  const hint = frontmatter.hint && frontmatter.hint !== description ? frontmatter.hint : undefined
  const hasArgumentsPlaceholder = hasTemplatePlaceholder(trimmedBody)

  return {
    id: `workspace.${slashRaw}`,
    name,
    slash: slashRaw,
    description,
    hint,
    body: trimmedBody,
    hasArgumentsPlaceholder,
    path: relPath,
    sourceKind: isApmPrompt ? 'apm' : 'tinadec',
  }
}

/**
 * Expand a workspace command markdown template, substituting `$ARGUMENTS` and APM
 * `${input:...}` placeholders when present or appending any provided argument cleanly.
 */
export function renderWorkspaceCommandTemplate(command: WorkspaceSlashCommand, argument = ''): string {
  const arg = argument.trim()
  const containsPlaceholder = command.hasArgumentsPlaceholder || hasTemplatePlaceholder(command.body)
  if (containsPlaceholder) {
    if (!arg) return command.body
    return command.body
      .replace(ARGUMENTS_PLACEHOLDER_RE, () => arg)
      .replace(APM_INPUT_PLACEHOLDER_RE, () => arg)
      .trim()
  }
  if (arg) {
    return command.body ? `${command.body}\n\n${arg}` : arg
  }
  return command.body
}

/** Convert a discovered `.tinadec/commands/*.md` or `.apm/prompts/*.prompt.md` entry into an `AppCommand` for Palette & Slash surfaces. */
export function workspaceCommandToAppCommand(command: WorkspaceSlashCommand): AppCommand {
  return {
    id: command.id,
    group: 'workspace',
    labelKey: '',
    label: command.name,
    hint: command.description || command.hint,
    slash: command.slash,
    template: command.body,
    hasArgumentsPlaceholder: command.hasArgumentsPlaceholder,
    source: 'workspace',
    keywords: [
      command.name,
      command.slash,
      command.description,
      command.hint ?? '',
      command.path,
      command.sourceKind === 'apm' ? 'apm' : '',
    ].filter(Boolean),
    run: (host, argument) => {
      const rendered = renderWorkspaceCommandTemplate(command, argument)
      if (host.routeName() !== 'home') host.navigate('home')
      host.setDraft(rendered)
    },
  }
}

export function setActiveWorkspaceCommandsRoot(cwd: string | null | undefined): void {
  sharedWorkspaceRoot.value = normalizeRoot(cwd ?? '')
}

export function invalidateWorkspaceCommands(cwd?: string): void {
  if (cwd) {
    commandCache.delete(normalizeRoot(cwd))
    inFlight.delete(normalizeRoot(cwd))
  } else {
    commandCache.clear()
    inFlight.clear()
  }
}

export function peekWorkspaceCommands(cwd: string | null | undefined): WorkspaceSlashCommand[] {
  const root = normalizeRoot(cwd ?? '')
  if (!root) return []
  const hit = commandCache.get(root)
  if (!hit) return []
  if (Date.now() - hit.at > CACHE_TTL_MS) {
    commandCache.delete(root)
    return []
  }
  return hit.commands
}

/**
 * Discover and parse `.tinadec/commands/*.md` and `.apm/prompts/*.prompt.md` files under `cwd`.
 * `.tinadec/commands` wins over `.apm/prompts` on slash collision.
 * Safely degrades to `[]` when backend is unavailable, directories do not exist,
 * or file tools are unmocked in unit tests.
 */
export async function loadWorkspaceCommands(
  cwd: string | null | undefined,
  options: { force?: boolean; signal?: AbortSignal } = {},
): Promise<WorkspaceSlashCommand[]> {
  const root = normalizeRoot(cwd ?? '')
  if (!root) return []
  const { canAccessBackend } = useHostAccess()
  if (!canAccessBackend.value) return []
  if (typeof api.listDirectory !== 'function' || typeof api.readFile !== 'function') return []

  if (!options.force) {
    const cached = peekWorkspaceCommands(root)
    if (cached.length > 0 || commandCache.has(root)) return cached
    const pending = inFlight.get(root)
    if (pending) return pending
  }

  async function readCommandDir(dir: string, filePattern: RegExp): Promise<WorkspaceSlashCommand[]> {
    try {
      const listing = await api.listDirectory(root, dir)
      if (options.signal?.aborted) return []
      const data = listing?.data as { entries?: DirEntryDto[]; success?: boolean } | undefined
      if (!data || data.success === false || !Array.isArray(data.entries)) return []

      const files = data.entries
        .map(toDirEntryView)
        .filter((entry): entry is NonNullable<typeof entry> => Boolean(entry && !entry.isDir && filePattern.test(entry.name)))
        .slice(0, MAX_WORKSPACE_COMMANDS)

      if (files.length === 0) return []

      const loaded = await Promise.all(
        files.map(async (entry) => {
          try {
            const relPath = `${dir}/${entry.name}`
            const file = await api.readFile(root, relPath)
            const fileData = file?.data as (ReadFileDataDto & { success?: boolean; content?: string })
            if (!fileData || fileData.success === false) return null
            const text = readFileText(fileData) || (typeof fileData.content === 'string' ? fileData.content : '')
            return parseWorkspaceCommandMarkdown(relPath, text)
          } catch {
            return null
          }
        }),
      )

      return loaded.filter((item): item is WorkspaceSlashCommand => item !== null)
    } catch {
      return []
    }
  }

  const request = (async (): Promise<WorkspaceSlashCommand[]> => {
    try {
      options.signal?.throwIfAborted()
      const tinadecPromise = readCommandDir(COMMANDS_DIR, /\.md$/i)
      const apmPromise = readCommandDir(APM_PROMPTS_DIR, /\.prompt\.md$/i)
      const [tinadecCommands, apmCommands] = await Promise.all([tinadecPromise, apmPromise])
      if (options.signal?.aborted) return []

      const bySlash = new Map<string, WorkspaceSlashCommand>()
      for (const cmd of tinadecCommands) {
        if (!bySlash.has(cmd.slash)) bySlash.set(cmd.slash, cmd)
      }
      for (const cmd of apmCommands) {
        if (!bySlash.has(cmd.slash)) bySlash.set(cmd.slash, cmd)
      }

      const commands = Array.from(bySlash.values())
        .sort((a, b) => a.slash.localeCompare(b.slash))
        .slice(0, MAX_WORKSPACE_COMMANDS)

      commandCache.set(root, { at: Date.now(), commands })
      return commands
    } catch {
      return []
    } finally {
      inFlight.delete(root)
    }
  })()

  inFlight.set(root, request)
  return request
}

export function useWorkspaceCommands(workspaceRoot?: MaybeRefOrGetter<string | null | undefined>) {
  const { canAccessBackend } = useHostAccess()
  const commands = ref<WorkspaceSlashCommand[]>([])
  const loading = ref(false)
  let seq = 0

  const effectiveRoot = computed(() => {
    const explicit = workspaceRoot !== undefined ? normalizeRoot(toValue(workspaceRoot) ?? '') : ''
    return explicit || sharedWorkspaceRoot.value
  })

  async function refresh(force = false): Promise<WorkspaceSlashCommand[]> {
    const root = effectiveRoot.value
    if (!root || !canAccessBackend.value) {
      commands.value = []
      return []
    }
    const token = ++seq
    loading.value = true
    try {
      const list = await loadWorkspaceCommands(root, { force })
      if (token !== seq) return list
      commands.value = list
      return list
    } finally {
      if (token === seq) loading.value = false
    }
  }

  watch([effectiveRoot, canAccessBackend], ([root, ready]) => {
    if (!root || !ready) {
      commands.value = []
      return
    }
    void refresh(false)
  }, { immediate: true })

  const appCommands = computed<AppCommand[]>(() => commands.value.map(workspaceCommandToAppCommand))

  return {
    commands,
    appCommands,
    loading,
    workspaceRoot: effectiveRoot,
    refresh,
  }
}
