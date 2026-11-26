/**
 * High-signal transport, zero-trust pinning, policy guard, <100ms revocation,
 * and multi-endpoint HTTP/SSE/WS transparent proxy tests for TinadecGateway Peers.
 *
 * Mutation Verification Strategy for Certificate Fingerprint Pinning (Spec §8 Gate #3):
 * - Every TLS test uses real ECDSA P-256 X.509 DER certificates over real `node:tls` sockets.
 * - Fingerprint extraction (`extractPeerCertFingerprint`) hashes `socket.getPeerCertificate(true).raw`
 *   directly via SHA-256; application-layer `peer.hello` / `peer.hello-ack` fields are never trusted.
 * - Test 3A presents a forged server X.509 certificate while `expected_fingerprint` pins the genuine
 *   device fingerprint -> asserts immediate `PeerProtocolError('fingerprint_mismatch')`.
 * - Test 3B presents a forged client X.509 certificate on `grant` reconnect AND smuggles the victim's
 *   genuine fingerprint inside the JSON `peer.hello` payload -> asserts the server hashes the raw TLS
 *   DER certificate, catches the lie, and rejects with `fingerprint_mismatch`.
 * - If `verifyPinnedFingerprint` or `extractPeerCertFingerprint` were removed or bypassed to return OK
 *   or trust JSON fields, Tests 3A, 3B, and 3C deterministically fail because handshake resolution
 *   succeeds instead of throwing `fingerprint_mismatch`.
 */

import { describe, expect, test } from 'bun:test';
import net from 'node:net';
import { Duplex } from 'node:stream';
import tls from 'node:tls';
import { Elysia } from 'elysia';
import { createWebAuthRuntime } from '../../src/webAuth.js';
import type { WsTerminalClient, WsTerminalSession } from '../../src/wsTerminalProxy.js';
import { PeerClientPool, type PeerDeviceRecord } from '../../src/peers/peerClientPool.js';
import { performClientHandshake, performServerHandshake } from '../../src/peers/peerHandshake.js';
import {
  buildSanitizedPeerCoreHeaders,
  evaluatePeerPolicy,
  type PeerAccessPolicy,
  type PeerControllerContext,
  type PeerPolicyEvent,
} from '../../src/peers/peerPolicyGuard.js';
import {
  encodePeerFrame,
  extractPeerCertFingerprint,
  generateSelfSignedP256TlsMaterial,
  MAX_OUTBOUND_QUEUE_FRAMES,
  MAX_PEER_FRAME_BYTES,
  PeerFrameConnection,
  PeerFrameDecoder,
  PeerProtocolError,
  verifyPinnedFingerprint,
} from '../../src/peers/peerProtocol.js';
import { registerPeerRoutes } from '../../src/peers/peerProxyRoutes.js';
import { PeerTlsServer } from '../../src/peers/peerTlsServer.js';

// ── Test Harness: In-Memory Worker & Controller Core Simulators ──────────────

interface HarnessGrant {
  controllerDeviceId: string;
  displayName: string;
  fingerprint: string;
  policy: PeerAccessPolicy;
}

function createWorkerCoreHarness(options?: {
  inboundEnabled?: boolean;
  listenPort?: number;
  pairingCode?: string;
  localHostControlToken?: string;
}) {
  const workerTls = generateSelfSignedP256TlsMaterial('worker-node-b');
  let inboundEnabled = options?.inboundEnabled ?? false;
  let listenPort = options?.listenPort ?? 0;
  let displayName = 'Worker-B';
  const localDeviceId = 'dev-worker-b';
  let activePairingCode: { code: string; remaining: number; expiresAt: number } | null =
    options?.pairingCode
      ? { code: options.pairingCode, remaining: 5, expiresAt: Date.now() + 300_000 }
      : null;

  const grants = new Map<string, HarnessGrant>();
  const policyStreamControllers = new Set<ReadableStreamDefaultController<Uint8Array>>();
  const activeSseResponseControllers = new Set<ReadableStreamDefaultController<Uint8Array>>();
  const capturedCoreRequests: Array<{
    method: string;
    path: string;
    query: string;
    headers: Record<string, string>;
    bodyText: string;
  }> = [];

  let policySeq = 1;
  const projects = [
    { id: 'proj-alpha', name: 'Alpha Repo', path: '/srv/alpha' },
    { id: 'proj-secret', name: 'Secret Repo', path: '/srv/secret' },
  ];
  const sessions: Array<Record<string, unknown>> = [
    {
      id: 'sess-local-alpha',
      project_id: 'proj-alpha',
      title: 'Local Worker Session',
      run_status: 'idle',
      initiator_device_id: 'local',
    },
    {
      id: 'sess-secret',
      project_id: 'proj-secret',
      title: 'Secret Session',
      run_status: 'idle',
      initiator_device_id: 'dev-ctrl-a',
    },
  ];

  const encoder = new TextEncoder();

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

  function emitWorkerSseEvent(id: number, eventName: string, payload: Record<string, unknown>): void {
    const chunk = encoder.encode(`id: ${id}\nevent: ${eventName}\ndata: ${JSON.stringify(payload)}\n\n`);
    for (const ctrl of activeSseResponseControllers) {
      try {
        ctrl.enqueue(chunk);
      } catch {
        activeSseResponseControllers.delete(ctrl);
      }
    }
  }

  const fetchImpl: typeof fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url);
    const method = (init?.method ?? 'GET').toUpperCase();
    const headersObj: Record<string, string> = {};
    new Headers(init?.headers).forEach((v, k) => {
      headersObj[k.toLowerCase()] = v;
    });

    let bodyText = '';
    if (typeof init?.body === 'string') {
      bodyText = init.body;
    } else if (init?.body instanceof Uint8Array || Buffer.isBuffer(init?.body)) {
      bodyText = Buffer.from(init.body).toString('utf8');
    } else if (init?.body instanceof ReadableStream) {
      const reader = init.body.getReader();
      const parts: Uint8Array[] = [];
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        if (value) parts.push(value);
      }
      bodyText = Buffer.concat(parts.map((p) => Buffer.from(p))).toString('utf8');
    }

    if (!url.pathname.startsWith('/api/v1/peers/internal/')) {
      capturedCoreRequests.push({
        method,
        path: url.pathname,
        query: url.search,
        headers: headersObj,
        bodyText,
      });
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
          { code: 'inbound_disabled', detail: 'Inbound peer connections are disabled.' },
          { status: 403 },
        );
      }
      const mode = String(req.pairing_mode ?? '');
      const ctrlId = String(req.controller_device_id ?? '');
      const ctrlName = String(req.display_name ?? ctrlId);
      const remoteFp = String(req.remote_fingerprint ?? '').toLowerCase();

      if (mode === 'code') {
        if (!activePairingCode) {
          return Response.json({ code: 'pairing_code_expired', detail: 'No active pairing code.' }, { status: 403 });
        }
        if (Date.now() > activePairingCode.expiresAt) {
          activePairingCode = null;
          return Response.json({ code: 'pairing_code_expired', detail: 'Pairing code expired.' }, { status: 403 });
        }
        if (String(req.pairing_code ?? '') !== activePairingCode.code) {
          activePairingCode.remaining -= 1;
          if (activePairingCode.remaining <= 0) activePairingCode = null;
          return Response.json({ code: 'pairing_code_rejected', detail: 'Invalid pairing code.' }, { status: 403 });
        }
        // Single-use pairing code consumed immediately!
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
          return Response.json({ code: 'unauthorized', detail: 'Grant revoked or unknown controller.' }, { status: 401 });
        }
        if (grant.fingerprint !== remoteFp) {
          return Response.json(
            { code: 'fingerprint_mismatch', detail: 'Client TLS certificate does not match pinned grant fingerprint.' },
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

      return Response.json({ code: 'handshake_failed', detail: 'Unsupported pairing mode.' }, { status: 400 });
    }

    if (url.pathname === '/api/v1/peers/inbound') {
      if (method === 'PUT') {
        const patch = JSON.parse(bodyText || '{}') as Record<string, unknown>;
        if (typeof patch.enabled === 'boolean') inboundEnabled = patch.enabled;
        if (typeof patch.display_name === 'string') displayName = patch.display_name;
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
      return Response.json(projects);
    }

    if (url.pathname === '/api/v1/projects' && method === 'POST') {
      const body = JSON.parse(bodyText || '{}') as { name?: string; path?: string };
      const created = { id: `proj-${projects.length + 1}`, name: body.name ?? 'New', path: body.path ?? '/tmp' };
      projects.push(created);
      return Response.json(created, { status: 201 });
    }

    if (url.pathname === '/api/v1/sessions' && method === 'GET') {
      return Response.json(sessions);
    }

    if (url.pathname === '/api/v1/sessions' && method === 'POST') {
      const body = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      const created = {
        id: `sess-${sessions.length + 1}`,
        project_id: typeof body.project_id === 'string' ? body.project_id : 'proj-alpha',
        title: typeof body.title === 'string' ? body.title : 'Peer Session',
        run_status: 'idle',
        initiator_device_id: headersObj['x-tinadec-peer-controller-id'] ?? 'local',
      };
      sessions.push(created);
      return Response.json(created, { status: 201 });
    }

    const msgMatch = /^\/api\/v1\/sessions\/([^/]+)\/messages$/i.exec(url.pathname);
    if (msgMatch && method === 'GET') {
      return Response.json([{ id: 'm1', session_id: msgMatch[1], content: 'hello from worker' }]);
    }
    if (msgMatch && method === 'POST') {
      const body = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      return Response.json(
        {
          id: 'm2',
          session_id: msgMatch[1],
          content: body.content,
          client_request_id: body.client_request_id ?? null,
          controller_id: headersObj['x-tinadec-peer-controller-id'] ?? null,
        },
        { status: 201 },
      );
    }

    const approvalMatch = /^\/api\/v1\/approvals\/([^/]+)\/decision$/i.exec(url.pathname);
    if (approvalMatch && method === 'POST') {
      const body = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      return Response.json({
        approval_id: approvalMatch[1],
        decision: body.decision ?? 'approve',
        decided_by_device_id: headersObj['x-tinadec-peer-controller-id'] ?? 'local',
        decided_by_device_name: headersObj['x-tinadec-peer-controller-name'] ?? 'Local',
      });
    }

    if (url.pathname === '/api/v1/events' && method === 'GET') {
      const afterSeq = url.searchParams.get('after_seq') ?? headersObj['last-event-id'] ?? '0';
      let ctrlRef: ReadableStreamDefaultController<Uint8Array> | null = null;
      const stream = new ReadableStream<Uint8Array>({
        start(controller) {
          ctrlRef = controller;
          activeSseResponseControllers.add(controller);
          controller.enqueue(
            encoder.encode(
              `id: ${Number(afterSeq) + 1}\nevent: session.snapshot\ndata: ${JSON.stringify({ resumed_from: afterSeq })}\n\n`,
            ),
          );
        },
        cancel() {
          if (ctrlRef) activeSseResponseControllers.delete(ctrlRef);
        },
      });
      init?.signal?.addEventListener(
        'abort',
        () => {
          if (ctrlRef) {
            activeSseResponseControllers.delete(ctrlRef);
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

    if (url.pathname.startsWith('/api/v1/tools/') && url.pathname.endsWith('/execute') && method === 'POST') {
      return Response.json({ status: 'completed', data: { ok: true } });
    }

    if (url.pathname === '/api/v1/fs/browse' && method === 'GET') {
      return Response.json({ current_path: '/srv', entries: [] });
    }

    if (url.pathname === '/api/v1/preferences' && (method === 'PUT' || method === 'PATCH')) {
      return Response.json({ updated: true });
    }

    return Response.json({ ok: true, path: url.pathname, query: url.search });
  }) as unknown as typeof fetch;

  return {
    workerTls,
    localDeviceId,
    grants,
    capturedCoreRequests,
    fetchImpl,
    setInboundEnabled(val: boolean) {
      inboundEnabled = val;
    },
    getListenPort() {
      return listenPort;
    },
    setPairingCode(code: string) {
      activePairingCode = { code, remaining: 5, expiresAt: Date.now() + 300_000 };
    },
    updatePolicy(controllerDeviceId: string, patch: Partial<PeerAccessPolicy>) {
      const existing = grants.get(controllerDeviceId);
      if (!existing) throw new Error(`Missing grant for ${controllerDeviceId}`);
      existing.policy = {
        ...existing.policy,
        ...patch,
        revision: `rev-${policySeq++}`,
      };
      emitPolicyEvent({
        type: 'policy_updated',
        controller_device_id: controllerDeviceId,
        revision: existing.policy.revision,
        policy: existing.policy,
      });
    },
    revokeGrant(controllerDeviceId: string) {
      grants.delete(controllerDeviceId);
      emitPolicyEvent({
        type: 'grant_revoked',
        controller_device_id: controllerDeviceId,
      });
    },
    emitWorkerSseEvent,
  };
}

function createControllerCoreHarness() {
  const controllerTls = generateSelfSignedP256TlsMaterial('controller-node-a');
  const devices = new Map<string, PeerDeviceRecord>();
  const mountedWorkspaces = new Map<string, Array<Record<string, unknown>>>();

  const fetchImpl: typeof fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url);
    const method = (init?.method ?? 'GET').toUpperCase();
    const bodyText = typeof init?.body === 'string' ? init.body : '';

    if (url.pathname === '/api/v1/peers/internal/local-tls-material') {
      return Response.json({
        inbound_enabled: false,
        local_device_id: 'dev-ctrl-a',
        display_name: 'Controller-A',
        listen_port: 48735,
        local_fingerprint: controllerTls.fingerprint,
        cert_pem: controllerTls.certPem,
        private_key_pem: controllerTls.privateKeyPem,
      });
    }

    if (url.pathname === '/api/v1/peers/devices/pair' && method === 'POST') {
      const req = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      const id = `peer-${String(req.remote_device_id ?? 'remote')}`;
      const record: PeerDeviceRecord = {
        device_id: id,
        display_name: String(req.display_name ?? 'Worker'),
        transport: 'lan',
        address: String(req.address ?? ''),
        remote_device_id: String(req.remote_device_id ?? ''),
        certificate_fingerprint: String(req.certificate_fingerprint ?? ''),
        protocol_major: Number(req.protocol_major ?? 1),
        protocol_minor: Number(req.protocol_minor ?? 0),
        paired_at: Date.now(),
        last_connected_at: Date.now(),
        connection_desired: Boolean(req.connection_desired ?? true),
        adopt_sessions: Boolean(req.adopt_sessions ?? true),
        last_state: (req.last_state as PeerDeviceRecord['last_state']) ?? 'ready',
        last_failure_code: null,
        access_revision: typeof req.access_revision === 'string' ? req.access_revision : 'rev-1',
      };
      devices.set(id, record);
      return Response.json(record, { status: 201 });
    }

    if (url.pathname === '/api/v1/peers/devices' && method === 'GET') {
      return Response.json({ devices: [...devices.values()] });
    }

    if (url.pathname === '/api/v1/peers/internal/controller-state' && method === 'POST') {
      const req = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      const existing = devices.get(String(req.device_id ?? ''));
      if (existing) {
        existing.last_state = (req.last_state as PeerDeviceRecord['last_state']) ?? existing.last_state;
        existing.last_failure_code =
          typeof req.last_failure_code === 'string' ? req.last_failure_code : null;
        if (typeof req.last_connected_at === 'number') existing.last_connected_at = req.last_connected_at;
        if (typeof req.access_revision === 'string') existing.access_revision = req.access_revision;
      }
      return Response.json({ ok: true });
    }

    const wsMatch = /^\/api\/v1\/peers\/devices\/([^/]+)\/workspaces$/i.exec(url.pathname);
    if (wsMatch && method === 'GET') {
      return Response.json({ workspaces: mountedWorkspaces.get(wsMatch[1]!) ?? [] });
    }

    return Response.json({ ok: true });
  }) as unknown as typeof fetch;

  return {
    controllerTls,
    devices,
    mountedWorkspaces,
    fetchImpl,
  };
}

// ── Test Suite ───────────────────────────────────────────────────────────────

describe('Peer mTLS Transport, Handshake, PolicyGuard & Multi-Endpoint Proxy', () => {
  test('1. Default inbound disabled: never binds port when inbound_enabled === false; rejects unauthorized handshake with inbound_disabled', async () => {
    const workerCore = createWorkerCoreHarness({ inboundEnabled: false, listenPort: 0 });
    const server = new PeerTlsServer({
      coreUrl: () => 'http://worker-core.local',
      fetchImpl: workerCore.fetchImpl,
      bindHost: '127.0.0.1',
      listenPortOverride: 0,
    });

    try {
      const status = await server.start();
      expect(status.enabled).toBe(false);
      expect(status.listening).toBe(false);
      expect(status.actualListenPort).toBeNull();
      expect(server.boundPort).toBeNull();

      // Enable inbound to bind port, then flip Core inbound switch OFF before handshake verify:
      // handshake must deterministically return `inbound_disabled`.
      workerCore.setInboundEnabled(true);
      const liveStatus = await server.syncFromCore();
      expect(liveStatus.listening).toBe(true);
      expect(typeof liveStatus.actualListenPort).toBe('number');

      workerCore.setInboundEnabled(false);
      const ctrlCore = createControllerCoreHarness();
      const pool = new PeerClientPool({
        coreUrl: () => 'http://ctrl-core.local',
        fetchImpl: ctrlCore.fetchImpl,
      });

      try {
        await expect(
          pool.pairWithPeer({
            address: `127.0.0.1:${liveStatus.actualListenPort}`,
            pairing_code: '123456',
            expected_fingerprint: workerCore.workerTls.fingerprint,
          }),
        ).rejects.toMatchObject({
          code: 'inbound_disabled',
        });
      } finally {
        await pool.stop();
      }
    } finally {
      await server.stop();
    }
  });

  test('2. Port conflict auto-probing: increments port on EADDRINUSE and notifies Core of selected listen_port', async () => {
    const blocker = net.createServer();
    await new Promise<void>((resolve) => blocker.listen(0, '127.0.0.1', () => resolve()));
    const occupiedPort = (blocker.address() as net.AddressInfo).port;

    const workerCore = createWorkerCoreHarness({
      inboundEnabled: true,
      listenPort: occupiedPort,
    });
    const server = new PeerTlsServer({
      coreUrl: () => 'http://worker-core.local',
      fetchImpl: workerCore.fetchImpl,
      bindHost: '127.0.0.1',
      listenPortOverride: occupiedPort,
      maxProbePortOffset: 10,
    });

    try {
      const status = await server.start();
      expect(status.listening).toBe(true);
      expect(status.portConflict).toBe(true);
      expect(status.actualListenPort).toBeGreaterThan(occupiedPort);
      expect(status.actualListenPort).toBeLessThanOrEqual(occupiedPort + 10);
      // Core was notified of the newly selected port.
      expect(workerCore.getListenPort()).toBe(status.actualListenPort!);
    } finally {
      await server.stop();
      await new Promise<void>((resolve) => blocker.close(() => resolve()));
    }
  });

  test('3. SHA-256 DER certificate pinning: forged server cert, forged client cert + spoofed hello, and pinning primitive mutation guard', async () => {
    // 3C: Direct unit verification of verifyPinnedFingerprint & extractPeerCertFingerprint
    const genuineMat = generateSelfSignedP256TlsMaterial('genuine-peer');
    const forgedMat = generateSelfSignedP256TlsMaterial('forged-attacker');
    expect(genuineMat.fingerprint).toMatch(/^[0-9a-f]{64}$/);
    expect(forgedMat.fingerprint).toMatch(/^[0-9a-f]{64}$/);
    expect(genuineMat.fingerprint).not.toBe(forgedMat.fingerprint);

    expect(() => verifyPinnedFingerprint(genuineMat.fingerprint, genuineMat.fingerprint)).not.toThrow();
    expect(() => verifyPinnedFingerprint(forgedMat.fingerprint, genuineMat.fingerprint)).toThrow(
      PeerProtocolError,
    );

    // Real mTLS server with Worker-B certificate
    const workerCore = createWorkerCoreHarness({
      inboundEnabled: true,
      pairingCode: '884422',
    });
    const server = new PeerTlsServer({
      coreUrl: () => 'http://worker-core.local',
      fetchImpl: workerCore.fetchImpl,
      bindHost: '127.0.0.1',
      listenPortOverride: 0,
    });

    const ctrlCore = createControllerCoreHarness();
    const pool = new PeerClientPool({
      coreUrl: () => 'http://ctrl-core.local',
      fetchImpl: ctrlCore.fetchImpl,
    });

    try {
      const status = await server.start();
      const addr = `127.0.0.1:${status.actualListenPort}`;

      // 3A: Forged server certificate vs pinned expected_fingerprint -> `fingerprint_mismatch`
      // (Simulates MITM presenting Worker-B's cert when Controller expects `forgedMat.fingerprint`,
      // AND pairing code is NOT consumed!)
      await expect(
        pool.pairWithPeer({
          address: addr,
          pairing_code: '884422',
          expected_fingerprint: forgedMat.fingerprint,
        }),
      ).rejects.toMatchObject({
        code: 'fingerprint_mismatch',
      });

      // Genuine pair now succeeds because pairing_code was preserved!
      const paired = await pool.pairWithPeer({
        address: addr,
        pairing_code: '884422',
        expected_fingerprint: workerCore.workerTls.fingerprint,
      });
      expect(paired.certificate_fingerprint).toBe(workerCore.workerTls.fingerprint);
      expect(paired.last_state).toBe('ready');

      // Re-using the single-use pairing code must fail with `pairing_code_expired`!
      await expect(
        pool.pairWithPeer({
          address: addr,
          pairing_code: '884422',
          expected_fingerprint: workerCore.workerTls.fingerprint,
        }),
      ).rejects.toMatchObject({
        code: 'pairing_code_expired',
      });

      // 3B: Forged client certificate attempting `grant` reconnect as `dev-ctrl-a` while
      // smuggling `certificateFingerprint: ctrlCore.controllerTls.fingerprint` inside JSON `peer.hello`!
      const rogueSocket = await new Promise<tls.TLSSocket>((resolve, reject) => {
        const s = tls.connect(
          {
            host: '127.0.0.1',
            port: status.actualListenPort!,
            cert: forgedMat.certPem,
            key: forgedMat.privateKeyPem,
            rejectUnauthorized: false,
            ALPNProtocols: ['tinadec-peer-http/1'],
          },
          () => resolve(s),
        );
        s.once('error', reject);
      });

      // Confirm TLS layer reports ALPN and real DER SHA-256 of server
      expect(rogueSocket.alpnProtocol).toBe('tinadec-peer-http/1');
      expect(extractPeerCertFingerprint(rogueSocket)).toBe(workerCore.workerTls.fingerprint);

      const rogueConn = new PeerFrameConnection(rogueSocket, { heartbeatIntervalMs: 0 });
      rogueConn.sendFrame({
        type: 'peer.hello',
        protocolMajor: 1,
        protocolMinor: 0,
        deviceId: 'dev-ctrl-a',
        deviceName: 'Spoofed Controller-A',
        pairingMode: 'grant',
        capabilities: ['proxy_v1'],
        expectedRemoteFingerprint: workerCore.workerTls.fingerprint,
        // Smuggled spoofed self-reported fingerprint in JSON:
        ...({ certificateFingerprint: ctrlCore.controllerTls.fingerprint } as Record<string, unknown>),
      });

      const reply = await rogueConn.nextFrame(3_000);
      expect(reply.header.type).toBe('peer.hello-reject');
      if (reply.header.type === 'peer.hello-reject') {
        expect(reply.header.code).toBe('fingerprint_mismatch');
      }
      rogueConn.destroy();
    } finally {
      await pool.stop();
      await server.stop();
    }
  });

  test('4. Wire framing limits: 32 MB max frame guard, 256-frame outbound backpressure ceiling, and 15s/45s heartbeat timeout', async () => {
    // 4A: 4-byte BE JSON + binary envelope round-trip
    const decoder = new PeerFrameDecoder();
    const binPayload = new Uint8Array([1, 2, 3, 254, 255]);
    const encodedJson = encodePeerFrame({ type: 'peer.ping', ts: 12345 });
    const encodedBin = encodePeerFrame({ type: 'http.res.chunk', streamId: 's1' }, binPayload);

    // Feed fragmented chunks across frame boundaries
    const combined = Buffer.concat([encodedJson, encodedBin]);
    const f1 = decoder.push(combined.subarray(0, 3));
    expect(f1).toHaveLength(0);
    const f2 = decoder.push(combined.subarray(3));
    expect(f2).toHaveLength(2);
    expect(f2[0]!.header).toEqual({ type: 'peer.ping', ts: 12345 });
    expect(f2[1]!.header).toEqual({ type: 'http.res.chunk', streamId: 's1' });
    expect(Array.from(f2[1]!.payload ?? [])).toEqual([1, 2, 3, 254, 255]);

    // 4B: > 32 MB header rejected immediately before buffering
    const oversizedHeader = Buffer.alloc(8);
    oversizedHeader.writeUInt32BE(MAX_PEER_FRAME_BYTES + 1, 0);
    expect(() => decoder.push(oversizedHeader)).toThrow(/violates bounds/);

    // 4C: 256-frame outbound backpressure queue ceiling
    const stalledDuplex = new Duplex({
      read() {},
      write(_chunk, _encoding, _callback) {
        // Never invoke callback -> socket.write() returns false after highWaterMark (1 byte)
      },
      writableHighWaterMark: 1,
    });
    const bpConn = new PeerFrameConnection(stalledDuplex, {
      maxOutboundQueueFrames: MAX_OUTBOUND_QUEUE_FRAMES,
      heartbeatIntervalMs: 0,
    });

    // First write triggers `draining = true`; next 256 frames fill queue; 257th throws `timeout`!
    bpConn.sendFrame({ type: 'peer.ping', ts: 0 });
    for (let i = 0; i < MAX_OUTBOUND_QUEUE_FRAMES; i++) {
      bpConn.sendFrame({ type: 'peer.ping', ts: i + 1 });
    }
    expect(bpConn.queuedOutboundFrames).toBe(MAX_OUTBOUND_QUEUE_FRAMES);
    expect(() => bpConn.sendFrame({ type: 'peer.ping', ts: 999 })).toThrow(PeerProtocolError);
    bpConn.destroy();

    // 4D: Dead peer timeout when remote stops responding to pings
    const silentDuplex = new Duplex({
      read() {},
      write(_chunk, _encoding, cb) {
        cb();
      },
    });
    const hbConn = new PeerFrameConnection(silentDuplex, {
      heartbeatIntervalMs: 30,
      heartbeatTimeoutMs: 90,
    });
    const closePromise = new Promise<Error | undefined>((resolve) => {
      hbConn.onClose((err) => resolve(err));
    });
    hbConn.startSteadyState(() => {});
    const closeErr = await closePromise;
    expect(closeErr).toBeInstanceOf(PeerProtocolError);
    expect((closeErr as PeerProtocolError).code).toBe('timeout');
  });

  test('5. PeerPolicyGuard role matrix, selected workspace whitelist, sync_all_sessions=0, and forged header stripping', () => {
    const makeCtx = (overrides: Partial<PeerAccessPolicy>): PeerControllerContext => ({
      controllerDeviceId: 'ctrl-1',
      displayName: 'Dev Laptop',
      clientFingerprint: 'a'.repeat(64),
      policy: {
        controller_device_id: 'ctrl-1',
        role: 'operator',
        workspace_mode: 'all',
        workspace_ids: [],
        allow_non_workspace_sessions: 1,
        sync_all_sessions: 1,
        allow_agent_delegation: 0,
        allow_provider_proxy: 0,
        revision: 'rev-1',
        ...overrides,
      },
    });

    const ownerCtx = makeCtx({ role: 'owner' });
    const operatorCtx = makeCtx({ role: 'operator' });
    const observerCtx = makeCtx({ role: 'observer' });

    // 5A: Even `owner` is strictly forbidden from accessing host security & peer admin endpoints!
    const forbiddenPaths = [
      '/api/v1/peers/inbound',
      '/api/v1/peers/pairing-code',
      '/api/v1/peers/grants',
      '/api/v1/peers/grants/ctrl-1/policy',
      '/api/v1/peers/devices',
      '/api/v1/peers/devices/pair',
      '/api/v1/peers/internal/local-tls-material',
      '/api/v1/web-auth/settings',
      '/api/v1/host-challenge',
      '/api/v1/mcp/tinadec',
      '/api/v1/peers/other-dev/proxy/api/v1/projects',
    ];
    for (const path of forbiddenPaths) {
      const res = evaluatePeerPolicy({ kind: 'http', method: 'GET', path, controller: ownerCtx });
      expect(res).toMatchObject({ allowed: false, status: 403, code: 'policy_denied' });
    }

    // 5B: `observer` allowed reads vs forbidden writes/approvals/terminals
    expect(evaluatePeerPolicy({ kind: 'http', method: 'GET', path: '/api/v1/projects', controller: observerCtx }).allowed).toBe(true);
    expect(evaluatePeerPolicy({ kind: 'http', method: 'GET', path: '/api/v1/sessions/s1/messages', controller: observerCtx }).allowed).toBe(true);
    expect(evaluatePeerPolicy({ kind: 'http', method: 'GET', path: '/api/v1/events', query: 'session_id=s1', controller: observerCtx }).allowed).toBe(true);
    expect(
      evaluatePeerPolicy({
        kind: 'http',
        method: 'POST',
        path: '/api/v1/tools/git_worktree_manager/execute',
        bodyJson: { arguments: { action: 'status' } },
        controller: observerCtx,
      }).allowed,
    ).toBe(true);

    const observerBlocked: Array<{ kind: 'http' | 'ws'; method: string; path: string; bodyJson?: Record<string, unknown> }> = [
      { kind: 'http', method: 'POST', path: '/api/v1/sessions' },
      { kind: 'http', method: 'POST', path: '/api/v1/sessions/s1/messages' },
      { kind: 'http', method: 'POST', path: '/api/v1/sessions/s1/interactions' },
      { kind: 'http', method: 'POST', path: '/api/v1/runs/r1/control' },
      { kind: 'http', method: 'POST', path: '/api/v1/approvals/a1/decision' },
      { kind: 'http', method: 'POST', path: '/api/v1/approvals/batch-decision' },
      { kind: 'http', method: 'POST', path: '/api/v1/tools/write_file/execute', bodyJson: {} },
      { kind: 'http', method: 'POST', path: '/api/v1/tools/git_commit/execute', bodyJson: {} },
      { kind: 'ws', method: 'GET', path: '/ws/terminal' },
    ];
    for (const item of observerBlocked) {
      const res = evaluatePeerPolicy({ ...item, controller: observerCtx });
      expect(res).toMatchObject({ allowed: false, status: 403, code: 'policy_denied' });
    }

    // 5C: `operator` vs `owner` capabilities
    expect(evaluatePeerPolicy({ kind: 'http', method: 'POST', path: '/api/v1/sessions/s1/messages', controller: operatorCtx }).allowed).toBe(true);
    expect(evaluatePeerPolicy({ kind: 'http', method: 'POST', path: '/api/v1/approvals/a1/decision', controller: operatorCtx }).allowed).toBe(true);
    expect(evaluatePeerPolicy({ kind: 'ws', method: 'GET', path: '/ws/terminal', controller: operatorCtx }).allowed).toBe(true);

    const ownerOnlyChecks: Array<{ method: string; path: string }> = [
      { method: 'GET', path: '/api/v1/fs/browse' },
      { method: 'GET', path: '/api/v1/filesystem/roots' },
      { method: 'POST', path: '/api/v1/projects' },
      { method: 'DELETE', path: '/api/v1/projects/proj-alpha' },
      { method: 'POST', path: '/api/v1/storage/scopes/open' },
      { method: 'DELETE', path: '/api/v1/storage/scopes/scope-1' },
      { method: 'PUT', path: '/api/v1/preferences' },
      { method: 'PATCH', path: '/api/v1/preferences' },
    ];
    for (const check of ownerOnlyChecks) {
      expect(evaluatePeerPolicy({ kind: 'http', ...check, controller: operatorCtx })).toMatchObject({
        allowed: false,
        status: 403,
        code: 'policy_denied',
      });
      expect(evaluatePeerPolicy({ kind: 'http', ...check, controller: ownerCtx }).allowed).toBe(true);
    }

    // 5D: `workspace_mode === 'selected'` & `sync_all_sessions === 0`
    const scopedOperator = makeCtx({
      role: 'operator',
      workspace_mode: 'selected',
      workspace_ids_json: JSON.stringify(['proj-alpha']),
      sync_all_sessions: 0,
    });
    expect(
      evaluatePeerPolicy({
        kind: 'http',
        method: 'GET',
        path: '/api/v1/projects/proj-secret/overview',
        controller: scopedOperator,
      }),
    ).toMatchObject({ allowed: false, status: 403, code: 'policy_denied' });

    expect(
      evaluatePeerPolicy({
        kind: 'http',
        method: 'GET',
        path: '/api/v1/sessions/sess-secret/messages',
        controller: scopedOperator,
        sessionMeta: { sessionId: 'sess-secret', projectId: 'proj-secret', initiatorDeviceId: 'ctrl-1' },
      }),
    ).toMatchObject({ allowed: false, status: 403, code: 'policy_denied' });

    expect(
      evaluatePeerPolicy({
        kind: 'http',
        method: 'GET',
        path: '/api/v1/sessions/sess-local/messages',
        controller: scopedOperator,
        sessionMeta: { sessionId: 'sess-local', projectId: 'proj-alpha', initiatorDeviceId: 'local' },
      }),
    ).toMatchObject({ allowed: false, status: 403, code: 'policy_denied' });

    expect(
      evaluatePeerPolicy({
        kind: 'http',
        method: 'GET',
        path: '/api/v1/sessions/sess-mine/messages',
        controller: scopedOperator,
        sessionMeta: { sessionId: 'sess-mine', projectId: 'proj-alpha', initiatorDeviceId: 'ctrl-1' },
      }).allowed,
    ).toBe(true);

    // 5E: Strip forged privileged headers and inject authentic audit headers + Worker host token
    const sanitized = buildSanitizedPeerCoreHeaders(
      {
        'X-Tinadec-Host-Control': 'FORGED_ATTACKER_TOKEN',
        'X-Tinadec-Peer-Role': 'owner',
        'X-Tinadec-Peer-Controller-Id': 'spoofed-id',
        Cookie: 'tinadec_session=stolen',
        'X-Tinadec-Storage-Id': 'scope-alpha',
        'Last-Event-ID': '42',
      },
      operatorCtx,
      'WORKER_OWN_TRUSTED_HOST_TOKEN',
    );
    expect(sanitized['x-tinadec-host-control']).toBe('WORKER_OWN_TRUSTED_HOST_TOKEN');
    expect(sanitized['x-tinadec-peer-controller-id']).toBe('ctrl-1');
    expect(sanitized['x-tinadec-peer-controller-name']).toBe('Dev Laptop');
    expect(sanitized['x-tinadec-peer-role']).toBe('operator');
    expect(sanitized['x-tinadec-storage-id']).toBe('scope-alpha');
    expect(sanitized['last-event-id']).toBe('42');
    expect(sanitized.cookie).toBeUndefined();
  });

  test('6. End-to-end transparent proxy (probe -> pair -> HTTP/JSON -> SSE after_seq -> WS terminal) & <100ms policy revocation kick', async () => {
    const workerCore = createWorkerCoreHarness({
      inboundEnabled: true,
      pairingCode: '776655',
    });

    // Deterministic fake interactive shell on Worker so WS terminal test is fast & OS-independent
    const createFakeWorkerShell = ({ ws }: { ws: WsTerminalClient }): WsTerminalSession => {
      ws.send(JSON.stringify({ type: 'ready', mode: 'interactive', shell: 'mock-sh', cwd: '/srv/alpha' }));
      return {
        onMessage(raw) {
          const text = typeof raw === 'string' ? raw : Buffer.from(raw).toString('utf8');
          const parsed = JSON.parse(text) as { type?: string; data?: string };
          if (parsed.type === 'input') {
            ws.send(JSON.stringify({ type: 'data', data: `ECHO:${parsed.data}` }));
          }
        },
        close() {
          ws.send(JSON.stringify({ type: 'exit', code: 0 }));
        },
      };
    };

    const workerTlsServer = new PeerTlsServer({
      coreUrl: () => 'http://worker-core.local',
      fetchImpl: workerCore.fetchImpl,
      hostControlToken: () => 'worker-secret-host-token',
      bindHost: '127.0.0.1',
      listenPortOverride: 0,
      createInteractiveShell: createFakeWorkerShell as never,
    });

    const ctrlCore = createControllerCoreHarness();
    const ctrlPool = new PeerClientPool({
      coreUrl: () => 'http://ctrl-core.local',
      fetchImpl: ctrlCore.fetchImpl,
      reconnectBaseDelayMs: 5_000,
    });

    // Spin up a real Elysia Controller Gateway to exercise HTTP, SSE, and real WebSocket `/ws/peers/:id/terminal`!
    const webAuthRuntime = createWebAuthRuntime();
    const ctrlApp = new Elysia();
    registerPeerRoutes(ctrlApp, {
      peerTlsServer: workerTlsServer, // unused for outbound controller calls
      peerClientPool: ctrlPool,
      forwardHeaders: () => ({ 'x-request-id': 'req-test', 'x-tinadec-principal': 'dev@local' }),
      keepStreamOpen: () => {},
      webAuthRuntime,
      mode: 'local',
      trustProxy: 'false',
      proxyCoreJson: async (path, opts) => {
        const res = await ctrlCore.fetchImpl(`http://ctrl-core.local${path}`, {
          method: opts?.method ?? 'GET',
          headers: opts?.headers,
          body:
            typeof opts?.body === 'string'
              ? opts.body
              : opts?.body
                ? JSON.stringify(opts.body)
                : undefined,
          signal: opts?.signal,
        });
        const data = await res.json().catch(() => null);
        return { status: res.status, data, headers: res.headers };
      },
    });
    ctrlApp.listen({ port: 0, hostname: '127.0.0.1' });
    const gwPort = ctrlApp.server!.port!;
    const gwBase = `http://127.0.0.1:${gwPort}`;

    try {
      const workerStatus = await workerTlsServer.start();
      const workerAddress = `127.0.0.1:${workerStatus.actualListenPort}`;

      // Step 1: Probe via management API (does NOT consume pairing code!)
      const probeRes = await fetch(`${gwBase}/api/v1/peers/devices/probe`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ address: workerAddress }),
      });
      expect(probeRes.status).toBe(200);
      const probeJson = (await probeRes.json()) as Record<string, unknown>;
      expect(probeJson.reachable).toBe(true);
      expect(probeJson.certificate_fingerprint).toBe(workerCore.workerTls.fingerprint);
      expect(probeJson.remote_device_id).toBe('dev-worker-b');

      // Step 2: Pair via management API using probed fingerprint + pairing code
      const pairRes = await fetch(`${gwBase}/api/v1/peers/devices/pair`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({
          address: workerAddress,
          pairing_code: '776655',
          expected_fingerprint: String(probeJson.certificate_fingerprint),
          display_name: 'Office Worker B',
        }),
      });
      expect(pairRes.status).toBe(201);
      const pairedDevice = (await pairRes.json()) as PeerDeviceRecord;
      const deviceId = pairedDevice.device_id;
      expect(pairedDevice.last_state).toBe('ready');

      // Step 3: Fetch remote workspaces via management route
      const wsRes = await fetch(`${gwBase}/api/v1/peers/devices/${deviceId}/workspaces`);
      expect(wsRes.status).toBe(200);
      const wsJson = (await wsRes.json()) as { workspaces: Array<{ remote_project_id: string }> };
      expect(wsJson.workspaces.map((w) => w.remote_project_id)).toEqual(['proj-alpha', 'proj-secret']);

      // Step 4: Transparent HTTP POST proxy + verify forged header stripped & audit headers injected!
      const msgRes = await fetch(
        `${gwBase}/api/v1/peers/${deviceId}/proxy/api/v1/sessions/sess-local-alpha/messages?trace=1`,
        {
          method: 'POST',
          headers: {
            'content-type': 'application/json',
            'x-tinadec-host-control': 'evil-client-header',
          },
          body: JSON.stringify({ content: 'cross-device prompt', client_request_id: 'req-uuid-1' }),
        },
      );
      expect(msgRes.status).toBe(201);
      const msgBody = (await msgRes.json()) as Record<string, unknown>;
      expect(msgBody.content).toBe('cross-device prompt');
      expect(msgBody.controller_id).toBe('dev-ctrl-a');

      const lastCoreReq = workerCore.capturedCoreRequests.at(-1)!;
      expect(lastCoreReq.query).toBe('?trace=1');
      expect(lastCoreReq.headers['x-tinadec-host-control']).toBe('worker-secret-host-token');
      expect(lastCoreReq.headers['x-tinadec-peer-controller-id']).toBe('dev-ctrl-a');
      expect(lastCoreReq.headers['x-tinadec-peer-controller-name']).toBe('Controller-A');
      expect(lastCoreReq.headers['x-tinadec-peer-role']).toBe('operator');

      // Step 5: Remote peer attempting to call Worker's `/api/v1/peers/inbound` over proxy -> 403 policy_denied
      const selfSecRes = await fetch(`${gwBase}/api/v1/peers/${deviceId}/proxy/api/v1/peers/inbound`);
      expect(selfSecRes.status).toBe(403);
      expect(((await selfSecRes.json()) as { code: string }).code).toBe('policy_denied');

      // Step 6: Transparent WebSocket terminal bridge `/ws/peers/:deviceId/terminal`
      const wsFrames: Array<Record<string, unknown>> = [];
      const wsClosedPromise = new Promise<{ code: number }>((resolve) => {
        const termWs = new WebSocket(`ws://127.0.0.1:${gwPort}/ws/peers/${deviceId}/terminal?shell=pwsh`);
        termWs.onmessage = (ev) => {
          const msg = JSON.parse(String(ev.data)) as Record<string, unknown>;
          wsFrames.push(msg);
          if (msg.type === 'ready') {
            termWs.send(JSON.stringify({ type: 'input', data: 'whoami' }));
          }
        };
        termWs.onclose = (ev) => resolve({ code: ev.code });
      });

      // Wait until terminal echoed back over the mTLS tunnel
      const echoDeadline = Date.now() + 3_000;
      while (Date.now() < echoDeadline && !wsFrames.some((f) => f.type === 'data')) {
        await new Promise((r) => setTimeout(r, 10));
      }
      expect(wsFrames).toContainEqual({
        type: 'ready',
        mode: 'interactive',
        shell: 'mock-sh',
        cwd: '/srv/alpha',
      });
      expect(wsFrames).toContainEqual({
        type: 'data',
        data: 'ECHO:whoami',
      });

      // Step 7: Open live SSE stream with `Last-Event-ID` / `after_seq`, read live events,
      // then trigger Core policy revocation via `/api/v1/peers/internal/policy-events` SSE and
      // verify < 100ms hard teardown of BOTH the SSE stream AND the WS terminal AND the mTLS socket!
      const sseRes = await fetch(
        `${gwBase}/api/v1/peers/${deviceId}/proxy/api/v1/events?after_seq=10`,
        {
          headers: { accept: 'text/event-stream', 'last-event-id': '10' },
        },
      );
      expect(sseRes.status).toBe(200);
      expect(sseRes.headers.get('content-type')).toContain('text/event-stream');

      const sseReader = sseRes.body!.getReader();
      const decoder = new TextDecoder();
      const firstChunk = await sseReader.read();
      expect(firstChunk.done).toBe(false);
      expect(decoder.decode(firstChunk.value)).toContain('"resumed_from":"10"');

      // Push another live SSE event from Worker Core across mTLS tunnel
      workerCore.emitWorkerSseEvent(12, 'approval.requested', { approval_id: 'app-99' });
      const secondChunk = await sseReader.read();
      expect(decoder.decode(secondChunk.value)).toContain('app-99');

      // Now revoke grant on Worker Core -> broadcasts over `/api/v1/peers/internal/policy-events` SSE!
      const kickStart = performance.now();
      workerCore.revokeGrant('dev-ctrl-a');

      // Both the SSE stream and WebSocket terminal MUST terminate in < 100ms!
      await Promise.all([
        sseReader.read().catch(() => ({ done: true, value: undefined })),
        wsClosedPromise,
      ]);
      const kickElapsedMs = performance.now() - kickStart;
      expect(kickElapsedMs).toBeLessThan(100);
      expect(workerTlsServer.activeSessionCount).toBe(0);
    } finally {
      try {
        ctrlApp.server?.stop(true);
      } catch {
        // ignore
      }
      await ctrlPool.stop();
      await workerTlsServer.stop();
    }
  });

  test('7. Full Gateway app route precedence: GET/HEAD /api/v1/peers/:deviceId/proxy/* is never shadowed by webServerPlugin /* catch-all', async () => {
    const { app } = await import('../../src/index.js');
    const origFetch = globalThis.fetch;
    globalThis.fetch = (async (input: RequestInfo | URL) => {
      const u = new URL(typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url);
      if (u.pathname === '/api/v1/peers/devices') {
        return Response.json({ devices: [] });
      }
      return Response.json({});
    }) as unknown as typeof fetch;

    try {
      const getProjectsRes = await app.handle(
        new Request('http://127.0.0.1/api/v1/peers/nonexistent/proxy/api/v1/projects'),
      );
      expect(getProjectsRes.status).toBe(503);
      const getProjectsBody = (await getProjectsRes.json()) as { code: string };
      expect(getProjectsBody.code).toBe('device_offline');

      const getEventsRes = await app.handle(
        new Request('http://127.0.0.1/api/v1/peers/nonexistent/proxy/api/v1/events?after_seq=0', {
          headers: { accept: 'text/event-stream' },
        }),
      );
      expect(getEventsRes.status).toBe(503);
      expect(((await getEventsRes.json()) as { code: string }).code).toBe('device_offline');

      const headProjectsRes = await app.handle(
        new Request('http://127.0.0.1/api/v1/peers/nonexistent/proxy/api/v1/projects', {
          method: 'HEAD',
        }),
      );
      expect(headProjectsRes.status).toBe(503);
    } finally {
      globalThis.fetch = origFetch;
    }
  });
});
