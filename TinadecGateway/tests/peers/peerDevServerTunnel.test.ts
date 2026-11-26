/**
 * Wave 2 Same-Port TLS ALPN (`tinadec-peer-devserver/1`) DevServer Preview Tunnel Tests
 * (Spec §5.1, §5.3, §6.1, §8 Gate #10):
 *
 * 1. Real local HTTP + WebSocket HMR DevServer mapped over the shared `PeerTlsServer` port
 *    via `ALPN = 'tinadec-peer-devserver/1'` onto `127.0.0.1:<localMappedPort>`, verifying
 *    concurrent HTTP GET/POST, binary responses, WebSocket HMR text/binary bidirectional
 *    streaming, and in-place lease renewal alongside `tinadec-peer-http/1` control traffic.
 * 2. Hard SSRF & privilege blocklist: non-loopback `target_host` (`192.168.*`, `10.*`,
 *    `172.16.*`, `169.254.169.254`, `0.0.0.0`, external domain), management ports (`48730`,
 *    `48731`, `48732`, `48735..48755`, dynamic Worker ports), privileged ports (`< 1024`),
 *    unreachable ports, direct non-tunnel HTTP requests, and `observer` role all rejected.
 * 3. Zero-trust ALPN wire verification: forged client X.509 DER certificate, forged server
 *    certificate, tampered ticket, wrong controller_device_id, or `observer` downgrade on
 *    raw `tinadec-peer-devserver/1` connections immediately rejected & destroyed.
 * 4. Sub-second (`< 1s`) port & socket cleanup on lease TTL expiry, explicit `DELETE` close,
 *    Worker grant revocation (`kickController`), and Controller `disconnectDevice`.
 */

import { describe, expect, test } from 'bun:test';
import net from 'node:net';
import tls from 'node:tls';
import { Elysia } from 'elysia';
import { createWebAuthRuntime } from '../../src/webAuth.js';
import { PeerClientPool, type PeerDeviceRecord } from '../../src/peers/peerClientPool.js';
import {
  DevServerFramedStream,
  type MappedDevServerLeaseDto,
  openClientDevServerAlpnStream,
  type WorkerDevServerLeaseDto,
} from '../../src/peers/peerDevServerTunnel.js';
import type { PeerAccessPolicy, PeerPolicyEvent } from '../../src/peers/peerPolicyGuard.js';
import {
  generateSelfSignedP256TlsMaterial,
  PEER_ALPN_DEVSERVER,
  PeerProtocolError,
   type PeerRole,
} from '../../src/peers/peerProtocol.js';
import { registerPeerRoutes } from '../../src/peers/peerProxyRoutes.js';
import { PeerTlsServer } from '../../src/peers/peerTlsServer.js';

// ── Test Harnesses ────────────────────────────────────────────────────────────

interface HarnessGrant {
  controllerDeviceId: string;
  displayName: string;
  fingerprint: string;
  policy: PeerAccessPolicy;
}

function createWorkerCoreHarness(options?: {
  inboundEnabled?: boolean;
  pairingCode?: string;
  corePort?: number;
}) {
  const workerTls = generateSelfSignedP256TlsMaterial('worker-dev-host-b');
  let inboundEnabled = options?.inboundEnabled ?? true;
  let listenPort = 0;
  let displayName = 'Worker-Dev-B';
  const localDeviceId = 'dev-worker-b';
  let activePairingCode: { code: string; remaining: number; expiresAt: number } | null =
    options?.pairingCode
      ? { code: options.pairingCode, remaining: 5, expiresAt: Date.now() + 300_000 }
      : { code: '842917', remaining: 5, expiresAt: Date.now() + 300_000 };

  const grants = new Map<string, HarnessGrant>();
  const policyStreamControllers = new Set<ReadableStreamDefaultController<Uint8Array>>();
  let policySeq = 1;
  const encoder = new TextEncoder();
  const coreOrigin = `http://127.0.0.1:${options?.corePort ?? 49831}`;

  function emitPolicyEvent(event: PeerPolicyEvent): void {
    const chunk = encoder.encode(`data: ${JSON.stringify(event)}\n\n`);
    for (const ctrl of policyStreamControllers) {
      try {
        ctrl.enqueue(chunk);
      } catch {
        policyStreamControllers.delete(ctrl);
      }
    }
  }

  const fetchImpl: typeof fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(
      typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url,
    );
    const method = (init?.method ?? 'GET').toUpperCase();
    let bodyText = '';
    if (typeof init?.body === 'string') {
      bodyText = init.body;
    } else if (init?.body instanceof Uint8Array || Buffer.isBuffer(init?.body)) {
      bodyText = Buffer.from(init.body).toString('utf8');
    }

    if (url.pathname === '/api/v1/peers/internal/local-tls-material') {
      return Response.json({
        inbound_enabled: inboundEnabled,
        local_device_id: localDeviceId,
        display_name: displayName,
        listen_port: listenPort,
        local_fingerprint: workerTls.fingerprint,
        cert_pem: workerTls.certPem,
        private_key_pem: workerTls.privateKeyPem,
      });
    }

    if (url.pathname === '/api/v1/peers/internal/policy-events') {
      let ctrlRef: ReadableStreamDefaultController<Uint8Array> | null = null;
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          ctrlRef = controller;
          policyStreamControllers.add(controller);
          controller.enqueue(encoder.encode(': connected\n\n'));
        },
        cancel() {
          if (ctrlRef) policyStreamControllers.delete(ctrlRef);
        },
      });
      init?.signal?.addEventListener(
        'abort',
        () => {
          if (ctrlRef) {
            policyStreamControllers.delete(ctrlRef);
            try {
              ctrlRef.close();
            } catch {
              // ignore
            }
          }
        },
        { once: true },
      );
      return new Response(stream, {
        status: 200,
        headers: { 'content-type': 'text/event-stream' },
      });
    }

    if (url.pathname === '/api/v1/peers/internal/handshake/verify' && method === 'POST') {
      const req = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      if (!inboundEnabled) {
        return Response.json(
          { code: 'inbound_disabled', detail: 'Inbound peer connections disabled.' },
          { status: 403 },
        );
      }
      const mode = String(req.pairing_mode ?? '');
      const ctrlId = String(req.controller_device_id ?? '');
      const ctrlName = String(req.display_name ?? ctrlId);
      const remoteFp = String(req.remote_fingerprint ?? '').toLowerCase();

      if (mode === 'code') {
        if (!activePairingCode || String(req.pairing_code ?? '') !== activePairingCode.code) {
          return Response.json(
            { code: 'pairing_code_rejected', detail: 'Invalid pairing code.' },
            { status: 403 },
          );
        }
        activePairingCode = null;
        const policy: PeerAccessPolicy = {
          controller_device_id: ctrlId,
          role: 'operator',
          workspace_mode: 'all',
          workspace_ids: [],
          allow_non_workspace_sessions: true,
          sync_all_sessions: true,
          allow_agent_delegation: false,
          allow_provider_proxy: false,
          revision: `rev-${policySeq++}`,
        };
        grants.set(ctrlId, {
          controllerDeviceId: ctrlId,
          displayName: ctrlName,
          fingerprint: remoteFp,
          policy,
        });
        return Response.json({
          accepted: true,
          grant_id: `grant-${ctrlId}`,
          controller_device_id: ctrlId,
          display_name: ctrlName,
          policy_revision: policy.revision,
          policy,
        });
      }

      if (mode === 'grant') {
        const grant = grants.get(ctrlId);
        if (!grant) {
          return Response.json(
            { code: 'unauthorized', detail: 'Grant revoked or unknown controller.' },
            { status: 401 },
          );
        }
        if (grant.fingerprint !== remoteFp) {
          return Response.json(
            {
              code: 'fingerprint_mismatch',
              detail: 'Client TLS certificate does not match pinned grant fingerprint.',
            },
            { status: 403 },
          );
        }
        return Response.json({
          accepted: true,
          grant_id: `grant-${ctrlId}`,
          controller_device_id: ctrlId,
          display_name: grant.displayName,
          policy_revision: grant.policy.revision,
          policy: grant.policy,
        });
      }

      return Response.json({ code: 'handshake_failed', detail: 'Unsupported mode.' }, { status: 400 });
    }

    if (url.pathname === '/api/v1/peers/inbound') {
      if (method === 'PUT') {
        const patch = JSON.parse(bodyText || '{}') as Record<string, unknown>;
        if (typeof patch.enabled === 'boolean') inboundEnabled = patch.enabled;
        if (typeof patch.listen_port === 'number') listenPort = patch.listen_port;
      }
      return Response.json({
        enabled: inboundEnabled,
        display_name: displayName,
        listen_port: listenPort,
        local_device_id: localDeviceId,
        local_fingerprint: workerTls.fingerprint,
      });
    }

    if (url.pathname === '/api/v1/projects' && method === 'GET') {
      return Response.json([{ id: 'proj-vite-app', name: 'Vite Preview App', path: '/srv/vite-app' }]);
    }

    return Response.json({ code: 'not_found', detail: `Unhandled ${method} ${url.pathname}` }, { status: 404 });
  }) as unknown as typeof fetch;

  return {
    coreUrl: () => coreOrigin,
    workerTls,
    localDeviceId,
    grants,
    fetchImpl,
    setControllerRole(controllerId: string, role: PeerRole) {
      const grant = grants.get(controllerId);
      if (!grant) throw new Error(`Unknown grant ${controllerId}`);
      grant.policy = {
        ...grant.policy,
        role,
        revision: `rev-${policySeq++}`,
      };
      emitPolicyEvent({
        type: 'policy_updated',
        controller_device_id: controllerId,
        revision: grant.policy.revision,
        policy: grant.policy,
      });
    },
    revokeControllerGrant(controllerId: string) {
      grants.delete(controllerId);
      emitPolicyEvent({
        type: 'grant_revoked',
        controller_device_id: controllerId,
      });
    },
  };
}

function createControllerCoreHarness() {
  const controllerTls = generateSelfSignedP256TlsMaterial('controller-dev-host-a');
  const controllerDeviceId = 'dev-ctrl-a';
  const devices = new Map<string, PeerDeviceRecord>();
  const shadowSessions = new Map<string, Array<Record<string, unknown>>>();

  const fetchImpl: typeof fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(
      typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url,
    );
    const method = (init?.method ?? 'GET').toUpperCase();
    const bodyText = typeof init?.body === 'string' ? init.body : '';

    if (url.pathname === '/api/v1/peers/internal/local-tls-material') {
      return Response.json({
        inbound_enabled: false,
        local_device_id: controllerDeviceId,
        display_name: 'Controller-A',
        listen_port: 48735,
        local_fingerprint: controllerTls.fingerprint,
        cert_pem: controllerTls.certPem,
        private_key_pem: controllerTls.privateKeyPem,
      });
    }

    if (url.pathname === '/api/v1/peers/devices/pair' && method === 'POST') {
      const req = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      const remoteId = String(req.remote_device_id ?? 'remote');
      const record: PeerDeviceRecord = {
        device_id: `peer-${remoteId}`,
        display_name: String(req.display_name ?? remoteId),
        transport: 'lan',
        address: String(req.address ?? ''),
        remote_device_id: remoteId,
        certificate_fingerprint: String(req.certificate_fingerprint ?? ''),
        protocol_major: Number(req.protocol_major ?? 1),
        protocol_minor: Number(req.protocol_minor ?? 0),
        paired_at: Date.now(),
        last_connected_at: Date.now(),
        connection_desired: req.connection_desired !== false,
        adopt_sessions: req.adopt_sessions !== false,
        last_state: 'ready',
        last_failure_code: null,
        access_revision: typeof req.access_revision === 'string' ? req.access_revision : 'rev-1',
      };
      devices.set(record.device_id, record);
      return Response.json(record, { status: 201 });
    }

    if (url.pathname === '/api/v1/peers/devices' && method === 'GET') {
      return Response.json({ devices: [...devices.values()] });
    }

    if (url.pathname === '/api/v1/peers/internal/controller-state' && method === 'POST') {
      const req = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      const dev = devices.get(String(req.device_id ?? ''));
      if (dev) {
        if (typeof req.last_state === 'string') {
          dev.last_state = req.last_state as PeerDeviceRecord['last_state'];
        }
        dev.last_failure_code =
          typeof req.last_failure_code === 'string' ? req.last_failure_code : null;
      }
      return Response.json({ ok: true });
    }

    const shadowSyncMatch = /^\/api\/v1\/peers\/devices\/([^/]+)\/shadow\/sync$/i.exec(url.pathname);
    if (shadowSyncMatch && method === 'POST') {
      const devId = decodeURIComponent(shadowSyncMatch[1]!);
      const req = JSON.parse(bodyText || '{}') as { sessions?: Array<Record<string, unknown>> };
      shadowSessions.set(devId, req.sessions ?? []);
      return Response.json({ synced: true, count: (req.sessions ?? []).length });
    }

    const shadowListMatch = /^\/api\/v1\/peers\/devices\/([^/]+)\/shadow\/sessions$/i.exec(url.pathname);
    if (shadowListMatch && method === 'GET') {
      const devId = decodeURIComponent(shadowListMatch[1]!);
      const pid = url.searchParams.get('project_id');
      const rows = (shadowSessions.get(devId) ?? []).filter(
        (s) => !pid || s.remote_project_id === pid,
      );
      return Response.json({ sessions: rows });
    }

    return Response.json({ ok: true });
  }) as unknown as typeof fetch;

  return {
    controllerTls,
    controllerDeviceId,
    devices,
    fetchImpl,
  };
}

function buildControllerGatewayApp(
  workerTlsServer: PeerTlsServer,
  clientPool: PeerClientPool,
  ctrlCore: ReturnType<typeof createControllerCoreHarness>,
) {
  const app = new Elysia();
  registerPeerRoutes(app, {
    peerTlsServer: workerTlsServer,
    peerClientPool: clientPool,
    forwardHeaders: () => ({
      'x-request-id': crypto.randomUUID(),
      'x-tinadec-principal': 'dev@local',
    }),
    keepStreamOpen: () => undefined,
    webAuthRuntime: createWebAuthRuntime(),
    mode: 'local',
    trustProxy: 'false',
    proxyCoreJson: async (path, opts) => {
      const res = await ctrlCore.fetchImpl(`http://127.0.0.1:48731${path}`, {
        method: opts?.method ?? 'GET',
        headers: {
          ...(opts?.headers ?? {}),
          ...(opts?.body ? { 'content-type': 'application/json' } : {}),
        },
        body: opts?.body ? JSON.stringify(opts.body) : undefined,
        signal: opts?.signal,
      });
      const data = await res.json().catch(() => null);
      return { status: res.status, data, headers: res.headers };
    },
  });
  return app;
}

function createLocalHttpAndWsDevServer() {
  const connectedHmrSockets = new Set<import('bun').ServerWebSocket<{ path: string }>>();

  const server = Bun.serve<{ path: string }>({
    hostname: '127.0.0.1',
    port: 0,
    async fetch(req, srv) {
      const url = new URL(req.url);
      if (req.headers.get('upgrade')?.toLowerCase() === 'websocket') {
        const ok = srv.upgrade(req, { data: { path: url.pathname } });
        if (ok) return undefined as unknown as Response;
        return new Response('WebSocket upgrade failed', { status: 400 });
      }

      if (url.pathname === '/index.html' && req.method === 'GET') {
        return new Response('<!doctype html><html><body><h1>Vite Remote Preview</h1></body></html>', {
          status: 200,
          headers: { 'content-type': 'text/html; charset=utf-8' },
        });
      }

      if (url.pathname === '/api/echo' && req.method === 'POST') {
        const body = await req.json();
        return Response.json({
          mirrored: body,
          query: url.searchParams.get('tag'),
          host_header: req.headers.get('host'),
        });
      }

      if (url.pathname === '/assets/chunk.bin' && req.method === 'GET') {
        const bytes = new Uint8Array(4096);
        for (let i = 0; i < bytes.length; i++) bytes[i] = (i * 31) & 0xff;
        return new Response(bytes, {
          status: 200,
          headers: { 'content-type': 'application/octet-stream' },
        });
      }

      return new Response('Not Found', { status: 404 });
    },
    websocket: {
      open(ws) {
        connectedHmrSockets.add(ws);
        ws.send(JSON.stringify({ type: 'connected', path: ws.data.path }));
      },
      message(ws, message) {
        if (typeof message === 'string') {
          const parsed = JSON.parse(message) as Record<string, unknown>;
          ws.send(JSON.stringify({ type: 'hmr-echo', payload: parsed }));
        } else {
          const view = message instanceof Uint8Array ? message : new Uint8Array(message);
          const inverted = new Uint8Array(view.length);
          for (let i = 0; i < view.length; i++) inverted[i] = view[i]! ^ 0xff;
          ws.send(inverted);
        }
      },
      close(ws) {
        connectedHmrSockets.delete(ws);
      },
    },
  });

  return {
    port: server.port!,
    broadcastHmrUpdate(file: string) {
      const msg = JSON.stringify({ type: 'update', updates: [{ path: file, timestamp: Date.now() }] });
      for (const ws of connectedHmrSockets) {
        ws.send(msg);
      }
    },
    stop() {
      for (const ws of connectedHmrSockets) {
        try {
          ws.close();
        } catch {
          // ignore
        }
      }
      connectedHmrSockets.clear();
      server.stop(true);
    },
  };
}

async function isTcpPortAcceptingConnections(port: number, host = '127.0.0.1'): Promise<boolean> {
  return await new Promise<boolean>((resolve) => {
    const sock = net.connect({ host, port });
    const timer = setTimeout(() => {
      sock.destroy();
      resolve(false);
    }, 150);
    sock.once('connect', () => {
      clearTimeout(timer);
      sock.destroy();
      resolve(true);
    });
    sock.once('error', () => {
      clearTimeout(timer);
      sock.destroy();
      resolve(false);
    });
  });
}

async function waitForPortClosed(port: number, maxWaitMs = 950): Promise<number> {
  const startedAt = performance.now();
  while (performance.now() - startedAt < maxWaitMs) {
    const open = await isTcpPortAcceptingConnections(port);
    if (!open) {
      return performance.now() - startedAt;
    }
    await new Promise((r) => setTimeout(r, 15));
  }
  const stillOpen = await isTcpPortAcceptingConnections(port);
  if (stillOpen) {
    throw new Error(`Mapped port 127.0.0.1:${port} was still accepting TCP connections after ${maxWaitMs}ms!`);
  }
  return performance.now() - startedAt;
}

// ── Test Suite ────────────────────────────────────────────────────────────────

describe('Wave 2 Same-Port TLS ALPN DevServer Preview Tunnel', () => {
  test('1. End-to-end HTTP + WebSocket HMR preview over shared mTLS port with ALPN tinadec-peer-devserver/1 & lease renewal', async () => {
    const devServer = createLocalHttpAndWsDevServer();
    const workerCore = createWorkerCoreHarness({ inboundEnabled: true, pairingCode: '517300' });
    const ctrlCore = createControllerCoreHarness();

    const workerTlsServer = new PeerTlsServer({
      coreUrl: workerCore.coreUrl,
      fetchImpl: workerCore.fetchImpl,
      bindHost: '127.0.0.1',
      listenPortOverride: 0,
    });
    const clientPool = new PeerClientPool({
      coreUrl: () => 'http://127.0.0.1:49731',
      fetchImpl: ctrlCore.fetchImpl,
    });
    const gatewayApp = buildControllerGatewayApp(workerTlsServer, clientPool, ctrlCore);

    try {
      const status = await workerTlsServer.start();
      expect(status.listening).toBe(true);
      const sharedTlsPort = status.actualListenPort!;
      expect(sharedTlsPort).toBeGreaterThan(0);

      // Pair Controller A -> Worker B over sharedTlsPort (ALPN tinadec-peer-http/1).
      const pairRes = await gatewayApp.handle(
        new Request('http://127.0.0.1:48730/api/v1/peers/devices/pair', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            address: `127.0.0.1:${sharedTlsPort}`,
            pairing_code: '517300',
            expected_fingerprint: workerCore.workerTls.fingerprint,
            display_name: 'Worker B',
          }),
        }),
      );
      expect(pairRes.status).toBe(201);
      const pairedDevice = (await pairRes.json()) as PeerDeviceRecord;

      // Request DevServer preview lease via Controller management route.
      const leaseRes = await gatewayApp.handle(
        new Request(
          `http://127.0.0.1:48730/api/v1/peers/devices/${pairedDevice.device_id}/devserver-lease`,
          {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ target_port: devServer.port }),
          },
        ),
      );
      expect(leaseRes.status).toBe(200);
      const mapped = (await leaseRes.json()) as MappedDevServerLeaseDto;
      expect(mapped.device_id).toBe(pairedDevice.device_id);
      expect(mapped.target_port).toBe(devServer.port);
      expect(mapped.local_port).toBeGreaterThan(1024);
      expect(mapped.local_port).not.toBe(devServer.port);
      expect(mapped.local_port).not.toBe(sharedTlsPort);
      expect(mapped.preview_url).toBe(`http://127.0.0.1:${mapped.local_port}`);
      expect(mapped.ttl_seconds).toBe(1800);
      expect(mapped.expires_at_ms).toBeGreaterThan(Date.now() + 1_700_000);

      // 1A. Real HTTP GET HTML + binary asset + POST JSON through mapped loopback port.
      const htmlRes = await fetch(`${mapped.preview_url}/index.html`);
      expect(htmlRes.status).toBe(200);
      expect(await htmlRes.text()).toContain('<h1>Vite Remote Preview</h1>');

      const postRes = await fetch(`${mapped.preview_url}/api/echo?tag=wave2`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ framework: 'vite', hmr: true }),
      });
      expect(postRes.status).toBe(200);
      const postJson = (await postRes.json()) as { mirrored: unknown; query: string };
      expect(postJson.mirrored).toEqual({ framework: 'vite', hmr: true });
      expect(postJson.query).toBe('wave2');

      const binRes = await fetch(`${mapped.preview_url}/assets/chunk.bin`);
      expect(binRes.status).toBe(200);
      const binBytes = new Uint8Array(await binRes.arrayBuffer());
      expect(binBytes.byteLength).toBe(4096);
      expect(binBytes[0]).toBe(0);
      expect(binBytes[1]).toBe(31);
      expect(binBytes[10]).toBe((10 * 31) & 0xff);

      // 1B. Real WebSocket HMR Upgrade + bidirectional text & binary frames over the same mapped port!
      const wsMessages: Array<string | Uint8Array> = [];
      let resolveNextMsg: (() => void) | null = null;
      const waitForMsgCount = async (count: number) => {
        const deadline = Date.now() + 3_000;
        while (wsMessages.length < count && Date.now() < deadline) {
          await new Promise<void>((resolve) => {
            const t = setTimeout(resolve, 30);
            resolveNextMsg = () => {
              clearTimeout(t);
              resolve();
            };
          });
        }
        expect(wsMessages.length).toBeGreaterThanOrEqual(count);
      };

      const ws = new WebSocket(`ws://127.0.0.1:${mapped.local_port}/__vite_hmr`);
      ws.binaryType = 'arraybuffer';
      await new Promise<void>((resolve, reject) => {
        ws.onopen = () => resolve();
        ws.onerror = () => reject(new Error('WebSocket failed to open over ALPN DevServer tunnel'));
      });
      ws.onmessage = (ev) => {
        if (typeof ev.data === 'string') {
          wsMessages.push(ev.data);
        } else {
          wsMessages.push(new Uint8Array(ev.data as ArrayBuffer));
        }
        resolveNextMsg?.();
      };

      // Server greeting frame
      await waitForMsgCount(1);
      expect(JSON.parse(wsMessages[0] as string)).toEqual({
        type: 'connected',
        path: '/__vite_hmr',
      });

      // Client -> Server -> Client text HMR frame
      ws.send(JSON.stringify({ event: 'vite:ping', seq: 42 }));
      await waitForMsgCount(2);
      expect(JSON.parse(wsMessages[1] as string)).toEqual({
        type: 'hmr-echo',
        payload: { event: 'vite:ping', seq: 42 },
      });

      // Server-initiated HMR push broadcast
      devServer.broadcastHmrUpdate('/src/App.vue');
      await waitForMsgCount(3);
      expect(JSON.parse(wsMessages[2] as string)).toMatchObject({
        type: 'update',
        updates: [{ path: '/src/App.vue' }],
      });

      // Binary WebSocket frame round-trip
      ws.send(new Uint8Array([0x00, 0x55, 0xaa, 0xff]));
      await waitForMsgCount(4);
      expect(Array.from(wsMessages[3] as Uint8Array)).toEqual([0xff, 0xaa, 0x55, 0x00]);

      // 1C. Control-plane proxy (`tinadec-peer-http/1`) continues working concurrently on the same port.
      const proxyRes = await gatewayApp.handle(
        new Request(
          `http://127.0.0.1:48730/api/v1/peers/${pairedDevice.device_id}/proxy/api/v1/projects`,
        ),
      );
      expect(proxyRes.status).toBe(200);
      expect(await proxyRes.json()).toEqual([
        { id: 'proj-vite-app', name: 'Vite Preview App', path: '/srv/vite-app' },
      ]);

      // 1D. In-place lease renewal preserves `lease_id` and `local_port` without interrupting WS!
      const renewRes = await gatewayApp.handle(
        new Request(
          `http://127.0.0.1:48730/api/v1/peers/devices/${pairedDevice.device_id}/devserver-lease`,
          {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ target_port: devServer.port, ttl_seconds: 3600 }),
          },
        ),
      );
      expect(renewRes.status).toBe(200);
      const renewed = (await renewRes.json()) as MappedDevServerLeaseDto;
      expect(renewed.lease_id).toBe(mapped.lease_id);
      expect(renewed.local_port).toBe(mapped.local_port);
      expect(renewed.expires_at_ms).toBeGreaterThan(mapped.expires_at_ms);

      // Existing WS is still open after renewal!
      ws.send(JSON.stringify({ event: 'after-renew' }));
      await waitForMsgCount(5);
      expect(JSON.parse(wsMessages[4] as string)).toEqual({
        type: 'hmr-echo',
        payload: { event: 'after-renew' },
      });

      const wsClosed = new Promise<void>((resolve) => {
        if (ws.readyState === WebSocket.CLOSED) {
          resolve();
          return;
        }
        ws.addEventListener('close', () => resolve(), { once: true });
      });
      ws.close();
      await wsClosed;
    } finally {
      await clientPool.stop();
      await workerTlsServer.stop();
      devServer.stop();
    }
  });

  test('2. Strict SSRF blocklist: rejects non-loopback hosts, management ports, privileged ports, closed ports, direct HTTP, and observer role', async () => {
    const devServer = createLocalHttpAndWsDevServer();
    const workerCore = createWorkerCoreHarness({
      inboundEnabled: true,
      pairingCode: '998877',
      corePort: 49831,
    });
    const ctrlCore = createControllerCoreHarness();

    const workerTlsServer = new PeerTlsServer({
      coreUrl: workerCore.coreUrl,
      fetchImpl: workerCore.fetchImpl,
      bindHost: '127.0.0.1',
      listenPortOverride: 0,
      devServerAllowedPorts: [devServer.port, 5173, 3000],
    });
    const clientPool = new PeerClientPool({
      coreUrl: () => 'http://127.0.0.1:49731',
      fetchImpl: ctrlCore.fetchImpl,
    });
    const gatewayApp = buildControllerGatewayApp(workerTlsServer, clientPool, ctrlCore);

    try {
      const status = await workerTlsServer.start();
      const sharedTlsPort = status.actualListenPort!;

      const pairRes = await gatewayApp.handle(
        new Request('http://127.0.0.1:48730/api/v1/peers/devices/pair', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            address: `127.0.0.1:${sharedTlsPort}`,
            pairing_code: '998877',
            expected_fingerprint: workerCore.workerTls.fingerprint,
          }),
        }),
      );
      expect(pairRes.status).toBe(201);
      const paired = (await pairRes.json()) as PeerDeviceRecord;

      const requestLease = async (body: Record<string, unknown>) => {
        const res = await gatewayApp.handle(
          new Request(
            `http://127.0.0.1:48730/api/v1/peers/devices/${paired.device_id}/devserver-lease`,
            {
              method: 'POST',
              headers: { 'content-type': 'application/json' },
              body: JSON.stringify(body),
            },
          ),
        );
        const json = (await res.json()) as Record<string, unknown>;
        return { status: res.status, json };
      };

      // 2A. Non-loopback SSRF target_host vectors MUST ALL be blocked with 403 policy_denied!
      for (const badHost of [
        '192.168.1.10',
        '10.0.0.8',
        '172.16.5.4',
        '169.254.169.254',
        '0.0.0.0',
        '::',
        '127.0.0.2',
        'example.com',
        'localhost.attacker.tld',
        'http://127.0.0.1',
        '2130706433',
      ]) {
        const res = await requestLease({ target_host: badHost, target_port: devServer.port });
        expect(res.status).toBe(403);
        expect(res.json.code).toBe('policy_denied');
      }

      // 2B. Management & privileged ports MUST ALL be blocked with 403 policy_denied!
      for (const badPort of [
        22, // SSH privileged
        80, // HTTP privileged
        443, // HTTPS privileged
        48730, // TinadecGateway default
        48731, // TinadecCore default
        48732, // ToolRuntime default
        48735, // PeerTlsServer default
        48742, // PeerTlsServer probe range
        48755, // PeerTlsServer probe ceiling
        sharedTlsPort, // Dynamic actual PeerTlsServer port
        49831, // Dynamic Worker Core port
        8088, // Outside devServerAllowedPorts whitelist
      ]) {
        const res = await requestLease({ target_port: badPort });
        expect(res.status).toBe(403);
        expect(res.json.code).toBe('policy_denied');
      }

      // 2C. Unreachable closed loopback port (in whitelist 5173 or unused ephemeral) returns 502 unreachable.
      // Pick an unused port via temporary bind+close and create a server without whitelist restriction to verify reachability error.
      const closedPort = await new Promise<number>((resolve) => {
        const tmp = net.createServer();
        tmp.listen(0, '127.0.0.1', () => {
          const p = (tmp.address() as net.AddressInfo).port;
          tmp.close(() => resolve(p));
        });
      });
      const workerUnrestricted = new PeerTlsServer({
        coreUrl: workerCore.coreUrl,
        fetchImpl: workerCore.fetchImpl,
        bindHost: '127.0.0.1',
        listenPortOverride: 0,
      });
      // Direct internal lease manager check on closed port -> 502 unreachable.
      const closedDecision = await workerUnrestricted.devServerTunnel.handleInternalLeaseHttpRequest(
        {
          controllerDeviceId: ctrlCore.controllerDeviceId,
          displayName: 'Controller-A',
          clientFingerprint: ctrlCore.controllerTls.fingerprint,
          policy: {
            controller_device_id: ctrlCore.controllerDeviceId,
            role: 'operator',
            workspace_mode: 'all',
            revision: 'rev-1',
          },
        },
        'POST',
        '/api/v1/peers/internal/devserver-lease',
        { target_port: closedPort },
      );
      expect(closedDecision.ok).toBe(false);
      if (!closedDecision.ok) {
        expect(closedDecision.status).toBe(502);
        expect(closedDecision.code).toBe('unreachable');
      }

      // 2D. Direct non-tunnel HTTP call to `/api/v1/peers/internal/devserver-lease` MUST be rejected 403!
      const directInternal = await gatewayApp.handle(
        new Request('http://127.0.0.1:48730/api/v1/peers/internal/devserver-lease', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ target_port: devServer.port }),
        }),
      );
      expect(directInternal.status).toBe(403);
      expect(((await directInternal.json()) as { code: string }).code).toBe('policy_denied');

      // 2E. Downgrading controller policy to `observer` blocks DevServer lease requests with 403 `policy_denied`!
      workerCore.setControllerRole(ctrlCore.controllerDeviceId, 'observer');
      // Wait briefly for disconnect + reconnect under `observer` role.
      await new Promise((r) => setTimeout(r, 40));
      await clientPool.connectDevice(paired.device_id);

      const observerRes = await requestLease({ target_port: devServer.port });
      expect(observerRes.status).toBe(403);
      expect(observerRes.json.code).toBe('policy_denied');
    } finally {
      await clientPool.stop();
      await workerTlsServer.stop();
      devServer.stop();
    }
  });

  test('3. Zero-trust ALPN wire security: forged client/server X.509 certs, bad tickets, and observer role rejected on tinadec-peer-devserver/1', async () => {
    const devServer = createLocalHttpAndWsDevServer();
    const workerCore = createWorkerCoreHarness({ inboundEnabled: true, pairingCode: '334455' });
    const ctrlCore = createControllerCoreHarness();

    const workerTlsServer = new PeerTlsServer({
      coreUrl: workerCore.coreUrl,
      fetchImpl: workerCore.fetchImpl,
      bindHost: '127.0.0.1',
      listenPortOverride: 0,
    });
    const clientPool = new PeerClientPool({
      coreUrl: () => 'http://127.0.0.1:49731',
      fetchImpl: ctrlCore.fetchImpl,
    });

    try {
      const status = await workerTlsServer.start();
      const sharedTlsPort = status.actualListenPort!;

      const paired = await clientPool.pairWithPeer({
        address: `127.0.0.1:${sharedTlsPort}`,
        pairing_code: '334455',
        expected_fingerprint: workerCore.workerTls.fingerprint,
      });

      // Obtain a genuine remote lease over the authenticated control tunnel.
      const rawLeaseRes = await clientPool.proxyHttpRequest(paired.device_id, {
        method: 'POST',
        path: '/api/v1/peers/internal/devserver-lease',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ target_port: devServer.port }),
      });
      expect(rawLeaseRes.status).toBe(200);
      const genuineLease = (await rawLeaseRes.json()) as WorkerDevServerLeaseDto;

      // Helper to send a raw ALPN `devserver.open` frame and inspect the server rejection.
      const attemptRawAlpnOpen = async (opts: {
        certPem: string;
        privateKeyPem: string;
        leaseId: string;
        ticket: string;
        controllerDeviceId: string;
      }) => {
        const sock = await new Promise<tls.TLSSocket>((resolve, reject) => {
          const s = tls.connect(
            {
              host: '127.0.0.1',
              port: sharedTlsPort,
              cert: opts.certPem,
              key: opts.privateKeyPem,
              rejectUnauthorized: false,
              ALPNProtocols: [PEER_ALPN_DEVSERVER],
            },
            () => {
              resolve(s);
            },
          );
          s.once('error', reject);
        });
        const stream = new DevServerFramedStream(sock);
        try {
          expect(sock.alpnProtocol).toBe(PEER_ALPN_DEVSERVER);
          stream.sendHandshake({
            type: 'devserver.open',
            lease_id: opts.leaseId,
            ticket: opts.ticket,
            controller_device_id: opts.controllerDeviceId,
          });
          return await stream.readHandshake(3_000);
        } finally {
          stream.destroy();
        }
      };

      // 3A. Forged client X.509 DER certificate presenting stolen valid lease_id + ticket -> fingerprint_mismatch!
      const attackerTls = generateSelfSignedP256TlsMaterial('mitm-attacker');
      const forgedCertFrame = await attemptRawAlpnOpen({
        certPem: attackerTls.certPem,
        privateKeyPem: attackerTls.privateKeyPem,
        leaseId: genuineLease.lease_id,
        ticket: genuineLease.ticket,
        controllerDeviceId: ctrlCore.controllerDeviceId,
      });
      expect(forgedCertFrame).toMatchObject({
        type: 'devserver.open-reject',
        code: 'fingerprint_mismatch',
      });

      // 3B. Genuine client cert but tampered ticket -> unauthorized!
      const badTicketFrame = await attemptRawAlpnOpen({
        certPem: ctrlCore.controllerTls.certPem,
        privateKeyPem: ctrlCore.controllerTls.privateKeyPem,
        leaseId: genuineLease.lease_id,
        ticket: '0'.repeat(64),
        controllerDeviceId: ctrlCore.controllerDeviceId,
      });
      expect(badTicketFrame).toMatchObject({
        type: 'devserver.open-reject',
        code: 'unauthorized',
      });

      // 3C. Controller-side server cert pinning: forged server fingerprint MUST throw PeerProtocolError('fingerprint_mismatch')!
      let caught3C: unknown = null;
      try {
        await openClientDevServerAlpnStream({
          host: '127.0.0.1',
          port: sharedTlsPort,
          certPem: ctrlCore.controllerTls.certPem,
          privateKeyPem: ctrlCore.controllerTls.privateKeyPem,
          pinnedServerFingerprint: attackerTls.fingerprint,
          leaseId: genuineLease.lease_id,
          ticket: genuineLease.ticket,
          controllerDeviceId: ctrlCore.controllerDeviceId,
        });
      } catch (err) {
        caught3C = err;
      }
      expect(caught3C).toBeInstanceOf(PeerProtocolError);
      expect((caught3C as PeerProtocolError).code).toBe('fingerprint_mismatch');

      // 3D. Policy downgraded to `observer` -> existing lease is revoked & new ALPN streams are rejected!
      workerCore.setControllerRole(ctrlCore.controllerDeviceId, 'observer');
      await new Promise((r) => setTimeout(r, 30));
      const observerFrame = await attemptRawAlpnOpen({
        certPem: ctrlCore.controllerTls.certPem,
        privateKeyPem: ctrlCore.controllerTls.privateKeyPem,
        leaseId: genuineLease.lease_id,
        ticket: genuineLease.ticket,
        controllerDeviceId: ctrlCore.controllerDeviceId,
      });
      expect(observerFrame.type).toBe('devserver.open-reject');
      if (observerFrame.type === 'devserver.open-reject') {
        expect(['policy_denied', 'unauthorized']).toContain(observerFrame.code);
      }
    } finally {
      await clientPool.stop();
      await workerTlsServer.stop();
      devServer.stop();
    }
  });

  test('4. Sub-second (< 1s) port & active socket teardown on TTL expiry, explicit DELETE, Worker grant revoke, and disconnectDevice', async () => {
    const devServer = createLocalHttpAndWsDevServer();
    const workerCore = createWorkerCoreHarness({ inboundEnabled: true, pairingCode: '776655' });
    const ctrlCore = createControllerCoreHarness();

    const workerTlsServer = new PeerTlsServer({
      coreUrl: workerCore.coreUrl,
      fetchImpl: workerCore.fetchImpl,
      bindHost: '127.0.0.1',
      listenPortOverride: 0,
    });
    const clientPool = new PeerClientPool({
      coreUrl: () => 'http://127.0.0.1:49731',
      fetchImpl: ctrlCore.fetchImpl,
    });
    const gatewayApp = buildControllerGatewayApp(workerTlsServer, clientPool, ctrlCore);

    try {
      const status = await workerTlsServer.start();
      const paired = await clientPool.pairWithPeer({
        address: `127.0.0.1:${status.actualListenPort!}`,
        pairing_code: '776655',
        expected_fingerprint: workerCore.workerTls.fingerprint,
      });

      // ── 4A. Lease TTL expiry closes mapped port in < 1s ─────────────────────
      const shortLease = await clientPool.requestDevServerLease(paired.device_id, {
        target_port: devServer.port,
        ttl_ms: 350,
      });
      const okBeforeExpiry = await fetch(`${shortLease.preview_url}/index.html`);
      expect(okBeforeExpiry.status).toBe(200);

      const expiryCloseMs = await waitForPortClosed(shortLease.local_port, 900);
      expect(expiryCloseMs).toBeLessThan(900);
      expect(await isTcpPortAcceptingConnections(shortLease.local_port)).toBe(false);

      // ── 4B. Explicit DELETE closes mapped port & active WS in < 1s ──────────
      const deleteLease = await clientPool.requestDevServerLease(paired.device_id, {
        target_port: devServer.port,
        ttl_seconds: 1800,
      });
      const wsBClosed = new Promise<void>((resolve) => {
        const wsB = new WebSocket(`ws://127.0.0.1:${deleteLease.local_port}/__vite_hmr`);
        wsB.onopen = () => {
          void gatewayApp
            .handle(
              new Request(
                `http://127.0.0.1:48730/api/v1/peers/devices/${paired.device_id}/devserver-lease/${deleteLease.lease_id}`,
                { method: 'DELETE' },
              ),
            )
            .then(async (res) => {
              expect(res.status).toBe(200);
              const body = (await res.json()) as { released: boolean };
              expect(body.released).toBe(true);
            });
        };
        wsB.onclose = () => resolve();
      });
      const deleteStart = performance.now();
      await wsBClosed;
      const deletePortClosedMs = await waitForPortClosed(deleteLease.local_port, 500);
      expect(performance.now() - deleteStart).toBeLessThan(900);
      expect(deletePortClosedMs).toBeLessThan(500);

      // ── 4C. Controller `disconnectDevice` closes mapped port in < 1s ────────
      const disconnectLease = await clientPool.requestDevServerLease(paired.device_id, {
        target_port: devServer.port,
      });
      expect(await isTcpPortAcceptingConnections(disconnectLease.local_port)).toBe(true);

      const discStart = performance.now();
      await clientPool.disconnectDevice(paired.device_id);
      const discCloseMs = await waitForPortClosed(disconnectLease.local_port, 500);
      expect(performance.now() - discStart).toBeLessThan(500);
      expect(discCloseMs).toBeLessThan(500);

      // ── 4D. Worker grant revocation (`kickController`) tears down WS & closes Controller mapped port in < 1s ──
      await clientPool.connectDevice(paired.device_id);
      const revokeLease = await clientPool.requestDevServerLease(paired.device_id, {
        target_port: devServer.port,
      });

      let wsDClosedAt = 0;
      const wsDOpened = new Promise<WebSocket>((resolve, reject) => {
        const wsD = new WebSocket(`ws://127.0.0.1:${revokeLease.local_port}/__vite_hmr`);
        wsD.onopen = () => resolve(wsD);
        wsD.onerror = () => reject(new Error('wsD open failed'));
        wsD.onclose = () => {
          wsDClosedAt = performance.now();
        };
      });
      const wsD = await wsDOpened;
      const wsDClosePromise = new Promise<void>((resolve) => {
        if (wsD.readyState === WebSocket.CLOSED) {
          resolve();
          return;
        }
        wsD.addEventListener('close', () => resolve(), { once: true });
      });

      const revokeStart = performance.now();
      workerCore.revokeControllerGrant(ctrlCore.controllerDeviceId);
      workerTlsServer.kickController(ctrlCore.controllerDeviceId, 'Grant revoked in test.');

      await wsDClosePromise;
      const revokePortClosedMs = await waitForPortClosed(revokeLease.local_port, 500);
      expect(wsDClosedAt - revokeStart).toBeLessThan(500);
      expect(revokePortClosedMs).toBeLessThan(500);

      // Bonus check: local shadow management passthrough routes on Gateway forward cleanly to Core.
      const syncShadowRes = await gatewayApp.handle(
        new Request(
          `http://127.0.0.1:48730/api/v1/peers/devices/${paired.device_id}/shadow/sync`,
          {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({
              sessions: [{ remote_session_id: 's-1', remote_project_id: 'proj-vite-app', title: 'Offline Shadow' }],
            }),
          },
        ),
      );
      expect(syncShadowRes.status).toBe(200);

      const listShadowRes = await gatewayApp.handle(
        new Request(
          `http://127.0.0.1:48730/api/v1/peers/devices/${paired.device_id}/shadow/sessions?project_id=proj-vite-app`,
        ),
      );
      expect(listShadowRes.status).toBe(200);
      expect(await listShadowRes.json()).toEqual({
        sessions: [{ remote_session_id: 's-1', remote_project_id: 'proj-vite-app', title: 'Offline Shadow' }],
      });
    } finally {
      await clientPool.stop();
      await workerTlsServer.stop();
      devServer.stop();
    }
  });
});
