/**
 * Controller-side outbound mTLS connection pool (`PeerClientPool`):
 * - Executes `probe` (TLS DER SHA-256 fingerprint inspection without burning pairing code),
 *   `pair` (4-step `code` mTLS handshake + Core persistence), and `connect` (`grant` reconnect).
 * - Multiplexes concurrent HTTP, SSE, and WebSocket terminal streams over a single
 *   persistent mTLS socket per remote device.
 * - Immediately fails/closes all in-flight streams when a peer disconnects or revokes access.
 */

import tls, { type TLSSocket } from 'node:tls';
import { ensureRequestId } from '../headers.js';
import type { WsTerminalClient } from '../wsTerminalProxy.js';
import { performClientHandshake } from './peerHandshake.js';
import {
  DEFAULT_PEER_LISTEN_PORT,
  type DecodedPeerFrame,
  generateSelfSignedP256TlsMaterial,
  PEER_ALPN_HTTP,
  type PeerErrorCode,
  PeerFrameConnection,
  PeerProtocolError,
} from './peerProtocol.js';
import type { LocalTlsMaterialDto } from './peerTlsServer.js';

export type PeerConnectionState =
  | 'disconnected'
  | 'connecting'
  | 'handshaking'
  | 'ready'
  | 'version_incompatible'
  | 'unauthorized'
  | 'fingerprint_mismatch'
  | 'error';

export interface PeerDeviceRecord {
  device_id: string;
  display_name: string;
  transport: 'lan' | 'relay';
  address: string;
  remote_device_id: string;
  certificate_fingerprint: string;
  protocol_major: number;
  protocol_minor: number;
  paired_at: number;
  last_connected_at: number | null;
  connection_desired: boolean;
  adopt_sessions: boolean;
  last_state: PeerConnectionState;
  last_failure_code: string | null;
  access_revision: string | null;
}

export interface PeerProbeResultDto {
  reachable: boolean;
  address: string;
  remote_device_id: string;
  display_name: string;
  certificate_fingerprint: string;
  protocol_major: number;
  protocol_minor: number;
  capabilities: string[];
}

export interface PairPeerDeviceInput {
  address: string;
  pairing_code: string;
  expected_fingerprint: string;
  display_name?: string;
  connection_desired?: boolean;
  adopt_sessions?: boolean;
}

export interface ProxyHttpOverTunnelOptions {
  method: string;
  path: string;
  query?: string;
  headers?: Record<string, string>;
  body?: ReadableStream<Uint8Array> | Uint8Array | Buffer | string | null;
  signal?: AbortSignal;
}

export interface PeerTunnelWsChannel {
  sendText(text: string): void;
  sendBinary(bytes: Uint8Array): void;
  close(code?: number, reason?: string): void;
}

export interface PeerClientPoolOptions {
  coreUrl: () => string;
  fetchImpl?: typeof fetch;
  hostControlToken?: () => string | undefined;
  connectTimeoutMs?: number;
  handshakeTimeoutMs?: number;
  heartbeatIntervalMs?: number;
  heartbeatTimeoutMs?: number;
  reconnectBaseDelayMs?: number;
}

interface PendingHttpClientStream {
  streamId: string;
  headResolved: boolean;
  resolveHead: (response: Response) => void;
  rejectHead: (error: Error) => void;
  bodyController: ReadableStreamDefaultController<Uint8Array> | null;
  cleanupAbort?: () => void;
}

interface PendingWsClientStream {
  streamId: string;
  opened: boolean;
  resolveOpen: (channel: PeerTunnelWsChannel) => void;
  rejectOpen: (error: Error) => void;
  downstream: WsTerminalClient;
}

interface PooledPeerEntry {
  deviceId: string;
  address: string;
  pinnedFingerprint: string;
  connectionDesired: boolean;
  state: PeerConnectionState;
  lastFailureCode: string | null;
  accessRevision: string | null;
  lastConnectedAt: number | null;
  connection: PeerFrameConnection | null;
  connectingPromise: Promise<PooledPeerEntry> | null;
  reconnectTimer: ReturnType<typeof setTimeout> | null;
  reconnectAttempt: number;
  httpStreams: Map<string, PendingHttpClientStream>;
  wsStreams: Map<string, PendingWsClientStream>;
}

export function parsePeerAddress(rawAddress: string): { host: string; port: number; normalized: string } {
  const trimmed = rawAddress.trim();
  if (!trimmed) {
    throw new PeerProtocolError('unreachable', 'Peer address is empty.');
  }

  let host = '';
  let port = DEFAULT_PEER_LISTEN_PORT;

  if (trimmed.startsWith('[')) {
    const closeIdx = trimmed.indexOf(']');
    if (closeIdx <= 1) {
      throw new PeerProtocolError('unreachable', `Malformed IPv6 peer address '${rawAddress}'.`);
    }
    host = trimmed.slice(1, closeIdx);
    const rest = trimmed.slice(closeIdx + 1);
    if (rest.startsWith(':')) {
      port = Number(rest.slice(1));
    }
  } else {
    const colonIdx = trimmed.lastIndexOf(':');
    if (colonIdx > 0 && trimmed.indexOf(':') === colonIdx) {
      host = trimmed.slice(0, colonIdx).trim();
      port = Number(trimmed.slice(colonIdx + 1).trim());
    } else {
      host = trimmed;
    }
  }

  if (!host || !Number.isInteger(port) || port < 1 || port > 65535) {
    throw new PeerProtocolError('unreachable', `Invalid peer host:port '${rawAddress}'.`);
  }

  const normalized = host.includes(':') ? `[${host}]:${port}` : `${host}:${port}`;
  return { host, port, normalized };
}

function mapErrorToConnectionState(err: unknown): {
  state: PeerConnectionState;
  code: PeerErrorCode;
} {
  if (err instanceof PeerProtocolError) {
    switch (err.code) {
      case 'fingerprint_mismatch':
        return { state: 'fingerprint_mismatch', code: err.code };
      case 'unauthorized':
      case 'pairing_code_rejected':
      case 'pairing_code_expired':
      case 'inbound_disabled':
      case 'policy_denied':
        return { state: 'unauthorized', code: err.code };
      case 'protocol_major_mismatch':
        return { state: 'version_incompatible', code: err.code };
      default:
        return { state: 'error', code: err.code };
    }
  }
  return { state: 'error', code: 'unreachable' };
}

export class PeerClientPool {
  private readonly options: PeerClientPoolOptions;
  private readonly entries = new Map<string, PooledPeerEntry>();
  private fallbackIdentity: {
    deviceId: string;
    displayName: string;
    certPem: string;
    privateKeyPem: string;
  } | null = null;
  private stopped = false;

  constructor(options: PeerClientPoolOptions) {
    this.options = options;
  }

  /**
   * Startup sync: read paired devices with `connection_desired === true` from Core and
   * initiate background `grant` reconnects.
   */
  async start(): Promise<void> {
    this.stopped = false;
    const devices = await this.fetchDevicesFromCore();
    for (const dev of devices) {
      this.upsertMetadata(dev);
      if (dev.connection_desired) {
        void this.connectDevice(dev.device_id, dev).catch(() => {
          // Background startup reconnect failures are recorded in controller-state.
        });
      }
    }
  }

  async stop(): Promise<void> {
    this.stopped = true;
    for (const entry of this.entries.values()) {
      if (entry.reconnectTimer) {
        clearTimeout(entry.reconnectTimer);
        entry.reconnectTimer = null;
      }
      entry.connectionDesired = false;
      entry.connection?.destroy();
      entry.connection = null;
      entry.state = 'disconnected';
    }
    this.entries.clear();
  }

  getLiveState(deviceId: string): {
    state: PeerConnectionState;
    lastFailureCode: string | null;
    accessRevision: string | null;
    lastConnectedAt: number | null;
  } | null {
    const entry = this.entries.get(deviceId);
    if (!entry) return null;
    return {
      state: entry.state,
      lastFailureCode: entry.lastFailureCode,
      accessRevision: entry.accessRevision,
      lastConnectedAt: entry.lastConnectedAt,
    };
  }

  upsertMetadata(device: PeerDeviceRecord): void {
    const existing = this.entries.get(device.device_id);
    if (existing) {
      existing.address = device.address;
      existing.pinnedFingerprint = device.certificate_fingerprint;
      existing.connectionDesired = Boolean(device.connection_desired);
      return;
    }
    this.entries.set(device.device_id, {
      deviceId: device.device_id,
      address: device.address,
      pinnedFingerprint: device.certificate_fingerprint,
      connectionDesired: Boolean(device.connection_desired),
      state: device.last_state ?? 'disconnected',
      lastFailureCode: device.last_failure_code ?? null,
      accessRevision: device.access_revision ?? null,
      lastConnectedAt: device.last_connected_at ?? null,
      connection: null,
      connectingPromise: null,
      reconnectTimer: null,
      reconnectAttempt: 0,
      httpStreams: new Map(),
      wsStreams: new Map(),
    });
  }

  /**
   * Probe a remote `IP:Port` over TLS, extracting its real X.509 DER SHA-256 fingerprint
   * and self-reported identity without consuming any active pairing code.
   */
  async probeAddress(rawAddress: string): Promise<PeerProbeResultDto> {
    const { host, port, normalized } = parsePeerAddress(rawAddress);
    const localIdentity = await this.resolveLocalIdentity();
    const tlsSocket = await this.openTlsSocket(host, port, localIdentity.certPem, localIdentity.privateKeyPem);
    const connection = new PeerFrameConnection(tlsSocket, {
      heartbeatIntervalMs: 0,
    });

    const result = await performClientHandshake({
      connection,
      tlsSocket,
      localDeviceId: localIdentity.deviceId,
      localDeviceName: localIdentity.displayName,
      pairingMode: 'probe',
      handshakeTimeoutMs: this.options.handshakeTimeoutMs ?? 8_000,
    });

    return {
      reachable: true,
      address: normalized,
      remote_device_id: result.remoteDeviceId,
      display_name: result.remoteDeviceName,
      certificate_fingerprint: result.remoteFingerprint,
      protocol_major: result.protocolMajor,
      protocol_minor: result.protocolMinor,
      capabilities: result.capabilities,
    };
  }

  /**
   * Execute 4-step `code` mTLS handshake with strict `expected_fingerprint` pinning,
   * persist the paired device in Core, and adopt the live tunnel into the pool.
   */
  async pairWithPeer(input: PairPeerDeviceInput): Promise<PeerDeviceRecord> {
    const { host, port, normalized } = parsePeerAddress(input.address);
    const pairingCode = input.pairing_code?.trim();
    if (!pairingCode) {
      throw new PeerProtocolError('pairing_code_rejected', 'Pairing code is required.');
    }
    const expectedFp = input.expected_fingerprint?.trim();
    if (!expectedFp) {
      throw new PeerProtocolError(
        'fingerprint_mismatch',
        'expected_fingerprint is required to complete zero-trust pairing.',
      );
    }

    const localIdentity = await this.resolveLocalIdentity();
    const tlsSocket = await this.openTlsSocket(host, port, localIdentity.certPem, localIdentity.privateKeyPem);
    const connection = new PeerFrameConnection(tlsSocket, {
      heartbeatIntervalMs: this.options.heartbeatIntervalMs,
      heartbeatTimeoutMs: this.options.heartbeatTimeoutMs,
    });

    const handshake = await performClientHandshake({
      connection,
      tlsSocket,
      localDeviceId: localIdentity.deviceId,
      localDeviceName: localIdentity.displayName,
      pairingMode: 'code',
      pairingCode,
      expectedRemoteFingerprint: expectedFp,
      handshakeTimeoutMs: this.options.handshakeTimeoutMs ?? 10_000,
    });

    if (handshake.mode === 'probe') {
      connection.destroy();
      throw new PeerProtocolError('handshake_failed', 'Unexpected probe handshake response during pairing.');
    }

    const desired = input.connection_desired ?? true;
    const adoptSessions = input.adopt_sessions ?? true;
    const displayName = input.display_name?.trim() || handshake.remoteDeviceName || handshake.remoteDeviceId;
    const now = Date.now();

    const saved = await this.persistPairedDeviceInCore({
      remote_device_id: handshake.remoteDeviceId,
      display_name: displayName,
      transport: 'lan',
      address: normalized,
      certificate_fingerprint: handshake.remoteFingerprint,
      protocol_major: handshake.protocolMajor,
      protocol_minor: handshake.protocolMinor,
      connection_desired: desired,
      adopt_sessions: adoptSessions,
      last_state: desired ? 'ready' : 'disconnected',
      last_connected_at: now,
      access_revision: handshake.policyRevision,
    });

    if (!desired) {
      connection.destroy();
      return saved;
    }

    const entry = this.getOrCreateEntry(saved);
    entry.connection?.destroy();
    this.attachReadyConnection(entry, connection, handshake.policyRevision, now);
    return {
      ...saved,
      last_state: 'ready',
      last_connected_at: now,
      access_revision: handshake.policyRevision,
    };
  }

  /**
   * Establish or reuse a `grant` mTLS tunnel to an already-paired remote peer.
   */
  async connectDevice(deviceId: string, providedRecord?: PeerDeviceRecord): Promise<PeerDeviceRecord> {
    const record = providedRecord ?? (await this.findDeviceFromCore(deviceId));
    if (!record) {
      throw new PeerProtocolError('device_offline', `Paired peer device '${deviceId}' was not found.`);
    }

    const entry = this.getOrCreateEntry(record);
    entry.connectionDesired = true;
    if (entry.reconnectTimer) {
      clearTimeout(entry.reconnectTimer);
      entry.reconnectTimer = null;
    }

    if (entry.connection && !entry.connection.isClosed && entry.state === 'ready') {
      return this.mergeLiveState(record, entry);
    }

    if (entry.connectingPromise) {
      await entry.connectingPromise;
      return this.mergeLiveState(record, entry);
    }

    entry.connectingPromise = this.doConnectGrant(entry).finally(() => {
      entry.connectingPromise = null;
    });

    try {
      await entry.connectingPromise;
    } catch (err) {
      throw err;
    }
    return this.mergeLiveState(record, entry);
  }

  async disconnectDevice(deviceId: string): Promise<PeerDeviceRecord | null> {
    const entry = this.entries.get(deviceId);
    if (entry) {
      entry.connectionDesired = false;
      if (entry.reconnectTimer) {
        clearTimeout(entry.reconnectTimer);
        entry.reconnectTimer = null;
      }
      entry.connection?.destroy();
      entry.connection = null;
      entry.state = 'disconnected';
      entry.lastFailureCode = null;
    }
    await this.reportControllerStateToCore(deviceId, 'disconnected', null, null, null);
    const updated = await this.findDeviceFromCore(deviceId);
    return updated ? { ...updated, last_state: 'disconnected', connection_desired: false } : null;
  }

  removeDevice(deviceId: string): void {
    const entry = this.entries.get(deviceId);
    if (!entry) return;
    entry.connectionDesired = false;
    if (entry.reconnectTimer) {
      clearTimeout(entry.reconnectTimer);
      entry.reconnectTimer = null;
    }
    entry.connection?.destroy();
    this.entries.delete(deviceId);
  }

  /**
   * Multiplex an HTTP or SSE request over the device's mTLS tunnel.
   */
  async proxyHttpRequest(deviceId: string, req: ProxyHttpOverTunnelOptions): Promise<Response> {
    const entry = await this.ensureReadyConnection(deviceId);
    const conn = entry.connection;
    if (!conn || conn.isClosed) {
      throw new PeerProtocolError('device_offline', `Peer device '${deviceId}' is offline.`);
    }

    const streamId = crypto.randomUUID();
    const hasBody =
      req.body !== undefined &&
      req.body !== null &&
      req.method.toUpperCase() !== 'GET' &&
      req.method.toUpperCase() !== 'HEAD';

    return await new Promise<Response>((resolveHead, rejectHead) => {
      const pending: PendingHttpClientStream = {
        streamId,
        headResolved: false,
        resolveHead,
        rejectHead,
        bodyController: null,
      };

      if (req.signal) {
        if (req.signal.aborted) {
          rejectHead(new DOMException('The operation was aborted.', 'AbortError'));
          return;
        }
        const onAbort = () => {
          entry.httpStreams.delete(streamId);
          try {
            conn.sendFrame({ type: 'http.req.abort', streamId, reason: 'Client aborted request.' });
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
        req.signal.addEventListener('abort', onAbort, { once: true });
        pending.cleanupAbort = () => req.signal?.removeEventListener('abort', onAbort);
      }

      entry.httpStreams.set(streamId, pending);

      try {
        conn.sendFrame({
          type: 'http.req',
          streamId,
          method: req.method.toUpperCase(),
          path: req.path,
          query: req.query ?? '',
          headers: req.headers ?? {},
          hasBody,
        });
      } catch (err) {
        pending.cleanupAbort?.();
        entry.httpStreams.delete(streamId);
        rejectHead(
          err instanceof Error ? err : new PeerProtocolError('device_offline', 'Failed to send request over tunnel.'),
        );
        return;
      }

      if (hasBody) {
        void this.pumpRequestBody(conn, streamId, req.body!, req.signal).catch((err) => {
          pending.cleanupAbort?.();
          entry.httpStreams.delete(streamId);
          if (!pending.headResolved) {
            pending.headResolved = true;
            rejectHead(err instanceof Error ? err : new Error(String(err)));
          }
        });
      }
    });
  }

  /**
   * Open a multiplexed WebSocket terminal stream over the device's mTLS tunnel.
   */
  async openTerminalTunnel(
    deviceId: string,
    options: {
      query?: string;
      headers?: Record<string, string>;
      downstream: WsTerminalClient;
    },
  ): Promise<PeerTunnelWsChannel> {
    const entry = await this.ensureReadyConnection(deviceId);
    const conn = entry.connection;
    if (!conn || conn.isClosed) {
      throw new PeerProtocolError('device_offline', `Peer device '${deviceId}' is offline.`);
    }

    const streamId = crypto.randomUUID();

    return await new Promise<PeerTunnelWsChannel>((resolveOpen, rejectOpen) => {
      const pending: PendingWsClientStream = {
        streamId,
        opened: false,
        resolveOpen,
        rejectOpen,
        downstream: options.downstream,
      };
      entry.wsStreams.set(streamId, pending);

      try {
        conn.sendFrame({
          type: 'ws.open',
          streamId,
          path: '/ws/terminal',
          query: options.query ?? '',
          headers: options.headers ?? {},
        });
      } catch (err) {
        entry.wsStreams.delete(streamId);
        rejectOpen(
          err instanceof Error ? err : new PeerProtocolError('device_offline', 'Failed to open WS tunnel.'),
        );
      }
    });
  }

  private async ensureReadyConnection(deviceId: string): Promise<PooledPeerEntry> {
    const existing = this.entries.get(deviceId);
    if (existing && existing.connection && !existing.connection.isClosed && existing.state === 'ready') {
      return existing;
    }
    if (existing && !existing.connectionDesired) {
      throw new PeerProtocolError('device_offline', `Peer device '${deviceId}' is disconnected.`);
    }

    const record = await this.findDeviceFromCore(deviceId);
    if (!record) {
      throw new PeerProtocolError('device_offline', `Peer device '${deviceId}' was not found.`);
    }
    if (!record.connection_desired && (!existing || !existing.connectionDesired)) {
      throw new PeerProtocolError('device_offline', `Peer device '${deviceId}' is disconnected.`);
    }

    await this.connectDevice(deviceId, record);
    const connected = this.entries.get(deviceId);
    if (!connected || !connected.connection || connected.connection.isClosed || connected.state !== 'ready') {
      throw new PeerProtocolError('device_offline', `Peer device '${deviceId}' is not reachable.`);
    }
    return connected;
  }

  private async doConnectGrant(entry: PooledPeerEntry): Promise<PooledPeerEntry> {
    entry.state = 'connecting';
    entry.lastFailureCode = null;
    void this.reportControllerStateToCore(entry.deviceId, 'connecting', null, null, entry.accessRevision);

    try {
      const { host, port } = parsePeerAddress(entry.address);
      const localIdentity = await this.resolveLocalIdentity();
      const tlsSocket = await this.openTlsSocket(host, port, localIdentity.certPem, localIdentity.privateKeyPem);

      entry.state = 'handshaking';
      const connection = new PeerFrameConnection(tlsSocket, {
        heartbeatIntervalMs: this.options.heartbeatIntervalMs,
        heartbeatTimeoutMs: this.options.heartbeatTimeoutMs,
      });

      const handshake = await performClientHandshake({
        connection,
        tlsSocket,
        localDeviceId: localIdentity.deviceId,
        localDeviceName: localIdentity.displayName,
        pairingMode: 'grant',
        expectedRemoteFingerprint: entry.pinnedFingerprint,
        handshakeTimeoutMs: this.options.handshakeTimeoutMs ?? 10_000,
      });

      if (handshake.mode === 'probe') {
        connection.destroy();
        throw new PeerProtocolError('handshake_failed', 'Unexpected probe response on grant connect.');
      }

      const now = Date.now();
      this.attachReadyConnection(entry, connection, handshake.policyRevision, now);
      await this.reportControllerStateToCore(
        entry.deviceId,
        'ready',
        null,
        now,
        handshake.policyRevision,
      );
      return entry;
    } catch (err) {
      const mapped = mapErrorToConnectionState(err);
      entry.state = mapped.state;
      entry.lastFailureCode = mapped.code;
      await this.reportControllerStateToCore(
        entry.deviceId,
        mapped.state,
        mapped.code,
        entry.lastConnectedAt,
        entry.accessRevision,
      );
      this.scheduleReconnectIfEligible(entry, mapped.state);
      throw err;
    }
  }

  private attachReadyConnection(
    entry: PooledPeerEntry,
    connection: PeerFrameConnection,
    policyRevision: string,
    connectedAt: number,
  ): void {
    entry.connection = connection;
    entry.state = 'ready';
    entry.lastFailureCode = null;
    entry.accessRevision = policyRevision;
    entry.lastConnectedAt = connectedAt;
    entry.reconnectAttempt = 0;

    connection.onClose((closeErr) => {
      if (entry.connection === connection) {
        entry.connection = null;
      }

      const failErr =
        closeErr ?? new PeerProtocolError('device_offline', 'Peer mTLS connection closed.');

      // Immediately terminate all in-flight HTTP/SSE streams (< 5ms).
      const httpStreams = [...entry.httpStreams.values()];
      entry.httpStreams.clear();
      for (const stream of httpStreams) {
        stream.cleanupAbort?.();
        if (!stream.headResolved) {
          stream.headResolved = true;
          stream.rejectHead(failErr);
        } else {
          try {
            stream.bodyController?.close();
          } catch {
            // ignore
          }
        }
      }

      // Immediately terminate all active WS terminal streams (< 5ms).
      const wsStreams = [...entry.wsStreams.values()];
      entry.wsStreams.clear();
      for (const ws of wsStreams) {
        if (!ws.opened) {
          ws.rejectOpen(failErr);
        } else {
          try {
            ws.downstream.close(1012, failErr.message);
          } catch {
            // ignore
          }
        }
      }

      if (!this.stopped && entry.connectionDesired) {
        entry.state = 'disconnected';
        const code = closeErr instanceof PeerProtocolError ? closeErr.code : null;
        entry.lastFailureCode = code;
        void this.reportControllerStateToCore(
          entry.deviceId,
          'disconnected',
          code,
          entry.lastConnectedAt,
          entry.accessRevision,
        );
        this.scheduleReconnectIfEligible(entry, 'disconnected');
      }
    });

    connection.startSteadyState((frame) => {
      this.handleIncomingClientFrame(entry, frame);
    });
  }

  private handleIncomingClientFrame(entry: PooledPeerEntry, frame: DecodedPeerFrame): void {
    const h = frame.header;
    switch (h.type) {
      case 'http.res.head': {
        const pending = entry.httpStreams.get(h.streamId);
        if (!pending || pending.headResolved) return;
        pending.headResolved = true;

        const body = new ReadableStream<Uint8Array>({
          start(controller) {
            pending.bodyController = controller;
          },
          cancel() {
            pending.cleanupAbort?.();
            entry.httpStreams.delete(h.streamId);
            try {
              entry.connection?.sendFrame({
                type: 'http.req.abort',
                streamId: h.streamId,
                reason: 'Downstream response stream cancelled.',
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
        return;
      }

      case 'http.res.chunk': {
        const pending = entry.httpStreams.get(h.streamId);
        if (!pending || !frame.payload || frame.payload.byteLength === 0) return;
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
          // Stream closed.
        }
        return;
      }

      case 'http.res.end': {
        const pending = entry.httpStreams.get(h.streamId);
        if (!pending) return;
        pending.cleanupAbort?.();
        entry.httpStreams.delete(h.streamId);
        if (h.error) {
          const err = new PeerProtocolError(h.error.code, h.error.message);
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
          return;
        }
        try {
          pending.bodyController?.close();
        } catch {
          // ignore
        }
        return;
      }

      case 'ws.open-ack': {
        const pending = entry.wsStreams.get(h.streamId);
        if (!pending || pending.opened) return;
        pending.opened = true;
        const conn = entry.connection;
        pending.resolveOpen({
          sendText(text: string) {
            conn?.sendFrame({ type: 'ws.frame', streamId: h.streamId, text });
          },
          sendBinary(bytes: Uint8Array) {
            conn?.sendFrame({ type: 'ws.frame', streamId: h.streamId, isBinary: true }, bytes);
          },
          close(code?: number, reason?: string) {
            entry.wsStreams.delete(h.streamId);
            try {
              conn?.sendFrame({ type: 'ws.close', streamId: h.streamId, code, reason });
            } catch {
              // ignore
            }
          },
        });
        return;
      }

      case 'ws.open-reject': {
        const pending = entry.wsStreams.get(h.streamId);
        if (!pending) return;
        entry.wsStreams.delete(h.streamId);
        pending.rejectOpen(new PeerProtocolError(h.code, h.message, h.status));
        return;
      }

      case 'ws.frame': {
        const pending = entry.wsStreams.get(h.streamId);
        if (!pending) return;
        try {
          if (typeof h.text === 'string') {
            pending.downstream.send(h.text);
          } else if (frame.payload) {
            pending.downstream.send(Buffer.from(frame.payload).toString('utf8'));
          }
        } catch {
          // Downstream closed.
        }
        return;
      }

      case 'ws.close': {
        const pending = entry.wsStreams.get(h.streamId);
        if (!pending) return;
        entry.wsStreams.delete(h.streamId);
        try {
          pending.downstream.close(h.code, h.reason);
        } catch {
          // ignore
        }
        return;
      }

      default:
        return;
    }
  }

  private async pumpRequestBody(
    conn: PeerFrameConnection,
    streamId: string,
    body: ReadableStream<Uint8Array> | Uint8Array | Buffer | string,
    signal?: AbortSignal,
  ): Promise<void> {
    const maxChunk = 1024 * 1024;
    if (typeof body === 'string') {
      const buf = Buffer.from(body, 'utf8');
      for (let off = 0; off < buf.byteLength; off += maxChunk) {
        conn.sendFrame({ type: 'http.req.chunk', streamId }, buf.subarray(off, Math.min(off + maxChunk, buf.byteLength)));
      }
      conn.sendFrame({ type: 'http.req.end', streamId });
      return;
    }

    if (body instanceof Uint8Array || Buffer.isBuffer(body)) {
      for (let off = 0; off < body.byteLength; off += maxChunk) {
        conn.sendFrame({ type: 'http.req.chunk', streamId }, body.subarray(off, Math.min(off + maxChunk, body.byteLength)));
      }
      conn.sendFrame({ type: 'http.req.end', streamId });
      return;
    }

    const reader = body.getReader();
    try {
      while (!signal?.aborted && !conn.isClosed) {
        const { done, value } = await reader.read();
        if (done) break;
        if (value && value.byteLength > 0) {
          for (let off = 0; off < value.byteLength; off += maxChunk) {
            conn.sendFrame(
              { type: 'http.req.chunk', streamId },
              value.subarray(off, Math.min(off + maxChunk, value.byteLength)),
            );
          }
        }
      }
      if (!signal?.aborted && !conn.isClosed) {
        conn.sendFrame({ type: 'http.req.end', streamId });
      }
    } finally {
      try {
        reader.releaseLock();
      } catch {
        // ignore
      }
    }
  }

  private scheduleReconnectIfEligible(entry: PooledPeerEntry, terminalCheckState: PeerConnectionState): void {
    if (this.stopped || !entry.connectionDesired) return;
    if (
      terminalCheckState === 'fingerprint_mismatch' ||
      terminalCheckState === 'unauthorized' ||
      terminalCheckState === 'version_incompatible'
    ) {
      return;
    }
    if (entry.reconnectTimer) return;

    const base = this.options.reconnectBaseDelayMs ?? 1_000;
    const delay = Math.min(15_000, base * Math.pow(2, Math.min(entry.reconnectAttempt, 4)));
    entry.reconnectAttempt += 1;

    entry.reconnectTimer = setTimeout(() => {
      entry.reconnectTimer = null;
      if (this.stopped || !entry.connectionDesired) return;
      void this.doConnectGrant(entry).catch(() => {
        // Next backoff scheduled inside doConnectGrant if still eligible.
      });
    }, delay);
    entry.reconnectTimer.unref?.();
  }

  private openTlsSocket(
    host: string,
    port: number,
    certPem: string,
    privateKeyPem: string,
  ): Promise<TLSSocket> {
    const timeoutMs = this.options.connectTimeoutMs ?? 5_000;
    return new Promise<TLSSocket>((resolve, reject) => {
      let settled = false;
      const timer = setTimeout(() => {
        if (settled) return;
        settled = true;
        try {
          sock.destroy();
        } catch {
          // ignore
        }
        reject(new PeerProtocolError('timeout', `TLS connection to ${host}:${port} timed out after ${timeoutMs}ms.`));
      }, timeoutMs);
      timer.unref?.();

      const sock = tls.connect(
        {
          host,
          port,
          cert: certPem,
          key: privateKeyPem,
          rejectUnauthorized: false,
          ALPNProtocols: [PEER_ALPN_HTTP],
        },
        () => {
          if (settled) return;
          settled = true;
          clearTimeout(timer);
          resolve(sock);
        },
      );

      sock.once('error', (err: NodeJS.ErrnoException) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        const code = err.code ?? '';
        if (code === 'ETIMEDOUT') {
          reject(new PeerProtocolError('timeout', `Connection to ${host}:${port} timed out.`));
          return;
        }
        reject(new PeerProtocolError('unreachable', `Cannot reach peer ${host}:${port}: ${err.message}`));
      });
    });
  }

  private getOrCreateEntry(record: PeerDeviceRecord): PooledPeerEntry {
    this.upsertMetadata(record);
    return this.entries.get(record.device_id)!;
  }

  private mergeLiveState(record: PeerDeviceRecord, entry: PooledPeerEntry): PeerDeviceRecord {
    return {
      ...record,
      connection_desired: entry.connectionDesired,
      last_state: entry.state,
      last_failure_code: entry.lastFailureCode,
      access_revision: entry.accessRevision ?? record.access_revision,
      last_connected_at: entry.lastConnectedAt ?? record.last_connected_at,
    };
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

  private buildLocalLoopbackCoreHeaders(): Record<string, string> {
    const headers: Record<string, string> = {
      accept: 'application/json',
      'x-request-id': ensureRequestId(null),
      'x-tinadec-principal': 'dev@local',
    };
    const token = (this.options.hostControlToken?.() ?? process.env.TINADEC_HOST_CONTROL_TOKEN ?? '').trim();
    if (token) {
      headers['x-tinadec-host-control'] = token;
    }
    return headers;
  }

  private async resolveLocalIdentity(): Promise<{
    deviceId: string;
    displayName: string;
    certPem: string;
    privateKeyPem: string;
  }> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    try {
      const url = new URL('/api/v1/peers/internal/local-tls-material', this.options.coreUrl()).toString();
      const res = await fetchImpl(url, { headers: this.buildInternalCoreHeaders() });
      if (res.ok) {
        const mat = (await res.json()) as LocalTlsMaterialDto;
        if (mat.cert_pem && mat.private_key_pem && mat.local_device_id) {
          return {
            deviceId: mat.local_device_id,
            displayName: mat.display_name || mat.local_device_id,
            certPem: mat.cert_pem,
            privateKeyPem: mat.private_key_pem,
          };
        }
      }
    } catch {
      // Fallback to ephemeral identity only if Core internal endpoint is not mounted (e.g. unit test).
    }

    if (!this.fallbackIdentity) {
      const generated = generateSelfSignedP256TlsMaterial('tinadec-controller');
      this.fallbackIdentity = {
        deviceId: `ephemeral-${generated.fingerprint.slice(0, 12)}`,
        displayName: 'Tinadec Controller',
        certPem: generated.certPem,
        privateKeyPem: generated.privateKeyPem,
      };
    }
    return this.fallbackIdentity;
  }

  private async persistPairedDeviceInCore(payload: Record<string, unknown>): Promise<PeerDeviceRecord> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    const url = new URL('/api/v1/peers/devices/pair', this.options.coreUrl()).toString();
    const res = await fetchImpl(url, {
      method: 'POST',
      headers: {
        ...this.buildLocalLoopbackCoreHeaders(),
        'content-type': 'application/json',
      },
      body: JSON.stringify(payload),
    });

    const body = (await res.json().catch(() => null)) as Record<string, unknown> | null;
    if (!res.ok || !body) {
      const detail = typeof body?.detail === 'string' ? body.detail : typeof body?.message === 'string' ? body.message : 'Failed to persist paired peer device in Core.';
      throw new PeerProtocolError('handshake_failed', detail, res.status);
    }

    const device = (body.device && typeof body.device === 'object' ? body.device : body) as PeerDeviceRecord;
    return device;
  }

  async fetchDevicesFromCore(): Promise<PeerDeviceRecord[]> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    try {
      const url = new URL('/api/v1/peers/devices', this.options.coreUrl()).toString();
      const res = await fetchImpl(url, { headers: this.buildLocalLoopbackCoreHeaders() });
      if (!res.ok) return [];
      const data = (await res.json()) as unknown;
      if (Array.isArray(data)) return data as PeerDeviceRecord[];
      if (data && typeof data === 'object' && Array.isArray((data as { devices?: unknown }).devices)) {
        return (data as { devices: PeerDeviceRecord[] }).devices;
      }
      return [];
    } catch {
      return [];
    }
  }

  async findDeviceFromCore(deviceId: string): Promise<PeerDeviceRecord | null> {
    const all = await this.fetchDevicesFromCore();
    return all.find((d) => d.device_id === deviceId) ?? null;
  }

  private async reportControllerStateToCore(
    deviceId: string,
    lastState: PeerConnectionState,
    lastFailureCode: string | null,
    lastConnectedAt: number | null,
    accessRevision: string | null,
  ): Promise<void> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    try {
      const url = new URL('/api/v1/peers/internal/controller-state', this.options.coreUrl()).toString();
      await fetchImpl(url, {
        method: 'POST',
        headers: {
          ...this.buildInternalCoreHeaders(),
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          device_id: deviceId,
          last_state: lastState,
          last_failure_code: lastFailureCode,
          last_connected_at: lastConnectedAt,
          access_revision: accessRevision,
        }),
      });
    } catch {
      // Best-effort state sync.
    }
  }
}
