/**
 * Worker-side inbound mTLS server (`PeerTlsServer`):
 * - Factory default OFF (`inbound_enabled === false` -> never binds 48735).
 * - When enabled, binds `0.0.0.0:48735` (auto-probing `48735..48755` on `EADDRINUSE` and
 *   updating Core with the actual bound port; supports `TINADEC_PEER_LISTEN_PORT` override).
 * - TLS config: `cert`, `key`, `requestCert: true`, `rejectUnauthorized: false`,
 *   `ALPNProtocols: ['tinadec-peer-http/1', 'tinadec-peer-devserver/1']`.
 * - Multiplexes HTTP, SSE, and WebSocket terminal streams over the single mTLS tunnel,
 *   guarded by `PeerPolicyGuard` and `< 100ms` instant teardown on policy change / revoke.
 */

import { networkInterfaces } from 'node:os';
import tls, { type Server as TlsServer, type TLSSocket } from 'node:tls';
import { ensureRequestId } from '../headers.js';
import { toProblemDetails } from '../mappers/errorMapper.js';
import {
  createAgentTerminalBridgeSession,
  createInteractiveShellSession,
  type WsTerminalClient,
  type WsTerminalSession,
} from '../wsTerminalProxy.js';
import { type CoreHandshakeVerifyDecision, type CoreHandshakeVerifyInput, performServerHandshake } from './peerHandshake.js';
import { PeerDevServerLeaseManager } from './peerDevServerTunnel.js';
import {
  buildSanitizedPeerCoreHeaders,
  evaluatePeerPolicy,
  extractTargetSessionId,
  filterPeerCollectionResponse,
  type PeerAccessPolicy,
  type PeerControllerContext,
  type PeerPolicyEvent,
  PeerPolicyEventSubscriber,
  type SessionScopeMetadata,
  toBoolFlag,
} from './peerPolicyGuard.js';
import {
  DEFAULT_PEER_LISTEN_PORT,
  type DecodedPeerFrame,
  isPeerErrorCode,
  MAX_PEER_LISTEN_PORT,
  PEER_ALPN_DEVSERVER,
  PEER_ALPN_PROTOCOLS,
  PEER_PROTOCOL_MAJOR,
  PEER_PROTOCOL_MINOR,
  type PeerErrorCode,
  PeerFrameConnection,
  type PeerHttpReqFrame,
  PeerProtocolError,
  type PeerWsOpenFrame,
} from './peerProtocol.js';

export interface LocalTlsMaterialDto {
  inbound_enabled: boolean;
  local_device_id: string;
  display_name: string;
  listen_port: number;
  local_fingerprint: string;
  cert_pem: string;
  private_key_pem: string;
}

export interface PeerInboundRuntimeStatus {
  enabled: boolean;
  listening: boolean;
  displayName: string;
  configuredPort: number;
  actualListenPort: number | null;
  portConflict: boolean;
  localDeviceId: string;
  localFingerprint: string;
  reachableAddresses: string[];
}

export interface PeerTlsServerOptions {
  coreUrl: () => string;
  fetchImpl?: typeof fetch;
  hostControlToken?: () => string | undefined;
  bindHost?: string;
  listenPortOverride?: number;
  maxProbePortOffset?: number;
  heartbeatIntervalMs?: number;
  heartbeatTimeoutMs?: number;
  handshakeTimeoutMs?: number;
  devServerAllowedPorts?: readonly number[];
  devServerForbiddenPorts?: readonly number[];
  devServerVerifyReachable?: boolean;
  createInteractiveShell?: typeof createInteractiveShellSession;
  createAgentTerminal?: typeof createAgentTerminalBridgeSession;
}

interface InFlightHttpStream {
  streamId: string;
  reqFrame: PeerHttpReqFrame;
  abort: AbortController;
  isJsonBuffered: boolean;
  jsonChunks: Buffer[];
  jsonByteLength: number;
  bodyStreamController: ReadableStreamDefaultController<Uint8Array> | null;
  startedCoreFetch: boolean;
}

interface ActivePeerSession {
  sessionId: string;
  tlsSocket: TLSSocket;
  connection: PeerFrameConnection;
  controller: PeerControllerContext;
  inFlightHttp: Map<string, InFlightHttpStream>;
  activeTerminals: Map<string, WsTerminalSession>;
}

const MAX_BUFFERED_JSON_BODY_BYTES = 2 * 1024 * 1024;

const RESPONSE_HOP_BY_HOP_HEADERS = new Set([
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

/**
 * Enumerate usable non-loopback IPv4 LAN and virtual NIC (Tailscale/WireGuard) `ip:port` addresses.
 */
export function discoverReachablePeerAddresses(port: number): string[] {
  if (!Number.isInteger(port) || port <= 0) return [];
  const ifaces = networkInterfaces();
  const lanAddresses: string[] = [];
  const virtualAddresses: string[] = [];

  for (const [name, entries] of Object.entries(ifaces)) {
    if (!entries) continue;
    const lowerName = name.toLowerCase();
    const isVirtualHint =
      lowerName.includes('tailscale') ||
      lowerName.includes('utun') ||
      lowerName.includes('wg') ||
      lowerName.includes('wireguard') ||
      lowerName.includes('tun');

    for (const info of entries) {
      const family = String(info.family);
      if ((family !== 'IPv4' && family !== '4') || info.internal) continue;
      const addr = info.address.trim();
      if (!addr || addr.startsWith('127.') || addr.startsWith('169.254.')) continue;

      // Tailscale CGNAT 100.64.0.0/10 or named virtual tunnel interface
      const octets = addr.split('.').map(Number);
      const isCgnat = octets.length === 4 && octets[0] === 100 && octets[1]! >= 64 && octets[1]! <= 127;
      if (isVirtualHint || isCgnat) {
        virtualAddresses.push(`${addr}:${port}`);
      } else {
        lanAddresses.push(`${addr}:${port}`);
      }
    }
  }

  return [...new Set([...lanAddresses, ...virtualAddresses])];
}

export class PeerTlsServer {
  private readonly options: PeerTlsServerOptions;
  private readonly policySubscriber: PeerPolicyEventSubscriber;
  readonly devServerTunnel: PeerDevServerLeaseManager;

  private server: TlsServer | null = null;
  private inboundEnabled = false;
  private localDeviceId = '';
  private displayName = '';
  private localFingerprint = '';
  private certPem = '';
  private privateKeyPem = '';
  private configuredPort = DEFAULT_PEER_LISTEN_PORT;
  private actualListenPort: number | null = null;
  private portConflict = false;

  private readonly rawSockets = new Set<import('node:net').Socket>();
  private readonly activeTlsSockets = new Set<TLSSocket>();
  private readonly pendingSockets = new Set<TLSSocket>();
  private readonly sessionsByController = new Map<string, Set<ActivePeerSession>>();
  private readonly knownSessionInitiators = new Map<string, string>();
  private readonly storageProjectCache = new Map<string, string>();
  private syncChain: Promise<PeerInboundRuntimeStatus> | null = null;
  private stopped = false;

  constructor(options: PeerTlsServerOptions) {
    this.options = options;
    this.policySubscriber = new PeerPolicyEventSubscriber({
      coreUrl: options.coreUrl,
      hostControlToken: options.hostControlToken,
      fetchImpl: options.fetchImpl,
      onPolicyEvent: (event) => this.handlePolicyEvent(event),
    });
    this.devServerTunnel = new PeerDevServerLeaseManager({
      getDynamicForbiddenPorts: () => this.collectDynamicForbiddenPorts(),
      allowedPorts: options.devServerAllowedPorts,
      verifyTargetReachable: options.devServerVerifyReachable,
    });
  }

  get isListening(): boolean {
    return Boolean(this.server && this.actualListenPort !== null);
  }

  get boundPort(): number | null {
    return this.actualListenPort;
  }

  get activeSessionCount(): number {
    let total = 0;
    for (const set of this.sessionsByController.values()) {
      total += set.size;
    }
    return total;
  }

  getRuntimeStatus(): PeerInboundRuntimeStatus {
    const effectivePort = this.actualListenPort ?? this.configuredPort;
    return {
      enabled: this.inboundEnabled,
      listening: this.isListening,
      displayName: this.displayName,
      configuredPort: this.configuredPort,
      actualListenPort: this.actualListenPort,
      portConflict: this.portConflict,
      localDeviceId: this.localDeviceId,
      localFingerprint: this.localFingerprint,
      reachableAddresses: this.inboundEnabled ? discoverReachablePeerAddresses(effectivePort) : [],
    };
  }

  /**
   * Synchronize local TLS identity & inbound switch from Core (`/api/v1/peers/internal/local-tls-material`).
   * Never listens on any port when `inbound_enabled === false`.
   */
  async start(): Promise<PeerInboundRuntimeStatus> {
    this.stopped = false;
    this.policySubscriber.start();
    return await this.syncFromCore();
  }

  async syncFromCore(): Promise<PeerInboundRuntimeStatus> {
    if (this.syncChain) {
      return await this.syncChain;
    }
    this.syncChain = this.doSyncFromCore().finally(() => {
      this.syncChain = null;
    });
    return await this.syncChain;
  }

  async stop(): Promise<void> {
    this.stopped = true;
    this.policySubscriber.stop();
    await this.stopListening('PeerTlsServer shutting down.');
  }

  /**
   * Handle a policy update / grant revocation / inbound toggle event synchronously (< 10ms).
   */
  handlePolicyEvent(event: PeerPolicyEvent): void {
    const kind = String(event.type ?? '').toLowerCase();
    const controllerId = (event.controller_device_id ?? event.controllerDeviceId ?? '').trim();

    if (kind === 'inbound_updated' || event.inbound_enabled !== undefined || event.inboundEnabled !== undefined) {
      const enabled = event.inbound_enabled ?? event.inboundEnabled;
      if (enabled === false) {
        this.inboundEnabled = false;
        void this.stopListening('Inbound peer access disabled.');
        return;
      }
      if (enabled === true && !this.stopped) {
        void this.syncFromCore();
      }
      return;
    }

    if (controllerId) {
      this.kickController(
        controllerId,
        kind === 'grant_revoked'
          ? 'Peer device grant was revoked by host.'
          : 'Peer access policy was updated; reconnect required.',
      );
    }
  }

  /**
   * Immediately destroy all mTLS sockets, in-flight HTTP/SSE streams, WS terminals,
   * and DevServer ALPN preview leases/sockets belonging to `controllerDeviceId`
   * (< 100ms SLA; executes synchronously in < 5ms).
   */
  kickController(controllerDeviceId: string, reason = 'Peer access policy changed.'): number {
    const revokedLeases = this.devServerTunnel.revokeControllerLeases(controllerDeviceId, reason);
    const sessions = this.sessionsByController.get(controllerDeviceId);
    if (!sessions || sessions.size === 0) return revokedLeases;

    const snapshot = [...sessions];
    this.sessionsByController.delete(controllerDeviceId);

    for (const session of snapshot) {
      this.teardownSession(session, 'policy_denied', reason);
    }
    return snapshot.length + revokedLeases;
  }

  private async doSyncFromCore(): Promise<PeerInboundRuntimeStatus> {
    const material = await this.fetchLocalTlsMaterial();
    if (!material) {
      await this.stopListening('Core TLS material unavailable.');
      return this.getRuntimeStatus();
    }

    this.inboundEnabled = Boolean(material.inbound_enabled);
    this.localDeviceId = material.local_device_id ?? '';
    this.displayName = material.display_name ?? '';
    this.localFingerprint = (material.local_fingerprint ?? '').trim().toLowerCase();

    const envPortRaw = process.env.TINADEC_PEER_LISTEN_PORT?.trim();
    const envPort = envPortRaw !== undefined && envPortRaw !== '' ? Number(envPortRaw) : undefined;
    const requestedPort =
      this.options.listenPortOverride !== undefined
        ? this.options.listenPortOverride
        : envPort !== undefined && Number.isFinite(envPort) && envPort >= 0
          ? envPort
          : material.listen_port > 0
            ? material.listen_port
            : DEFAULT_PEER_LISTEN_PORT;

    this.configuredPort = requestedPort;

    if (!this.inboundEnabled || this.stopped) {
      await this.stopListening('Inbound peer access is disabled.');
      return this.getRuntimeStatus();
    }

    if (!material.cert_pem || !material.private_key_pem) {
      await this.stopListening('Missing local TLS certificate or private key.');
      return this.getRuntimeStatus();
    }

    const certChanged = material.cert_pem !== this.certPem || material.private_key_pem !== this.privateKeyPem;
    const needRebind =
      !this.server ||
      this.actualListenPort === null ||
      certChanged ||
      (requestedPort !== 0 &&
        this.actualListenPort !== requestedPort &&
        !this.portConflict);

    if (needRebind) {
      await this.stopListening('Rebinding PeerTlsServer.');
      this.certPem = material.cert_pem;
      this.privateKeyPem = material.private_key_pem;
      await this.bindWithPortProbe(requestedPort, material.listen_port);
    }

    return this.getRuntimeStatus();
  }

  private async bindWithPortProbe(startPort: number, corePersistedPort: number): Promise<void> {
    const host = this.options.bindHost ?? '0.0.0.0';
    const maxOffset =
      startPort === 0
        ? 0
        : this.options.maxProbePortOffset ?? (MAX_PEER_LISTEN_PORT - DEFAULT_PEER_LISTEN_PORT);

    let lastError: unknown = null;
    for (let offset = 0; offset <= maxOffset; offset++) {
      const candidatePort = startPort === 0 ? 0 : startPort + offset;
      try {
        const { server, boundPort } = await this.tryListenOnce(host, candidatePort);
        this.server = server;
        this.actualListenPort = boundPort;
        this.portConflict = startPort !== 0 && boundPort !== startPort;

        if (boundPort !== corePersistedPort && startPort !== 0) {
          void this.notifyCoreSelectedListenPort(boundPort);
        }
        return;
      } catch (err) {
        lastError = err;
        const code = (err as NodeJS.ErrnoException)?.code;
        if (code === 'EADDRINUSE' && startPort !== 0 && offset < maxOffset) {
          continue;
        }
        throw err;
      }
    }

    throw lastError ?? new Error(`No available peer TLS port in range ${startPort}..${startPort + maxOffset}`);
  }

  private tryListenOnce(
    host: string,
    port: number,
  ): Promise<{ server: TlsServer; boundPort: number }> {
    return new Promise((resolve, reject) => {
      const server = tls.createServer(
        {
          cert: this.certPem,
          key: this.privateKeyPem,
          requestCert: true,
          rejectUnauthorized: false,
          ALPNProtocols: [...PEER_ALPN_PROTOCOLS],
        },
        (tlsSocket) => {
          void this.handleIncomingTlsConnection(tlsSocket);
        },
      );

      const onStartupError = (err: Error) => {
        server.removeListener('listening', onListening);
        try {
          server.close();
        } catch {
          // ignore
        }
        reject(err);
      };

      const onListening = () => {
        server.removeListener('error', onStartupError);
        const addr = server.address();
        const boundPort = typeof addr === 'object' && addr ? addr.port : port;
        resolve({ server, boundPort });
      };

      server.on('connection', (rawSocket) => {
        this.rawSockets.add(rawSocket);
        rawSocket.once('close', () => {
          this.rawSockets.delete(rawSocket);
        });
      });

      server.once('error', onStartupError);
      server.once('listening', onListening);
      server.listen(port, host);
    });
  }

  private async stopListening(reason: string): Promise<void> {
    const closingPromises: Promise<void>[] = [];
    for (const tlsSock of this.activeTlsSockets) {
      if (!tlsSock.destroyed) {
        closingPromises.push(
          new Promise<void>((resolve) => {
            const t = setTimeout(resolve, 40);
            t.unref?.();
            tlsSock.once('close', () => {
              clearTimeout(t);
              resolve();
            });
          }),
        );
      }
    }

    this.devServerTunnel.revokeAll(reason);

    for (const sock of this.pendingSockets) {
      try {
        sock.destroy();
      } catch {
        // ignore
      }
    }
    this.pendingSockets.clear();

    for (const [, sessions] of this.sessionsByController) {
      for (const session of sessions) {
        this.teardownSession(session, 'inbound_disabled', reason);
      }
    }
    this.sessionsByController.clear();

    if (closingPromises.length > 0) {
      await Promise.all(closingPromises);
    }
    this.activeTlsSockets.clear();

    // Only destroy raw TCP sockets that never completed TLS upgrade (never double-destroy TLSSocket backing handle).
    for (const rawSock of this.rawSockets) {
      if (!rawSock.destroyed) {
        try {
          rawSock.destroy();
        } catch {
          // ignore
        }
      }
    }
    this.rawSockets.clear();

    const srv = this.server;
    this.server = null;
    this.actualListenPort = null;
    this.portConflict = false;

    if (srv) {
      await new Promise<void>((resolve) => {
        const fallback = setTimeout(() => resolve(), 50);
        try {
          srv.close(() => {
            clearTimeout(fallback);
            resolve();
          });
        } catch {
          clearTimeout(fallback);
          resolve();
        }
      });
    }
  }

  private teardownSession(session: ActivePeerSession, code: PeerErrorCode, reason: string): void {
    try {
      session.connection.sendFrame({ type: 'peer.disconnect', code, reason });
    } catch {
      // ignore write failure during immediate teardown
    }

    for (const stream of session.inFlightHttp.values()) {
      stream.abort.abort();
      try {
        stream.bodyStreamController?.close();
      } catch {
        // ignore
      }
    }
    session.inFlightHttp.clear();

    for (const term of session.activeTerminals.values()) {
      try {
        term.close();
      } catch {
        // ignore
      }
    }
    session.activeTerminals.clear();

    session.connection.destroy(new PeerProtocolError(code, reason));
  }

  private async handleIncomingTlsConnection(tlsSocket: TLSSocket): Promise<void> {
    if (!this.inboundEnabled || this.stopped) {
      tlsSocket.destroy();
      return;
    }

    this.activeTlsSockets.add(tlsSocket);
    tlsSocket.once('close', () => {
      this.activeTlsSockets.delete(tlsSocket);
    });

    if (tlsSocket.alpnProtocol === PEER_ALPN_DEVSERVER) {
      this.pendingSockets.add(tlsSocket);
      tlsSocket.once('close', () => {
        this.pendingSockets.delete(tlsSocket);
      });
      try {
        await this.devServerTunnel.handleIncomingDevServerSocket({
          tlsSocket,
          isInboundEnabled: () => this.inboundEnabled && !this.stopped,
          verifyControllerGrant: (input) =>
            this.verifyHandshakeWithCore({
              transport: 'lan',
              controllerDeviceId: input.controllerDeviceId,
              displayName: input.controllerDeviceId,
              remoteFingerprint: input.clientFingerprint,
              protocolMajor: PEER_PROTOCOL_MAJOR,
              protocolMinor: PEER_PROTOCOL_MINOR,
              pairingMode: 'grant',
            }),
          handshakeTimeoutMs: this.options.handshakeTimeoutMs,
        });
      } finally {
        this.pendingSockets.delete(tlsSocket);
      }
      return;
    }

    this.pendingSockets.add(tlsSocket);
    tlsSocket.once('close', () => {
      this.pendingSockets.delete(tlsSocket);
    });

    const connection = new PeerFrameConnection(tlsSocket, {
      heartbeatIntervalMs: this.options.heartbeatIntervalMs,
      heartbeatTimeoutMs: this.options.heartbeatTimeoutMs,
    });

    try {
      const handshake = await performServerHandshake({
        connection,
        tlsSocket,
        localDeviceId: this.localDeviceId,
        localDeviceName: this.displayName,
        localFingerprint: this.localFingerprint,
        isInboundEnabled: () => this.inboundEnabled && !this.stopped,
        verifyHandshakeWithCore: (input) => this.verifyHandshakeWithCore(input),
        handshakeTimeoutMs: this.options.handshakeTimeoutMs,
      });

      this.pendingSockets.delete(tlsSocket);

      if (handshake.mode === 'probe') {
        setTimeout(() => {
          connection.destroy();
        }, 20);
        return;
      }

      const session: ActivePeerSession = {
        sessionId: crypto.randomUUID(),
        tlsSocket,
        connection,
        controller: {
          controllerDeviceId: handshake.controllerDeviceId,
          displayName: handshake.displayName,
          clientFingerprint: handshake.clientFingerprint,
          policy: handshake.policy,
        },
        inFlightHttp: new Map(),
        activeTerminals: new Map(),
      };

      let group = this.sessionsByController.get(handshake.controllerDeviceId);
      if (!group) {
        group = new Set();
        this.sessionsByController.set(handshake.controllerDeviceId, group);
      }
      group.add(session);

      connection.onClose(() => {
        const currentGroup = this.sessionsByController.get(handshake.controllerDeviceId);
        if (currentGroup) {
          currentGroup.delete(session);
          if (currentGroup.size === 0) {
            this.sessionsByController.delete(handshake.controllerDeviceId);
          }
        }
        for (const stream of session.inFlightHttp.values()) {
          stream.abort.abort();
        }
        session.inFlightHttp.clear();
        for (const term of session.activeTerminals.values()) {
          term.close();
        }
        session.activeTerminals.clear();
      });

      connection.startSteadyState((frame) => {
        this.handleMultiplexedFrame(session, frame);
      });
    } catch {
      this.pendingSockets.delete(tlsSocket);
      connection.destroy();
    }
  }

  private handleMultiplexedFrame(session: ActivePeerSession, frame: DecodedPeerFrame): void {
    const h = frame.header;
    switch (h.type) {
      case 'http.req':
        this.handleHttpReqStart(session, h);
        return;
      case 'http.req.chunk':
        this.handleHttpReqChunk(session, h.streamId, frame.payload);
        return;
      case 'http.req.end':
        this.handleHttpReqEnd(session, h.streamId);
        return;
      case 'http.req.abort': {
        const inFlight = session.inFlightHttp.get(h.streamId);
        if (inFlight) {
          session.inFlightHttp.delete(h.streamId);
          inFlight.abort.abort();
          try {
            inFlight.bodyStreamController?.close();
          } catch {
            // ignore
          }
        }
        return;
      }
      case 'ws.open':
        this.handleWsOpen(session, h);
        return;
      case 'ws.frame': {
        const term = session.activeTerminals.get(h.streamId);
        if (!term) return;
        if (h.isBinary && frame.payload) {
          term.onMessage(frame.payload);
        } else if (typeof h.text === 'string') {
          term.onMessage(h.text);
        } else if (frame.payload) {
          term.onMessage(frame.payload);
        }
        return;
      }
      case 'ws.close': {
        const term = session.activeTerminals.get(h.streamId);
        if (term) {
          session.activeTerminals.delete(h.streamId);
          term.close();
        }
        return;
      }
      default:
        return;
    }
  }

  private handleHttpReqStart(session: ActivePeerSession, req: PeerHttpReqFrame): void {
    const contentType = this.getHeader(req.headers, 'content-type') ?? '';
    const isJsonBody =
      req.hasBody &&
      (contentType.toLowerCase().includes('application/json') ||
        /^\/api\/v1\/peers\/internal\/devserver-lease(?:\/|$)/i.test(req.path));

    // Fast fail-closed policy pre-check before reading any body bytes (unless observer POST tool execute needs bodyJson).
    const mightNeedJsonForPolicy =
      isJsonBody &&
      ((session.controller.policy.role === 'observer' &&
        /^\/api\/v1\/(?:code\/|tool-runtime\/)?tools\/[^/]+\/execute$/i.test(req.path)) ||
        session.controller.policy.workspace_mode === 'selected');

    if (!mightNeedJsonForPolicy && !req.hasBody) {
      const abort = new AbortController();
      const entry: InFlightHttpStream = {
        streamId: req.streamId,
        reqFrame: req,
        abort,
        isJsonBuffered: false,
        jsonChunks: [],
        jsonByteLength: 0,
        bodyStreamController: null,
        startedCoreFetch: true,
      };
      session.inFlightHttp.set(req.streamId, entry);
      void this.executeGuardedHttpToCore(session, entry, null, undefined);
      return;
    }

    if (isJsonBody) {
      const entry: InFlightHttpStream = {
        streamId: req.streamId,
        reqFrame: req,
        abort: new AbortController(),
        isJsonBuffered: true,
        jsonChunks: [],
        jsonByteLength: 0,
        bodyStreamController: null,
        startedCoreFetch: false,
      };
      session.inFlightHttp.set(req.streamId, entry);
      return;
    }

    // Streaming binary request body.
    let streamCtrl: ReadableStreamDefaultController<Uint8Array> | null = null;
    const bodyStream = new ReadableStream<Uint8Array>({
      start(controller) {
        streamCtrl = controller;
      },
    });
    const entry: InFlightHttpStream = {
      streamId: req.streamId,
      reqFrame: req,
      abort: new AbortController(),
      isJsonBuffered: false,
      jsonChunks: [],
      jsonByteLength: 0,
      bodyStreamController: streamCtrl,
      startedCoreFetch: true,
    };
    session.inFlightHttp.set(req.streamId, entry);
    void this.executeGuardedHttpToCore(session, entry, null, bodyStream);
  }

  private handleHttpReqChunk(
    session: ActivePeerSession,
    streamId: string,
    payload: Uint8Array | undefined,
  ): void {
    const entry = session.inFlightHttp.get(streamId);
    if (!entry || !payload || payload.byteLength === 0) return;

    if (entry.isJsonBuffered) {
      entry.jsonByteLength += payload.byteLength;
      if (entry.jsonByteLength > MAX_BUFFERED_JSON_BODY_BYTES) {
        session.inFlightHttp.delete(streamId);
        this.sendHttpProblemResponse(
          session.connection,
          streamId,
          413,
          'invalid_request',
          'JSON request payload exceeds 2 MB limit.',
          entry.reqFrame.path,
        );
        return;
      }
      entry.jsonChunks.push(Buffer.from(payload.buffer, payload.byteOffset, payload.byteLength));
      return;
    }

    try {
      entry.bodyStreamController?.enqueue(
        new Uint8Array(payload.buffer.slice(payload.byteOffset, payload.byteOffset + payload.byteLength)),
      );
    } catch {
      // Stream already closed.
    }
  }

  private handleHttpReqEnd(session: ActivePeerSession, streamId: string): void {
    const entry = session.inFlightHttp.get(streamId);
    if (!entry) return;

    if (entry.isJsonBuffered && !entry.startedCoreFetch) {
      entry.startedCoreFetch = true;
      const rawBuf = Buffer.concat(entry.jsonChunks, entry.jsonByteLength);
      entry.jsonChunks = [];
      let parsedBody: Record<string, unknown> | null = null;
      if (rawBuf.byteLength > 0) {
        try {
          const parsed = JSON.parse(rawBuf.toString('utf8')) as unknown;
          if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
            parsedBody = parsed as Record<string, unknown>;
          }
        } catch {
          session.inFlightHttp.delete(streamId);
          this.sendHttpProblemResponse(
            session.connection,
            streamId,
            400,
            'invalid_request',
            'Malformed JSON request body.',
            entry.reqFrame.path,
          );
          return;
        }
      }
      void this.executeGuardedHttpToCore(session, entry, parsedBody, rawBuf);
      return;
    }

    try {
      entry.bodyStreamController?.close();
    } catch {
      // ignore
    }
  }

  private async executeGuardedHttpToCore(
    session: ActivePeerSession,
    entry: InFlightHttpStream,
    bodyJson: Record<string, unknown> | null,
    rawBody: Buffer | ReadableStream<Uint8Array> | undefined,
  ): Promise<void> {
    const { reqFrame, abort, streamId } = entry;
    try {
      const needSessionLookup =
        session.controller.policy.workspace_mode === 'selected' ||
        !toBoolFlag(session.controller.policy.sync_all_sessions, false);

      let sessionMeta: SessionScopeMetadata | null = null;
      if (needSessionLookup) {
        const targetSessionId = extractTargetSessionId(reqFrame.path, reqFrame.query);
        if (targetSessionId) {
          sessionMeta = await this.resolveSessionScopeMetadata(targetSessionId, abort.signal);
        }
        if (
          session.controller.policy.workspace_mode === 'selected' &&
          this.storageProjectCache.size === 0
        ) {
          await this.refreshStorageProjectCache(abort.signal);
        }
      }

      const decision = evaluatePeerPolicy({
        kind: 'http',
        method: reqFrame.method,
        path: reqFrame.path,
        query: reqFrame.query,
        headers: reqFrame.headers,
        bodyJson,
        controller: session.controller,
        resolveStorageProjectId: (sid) => this.storageProjectCache.get(sid),
        sessionMeta,
      });

      if (!decision.allowed) {
        session.inFlightHttp.delete(streamId);
        this.sendHttpProblemResponse(
          session.connection,
          streamId,
          decision.status,
          decision.code,
          decision.message,
          reqFrame.path,
        );
        return;
      }

      const trimmedReqPath = reqFrame.path.replace(/\/+$/, '');
      const lowerReqPath = trimmedReqPath.toLowerCase();
      if (
        lowerReqPath === '/api/v1/peers/internal/devserver-lease' ||
        lowerReqPath.startsWith('/api/v1/peers/internal/devserver-lease/')
      ) {
        const leaseResult = await this.devServerTunnel.handleInternalLeaseHttpRequest(
          session.controller,
          reqFrame.method,
          trimmedReqPath,
          bodyJson,
        );
        session.inFlightHttp.delete(streamId);
        if (!leaseResult.ok) {
          this.sendHttpProblemResponse(
            session.connection,
            streamId,
            leaseResult.status,
            leaseResult.code,
            leaseResult.detail,
            reqFrame.path,
          );
          return;
        }
        const outBytes = Buffer.from(JSON.stringify(leaseResult.body), 'utf8');
        session.connection.sendFrame({
          type: 'http.res.head',
          streamId,
          status: leaseResult.status,
          headers: { 'content-type': 'application/json; charset=utf-8' },
        });
        session.connection.sendFrame({ type: 'http.res.chunk', streamId }, outBytes);
        session.connection.sendFrame({ type: 'http.res.end', streamId });
        return;
      }

      const fetchImpl = this.options.fetchImpl ?? fetch;
      const forwardHeaders = buildSanitizedPeerCoreHeaders(
        reqFrame.headers,
        session.controller,
        this.options.hostControlToken?.(),
      );

      const querySuffix = reqFrame.query
        ? reqFrame.query.startsWith('?')
          ? reqFrame.query
          : `?${reqFrame.query}`
        : '';
      const targetUrl = new URL(`${reqFrame.path}${querySuffix}`, this.options.coreUrl()).toString();

      const response = await fetchImpl(targetUrl, {
        method: reqFrame.method,
        headers: forwardHeaders,
        body:
          reqFrame.method === 'GET' || reqFrame.method === 'HEAD'
            ? undefined
            : (rawBody as BodyInit | undefined),
        signal: abort.signal,
        redirect: 'manual',
        // @ts-expect-error Bun duplex streaming option
        duplex: rawBody instanceof ReadableStream ? 'half' : undefined,
      });

      if (abort.signal.aborted || session.connection.isClosed) {
        return;
      }

      const resHeaders: Record<string, string> = {};
      response.headers.forEach((value, key) => {
        const lower = key.toLowerCase();
        if (!RESPONSE_HOP_BY_HOP_HEADERS.has(lower)) {
          resHeaders[lower] = value;
        }
      });

      const contentType = (response.headers.get('content-type') ?? '').toLowerCase();
      const normPath = reqFrame.path.replace(/\/+$/, '').toLowerCase();

      // Track newly created sessions so sync_all_sessions=0 knows this peer initiated it.
      if (
        reqFrame.method.toUpperCase() === 'POST' &&
        normPath === '/api/v1/sessions' &&
        response.status >= 200 &&
        response.status < 300 &&
        contentType.includes('application/json')
      ) {
        const text = await response.text();
        try {
          const created = JSON.parse(text) as Record<string, unknown>;
          const createdId = String(created.id ?? created.session_id ?? created.sessionId ?? '').trim();
          if (createdId) {
            this.knownSessionInitiators.set(createdId, session.controller.controllerDeviceId);
          }
        } catch {
          // ignore
        }
        session.connection.sendFrame({
          type: 'http.res.head',
          streamId,
          status: response.status,
          headers: resHeaders,
        });
        if (text.length > 0) {
          session.connection.sendFrame({ type: 'http.res.chunk', streamId }, Buffer.from(text, 'utf8'));
        }
        session.connection.sendFrame({ type: 'http.res.end', streamId });
        return;
      }

      // Filter list responses (`/api/v1/projects`, `/api/v1/sessions`, `/api/v1/storage/scopes`) per policy.
      if (
        reqFrame.method.toUpperCase() === 'GET' &&
        response.status >= 200 &&
        response.status < 300 &&
        contentType.includes('application/json') &&
        (normPath === '/api/v1/projects' ||
          normPath === '/api/v1/sessions' ||
          normPath === '/api/v1/storage/scopes')
      ) {
        const text = await response.text();
        let outBytes = Buffer.from(text, 'utf8');
        try {
          const parsed = JSON.parse(text) as unknown;
          const filtered = filterPeerCollectionResponse(
            normPath,
            parsed,
            session.controller,
            this.knownSessionInitiators,
          );
          outBytes = Buffer.from(JSON.stringify(filtered), 'utf8');
        } catch {
          // pass raw JSON if unparseable
        }
        session.connection.sendFrame({
          type: 'http.res.head',
          streamId,
          status: response.status,
          headers: resHeaders,
        });
        if (outBytes.byteLength > 0) {
          session.connection.sendFrame({ type: 'http.res.chunk', streamId }, outBytes);
        }
        session.connection.sendFrame({ type: 'http.res.end', streamId });
        return;
      }

      // Stream response (SSE `text/event-stream`, attachments, or standard JSON).
      session.connection.sendFrame({
        type: 'http.res.head',
        streamId,
        status: response.status,
        headers: resHeaders,
      });

      if (response.body) {
        const reader = response.body.getReader();
        try {
          while (!abort.signal.aborted && !session.connection.isClosed) {
            const { done, value } = await reader.read();
            if (done) break;
            if (value && value.byteLength > 0) {
              // Slice into <= 1 MB chunks so no single chunk ever approaches the 32 MB frame cap.
              const maxChunk = 1024 * 1024;
              for (let off = 0; off < value.byteLength; off += maxChunk) {
                const slice = value.subarray(off, Math.min(off + maxChunk, value.byteLength));
                session.connection.sendFrame({ type: 'http.res.chunk', streamId }, slice);
              }
            }
          }
        } finally {
          try {
            reader.releaseLock();
          } catch {
            // ignore
          }
        }
      }

      if (!abort.signal.aborted && !session.connection.isClosed) {
        session.connection.sendFrame({ type: 'http.res.end', streamId });
      }
    } catch (err) {
      if (abort.signal.aborted || session.connection.isClosed) return;
      const message = err instanceof Error ? err.message : 'Upstream Core request failed.';
      this.sendHttpProblemResponse(
        session.connection,
        streamId,
        502,
        'unreachable',
        message,
        reqFrame.path,
      );
    } finally {
      session.inFlightHttp.delete(streamId);
    }
  }

  private handleWsOpen(session: ActivePeerSession, openFrame: PeerWsOpenFrame): void {
    const decision = evaluatePeerPolicy({
      kind: 'ws',
      method: 'GET',
      path: openFrame.path || '/ws/terminal',
      query: openFrame.query,
      headers: openFrame.headers,
      controller: session.controller,
      resolveStorageProjectId: (sid) => this.storageProjectCache.get(sid),
    });

    if (!decision.allowed) {
      try {
        session.connection.sendFrame({
          type: 'ws.open-reject',
          streamId: openFrame.streamId,
          status: decision.status,
          code: decision.code,
          message: decision.message,
        });
      } catch {
        // ignore
      }
      return;
    }

    try {
      session.connection.sendFrame({
        type: 'ws.open-ack',
        streamId: openFrame.streamId,
      });
    } catch {
      return;
    }

    const search = new URLSearchParams(openFrame.query ? openFrame.query.replace(/^\?/, '') : '');
    const terminalSessionId = search.get('terminal_session_id')?.trim();

    const virtualClient: WsTerminalClient = {
      send: (data: string) => {
        if (session.connection.isClosed) return;
        try {
          session.connection.sendFrame({
            type: 'ws.frame',
            streamId: openFrame.streamId,
            text: data,
          });
        } catch {
          // ignore
        }
      },
      close: (code?: number, reason?: string) => {
        session.activeTerminals.delete(openFrame.streamId);
        if (session.connection.isClosed) return;
        try {
          session.connection.sendFrame({
            type: 'ws.close',
            streamId: openFrame.streamId,
            code,
            reason,
          });
        } catch {
          // ignore
        }
      },
    };

    const createAgentTerm = this.options.createAgentTerminal ?? createAgentTerminalBridgeSession;
    const createShellTerm = this.options.createInteractiveShell ?? createInteractiveShellSession;

    const termSession = terminalSessionId
      ? createAgentTerm({
          ws: virtualClient,
          terminalSessionId,
          runId: search.get('run_id'),
          forwardHeaders: buildSanitizedPeerCoreHeaders(
            openFrame.headers,
            session.controller,
            this.options.hostControlToken?.(),
          ),
          fetchImpl: this.options.fetchImpl,
        })
      : createShellTerm({
          ws: virtualClient,
          shellId: search.get('shell'),
          cwd: search.get('cwd'),
        });

    session.activeTerminals.set(openFrame.streamId, termSession);
  }

  private sendHttpProblemResponse(
    connection: PeerFrameConnection,
    streamId: string,
    status: number,
    code: string,
    detail: string,
    instance: string,
  ): void {
    if (connection.isClosed) return;
    const problem = toProblemDetails(status, code, detail, instance, ensureRequestId(null));
    const payload = Buffer.from(JSON.stringify(problem), 'utf8');
    try {
      connection.sendFrame({
        type: 'http.res.head',
        streamId,
        status,
        headers: {
          'content-type': 'application/problem+json',
        },
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
    const token = (this.options.hostControlToken?.() ?? process.env.TINADEC_HOST_CONTROL_TOKEN ?? '').trim();
    if (token) {
      headers['x-tinadec-host-control'] = token;
    }
    return headers;
  }

  private async fetchLocalTlsMaterial(): Promise<LocalTlsMaterialDto | null> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    try {
      const url = new URL('/api/v1/peers/internal/local-tls-material', this.options.coreUrl()).toString();
      const res = await fetchImpl(url, {
        headers: this.buildInternalCoreHeaders(),
      });
      if (!res.ok) return null;
      return (await res.json()) as LocalTlsMaterialDto;
    } catch {
      return null;
    }
  }

  private async notifyCoreSelectedListenPort(boundPort: number): Promise<void> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    try {
      const url = new URL('/api/v1/peers/inbound', this.options.coreUrl()).toString();
      await fetchImpl(url, {
        method: 'PUT',
        headers: {
          ...this.buildInternalCoreHeaders(),
          'content-type': 'application/json',
        },
        body: JSON.stringify({ listen_port: boundPort }),
      });
    } catch {
      // Best-effort sync back to Core.
    }
  }

  private async verifyHandshakeWithCore(
    input: CoreHandshakeVerifyInput,
  ): Promise<CoreHandshakeVerifyDecision> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    const url = new URL('/api/v1/peers/internal/handshake/verify', this.options.coreUrl()).toString();
    let res: Response;
    try {
      res = await fetchImpl(url, {
        method: 'POST',
        headers: {
          ...this.buildInternalCoreHeaders(),
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          transport: input.transport,
          controller_device_id: input.controllerDeviceId,
          display_name: input.displayName,
          remote_fingerprint: input.remoteFingerprint,
          protocol_major: input.protocolMajor,
          protocol_minor: input.protocolMinor,
          pairing_mode: input.pairingMode,
          ...(input.pairingCode ? { pairing_code: input.pairingCode } : {}),
          ...(input.expectedRemoteFingerprint
            ? { expected_remote_fingerprint: input.expectedRemoteFingerprint }
            : {}),
        }),
      });
    } catch (err) {
      return {
        accepted: false,
        code: 'handshake_failed',
        message: err instanceof Error ? err.message : 'Core handshake verification unreachable.',
      };
    }

    let body: Record<string, unknown> = {};
    try {
      body = (await res.json()) as Record<string, unknown>;
    } catch {
      body = {};
    }

    if (!res.ok || body.accepted === false) {
      const rawCode = typeof body.code === 'string' ? body.code : typeof body.title === 'string' ? body.title : '';
      const code: PeerErrorCode = isPeerErrorCode(rawCode) ? rawCode : 'unauthorized';
      const message =
        typeof body.detail === 'string'
          ? body.detail
          : typeof body.message === 'string'
            ? body.message
            : 'Handshake rejected by Core.';
      return {
        accepted: false,
        code,
        message,
      };
    }

    const policy = (body.policy ?? {}) as PeerAccessPolicy;
    return {
      accepted: true,
      grantId: typeof body.grant_id === 'string' ? body.grant_id : undefined,
      controllerDeviceId:
        typeof body.controller_device_id === 'string'
          ? body.controller_device_id
          : input.controllerDeviceId,
      displayName: typeof body.display_name === 'string' ? body.display_name : input.displayName,
      policyRevision:
        typeof body.policy_revision === 'string'
          ? body.policy_revision
          : policy.revision ?? '1',
      policy,
    };
  }

  private async resolveSessionScopeMetadata(
    sessionId: string,
    signal: AbortSignal,
  ): Promise<SessionScopeMetadata | null> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    try {
      const url = new URL('/api/v1/sessions', this.options.coreUrl()).toString();
      const res = await fetchImpl(url, {
        headers: {
          ...this.buildInternalCoreHeaders(),
          'x-tinadec-principal': 'dev@local',
        },
        signal,
      });
      if (!res.ok) return null;
      const list = (await res.json()) as unknown;
      if (!Array.isArray(list)) return null;
      for (const item of list) {
        if (!item || typeof item !== 'object') continue;
        const rec = item as Record<string, unknown>;
        const id = String(rec.id ?? rec.session_id ?? rec.sessionId ?? '').trim();
        if (id !== sessionId) continue;
        const projectRaw = rec.project_id ?? rec.projectId ?? null;
        const projectId = typeof projectRaw === 'string' && projectRaw.trim() ? projectRaw.trim() : null;
        const initRaw =
          rec.initiator_device_id ??
          rec.initiatorDeviceId ??
          rec.origin_device_id ??
          rec.originDeviceId ??
          rec.created_by_device_id;
        const initiatorDeviceId =
          typeof initRaw === 'string' && initRaw.trim()
            ? initRaw.trim()
            : this.knownSessionInitiators.get(sessionId) ?? null;
        return {
          sessionId,
          projectId,
          initiatorDeviceId,
        };
      }
      return null;
    } catch {
      return null;
    }
  }

  private async refreshStorageProjectCache(signal: AbortSignal): Promise<void> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    try {
      const url = new URL('/api/v1/storage/scopes', this.options.coreUrl()).toString();
      const res = await fetchImpl(url, {
        headers: {
          ...this.buildInternalCoreHeaders(),
          'x-tinadec-principal': 'dev@local',
        },
        signal,
      });
      if (!res.ok) return;
      const list = (await res.json()) as unknown;
      if (!Array.isArray(list)) return;
      for (const item of list) {
        if (!item || typeof item !== 'object') continue;
        const rec = item as Record<string, unknown>;
        const sid = String(rec.storage_id ?? rec.storageId ?? '').trim();
        const pid = String(rec.project_id ?? rec.projectId ?? '').trim();
        if (sid && pid) {
          this.storageProjectCache.set(sid, pid);
        }
      }
    } catch {
      // ignore
    }
  }

  private collectDynamicForbiddenPorts(): Set<number> {
    const ports = new Set<number>(this.options.devServerForbiddenPorts ?? []);
    if (this.actualListenPort && this.actualListenPort > 0) {
      ports.add(this.actualListenPort);
    }
    if (this.configuredPort > 0) {
      ports.add(this.configuredPort);
    }
    const addPortFromUrl = (rawUrl: string | undefined) => {
      if (!rawUrl) return;
      try {
        const parsed = new URL(rawUrl);
        const port = Number(parsed.port);
        if (Number.isInteger(port) && port > 0) {
          ports.add(port);
        }
      } catch {
        // ignore invalid URL
      }
    };
    addPortFromUrl(this.options.coreUrl());
    addPortFromUrl(process.env.TINADEC_TOOL_RUNTIME_URL);
    const gwPort = Number(process.env.TINADEC_GATEWAY_PORT || process.env.PORT || '');
    if (Number.isInteger(gwPort) && gwPort > 0) {
      ports.add(gwPort);
    }
    return ports;
  }

  private getHeader(headers: Record<string, string> | undefined, name: string): string | undefined {
    if (!headers) return undefined;
    const lower = name.toLowerCase();
    for (const [k, v] of Object.entries(headers)) {
      if (k.toLowerCase() === lower) return v;
    }
    return undefined;
  }
}
