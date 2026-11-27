/**
 * Controller-side outbound mTLS connection pool (`PeerClientPool`):
 * - Executes `probe` (TLS DER SHA-256 fingerprint inspection without burning pairing code),
 *   `pair` (4-step `code` mTLS handshake + Core persistence), and `connect` (`grant` reconnect).
 * - Multiplexes concurrent HTTP, SSE, and WebSocket terminal streams over a single
 *   persistent mTLS socket per remote device.
 * - Immediately fails/closes all in-flight streams when a peer disconnects or revokes access.
 */

import { ensureRequestId } from '../headers.js';
import type { WsTerminalClient } from '../wsTerminalProxy.js';
import { performClientHandshake } from './peerHandshake.js';
import {
  DevServerFramedStream,
  DevServerRequestError,
  type DialDevServerTunnelParams,
  type DialedDevServerStream,
  type MappedDevServerLeaseDto,
  openClientDevServerAlpnStream,
  PeerDevServerPortMapper,
  type RequestDevServerLeaseInput,
  type WorkerDevServerLeaseDto,
} from './peerDevServerTunnel.js';
import {
  type DecodedPeerFrame,
  extractPeerCertFingerprint,
  generateSelfSignedP256TlsMaterial,
  isPeerErrorCode,
  PEER_ALPN_DEVSERVER,
  PEER_ALPN_HTTP,
  type PeerErrorCode,
  PeerFrameConnection,
  PeerProtocolError,
  verifyPinnedFingerprint,
} from './peerProtocol.js';
import type { LocalTlsMaterialDto } from './peerTlsServer.js';
import {
  BlindRelayTransport,
  inferPeerTransportKind,
  type IPeerTransportChannel,
  LanMtlsTransport,
  parseLanPeerAddress,
  type PeerTransportKind,
} from './peerTransport.js';
import type { PeerProviderProxyRuntime } from './peerProviderProxy.js';

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
  transport?: PeerTransportKind;
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
  /** Optional custom `IPeerTransportChannel` for `'lan'` connections (defaults to `LanMtlsTransport`). */
  lanTransport?: IPeerTransportChannel;
  /** Optional `IPeerTransportChannel` for `'relay'` connections (defaults to `BlindRelayTransport`). */
  relayTransport?: IPeerTransportChannel;
  /** Default stateless Relay base URL used when dialing shorthand `'relay'` addresses. */
  relayUrl?: string | (() => string | undefined);
  /** Additional custom `IPeerTransportChannel` plugins matched via `canHandleAddress`. */
  customTransports?: readonly IPeerTransportChannel[];
  /** Optional Wave 3 provider-proxy runtime handling reverse mTLS completion callbacks. */
  providerProxy?: PeerProviderProxyRuntime;
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
  remoteDeviceId: string;
  transport: PeerTransportKind;
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
  return parseLanPeerAddress(rawAddress);
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
  private readonly devServerMapper: PeerDevServerPortMapper;
  private readonly defaultLanTransport: IPeerTransportChannel;
  private readonly defaultRelayTransport: IPeerTransportChannel;
  private providerProxy: PeerProviderProxyRuntime | null;
  private fallbackIdentity: {
    deviceId: string;
    displayName: string;
    certPem: string;
    privateKeyPem: string;
  } | null = null;
  private stopped = false;

  constructor(options: PeerClientPoolOptions) {
    this.options = options;
    this.providerProxy = options.providerProxy ?? null;
    this.defaultLanTransport = options.lanTransport ?? new LanMtlsTransport();
    this.defaultRelayTransport =
      options.relayTransport ??
      new BlindRelayTransport({
        relayUrl: options.relayUrl,
        connectTimeoutMs: options.connectTimeoutMs,
        handshakeTimeoutMs: options.handshakeTimeoutMs,
      });
    this.devServerMapper = new PeerDevServerPortMapper({
      dialDevServerStream: (params) => this.dialDevServerTunnelStream(params),
      isDeviceControlReady: (deviceId) => {
        const entry = this.entries.get(deviceId);
        return Boolean(entry && entry.state === 'ready' && entry.connection && !entry.connection.isClosed);
      },
    });
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
    await this.devServerMapper.closeAll('PeerClientPool stopping.');
    const inFlightConnects: Promise<unknown>[] = [];
    for (const entry of this.entries.values()) {
      if (entry.reconnectTimer) {
        clearTimeout(entry.reconnectTimer);
        entry.reconnectTimer = null;
      }
      entry.connectionDesired = false;
      if (entry.connectingPromise) {
        inFlightConnects.push(entry.connectingPromise.catch(() => undefined));
      }
      entry.connection?.destroy();
      entry.connection = null;
      entry.state = 'disconnected';
    }
    if (inFlightConnects.length > 0) {
      await Promise.all(inFlightConnects);
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

  setProviderProxyRuntime(runtime: PeerProviderProxyRuntime | null): void {
    this.providerProxy = runtime;
  }

  getRemoteDeviceId(deviceId: string): string | null {
    return this.entries.get(deviceId)?.remoteDeviceId ?? null;
  }

  upsertMetadata(device: PeerDeviceRecord): void {
    const resolvedTransport = inferPeerTransportKind(device.address, device.transport);
    const existing = this.entries.get(device.device_id);
    if (existing) {
      existing.remoteDeviceId = device.remote_device_id || existing.remoteDeviceId;
      existing.transport = resolvedTransport;
      existing.address = device.address;
      existing.pinnedFingerprint = device.certificate_fingerprint;
      existing.connectionDesired = Boolean(device.connection_desired);
      return;
    }
    this.entries.set(device.device_id, {
      deviceId: device.device_id,
      remoteDeviceId: device.remote_device_id || device.device_id,
      transport: resolvedTransport,
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
   * Probe a remote peer (`IP:Port` or Relay address) over end-to-end TLS, extracting its real
   * X.509 DER SHA-256 fingerprint and self-reported identity without consuming any pairing code.
   */
  async probeAddress(rawAddress: string, explicitTransport?: PeerTransportKind): Promise<PeerProbeResultDto> {
    const localIdentity = await this.resolveLocalIdentity();
    const channel = this.resolveTransportChannel(rawAddress, explicitTransport);
    const dialed = await channel.connect({
      address: rawAddress,
      certPem: localIdentity.certPem,
      privateKeyPem: localIdentity.privateKeyPem,
      alpnProtocols: [PEER_ALPN_HTTP],
      connectTimeoutMs: this.options.connectTimeoutMs,
    });
    const connection = new PeerFrameConnection(dialed.tlsSocket, {
      heartbeatIntervalMs: 0,
    });

    const result = await performClientHandshake({
      connection,
      tlsSocket: dialed.tlsSocket,
      localDeviceId: localIdentity.deviceId,
      localDeviceName: localIdentity.displayName,
      pairingMode: 'probe',
      handshakeTimeoutMs: this.options.handshakeTimeoutMs ?? 8_000,
    });

    return {
      reachable: true,
      address: dialed.normalizedAddress,
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
    const channel = this.resolveTransportChannel(input.address, input.transport);
    const dialed = await channel.connect({
      address: input.address,
      certPem: localIdentity.certPem,
      privateKeyPem: localIdentity.privateKeyPem,
      alpnProtocols: [PEER_ALPN_HTTP],
      connectTimeoutMs: this.options.connectTimeoutMs,
      pairingCode,
    });
    const connection = new PeerFrameConnection(dialed.tlsSocket, {
      heartbeatIntervalMs: this.options.heartbeatIntervalMs,
      heartbeatTimeoutMs: this.options.heartbeatTimeoutMs,
    });

    const handshake = await performClientHandshake({
      connection,
      tlsSocket: dialed.tlsSocket,
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
      transport: dialed.transport,
      address: dialed.normalizedAddress,
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
    await this.devServerMapper.closeDeviceMappings(deviceId, 'Peer device disconnected.');
    const entry = this.entries.get(deviceId);
    if (entry) {
      this.providerProxy?.revokeDeviceTokens(entry.remoteDeviceId || deviceId);
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
    void this.devServerMapper.closeDeviceMappings(deviceId, 'Peer device removed.');
    const entry = this.entries.get(deviceId);
    if (!entry) return;
    this.providerProxy?.revokeDeviceTokens(entry.remoteDeviceId || deviceId);
    entry.connectionDesired = false;
    if (entry.reconnectTimer) {
      clearTimeout(entry.reconnectTimer);
      entry.reconnectTimer = null;
    }
    entry.connection?.destroy();
    this.entries.delete(deviceId);
  }

  listDevServerLeases(deviceId?: string): MappedDevServerLeaseDto[] {
    return this.devServerMapper.listMappings(deviceId);
  }

  /**
   * Request or renew a remote DevServer preview lease over the control tunnel and bind
   * an ephemeral `127.0.0.1:<localPort>` listener that tunnels HTTP & WebSocket HMR over
   * `ALPN = 'tinadec-peer-devserver/1'`.
   */
  async requestDevServerLease(
    deviceId: string,
    input: RequestDevServerLeaseInput,
    signal?: AbortSignal,
  ): Promise<MappedDevServerLeaseDto> {
    const rawLeaseId = typeof input?.lease_id === 'string' && input.lease_id.trim() ? input.lease_id.trim() : undefined;
    if (
      !rawLeaseId &&
      (typeof input?.target_port !== 'number' ||
        !Number.isInteger(input.target_port) ||
        input.target_port < 1 ||
        input.target_port > 65535)
    ) {
      throw new DevServerRequestError(
        400,
        'invalid_request',
        'target_port must be an integer between 1024 and 65535.',
      );
    }

    const entry = await this.ensureReadyConnection(deviceId);
    const targetHost =
      typeof input?.target_host === 'string' && input.target_host.trim()
        ? input.target_host.trim()
        : undefined;

    const existingMapping =
      rawLeaseId || typeof input?.target_port !== 'number'
        ? null
        : this.devServerMapper.findActiveMappingByTarget(
            deviceId,
            input.target_port,
            targetHost ?? '127.0.0.1',
          );
    const renewLeaseId = rawLeaseId ?? existingMapping?.lease_id;

    const reqPayload: Record<string, unknown> = {};
    if (typeof input?.target_port === 'number') reqPayload.target_port = input.target_port;
    if (targetHost !== undefined) reqPayload.target_host = targetHost;
    if (input?.ttl_seconds !== undefined) reqPayload.ttl_seconds = input.ttl_seconds;
    if (input?.ttl_ms !== undefined) reqPayload.ttl_ms = input.ttl_ms;
    if (renewLeaseId) reqPayload.lease_id = renewLeaseId;

    const leaseRes = await this.proxyHttpRequest(deviceId, {
      method: 'POST',
      path: '/api/v1/peers/internal/devserver-lease',
      headers: {
        accept: 'application/json',
        'content-type': 'application/json',
      },
      body: JSON.stringify(reqPayload),
      signal,
    });

    if (!leaseRes.ok) {
      const errBody = (await leaseRes.json().catch(() => null)) as Record<string, unknown> | null;
      const rawCode = typeof errBody?.code === 'string' ? errBody.code : '';
      const detail =
        typeof errBody?.detail === 'string'
          ? errBody.detail
          : typeof errBody?.message === 'string'
            ? errBody.message
            : `Remote peer rejected DevServer lease request (HTTP ${leaseRes.status}).`;
      if (isPeerErrorCode(rawCode)) {
        throw new PeerProtocolError(rawCode, detail, leaseRes.status);
      }
      throw new DevServerRequestError(leaseRes.status, rawCode || 'invalid_request', detail);
    }

    const remoteLease = (await leaseRes.json()) as WorkerDevServerLeaseDto;
    if (!remoteLease?.lease_id || !remoteLease?.ticket) {
      throw new PeerProtocolError('handshake_failed', 'Remote peer returned malformed DevServer lease response.');
    }

    const localIdentity = await this.resolveLocalIdentity();
    const mapped = await this.devServerMapper.openOrRefreshMapping({
      deviceId,
      lease: remoteLease,
      fallbackControllerDeviceId: localIdentity.deviceId,
    });

    if (!entry.connection || entry.connection.isClosed || entry.state !== 'ready') {
      await this.devServerMapper.closeMapping(mapped.lease_id, 'Control tunnel dropped during lease setup.');
      throw new PeerProtocolError('device_offline', `Peer device '${deviceId}' disconnected during DevServer setup.`);
    }

    return mapped;
  }

  async releaseDevServerLease(
    deviceId: string,
    leaseId?: string,
  ): Promise<{ released: boolean; device_id: string; lease_id: string | null; released_count: number }> {
    const trimmedLeaseId = leaseId?.trim();
    const entry = this.entries.get(deviceId);
    const canNotifyRemote = Boolean(
      entry && entry.state === 'ready' && entry.connection && !entry.connection.isClosed,
    );

    if (trimmedLeaseId) {
      const closed = await this.devServerMapper.closeMapping(
        trimmedLeaseId,
        'Explicitly released by controller.',
      );
      if (canNotifyRemote) {
        void this.proxyHttpRequest(deviceId, {
          method: 'DELETE',
          path: `/api/v1/peers/internal/devserver-lease/${encodeURIComponent(trimmedLeaseId)}`,
          headers: { accept: 'application/json' },
        }).catch(() => undefined);
      }
      return {
        released: closed,
        device_id: deviceId,
        lease_id: trimmedLeaseId,
        released_count: closed ? 1 : 0,
      };
    }

    const active = this.devServerMapper.listMappings(deviceId);
    const closedCount = await this.devServerMapper.closeDeviceMappings(
      deviceId,
      'Explicitly released all device leases.',
    );
    if (canNotifyRemote) {
      for (const item of active) {
        void this.proxyHttpRequest(deviceId, {
          method: 'DELETE',
          path: `/api/v1/peers/internal/devserver-lease/${encodeURIComponent(item.lease_id)}`,
          headers: { accept: 'application/json' },
        }).catch(() => undefined);
      }
    }
    return {
      released: closedCount > 0,
      device_id: deviceId,
      lease_id: null,
      released_count: closedCount,
    };
  }

  private async dialDevServerTunnelStream(
    params: DialDevServerTunnelParams,
  ): Promise<DialedDevServerStream> {
    const entry = this.entries.get(params.deviceId);
    if (!entry || entry.state !== 'ready' || !entry.connection || entry.connection.isClosed) {
      throw new PeerProtocolError('device_offline', `Peer device '${params.deviceId}' is offline.`);
    }
    const localIdentity = await this.resolveLocalIdentity();
    if (
      entry.transport === 'lan' &&
      !this.options.lanTransport &&
      !this.options.customTransports?.some((ch) => ch.canHandleAddress(entry.address))
    ) {
      const { host, port } = parsePeerAddress(entry.address);
      return await openClientDevServerAlpnStream({
        host,
        port,
        certPem: localIdentity.certPem,
        privateKeyPem: localIdentity.privateKeyPem,
        pinnedServerFingerprint: entry.pinnedFingerprint,
        leaseId: params.leaseId,
        ticket: params.ticket,
        controllerDeviceId: params.controllerDeviceId || localIdentity.deviceId,
        connectTimeoutMs: this.options.connectTimeoutMs,
        handshakeTimeoutMs: this.options.handshakeTimeoutMs,
      });
    }

    return await this.dialDevServerOverTransportChannel(
      entry,
      localIdentity,
      params.leaseId,
      params.ticket,
      params.controllerDeviceId || localIdentity.deviceId,
    );
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
      const localIdentity = await this.resolveLocalIdentity();
      const channel = this.resolveTransportChannel(entry.address, entry.transport);
      const dialed = await channel.connect({
        address: entry.address,
        certPem: localIdentity.certPem,
        privateKeyPem: localIdentity.privateKeyPem,
        alpnProtocols: [PEER_ALPN_HTTP],
        connectTimeoutMs: this.options.connectTimeoutMs,
      });
      entry.transport = dialed.transport;
      entry.address = dialed.normalizedAddress;

      entry.state = 'handshaking';
      const connection = new PeerFrameConnection(dialed.tlsSocket, {
        heartbeatIntervalMs: this.options.heartbeatIntervalMs,
        heartbeatTimeoutMs: this.options.heartbeatTimeoutMs,
      });

      const handshake = await performClientHandshake({
        connection,
        tlsSocket: dialed.tlsSocket,
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

      if (handshake.remoteDeviceId) {
        entry.remoteDeviceId = handshake.remoteDeviceId;
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

      // Immediately tear down all local DevServer port mappings & active preview sockets (< 5ms).
      void this.devServerMapper.closeDeviceMappings(entry.deviceId, failErr.message);

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

      case 'http.req':
      case 'http.req.chunk':
      case 'http.req.end':
      case 'http.req.abort': {
        const conn = entry.connection;
        if (!conn || conn.isClosed) return;
        if (this.providerProxy) {
          this.providerProxy.handleControllerReverseRequestFrame(
            {
              deviceId: entry.deviceId,
              remoteDeviceId: entry.remoteDeviceId || entry.deviceId,
              connection: conn,
            },
            frame,
          );
          return;
        }
        if (h.type === 'http.req') {
          const payload = Buffer.from(
            JSON.stringify({
              type: 'https://tinadec.dev/errors/policy_denied',
              title: 'policy_denied',
              status: 403,
              code: 'policy_denied',
              detail: 'Controller provider proxy runtime is not enabled.',
              instance: h.path,
            }),
            'utf8',
          );
          try {
            conn.sendFrame({
              type: 'http.res.head',
              streamId: h.streamId,
              status: 403,
              headers: { 'content-type': 'application/problem+json' },
            });
            conn.sendFrame({ type: 'http.res.chunk', streamId: h.streamId }, payload);
            conn.sendFrame({ type: 'http.res.end', streamId: h.streamId });
          } catch {
            // ignore
          }
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

  private resolveTransportChannel(
    rawAddress: string,
    explicitTransport?: PeerTransportKind | null,
  ): IPeerTransportChannel {
    if (this.options.customTransports?.length) {
      for (const ch of this.options.customTransports) {
        if (explicitTransport && ch.kind === explicitTransport && ch.canHandleAddress(rawAddress)) {
          return ch;
        }
      }
      for (const ch of this.options.customTransports) {
        if (ch.canHandleAddress(rawAddress)) {
          return ch;
        }
      }
    }
    const kind = inferPeerTransportKind(rawAddress, explicitTransport);
    return kind === 'relay' ? this.defaultRelayTransport : this.defaultLanTransport;
  }

  private async dialDevServerOverTransportChannel(
    entry: PooledPeerEntry,
    localIdentity: { certPem: string; privateKeyPem: string },
    leaseId: string,
    ticket: string,
    controllerDeviceId: string,
  ): Promise<DialedDevServerStream> {
    const channel = this.resolveTransportChannel(entry.address, entry.transport);
    const dialed = await channel.connect({
      address: entry.address,
      certPem: localIdentity.certPem,
      privateKeyPem: localIdentity.privateKeyPem,
      alpnProtocols: [PEER_ALPN_DEVSERVER],
      connectTimeoutMs: this.options.connectTimeoutMs,
    });

    const tlsSocket = dialed.tlsSocket;
    const tlsStream = new DevServerFramedStream(tlsSocket);
    const handshakeTimeoutMs = this.options.handshakeTimeoutMs ?? 8_000;

    try {
      if (tlsSocket.alpnProtocol !== PEER_ALPN_DEVSERVER) {
        throw new PeerProtocolError(
          'handshake_failed',
          `Remote peer did not negotiate ALPN '${PEER_ALPN_DEVSERVER}' (got '${String(tlsSocket.alpnProtocol)}').`,
        );
      }

      const actualServerFp = extractPeerCertFingerprint(tlsSocket);
      verifyPinnedFingerprint(
        actualServerFp,
        entry.pinnedFingerprint,
        'Remote DevServer TLS server certificate',
      );

      tlsStream.sendHandshake({
        type: 'devserver.open',
        lease_id: leaseId,
        ticket,
        controller_device_id: controllerDeviceId,
      });

      const frame = await tlsStream.readHandshake(handshakeTimeoutMs);
      if (frame.type === 'devserver.open-reject') {
        const code = isPeerErrorCode(frame.code) ? frame.code : 'unauthorized';
        throw new PeerProtocolError(code, frame.message || 'Remote peer rejected DevServer tunnel.');
      }
      if (frame.type !== 'devserver.open-ack') {
        throw new PeerProtocolError(
          'handshake_failed',
          `Expected devserver.open-ack, received '${frame.type}'.`,
        );
      }
      return { tlsStream };
    } catch (err) {
      await new Promise<void>((resolve) => setTimeout(resolve, 0));
      tlsStream.destroy();
      throw err;
    }
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
