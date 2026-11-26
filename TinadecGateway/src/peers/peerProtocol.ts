/**
 * Peer mTLS wire framing, SHA-256 DER certificate fingerprint pinning,
 * RFC 9457 error vocabulary, backpressure queue, and heartbeat state machine.
 */

import { createHash, generateKeyPairSync, randomBytes, sign, timingSafeEqual } from 'node:crypto';
import type { Duplex } from 'node:stream';
import type { TLSSocket } from 'node:tls';

export const PEER_PROTOCOL_MAJOR = 1;
export const PEER_PROTOCOL_MINOR = 0;

export const PEER_ALPN_HTTP = 'tinadec-peer-http/1';
export const PEER_ALPN_DEVSERVER = 'tinadec-peer-devserver/1';
export const PEER_ALPN_PROTOCOLS = [PEER_ALPN_HTTP, PEER_ALPN_DEVSERVER] as const;

export const PEER_CAPABILITIES = [
  'proxy_v1',
  'devserver_tunnel',
  'agent_delegation',
  'provider_proxy',
] as const;
export type PeerCapability = (typeof PEER_CAPABILITIES)[number];

/** Hard single-frame payload limit: 32 MB. */
export const MAX_PEER_FRAME_BYTES = 32 * 1024 * 1024;

/** Hard outbound backpressure queue limit: 256 frames. */
export const MAX_OUTBOUND_QUEUE_FRAMES = 256;

/** Steady-state keepalive ping interval (15s) and dead-peer timeout (45s). */
export const PEER_HEARTBEAT_INTERVAL_MS = 15_000;
export const PEER_HEARTBEAT_TIMEOUT_MS = 45_000;

/** Default inbound LAN mTLS port and automatic EADDRINUSE probe ceiling. */
export const DEFAULT_PEER_LISTEN_PORT = 48735;
export const MAX_PEER_LISTEN_PORT = 48755;

export const PEER_ERROR_CODES = [
  'unreachable',
  'inbound_disabled',
  'pairing_code_rejected',
  'pairing_code_expired',
  'fingerprint_mismatch',
  'unauthorized',
  'protocol_major_mismatch',
  'encryption_unavailable',
  'handshake_failed',
  'timeout',
  'policy_denied',
  'device_offline',
  'submission_uncertain',
] as const;

export type PeerErrorCode = (typeof PEER_ERROR_CODES)[number];

const PEER_ERROR_SET = new Set<string>(PEER_ERROR_CODES);

export function isPeerErrorCode(raw: unknown): raw is PeerErrorCode {
  return typeof raw === 'string' && PEER_ERROR_SET.has(raw);
}

export function peerErrorHttpStatus(code: PeerErrorCode): number {
  switch (code) {
    case 'inbound_disabled':
    case 'pairing_code_rejected':
    case 'pairing_code_expired':
    case 'fingerprint_mismatch':
    case 'policy_denied':
      return 403;
    case 'unauthorized':
      return 401;
    case 'protocol_major_mismatch':
      return 409;
    case 'encryption_unavailable':
    case 'device_offline':
      return 503;
    case 'timeout':
    case 'submission_uncertain':
      return 504;
    case 'unreachable':
    case 'handshake_failed':
    default:
      return 502;
  }
}

export class PeerProtocolError extends Error {
  readonly code: PeerErrorCode;
  readonly status: number;

  constructor(code: PeerErrorCode, message: string, status?: number) {
    super(message);
    this.name = 'PeerProtocolError';
    this.code = code;
    this.status = status ?? peerErrorHttpStatus(code);
  }
}

export type PairingMode = 'probe' | 'code' | 'grant';
export type PeerRole = 'observer' | 'operator' | 'owner';

export type PeerHelloFrame = {
  type: 'peer.hello';
  protocolMajor: number;
  protocolMinor: number;
  deviceId: string;
  deviceName: string;
  pairingMode: PairingMode;
  pairingCode?: string;
  capabilities: string[];
  expectedRemoteFingerprint?: string;
};

export type PeerHelloAckFrame = {
  type: 'peer.hello-ack';
  protocolMajor: number;
  protocolMinor: number;
  deviceId: string;
  deviceName: string;
  certificateFingerprint: string;
  policyRevision?: string;
  capabilities: string[];
  probeOnly?: boolean;
};

export type PeerHelloRejectFrame = {
  type: 'peer.hello-reject';
  code: PeerErrorCode;
  message: string;
};

export type PeerHelloConfirmFrame = {
  type: 'peer.hello-confirm';
  deviceId: string;
  confirmedRemoteFingerprint: string;
};

export type PeerReadyFrame = {
  type: 'peer.ready';
  policyRevision: string;
  role?: PeerRole;
};

export type PeerPingFrame = {
  type: 'peer.ping';
  ts: number;
};

export type PeerPongFrame = {
  type: 'peer.pong';
  ts: number;
};

export type PeerDisconnectFrame = {
  type: 'peer.disconnect';
  code: PeerErrorCode;
  reason: string;
};

export type PeerHttpReqFrame = {
  type: 'http.req';
  streamId: string;
  method: string;
  path: string;
  query: string;
  headers: Record<string, string>;
  hasBody: boolean;
};

export type PeerHttpReqChunkFrame = {
  type: 'http.req.chunk';
  streamId: string;
};

export type PeerHttpReqEndFrame = {
  type: 'http.req.end';
  streamId: string;
};

export type PeerHttpReqAbortFrame = {
  type: 'http.req.abort';
  streamId: string;
  reason?: string;
};

export type PeerHttpResHeadFrame = {
  type: 'http.res.head';
  streamId: string;
  status: number;
  headers: Record<string, string>;
};

export type PeerHttpResChunkFrame = {
  type: 'http.res.chunk';
  streamId: string;
};

export type PeerHttpResEndFrame = {
  type: 'http.res.end';
  streamId: string;
  error?: {
    code: PeerErrorCode;
    message: string;
  };
};

export type PeerWsOpenFrame = {
  type: 'ws.open';
  streamId: string;
  path: string;
  query: string;
  headers: Record<string, string>;
};

export type PeerWsOpenAckFrame = {
  type: 'ws.open-ack';
  streamId: string;
};

export type PeerWsOpenRejectFrame = {
  type: 'ws.open-reject';
  streamId: string;
  status: number;
  code: PeerErrorCode;
  message: string;
};

export type PeerWsFrame = {
  type: 'ws.frame';
  streamId: string;
  isBinary?: boolean;
  text?: string;
};

export type PeerWsCloseFrame = {
  type: 'ws.close';
  streamId: string;
  code?: number;
  reason?: string;
};

export type PeerFrameHeader =
  | PeerHelloFrame
  | PeerHelloAckFrame
  | PeerHelloRejectFrame
  | PeerHelloConfirmFrame
  | PeerReadyFrame
  | PeerPingFrame
  | PeerPongFrame
  | PeerDisconnectFrame
  | PeerHttpReqFrame
  | PeerHttpReqChunkFrame
  | PeerHttpReqEndFrame
  | PeerHttpReqAbortFrame
  | PeerHttpResHeadFrame
  | PeerHttpResChunkFrame
  | PeerHttpResEndFrame
  | PeerWsOpenFrame
  | PeerWsOpenAckFrame
  | PeerWsOpenRejectFrame
  | PeerWsFrame
  | PeerWsCloseFrame;

export interface DecodedPeerFrame {
  header: PeerFrameHeader;
  payload?: Uint8Array;
}

/**
 * Compute authoritative 64-char lowercase hex SHA-256 from raw X.509 DER bytes.
 */
export function computeDerSha256Fingerprint(der: Buffer | Uint8Array): string {
  return createHash('sha256').update(der).digest('hex').toLowerCase();
}

export function normalizeFingerprint(raw: string | null | undefined): string | null {
  if (typeof raw !== 'string') return null;
  const cleaned = raw.trim().replace(/[:\s-]/g, '').toLowerCase();
  return /^[0-9a-f]{64}$/.test(cleaned) ? cleaned : null;
}

/**
 * Extract the peer's real TLS X.509 certificate SHA-256 fingerprint strictly from
 * `socket.getPeerCertificate(true).raw`. Application-layer self-reported fingerprints
 * are NEVER trusted as identity proof.
 */
export function extractPeerCertFingerprint(
  socket: Pick<TLSSocket, 'getPeerCertificate'>,
): string | null {
  try {
    const cert = socket.getPeerCertificate(true);
    if (!cert || !cert.raw || cert.raw.byteLength === 0) {
      return null;
    }
    return computeDerSha256Fingerprint(cert.raw);
  } catch {
    return null;
  }
}

/**
 * Constant-time SHA-256 fingerprint pinning check.
 * Throws `PeerProtocolError('fingerprint_mismatch', ...)` on any mismatch or malformed input.
 */
export function verifyPinnedFingerprint(
  actualTlsFingerprint: string | null | undefined,
  expectedFingerprint: string | null | undefined,
  context = 'TLS peer certificate',
): void {
  const actualNorm = normalizeFingerprint(actualTlsFingerprint);
  const expectedNorm = normalizeFingerprint(expectedFingerprint);
  if (!actualNorm || !expectedNorm) {
    throw new PeerProtocolError(
      'fingerprint_mismatch',
      `${context} fingerprint is missing or malformed during pinning verification.`,
    );
  }
  const actualBuf = Buffer.from(actualNorm, 'hex');
  const expectedBuf = Buffer.from(expectedNorm, 'hex');
  if (actualBuf.length !== expectedBuf.length || !timingSafeEqual(actualBuf, expectedBuf)) {
    throw new PeerProtocolError(
      'fingerprint_mismatch',
      `${context} SHA-256 fingerprint (${actualNorm.slice(0, 12)}...) does not match pinned fingerprint (${expectedNorm.slice(0, 12)}...).`,
    );
  }
}

const BINARY_ENVELOPE_MARKER = 0x00;

/**
 * Encode a peer wire frame with a 4-byte big-endian length prefix.
 * - Pure JSON frame: `[uint32BE payloadLen][utf8 JSON]`
 * - Binary envelope frame: `[uint32BE payloadLen][0x00][uint32BE metaLen][utf8 JSON][binaryBytes]`
 */
export function encodePeerFrame(
  header: PeerFrameHeader,
  binaryPayload?: Uint8Array | Buffer | null,
): Buffer {
  const metaBuf = Buffer.from(JSON.stringify(header), 'utf8');
  if (!binaryPayload || binaryPayload.byteLength === 0) {
    if (metaBuf.byteLength === 0 || metaBuf.byteLength > MAX_PEER_FRAME_BYTES) {
      throw new PeerProtocolError(
        'handshake_failed',
        `Peer JSON frame size (${metaBuf.byteLength} bytes) exceeds 32 MB ceiling.`,
      );
    }
    const out = Buffer.allocUnsafe(4 + metaBuf.byteLength);
    out.writeUInt32BE(metaBuf.byteLength, 0);
    metaBuf.copy(out, 4);
    return out;
  }

  const bodyLen = 1 + 4 + metaBuf.byteLength + binaryPayload.byteLength;
  if (bodyLen > MAX_PEER_FRAME_BYTES) {
    throw new PeerProtocolError(
      'handshake_failed',
      `Peer binary frame size (${bodyLen} bytes) exceeds 32 MB ceiling.`,
    );
  }
  const out = Buffer.allocUnsafe(4 + bodyLen);
  out.writeUInt32BE(bodyLen, 0);
  out[4] = BINARY_ENVELOPE_MARKER;
  out.writeUInt32BE(metaBuf.byteLength, 5);
  metaBuf.copy(out, 9);
  Buffer.from(binaryPayload.buffer, binaryPayload.byteOffset, binaryPayload.byteLength).copy(
    out,
    9 + metaBuf.byteLength,
  );
  return out;
}

export function decodePeerFramePayload(frameBody: Buffer): DecodedPeerFrame {
  if (frameBody.byteLength === 0 || frameBody.byteLength > MAX_PEER_FRAME_BYTES) {
    throw new PeerProtocolError(
      'handshake_failed',
      `Invalid peer frame body length (${frameBody.byteLength} bytes).`,
    );
  }
  if (frameBody[0] === BINARY_ENVELOPE_MARKER) {
    if (frameBody.byteLength < 5) {
      throw new PeerProtocolError('handshake_failed', 'Truncated binary peer frame envelope.');
    }
    const metaLen = frameBody.readUInt32BE(1);
    if (metaLen === 0 || 5 + metaLen > frameBody.byteLength) {
      throw new PeerProtocolError('handshake_failed', 'Invalid binary peer frame metadata length.');
    }
    let header: PeerFrameHeader;
    try {
      header = JSON.parse(frameBody.subarray(5, 5 + metaLen).toString('utf8')) as PeerFrameHeader;
    } catch {
      throw new PeerProtocolError('handshake_failed', 'Malformed JSON metadata in binary peer frame.');
    }
    if (!header || typeof header !== 'object' || typeof header.type !== 'string') {
      throw new PeerProtocolError('handshake_failed', 'Peer frame metadata missing type.');
    }
    const payload = new Uint8Array(
      frameBody.buffer,
      frameBody.byteOffset + 5 + metaLen,
      frameBody.byteLength - 5 - metaLen,
    );
    return { header, payload };
  }

  let header: PeerFrameHeader;
  try {
    header = JSON.parse(frameBody.toString('utf8')) as PeerFrameHeader;
  } catch {
    throw new PeerProtocolError('handshake_failed', 'Malformed JSON in peer frame.');
  }
  if (!header || typeof header !== 'object' || typeof header.type !== 'string') {
    throw new PeerProtocolError('handshake_failed', 'Peer frame missing type field.');
  }
  return { header };
}

/**
 * Streaming 4-byte BE length-prefixed decoder with immediate 32 MB guard.
 */
export class PeerFrameDecoder {
  private chunks: Buffer[] = [];
  private bufferedBytes = 0;
  private readonly maxFrameBytes: number;

  constructor(maxFrameBytes = MAX_PEER_FRAME_BYTES) {
    this.maxFrameBytes = maxFrameBytes;
  }

  push(chunk: Buffer | Uint8Array): DecodedPeerFrame[] {
    if (chunk.byteLength === 0) return [];
    const buf = Buffer.isBuffer(chunk)
      ? chunk
      : Buffer.from(chunk.buffer, chunk.byteOffset, chunk.byteLength);
    this.chunks.push(buf);
    this.bufferedBytes += buf.byteLength;

    const decoded: DecodedPeerFrame[] = [];
    while (this.bufferedBytes >= 4) {
      const head4 = this.peekHeader4();
      const frameLen = head4.readUInt32BE(0);
      if (frameLen === 0 || frameLen > this.maxFrameBytes) {
        this.reset();
        throw new PeerProtocolError(
          'handshake_failed',
          `Incoming peer frame length (${frameLen}) violates bounds (1..${this.maxFrameBytes}).`,
        );
      }
      if (this.bufferedBytes < 4 + frameLen) {
        break;
      }
      const flat = this.consumeBytes(4 + frameLen);
      decoded.push(decodePeerFramePayload(flat.subarray(4, 4 + frameLen)));
    }
    return decoded;
  }

  reset(): void {
    this.chunks = [];
    this.bufferedBytes = 0;
  }

  private peekHeader4(): Buffer {
    if (this.chunks[0]!.byteLength >= 4) {
      return this.chunks[0]!.subarray(0, 4);
    }
    const merged = Buffer.concat(this.chunks, this.bufferedBytes);
    this.chunks = [merged];
    return merged.subarray(0, 4);
  }

  private consumeBytes(count: number): Buffer {
    if (this.chunks.length === 1 && this.chunks[0]!.byteLength === count) {
      const exact = this.chunks[0]!;
      this.chunks = [];
      this.bufferedBytes = 0;
      return exact;
    }
    if (this.chunks[0]!.byteLength > count) {
      const first = this.chunks[0]!;
      const taken = first.subarray(0, count);
      this.chunks[0] = first.subarray(count);
      this.bufferedBytes -= count;
      return taken;
    }
    const merged = Buffer.concat(this.chunks, this.bufferedBytes);
    const taken = merged.subarray(0, count);
    const remainder = merged.subarray(count);
    this.chunks = remainder.byteLength > 0 ? [remainder] : [];
    this.bufferedBytes = remainder.byteLength;
    return taken;
  }
}

export interface PeerFrameConnectionOptions {
  maxFrameBytes?: number;
  maxOutboundQueueFrames?: number;
  heartbeatIntervalMs?: number;
  heartbeatTimeoutMs?: number;
}

interface PendingWaiter {
  resolve: (frame: DecodedPeerFrame) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout> | null;
}

/**
 * Framed full-duplex connection over a TLS/TCP Duplex socket with:
 * - 32 MB max frame ceiling
 * - 256-frame outbound backpressure queue
 * - Handshake `nextFrame()` queue -> seamless transition to multiplexed `onFrame` callback
 * - 15s `peer.ping` / `peer.pong` keepalive and 45s dead-peer cutoff
 */
export class PeerFrameConnection {
  readonly socket: Duplex;
  private readonly decoder: PeerFrameDecoder;
  private readonly maxOutboundQueueFrames: number;
  private readonly heartbeatIntervalMs: number;
  private readonly heartbeatTimeoutMs: number;

  private outboundQueue: Buffer[] = [];
  private draining = false;
  private closed = false;
  private closeError: Error | null = null;

  private handshakeBuffer: DecodedPeerFrame[] = [];
  private handshakeWaiters: PendingWaiter[] = [];

  private steadyFrameHandler: ((frame: DecodedPeerFrame) => void) | null = null;
  private closeListeners = new Set<(error?: Error) => void>();

  private heartbeatTimer: ReturnType<typeof setInterval> | null = null;
  private lastActivityAt = Date.now();

  constructor(socket: Duplex, options: PeerFrameConnectionOptions = {}) {
    this.socket = socket;
    this.decoder = new PeerFrameDecoder(options.maxFrameBytes ?? MAX_PEER_FRAME_BYTES);
    this.maxOutboundQueueFrames = options.maxOutboundQueueFrames ?? MAX_OUTBOUND_QUEUE_FRAMES;
    this.heartbeatIntervalMs = options.heartbeatIntervalMs ?? PEER_HEARTBEAT_INTERVAL_MS;
    this.heartbeatTimeoutMs = options.heartbeatTimeoutMs ?? PEER_HEARTBEAT_TIMEOUT_MS;

    socket.on('data', (chunk: Buffer | Uint8Array) => {
      if (this.closed) return;
      this.lastActivityAt = Date.now();
      let frames: DecodedPeerFrame[];
      try {
        frames = this.decoder.push(chunk);
      } catch (err) {
        const error = err instanceof Error ? err : new PeerProtocolError('handshake_failed', String(err));
        this.destroy(error);
        return;
      }
      for (const frame of frames) {
        if (this.closed) break;
        this.dispatchIncomingFrame(frame);
      }
    });

    socket.on('drain', () => {
      if (this.closed) return;
      this.flushOutboundQueue();
    });

    socket.on('error', (err: Error) => {
      this.handleSocketClosed(err);
    });

    socket.on('close', () => {
      this.handleSocketClosed();
    });

    socket.on('end', () => {
      this.handleSocketClosed();
    });
  }

  get isClosed(): boolean {
    return this.closed;
  }

  get queuedOutboundFrames(): number {
    return this.outboundQueue.length;
  }

  onClose(listener: (error?: Error) => void): () => void {
    if (this.closed) {
      queueMicrotask(() => listener(this.closeError ?? undefined));
      return () => undefined;
    }
    this.closeListeners.add(listener);
    return () => {
      this.closeListeners.delete(listener);
    };
  }

  sendFrame(header: PeerFrameHeader, binaryPayload?: Uint8Array | Buffer | null): void {
    if (this.closed) {
      throw new PeerProtocolError('device_offline', 'Peer connection is closed.');
    }
    const raw = encodePeerFrame(header, binaryPayload);
    if (this.draining || this.outboundQueue.length > 0) {
      if (this.outboundQueue.length >= this.maxOutboundQueueFrames) {
        const overflowErr = new PeerProtocolError(
          'timeout',
          `Peer outbound queue exceeded ${this.maxOutboundQueueFrames} frames under backpressure.`,
        );
        this.destroy(overflowErr);
        throw overflowErr;
      }
      this.outboundQueue.push(raw);
      return;
    }

    const flushed = this.socket.write(raw);
    if (!flushed) {
      this.draining = true;
    }
  }

  /**
   * Read the next frame during the 4-step handshake before entering steady-state multiplexing.
   */
  nextFrame(timeoutMs = 10_000): Promise<DecodedPeerFrame> {
    if (this.handshakeBuffer.length > 0) {
      return Promise.resolve(this.handshakeBuffer.shift()!);
    }
    if (this.closed) {
      return Promise.reject(
        this.closeError ?? new PeerProtocolError('handshake_failed', 'Peer socket closed during handshake.'),
      );
    }

    return new Promise<DecodedPeerFrame>((resolve, reject) => {
      const waiter: PendingWaiter = {
        resolve,
        reject,
        timer:
          timeoutMs > 0
            ? setTimeout(() => {
                const idx = this.handshakeWaiters.indexOf(waiter);
                if (idx >= 0) this.handshakeWaiters.splice(idx, 1);
                const err = new PeerProtocolError(
                  'timeout',
                  `Timed out waiting ${timeoutMs}ms for peer handshake frame.`,
                );
                this.destroy(err);
                reject(err);
              }, timeoutMs)
            : null,
      };
      this.handshakeWaiters.push(waiter);
    });
  }

  /**
   * Transition to steady-state multiplexed mode and start 15s/45s ping-pong supervision.
   */
  startSteadyState(onFrame: (frame: DecodedPeerFrame) => void): void {
    if (this.closed) return;
    this.steadyFrameHandler = onFrame;
    this.lastActivityAt = Date.now();

    while (this.handshakeBuffer.length > 0 && !this.closed) {
      const buffered = this.handshakeBuffer.shift()!;
      this.dispatchSteadyFrame(buffered);
    }

    if (this.heartbeatIntervalMs > 0 && !this.heartbeatTimer) {
      const checkEvery = Math.max(20, Math.min(this.heartbeatIntervalMs, Math.floor(this.heartbeatTimeoutMs / 3)));
      let lastPingSentAt = Date.now();
      this.heartbeatTimer = setInterval(() => {
        if (this.closed) return;
        const now = Date.now();
        if (now - this.lastActivityAt > this.heartbeatTimeoutMs) {
          this.destroy(
            new PeerProtocolError(
              'timeout',
              `Peer connection timed out after ${this.heartbeatTimeoutMs}ms without activity.`,
            ),
          );
          return;
        }
        if (now - lastPingSentAt >= this.heartbeatIntervalMs) {
          lastPingSentAt = now;
          try {
            this.sendFrame({ type: 'peer.ping', ts: now });
          } catch {
            // Destroyed on write error.
          }
        }
      }, checkEvery);
    }
  }

  /**
   * Immediately tear down socket, timers, queues, and notify all streams (< 5ms).
   */
  destroy(error?: Error): void {
    if (this.closed) return;
    this.handleSocketClosed(error);
    try {
      this.socket.destroy();
    } catch {
      // Ignore already destroyed socket.
    }
  }

  private flushOutboundQueue(): void {
    this.draining = false;
    while (this.outboundQueue.length > 0 && !this.closed) {
      const next = this.outboundQueue.shift()!;
      const ok = this.socket.write(next);
      if (!ok) {
        this.draining = true;
        return;
      }
    }
  }

  private dispatchIncomingFrame(frame: DecodedPeerFrame): void {
    if (frame.header.type === 'peer.ping') {
      try {
        this.sendFrame({ type: 'peer.pong', ts: frame.header.ts });
      } catch {
        // Ignore write error on closing socket.
      }
      return;
    }
    if (frame.header.type === 'peer.pong') {
      return;
    }

    if (this.steadyFrameHandler) {
      this.dispatchSteadyFrame(frame);
      return;
    }

    const waiter = this.handshakeWaiters.shift();
    if (waiter) {
      if (waiter.timer) clearTimeout(waiter.timer);
      waiter.resolve(frame);
      return;
    }
    this.handshakeBuffer.push(frame);
  }

  private dispatchSteadyFrame(frame: DecodedPeerFrame): void {
    if (frame.header.type === 'peer.disconnect') {
      this.destroy(new PeerProtocolError(frame.header.code, frame.header.reason));
      return;
    }
    try {
      this.steadyFrameHandler?.(frame);
    } catch {
      // Individual stream handlers isolate their own errors.
    }
  }

  private handleSocketClosed(error?: Error): void {
    if (this.closed) return;
    this.closed = true;
    this.closeError = error ?? null;

    if (this.heartbeatTimer) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
    this.outboundQueue = [];
    this.decoder.reset();

    while (this.handshakeWaiters.length > 0) {
      const waiter = this.handshakeWaiters.shift()!;
      if (waiter.timer) clearTimeout(waiter.timer);
      waiter.reject(
        error ?? new PeerProtocolError('handshake_failed', 'Peer connection closed before handshake completed.'),
      );
    }

    const listeners = [...this.closeListeners];
    this.closeListeners.clear();
    for (const listener of listeners) {
      try {
        listener(error);
      } catch {
        // Ignore listener error.
      }
    }
  }
}

/**
 * Intersect two capability lists while preserving canonical capability ordering.
 */
export function intersectCapabilities(remoteCapabilities: readonly string[]): string[] {
  const remoteSet = new Set(remoteCapabilities);
  return PEER_CAPABILITIES.filter((cap) => remoteSet.has(cap));
}

// ── Self-Signed ECDSA P-256 X.509v3 Material Generator (Pure node:crypto) ────

function derLength(len: number): Buffer {
  if (len < 0x80) return Buffer.from([len]);
  const octets: number[] = [];
  let remaining = len;
  while (remaining > 0) {
    octets.unshift(remaining & 0xff);
    remaining = Math.floor(remaining / 256);
  }
  return Buffer.from([0x80 | octets.length, ...octets]);
}

function derTlv(tag: number, content: Buffer): Buffer {
  return Buffer.concat([Buffer.from([tag]), derLength(content.length), content]);
}

function derSeq(...items: Buffer[]): Buffer {
  return derTlv(0x30, Buffer.concat(items));
}

function derSet(...items: Buffer[]): Buffer {
  return derTlv(0x31, Buffer.concat(items));
}

function derOid(bytes: number[]): Buffer {
  return derTlv(0x06, Buffer.from(bytes));
}

function derUtf8(text: string): Buffer {
  return derTlv(0x0c, Buffer.from(text, 'utf8'));
}

function derUtcTime(date: Date): Buffer {
  const compact = date.toISOString().replace(/[-:T]/g, '').slice(2, 14) + 'Z';
  return derTlv(0x17, Buffer.from(compact, 'ascii'));
}

function derInteger(raw: Buffer | number[]): Buffer {
  const buf = Buffer.isBuffer(raw) ? raw : Buffer.from(raw);
  let start = 0;
  while (start < buf.length - 1 && buf[start] === 0 && (buf[start + 1]! & 0x80) === 0) {
    start++;
  }
  const trimmed = buf.subarray(start);
  const padded = (trimmed[0]! & 0x80) !== 0 ? Buffer.concat([Buffer.from([0x00]), trimmed]) : trimmed;
  return derTlv(0x02, padded);
}

function derBitString(raw: Buffer): Buffer {
  return derTlv(0x03, Buffer.concat([Buffer.from([0x00]), raw]));
}

export interface GeneratedTlsMaterial {
  certPem: string;
  privateKeyPem: string;
  fingerprint: string;
}

/**
 * Generate an ECDSA P-256 self-signed X.509v3 certificate + PKCS#8 private key PEM
 * and its authoritative DER SHA-256 lowercase hex fingerprint.
 */
export function generateSelfSignedP256TlsMaterial(commonName = 'tinadec-peer'): GeneratedTlsMaterial {
  const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  const spkiDer = publicKey.export({ type: 'spki', format: 'der' });
  const privateKeyPem = privateKey.export({ type: 'pkcs8', format: 'pem' }).toString();

  const versionV3 = derTlv(0xa0, derInteger([0x02]));
  const serialBytes = randomBytes(16);
  serialBytes[0] = (serialBytes[0]! & 0x7f) | 0x01;
  const serial = derInteger(serialBytes);
  // ecdsa-with-SHA256 (1.2.840.10045.4.3.2)
  const sigAlg = derSeq(derOid([0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x02]));
  // CN (2.5.4.3)
  const subject = derSeq(derSet(derSeq(derOid([0x55, 0x04, 0x03]), derUtf8(commonName))));
  const now = Date.now();
  const validity = derSeq(
    derUtcTime(new Date(now - 5 * 60_000)),
    derUtcTime(new Date(now + 10 * 365 * 86_400_000)),
  );

  const tbsCert = derSeq(versionV3, serial, sigAlg, subject, validity, subject, Buffer.from(spkiDer));
  const signature = sign('sha256', tbsCert, privateKey);
  const certDer = derSeq(tbsCert, sigAlg, derBitString(signature));

  const lines = certDer.toString('base64').match(/.{1,64}/g) ?? [];
  const certPem = `-----BEGIN CERTIFICATE-----\n${lines.join('\n')}\n-----END CERTIFICATE-----\n`;
  const fingerprint = computeDerSha256Fingerprint(certDer);

  return { certPem, privateKeyPem, fingerprint };
}
