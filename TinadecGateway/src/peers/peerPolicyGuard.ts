/**
 * PeerPolicyGuard:
 * 1. Enforces the 3-tier role permission matrix (`observer` / `operator` / `owner`),
 *    absolute self-security blocklist, `workspace_mode === 'selected'` whitelist, and
 *    `sync_all_sessions === 0` initiator isolation.
 * 2. Strips any untrusted incoming privileged headers (`x-tinadec-host-control`, etc.)
 *    and injects authoritative peer audit headers (`x-tinadec-peer-controller-id`,
 *    `x-tinadec-peer-controller-name`, `x-tinadec-peer-role`) for Core CAS & queueing.
 * 3. Subscribes to Core `/api/v1/peers/internal/policy-events` SSE to enforce <100ms
 *    instant revocation / disconnect of all active mTLS sockets, HTTP/SSE streams, and WS terminals.
 */

import { ensureRequestId, PRINCIPAL_VALUE } from '../headers.js';
import type { PeerRole } from './peerProtocol.js';

export type PeerWorkspaceMode = 'all' | 'selected';

export interface PeerAccessPolicy {
  controller_device_id: string;
  role: PeerRole;
  workspace_mode: PeerWorkspaceMode;
  workspace_ids?: string[] | null;
  workspace_ids_json?: string | null;
  allow_non_workspace_sessions?: boolean | number;
  sync_all_sessions?: boolean | number;
  allow_agent_delegation?: boolean | number;
  allow_provider_proxy?: boolean | number;
  revision: string;
}

export interface PeerControllerContext {
  controllerDeviceId: string;
  displayName: string;
  clientFingerprint: string;
  policy: PeerAccessPolicy;
}

export interface SessionScopeMetadata {
  sessionId: string;
  projectId: string | null;
  initiatorDeviceId: string | null;
}

export interface PolicyEvaluationInput {
  kind: 'http' | 'ws';
  method: string;
  path: string;
  query?: string | URLSearchParams;
  headers?: Record<string, string>;
  bodyJson?: Record<string, unknown> | null;
  controller: PeerControllerContext;
  /** Optional resolver mapping storage_id -> project_id when workspace_mode === 'selected'. */
  resolveStorageProjectId?: (storageId: string) => string | null | undefined;
  /** Optional session metadata lookup for selected workspaces & sync_all_sessions=0 checks. */
  sessionMeta?: SessionScopeMetadata | null;
}

export type PolicyDecision =
  | { allowed: true }
  | { allowed: false; status: 403; code: 'policy_denied'; message: string };

export function toBoolFlag(value: boolean | number | null | undefined, fallback = false): boolean {
  if (typeof value === 'boolean') return value;
  if (typeof value === 'number') return value !== 0;
  return fallback;
}

export function parseAllowedWorkspaceIds(policy: PeerAccessPolicy): Set<string> {
  const out = new Set<string>();
  if (Array.isArray(policy.workspace_ids)) {
    for (const id of policy.workspace_ids) {
      const trimmed = String(id ?? '').trim();
      if (trimmed) out.add(trimmed);
    }
  }
  if (typeof policy.workspace_ids_json === 'string' && policy.workspace_ids_json.trim().length > 0) {
    try {
      const parsed = JSON.parse(policy.workspace_ids_json) as unknown;
      if (Array.isArray(parsed)) {
        for (const id of parsed) {
          const trimmed = String(id ?? '').trim();
          if (trimmed) out.add(trimmed);
        }
      }
    } catch {
      // Ignore malformed JSON; fail closed with whatever valid IDs were parsed.
    }
  }
  return out;
}

function deny(message: string): PolicyDecision {
  return {
    allowed: false,
    status: 403,
    code: 'policy_denied',
    message,
  };
}

function normalizePath(rawPath: string): string {
  const qIdx = rawPath.indexOf('?');
  const pathname = qIdx >= 0 ? rawPath.slice(0, qIdx) : rawPath;
  let decoded = pathname;
  try {
    decoded = decodeURIComponent(pathname);
  } catch {
    // Keep raw pathname if malformed percent-encoding; reject dot-segments below.
  }
  const collapsed = decoded.replace(/\/{2,}/g, '/');
  return collapsed.length > 1 && collapsed.endsWith('/') ? collapsed.slice(0, -1) : collapsed;
}

function hasPathTraversal(rawPath: string): boolean {
  const qIdx = rawPath.indexOf('?');
  const pathname = qIdx >= 0 ? rawPath.slice(0, qIdx) : rawPath;
  if (/(?:^|\/)\.\.(?:\/|$)/i.test(pathname) || /%2e%2e/i.test(pathname) || pathname.includes('\\')) {
    return true;
  }
  return false;
}

const OBSERVER_READONLY_TOOL_IDS = new Set([
  'read_file',
  'view_file',
  'list_dir',
  'list_directory',
  'glob',
  'grep',
  'search_files',
  'git_status',
  'git_diff',
  'git_log',
  'git_log_list',
  'git_log_detail',
  'git_branch_list',
  'git_worktree_list',
  'git_file_history',
  'git_blame',
  'git_file_at_revision',
  'git_stash_list',
  'git_stash_show',
]);

const OBSERVER_READONLY_GIT_FACADE_ACTIONS = new Set([
  'status',
  'diff_preview',
  'diff_compare',
  'branch_list',
  'worktrees',
  'log',
  'log_list',
  'log_detail',
  'file_history',
  'blame',
  'file_at_revision',
  'stash_list',
  'stash_show',
]);

function isObserverAllowedToolExecute(path: string, bodyJson?: Record<string, unknown> | null): boolean {
  const match = /^\/api\/v1\/(?:code\/|tool-runtime\/)?tools\/([^/]+)\/execute$/i.exec(path);
  if (!match) return false;
  const toolId = decodeURIComponent(match[1]!).trim().toLowerCase();
  if (OBSERVER_READONLY_TOOL_IDS.has(toolId)) return true;
  if (toolId === 'git_worktree_manager') {
    const args =
      bodyJson && typeof bodyJson.arguments === 'object' && bodyJson.arguments !== null
        ? (bodyJson.arguments as Record<string, unknown>)
        : bodyJson;
    const action = typeof args?.action === 'string' ? args.action.trim().toLowerCase() : '';
    return OBSERVER_READONLY_GIT_FACADE_ACTIONS.has(action);
  }
  return false;
}

function isAbsoluteBlockedPeerPath(path: string, policy: PeerAccessPolicy): string | null {
  const lower = path.toLowerCase();

  // Wave 3 provider-proxy internal callback is permitted ONLY when allow_provider_proxy = 1.
  if (lower === '/api/v1/peers/internal/provider-proxy/completions') {
    if (!toBoolFlag(policy.allow_provider_proxy, false)) {
      return 'Provider proxy completions are disabled by peer access policy.';
    }
    return null;
  }

  // Wave 2 DevServer preview lease internal endpoint is permitted over authenticated mTLS
  // tunnels ONLY for operator/owner roles (observer is strictly denied 403 policy_denied).
  if (
    lower === '/api/v1/peers/internal/devserver-lease' ||
    lower.startsWith('/api/v1/peers/internal/devserver-lease/')
  ) {
    if (policy.role === 'observer') {
      return 'Observer role cannot request or manage DevServer preview leases.';
    }
    return null;
  }

  const forbiddenPrefixes = [
    '/api/v1/peers/inbound',
    '/api/v1/peers/pairing-code',
    '/api/v1/peers/grants',
    '/api/v1/peers/devices',
    '/api/v1/peers/policies',
    '/api/v1/peers/internal',
    '/api/v1/web-auth',
    '/api/v1/host-challenge',
    '/api/v1/mcp/tinadec',
    '/auth',
  ];

  for (const prefix of forbiddenPrefixes) {
    if (lower === prefix || lower.startsWith(`${prefix}/`)) {
      return `Remote peers are strictly forbidden from accessing host security endpoint ${path}.`;
    }
  }

  // Block proxy-chaining `/api/v1/peers/:id/proxy/*` from remote peers.
  if (/^\/api\/v1\/peers\/[^/]+\/proxy(?:\/|$)/i.test(lower)) {
    return 'Nested multi-hop peer proxy chaining is forbidden.';
  }

  return null;
}

function checkOwnerOnlyCapability(method: string, path: string, policy: PeerAccessPolicy): string | null {
  const lower = path.toLowerCase();
  const upperMethod = method.toUpperCase();
  const isMutation = upperMethod !== 'GET' && upperMethod !== 'HEAD' && upperMethod !== 'OPTIONS';

  // Delegation endpoints: observer denied; operator requires allow_agent_delegation=1; owner allowed.
  if (
    lower === '/api/v1/peers/delegate' ||
    lower.startsWith('/api/v1/peers/delegate/') ||
    lower.startsWith('/api/v1/peers/delegations')
  ) {
    if (policy.role === 'owner') return null;
    if (policy.role === 'operator' && toBoolFlag(policy.allow_agent_delegation, false)) return null;
    return 'Agent task delegation is not permitted for this peer policy.';
  }

  if (policy.role === 'owner') {
    return null;
  }

  // Host arbitrary filesystem directory browsing/creation: owner only.
  if (
    lower === '/api/v1/filesystem' ||
    lower.startsWith('/api/v1/filesystem/') ||
    lower === '/api/v1/fs' ||
    lower.startsWith('/api/v1/fs/')
  ) {
    return 'Host filesystem browsing and directory creation require owner role.';
  }

  // Creating, renaming, archiving, trashing, restoring, or deleting workspaces: owner only.
  if (lower === '/api/v1/projects' && isMutation) {
    return 'Creating workspaces requires owner role.';
  }
  if (/^\/api\/v1\/projects\/[^/]+(?:\/(?:archive|trash|restore))?$/i.test(lower) && isMutation) {
    return 'Modifying or deleting workspaces requires owner role.';
  }

  // Storage scopes & TOML configuration mutations: owner only.
  if (
    (lower === '/api/v1/storage/scopes' || lower.startsWith('/api/v1/storage/scopes/')) &&
    isMutation
  ) {
    return 'Creating, configuring, or deleting storage scopes requires owner role.';
  }
  if (
    (lower === '/api/v1/configuration/documents' ||
      lower.startsWith('/api/v1/configuration/documents/')) &&
    isMutation
  ) {
    return 'Modifying host configuration documents requires owner role.';
  }

  // Global preferences, model providers, extensions, MCP servers, governance bundles: owner only on mutation.
  const globalConfigPrefixes = [
    '/api/v1/preferences',
    '/api/v1/model-providers',
    '/api/v1/model-routes',
    '/api/v1/model-settings',
    '/api/v1/lifecycle-hooks',
    '/api/v1/extensions',
    '/api/v1/mcp/servers',
    '/api/v1/agent-packs',
    '/api/v1/workspace-defaults',
    '/api/v1/governance/policy-bundles',
    '/api/v1/governance/capability-grants',
    '/api/v1/governance/approval-delegations',
    '/api/v1/governance/capability-leases',
    '/api/v1/approval-rules',
  ];
  if (isMutation) {
    for (const prefix of globalConfigPrefixes) {
      if (lower === prefix || lower.startsWith(`${prefix}/`)) {
        return `Modifying global host settings (${prefix}) requires owner role.`;
      }
    }
  }

  return null;
}

function checkObserverCapability(
  kind: 'http' | 'ws',
  method: string,
  path: string,
  bodyJson?: Record<string, unknown> | null,
): string | null {
  if (kind === 'ws') {
    return 'Observer role cannot open interactive WebSocket terminals or mutable channels.';
  }
  const upperMethod = method.toUpperCase();
  if (upperMethod === 'GET' || upperMethod === 'HEAD' || upperMethod === 'OPTIONS') {
    return null;
  }
  if (upperMethod === 'POST' && isObserverAllowedToolExecute(path, bodyJson)) {
    return null;
  }
  return `Observer role is read-only and cannot perform ${upperMethod} ${path}.`;
}

function getHeaderCaseInsensitive(
  headers: Record<string, string> | undefined,
  targetName: string,
): string | undefined {
  if (!headers) return undefined;
  const lower = targetName.toLowerCase();
  for (const [k, v] of Object.entries(headers)) {
    if (k.toLowerCase() === lower) return v;
  }
  return undefined;
}

/**
 * Evaluate whether an inbound mTLS peer request or WebSocket tunnel open is permitted.
 */
export function evaluatePeerPolicy(input: PolicyEvaluationInput): PolicyDecision {
  const { controller } = input;
  const policy = controller.policy;

  if (hasPathTraversal(input.path)) {
    return deny('Path traversal segments are forbidden on peer proxy routes.');
  }

  const path = normalizePath(input.path);

  const blockedReason = isAbsoluteBlockedPeerPath(path, policy);
  if (blockedReason) {
    return deny(blockedReason);
  }

  const ownerReason = checkOwnerOnlyCapability(input.method, path, policy);
  if (ownerReason) {
    return deny(ownerReason);
  }

  if (policy.role === 'observer') {
    const observerReason = checkObserverCapability(input.kind, input.method, path, input.bodyJson);
    if (observerReason) {
      return deny(observerReason);
    }
  }

  const search =
    input.query instanceof URLSearchParams
      ? input.query
      : new URLSearchParams(typeof input.query === 'string' ? input.query.replace(/^\?/, '') : '');

  // Workspace whitelist enforcement when workspace_mode === 'selected'.
  if (policy.workspace_mode === 'selected') {
    const allowedWorkspaces = parseAllowedWorkspaceIds(policy);
    const allowNonWorkspace = toBoolFlag(policy.allow_non_workspace_sessions, false);

    // 1. Direct `/api/v1/projects/:projectId...`
    const projectMatch = /^\/api\/v1\/projects\/([^/]+)(?:\/|$)/i.exec(path);
    if (projectMatch) {
      const projectId = decodeURIComponent(projectMatch[1]!).trim();
      if (!allowedWorkspaces.has(projectId)) {
        return deny(`Workspace '${projectId}' is not authorized by selected workspace whitelist.`);
      }
    }

    // 2. Query or JSON body `project_id` / `projectId`
    const queryProjectId = (search.get('project_id') ?? search.get('projectId'))?.trim();
    if (queryProjectId && !allowedWorkspaces.has(queryProjectId)) {
      return deny(`Workspace '${queryProjectId}' is not authorized by selected workspace whitelist.`);
    }

    const bodyProjectIdRaw = input.bodyJson?.project_id ?? input.bodyJson?.projectId;
    const bodyProjectId = typeof bodyProjectIdRaw === 'string' ? bodyProjectIdRaw.trim() : '';
    if (bodyProjectId && !allowedWorkspaces.has(bodyProjectId)) {
      return deny(`Workspace '${bodyProjectId}' is not authorized by selected workspace whitelist.`);
    }

    // Creating a projectless session when allow_non_workspace_sessions === 0.
    if (
      path.toLowerCase() === '/api/v1/sessions' &&
      input.method.toUpperCase() === 'POST' &&
      !bodyProjectId &&
      !queryProjectId &&
      !allowNonWorkspace
    ) {
      return deny('Projectless sessions are disabled for this peer policy.');
    }

    // 3. Storage header / query scope check if resolver knows the backing project_id.
    const storageId = (
      getHeaderCaseInsensitive(input.headers, 'x-tinadec-storage-id') ??
      search.get('storage_id')
    )?.trim();
    if (storageId && input.resolveStorageProjectId) {
      const mappedProjectId = input.resolveStorageProjectId(storageId);
      if (mappedProjectId && !allowedWorkspaces.has(mappedProjectId)) {
        return deny(`Storage scope '${storageId}' belongs to unauthorized workspace '${mappedProjectId}'.`);
      }
      if (!mappedProjectId && allowedWorkspaces.has(storageId) === false && storageId === 'user' && !allowNonWorkspace) {
        // User-global scope for session mutations when non-workspace sessions are disallowed
        if (path.toLowerCase().startsWith('/api/v1/sessions') && input.method.toUpperCase() === 'POST' && !bodyProjectId) {
          return deny('Non-workspace user scope sessions are disabled for this peer policy.');
        }
      }
    }

    // 4. Session-scoped check via resolved session metadata.
    if (input.sessionMeta) {
      if (input.sessionMeta.projectId) {
        if (!allowedWorkspaces.has(input.sessionMeta.projectId)) {
          return deny(
            `Session '${input.sessionMeta.sessionId}' belongs to unauthorized workspace '${input.sessionMeta.projectId}'.`,
          );
        }
      } else if (!allowNonWorkspace) {
        return deny(`Non-workspace session '${input.sessionMeta.sessionId}' is not authorized.`);
      }
    }
  }

  // Initiator isolation enforcement when sync_all_sessions === 0.
  if (!toBoolFlag(policy.sync_all_sessions, false) && input.sessionMeta) {
    const initiator = input.sessionMeta.initiatorDeviceId ?? '';
    if (initiator !== controller.controllerDeviceId) {
      return deny(
        `Session '${input.sessionMeta.sessionId}' was not initiated by this peer device (sync_all_sessions=0).`,
      );
    }
  }

  return { allowed: true };
}

/**
 * Extract target sessionId from `/api/v1/sessions/:sessionId...` or `/api/v1/events?session_id=...`.
 */
export function extractTargetSessionId(path: string, query?: string | URLSearchParams): string | null {
  const norm = normalizePath(path);
  const match = /^\/api\/v1\/sessions\/([^/]+)(?:\/|$)/i.exec(norm);
  if (match) {
    return decodeURIComponent(match[1]!).trim() || null;
  }
  if (norm.toLowerCase() === '/api/v1/events') {
    const search =
      query instanceof URLSearchParams
        ? query
        : new URLSearchParams(typeof query === 'string' ? query.replace(/^\?/, '') : '');
    const sid = (search.get('session_id') ?? search.get('sessionId'))?.trim();
    return sid || null;
  }
  return null;
}

/**
 * Filter a JSON list response (`GET /api/v1/projects`, `GET /api/v1/sessions`, `GET /api/v1/storage/scopes`)
 * according to the controller's active `PeerAccessPolicy`.
 */
export function filterPeerCollectionResponse(
  path: string,
  data: unknown,
  controller: PeerControllerContext,
  knownSessionInitiators?: ReadonlyMap<string, string>,
): unknown {
  const norm = normalizePath(path).toLowerCase();
  const policy = controller.policy;
  const selectedMode = policy.workspace_mode === 'selected';
  const allowedWorkspaces = selectedMode ? parseAllowedWorkspaceIds(policy) : null;
  const allowNonWorkspace = toBoolFlag(policy.allow_non_workspace_sessions, false);
  const syncAllSessions = toBoolFlag(policy.sync_all_sessions, false);

  if (norm === '/api/v1/projects' && selectedMode && Array.isArray(data)) {
    return data.filter((item) => {
      if (!item || typeof item !== 'object') return false;
      const rec = item as Record<string, unknown>;
      const id = String(rec.id ?? rec.project_id ?? rec.projectId ?? '').trim();
      return id.length > 0 && allowedWorkspaces!.has(id);
    });
  }

  if (norm === '/api/v1/storage/scopes' && selectedMode && Array.isArray(data)) {
    return data.filter((item) => {
      if (!item || typeof item !== 'object') return false;
      const rec = item as Record<string, unknown>;
      const projectId = String(rec.project_id ?? rec.projectId ?? '').trim();
      if (projectId) return allowedWorkspaces!.has(projectId);
      const storageId = String(rec.storage_id ?? rec.storageId ?? '').trim();
      if (allowedWorkspaces!.has(storageId)) return true;
      return allowNonWorkspace && String(rec.scope_kind ?? '') === 'user';
    });
  }

  if (norm === '/api/v1/sessions' && (selectedMode || !syncAllSessions) && Array.isArray(data)) {
    return data.filter((item) => {
      if (!item || typeof item !== 'object') return false;
      const rec = item as Record<string, unknown>;
      const sessionId = String(rec.id ?? rec.session_id ?? rec.sessionId ?? '').trim();
      const projectIdRaw = rec.project_id ?? rec.projectId ?? null;
      const projectId = typeof projectIdRaw === 'string' && projectIdRaw.trim() ? projectIdRaw.trim() : null;

      if (selectedMode) {
        if (projectId) {
          if (!allowedWorkspaces!.has(projectId)) return false;
        } else if (!allowNonWorkspace) {
          return false;
        }
      }

      if (!syncAllSessions) {
        const initiatorFromDto =
          rec.initiator_device_id ??
          rec.initiatorDeviceId ??
          rec.origin_device_id ??
          rec.originDeviceId ??
          rec.created_by_device_id;
        const initiator =
          typeof initiatorFromDto === 'string' && initiatorFromDto.trim().length > 0
            ? initiatorFromDto.trim()
            : knownSessionInitiators?.get(sessionId) ?? '';
        if (initiator !== controller.controllerDeviceId) {
          return false;
        }
      }

      return true;
    });
  }

  return data;
}

const STRIPPED_INCOMING_HEADERS = new Set([
  'x-tinadec-host-control',
  'x-tinadec-principal',
  'x-tinadec-session',
  'x-tinadec-peer-controller-id',
  'x-tinadec-peer-controller-name',
  'x-tinadec-peer-role',
  'authorization',
  'x-api-key',
  'cookie',
  'cf-access-jwt-assertion',
  'cf-connecting-ip',
  'cf-ray',
  'x-forwarded-for',
  'x-forwarded-host',
  'x-forwarded-proto',
  'x-real-ip',
  'forwarded',
  'host',
  'connection',
  'keep-alive',
  'transfer-encoding',
  'upgrade',
  'content-length',
]);

/**
 * Strip untrusted/forged privilege headers from incoming tunnel frames and inject
 * authoritative local Core credential + peer controller audit headers.
 */
export function buildSanitizedPeerCoreHeaders(
  incoming: Record<string, string> | undefined,
  controller: PeerControllerContext,
  localHostControlToken?: string | null,
): Record<string, string> {
  const sanitized: Record<string, string> = {};
  let incomingRequestId: string | undefined;

  if (incoming) {
    for (const [rawKey, rawVal] of Object.entries(incoming)) {
      const lower = rawKey.trim().toLowerCase();
      if (!lower || STRIPPED_INCOMING_HEADERS.has(lower)) {
        continue;
      }
      if (lower === 'x-request-id') {
        incomingRequestId = rawVal;
        continue;
      }
      sanitized[lower] = rawVal;
    }
  }

  sanitized['x-request-id'] = ensureRequestId(incomingRequestId);
  sanitized['x-tinadec-principal'] = PRINCIPAL_VALUE;
  sanitized['x-tinadec-peer-controller-id'] = controller.controllerDeviceId;
  sanitized['x-tinadec-peer-controller-name'] = controller.displayName;
  sanitized['x-tinadec-peer-role'] = controller.policy.role;

  const hostToken = (localHostControlToken ?? process.env.TINADEC_HOST_CONTROL_TOKEN ?? '').trim();
  if (hostToken) {
    sanitized['x-tinadec-host-control'] = hostToken;
  }

  return sanitized;
}

// ── Policy Events SSE Subscriber (< 100ms Instant Teardown) ───────────────────

export interface PeerPolicyEvent {
  type: 'policy_updated' | 'grant_revoked' | 'inbound_updated' | string;
  controller_device_id?: string;
  controllerDeviceId?: string;
  revision?: string;
  policy?: PeerAccessPolicy;
  inbound_enabled?: boolean;
  inboundEnabled?: boolean;
}

export interface PolicyEventSubscriberOptions {
  coreUrl: () => string;
  hostControlToken?: () => string | undefined;
  fetchImpl?: typeof fetch;
  reconnectDelayMs?: number;
  onPolicyEvent: (event: PeerPolicyEvent) => void;
}

export class PeerPolicyEventSubscriber {
  private readonly options: PolicyEventSubscriberOptions;
  private abortController: AbortController | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private stopped = true;

  constructor(options: PolicyEventSubscriberOptions) {
    this.options = options;
  }

  start(): void {
    if (!this.stopped) return;
    this.stopped = false;
    void this.loop();
  }

  stop(): void {
    this.stopped = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.abortController) {
      this.abortController.abort();
      this.abortController = null;
    }
  }

  private async loop(): Promise<void> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    const reconnectDelay = this.options.reconnectDelayMs ?? 1_000;

    while (!this.stopped) {
      const abort = new AbortController();
      this.abortController = abort;
      try {
        const headers: Record<string, string> = {
          accept: 'text/event-stream',
          'x-tinadec-principal': 'gateway-internal',
          'x-request-id': ensureRequestId(null),
        };
        const token = (this.options.hostControlToken?.() ?? process.env.TINADEC_HOST_CONTROL_TOKEN ?? '').trim();
        if (token) headers['x-tinadec-host-control'] = token;

        const endpoint = new URL('/api/v1/peers/internal/policy-events', this.options.coreUrl()).toString();
        const res = await fetchImpl(endpoint, {
          headers,
          signal: abort.signal,
        });

        if (!res.ok || !res.body) {
          await this.sleep(reconnectDelay);
          continue;
        }

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        while (!this.stopped && !abort.signal.aborted) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true }).replace(/\r\n/g, '\n');
          let sepIdx: number;
          while ((sepIdx = buffer.indexOf('\n\n')) >= 0) {
            const rawBlock = buffer.slice(0, sepIdx);
            buffer = buffer.slice(sepIdx + 2);
            this.dispatchSseBlock(rawBlock);
          }
        }
      } catch {
        // Reconnect unless stopped.
      }

      if (!this.stopped) {
        await this.sleep(reconnectDelay);
      }
    }
  }

  private dispatchSseBlock(block: string): void {
    let eventName = '';
    const dataLines: string[] = [];
    for (const line of block.split('\n')) {
      if (!line || line.startsWith(':')) continue;
      const idx = line.indexOf(':');
      const key = idx < 0 ? line : line.slice(0, idx);
      const val = idx < 0 ? '' : line.slice(idx + 1).replace(/^ /, '');
      if (key === 'event') eventName = val.trim();
      else if (key === 'data') dataLines.push(val);
    }
    if (dataLines.length === 0) return;
    try {
      const parsed = JSON.parse(dataLines.join('\n')) as PeerPolicyEvent;
      if (!parsed.type && eventName) {
        parsed.type = eventName;
      }
      this.options.onPolicyEvent(parsed);
    } catch {
      // Ignore heartbeat or non-JSON comment frames.
    }
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => {
      if (this.stopped) {
        resolve();
        return;
      }
      this.reconnectTimer = setTimeout(() => {
        this.reconnectTimer = null;
        resolve();
      }, ms);
      this.reconnectTimer.unref?.();
    });
  }
}
