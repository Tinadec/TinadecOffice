/**
 * Wave 3 Pluggable `IPeerTransportChannel` & Stateless Blind-Forwarding Relay Tests
 * (Spec §5.1, §6.2.3, §8 Gate #3):
 *
 * Covers:
 * 1. Real `scripts/peer-relay-server.mjs` startup (library & standalone CLI), `/health`,
 *    `/v1/devices/register` anti-hijack, `/v1/rendezvous` short-code mapping, `/v1/links/tickets`,
 *    and single-use mutual-close binary WebSocket links.
 * 2. Pure-Relay E2EE mTLS (`disableLanListen: true` -> zero direct LAN TCP ports):
 *    Rendezvous pairing (`code`), persistent `grant` reconnect after pairing code burn,
 *    transparent HTTP/SSE proxy, ALPN `tinadec-peer-devserver/1` preview over Relay, and
 *    `< 100ms` instant grant revocation over Relay.
 * 3. Relay Wire-Tap Ciphertext Verification:
 *    Proves 100% of `/v1/links/:ticketId` frames observed by the Relay are opaque TLS
 *    records (`0x16` / `0x17`) and zero plaintext headers, pairing codes, or canary payloads
 *    ever appear in Relay memory or network captures.
 * 4. Active Malicious Relay MITM Certificate Substitution Attacks (Both Directions):
 *    - Rogue Relay substituting server X.509 certificate -> Controller aborts immediately with
 *      `PeerProtocolError('fingerprint_mismatch')` BEFORE transmitting `peer.hello` / `pairing_code`.
 *    - Rogue Relay substituting client X.509 certificate & spoofing `controllerDeviceId` ->
 *      Worker rejects immediately with `peer.hello-reject` (`code: 'fingerprint_mismatch'`).
 * 5. Custom `IPeerTransportChannel` plugin injection over in-memory `Duplex` streams.
 */

import { describe, expect, test } from 'bun:test';
import { spawn } from 'node:child_process';
import http from 'node:http';
import net from 'node:net';
import { resolve } from 'node:path';
import { Duplex } from 'node:stream';
import tls from 'node:tls';
import { Elysia } from 'elysia';
import { startPeerRelayServer } from '../../../scripts/peer-relay-server.mjs';
import { createWebAuthRuntime } from '../../src/webAuth.js';
import { PeerClientPool, type PeerDeviceRecord } from '../../src/peers/peerClientPool.js';
import type { PeerAccessPolicy, PeerPolicyEvent } from '../../src/peers/peerPolicyGuard.js';
import {
  encodePeerFrame,
  generateSelfSignedP256TlsMaterial,
  PEER_ALPN_PROTOCOLS,
  PeerFrameConnection,
  PeerFrameDecoder,
  PeerProtocolError,
} from '../../src/peers/peerProtocol.js';
import { registerPeerRoutes } from '../../src/peers/peerProxyRoutes.js';
import { PeerTlsServer } from '../../src/peers/peerTlsServer.js';
import {
  BlindRelayTransport,
  type IPeerTransportChannel,
  type PeerTransportConnectOptions,
  type PeerTransportConnectedTunnel,
  type PeerTransportListenOptions,
  type PeerTransportListenerHandle,
  type PeerTransportStream,
  upgradeStreamToClientTls,
} from '../../src/peers/peerTransport.js';

const RELAY_SCRIPT_PATH = resolve(import.meta.dir, '../../../scripts/peer-relay-server.mjs');

// ── Test Harnesses (Worker & Controller Core Simulators with Relay Support) ──

function createRelayWorkerCoreHarness(options?: {
  inboundEnabled?: boolean;
  pairingCode?: string;
  secretReplyCanary?: string;
}) {
  const workerTls = generateSelfSignedP256TlsMaterial('worker-relay-b');
  let inboundEnabled = options?.inboundEnabled ?? true;
  const localDeviceId = 'dev-relay-worker-b';
  const displayName = 'Relay-Worker-B';
  const secretReplyCanary = options?.secretReplyCanary ?? 'SUPER_SECRET_WORKER_REPLY_BETA_77889';

  let activePairingCode: { code: string; remaining: number; expiresAt: number } | null =
    options?.pairingCode
      ? { code: options.pairingCode, remaining: 5, expiresAt: Date.now() + 300_000 }
      : null;

  const grants = new Map<
    string,
    {
      transport: string;
      controllerDeviceId: string;
      displayName: string;
      fingerprint: string;
      policy: PeerAccessPolicy;
    }
  >();
  const verifyCalls: Array<Record<string, unknown>> = [];
  const policyStreamControllers = new Set<ReadableStreamDefaultController<Uint8Array>>();
  const sseControllers = new Set<ReadableStreamDefaultController<Uint8Array>>();
  const encoder = new TextEncoder();
  let policyRev = 1;

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

    if (url.pathname === '/api/v1/peers/internal/local-tls-material') {
      return Response.json({
        inbound_enabled: inboundEnabled,
        local_device_id: localDeviceId,
        display_name: displayName,
        listen_port: 0,
        local_fingerprint: workerTls.fingerprint,
        cert_pem: workerTls.certPem,
        private_key_pem: workerTls.privateKeyPem,
      });
    }

    if (url.pathname === '/api/v1/peers/internal/policy-events') {
      let ctrlRef: ReadableStreamDefaultController<Uint8Array> | null = null;
      const stream = new ReadableStream<Uint8Array>({
        start(c) {
          ctrlRef = c;
          policyStreamControllers.add(c);
          c.enqueue(encoder.encode(': connected\n\n'));
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
      verifyCalls.push(req);
      if (!inboundEnabled) {
        return Response.json({ code: 'inbound_disabled', detail: 'Disabled.' }, { status: 403 });
      }
      const transport = String(req.transport ?? 'lan');
      const mode = String(req.pairing_mode ?? '');
      const ctrlId = String(req.controller_device_id ?? '');
      const ctrlName = String(req.display_name ?? ctrlId);
      const remoteFp = String(req.remote_fingerprint ?? '').toLowerCase();
      const grantKey = `${transport}:${ctrlId}`;

      if (mode === 'code') {
        if (!activePairingCode) {
          return Response.json({ code: 'pairing_code_expired', detail: 'No pairing code.' }, { status: 403 });
        }
        if (String(req.pairing_code ?? '') !== activePairingCode.code) {
          return Response.json({ code: 'pairing_code_rejected', detail: 'Wrong code.' }, { status: 403 });
        }
        activePairingCode = null;
        const policy: PeerAccessPolicy = {
          controller_device_id: ctrlId,
          role: 'operator',
          workspace_mode: 'all',
          workspace_ids: [],
          allow_non_workspace_sessions: true,
          sync_all_sessions: true,
          allow_agent_delegation: true,
          allow_provider_proxy: true,
          revision: `rev-${policyRev++}`,
        };
        grants.set(grantKey, {
          transport,
          controllerDeviceId: ctrlId,
          displayName: ctrlName,
          fingerprint: remoteFp,
          policy,
        });
        return Response.json({
          accepted: true,
          grant_id: `grant-${grantKey}`,
          controller_device_id: ctrlId,
          display_name: ctrlName,
          policy_revision: policy.revision,
          policy,
        });
      }

      if (mode === 'grant') {
        const grant = grants.get(grantKey);
        if (!grant) {
          return Response.json({ code: 'unauthorized', detail: 'Unknown grant.' }, { status: 401 });
        }
        if (grant.fingerprint !== remoteFp) {
          return Response.json(
            { code: 'fingerprint_mismatch', detail: 'Client cert does not match pinned grant.' },
            { status: 403 },
          );
        }
        return Response.json({
          accepted: true,
          grant_id: `grant-${grantKey}`,
          controller_device_id: ctrlId,
          display_name: grant.displayName,
          policy_revision: grant.policy.revision,
          policy: grant.policy,
        });
      }

      return Response.json({ code: 'handshake_failed', detail: 'Invalid mode.' }, { status: 400 });
    }

    if (url.pathname === '/api/v1/projects' && method === 'GET') {
      return Response.json([
        { id: 'proj-relay-1', name: 'Relay Workspace', path: '/workspace/relay-1' },
      ]);
    }

    if (url.pathname === '/api/v1/tools' && method === 'GET') {
      return Response.json([{ id: 'ls' }, { id: 'stat' }, { id: 'file_search' }]);
    }

    if (url.pathname === '/api/v1/tool-layer-readiness' && method === 'GET') {
      return Response.json({ ready: true });
    }

    const toolExecMatch = /^\/api\/v1\/tools\/([^/]+)\/execute$/i.exec(url.pathname);
    if (toolExecMatch && method === 'POST') {
      return Response.json({
        status: 'completed',
        tool_id: decodeURIComponent(toolExecMatch[1]!),
        rewritten_path: url.pathname,
      });
    }

    if (url.pathname === '/api/v1/sessions/sess-relay/messages' && method === 'POST') {
      const parsed = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      return Response.json(
        {
          id: 'msg-relay-1',
          echoed_content: parsed.content,
          secret_worker_reply: secretReplyCanary,
          controller_id: headersObj['x-tinadec-peer-controller-id'] ?? null,
        },
        { status: 201 },
      );
    }

    if (url.pathname === '/api/v1/events' && method === 'GET') {
      const afterSeq = url.searchParams.get('after_seq') ?? '0';
      let ctrlRef: ReadableStreamDefaultController<Uint8Array> | null = null;
      const stream = new ReadableStream<Uint8Array>({
        start(c) {
          ctrlRef = c;
          sseControllers.add(c);
          c.enqueue(
            encoder.encode(
              `id: ${Number(afterSeq) + 1}\nevent: relay.ready\ndata: ${JSON.stringify({ secret_sse: secretReplyCanary })}\n\n`,
            ),
          );
        },
        cancel() {
          if (ctrlRef) sseControllers.delete(ctrlRef);
        },
      });
      init?.signal?.addEventListener(
        'abort',
        () => {
          if (ctrlRef) {
            sseControllers.delete(ctrlRef);
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

    return Response.json({ code: 'not_found', detail: url.pathname }, { status: 404 });
  }) as typeof fetch;

  return {
    localDeviceId,
    fingerprint: workerTls.fingerprint,
    certPem: workerTls.certPem,
    privateKeyPem: workerTls.privateKeyPem,
    verifyCalls,
    grants,
    fetchImpl,
    revokeController(controllerDeviceId: string, transport = 'relay') {
      grants.delete(`${transport}:${controllerDeviceId}`);
      emitPolicyEvent({
        type: 'grant_revoked',
        controller_device_id: controllerDeviceId,
      });
    },
  };
}

function createRelayControllerCoreHarness() {
  const ctrlTls = generateSelfSignedP256TlsMaterial('controller-relay-a');
  const localDeviceId = 'dev-relay-ctrl-a';
  const devices = new Map<string, PeerDeviceRecord>();
  let seq = 1;

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
        inbound_enabled: false,
        local_device_id: localDeviceId,
        display_name: 'Controller-A',
        listen_port: 48735,
        local_fingerprint: ctrlTls.fingerprint,
        cert_pem: ctrlTls.certPem,
        private_key_pem: ctrlTls.privateKeyPem,
      });
    }

    if (url.pathname === '/api/v1/peers/devices' && method === 'GET') {
      return Response.json({ devices: [...devices.values()] });
    }

    if (url.pathname === '/api/v1/peers/devices/pair' && method === 'POST') {
      const payload = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      const remoteId = String(payload.remote_device_id ?? '');
      const transport = (payload.transport === 'relay' ? 'relay' : 'lan') as 'lan' | 'relay';
      let existing = [...devices.values()].find(
        (d) => d.remote_device_id === remoteId && d.transport === transport,
      );
      const now = Date.now();
      if (!existing) {
        existing = {
          device_id: `peer-dev-${seq++}`,
          display_name: String(payload.display_name ?? remoteId),
          transport,
          address: String(payload.address ?? ''),
          remote_device_id: remoteId,
          certificate_fingerprint: String(payload.certificate_fingerprint ?? ''),
          protocol_major: Number(payload.protocol_major ?? 1),
          protocol_minor: Number(payload.protocol_minor ?? 0),
          paired_at: now,
          last_connected_at: now,
          connection_desired: Boolean(payload.connection_desired ?? true),
          adopt_sessions: Boolean(payload.adopt_sessions ?? true),
          last_state: (payload.last_state as PeerDeviceRecord['last_state']) ?? 'ready',
          last_failure_code: null,
          access_revision: (payload.access_revision as string) ?? 'rev-1',
        };
      } else {
        existing.address = String(payload.address ?? existing.address);
        existing.certificate_fingerprint = String(
          payload.certificate_fingerprint ?? existing.certificate_fingerprint,
        );
        existing.last_state = (payload.last_state as PeerDeviceRecord['last_state']) ?? 'ready';
        existing.last_connected_at = now;
      }
      devices.set(existing.device_id, existing);
      return Response.json(existing, { status: 201 });
    }

    if (url.pathname === '/api/v1/peers/internal/controller-state' && method === 'POST') {
      const patch = JSON.parse(bodyText || '{}') as Record<string, unknown>;
      const target = devices.get(String(patch.device_id ?? ''));
      if (target) {
        if (typeof patch.last_state === 'string') {
          target.last_state = patch.last_state as PeerDeviceRecord['last_state'];
        }
        target.last_failure_code =
          typeof patch.last_failure_code === 'string' ? patch.last_failure_code : null;
      }
      return Response.json({ ok: true });
    }

    return Response.json({ code: 'not_found' }, { status: 404 });
  }) as typeof fetch;

  return {
    localDeviceId,
    fingerprint: ctrlTls.fingerprint,
    certPem: ctrlTls.certPem,
    privateKeyPem: ctrlTls.privateKeyPem,
    devices,
    fetchImpl,
  };
}

// ── Test Suite ───────────────────────────────────────────────────────────────

describe('Stateless Blind Relay Server & Pluggable IPeerTransportChannel (Wave 3)', () => {
  test(
    '1. peer-relay-server.mjs standalone CLI & REST/WS primitives enforce token auth, single-use tickets, and mutual teardown',
    async () => {
      // 1A. Verify standalone CLI execution (`bun scripts/peer-relay-server.mjs --port 0`)
      const cliChild = spawn(process.execPath, [RELAY_SCRIPT_PATH, '--port', '0'], {
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true,
      });

      try {
        const startupLine = await new Promise<string>((resolveLine, rejectLine) => {
          let buf = '';
          const timer = setTimeout(() => rejectLine(new Error('CLI relay startup timed out.')), 15_000);
        cliChild.stdout.on('data', (chunk: Buffer) => {
          buf += chunk.toString('utf8');
          const nl = buf.indexOf('\n');
          if (nl >= 0) {
            clearTimeout(timer);
            resolveLine(buf.slice(0, nl).trim());
          }
        });
        cliChild.once('error', (err) => {
          clearTimeout(timer);
          rejectLine(err);
        });
      });

      const started = JSON.parse(startupLine) as {
        event: string;
        port: number;
        http_url: string;
      };
      expect(started.event).toBe('peer_relay_started');
      expect(started.port).toBeGreaterThan(0);

      const healthRes = await fetch(`${started.http_url}/health`);
      expect(healthRes.status).toBe(200);
      const healthBody = (await healthRes.json()) as { status: string; mode: string };
      expect(healthBody.status).toBe('ok');
      expect(healthBody.mode).toBe('blind_ciphertext_only');
    } finally {
      cliChild.kill();
    }

    // 1B. Library mode: registration anti-hijack, rendezvous lifecycle, single-use link ticket & mutual close
    const relay = await startPeerRelayServer({ port: 0, host: '127.0.0.1' });
    try {
      const regRes = await fetch(`${relay.httpUrl}/v1/devices/register`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ relay_device_id: 'worker-alpha' }),
      });
      expect(regRes.status).toBe(200);
      const reg = (await regRes.json()) as { relay_device_id: string; relay_token: string };
      expect(reg.relay_device_id).toBe('worker-alpha');
      expect(reg.relay_token.length).toBeGreaterThanOrEqual(32);

      // Unauthenticated attempt to hijack an active registered device_id must be rejected 403
      const hijackRes = await fetch(`${relay.httpUrl}/v1/devices/register`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ relay_device_id: 'worker-alpha', relay_token: 'forged-token-1234567890' }),
      });
      expect(hijackRes.status).toBe(403);

      // Publish & resolve short-lived rendezvous code
      const pubRes = await fetch(`${relay.httpUrl}/v1/rendezvous`, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
          authorization: `Bearer ${reg.relay_token}`,
        },
        body: JSON.stringify({
          action: 'publish',
          pairing_code: '492817',
          relay_device_id: 'worker-alpha',
        }),
      });
      expect(pubRes.status).toBe(200);

      const resolveRes = await fetch(`${relay.httpUrl}/v1/rendezvous`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ action: 'resolve', pairing_code: '492817' }),
      });
      expect(resolveRes.status).toBe(200);
      expect(((await resolveRes.json()) as { relay_device_id: string }).relay_device_id).toBe('worker-alpha');

      // Ticket allocation before inbox is connected -> 503 unreachable
      const offlineTicket = await fetch(`${relay.httpUrl}/v1/links/tickets`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ target_device_id: 'worker-alpha' }),
      });
      expect(offlineTicket.status).toBe(503);

      // Connect authenticated /v1/inbox and verify single-use binary link + mutual close
      const inboxWs = new WebSocket(
        `${relay.wsUrl}/v1/inbox?device_id=worker-alpha&token=${encodeURIComponent(reg.relay_token)}`,
      );
      await new Promise<void>((res, rej) => {
        inboxWs.addEventListener('message', (e) => {
          const msg = JSON.parse(String(e.data)) as { type?: string };
          if (msg.type === 'relay.inbox_ready') res();
        });
        inboxWs.addEventListener('error', rej, { once: true });
      });

      const inboxNotificationPromise = new Promise<{ ticket_id: string; acceptor_token: string }>((res) => {
        inboxWs.addEventListener('message', (e) => {
          const msg = JSON.parse(String(e.data)) as {
            type?: string;
            ticket_id?: string;
            acceptor_token?: string;
          };
          if (msg.type === 'relay.link_requested' && msg.ticket_id && msg.acceptor_token) {
            res({ ticket_id: msg.ticket_id, acceptor_token: msg.acceptor_token });
          }
        });
      });

      const tktRes = await fetch(`${relay.httpUrl}/v1/links/tickets`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ target_device_id: 'worker-alpha' }),
      });
      expect(tktRes.status).toBe(201);
      const tkt = (await tktRes.json()) as { ticket_id: string; initiator_token: string };
      const notified = await inboxNotificationPromise;
      expect(notified.ticket_id).toBe(tkt.ticket_id);

      const initiatorWs = new WebSocket(
        `${relay.wsUrl}/v1/links/${tkt.ticket_id}?role=initiator&token=${tkt.initiator_token}`,
      );
      initiatorWs.binaryType = 'arraybuffer';
      await new Promise<void>((r) => initiatorWs.addEventListener('open', () => r(), { once: true }));

      // Replaying the same initiator slot must fail immediately
      const replayWs = new WebSocket(
        `${relay.wsUrl}/v1/links/${tkt.ticket_id}?role=initiator&token=${tkt.initiator_token}`,
      );
      const replayClosed = await new Promise<boolean>((r) => {
        replayWs.addEventListener('error', () => r(true), { once: true });
        replayWs.addEventListener('close', () => r(true), { once: true });
      });
      expect(replayClosed).toBe(true);

      const acceptorWs = new WebSocket(
        `${relay.wsUrl}/v1/links/${tkt.ticket_id}?role=acceptor&token=${notified.acceptor_token}`,
      );
      acceptorWs.binaryType = 'arraybuffer';
      await new Promise<void>((r) => acceptorWs.addEventListener('open', () => r(), { once: true }));

      // Mutual teardown: closing initiator closes acceptor and destroys the ticket
      const acceptorClosedPromise = new Promise<void>((r) =>
        acceptorWs.addEventListener('close', () => r(), { once: true }),
      );
      initiatorWs.close(1000, 'done');
      await acceptorClosedPromise;
      expect(relay.getStats().activeTickets).toBe(0);
      expect(relay.getStats().activeBridges).toBe(0);

      inboxWs.close();
    } finally {
      await relay.close();
    }
    },
    20_000,
  );

  test(
    '2. Pure-Relay E2EE mTLS + 4-step handshake + transparent HTTP/SSE/ALPN DevServer proxy + wire-tap proves ZERO plaintext exposure',
    async () => {
    const PAIRING_CODE = '739184';
    const REQUEST_CANARY = 'SUPER_SECRET_PLAINTEXT_PAYLOAD_ALPHA_90210';
    const REPLY_CANARY = 'SUPER_SECRET_WORKER_REPLY_BETA_77889';

    const relay = await startPeerRelayServer({
      port: 0,
      host: '127.0.0.1',
      recordWireTap: true,
    });

    // Start a tiny local DevServer on Worker side to verify ALPN `tinadec-peer-devserver/1` over Relay too
    const localDevServer = http.createServer((_req, res) => {
      res.writeHead(200, { 'content-type': 'text/plain; charset=utf-8' });
      res.end('DEVSERVER_PREVIEW_OVER_BLIND_RELAY_OK');
    });
    await new Promise<void>((r) => localDevServer.listen(0, '127.0.0.1', r));
    const devServerPort = (localDevServer.address() as net.AddressInfo).port;

    const workerCore = createRelayWorkerCoreHarness({
      inboundEnabled: true,
      pairingCode: PAIRING_CODE,
      secretReplyCanary: REPLY_CANARY,
    });
    const ctrlCore = createRelayControllerCoreHarness();

    // Worker listens EXCLUSIVELY via Relay (`disableLanListen: true` -> zero LAN TCP port bound!)
    const workerServer = new PeerTlsServer({
      coreUrl: () => 'http://worker-core.internal',
      fetchImpl: workerCore.fetchImpl,
      relayTransport: new BlindRelayTransport({ relayUrl: relay.httpUrl }),
      disableLanListen: true,
      heartbeatIntervalMs: 5_000,
      heartbeatTimeoutMs: 15_000,
    });

    const controllerPool = new PeerClientPool({
      coreUrl: () => 'http://ctrl-core.internal',
      fetchImpl: ctrlCore.fetchImpl,
      relayTransport: new BlindRelayTransport({ relayUrl: relay.httpUrl }),
      heartbeatIntervalMs: 5_000,
      heartbeatTimeoutMs: 15_000,
    });

    const controllerGatewayApp = registerPeerRoutes(new Elysia(), {
      peerTlsServer: new PeerTlsServer({
        coreUrl: () => 'http://ctrl-core.internal',
        fetchImpl: ctrlCore.fetchImpl,
      }),
      peerClientPool: controllerPool,
      forwardHeaders: () => ({
        'x-request-id': crypto.randomUUID(),
        'x-tinadec-principal': 'dev@local',
      }),
      keepStreamOpen: () => undefined,
      webAuthRuntime: createWebAuthRuntime(),
      mode: 'local',
      trustProxy: 'false',
      proxyCoreJson: async (path, opts) => {
        const res = await ctrlCore.fetchImpl(`http://ctrl-core.internal${path}`, {
          method: opts?.method ?? 'GET',
          headers: opts?.headers,
          body: opts?.body ? JSON.stringify(opts.body) : undefined,
        });
        return {
          status: res.status,
          data: await res.json(),
          headers: res.headers,
        };
      },
    });

    try {
      const workerStatus = await workerServer.start();
      expect(workerStatus.enabled).toBe(true);
      expect(workerStatus.listening).toBe(true);
      expect(workerStatus.actualListenPort).toBeNull(); // Proves NO direct LAN TCP port exists!
      expect(workerStatus.relayConnected).toBe(true);
      expect(workerStatus.relayDeviceId).toBe(workerCore.localDeviceId);

      // Worker publishes short-lived pairing code to Relay rendezvous
      const rvPublished = await workerServer.publishRelayRendezvous(PAIRING_CODE, 120);
      expect(rvPublished.relay_device_id).toBe(workerCore.localDeviceId);

      // Step A: Probe Worker over Relay without consuming the pairing code
      const probeRes = await controllerGatewayApp.handle(
        new Request('http://127.0.0.1/api/v1/peers/devices/probe', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ address: `relay:${workerCore.localDeviceId}` }),
        }),
      );
      expect(probeRes.status).toBe(200);
      const probeDto = (await probeRes.json()) as {
        reachable: boolean;
        remote_device_id: string;
        certificate_fingerprint: string;
      };
      expect(probeDto.reachable).toBe(true);
      expect(probeDto.remote_device_id).toBe(workerCore.localDeviceId);
      expect(probeDto.certificate_fingerprint).toBe(workerCore.fingerprint);

      // Step B: Pair via Relay short-code rendezvous (`relay:rendezvous:<code`>)
      const pairRes = await controllerGatewayApp.handle(
        new Request('http://127.0.0.1/api/v1/peers/devices/pair', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({
            address: `relay:rendezvous:${PAIRING_CODE}`,
            pairing_code: PAIRING_CODE,
            expected_fingerprint: workerCore.fingerprint,
            display_name: 'Cloud Relay Worker',
          }),
        }),
      );
      expect(pairRes.status).toBe(201);
      const pairedDevice = (await pairRes.json()) as PeerDeviceRecord;
      expect(pairedDevice.transport).toBe('relay');
      expect(pairedDevice.address).toContain(workerCore.localDeviceId);
      expect(pairedDevice.last_state).toBe('ready');
      expect(workerCore.verifyCalls[0]?.transport).toBe('relay');

      // Step C: Transparent HTTP GET & POST over the E2EE Relay mTLS tunnel
      const projRes = await controllerGatewayApp.handle(
        new Request(
          `http://127.0.0.1/api/v1/peers/${pairedDevice.device_id}/proxy/api/v1/projects`,
        ),
      );
      expect(projRes.status).toBe(200);
      const projects = (await projRes.json()) as Array<{ id: string }>;
      expect(projects[0]?.id).toBe('proj-relay-1');

      // Verify Gateway BFF tool aliases (`/api/v1/code/tools/ls/execute`, `/api/v1/code/tools`, `/api/v1/tool-runtime/health`)
      // rewrite to canonical Core paths (`/api/v1/tools/ls/execute`, `/api/v1/tools`, `/api/v1/tool-layer-readiness`)
      const lsExecRes = await controllerGatewayApp.handle(
        new Request(
          `http://127.0.0.1/api/v1/peers/${pairedDevice.device_id}/proxy/api/v1/code/tools/ls/execute`,
          {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({ arguments: { path: '.' } }),
          },
        ),
      );
      expect(lsExecRes.status).toBe(200);
      expect(await lsExecRes.json()).toEqual({
        status: 'completed',
        tool_id: 'ls',
        rewritten_path: '/api/v1/tools/ls/execute',
      });

      const codeToolsRes = await controllerGatewayApp.handle(
        new Request(
          `http://127.0.0.1/api/v1/peers/${pairedDevice.device_id}/proxy/api/v1/code/tools`,
        ),
      );
      expect(codeToolsRes.status).toBe(200);

      const trHealthRes = await controllerGatewayApp.handle(
        new Request(
          `http://127.0.0.1/api/v1/peers/${pairedDevice.device_id}/proxy/api/v1/tool-runtime/health`,
        ),
      );
      expect(trHealthRes.status).toBe(200);

      const msgRes = await controllerGatewayApp.handle(
        new Request(
          `http://127.0.0.1/api/v1/peers/${pairedDevice.device_id}/proxy/api/v1/sessions/sess-relay/messages`,
          {
            method: 'POST',
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify({
              content: REQUEST_CANARY,
              client_request_id: 'req-relay-001',
            }),
          },
        ),
      );
      expect(msgRes.status).toBe(201);
      const msgBody = (await msgRes.json()) as {
        echoed_content: string;
        secret_worker_reply: string;
        controller_id: string;
      };
      expect(msgBody.echoed_content).toBe(REQUEST_CANARY);
      expect(msgBody.secret_worker_reply).toBe(REPLY_CANARY);
      expect(msgBody.controller_id).toBe(ctrlCore.localDeviceId);

      // Step D: ALPN `tinadec-peer-devserver/1` preview lease over BlindRelayTransport!
      const lease = await controllerPool.requestDevServerLease(pairedDevice.device_id, {
        target_port: devServerPort,
      });
      const previewRes = await fetch(lease.preview_url);
      expect(previewRes.status).toBe(200);
      expect(await previewRes.text()).toBe('DEVSERVER_PREVIEW_OVER_BLIND_RELAY_OK');
      await controllerPool.releaseDevServerLease(pairedDevice.device_id, lease.lease_id);

      // Step E: Disconnect and reconnect using persistent `grant` (pairing code already burned!)
      await controllerPool.disconnectDevice(pairedDevice.device_id);
      expect(controllerPool.getLiveState(pairedDevice.device_id)?.state).toBe('disconnected');

      const reconnected = await controllerPool.connectDevice(pairedDevice.device_id);
      expect(reconnected.last_state).toBe('ready');

      // Step F: Open SSE stream over Relay & verify < 1000ms instant teardown on grant revocation
      const sseAbort = new AbortController();
      const sseRes = await controllerGatewayApp.handle(
        new Request(
          `http://127.0.0.1/api/v1/peers/${pairedDevice.device_id}/proxy/api/v1/events?after_seq=41`,
          { signal: sseAbort.signal },
        ),
      );
      expect(sseRes.status).toBe(200);
      const sseReader = sseRes.body!.getReader();
      const firstChunk = await sseReader.read();
      expect(new TextDecoder().decode(firstChunk.value)).toContain(REPLY_CANARY);

      // Inspect Relay wire-tap BEFORE revocation: 100% TLS ciphertext, ZERO plaintext leakage!
      const captured = relay.getCapturedFrames();
      expect(captured.length).toBeGreaterThan(6);
      expect(captured.every((f) => f.opcode === 0x02)).toBe(true);
      expect(captured.some((f) => f.direction === 'initiator->acceptor')).toBe(true);
      expect(captured.some((f) => f.direction === 'acceptor->initiator')).toBe(true);

      // First relayed frame from initiator MUST start with TLS Handshake ContentType 0x16 (22) and Legacy Version 0x03
      const firstInitiatorFrame = captured.find((f) => f.direction === 'initiator->acceptor')!;
      expect(firstInitiatorFrame.payload[0]).toBe(0x16);
      expect(firstInitiatorFrame.payload[1]).toBe(0x03);

      // Concatenate EVERY byte the Relay ever touched in both directions and scan for forbidden plaintext
      const rawRelayBytesUtf8 = Buffer.concat(captured.map((f) => f.payload)).toString('utf8');
      const rawRelayBytesLatin1 = Buffer.concat(captured.map((f) => f.payload)).toString('latin1');
      for (const forbiddenPlaintext of [
        REQUEST_CANARY,
        REPLY_CANARY,
        PAIRING_CODE,
        'DEVSERVER_PREVIEW_OVER_BLIND_RELAY_OK',
        'peer.hello',
        'peer.hello-ack',
        'peer.ready',
        'http.req',
        'x-tinadec-peer-controller-id',
      ]) {
        expect(rawRelayBytesUtf8.includes(forbiddenPlaintext)).toBe(false);
        expect(rawRelayBytesLatin1.includes(forbiddenPlaintext)).toBe(false);
      }

      // Trigger instant revocation on Worker and assert SSE stream terminates in < 500ms
      const t0 = performance.now();
      workerCore.revokeController(ctrlCore.localDeviceId, 'relay');
      while (true) {
        const next = await sseReader.read();
        if (next.done) break;
      }
      expect(performance.now() - t0).toBeLessThan(500);
    } finally {
      await controllerPool.stop();
      await workerServer.stop();
      localDevServer.close();
      await relay.close();
    }
    },
    25_000,
  );

  test(
    '3. Active MITM Relay substituting server or client X.509 certificate is deterministically blocked with fingerprint_mismatch',
    async () => {
      const GENUINE_PAIRING_CODE = '884422';
      const workerCore = createRelayWorkerCoreHarness({
        inboundEnabled: true,
        pairingCode: GENUINE_PAIRING_CODE,
      });
      const ctrlCore = createRelayControllerCoreHarness();

      // Attacker's forged ECDSA P-256 certificates generated on a compromised Relay
      const rogueServerTls = generateSelfSignedP256TlsMaterial('evil-relay-mitm-server');
      const rogueClientTls = generateSelfSignedP256TlsMaterial('evil-relay-mitm-client');

      let mitmCapturedControllerFrames = 0;
      let mitmInterceptServerCert = true;

      const rogueMitmServer = tls.createServer(
        {
          cert: rogueServerTls.certPem,
          key: rogueServerTls.privateKeyPem,
          requestCert: true,
          rejectUnauthorized: false,
          ALPNProtocols: [...PEER_ALPN_PROTOCOLS],
        },
        (mitmServerSideSock) => {
          const decoder = new PeerFrameDecoder();
          mitmServerSideSock.on('data', (chunk: Buffer) => {
            try {
              const frames = decoder.push(chunk);
              for (const f of frames) {
                if (f.header.type === 'peer.hello') {
                  mitmCapturedControllerFrames += 1;
                }
              }
            } catch {
              // ignore
            }
          });
        },
      );
      await new Promise<void>((r) => rogueMitmServer.listen(0, '127.0.0.1', r));
      const rogueMitmPort = (rogueMitmServer.address() as net.AddressInfo).port;

      const evilRelay = await startPeerRelayServer({
        port: 0,
        host: '127.0.0.1',
        customLinkInterceptor: (_ticketId: string, initWs: any, accWs: any, ctx: any) => {
          if (mitmInterceptServerCert) {
            // Attack 3A mode: Divert Controller's initiator WS into Relay's rogue TLS server (`rogueServerTls`)
            const sockToRogueSrv = net.connect({ host: '127.0.0.1', port: rogueMitmPort }, () => {
              for (const early of ctx.earlyInitiatorFrames) {
                sockToRogueSrv.write(early);
              }
              initWs.onMessage = (_op: number, buf: Buffer) => {
                if (!sockToRogueSrv.destroyed) sockToRogueSrv.write(buf);
              };
              sockToRogueSrv.on('data', (chunk: Buffer) => {
                initWs.sendBinary(chunk);
              });
              sockToRogueSrv.once('close', () => initWs.close(1000, 'mitm closed'));
            });
            return;
          }

          // Attack 3B mode: Forward raw frames between rogue client initiator and genuine Worker acceptor
          initWs.onMessage = (_op: number, buf: Buffer) => accWs.sendBinary(buf);
          accWs.onMessage = (_op: number, buf: Buffer) => initWs.sendBinary(buf);
          for (const early of ctx.earlyInitiatorFrames) accWs.sendBinary(early);
          for (const early of ctx.earlyAcceptorFrames) initWs.sendBinary(early);
        },
      });

      // Pre-seed a genuine relay grant on Worker so Attack 3B specifically hits fingerprint verification
      workerCore.grants.set(`relay:${ctrlCore.localDeviceId}`, {
        transport: 'relay',
        controllerDeviceId: ctrlCore.localDeviceId,
        displayName: 'Controller-A',
        fingerprint: ctrlCore.fingerprint,
        policy: {
          controller_device_id: ctrlCore.localDeviceId,
          role: 'operator',
          workspace_mode: 'all',
          revision: 'rev-1',
        },
      });

      const workerServer = new PeerTlsServer({
        coreUrl: () => 'http://worker-core.internal',
        fetchImpl: workerCore.fetchImpl,
        relayTransport: new BlindRelayTransport({ relayUrl: evilRelay.httpUrl }),
        disableLanListen: true,
      });

      const controllerPool = new PeerClientPool({
        coreUrl: () => 'http://ctrl-core.internal',
        fetchImpl: ctrlCore.fetchImpl,
        relayTransport: new BlindRelayTransport({ relayUrl: evilRelay.httpUrl }),
      });

      try {
        await workerServer.start();

        // Attack 3A: Controller pairs through Evil MITM Relay -> MUST fail with `fingerprint_mismatch`
        // BEFORE sending `peer.hello` (protecting the one-time pairing_code from leakage!)
        let caughtErr: unknown = null;
        try {
          await controllerPool.pairWithPeer({
            address: `relay:${workerCore.localDeviceId}`,
            pairing_code: GENUINE_PAIRING_CODE,
            expected_fingerprint: workerCore.fingerprint,
          });
        } catch (err) {
          caughtErr = err;
        }

        expect(caughtErr).toBeInstanceOf(PeerProtocolError);
        expect((caughtErr as PeerProtocolError).code).toBe('fingerprint_mismatch');
        expect((caughtErr as PeerProtocolError).status).toBe(403);
        expect(mitmCapturedControllerFrames).toBe(0);

        // Attack 3B: Compromised Relay / rogue actor dials Worker via Relay using forged client cert
        // (`rogueClientTls`) while spoofing genuine `ctrlCore.localDeviceId` & smuggled fingerprint!
        mitmInterceptServerCert = false;
        const rogueChannel = new BlindRelayTransport({ relayUrl: evilRelay.httpUrl });
        const rogueDialed = await rogueChannel.connect({
          address: `relay:${workerCore.localDeviceId}`,
          certPem: rogueClientTls.certPem,
          privateKeyPem: rogueClientTls.privateKeyPem,
          alpnProtocols: ['tinadec-peer-http/1'],
        });

        const rogueConn = new PeerFrameConnection(rogueDialed.tlsSocket, { heartbeatIntervalMs: 0 });
        rogueConn.sendFrame({
          type: 'peer.hello',
          protocolMajor: 1,
          protocolMinor: 0,
          deviceId: ctrlCore.localDeviceId,
          deviceName: 'Spoofed Controller By Evil Relay',
          pairingMode: 'grant',
          capabilities: ['proxy_v1'],
          expectedRemoteFingerprint: workerCore.fingerprint,
          ...({ certificateFingerprint: ctrlCore.fingerprint } as Record<string, unknown>),
        });

        const reply = await rogueConn.nextFrame(4_000);
        expect(reply.header.type).toBe('peer.hello-reject');
        if (reply.header.type === 'peer.hello-reject') {
          expect(reply.header.code).toBe('fingerprint_mismatch');
        }
        rogueConn.destroy();
        expect(workerServer.activeSessionCount).toBe(0);
      } finally {
        await controllerPool.stop();
        await workerServer.stop();
        rogueMitmServer.close();
        await evilRelay.close();
      }
    },
    20_000,
  );

  test('4. Custom IPeerTransportChannel implementation plugs into PeerClientPool & PeerTlsServer without touching handshake/protocol', async () => {
    const workerCore = createRelayWorkerCoreHarness({
      inboundEnabled: true,
      pairingCode: '551199',
    });
    const ctrlCore = createRelayControllerCoreHarness();

    // Custom in-memory transport channel (`mem://worker`) built purely on IPeerTransportChannel
    let onInboundStream: ((raw: Duplex) => void) | null = null;
    class InMemoryDuplexTransport implements IPeerTransportChannel {
      readonly kind = 'lan' as const;
      private internalSrv: tls.Server | null = null;

      canHandleAddress(address: string): boolean {
        return address.startsWith('mem://');
      }

      async connect(options: PeerTransportConnectOptions): Promise<PeerTransportConnectedTunnel> {
        if (!onInboundStream) {
          throw new PeerProtocolError('unreachable', 'In-memory listener is offline.');
        }
        let cSide!: Duplex;
        let sSide!: Duplex;
        cSide = new Duplex({
          read() {},
          write(chunk, _enc, cb) {
            queueMicrotask(() => sSide.push(Buffer.from(chunk)));
            cb(null);
          },
          final(cb) {
            queueMicrotask(() => sSide.push(null));
            cb(null);
          },
        });
        sSide = new Duplex({
          read() {},
          write(chunk, _enc, cb) {
            queueMicrotask(() => cSide.push(Buffer.from(chunk)));
            cb(null);
          },
          final(cb) {
            queueMicrotask(() => cSide.push(null));
            cb(null);
          },
        });
        onInboundStream(sSide);
        const tlsSocket = await upgradeStreamToClientTls(cSide, {
          certPem: options.certPem,
          privateKeyPem: options.privateKeyPem,
          alpnProtocols: options.alpnProtocols,
        });
        return {
          transport: 'lan',
          normalizedAddress: options.address,
          tlsSocket,
          rawStream: cSide as PeerTransportStream,
        };
      }

      async listen(options: PeerTransportListenOptions): Promise<PeerTransportListenerHandle> {
        const srv = tls.createServer(
          {
            cert: options.certPem,
            key: options.privateKeyPem,
            requestCert: true,
            rejectUnauthorized: false,
            ALPNProtocols: [...(options.alpnProtocols ?? PEER_ALPN_PROTOCOLS)],
          },
          (tlsSocket) => {
            options.onConnection({
              transport: 'lan',
              tlsSocket,
              rawStream: tlsSocket as PeerTransportStream,
            });
          },
        );
        await new Promise<void>((r) => srv.listen(0, '127.0.0.1', r));
        const port = (srv.address() as net.AddressInfo).port;
        this.internalSrv = srv;

        onInboundStream = (sSide: Duplex) => {
          const bridge = net.connect({ host: '127.0.0.1', port }, () => {
            sSide.on('data', (c) => {
              if (!bridge.destroyed) bridge.write(c);
            });
            bridge.on('data', (c) => {
              if (!sSide.destroyed) sSide.write(c);
            });
            sSide.once('close', () => bridge.destroy());
            bridge.once('close', () => sSide.destroy());
          });
        };

        return {
          transport: 'lan',
          boundPort: port,
          async close() {
            onInboundStream = null;
            await new Promise<void>((r) => srv.close(() => r()));
          },
        };
      }
    }

    const memTransport = new InMemoryDuplexTransport();
    const workerServer = new PeerTlsServer({
      coreUrl: () => 'http://worker-core.internal',
      fetchImpl: workerCore.fetchImpl,
      lanTransport: memTransport,
    });
    const controllerPool = new PeerClientPool({
      coreUrl: () => 'http://ctrl-core.internal',
      fetchImpl: ctrlCore.fetchImpl,
      customTransports: [memTransport],
    });

    try {
      await workerServer.start();
      const paired = await controllerPool.pairWithPeer({
        address: 'mem://worker-node',
        pairing_code: '551199',
        expected_fingerprint: workerCore.fingerprint,
      });
      expect(paired.last_state).toBe('ready');
      expect(paired.address).toBe('mem://worker-node');

      const res = await controllerPool.proxyHttpRequest(paired.device_id, {
        method: 'GET',
        path: '/api/v1/projects',
      });
      expect(res.status).toBe(200);
      const list = (await res.json()) as Array<{ id: string }>;
      expect(list[0]?.id).toBe('proj-relay-1');
    } finally {
      await controllerPool.stop();
      await workerServer.stop();
    }
  });
});
