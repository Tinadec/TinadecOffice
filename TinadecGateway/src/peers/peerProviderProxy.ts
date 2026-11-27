/**
 * Wave 3 Optional Controller Model Provider Proxy (`peerProviderProxy.ts`):
 *
 * Security & Engineering Invariants:
 * 1. Zero API Key Leakage:
 *    - Controller provider API keys (`Authorization: Bearer ...`, `x-api-key`, `sk-...`) NEVER leave
 *      the Controller machine/Core process and are actively redacted on every error/chunk boundary.
 * 2. Reverse mTLS Tunnel Forwarding (No Extra Port):
 *    - Worker Core (`PeerProxyModelProvider`) calls local Worker Gateway loopback
 *      `POST /api/v1/peers/internal/provider-proxy/completions` with `x-tinadec-peer-proxy-token`
 *      and `x-tinadec-peer-controller-target`.
 *    - Worker Gateway verifies local loopback + worker `allow_provider_proxy === true` on the active
 *      authenticated mTLS session from `controller_device_id`, then sends `http.req*` frames
 *      in reverse down that existing mTLS tunnel to Controller Gateway (`PeerClientPool`).
 * 3. Triple Gate on Controller:
 *    - (a) Arrived over authenticated mTLS tunnel (pinned X.509 certificate verified in handshake),
 *    - (b) Peer policy allows `allow_provider_proxy`,
 *    - (c) Short-lived `proxy_session_token` is valid, unexpired, and within `max_requests`.
 *    - Direct external/HTTP calls to `/api/v1/peers/internal/provider-proxy/*` without valid
 *      internal loopback worker tunnel context are unconditionally rejected with `403 policy_denied`.
 */

import { randomBytes, timingSafeEqual } from 'node:crypto';
import { ensureRequestId, PRINCIPAL_VALUE } from '../headers.js';
import { toProblemDetails } from '../mappers/errorMapper.js';
import { wrapSseWithKeepalive } from '../streaming.js';
import { isStrictLoopbackRequest, type ServerLike } from '../webAuth.js';
import { type PeerControllerContext, toBoolFlag } from './peerPolicyGuard.js';
import {
  type DecodedPeerFrame,
  PeerFrameConnection,
  type PeerHttpReqFrame,
  PeerProtocolError,
} from './peerProtocol.js';

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type AnyElysia = any;

export const PROVIDER_PROXY_COMPLETIONS_PATH = '/api/v1/peers/internal/provider-proxy/completions';
export const PROVIDER_PROXY_TOKENS_PATH = '/api/v1/peers/internal/provider-proxy/tokens';
export const PROVIDER_PROXY_SESSION_BINDING_PATH = '/api/v1/peers/internal/provider-proxy/session-binding';

export const HEADER_PROXY_TOKEN = 'x-tinadec-peer-proxy-token';
export const HEADER_CONTROLLER_TARGET = 'x-tinadec-peer-controller-target';
export const HEADER_USE_PROVIDER_PROXY = 'x-tinadec-peer-use-provider-proxy';
export const HEADER_VERIFIED_WORKER_ID = 'x-tinadec-peer-verified-worker-id';
export const HEADER_VERIFIED_DEVICE_ID = 'x-tinadec-peer-verified-device-id';
export const HEADER_GATEWAY_LOOPBACK_URL = 'x-tinadec-gateway-loopback-url';

const MAX_REVERSE_JSON_BYTES = 4 * 1024 * 1024;
const DEFAULT_TOKEN_TTL_MS = 30 * 60 * 1000;
const DEFAULT_MAX_REQUESTS = 64;

const RESPONSE_HOP_HEADERS = new Set([
  'connection',
  'keep-alive',
  'proxy-authenticate',
  'proxy-authorization',
  'te',
  'trailer',
  'transfer-encoding',
  'upgrade',
  'content-length',
  'content-encoding',
  'authorization',
  'x-api-key',
]);

export interface IssueProxySessionTokenInput {
  controller_device_id?: string;
  worker_device_id?: string;
  delegation_id?: string | null;
  session_id?: string | null;
  remote_session_id?: string | null;
  expires_at_ms?: number;
  ttl_seconds?: number;
  max_requests?: number;
}

export interface IssuedProxySessionTokenDto {
  proxy_session_token: string;
  token: string;
  controller_device_id: string;
  worker_device_id: string;
  delegation_id: string | null;
  session_id: string | null;
  expires_at_ms: number;
  max_requests: number;
}

interface FallbackGatewayTokenRecord {
  token: string;
  controllerDeviceId: string;
  workerDeviceId: string;
  delegationId: string | null;
  sessionId: string | null;
  expiresAtMs: number;
  maxRequests: number;
  usedRequests: number;
  revoked: boolean;
}

interface ActiveWorkerReverseSession {
  sessionId: string;
  controller: PeerControllerContext;
  connection: PeerFrameConnection;
  pendingReverseStreams: Map<string, PendingReverseHttpStream>;
}

interface PendingReverseHttpStream {
  streamId: string;
  headResolved: boolean;
  resolveHead: (res: Response) => void;
  rejectHead: (err: Error) => void;
  bodyController: ReadableStreamDefaultController<Uint8Array> | null;
  cleanupAbort?: () => void;
}

interface InFlightControllerReverseRequest {
  streamId: string;
  reqFrame: PeerHttpReqFrame;
  chunks: Buffer[];
  byteLength: number;
  abort: AbortController;
}

export interface ControllerReverseTunnelPeerContext {
  deviceId: string;
  remoteDeviceId: string;
  connection: PeerFrameConnection;
}

export interface PeerProviderProxyRuntimeOptions {
  coreUrl: () => string;
  fetchImpl?: typeof fetch;
  hostControlToken?: () => string | undefined;
  gatewayLoopbackUrl?: () => string | undefined;
  /** Optional custom upstream handler for standalone Gateway tests without a full C# Core. */
  fallbackUpstreamCompletions?: (input: {
    workerDeviceId: string;
    pairedDeviceId: string;
    tokenRecord: FallbackGatewayTokenRecord;
    bodyJson: Record<string, unknown>;
    rawBody: Buffer;
    acceptHeader: string;
    signal: AbortSignal;
  }) => Promise<Response>;
}

/**
 * Scrubs any Bearer token, vendor `sk-...` key, `x-api-key`, or explicit canary secret from text.
 */
export function redactProviderSecrets(input: string, explicitSecrets: readonly string[] = []): string {
  if (!input) return '';
  let out = input;
  for (const secret of explicitSecrets) {
    const trimmed = secret?.trim();
    if (trimmed && trimmed.length >= 4) {
      out = out.split(trimmed).join('[REDACTED]');
    }
  }
  out = out.replace(/(Bearer\s+)[A-Za-z0-9._~+/=-]{6,}/gi, '$1[REDACTED]');
  out = out.replace(/\b(?:sk|rk|pk|ak|ant|gsk|xai)-[A-Za-z0-9_-]{8,}\b/g, '[REDACTED]');
  out = out.replace(
    /(["']?(?:api[_-]?key|x-api-key|authorization|secret)["']?\s*[:=]\s*["']?)([^"'\s,}]{6,})/gi,
    '$1[REDACTED]',
  );
  return out;
}

function getHeaderCaseInsensitive(
  headers: Record<string, string> | undefined,
  name: string,
): string | undefined {
  if (!headers) return undefined;
  const lower = name.toLowerCase();
  for (const [k, v] of Object.entries(headers)) {
    if (k.toLowerCase() === lower) return v;
  }
  return undefined;
}

function verifyInternalHostControl(request: Request, configuredToken?: string): boolean {
  const expected = (configuredToken ?? process.env.TINADEC_HOST_CONTROL_TOKEN ?? '').trim();
  if (!expected) return true;
  const supplied = (request.headers.get('x-tinadec-host-control') ?? '').trim();
  if (!supplied) return false;
  const a = Buffer.from(expected, 'utf8');
  const b = Buffer.from(supplied, 'utf8');
  return a.length === b.length && timingSafeEqual(a, b);
}

export class PeerProviderProxyRuntime {
  private readonly options: PeerProviderProxyRuntimeOptions;
  private readonly workerSessionsById = new Map<string, ActiveWorkerReverseSession>();
  private readonly workerSessionsByController = new Map<string, Set<ActiveWorkerReverseSession>>();
  private readonly controllerInFlightByConn = new WeakMap<
    PeerFrameConnection,
    Map<string, InFlightControllerReverseRequest>
  >();
  private readonly fallbackTokens = new Map<string, FallbackGatewayTokenRecord>();
  private readonly autoTokenCache = new Map<
    string,
    { token: string; expiresAtMs: number; remainingUses: number }
  >();
  private controllerAllowProviderProxy = true;

  constructor(options: PeerProviderProxyRuntimeOptions) {
    this.options = options;
  }

  setControllerAllowProviderProxy(allowed: boolean): void {
    this.controllerAllowProviderProxy = allowed;
  }

  // ── 1. Worker-Side Reverse Session Tracking (`PeerTlsServer` hooks) ─────────

  registerWorkerControllerSession(
    sessionId: string,
    controller: PeerControllerContext,
    connection: PeerFrameConnection,
  ): void {
    const record: ActiveWorkerReverseSession = {
      sessionId,
      controller,
      connection,
      pendingReverseStreams: new Map(),
    };
    this.workerSessionsById.set(sessionId, record);
    let set = this.workerSessionsByController.get(controller.controllerDeviceId);
    if (!set) {
      set = new Set();
      this.workerSessionsByController.set(controller.controllerDeviceId, set);
    }
    set.add(record);

    connection.onClose((closeErr) => {
      this.unregisterWorkerControllerSession(sessionId, closeErr);
    });
  }

  unregisterWorkerControllerSession(sessionId: string, reasonErr?: Error): void {
    const record = this.workerSessionsById.get(sessionId);
    if (!record) return;
    this.workerSessionsById.delete(sessionId);

    const group = this.workerSessionsByController.get(record.controller.controllerDeviceId);
    if (group) {
      group.delete(record);
      if (group.size === 0) {
        this.workerSessionsByController.delete(record.controller.controllerDeviceId);
      }
    }

    const err =
      reasonErr ?? new PeerProtocolError('device_offline', 'Controller mTLS tunnel disconnected.');
    const pending = [...record.pendingReverseStreams.values()];
    record.pendingReverseStreams.clear();
    for (const stream of pending) {
      stream.cleanupAbort?.();
      if (!stream.headResolved) {
        stream.headResolved = true;
        stream.rejectHead(err);
      } else {
        try {
          stream.bodyController?.error(err);
        } catch {
          // ignore
        }
      }
    }
  }

  /**
   * Handle `http.res.head`, `http.res.chunk`, `http.res.end` arriving on Worker's `PeerTlsServer`
   * in response to a reverse HTTP request initiated by Worker toward Controller.
   */
  handleWorkerReverseResponseFrame(sessionId: string, frame: DecodedPeerFrame): boolean {
    const h = frame.header;
    if (h.type !== 'http.res.head' && h.type !== 'http.res.chunk' && h.type !== 'http.res.end') {
      return false;
    }
    const session = this.workerSessionsById.get(sessionId);
    if (!session) return true;
    const pending = session.pendingReverseStreams.get(h.streamId);
    if (!pending) return true;

    if (h.type === 'http.res.head') {
      if (pending.headResolved) return true;
      pending.headResolved = true;
      const conn = session.connection;
      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          pending.bodyController = controller;
        },
        cancel() {
          pending.cleanupAbort?.();
          session.pendingReverseStreams.delete(h.streamId);
          try {
            conn.sendFrame({
              type: 'http.req.abort',
              streamId: h.streamId,
              reason: 'Worker cancelled reverse provider-proxy stream.',
            });
          } catch {
            // ignore
          }
        },
      });
      pending.resolveHead(
        new Response(h.status === 204 || h.status === 304 ? null : body, {
          status: h.status,
          headers: h.headers,
        }),
      );
      return true;
    }

    if (h.type === 'http.res.chunk') {
      if (frame.payload && frame.payload.byteLength > 0) {
        try {
          pending.bodyController?.enqueue(
            new Uint8Array(
              frame.payload.buffer.slice(
                frame.payload.byteOffset,
                frame.payload.byteOffset + frame.payload.byteLength,
              ),
            ),
          );
        } catch {
          // stream already closed
        }
      }
      return true;
    }

    if (h.type === 'http.res.end') {
      pending.cleanupAbort?.();
      session.pendingReverseStreams.delete(h.streamId);
      if (h.error) {
        const err = new PeerProtocolError(h.error.code, redactProviderSecrets(h.error.message));
        if (!pending.headResolved) {
          pending.headResolved = true;
          pending.rejectHead(err);
        } else {
          try {
            pending.bodyController?.error(err);
          } catch {
            // ignore
          }
        }
        return true;
      }
      try {
        pending.bodyController?.close();
      } catch {
        // ignore
      }
      return true;
    }

    return false;
  }

  /**
   * Send a reverse HTTP request over an already-authenticated mTLS controller tunnel on Worker.
   * Rejects with `PeerProtocolError('policy_denied', ...)` if no active tunnel exists or if
   * Worker's policy for that controller has `allow_provider_proxy !== true`.
   */
  async sendWorkerReverseCompletionToController(params: {
    controllerDeviceId: string;
    proxySessionToken: string;
    acceptHeader: string;
    bodyBytes: Uint8Array | Buffer;
    signal?: AbortSignal;
    fallbackClientProxy?: (
      deviceId: string,
      req: {
        method: string;
        path: string;
        headers: Record<string, string>;
        body: Uint8Array | Buffer;
        signal?: AbortSignal;
      },
    ) => Promise<Response>;
  }): Promise<Response> {
    const targetControllerId = params.controllerDeviceId.trim();
    const token = params.proxySessionToken.trim();
    if (!targetControllerId || !token) {
      throw new PeerProtocolError(
        'policy_denied',
        'Provider proxy requires both controller_device_id and proxy_session_token.',
        403,
      );
    }

    const activeGroup = this.workerSessionsByController.get(targetControllerId);
    let selectedSession: ActiveWorkerReverseSession | null = null;
    if (activeGroup) {
      for (const candidate of activeGroup) {
        if (!candidate.connection.isClosed) {
          selectedSession = candidate;
          break;
        }
      }
    }

    if (!selectedSession) {
      if (params.fallbackClientProxy) {
        const allowedOnWorker = await this.verifyWorkerGrantAllowsProviderProxy(targetControllerId);
        if (!allowedOnWorker) {
          throw new PeerProtocolError(
            'policy_denied',
            `Worker access policy for controller '${targetControllerId}' denies provider_proxy.`,
            403,
          );
        }
        return await params.fallbackClientProxy(targetControllerId, {
          method: 'POST',
          path: PROVIDER_PROXY_COMPLETIONS_PATH,
          headers: {
            'content-type': 'application/json',
            accept: params.acceptHeader || 'text/event-stream',
            [HEADER_PROXY_TOKEN]: token,
          },
          body: params.bodyBytes,
          signal: params.signal,
        });
      }
      throw new PeerProtocolError(
        'policy_denied',
        `No active authenticated mTLS tunnel from controller '${targetControllerId}' is available for provider proxy.`,
        403,
      );
    }

    if (!toBoolFlag(selectedSession.controller.policy.allow_provider_proxy, false)) {
      throw new PeerProtocolError(
        'policy_denied',
        `Provider proxy is disabled by worker access policy for controller '${targetControllerId}'.`,
        403,
      );
    }

    const conn = selectedSession.connection;
    const streamId = `rev_${crypto.randomUUID()}`;

    return await new Promise<Response>((resolveHead, rejectHead) => {
      const pending: PendingReverseHttpStream = {
        streamId,
        headResolved: false,
        resolveHead,
        rejectHead,
        bodyController: null,
      };

      if (params.signal) {
        if (params.signal.aborted) {
          rejectHead(new DOMException('The operation was aborted.', 'AbortError'));
          return;
        }
        const onAbort = () => {
          selectedSession!.pendingReverseStreams.delete(streamId);
          try {
            conn.sendFrame({
              type: 'http.req.abort',
              streamId,
              reason: 'Worker aborted provider-proxy request.',
            });
          } catch {
            // ignore
          }
          try {
            pending.bodyController?.close();
          } catch {
            // ignore
          }
          if (!pending.headResolved) {
            pending.headResolved = true;
            rejectHead(new DOMException('The operation was aborted.', 'AbortError'));
          }
        };
        params.signal.addEventListener('abort', onAbort, { once: true });
        pending.cleanupAbort = () => params.signal?.removeEventListener('abort', onAbort);
      }

      selectedSession!.pendingReverseStreams.set(streamId, pending);

      try {
        conn.sendFrame({
          type: 'http.req',
          streamId,
          method: 'POST',
          path: PROVIDER_PROXY_COMPLETIONS_PATH,
          query: '',
          headers: {
            'content-type': 'application/json',
            accept: params.acceptHeader || 'text/event-stream',
            [HEADER_PROXY_TOKEN]: token,
            'x-request-id': ensureRequestId(null),
          },
          hasBody: params.bodyBytes.byteLength > 0,
        });

        const maxChunk = 1024 * 1024;
        for (let off = 0; off < params.bodyBytes.byteLength; off += maxChunk) {
          const slice = params.bodyBytes.subarray(
            off,
            Math.min(off + maxChunk, params.bodyBytes.byteLength),
          );
          conn.sendFrame({ type: 'http.req.chunk', streamId }, slice);
        }
        conn.sendFrame({ type: 'http.req.end', streamId });
      } catch (err) {
        pending.cleanupAbort?.();
        selectedSession!.pendingReverseStreams.delete(streamId);
        rejectHead(
          err instanceof Error
            ? err
            : new PeerProtocolError('device_offline', 'Failed to write reverse tunnel frame.'),
        );
      }
    });
  }

  // ── 2. Worker Session Binding Sync (for Zero-Key Transparent Proxy Sessions) ─

  async notifyWorkerCoreSessionBinding(params: {
    sessionId: string;
    controller: PeerControllerContext;
    incomingHeaders?: Record<string, string>;
    gatewayLoopbackUrl?: string;
  }): Promise<void> {
    if (!toBoolFlag(params.controller.policy.allow_provider_proxy, false)) {
      return;
    }
    const proxyToken = getHeaderCaseInsensitive(params.incomingHeaders, HEADER_PROXY_TOKEN)?.trim();
    if (!proxyToken || !params.sessionId) return;

    const forceProxyRaw = getHeaderCaseInsensitive(
      params.incomingHeaders,
      HEADER_USE_PROVIDER_PROXY,
    )?.trim();
    const forceProviderProxy =
      forceProxyRaw === '1' || forceProxyRaw?.toLowerCase() === 'true';

    const gwUrl =
      params.gatewayLoopbackUrl ??
      this.options.gatewayLoopbackUrl?.() ??
      this.resolveDefaultGatewayLoopbackUrl();

    const fetchImpl = this.options.fetchImpl ?? fetch;
    try {
      const url = new URL(PROVIDER_PROXY_SESSION_BINDING_PATH, this.options.coreUrl()).toString();
      await fetchImpl(url, {
        method: 'POST',
        headers: {
          ...this.buildInternalCoreHeaders(),
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          session_id: params.sessionId,
          controller_device_id: params.controller.controllerDeviceId,
          proxy_session_token: proxyToken,
          use_provider_proxy: forceProviderProxy,
          gateway_loopback_url: gwUrl,
        }),
      });
    } catch {
      // Best-effort binding notification; delegation endpoint also binds directly in Core.
    }
  }

  // ── 3. Controller-Side Reverse Tunnel Handler (`PeerClientPool` / `PeerTlsServer`) ─

  /**
   * Handle reverse `http.req*` frames sent by an authenticated Worker back over the mTLS tunnel
   * in `PeerClientPool` (`entry.connection`).
   * Strictly restricts reverse requests ONLY to `POST /api/v1/peers/internal/provider-proxy/completions`!
   * A Worker can NEVER reverse-proxy arbitrary Controller endpoints!
   */
  handleControllerReverseRequestFrame(
    peer: ControllerReverseTunnelPeerContext,
    frame: DecodedPeerFrame,
  ): boolean {
    const h = frame.header;
    if (
      h.type !== 'http.req' &&
      h.type !== 'http.req.chunk' &&
      h.type !== 'http.req.end' &&
      h.type !== 'http.req.abort'
    ) {
      return false;
    }

    let map = this.controllerInFlightByConn.get(peer.connection);
    if (!map) {
      map = new Map();
      this.controllerInFlightByConn.set(peer.connection, map);
      peer.connection.onClose(() => {
        const active = this.controllerInFlightByConn.get(peer.connection);
        if (active) {
          for (const item of active.values()) item.abort.abort();
          active.clear();
        }
      });
    }

    if (h.type === 'http.req') {
      const normPath = h.path.replace(/\/+$/, '').toLowerCase();
      if (h.method.toUpperCase() !== 'POST' || normPath !== PROVIDER_PROXY_COMPLETIONS_PATH) {
        this.sendTunnelProblemResponse(
          peer.connection,
          h.streamId,
          403,
          'policy_denied',
          `Reverse peer tunnel strictly forbids ${h.method} ${h.path}; only POST ${PROVIDER_PROXY_COMPLETIONS_PATH} is allowed.`,
          h.path,
        );
        return true;
      }

      const entry: InFlightControllerReverseRequest = {
        streamId: h.streamId,
        reqFrame: h,
        chunks: [],
        byteLength: 0,
        abort: new AbortController(),
      };
      map.set(h.streamId, entry);
      if (!h.hasBody) {
        map.delete(h.streamId);
        void this.executeControllerTunnelCompletion(peer, entry, Buffer.alloc(0));
      }
      return true;
    }

    if (h.type === 'http.req.chunk') {
      const entry = map.get(h.streamId);
      if (!entry || !frame.payload || frame.payload.byteLength === 0) return true;
      entry.byteLength += frame.payload.byteLength;
      if (entry.byteLength > MAX_REVERSE_JSON_BYTES) {
        map.delete(h.streamId);
        entry.abort.abort();
        this.sendTunnelProblemResponse(
          peer.connection,
          h.streamId,
          413,
          'invalid_request',
          'Provider proxy completion payload exceeds 4 MB limit.',
          entry.reqFrame.path,
        );
        return true;
      }
      entry.chunks.push(
        Buffer.from(frame.payload.buffer, frame.payload.byteOffset, frame.payload.byteLength),
      );
      return true;
    }

    if (h.type === 'http.req.end') {
      const entry = map.get(h.streamId);
      if (!entry) return true;
      map.delete(h.streamId);
      const rawBuf = Buffer.concat(entry.chunks, entry.byteLength);
      entry.chunks = [];
      void this.executeControllerTunnelCompletion(peer, entry, rawBuf);
      return true;
    }

    if (h.type === 'http.req.abort') {
      const entry = map.get(h.streamId);
      if (entry) {
        map.delete(h.streamId);
        entry.abort.abort();
      }
      return true;
    }

    return false;
  }

  /**
   * Execute an inbound provider-proxy completion on Controller after verifying:
   * 1. Authenticated mTLS tunnel peer identity (`peer.remoteDeviceId`),
   * 2. Controller provider-proxy policy enabled,
   * 3. Valid, unexpired, within-limit `proxy_session_token`.
   */
  async executeControllerTunnelCompletion(
    peer: ControllerReverseTunnelPeerContext,
    entry: {
      streamId: string;
      reqFrame: PeerHttpReqFrame;
      abort: AbortController;
    },
    rawBody: Buffer,
  ): Promise<void> {
    const { streamId, reqFrame, abort } = entry;
    const conn = peer.connection;

    if (!this.controllerAllowProviderProxy) {
      this.sendTunnelProblemResponse(
        conn,
        streamId,
        403,
        'policy_denied',
        'Controller has disabled peer provider proxy.',
        PROVIDER_PROXY_COMPLETIONS_PATH,
      );
      return;
    }

    const token = getHeaderCaseInsensitive(reqFrame.headers, HEADER_PROXY_TOKEN)?.trim() ?? '';
    if (!token) {
      this.sendTunnelProblemResponse(
        conn,
        streamId,
        403,
        'policy_denied',
        'Missing x-tinadec-peer-proxy-token on provider-proxy tunnel request.',
        PROVIDER_PROXY_COMPLETIONS_PATH,
      );
      return;
    }

    if (!peer.remoteDeviceId) {
      this.sendTunnelProblemResponse(
        conn,
        streamId,
        403,
        'policy_denied',
        'Unverified peer tunnel identity for provider proxy.',
        PROVIDER_PROXY_COMPLETIONS_PATH,
      );
      return;
    }

    const acceptHeader =
      getHeaderCaseInsensitive(reqFrame.headers, 'accept') ?? 'text/event-stream';

    try {
      let upstreamResponse: Response;
      if (this.options.fallbackUpstreamCompletions) {
        const verifiedFallback = this.consumeFallbackToken(token, peer.remoteDeviceId);
        if (!verifiedFallback.ok) {
          this.sendTunnelProblemResponse(
            conn,
            streamId,
            403,
            'policy_denied',
            verifiedFallback.reason,
            PROVIDER_PROXY_COMPLETIONS_PATH,
          );
          return;
        }
        let parsedJson: Record<string, unknown> = {};
        try {
          parsedJson = JSON.parse(rawBody.toString('utf8')) as Record<string, unknown>;
        } catch {
          this.sendTunnelProblemResponse(
            conn,
            streamId,
            400,
            'invalid_request',
            'Malformed JSON in provider-proxy completion request.',
            PROVIDER_PROXY_COMPLETIONS_PATH,
          );
          return;
        }
        upstreamResponse = await this.options.fallbackUpstreamCompletions({
          workerDeviceId: peer.remoteDeviceId,
          pairedDeviceId: peer.deviceId,
          tokenRecord: verifiedFallback.record,
          bodyJson: parsedJson,
          rawBody,
          acceptHeader,
          signal: abort.signal,
        });
      } else {
        const fetchImpl = this.options.fetchImpl ?? fetch;
        const targetUrl = new URL(
          PROVIDER_PROXY_COMPLETIONS_PATH,
          this.options.coreUrl(),
        ).toString();
        upstreamResponse = await fetchImpl(targetUrl, {
          method: 'POST',
          headers: {
            ...this.buildInternalCoreHeaders(),
            'content-type': 'application/json; charset=utf-8',
            accept: acceptHeader,
            [HEADER_PROXY_TOKEN]: token,
            [HEADER_VERIFIED_WORKER_ID]: peer.remoteDeviceId,
            [HEADER_VERIFIED_DEVICE_ID]: peer.deviceId,
          },
          body: new Uint8Array(rawBody),
          signal: abort.signal,
        });
      }

      if (abort.signal.aborted || conn.isClosed) return;

      if (!upstreamResponse.ok) {
        const errText = await upstreamResponse.text().catch(() => '');
        const scrubbed = redactProviderSecrets(errText);
        let code = upstreamResponse.status === 403 ? 'policy_denied' : 'provider_proxy_error';
        let detail = `Upstream provider proxy failed (HTTP ${upstreamResponse.status}).`;
        try {
          const parsed = JSON.parse(scrubbed) as Record<string, unknown>;
          if (typeof parsed.code === 'string' && parsed.code) code = parsed.code;
          else if (typeof parsed.title === 'string' && parsed.title) code = parsed.title;
          if (typeof parsed.detail === 'string' && parsed.detail) detail = parsed.detail;
          else if (typeof parsed.message === 'string' && parsed.message) detail = parsed.message;
          else if (
            parsed.error &&
            typeof parsed.error === 'object' &&
            typeof (parsed.error as Record<string, unknown>).message === 'string'
          ) {
            detail = String((parsed.error as Record<string, unknown>).message);
          }
        } catch {
          if (scrubbed.trim()) detail = scrubbed.slice(0, 300);
        }
        this.sendTunnelProblemResponse(
          conn,
          streamId,
          upstreamResponse.status,
          code,
          redactProviderSecrets(detail),
          PROVIDER_PROXY_COMPLETIONS_PATH,
        );
        return;
      }

      const resHeaders: Record<string, string> = {};
      upstreamResponse.headers.forEach((val, key) => {
        const lower = key.toLowerCase();
        if (!RESPONSE_HOP_HEADERS.has(lower)) {
          resHeaders[lower] = redactProviderSecrets(val);
        }
      });

      conn.sendFrame({
        type: 'http.res.head',
        streamId,
        status: upstreamResponse.status,
        headers: resHeaders,
      });

      if (upstreamResponse.body) {
        const reader = upstreamResponse.body.getReader();
        const decoder = new TextDecoder();
        const encoder = new TextEncoder();
        try {
          while (!abort.signal.aborted && !conn.isClosed) {
            const { done, value } = await reader.read();
            if (done) break;
            if (value && value.byteLength > 0) {
              const decodedText = decoder.decode(value, { stream: true });
              const safeBytes = encoder.encode(redactProviderSecrets(decodedText));
              const maxChunk = 512 * 1024;
              for (let off = 0; off < safeBytes.byteLength; off += maxChunk) {
                conn.sendFrame(
                  { type: 'http.res.chunk', streamId },
                  safeBytes.subarray(off, Math.min(off + maxChunk, safeBytes.byteLength)),
                );
              }
            }
          }
          const flushed = decoder.decode();
          if (flushed && !abort.signal.aborted && !conn.isClosed) {
            conn.sendFrame(
              { type: 'http.res.chunk', streamId },
              encoder.encode(redactProviderSecrets(flushed)),
            );
          }
        } finally {
          try {
            reader.releaseLock();
          } catch {
            // ignore
          }
        }
      }

      if (!abort.signal.aborted && !conn.isClosed) {
        conn.sendFrame({ type: 'http.res.end', streamId });
      }
    } catch (err) {
      if (abort.signal.aborted || conn.isClosed) return;
      const safeMessage = redactProviderSecrets(
        err instanceof Error ? err.message : 'Controller provider proxy request failed.',
      );
      this.sendTunnelProblemResponse(
        conn,
        streamId,
        502,
        'unreachable',
        safeMessage,
        PROVIDER_PROXY_COMPLETIONS_PATH,
      );
    }
  }

  // ── 4. Token Issuance & Outbound Transparent Proxy Auto-Attachment ──────────

  async issueControllerProxyToken(
    input: IssueProxySessionTokenInput,
  ): Promise<IssuedProxySessionTokenDto> {
    const now = Date.now();
    const expiresAtMs =
      typeof input.expires_at_ms === 'number' && input.expires_at_ms > now
        ? input.expires_at_ms
        : typeof input.ttl_seconds === 'number' && input.ttl_seconds > 0
          ? now + input.ttl_seconds * 1000
          : now + DEFAULT_TOKEN_TTL_MS;
    const maxRequests =
      typeof input.max_requests === 'number' && input.max_requests > 0
        ? Math.min(Math.floor(input.max_requests), 4096)
        : DEFAULT_MAX_REQUESTS;
    const sessionId = (input.remote_session_id ?? input.session_id ?? null) || null;
    const delegationId = input.delegation_id ?? null;

    if (!this.options.fallbackUpstreamCompletions) {
      const fetchImpl = this.options.fetchImpl ?? fetch;
      try {
        const url = new URL(PROVIDER_PROXY_TOKENS_PATH, this.options.coreUrl()).toString();
        const res = await fetchImpl(url, {
          method: 'POST',
          headers: {
            ...this.buildInternalCoreHeaders(),
            'content-type': 'application/json',
          },
          body: JSON.stringify({
            controller_device_id: input.controller_device_id ?? '',
            worker_device_id: input.worker_device_id ?? '',
            delegation_id: delegationId,
            session_id: sessionId,
            expires_at_ms: expiresAtMs,
            max_requests: maxRequests,
          }),
        });
        if (res.ok) {
          const data = (await res.json()) as Record<string, unknown>;
          const minted = String(data.proxy_session_token ?? data.token ?? '').trim();
          if (minted) {
            return {
              proxy_session_token: minted,
              token: minted,
              controller_device_id: String(
                data.controller_device_id ?? input.controller_device_id ?? '',
              ),
              worker_device_id: String(data.worker_device_id ?? input.worker_device_id ?? ''),
              delegation_id:
                typeof data.delegation_id === 'string' ? data.delegation_id : delegationId,
              session_id: typeof data.session_id === 'string' ? data.session_id : sessionId,
              expires_at_ms:
                typeof data.expires_at_ms === 'number' ? data.expires_at_ms : expiresAtMs,
              max_requests:
                typeof data.max_requests === 'number' ? data.max_requests : maxRequests,
            };
          }
        }
      } catch {
        // Fallback to local Gateway token store when Core token endpoint is not mounted.
      }
    }

    const token = `ppt_v1_${randomBytes(24).toString('hex')}`;
    const record: FallbackGatewayTokenRecord = {
      token,
      controllerDeviceId: (input.controller_device_id ?? '').trim(),
      workerDeviceId: (input.worker_device_id ?? '').trim(),
      delegationId,
      sessionId,
      expiresAtMs,
      maxRequests,
      usedRequests: 0,
      revoked: false,
    };
    this.fallbackTokens.set(token, record);
    return {
      proxy_session_token: token,
      token,
      controller_device_id: record.controllerDeviceId,
      worker_device_id: record.workerDeviceId,
      delegation_id: delegationId,
      sessionId: sessionId,
      session_id: sessionId,
      expires_at_ms: expiresAtMs,
      max_requests: maxRequests,
    } as IssuedProxySessionTokenDto;
  }

  /**
   * Automatically attaches a scoped short-lived `x-tinadec-peer-proxy-token` when Controller
   * proxies a session creation, interaction, or delegation request to a paired Worker,
   * enabling zero-API-key Worker runs without ever sending an API key!
   */
  async maybeAttachOutboundProxyToken(params: {
    deviceId: string;
    remoteDeviceId?: string;
    controllerDeviceId?: string;
    method: string;
    targetPath: string;
    headers: Record<string, string>;
  }): Promise<void> {
    if (!this.controllerAllowProviderProxy) return;
    if (getHeaderCaseInsensitive(params.headers, HEADER_PROXY_TOKEN)) return;

    const upperMethod = params.method.toUpperCase();
    if (upperMethod !== 'POST') return;
    const lowerPath = params.targetPath.replace(/\/+$/, '').toLowerCase();
    const sessionInteractionMatch = /^\/api\/v1\/sessions\/([^/]+)\/interactions$/i.exec(
      params.targetPath,
    );
    const isSessionCreate = lowerPath === '/api/v1/sessions';
    const isDelegate =
      lowerPath === '/api/v1/peers/delegate' || lowerPath.startsWith('/api/v1/peers/delegate/');

    if (!isSessionCreate && !sessionInteractionMatch && !isDelegate) return;

    const sessionId = sessionInteractionMatch
      ? decodeURIComponent(sessionInteractionMatch[1]!).trim()
      : null;
    const workerId = (params.remoteDeviceId ?? '').trim();
    const cacheKey = `${workerId || params.deviceId}:${sessionId ?? '*'}`;
    const now = Date.now();
    const cached = this.autoTokenCache.get(cacheKey);
    if (cached && cached.expiresAtMs - now > 60_000 && cached.remainingUses > 4) {
      cached.remainingUses -= 1;
      params.headers[HEADER_PROXY_TOKEN] = cached.token;
      return;
    }

    try {
      const issued = await this.issueControllerProxyToken({
        controller_device_id: params.controllerDeviceId,
        worker_device_id: workerId,
        session_id: sessionId,
        expires_at_ms: now + DEFAULT_TOKEN_TTL_MS,
        max_requests: DEFAULT_MAX_REQUESTS,
      });
      if (issued.proxy_session_token) {
        this.autoTokenCache.set(cacheKey, {
          token: issued.proxy_session_token,
          expiresAtMs: issued.expires_at_ms,
          remainingUses: Math.max(1, issued.max_requests - 1),
        });
        params.headers[HEADER_PROXY_TOKEN] = issued.proxy_session_token;
      }
    } catch {
      // Non-fatal: Worker will still run using its own local model providers if configured.
    }
  }

  revokeDeviceTokens(workerOrPairedDeviceId: string): void {
    const trimmed = workerOrPairedDeviceId.trim();
    if (!trimmed) return;
    for (const [k, v] of this.fallbackTokens) {
      if (v.workerDeviceId === trimmed) {
        v.revoked = true;
        this.fallbackTokens.delete(k);
      }
    }
    for (const k of this.autoTokenCache.keys()) {
      if (k.startsWith(`${trimmed}:`)) {
        this.autoTokenCache.delete(k);
      }
    }
  }

  // ── Private Helpers ──────────────────────────────────────────────────────────

  private consumeFallbackToken(
    token: string,
    verifiedWorkerDeviceId: string,
  ): { ok: true; record: FallbackGatewayTokenRecord } | { ok: false; reason: string } {
    const rec = this.fallbackTokens.get(token);
    if (!rec || rec.revoked) {
      return { ok: false, reason: 'Invalid or revoked proxy_session_token.' };
    }
    if (Date.now() > rec.expiresAtMs) {
      this.fallbackTokens.delete(token);
      return { ok: false, reason: 'proxy_session_token has expired.' };
    }
    if (rec.workerDeviceId && rec.workerDeviceId !== verifiedWorkerDeviceId) {
      return {
        ok: false,
        reason: `proxy_session_token is not bound to worker '${verifiedWorkerDeviceId}'.`,
      };
    }
    rec.usedRequests += 1;
    if (rec.usedRequests > rec.maxRequests) {
      return {
        ok: false,
        reason: `proxy_session_token exceeded max_requests (${rec.maxRequests}).`,
      };
    }
    return { ok: true, record: rec };
  }

  private async verifyWorkerGrantAllowsProviderProxy(controllerDeviceId: string): Promise<boolean> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    try {
      const url = new URL('/api/v1/peers/internal/policy-snapshot', this.options.coreUrl()).toString();
      const res = await fetchImpl(url, { headers: this.buildInternalCoreHeaders() });
      if (!res.ok) return false;
      const snap = (await res.json()) as {
        grants?: Array<{
          controller_device_id?: string;
          revoked_at?: number | null;
          policy?: { allow_provider_proxy?: boolean | number };
        }>;
      };
      if (!Array.isArray(snap.grants)) return false;
      const grant = snap.grants.find(
        (g) => g.controller_device_id === controllerDeviceId && !g.revoked_at,
      );
      return Boolean(grant && toBoolFlag(grant.policy?.allow_provider_proxy, false));
    } catch {
      return false;
    }
  }

  private sendTunnelProblemResponse(
    connection: PeerFrameConnection,
    streamId: string,
    status: number,
    code: string,
    detail: string,
    instance: string,
  ): void {
    if (connection.isClosed) return;
    const problem = toProblemDetails(
      status,
      code,
      redactProviderSecrets(detail),
      instance,
      ensureRequestId(null),
    );
    const payload = Buffer.from(JSON.stringify(problem), 'utf8');
    try {
      connection.sendFrame({
        type: 'http.res.head',
        streamId,
        status,
        headers: { 'content-type': 'application/problem+json' },
      });
      connection.sendFrame({ type: 'http.res.chunk', streamId }, payload);
      connection.sendFrame({ type: 'http.res.end', streamId });
    } catch {
      // ignore closed socket
    }
  }

  private buildInternalCoreHeaders(): Record<string, string> {
    const headers: Record<string, string> = {
      accept: 'application/json',
      'x-request-id': ensureRequestId(null),
      'x-tinadec-principal': 'gateway-internal',
    };
    const token = (
      this.options.hostControlToken?.() ??
      process.env.TINADEC_HOST_CONTROL_TOKEN ??
      ''
    ).trim();
    if (token) {
      headers['x-tinadec-host-control'] = token;
    }
    return headers;
  }

  private resolveDefaultGatewayLoopbackUrl(): string {
    const envUrl = process.env.TINADEC_GATEWAY_URL?.trim();
    if (envUrl) return envUrl.replace(/\/+$/, '');
    const port = Number(process.env.TINADEC_GATEWAY_PORT || process.env.PORT || '48730');
    return `http://127.0.0.1:${Number.isInteger(port) && port > 0 ? port : 48730}`;
  }
}

export interface RegisterProviderProxyRoutesOptions {
  runtime: PeerProviderProxyRuntime;
  keepStreamOpen: (
    server: { timeout(request: Request, seconds: number): void } | null | undefined,
    request: Request,
  ) => void;
  hostControlToken?: () => string | undefined;
  fallbackClientProxy?: (
    deviceId: string,
    req: {
      method: string;
      path: string;
      headers: Record<string, string>;
      body: Uint8Array | Buffer;
      signal?: AbortSignal;
    },
  ) => Promise<Response>;
}

const HIDDEN_DETAIL = { detail: { hide: true } as never };

/**
 * Registers internal loopback routes for Wave 3 Provider Proxy on Gateway:
 * - `POST /api/v1/peers/internal/provider-proxy/tokens` (loopback internal token issuance)
 * - `POST /api/v1/peers/internal/provider-proxy/completions` (Worker loopback -> reverse mTLS tunnel;
 *   any external HTTP or direct HTTP call against a Controller returns `403 policy_denied`!)
 */
export function registerProviderProxyRoutes(
  app: AnyElysia,
  options: RegisterProviderProxyRoutesOptions,
): AnyElysia {
  const { runtime, keepStreamOpen } = options;

  const denyPolicy = (
    request: Request,
    set: { status?: number | string; headers: Record<string, string | number> },
    detail: string,
  ) => {
    const rid = ensureRequestId(request.headers.get('x-request-id'));
    const pathname = new URL(request.url).pathname;
    set.status = 403;
    set.headers['x-request-id'] = rid;
    set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;
    set.headers['content-type'] = 'application/problem+json';
    return toProblemDetails(403, 'policy_denied', detail, pathname, rid);
  };

  const verifyInternalLoopbackCaller = (
    request: Request,
    server?: ServerLike | null,
  ): string | null => {
    if (request.headers.has('x-tinadec-peer-controller-id')) {
      return 'Remote peer requests cannot invoke internal provider-proxy loopback endpoints.';
    }
    if (!isStrictLoopbackRequest(request, server ?? null)) {
      return 'Internal provider-proxy endpoints only accept strict local loopback requests.';
    }
    const principal = (request.headers.get('x-tinadec-principal') ?? '').trim();
    if (principal !== 'gateway-internal' && principal !== 'core-internal') {
      return 'Internal provider-proxy endpoints require internal service principal authentication.';
    }
    if (!verifyInternalHostControl(request, options.hostControlToken?.())) {
      return 'Host control authentication failed on internal provider-proxy endpoint.';
    }
    return null;
  };

  return app
    .post(
      PROVIDER_PROXY_TOKENS_PATH,
      async ({
        body,
        request,
        set,
        server,
      }: {
        body: IssueProxySessionTokenInput;
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denyReason = verifyInternalLoopbackCaller(request, server);
        if (denyReason) return denyPolicy(request, set, denyReason);
        return await runtime.issueControllerProxyToken((body ?? {}) as IssueProxySessionTokenInput);
      },
      HIDDEN_DETAIL,
    )
    .all(
      PROVIDER_PROXY_TOKENS_PATH,
      ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) =>
        denyPolicy(
          request,
          set,
          'Internal provider-proxy token endpoint only accepts authenticated loopback POST requests.',
        ),
      HIDDEN_DETAIL,
    )
    .post(
      PROVIDER_PROXY_COMPLETIONS_PATH,
      async ({
        body: parsedBody,
        request,
        set,
        server,
      }: {
        body?: unknown;
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ({ timeout(request: Request, seconds: number): void } & ServerLike) | null;
      }) => {
        const denyReason = verifyInternalLoopbackCaller(request, server);
        if (denyReason) return denyPolicy(request, set, denyReason);

        const proxyToken = (request.headers.get(HEADER_PROXY_TOKEN) ?? '').trim();
        const controllerTarget = (
          request.headers.get(HEADER_CONTROLLER_TARGET) ??
          request.headers.get('x-tinadec-controller-device-id') ??
          ''
        ).trim();

        if (!proxyToken || !controllerTarget) {
          return denyPolicy(
            request,
            set,
            'Direct HTTP provider-proxy completions are forbidden without a valid worker tunnel target and proxy_session_token.',
          );
        }

        keepStreamOpen(server ?? undefined, request);
        const rid = ensureRequestId(request.headers.get('x-request-id'));

        let bodyBytes: Buffer;
        if (!request.bodyUsed && request.body) {
          const ab = await request.arrayBuffer();
          bodyBytes = Buffer.from(ab);
        } else if (typeof parsedBody === 'string') {
          bodyBytes = Buffer.from(parsedBody, 'utf8');
        } else if (parsedBody instanceof Uint8Array || Buffer.isBuffer(parsedBody)) {
          bodyBytes = Buffer.from(parsedBody);
        } else if (parsedBody !== undefined && parsedBody !== null) {
          bodyBytes = Buffer.from(JSON.stringify(parsedBody), 'utf8');
        } else {
          bodyBytes = Buffer.from('{}', 'utf8');
        }

        try {
          const upstreamRes = await runtime.sendWorkerReverseCompletionToController({
            controllerDeviceId: controllerTarget,
            proxySessionToken: proxyToken,
            acceptHeader: request.headers.get('accept') ?? 'text/event-stream',
            bodyBytes,
            signal: request.signal,
            fallbackClientProxy: options.fallbackClientProxy,
          });

          const contentType = upstreamRes.headers.get('content-type') ?? '';
          if (contentType.toLowerCase().includes('text/event-stream') && upstreamRes.status < 400) {
            const headers = new Headers();
            upstreamRes.headers.forEach((v, k) => {
              if (!RESPONSE_HOP_HEADERS.has(k.toLowerCase())) {
                headers.set(k, redactProviderSecrets(v));
              }
            });
            headers.set('content-type', 'text/event-stream; charset=utf-8');
            headers.set('cache-control', 'no-cache, no-transform');
            headers.set('x-accel-buffering', 'no');
            headers.set('x-request-id', rid);
            headers.set('x-tinadec-principal', PRINCIPAL_VALUE);
            set.status = upstreamRes.status;
            return new Response(wrapSseWithKeepalive(upstreamRes.body, 15_000, request.signal), {
              status: upstreamRes.status,
              headers,
            });
          }

          const textBody = redactProviderSecrets(await upstreamRes.text());
          const outHeaders = new Headers();
          upstreamRes.headers.forEach((v, k) => {
            if (!RESPONSE_HOP_HEADERS.has(k.toLowerCase())) {
              outHeaders.set(k, redactProviderSecrets(v));
            }
          });
          outHeaders.set('x-request-id', rid);
          outHeaders.set('x-tinadec-principal', PRINCIPAL_VALUE);
          set.status = upstreamRes.status;
          return new Response(textBody, {
            status: upstreamRes.status,
            headers: outHeaders,
          });
        } catch (err) {
          if (err instanceof PeerProtocolError) {
            set.status = err.status;
            set.headers['content-type'] = 'application/problem+json';
            return toProblemDetails(
              err.status,
              err.code,
              redactProviderSecrets(err.message),
              PROVIDER_PROXY_COMPLETIONS_PATH,
              rid,
            );
          }
          set.status = 502;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(
            502,
            'unreachable',
            redactProviderSecrets(
              err instanceof Error ? err.message : 'Provider proxy tunnel failed.',
            ),
            PROVIDER_PROXY_COMPLETIONS_PATH,
            rid,
          );
        }
      },
      { parse: 'none', ...HIDDEN_DETAIL } as never,
    )
    .get(
      PROVIDER_PROXY_COMPLETIONS_PATH,
      ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) =>
        denyPolicy(
          request,
          set,
          'Provider proxy completions endpoint only accepts authenticated mTLS tunnel requests.',
        ),
      HIDDEN_DETAIL,
    )
    .all(
      PROVIDER_PROXY_COMPLETIONS_PATH,
      ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) =>
        denyPolicy(
          request,
          set,
          'Provider proxy completions endpoint only accepts authenticated mTLS tunnel requests.',
        ),
      HIDDEN_DETAIL,
    );
}
