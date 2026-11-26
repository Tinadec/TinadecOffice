/**
 * Peer Management Surface (§5.3A) & Multi-Endpoint Transparent Proxy Routes (§5.3B):
 * - `/api/v1/peers/inbound`, `/pairing-code`, `/devices/*`, `/grants/*` (loopback/trustedHost only)
 * - `ALL /api/v1/peers/:deviceId/proxy/api/v1/*` (Path/Query/Body/Content-Type/SSE `after_seq` & `Last-Event-ID`)
 * - `WS /ws/peers/:deviceId/terminal` (reuses `wsTerminalProxy.ts` framing over mTLS tunnel)
 */

import type { DeployMode, AuthConfig } from '../config.js';
import { isCancellationError, proxyJson } from '../coreClient.js';
import { ensureRequestId, PRINCIPAL_VALUE } from '../headers.js';
import { mapCoreErrorToExternal, toProblemDetails } from '../mappers/errorMapper.js';
import { wrapSseWithKeepalive } from '../streaming.js';
import {
  evaluateWebAdmission,
  isLoopbackIp,
  type ServerLike,
  type TrustProxyMode,
  type WebAuthRuntime,
} from '../webAuth.js';
import {
  createPeerTunnelTerminalBridgeSession,
  type WsTerminalClient,
  type WsTerminalSession,
} from '../wsTerminalProxy.js';
import type { PairPeerDeviceInput, PeerClientPool, PeerDeviceRecord } from './peerClientPool.js';
import { DevServerRequestError, type RequestDevServerLeaseInput } from './peerDevServerTunnel.js';
import { PeerProtocolError } from './peerProtocol.js';
import { discoverReachablePeerAddresses, type PeerTlsServer } from './peerTlsServer.js';

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type AnyElysia = any;

export interface RegisterPeerRoutesOptions {
  peerTlsServer: PeerTlsServer;
  peerClientPool: PeerClientPool;
  forwardHeaders: (request: Request) => Record<string, string>;
  keepStreamOpen: (
    server: { timeout(request: Request, seconds: number): void } | null | undefined,
    request: Request,
  ) => void;
  webAuthRuntime: WebAuthRuntime;
  mode: DeployMode;
  authConfig?: AuthConfig;
  trustProxy: TrustProxyMode;
  proxyCoreJson?: typeof proxyJson;
}

const HIDDEN_DETAIL = { detail: { hide: true } as never };

const PROXY_FORWARD_HEADER_ALLOWLIST = new Set([
  'accept',
  'content-type',
  'x-request-id',
  'x-tinadec-storage-id',
  'x-tinadec-client-request-id',
  'if-match',
  'idempotency-key',
  'last-event-id',
  'range',
  'if-range',
]);

const PROXY_RESPONSE_HOP_HEADERS = new Set([
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
]);

function buildPeerSseResponse(
  sourceResponse: Response,
  signal: AbortSignal,
  setHeaders?: Record<string, string | number>,
): Response {
  const headers = new Headers();
  sourceResponse.headers.forEach((value, key) => {
    if (!PROXY_RESPONSE_HOP_HEADERS.has(key.toLowerCase())) {
      headers.set(key, value);
    }
  });
  if (setHeaders) {
    for (const [key, value] of Object.entries(setHeaders)) {
      if (!PROXY_RESPONSE_HOP_HEADERS.has(key.toLowerCase())) {
        headers.set(key, String(value));
      }
    }
  }
  headers.set('content-type', sourceResponse.headers.get('content-type') ?? 'text/event-stream');
  headers.set('cache-control', 'no-cache, no-transform');
  headers.set('x-accel-buffering', 'no');
  return new Response(wrapSseWithKeepalive(sourceResponse.body, 15_000, signal), {
    status: sourceResponse.status,
    headers,
  });
}

function guardLocalManagementRequest(
  request: Request,
  server: ServerLike | null | undefined,
  mode: DeployMode,
): { denied: true; status: 403; body: unknown } | { denied: false } {
  const path = new URL(request.url).pathname;

  // Never allow a proxied remote peer request to touch local peer security endpoints.
  if (request.headers.has('x-tinadec-peer-controller-id')) {
    return {
      denied: true,
      status: 403,
      body: toProblemDetails(
        403,
        'policy_denied',
        'Remote peer controllers cannot access local peer management endpoints.',
        path,
      ),
    };
  }

  if (mode === 'local' && server?.requestIP) {
    const remoteIp = server.requestIP(request)?.address;
    if (remoteIp && !isLoopbackIp(remoteIp)) {
      return {
        denied: true,
        status: 403,
        body: toProblemDetails(
          403,
          'policy_denied',
          'Peer management endpoints only accept local loopback requests.',
          path,
        ),
      };
    }
  }

  return { denied: false };
}

function formatPeerErrorResponse(
  set: { status?: number | string; headers: Record<string, string | number> },
  err: unknown,
  path: string,
  rid: string,
) {
  set.headers['x-request-id'] = rid;
  set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;
  set.headers['content-type'] = 'application/problem+json';

  if (err instanceof PeerProtocolError) {
    set.status = err.status;
    return toProblemDetails(err.status, err.code, err.message, path, rid);
  }
  if (err instanceof DevServerRequestError) {
    set.status = err.status;
    return toProblemDetails(err.status, err.code, err.message, path, rid);
  }
  const message = err instanceof Error ? err.message : 'Peer operation failed.';
  set.status = 502;
  return toProblemDetails(502, 'unreachable', message, path, rid);
}

function resolveWsKey(ws: unknown): unknown {
  if (ws && typeof ws === 'object') {
    const candidate = ws as { raw?: unknown; id?: string };
    if (candidate.raw !== undefined) return candidate.raw;
    if (typeof candidate.id === 'string' && candidate.id.length > 0) return candidate.id;
  }
  return ws;
}

export function registerPeerRoutes(app: AnyElysia, options: RegisterPeerRoutesOptions): AnyElysia {
  const { peerTlsServer, peerClientPool, forwardHeaders, keepStreamOpen } = options;
  const callCoreJson = options.proxyCoreJson ?? proxyJson;
  const activePeerWsTerminals = new Map<unknown, WsTerminalSession>();

  const checkMgmt = (
    request: Request,
    set: { status?: number | string; headers: Record<string, string | number> },
    server?: ServerLike | null,
  ) => {
    const guard = guardLocalManagementRequest(request, server, options.mode);
    if (guard.denied) {
      set.status = guard.status;
      set.headers['content-type'] = 'application/problem+json';
      return guard.body;
    }
    return null;
  };

  const PROXY_ROUTE_OPTS = { parse: 'none', ...HIDDEN_DETAIL } as never;

  const handlePeerTransparentProxy = async ({
    params,
    body: parsedBody,
    request,
    set,
    server,
  }: {
    params: { deviceId: string; '*': string };
    body?: unknown;
    request: Request;
    set: { status?: number | string; headers: Record<string, string | number> };
    server?: { timeout(request: Request, seconds: number): void } & ServerLike;
  }) => {
    const rid = ensureRequestId(request.headers.get('x-request-id'));
    const reqUrl = new URL(request.url);
    const prefix = `/api/v1/peers/${encodeURIComponent(params.deviceId)}/proxy`;
    let targetPath = reqUrl.pathname.startsWith(prefix)
      ? reqUrl.pathname.slice(prefix.length)
      : `/${params['*'] ?? ''}`;
    if (!targetPath.startsWith('/')) targetPath = `/${targetPath}`;

    if (!targetPath.startsWith('/api/v1/') && targetPath !== '/api/v1') {
      set.status = 400;
      set.headers['content-type'] = 'application/problem+json';
      return toProblemDetails(
        400,
        'invalid_request',
        `Peer proxy target must start with /api/v1/, got '${targetPath}'.`,
        reqUrl.pathname,
        rid,
      );
    }

    // Prevent Bun's idleTimeout from killing long-lived SSE or model generation requests.
    keepStreamOpen(server, request);

    const tunnelHeaders: Record<string, string> = {
      'x-request-id': rid,
    };
    request.headers.forEach((value, key) => {
      const lower = key.toLowerCase();
      if (PROXY_FORWARD_HEADER_ALLOWLIST.has(lower)) {
        tunnelHeaders[lower] = value;
      }
    });

    let outboundBody: ReadableStream<Uint8Array> | Uint8Array | Buffer | string | null = null;
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      if (!request.bodyUsed && request.body) {
        outboundBody = request.body;
      } else if (parsedBody !== undefined && parsedBody !== null) {
        if (typeof parsedBody === 'string' || parsedBody instanceof Uint8Array || Buffer.isBuffer(parsedBody)) {
          outboundBody = parsedBody;
        } else if (parsedBody instanceof ArrayBuffer) {
          outboundBody = new Uint8Array(parsedBody);
        } else {
          outboundBody = JSON.stringify(parsedBody);
        }
      }
    }

    try {
      const upstreamResponse = await peerClientPool.proxyHttpRequest(params.deviceId, {
        method: request.method,
        path: targetPath,
        query: reqUrl.search ? reqUrl.search.slice(1) : '',
        headers: tunnelHeaders,
        body: outboundBody,
        signal: request.signal,
      });

      const contentType = upstreamResponse.headers.get('content-type') ?? '';
      if (contentType.toLowerCase().includes('text/event-stream') && upstreamResponse.status < 400) {
        set.status = upstreamResponse.status;
        set.headers['x-request-id'] = rid;
        set.headers['x-tinadec-principal'] = PRINCIPAL_VALUE;
        return buildPeerSseResponse(
          upstreamResponse,
          request.signal,
          set.headers as Record<string, string | number>,
        );
      }

      const outHeaders = new Headers();
      for (const [k, v] of Object.entries(set.headers)) {
        if (!PROXY_RESPONSE_HOP_HEADERS.has(k.toLowerCase())) {
          outHeaders.set(k, String(v));
        }
      }
      upstreamResponse.headers.forEach((value, key) => {
        if (!PROXY_RESPONSE_HOP_HEADERS.has(key.toLowerCase())) {
          outHeaders.set(key, value);
        }
      });
      outHeaders.set('x-request-id', rid);
      outHeaders.set('x-tinadec-principal', PRINCIPAL_VALUE);

      set.status = upstreamResponse.status;
      return new Response(
        upstreamResponse.status === 204 || upstreamResponse.status === 304 || request.method === 'HEAD'
          ? null
          : upstreamResponse.body,
        {
          status: upstreamResponse.status,
          headers: outHeaders,
        },
      );
    } catch (err) {
      if (isCancellationError(err, request.signal)) {
        set.status = 499;
        return '';
      }
      return formatPeerErrorResponse(set, err, reqUrl.pathname, rid);
    }
  };

  return app
    // ── 1. Inbound Settings & PeerTlsServer Control ──────────────────────────────
    .get(
      '/api/v1/peers/inbound',
      async ({
        request,
        set,
        server,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;

        const headers = forwardHeaders(request);
        const coreRes = await callCoreJson('/api/v1/peers/inbound', { headers, signal: request.signal });
        if (coreRes.status >= 400) {
          set.status = coreRes.status;
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(coreRes.status, coreRes.data, '/api/v1/peers/inbound');
        }

        const coreObj =
          coreRes.data && typeof coreRes.data === 'object'
            ? (coreRes.data as Record<string, unknown>)
            : {};
        const runtime = peerTlsServer.getRuntimeStatus();
        const enabled = typeof coreObj.enabled === 'boolean' ? coreObj.enabled : runtime.enabled;
        const configuredPort =
          typeof coreObj.listen_port === 'number' && coreObj.listen_port > 0
            ? coreObj.listen_port
            : runtime.configuredPort;
        const effectivePort = runtime.actualListenPort ?? configuredPort;
        const reachable =
          enabled && effectivePort > 0
            ? discoverReachablePeerAddresses(effectivePort)
            : Array.isArray(coreObj.reachable_addresses)
              ? (coreObj.reachable_addresses as string[])
              : [];

        return {
          ...coreObj,
          enabled,
          listening: runtime.listening,
          display_name:
            typeof coreObj.display_name === 'string' ? coreObj.display_name : runtime.displayName,
          listen_port: effectivePort,
          configured_port: configuredPort,
          port_conflict: runtime.portConflict,
          local_device_id:
            typeof coreObj.local_device_id === 'string'
              ? coreObj.local_device_id
              : runtime.localDeviceId,
          local_fingerprint:
            typeof coreObj.local_fingerprint === 'string'
              ? coreObj.local_fingerprint
              : runtime.localFingerprint,
          reachable_addresses: reachable,
          firewall_hint:
            typeof coreObj.firewall_hint === 'string' ? coreObj.firewall_hint : null,
        };
      },
      HIDDEN_DETAIL,
    )
    .put(
      '/api/v1/peers/inbound',
      async ({
        body,
        request,
        set,
        server,
      }: {
        body: Record<string, unknown>;
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;

        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const headers = forwardHeaders(request);
        const coreRes = await callCoreJson('/api/v1/peers/inbound', {
          method: 'PUT',
          body: (body ?? {}) as Record<string, unknown>,
          headers,
          signal: request.signal,
        });

        if (coreRes.status >= 400) {
          set.status = coreRes.status;
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(coreRes.status, coreRes.data, '/api/v1/peers/inbound');
        }

        try {
          const runtime = await peerTlsServer.syncFromCore();
          const coreObj =
            coreRes.data && typeof coreRes.data === 'object'
              ? (coreRes.data as Record<string, unknown>)
              : {};
          const effectivePort = runtime.actualListenPort ?? runtime.configuredPort;
          return {
            ...coreObj,
            enabled: runtime.enabled,
            listening: runtime.listening,
            display_name: runtime.displayName || (coreObj.display_name as string) || '',
            listen_port: effectivePort,
            configured_port: runtime.configuredPort,
            port_conflict: runtime.portConflict,
            local_device_id: runtime.localDeviceId || (coreObj.local_device_id as string) || '',
            local_fingerprint: runtime.localFingerprint || (coreObj.local_fingerprint as string) || '',
            reachable_addresses: runtime.enabled
              ? discoverReachablePeerAddresses(effectivePort)
              : [],
            firewall_hint:
              typeof coreObj.firewall_hint === 'string' ? coreObj.firewall_hint : null,
          };
        } catch (err) {
          return formatPeerErrorResponse(set, err, '/api/v1/peers/inbound', rid);
        }
      },
      HIDDEN_DETAIL,
    )

    // ── 2. Ephemeral Pairing Code Management ─────────────────────────────────────
    .get(
      '/api/v1/peers/pairing-code',
      async ({
        request,
        set,
        server,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const result = await callCoreJson('/api/v1/peers/pairing-code', {
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, '/api/v1/peers/pairing-code');
        }
        return result.data;
      },
      HIDDEN_DETAIL,
    )
    .post(
      '/api/v1/peers/pairing-code',
      async ({
        body,
        request,
        set,
        server,
      }: {
        body: unknown;
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const result = await callCoreJson('/api/v1/peers/pairing-code', {
          method: 'POST',
          body: (body && typeof body === 'object' ? (body as Record<string, unknown>) : {}),
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, '/api/v1/peers/pairing-code');
        }
        return result.data;
      },
      HIDDEN_DETAIL,
    )
    .delete(
      '/api/v1/peers/pairing-code',
      async ({
        request,
        set,
        server,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const result = await callCoreJson('/api/v1/peers/pairing-code', {
          method: 'DELETE',
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, '/api/v1/peers/pairing-code');
        }
        return result.data;
      },
      HIDDEN_DETAIL,
    )

    // ── 3. Paired Remote Devices (Controller Perspective) ────────────────────────
    .get(
      '/api/v1/peers/devices',
      async ({
        request,
        set,
        server,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;

        const result = await callCoreJson('/api/v1/peers/devices', {
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, '/api/v1/peers/devices');
        }

        const rawList = Array.isArray(result.data)
          ? (result.data as PeerDeviceRecord[])
          : result.data && typeof result.data === 'object' && Array.isArray((result.data as { devices?: unknown }).devices)
            ? (result.data as { devices: PeerDeviceRecord[] }).devices
            : [];

        const enriched = rawList.map((dev) => {
          const live = peerClientPool.getLiveState(dev.device_id);
          if (!live) return dev;
          return {
            ...dev,
            last_state: live.state,
            last_failure_code: live.lastFailureCode ?? dev.last_failure_code,
            access_revision: live.accessRevision ?? dev.access_revision,
            last_connected_at: live.lastConnectedAt ?? dev.last_connected_at,
          };
        });

        return { devices: enriched };
      },
      HIDDEN_DETAIL,
    )
    .post(
      '/api/v1/peers/devices/probe',
      async ({
        body,
        request,
        set,
        server,
      }: {
        body: { address?: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const address = typeof body?.address === 'string' ? body.address.trim() : '';
        if (!address) {
          set.status = 400;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(400, 'invalid_request', 'address is required.', '/api/v1/peers/devices/probe', rid);
        }
        try {
          return await peerClientPool.probeAddress(address);
        } catch (err) {
          return formatPeerErrorResponse(set, err, '/api/v1/peers/devices/probe', rid);
        }
      },
      HIDDEN_DETAIL,
    )
    .post(
      '/api/v1/peers/devices/:deviceId/probe',
      async ({
        params,
        body,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        body: { address?: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/probe`;
        try {
          let address = typeof body?.address === 'string' ? body.address.trim() : '';
          if (!address) {
            const dev = await peerClientPool.findDeviceFromCore(params.deviceId);
            if (!dev) {
              set.status = 404;
              set.headers['content-type'] = 'application/problem+json';
              return toProblemDetails(404, 'not_found', `Peer device '${params.deviceId}' not found.`, path, rid);
            }
            address = dev.address;
          }
          return await peerClientPool.probeAddress(address);
        } catch (err) {
          return formatPeerErrorResponse(set, err, path, rid);
        }
      },
      HIDDEN_DETAIL,
    )
    .post(
      '/api/v1/peers/devices/pair',
      async ({
        body,
        request,
        set,
        server,
      }: {
        body: PairPeerDeviceInput;
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        try {
          const paired = await peerClientPool.pairWithPeer(body);
          set.status = 201;
          return paired;
        } catch (err) {
          return formatPeerErrorResponse(set, err, '/api/v1/peers/devices/pair', rid);
        }
      },
      HIDDEN_DETAIL,
    )
    .patch(
      '/api/v1/peers/devices/:deviceId',
      async ({
        params,
        body,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        body: Record<string, unknown>;
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}`;
        const result = await callCoreJson(path, {
          method: 'PATCH',
          body: (body ?? {}) as Record<string, unknown>,
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, path);
        }

        const updated = (result.data && typeof result.data === 'object' && 'device' in (result.data as Record<string, unknown>)
          ? (result.data as { device: PeerDeviceRecord }).device
          : result.data) as PeerDeviceRecord;

        if (updated && updated.device_id) {
          peerClientPool.upsertMetadata(updated);
          if (body?.connection_desired === false) {
            await peerClientPool.disconnectDevice(updated.device_id);
          } else if (body?.connection_desired === true) {
            void peerClientPool.connectDevice(updated.device_id, updated).catch(() => undefined);
          }
        }
        return updated;
      },
      HIDDEN_DETAIL,
    )
    .delete(
      '/api/v1/peers/devices/:deviceId',
      async ({
        params,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        peerClientPool.removeDevice(params.deviceId);
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}`;
        const result = await callCoreJson(path, {
          method: 'DELETE',
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, path);
        }
        return result.data;
      },
      HIDDEN_DETAIL,
    )
    .post(
      '/api/v1/peers/devices/:deviceId/connect',
      async ({
        params,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/connect`;
        try {
          return await peerClientPool.connectDevice(params.deviceId);
        } catch (err) {
          return formatPeerErrorResponse(set, err, path, rid);
        }
      },
      HIDDEN_DETAIL,
    )
    .post(
      '/api/v1/peers/devices/:deviceId/disconnect',
      async ({
        params,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/disconnect`;
        const updated = await peerClientPool.disconnectDevice(params.deviceId);
        if (!updated) {
          set.status = 404;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(404, 'not_found', `Peer device '${params.deviceId}' not found.`, path, rid);
        }
        return updated;
      },
      HIDDEN_DETAIL,
    )
    .get(
      '/api/v1/peers/devices/:deviceId/workspaces',
      async ({
        params,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const corePath = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/workspaces`;

        // Read local mounted shadow workspace metadata from Core.
        const localMountRes = await callCoreJson(corePath, {
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        const mountedListRaw =
          localMountRes.status < 400 && localMountRes.data && typeof localMountRes.data === 'object'
            ? Array.isArray(localMountRes.data)
              ? localMountRes.data
              : Array.isArray((localMountRes.data as { workspaces?: unknown }).workspaces)
                ? (localMountRes.data as { workspaces: unknown[] }).workspaces
                : []
            : [];

        const mountedByRemoteId = new Map<
          string,
          { alias_workspace_id?: string | null; visible?: boolean; name?: string; remote_root_path?: string | null }
        >();
        for (const item of mountedListRaw) {
          if (!item || typeof item !== 'object') continue;
          const rec = item as Record<string, unknown>;
          const remoteId = String(rec.remote_project_id ?? '').trim();
          if (!remoteId) continue;
          mountedByRemoteId.set(remoteId, {
            alias_workspace_id: typeof rec.alias_workspace_id === 'string' ? rec.alias_workspace_id : null,
            visible: rec.visible === undefined ? true : Boolean(rec.visible),
            name: typeof rec.name === 'string' ? rec.name : undefined,
            remote_root_path: typeof rec.remote_root_path === 'string' ? rec.remote_root_path : null,
          });
        }

        try {
          const remoteRes = await peerClientPool.proxyHttpRequest(params.deviceId, {
            method: 'GET',
            path: '/api/v1/projects',
            headers: { accept: 'application/json', 'x-request-id': rid },
            signal: request.signal,
          });

          if (!remoteRes.ok) {
            const errBody = await remoteRes.json().catch(() => null);
            set.status = remoteRes.status;
            set.headers['content-type'] = 'application/problem+json';
            return mapCoreErrorToExternal(remoteRes.status, errBody, corePath);
          }

          const remoteProjects = (await remoteRes.json()) as unknown;
          const projectsArray = Array.isArray(remoteProjects) ? remoteProjects : [];
          const workspaces = projectsArray.flatMap((proj) => {
            if (!proj || typeof proj !== 'object') return [];
            const rec = proj as Record<string, unknown>;
            const remoteProjectId = String(rec.id ?? rec.project_id ?? rec.projectId ?? '').trim();
            if (!remoteProjectId) return [];
            const name = String(rec.name ?? remoteProjectId);
            const rootPath =
              typeof rec.path === 'string'
                ? rec.path
                : typeof rec.root_path === 'string'
                  ? rec.root_path
                  : typeof rec.rootPath === 'string'
                    ? rec.rootPath
                    : null;
            const mountInfo = mountedByRemoteId.get(remoteProjectId);
            return [
              {
                remote_project_id: remoteProjectId,
                name,
                remote_root_path: rootPath,
                mounted: Boolean(mountInfo && mountInfo.visible !== false),
                visible: mountInfo?.visible ?? false,
                alias_workspace_id: mountInfo?.alias_workspace_id ?? null,
              },
            ];
          });

          return { workspaces };
        } catch (err) {
          // If offline but local shadow workspaces exist, return cached mounted entries gracefully.
          if (mountedByRemoteId.size > 0) {
            const fallback = [...mountedByRemoteId.entries()].map(([remoteProjectId, info]) => ({
              remote_project_id: remoteProjectId,
              name: info.name ?? remoteProjectId,
              remote_root_path: info.remote_root_path ?? null,
              mounted: info.visible !== false,
              visible: info.visible ?? true,
              alias_workspace_id: info.alias_workspace_id ?? null,
            }));
            return { workspaces: fallback };
          }
          return formatPeerErrorResponse(set, err, corePath, rid);
        }
      },
      HIDDEN_DETAIL,
    )
    .put(
      '/api/v1/peers/devices/:deviceId/workspaces/mount',
      async ({
        params,
        body,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        body: Record<string, unknown>;
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/workspaces/mount`;
        const items = Array.isArray(body?.workspaces)
          ? body.workspaces
          : Array.isArray(body?.mounts)
            ? body.mounts
            : [];
        const result = await callCoreJson(path, {
          method: 'PUT',
          body: { workspaces: items, mounts: items },
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, path);
        }
        return result.data;
      },
      HIDDEN_DETAIL,
    )
    .delete(
      '/api/v1/peers/devices/:deviceId/local-cache',
      async ({
        params,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/local-cache`;
        const result = await callCoreJson(path, {
          method: 'DELETE',
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, path);
        }
        return result.data ?? { cleared: true, device_id: params.deviceId };
      },
      HIDDEN_DETAIL,
    )
    .get(
      '/api/v1/peers/devices/:deviceId/shadow/sessions',
      async ({
        params,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const reqUrl = new URL(request.url);
        const basePath = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/shadow/sessions`;
        const fullPath = `${basePath}${reqUrl.search}`;
        const result = await callCoreJson(fullPath, {
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, basePath);
        }
        return result.data;
      },
      HIDDEN_DETAIL,
    )
    .get(
      '/api/v1/peers/devices/:deviceId/shadow/sessions/:remoteSessionId',
      async ({
        params,
        request,
        set,
        server,
      }: {
        params: { deviceId: string; remoteSessionId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const reqUrl = new URL(request.url);
        const basePath = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/shadow/sessions/${encodeURIComponent(params.remoteSessionId)}`;
        const fullPath = `${basePath}${reqUrl.search}`;
        const result = await callCoreJson(fullPath, {
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, basePath);
        }
        return result.data;
      },
      HIDDEN_DETAIL,
    )
    .post(
      '/api/v1/peers/devices/:deviceId/shadow/sync',
      async ({
        params,
        body,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        body: unknown;
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/shadow/sync`;
        const result = await callCoreJson(path, {
          method: 'POST',
          body: (body && typeof body === 'object' ? (body as Record<string, unknown>) : {}),
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, path);
        }
        return result.data;
      },
      HIDDEN_DETAIL,
    )
    .get(
      '/api/v1/peers/devices/:deviceId/devserver-lease',
      ({
        params,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        return {
          leases: peerClientPool.listDevServerLeases(params.deviceId),
        };
      },
      HIDDEN_DETAIL,
    )
    .post(
      '/api/v1/peers/devices/:deviceId/devserver-lease',
      async ({
        params,
        body,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        body: RequestDevServerLeaseInput;
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/devserver-lease`;
        try {
          return await peerClientPool.requestDevServerLease(
            params.deviceId,
            (body ?? {}) as RequestDevServerLeaseInput,
            request.signal,
          );
        } catch (err) {
          return formatPeerErrorResponse(set, err, path, rid);
        }
      },
      HIDDEN_DETAIL,
    )
    .delete(
      '/api/v1/peers/devices/:deviceId/devserver-lease/:leaseId',
      async ({
        params,
        request,
        set,
        server,
      }: {
        params: { deviceId: string; leaseId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/devserver-lease/${encodeURIComponent(params.leaseId)}`;
        try {
          return await peerClientPool.releaseDevServerLease(params.deviceId, params.leaseId);
        } catch (err) {
          return formatPeerErrorResponse(set, err, path, rid);
        }
      },
      HIDDEN_DETAIL,
    )
    .delete(
      '/api/v1/peers/devices/:deviceId/devserver-lease',
      async ({
        params,
        request,
        set,
        server,
      }: {
        params: { deviceId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const path = `/api/v1/peers/devices/${encodeURIComponent(params.deviceId)}/devserver-lease`;
        try {
          return await peerClientPool.releaseDevServerLease(params.deviceId);
        } catch (err) {
          return formatPeerErrorResponse(set, err, path, rid);
        }
      },
      HIDDEN_DETAIL,
    )
    .get(
      '/api/v1/peers/internal/devserver-lease',
      ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) => {
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        set.status = 403;
        set.headers['content-type'] = 'application/problem+json';
        return toProblemDetails(
          403,
          'policy_denied',
          'Internal DevServer lease endpoint is only accessible over authenticated mTLS peer tunnels.',
          '/api/v1/peers/internal/devserver-lease',
          rid,
        );
      },
      HIDDEN_DETAIL,
    )
    .post(
      '/api/v1/peers/internal/devserver-lease',
      ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) => {
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        set.status = 403;
        set.headers['content-type'] = 'application/problem+json';
        return toProblemDetails(
          403,
          'policy_denied',
          'Internal DevServer lease endpoint is only accessible over authenticated mTLS peer tunnels.',
          '/api/v1/peers/internal/devserver-lease',
          rid,
        );
      },
      HIDDEN_DETAIL,
    )
    .all(
      '/api/v1/peers/internal/devserver-lease',
      ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) => {
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        set.status = 403;
        set.headers['content-type'] = 'application/problem+json';
        return toProblemDetails(
          403,
          'policy_denied',
          'Internal DevServer lease endpoint is only accessible over authenticated mTLS peer tunnels.',
          '/api/v1/peers/internal/devserver-lease',
          rid,
        );
      },
      HIDDEN_DETAIL,
    )
    .delete(
      '/api/v1/peers/internal/devserver-lease/:leaseId',
      ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) => {
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const pathname = new URL(request.url).pathname;
        set.status = 403;
        set.headers['content-type'] = 'application/problem+json';
        return toProblemDetails(
          403,
          'policy_denied',
          'Internal DevServer lease endpoint is only accessible over authenticated mTLS peer tunnels.',
          pathname,
          rid,
        );
      },
      HIDDEN_DETAIL,
    )
    .all(
      '/api/v1/peers/internal/devserver-lease/:leaseId',
      ({
        request,
        set,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
      }) => {
        const rid = ensureRequestId(request.headers.get('x-request-id'));
        const pathname = new URL(request.url).pathname;
        set.status = 403;
        set.headers['content-type'] = 'application/problem+json';
        return toProblemDetails(
          403,
          'policy_denied',
          'Internal DevServer lease endpoint is only accessible over authenticated mTLS peer tunnels.',
          pathname,
          rid,
        );
      },
      HIDDEN_DETAIL,
    )

    // ── 4. Grants & Governance Access Policies (Worker Perspective) ──────────────
    .get(
      '/api/v1/peers/grants',
      async ({
        request,
        set,
        server,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const result = await callCoreJson('/api/v1/peers/grants', {
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, '/api/v1/peers/grants');
        }
        if (Array.isArray(result.data)) {
          return { grants: result.data };
        }
        return result.data;
      },
      HIDDEN_DETAIL,
    )
    .put(
      '/api/v1/peers/grants/:controllerDeviceId/policy',
      async ({
        params,
        body,
        request,
        set,
        server,
      }: {
        params: { controllerDeviceId: string };
        body: Record<string, unknown>;
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const path = `/api/v1/peers/grants/${encodeURIComponent(params.controllerDeviceId)}/policy`;
        const result = await callCoreJson(path, {
          method: 'PUT',
          body: (body ?? {}) as Record<string, unknown>,
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, path);
        }
        // In-process synchronous kick (< 1ms) in addition to Core SSE policy-events broadcast!
        peerTlsServer.kickController(
          params.controllerDeviceId,
          'Access policy updated; re-handshake required.',
        );
        return result.data;
      },
      HIDDEN_DETAIL,
    )
    .delete(
      '/api/v1/peers/grants/:controllerDeviceId',
      async ({
        params,
        request,
        set,
        server,
      }: {
        params: { controllerDeviceId: string };
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) => {
        const denied = checkMgmt(request, set, server);
        if (denied) return denied;
        const path = `/api/v1/peers/grants/${encodeURIComponent(params.controllerDeviceId)}`;
        const result = await callCoreJson(path, {
          method: 'DELETE',
          headers: forwardHeaders(request),
          signal: request.signal,
        });
        set.status = result.status;
        if (result.status >= 400) {
          set.headers['content-type'] = 'application/problem+json';
          return mapCoreErrorToExternal(result.status, result.data, path);
        }
        peerTlsServer.kickController(params.controllerDeviceId, 'Peer device grant revoked.');
        return result.data;
      },
      HIDDEN_DETAIL,
    )

    // ── 5. Transparent Multi-Endpoint HTTP & SSE Proxy (`ALL /api/v1/peers/:deviceId/proxy/*`) ──
    // Register explicit per-method routes in addition to `.all(...)` so Elysia/memoirist's
    // method-specific radix trees (`GET`/`HEAD`) match before `webServerPlugin`'s `/*` catch-all.
    .get('/api/v1/peers/:deviceId/proxy/*', handlePeerTransparentProxy, PROXY_ROUTE_OPTS)
    .head('/api/v1/peers/:deviceId/proxy/*', handlePeerTransparentProxy, PROXY_ROUTE_OPTS)
    .post('/api/v1/peers/:deviceId/proxy/*', handlePeerTransparentProxy, PROXY_ROUTE_OPTS)
    .put('/api/v1/peers/:deviceId/proxy/*', handlePeerTransparentProxy, PROXY_ROUTE_OPTS)
    .patch('/api/v1/peers/:deviceId/proxy/*', handlePeerTransparentProxy, PROXY_ROUTE_OPTS)
    .delete('/api/v1/peers/:deviceId/proxy/*', handlePeerTransparentProxy, PROXY_ROUTE_OPTS)
    .all('/api/v1/peers/:deviceId/proxy/*', handlePeerTransparentProxy, PROXY_ROUTE_OPTS)

    // ── 6. Transparent Multi-Endpoint WebSocket Terminal Proxy (`/ws/peers/:deviceId/terminal`) ──
    .ws('/ws/peers/:deviceId/terminal', {
      async beforeHandle({
        request,
        set,
        server,
      }: {
        request: Request;
        set: { status?: number | string; headers: Record<string, string | number> };
        server?: ServerLike | null;
      }) {
        const admission = await evaluateWebAdmission({
          request,
          server,
          runtime: options.webAuthRuntime,
          mode: options.mode,
          authConfig: options.authConfig,
          trustProxy: options.trustProxy,
        });
        if (!admission.admitted) {
          set.status = admission.status;
          set.headers['content-type'] = 'application/problem+json';
          return toProblemDetails(
            admission.status,
            admission.code,
            admission.message,
            new URL(request.url).pathname,
          );
        }
      },
      open(ws: {
        data?: { request?: Request; params?: { deviceId?: string } };
        send(data: string): void;
        close(code?: number, reason?: string): void;
      }) {
        const key = resolveWsKey(ws);
        const request = ws.data?.request;
        const deviceId = ws.data?.params?.deviceId ?? '';
        const reqUrl = request
          ? new URL(request.url)
          : new URL(`http://127.0.0.1/ws/peers/${encodeURIComponent(deviceId)}/terminal`);

        const tunnelHeaders: Record<string, string> = {};
        if (request) {
          const storageId =
            request.headers.get('x-tinadec-storage-id') ?? reqUrl.searchParams.get('storage_id');
          if (storageId) tunnelHeaders['x-tinadec-storage-id'] = storageId;
        }

        const downstream: WsTerminalClient = {
          send: (data) => ws.send(data),
          close: (code, reason) => ws.close(code, reason),
        };

        const bridge = createPeerTunnelTerminalBridgeSession({
          ws: downstream,
          openTunnel: (client) =>
            peerClientPool.openTerminalTunnel(deviceId, {
              query: reqUrl.search ? reqUrl.search.slice(1) : '',
              headers: tunnelHeaders,
              downstream: client,
            }),
        });

        activePeerWsTerminals.set(key, bridge);
      },
      message(ws: unknown, message: string | Buffer | Uint8Array) {
        activePeerWsTerminals.get(resolveWsKey(ws))?.onMessage(message);
      },
      close(ws: unknown) {
        const key = resolveWsKey(ws);
        const session = activePeerWsTerminals.get(key);
        if (session) {
          session.close();
          activePeerWsTerminals.delete(key);
        }
      },
    });
}
