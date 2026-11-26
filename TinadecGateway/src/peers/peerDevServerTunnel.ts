/**
 * Same-Port TLS ALPN (`tinadec-peer-devserver/1`) DevServer Preview Tunnel (Spec §5.1, §5.3, §6.1):
 *
 * 1. Worker-side `PeerDevServerLeaseManager`:
 *    - Issues and renews bounded TTL preview leases (`POST /api/v1/peers/internal/devserver-lease`)
 *      only to authenticated `operator` / `owner` peer controllers (`observer` denied 403 `policy_denied`).
 *    - Enforces a strict loopback-only SSRF allowlist (`127.0.0.1`, `::1`, `localhost` resolved strictly
 *      to literal `127.0.0.1`/`::1` without OS DNS lookup, blocking `0.0.0.0`, RFC1918 LAN `10.*`/`172.16.*`/
 *      `192.168.*`, link-local metadata `169.254.169.254`, external hosts, privileged ports `< 1024`, and
 *      Tinadec management/control-plane ports `48730`, `48731`, `48732`, `48735..48755` + dynamic ports).
 *    - Accepts `ALPN = 'tinadec-peer-devserver/1'` connections on the shared `PeerTlsServer` port,
 *      verifies the peer's raw X.509 DER SHA-256 fingerprint + active Core `peer_grants` + non-`observer`
 *      role + constant-time `(lease_id, ticket, controller_device_id)` check, then bridges raw bidirectional
 *      TCP bytes to `127.0.0.1:<target_port>` (supporting HTTP/1.1, SSE, static assets, and Vite/Webpack
 *      WebSocket HMR `101 Switching Protocols` upgrades).
 *
 * 2. Controller-side `PeerDevServerPortMapper`:
 *    - Binds an ephemeral `127.0.0.1:0` local TCP server per active remote lease and dials
 *      `ALPN = ['tinadec-peer-devserver/1']` with strict server DER SHA-256 fingerprint pinning on each
 *      incoming browser HTTP / WebSocket connection.
 *    - Guarantees immediate (`< 50ms`) teardown of the local listener and all active tunnel sockets on
 *      lease TTL expiry, explicit close, `disconnectDevice`, remote grant revocation, or tunnel drop.
 */

import { randomBytes, timingSafeEqual } from 'node:crypto';
import net, { type Server as NetServer, type Socket as NetSocket } from 'node:net';
import type { Duplex } from 'node:stream';
import tls, { type TLSSocket } from 'node:tls';
import type { CoreHandshakeVerifyDecision } from './peerHandshake.js';
import type { PeerControllerContext } from './peerPolicyGuard.js';
import {
  DEFAULT_PEER_LISTEN_PORT,
  extractPeerCertFingerprint,
  isPeerErrorCode,
  MAX_PEER_LISTEN_PORT,
  normalizeFingerprint,
  PEER_ALPN_DEVSERVER,
  type PeerErrorCode,
  PeerProtocolError,
  verifyPinnedFingerprint,
} from './peerProtocol.js';

export const DEFAULT_DEVSERVER_LEASE_TTL_SECONDS = 1800;
export const MAX_DEVSERVER_LEASE_TTL_SECONDS = 7200;
export const MIN_DEVSERVER_USER_PORT = 1024;
export const MAX_DEVSERVER_USER_PORT = 65535;
export const MAX_DEVSERVER_HANDSHAKE_FRAME_BYTES = 64 * 1024;
const MAX_PRE_STREAM_BUFFER_BYTES = 4 * 1024 * 1024;

/** Canonical Tinadec host control-plane ports that must NEVER be exposed via DevServer leases. */
const STATIC_FORBIDDEN_MANAGEMENT_PORTS = new Set<number>([
  48730, // TinadecGateway default HTTP port
  48731, // TinadecCore default HTTP port
  48732, // Tinadec ToolRuntime default HTTP port
]);

export interface DevServerOpenFrame {
  type: 'devserver.open';
  lease_id: string;
  ticket: string;
  controller_device_id: string;
}

export interface DevServerOpenAckFrame {
  type: 'devserver.open-ack';
  lease_id: string;
  target_port: number;
}

export interface DevServerOpenRejectFrame {
  type: 'devserver.open-reject';
  code: PeerErrorCode;
  message: string;
}

export type DevServerWireHandshake =
  | DevServerOpenFrame
  | DevServerOpenAckFrame
  | DevServerOpenRejectFrame;

export interface WorkerDevServerLeaseDto {
  lease_id: string;
  ticket: string;
  controller_device_id: string;
  target_host: string;
  target_port: number;
  ttl_seconds: number;
  expires_at_ms: number;
}

export interface RequestDevServerLeaseInput {
  target_port: number;
  target_host?: string;
  ttl_seconds?: number;
  /** Optional millisecond TTL override (e.g. sub-second TTL expiry tests). */
  ttl_ms?: number;
  /** Optional existing lease_id to renew in-place. */
  lease_id?: string;
}

export interface MappedDevServerLeaseDto {
  lease_id: string;
  device_id: string;
  target_port: number;
  local_port: number;
  preview_url: string;
  ttl_seconds: number;
  expires_at_ms: number;
}

export class DevServerRequestError extends Error {
  readonly status: number;
  readonly code: string;

  constructor(status: number, code: string, message: string) {
    super(message);
    this.name = 'DevServerRequestError';
    this.status = status;
    this.code = code;
  }
}

export function isForbiddenDevServerPort(
  port: number,
  dynamicForbiddenPorts?: Iterable<number>,
): boolean {
  if (STATIC_FORBIDDEN_MANAGEMENT_PORTS.has(port)) {
    return true;
  }
  if (port >= DEFAULT_PEER_LISTEN_PORT && port <= MAX_PEER_LISTEN_PORT) {
    return true;
  }
  if (dynamicForbiddenPorts) {
    for (const dynPort of dynamicForbiddenPorts) {
      if (Number.isInteger(dynPort) && dynPort > 0 && port === dynPort) {
        return true;
      }
    }
  }
  return false;
}

export interface ValidatedLoopbackTarget {
  canonicalHost: '127.0.0.1' | '::1' | 'localhost';
  /** Literal loopback IP candidates; OS DNS is never queried to eliminate DNS rebinding. */
  connectIps: readonly ('127.0.0.1' | '::1')[];
  targetPort: number;
  ttlMs: number;
  ttlSeconds: number;
}

export type TargetValidationDecision =
  | { ok: true; value: ValidatedLoopbackTarget }
  | { ok: false; status: number; code: string; message: string };

/**
 * Strictly validate `target_host`, `target_port`, and TTL against SSRF & management-port rules.
 */
export function validateDevServerLeaseTarget(
  rawBody: Record<string, unknown> | null | undefined,
  options?: {
    dynamicForbiddenPorts?: Iterable<number>;
    allowedPorts?: ReadonlySet<number> | readonly number[];
  },
): TargetValidationDecision {
  const body = rawBody ?? {};

  // 1. Strict loopback host whitelist (127.0.0.1, ::1, [::1], localhost ONLY).
  const rawHost = body.target_host ?? body.targetHost;
  let canonicalHost: '127.0.0.1' | '::1' | 'localhost' = '127.0.0.1';
  let connectIps: readonly ('127.0.0.1' | '::1')[] = ['127.0.0.1'];

  if (rawHost !== undefined && rawHost !== null && rawHost !== '') {
    if (typeof rawHost !== 'string') {
      return {
        ok: false,
        status: 400,
        code: 'invalid_request',
        message: 'target_host must be a string.',
      };
    }
    const cleanedHost = rawHost.trim().toLowerCase();
    if (cleanedHost === '127.0.0.1') {
      canonicalHost = '127.0.0.1';
      connectIps = ['127.0.0.1'];
    } else if (cleanedHost === '::1' || cleanedHost === '[::1]') {
      canonicalHost = '::1';
      connectIps = ['::1'];
    } else if (cleanedHost === 'localhost') {
      canonicalHost = 'localhost';
      connectIps = ['127.0.0.1', '::1'];
    } else {
      return {
        ok: false,
        status: 403,
        code: 'policy_denied',
        message: `SSRF guard blocked non-loopback DevServer target_host '${rawHost}'. Only 127.0.0.1, ::1, or localhost are permitted.`,
      };
    }
  }

  // 2. Strict positive integer port bounds & management port blocklist.
  const rawPort = body.target_port ?? body.targetPort;
  if (typeof rawPort !== 'number' || !Number.isInteger(rawPort) || rawPort < 1 || rawPort > MAX_DEVSERVER_USER_PORT) {
    return {
      ok: false,
      status: 400,
      code: 'invalid_request',
      message: `target_port must be an integer between ${MIN_DEVSERVER_USER_PORT} and ${MAX_DEVSERVER_USER_PORT}.`,
    };
  }

  if (rawPort < MIN_DEVSERVER_USER_PORT) {
    return {
      ok: false,
      status: 403,
      code: 'policy_denied',
      message: `Privileged system port ${rawPort} (< ${MIN_DEVSERVER_USER_PORT}) cannot be leased for DevServer preview.`,
    };
  }

  if (isForbiddenDevServerPort(rawPort, options?.dynamicForbiddenPorts)) {
    return {
      ok: false,
      status: 403,
      code: 'policy_denied',
      message: `Tinadec control-plane / management port ${rawPort} is strictly forbidden for DevServer preview.`,
    };
  }

  if (options?.allowedPorts) {
    const allowedSet =
      options.allowedPorts instanceof Set ? options.allowedPorts : new Set(options.allowedPorts);
    if (allowedSet.size > 0 && !allowedSet.has(rawPort)) {
      return {
        ok: false,
        status: 403,
        code: 'policy_denied',
        message: `Target port ${rawPort} is not in the allowed DevServer preview port whitelist.`,
      };
    }
  }

  // 3. Bounded TTL calculation (default 1800s, max 7200s).
  const rawTtlMs = body.ttl_ms ?? body.ttlMs;
  const rawTtlSeconds = body.ttl_seconds ?? body.ttlSeconds;
  const maxTtlMs = MAX_DEVSERVER_LEASE_TTL_SECONDS * 1000;
  let ttlMs = DEFAULT_DEVSERVER_LEASE_TTL_SECONDS * 1000;

  if (rawTtlMs !== undefined && rawTtlMs !== null) {
    if (typeof rawTtlMs !== 'number' || !Number.isFinite(rawTtlMs) || rawTtlMs <= 0) {
      return {
        ok: false,
        status: 400,
        code: 'invalid_request',
        message: 'ttl_ms must be a positive number.',
      };
    }
    ttlMs = Math.min(maxTtlMs, Math.max(50, Math.round(rawTtlMs)));
  } else if (rawTtlSeconds !== undefined && rawTtlSeconds !== null) {
    if (typeof rawTtlSeconds !== 'number' || !Number.isFinite(rawTtlSeconds) || rawTtlSeconds <= 0) {
      return {
        ok: false,
        status: 400,
        code: 'invalid_request',
        message: 'ttl_seconds must be a positive number.',
      };
    }
    ttlMs = Math.min(maxTtlMs, Math.max(50, Math.round(rawTtlSeconds * 1000)));
  }

  const ttlSeconds = Math.max(1, Math.round(ttlMs / 1000));

  return {
    ok: true,
    value: {
      canonicalHost,
      connectIps,
      targetPort: rawPort,
      ttlMs,
      ttlSeconds,
    },
  };
}

// ── Single-Listener TLS Stream Wrapper (Bun/BoringSSL Crash-Safe) ─────────────

export function encodeDevServerHandshakeFrame(header: DevServerWireHandshake): Buffer {
  const jsonBuf = Buffer.from(JSON.stringify(header), 'utf8');
  if (jsonBuf.byteLength === 0 || jsonBuf.byteLength > MAX_DEVSERVER_HANDSHAKE_FRAME_BYTES) {
    throw new PeerProtocolError(
      'handshake_failed',
      `DevServer handshake frame size (${jsonBuf.byteLength}) is out of bounds.`,
    );
  }
  const frame = Buffer.allocUnsafe(4 + jsonBuf.byteLength);
  frame.writeUInt32BE(jsonBuf.byteLength, 0);
  jsonBuf.copy(frame, 4);
  return frame;
}

/**
 * Single-listener state machine around a `TLSSocket` (or `Duplex`) that reads exactly one
 * 4-byte BE length-prefixed JSON handshake frame and seamlessly transitions to raw byte
 * forwarding WITHOUT calling `.pause()`, `.resume()`, `.pipe()`, or mutating native stream
 * listeners inside `'data'`/`'write'` callbacks (avoiding Bun Windows `usockets` TLS crashes).
 */
export class DevServerFramedStream {
  readonly socket: Duplex;
  private mode: 'handshake' | 'buffering' | 'streaming' | 'closed' = 'handshake';
  private handshakeChunks: Buffer[] = [];
  private handshakeBytes = 0;
  private preStreamChunks: Buffer[] = [];
  private preStreamBytes = 0;

  private resolvedHandshake: DevServerWireHandshake | null = null;
  private handshakeError: Error | null = null;
  private handshakeWaiter: {
    resolve: (frame: DevServerWireHandshake) => void;
    reject: (err: Error) => void;
    timer: ReturnType<typeof setTimeout> | null;
  } | null = null;

  private rawDataHandler: ((chunk: Buffer) => void) | null = null;
  private rawEndHandler: (() => void) | null = null;
  private closeHandlers = new Set<() => void>();
  private rejectCloseTimer: ReturnType<typeof setTimeout> | null = null;
  private inNativeCallbackTurn = false;
  private peerEnded = false;

  constructor(socket: Duplex) {
    this.socket = socket;

    socket.on('data', (chunk: Buffer | Uint8Array) => {
      if (this.mode === 'closed' || chunk.byteLength === 0) return;
      this.enterNativeTurn();
      const buf = Buffer.isBuffer(chunk)
        ? chunk
        : Buffer.from(chunk.buffer, chunk.byteOffset, chunk.byteLength);

      if (this.mode === 'streaming') {
        this.rawDataHandler?.(buf);
        return;
      }

      if (this.mode === 'buffering') {
        if (this.preStreamBytes + buf.byteLength > MAX_PRE_STREAM_BUFFER_BYTES) {
          this.failHandshakeOrDestroy(
            new PeerProtocolError('handshake_failed', 'Pre-stream buffer limit exceeded.'),
          );
          return;
        }
        this.preStreamChunks.push(buf);
        this.preStreamBytes += buf.byteLength;
        return;
      }

      // mode === 'handshake'
      this.handshakeChunks.push(buf);
      this.handshakeBytes += buf.byteLength;
      if (this.handshakeBytes < 4) return;

      const headSource =
        this.handshakeChunks[0]!.byteLength >= 4
          ? this.handshakeChunks[0]!
          : Buffer.concat(this.handshakeChunks, this.handshakeBytes);
      if (this.handshakeChunks[0]!.byteLength < 4) {
        this.handshakeChunks = [headSource];
      }

      const frameLen = headSource.readUInt32BE(0);
      if (frameLen < 2 || frameLen > MAX_DEVSERVER_HANDSHAKE_FRAME_BYTES) {
        this.failHandshakeOrDestroy(
          new PeerProtocolError(
            'handshake_failed',
            `Invalid DevServer handshake frame length (${frameLen} bytes).`,
          ),
        );
        return;
      }

      if (this.handshakeBytes < 4 + frameLen) return;

      const merged =
        this.handshakeChunks.length === 1
          ? this.handshakeChunks[0]!
          : Buffer.concat(this.handshakeChunks, this.handshakeBytes);
      this.handshakeChunks = [];
      this.handshakeBytes = 0;

      const jsonSlice = merged.subarray(4, 4 + frameLen);
      if (merged.byteLength > 4 + frameLen) {
        const rem = Buffer.from(merged.subarray(4 + frameLen));
        this.preStreamChunks.push(rem);
        this.preStreamBytes += rem.byteLength;
      }

      let parsed: DevServerWireHandshake;
      try {
        parsed = JSON.parse(jsonSlice.toString('utf8')) as DevServerWireHandshake;
      } catch {
        this.failHandshakeOrDestroy(
          new PeerProtocolError('handshake_failed', 'Malformed JSON in DevServer handshake frame.'),
        );
        return;
      }

      if (!parsed || typeof parsed !== 'object' || typeof parsed.type !== 'string') {
        this.failHandshakeOrDestroy(
          new PeerProtocolError('handshake_failed', 'DevServer handshake frame missing type.'),
        );
        return;
      }

      this.mode = 'buffering';
      this.resolvedHandshake = parsed;
      if (this.handshakeWaiter) {
        const w = this.handshakeWaiter;
        this.handshakeWaiter = null;
        if (w.timer) clearTimeout(w.timer);
        w.resolve(parsed);
      }
    });

    socket.on('end', () => {
      this.peerEnded = true;
      if (this.mode === 'handshake') {
        this.failHandshakeOrDestroy(
          new PeerProtocolError('handshake_failed', 'DevServer TLS socket ended before open handshake.'),
        );
        return;
      }
      if (this.mode === 'streaming') {
        this.rawEndHandler?.();
      }
    });

    socket.on('error', (err: Error) => {
      this.failHandshakeOrDestroy(
        err instanceof PeerProtocolError ? err : new PeerProtocolError('handshake_failed', err.message),
      );
    });

    socket.on('close', () => {
      this.markClosed(
        new PeerProtocolError('handshake_failed', 'DevServer TLS socket closed during handshake.'),
      );
    });
  }

  get isClosed(): boolean {
    return this.mode === 'closed';
  }

  readHandshake(timeoutMs = 8_000): Promise<DevServerWireHandshake> {
    if (this.resolvedHandshake) {
      return Promise.resolve(this.resolvedHandshake);
    }
    if (this.handshakeError || this.mode === 'closed') {
      return Promise.reject(
        this.handshakeError ??
          new PeerProtocolError('handshake_failed', 'DevServer stream already closed.'),
      );
    }

    return new Promise<DevServerWireHandshake>((resolve, reject) => {
      const timer =
        timeoutMs > 0
          ? setTimeout(() => {
              this.handshakeWaiter = null;
              const err = new PeerProtocolError(
                'timeout',
                `Timed out waiting ${timeoutMs}ms for DevServer ALPN handshake frame.`,
              );
              this.destroy();
              reject(err);
            }, timeoutMs)
          : null;
      timer?.unref?.();
      this.handshakeWaiter = { resolve, reject, timer };
    });
  }

  sendHandshake(header: DevServerWireHandshake): void {
    if (this.mode === 'closed') return;
    this.socket.write(encodeDevServerHandshakeFrame(header));
  }

  sendRejectAndClose(code: PeerErrorCode, message: string): void {
    if (this.mode === 'closed') return;
    try {
      this.socket.write(
        encodeDevServerHandshakeFrame({
          type: 'devserver.open-reject',
          code,
          message,
        }),
      );
    } catch {
      this.destroy();
      return;
    }
    if (!this.rejectCloseTimer) {
      this.rejectCloseTimer = setTimeout(() => {
        this.rejectCloseTimer = null;
        this.destroy();
      }, 30);
      this.rejectCloseTimer.unref?.();
    }
  }

  startRawStreaming(
    onData: (chunk: Buffer) => void,
    onEnd: () => void,
    onClose: () => void,
  ): void {
    if (this.mode === 'closed') {
      queueMicrotask(onClose);
      return;
    }
    this.closeHandlers.add(onClose);
    this.rawDataHandler = onData;
    this.rawEndHandler = onEnd;
    this.mode = 'streaming';

    if (this.preStreamChunks.length > 0) {
      const pending = this.preStreamChunks;
      this.preStreamChunks = [];
      this.preStreamBytes = 0;
      for (const chunk of pending) {
        if (this.mode !== 'streaming') break;
        onData(chunk);
      }
    }

    if (this.peerEnded && this.mode === 'streaming') {
      onEnd();
    }
  }

  writeRaw(chunk: Buffer | Uint8Array): void {
    if (this.mode === 'closed') return;
    try {
      this.socket.write(chunk);
    } catch {
      this.destroy();
    }
  }

  destroy(): void {
    if (this.mode === 'closed') return;
    this.markClosed();
    this.destroyNativeSocketSafely();
  }

  private failHandshakeOrDestroy(err: Error): void {
    if (this.mode === 'closed') return;
    this.markClosed(err);
    this.destroyNativeSocketSafely();
  }

  private enterNativeTurn(): void {
    if (this.inNativeCallbackTurn) return;
    this.inNativeCallbackTurn = true;
    const t = setTimeout(() => {
      this.inNativeCallbackTurn = false;
    }, 0);
    t.unref?.();
  }

  private destroyNativeSocketSafely(): void {
    if (this.inNativeCallbackTurn) {
      const t = setTimeout(() => {
        try {
          this.socket.destroy();
        } catch {
          // ignore
        }
      }, 1);
      t.unref?.();
      return;
    }
    try {
      this.socket.destroy();
    } catch {
      // ignore
    }
  }

  private markClosed(err?: Error): void {
    if (this.mode === 'closed') return;
    this.mode = 'closed';
    if (this.rejectCloseTimer) {
      clearTimeout(this.rejectCloseTimer);
      this.rejectCloseTimer = null;
    }
    this.handshakeChunks = [];
    this.handshakeBytes = 0;
    this.preStreamChunks = [];
    this.preStreamBytes = 0;
    if (err && !this.handshakeError) {
      this.handshakeError = err;
    }

    if (this.handshakeWaiter) {
      const w = this.handshakeWaiter;
      this.handshakeWaiter = null;
      if (w.timer) clearTimeout(w.timer);
      w.reject(
        this.handshakeError ??
          new PeerProtocolError('handshake_failed', 'DevServer stream closed.'),
      );
    }

    const handlers = [...this.closeHandlers];
    this.closeHandlers.clear();
    for (const fn of handlers) {
      try {
        fn();
      } catch {
        // ignore
      }
    }
  }
}

/**
 * Convenience helper for tests/callers reading a single handshake frame from a Duplex socket.
 */
export async function readSingleDevServerHandshakeFrame(
  socket: Duplex,
  timeoutMs = 8_000,
): Promise<{ frame: DevServerWireHandshake; stream: DevServerFramedStream }> {
  const stream = new DevServerFramedStream(socket);
  const frame = await stream.readHandshake(timeoutMs);
  return { frame, stream };
}

interface ActivePipeBridge {
  destroy(): void;
}

function bridgeFramedTlsWithTcpSocket(
  tlsStream: DevServerFramedStream,
  tcpSocket: NetSocket,
  options: {
    initialToTls?: Buffer;
    onClosed?: () => void;
  } = {},
): ActivePipeBridge {
  let closed = false;
  let lingerTimer: ReturnType<typeof setTimeout> | null = null;

  const notifyOnce = () => {
    if (closed) return;
    closed = true;
    if (lingerTimer) {
      clearTimeout(lingerTimer);
      lingerTimer = null;
    }
    options.onClosed?.();
  };

  const hardDestroyBoth = () => {
    notifyOnce();
    tlsStream.destroy();
    try {
      tcpSocket.destroy();
    } catch {
      // ignore
    }
  };

  const scheduleGracefulDestroy = () => {
    if (closed || lingerTimer) return;
    lingerTimer = setTimeout(() => {
      lingerTimer = null;
      hardDestroyBoth();
    }, 20);
    lingerTimer.unref?.();
  };

  if (options.initialToTls && options.initialToTls.byteLength > 0) {
    tlsStream.writeRaw(options.initialToTls);
  }

  tcpSocket.on('data', (chunk: Buffer | Uint8Array) => {
    if (closed) return;
    tlsStream.writeRaw(chunk);
  });

  tcpSocket.on('end', () => {
    if (closed) return;
    scheduleGracefulDestroy();
  });

  tcpSocket.on('error', () => {
    hardDestroyBoth();
  });

  let tcpClosed = false;
  let tlsClosed = false;

  tcpSocket.once('close', () => {
    tcpClosed = true;
    if (tlsClosed) {
      notifyOnce();
    } else {
      scheduleGracefulDestroy();
    }
  });

  tlsStream.startRawStreaming(
    (chunk) => {
      if (closed || tcpSocket.destroyed) return;
      try {
        tcpSocket.write(chunk);
      } catch {
        hardDestroyBoth();
      }
    },
    () => {
      if (closed || tcpSocket.destroyed) return;
      try {
        tcpSocket.end();
      } catch {
        hardDestroyBoth();
      }
      scheduleGracefulDestroy();
    },
    () => {
      tlsClosed = true;
      if (tcpClosed) {
        notifyOnce();
      } else {
        try {
          tcpSocket.end();
        } catch {
          hardDestroyBoth();
        }
        scheduleGracefulDestroy();
      }
    },
  );

  return {
    destroy: hardDestroyBoth,
  };
}

function connectLoopbackTcp(
  connectIps: readonly ('127.0.0.1' | '::1')[],
  port: number,
  timeoutMs = 2_000,
): Promise<{ socket: NetSocket; connectedIp: '127.0.0.1' | '::1' }> {
  return (async () => {
    let lastErr: Error | null = null;
    for (const ip of connectIps) {
      try {
        const socket = await new Promise<NetSocket>((resolve, reject) => {
          let settled = false;
          // `ip` is strictly validated literal '127.0.0.1' or '::1', so net.connect never invokes DNS.
          const sock = net.connect({
            host: ip,
            port,
          });
          sock.setNoDelay(true);

          const timer = setTimeout(() => {
            if (settled) return;
            settled = true;
            sock.destroy();
            reject(new Error(`Loopback TCP connect to ${ip}:${port} timed out after ${timeoutMs}ms.`));
          }, timeoutMs);
          timer.unref?.();

          sock.once('connect', () => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            resolve(sock);
          });

          sock.once('error', (err) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            sock.destroy();
            reject(err);
          });
        });
        return { socket, connectedIp: ip };
      } catch (err) {
        lastErr = err instanceof Error ? err : new Error(String(err));
      }
    }
    throw lastErr ?? new Error(`Cannot connect to loopback port ${port}.`);
  })();
}

function constantTimeHexEquals(a: string, b: string): boolean {
  if (typeof a !== 'string' || typeof b !== 'string') return false;
  if (a.length === 0 || a.length !== b.length) return false;
  const bufA = Buffer.from(a, 'utf8');
  const bufB = Buffer.from(b, 'utf8');
  if (bufA.length !== bufB.length) return false;
  return timingSafeEqual(bufA, bufB);
}

// ── 1. Worker-Side DevServer Lease Manager & ALPN Stream Handler ─────────────

interface WorkerLeaseRecord {
  leaseId: string;
  ticket: string;
  controllerDeviceId: string;
  clientFingerprint: string;
  canonicalHost: '127.0.0.1' | '::1' | 'localhost';
  connectIps: readonly ('127.0.0.1' | '::1')[];
  preferredConnectIp: '127.0.0.1' | '::1';
  targetPort: number;
  ttlSeconds: number;
  expiresAtMs: number;
  revoked: boolean;
  expiryTimer: ReturnType<typeof setTimeout> | null;
  activeBridges: Set<ActivePipeBridge>;
}

export interface PeerDevServerLeaseManagerOptions {
  getDynamicForbiddenPorts?: () => Iterable<number>;
  allowedPorts?: readonly number[];
  verifyTargetReachable?: boolean;
  probeTimeoutMs?: number;
}

export interface HandleIncomingDevServerSocketInput {
  tlsSocket: TLSSocket;
  isInboundEnabled: () => boolean;
  verifyControllerGrant: (input: {
    controllerDeviceId: string;
    clientFingerprint: string;
  }) => Promise<CoreHandshakeVerifyDecision>;
  handshakeTimeoutMs?: number;
  extractFingerprintFn?: (socket: Pick<TLSSocket, 'getPeerCertificate'>) => string | null;
}

export class PeerDevServerLeaseManager {
  private readonly options: PeerDevServerLeaseManagerOptions;
  private readonly leasesById = new Map<string, WorkerLeaseRecord>();
  private readonly leaseIdsByController = new Map<string, Set<string>>();

  constructor(options: PeerDevServerLeaseManagerOptions = {}) {
    this.options = options;
  }

  get activeLeaseCount(): number {
    return this.leasesById.size;
  }

  /**
   * Handle internal tunnel HTTP calls (`POST /api/v1/peers/internal/devserver-lease` and
   * `DELETE /api/v1/peers/internal/devserver-lease/:leaseId`) after `PeerPolicyGuard` has verified
   * the authenticated controller session.
   */
  async handleInternalLeaseHttpRequest(
    controller: PeerControllerContext,
    method: string,
    normalizedPath: string,
    bodyJson: Record<string, unknown> | null,
  ): Promise<
    | { ok: true; status: number; body: Record<string, unknown> }
    | { ok: false; status: number; code: string; detail: string }
  > {
    if (controller.policy.role === 'observer') {
      return {
        ok: false,
        status: 403,
        code: 'policy_denied',
        detail: 'Observer role is forbidden from requesting or managing DevServer preview leases.',
      };
    }

    const upperMethod = method.toUpperCase();
    if (upperMethod === 'POST' && normalizedPath === '/api/v1/peers/internal/devserver-lease') {
      try {
        const lease = await this.issueOrRenewLease(controller, bodyJson);
        return {
          ok: true,
          status: 200,
          body: lease as unknown as Record<string, unknown>,
        };
      } catch (err) {
        if (err instanceof DevServerRequestError) {
          return {
            ok: false,
            status: err.status,
            code: err.code,
            detail: err.message,
          };
        }
        return {
          ok: false,
          status: 502,
          code: 'unreachable',
          detail: err instanceof Error ? err.message : 'Failed to issue DevServer lease.',
        };
      }
    }

    const deleteMatch = /^\/api\/v1\/peers\/internal\/devserver-lease\/([^/]+)$/i.exec(normalizedPath);
    if (upperMethod === 'DELETE' && deleteMatch) {
      const leaseId = decodeURIComponent(deleteMatch[1]!).trim();
      const released = this.revokeLeaseForController(controller.controllerDeviceId, leaseId);
      return {
        ok: true,
        status: 200,
        body: {
          released,
          lease_id: leaseId,
          controller_device_id: controller.controllerDeviceId,
        },
      };
    }

    return {
      ok: false,
      status: 405,
      code: 'invalid_request',
      detail: `Unsupported internal DevServer lease operation: ${upperMethod} ${normalizedPath}.`,
    };
  }

  /**
   * Issue a new DevServer lease or renew an existing active lease owned by `controller`.
   */
  async issueOrRenewLease(
    controller: PeerControllerContext,
    rawBody: Record<string, unknown> | null | undefined,
  ): Promise<WorkerDevServerLeaseDto> {
    if (controller.policy.role === 'observer') {
      throw new DevServerRequestError(
        403,
        'policy_denied',
        'Observer role cannot request DevServer preview leases.',
      );
    }

    const clientFp = normalizeFingerprint(controller.clientFingerprint);
    if (!clientFp) {
      throw new DevServerRequestError(
        401,
        'unauthorized',
        'Controller TLS certificate fingerprint is missing.',
      );
    }

    const requestedLeaseId =
      typeof rawBody?.lease_id === 'string' && rawBody.lease_id.trim().length > 0
        ? rawBody.lease_id.trim()
        : null;

    const existingForRenew = requestedLeaseId ? this.leasesById.get(requestedLeaseId) : undefined;
    const effectiveInput: Record<string, unknown> = {
      ...(existingForRenew
        ? {
            target_host: existingForRenew.canonicalHost,
            target_port: existingForRenew.targetPort,
          }
        : {}),
      ...(rawBody ?? {}),
    };

    const validation = validateDevServerLeaseTarget(effectiveInput, {
      dynamicForbiddenPorts: this.options.getDynamicForbiddenPorts?.(),
      allowedPorts: this.options.allowedPorts,
    });

    if (!validation.ok) {
      throw new DevServerRequestError(validation.status, validation.code, validation.message);
    }

    const { canonicalHost, connectIps, targetPort, ttlMs, ttlSeconds } = validation.value;

    let preferredConnectIp: '127.0.0.1' | '::1' = connectIps[0]!;
    if (this.options.verifyTargetReachable !== false) {
      try {
        const probe = await connectLoopbackTcp(
          connectIps,
          targetPort,
          this.options.probeTimeoutMs ?? 1_500,
        );
        preferredConnectIp = probe.connectedIp;
        try {
          probe.socket.end();
          setTimeout(() => probe.socket.destroy(), 25).unref?.();
        } catch {
          probe.socket.destroy();
        }
      } catch {
        throw new DevServerRequestError(
          502,
          'unreachable',
          `Local DevServer on 127.0.0.1:${targetPort} is not listening or unreachable on worker host.`,
        );
      }
    }

    const now = Date.now();
    const expiresAtMs = now + ttlMs;

    if (
      existingForRenew &&
      !existingForRenew.revoked &&
      existingForRenew.expiresAtMs > now &&
      existingForRenew.controllerDeviceId === controller.controllerDeviceId &&
      existingForRenew.clientFingerprint === clientFp &&
      existingForRenew.targetPort === targetPort
    ) {
      existingForRenew.ttlSeconds = ttlSeconds;
      existingForRenew.expiresAtMs = expiresAtMs;
      existingForRenew.preferredConnectIp = preferredConnectIp;
      this.armExpiryTimer(existingForRenew, ttlMs);
      return this.toWorkerLeaseDto(existingForRenew);
    }

    const leaseId = `dvl_${randomBytes(12).toString('hex')}`;
    const ticket = randomBytes(32).toString('hex');

    const record: WorkerLeaseRecord = {
      leaseId,
      ticket,
      controllerDeviceId: controller.controllerDeviceId,
      clientFingerprint: clientFp,
      canonicalHost,
      connectIps,
      preferredConnectIp,
      targetPort,
      ttlSeconds,
      expiresAtMs,
      revoked: false,
      expiryTimer: null,
      activeBridges: new Set(),
    };

    this.leasesById.set(leaseId, record);
    let controllerSet = this.leaseIdsByController.get(controller.controllerDeviceId);
    if (!controllerSet) {
      controllerSet = new Set();
      this.leaseIdsByController.set(controller.controllerDeviceId, controllerSet);
    }
    controllerSet.add(leaseId);
    this.armExpiryTimer(record, ttlMs);

    return this.toWorkerLeaseDto(record);
  }

  /**
   * Authenticate and bridge an incoming `ALPN = 'tinadec-peer-devserver/1'` TLS connection.
   */
  async handleIncomingDevServerSocket(input: HandleIncomingDevServerSocketInput): Promise<void> {
    const { tlsSocket } = input;
    const timeoutMs = input.handshakeTimeoutMs ?? 8_000;
    const extractFp = input.extractFingerprintFn ?? extractPeerCertFingerprint;

    const tlsStream = new DevServerFramedStream(tlsSocket);

    // 1. Read the opening `devserver.open` header frame first (matching `performServerHandshake`
    //    so TLS 1.3 post-handshake state in Bun/BoringSSL is completely settled before cert inspection).
    let openFrame: DevServerOpenFrame;
    try {
      const frame = await tlsStream.readHandshake(timeoutMs);
      if (frame.type === 'devserver.open-reject') {
        tlsStream.sendRejectAndClose('handshake_failed', 'Client aborted DevServer handshake.');
        return;
      }
      if (frame.type !== 'devserver.open') {
        tlsStream.sendRejectAndClose(
          'handshake_failed',
          `Expected devserver.open frame, received '${frame.type}'.`,
        );
        return;
      }
      openFrame = frame;
    } catch (err) {
      const code = err instanceof PeerProtocolError ? err.code : 'handshake_failed';
      tlsStream.sendRejectAndClose(
        code,
        err instanceof Error ? err.message : 'Invalid DevServer open frame.',
      );
      return;
    }

    if (!input.isInboundEnabled()) {
      tlsStream.sendRejectAndClose('inbound_disabled', 'Inbound peer access is disabled on this device.');
      return;
    }

    // 2. Extract authoritative X.509 DER SHA-256 fingerprint from the TLS socket.
    const actualClientFp = extractFp(tlsSocket);
    if (!actualClientFp) {
      tlsStream.sendRejectAndClose(
        'unauthorized',
        'Client TLS X.509 certificate is required for DevServer tunnel.',
      );
      return;
    }

    const controllerDeviceId = String(openFrame.controller_device_id ?? '').trim();
    const leaseId = String(openFrame.lease_id ?? '').trim();
    const ticket = String(openFrame.ticket ?? '').trim();

    if (!controllerDeviceId || !leaseId || !ticket) {
      tlsStream.sendRejectAndClose(
        'unauthorized',
        'devserver.open requires non-empty lease_id, ticket, and controller_device_id.',
      );
      return;
    }

    // 3. Re-verify against Core `peer_grants` + pinned DER fingerprint + role !== 'observer'.
    const grantDecision = await input.verifyControllerGrant({
      controllerDeviceId,
      clientFingerprint: actualClientFp,
    });

    if (!grantDecision.accepted || !grantDecision.policy) {
      const rejectCode: PeerErrorCode =
        grantDecision.code && isPeerErrorCode(grantDecision.code) ? grantDecision.code : 'unauthorized';
      tlsStream.sendRejectAndClose(
        rejectCode,
        grantDecision.message || 'Peer controller grant is revoked or unauthorized.',
      );
      return;
    }

    if (grantDecision.policy.role === 'observer') {
      tlsStream.sendRejectAndClose(
        'policy_denied',
        'Observer role is forbidden from opening DevServer tunnels.',
      );
      return;
    }

    const canonicalControllerId = grantDecision.controllerDeviceId ?? controllerDeviceId;

    // 4. Verify lease existence, controller binding, non-expiry, pinned cert, and constant-time ticket.
    const lease = this.leasesById.get(leaseId);
    const now = Date.now();
    if (!lease || lease.revoked || now >= lease.expiresAtMs) {
      if (lease && now >= lease.expiresAtMs) {
        this.teardownLeaseRecord(lease);
      }
      tlsStream.sendRejectAndClose(
        'unauthorized',
        `DevServer lease '${leaseId}' is invalid, expired, or revoked.`,
      );
      return;
    }

    if (lease.controllerDeviceId !== canonicalControllerId) {
      tlsStream.sendRejectAndClose(
        'policy_denied',
        'DevServer lease belongs to a different controller device.',
      );
      return;
    }

    try {
      verifyPinnedFingerprint(actualClientFp, lease.clientFingerprint, 'DevServer tunnel client certificate');
    } catch (err) {
      const code = err instanceof PeerProtocolError ? err.code : 'fingerprint_mismatch';
      tlsStream.sendRejectAndClose(
        code,
        err instanceof Error ? err.message : 'Client certificate fingerprint mismatch.',
      );
      return;
    }

    if (!constantTimeHexEquals(ticket, lease.ticket)) {
      tlsStream.sendRejectAndClose('unauthorized', 'DevServer lease ticket verification failed.');
      return;
    }

    if (isForbiddenDevServerPort(lease.targetPort, this.options.getDynamicForbiddenPorts?.())) {
      this.teardownLeaseRecord(lease);
      tlsStream.sendRejectAndClose('policy_denied', `Target port ${lease.targetPort} is forbidden.`);
      return;
    }

    // 5. Dial the worker's local loopback DevServer (`127.0.0.1` / `::1` ONLY).
    const orderedIps: ('127.0.0.1' | '::1')[] =
      lease.connectIps.length > 1 && lease.preferredConnectIp === '::1'
        ? ['::1', '127.0.0.1']
        : [...lease.connectIps];

    let upstreamSocket: NetSocket;
    try {
      const connected = await connectLoopbackTcp(orderedIps, lease.targetPort, 3_000);
      upstreamSocket = connected.socket;
      lease.preferredConnectIp = connected.connectedIp;
    } catch (err) {
      tlsStream.sendRejectAndClose(
        'unreachable',
        `Worker loopback DevServer on port ${lease.targetPort} is unreachable: ${err instanceof Error ? err.message : String(err)}`,
      );
      return;
    }

    if (
      !input.isInboundEnabled() ||
      lease.revoked ||
      Date.now() >= lease.expiresAtMs ||
      tlsStream.isClosed
    ) {
      upstreamSocket.destroy();
      tlsStream.sendRejectAndClose(
        'unauthorized',
        'DevServer lease expired or revoked during stream setup.',
      );
      return;
    }

    // 6. Send `devserver.open-ack` and bridge raw TCP <-> TLS streams bidirectionally.
    try {
      tlsStream.sendHandshake({
        type: 'devserver.open-ack',
        lease_id: lease.leaseId,
        target_port: lease.targetPort,
      });
    } catch {
      upstreamSocket.destroy();
      tlsStream.destroy();
      return;
    }

    const bridge = bridgeFramedTlsWithTcpSocket(tlsStream, upstreamSocket, {
      onClosed: () => {
        lease.activeBridges.delete(bridge);
      },
    });
    lease.activeBridges.add(bridge);
  }

  revokeLeaseForController(controllerDeviceId: string, leaseId: string): boolean {
    const lease = this.leasesById.get(leaseId);
    if (!lease || lease.controllerDeviceId !== controllerDeviceId) {
      return false;
    }
    this.teardownLeaseRecord(lease);
    return true;
  }

  /**
   * Synchronously revoke all DevServer leases and destroy all active ALPN tunnel sockets
   * belonging to `controllerDeviceId` (< 5ms).
   */
  revokeControllerLeases(controllerDeviceId: string, _reason = 'Controller revoked.'): number {
    const ids = this.leaseIdsByController.get(controllerDeviceId);
    if (!ids || ids.size === 0) return 0;
    const snapshot = [...ids];
    this.leaseIdsByController.delete(controllerDeviceId);
    let count = 0;
    for (const id of snapshot) {
      const lease = this.leasesById.get(id);
      if (lease) {
        this.teardownLeaseRecord(lease);
        count += 1;
      }
    }
    return count;
  }

  revokeAll(_reason = 'PeerTlsServer shutting down.'): void {
    const all = [...this.leasesById.values()];
    this.leasesById.clear();
    this.leaseIdsByController.clear();
    for (const lease of all) {
      this.teardownLeaseRecord(lease);
    }
  }

  private armExpiryTimer(record: WorkerLeaseRecord, ttlMs: number): void {
    if (record.expiryTimer) {
      clearTimeout(record.expiryTimer);
    }
    record.expiryTimer = setTimeout(() => {
      record.expiryTimer = null;
      this.teardownLeaseRecord(record);
    }, ttlMs);
    record.expiryTimer.unref?.();
  }

  private teardownLeaseRecord(record: WorkerLeaseRecord): void {
    if (record.revoked && !this.leasesById.has(record.leaseId)) return;
    record.revoked = true;
    if (record.expiryTimer) {
      clearTimeout(record.expiryTimer);
      record.expiryTimer = null;
    }
    this.leasesById.delete(record.leaseId);
    const set = this.leaseIdsByController.get(record.controllerDeviceId);
    if (set) {
      set.delete(record.leaseId);
      if (set.size === 0) {
        this.leaseIdsByController.delete(record.controllerDeviceId);
      }
    }

    const bridges = [...record.activeBridges];
    record.activeBridges.clear();
    for (const bridge of bridges) {
      bridge.destroy();
    }
  }

  private toWorkerLeaseDto(record: WorkerLeaseRecord): WorkerDevServerLeaseDto {
    return {
      lease_id: record.leaseId,
      ticket: record.ticket,
      controller_device_id: record.controllerDeviceId,
      target_host: record.canonicalHost,
      target_port: record.targetPort,
      ttl_seconds: record.ttlSeconds,
      expires_at_ms: record.expiresAtMs,
    };
  }
}

// ── 2. Controller-Side Local Loopback Port Mapper (`127.0.0.1:<ephemeralPort>`) ──

export interface DialDevServerTunnelParams {
  deviceId: string;
  leaseId: string;
  ticket: string;
  controllerDeviceId: string;
}

export interface DialedDevServerStream {
  tlsStream: DevServerFramedStream;
}

export interface PeerDevServerPortMapperOptions {
  dialDevServerStream: (params: DialDevServerTunnelParams) => Promise<DialedDevServerStream>;
  isDeviceControlReady: (deviceId: string) => boolean;
}

interface LocalMappedLeaseEntry {
  leaseId: string;
  deviceId: string;
  controllerDeviceId: string;
  ticket: string;
  targetHost: string;
  targetPort: number;
  localPort: number;
  ttlSeconds: number;
  expiresAtMs: number;
  closed: boolean;
  server: NetServer;
  expiryTimer: ReturnType<typeof setTimeout> | null;
  rawClientSockets: Set<NetSocket>;
  activeBridges: Set<ActivePipeBridge>;
}

export class PeerDevServerPortMapper {
  private readonly options: PeerDevServerPortMapperOptions;
  private readonly mappingsByLeaseId = new Map<string, LocalMappedLeaseEntry>();
  private readonly leaseIdsByDevice = new Map<string, Set<string>>();

  constructor(options: PeerDevServerPortMapperOptions) {
    this.options = options;
  }

  findActiveMappingByTarget(
    deviceId: string,
    targetPort: number,
    targetHost = '127.0.0.1',
  ): MappedDevServerLeaseDto | null {
    const ids = this.leaseIdsByDevice.get(deviceId);
    if (!ids) return null;
    const normHost = targetHost.trim().toLowerCase() || '127.0.0.1';
    const now = Date.now();
    for (const id of ids) {
      const entry = this.mappingsByLeaseId.get(id);
      if (
        entry &&
        !entry.closed &&
        entry.expiresAtMs > now &&
        entry.targetPort === targetPort &&
        entry.targetHost === normHost
      ) {
        return this.toDto(entry);
      }
    }
    return null;
  }

  listMappings(deviceId?: string): MappedDevServerLeaseDto[] {
    const now = Date.now();
    const out: MappedDevServerLeaseDto[] = [];
    for (const entry of this.mappingsByLeaseId.values()) {
      if (entry.closed || entry.expiresAtMs <= now) continue;
      if (deviceId && entry.deviceId !== deviceId) continue;
      out.push(this.toDto(entry));
    }
    return out;
  }

  async openOrRefreshMapping(input: {
    deviceId: string;
    lease: WorkerDevServerLeaseDto;
    fallbackControllerDeviceId: string;
  }): Promise<MappedDevServerLeaseDto> {
    const { deviceId, lease } = input;
    const existing = this.mappingsByLeaseId.get(lease.lease_id);
    const now = Date.now();

    if (existing && !existing.closed && existing.deviceId === deviceId) {
      existing.ticket = lease.ticket;
      existing.ttlSeconds = lease.ttl_seconds;
      existing.expiresAtMs = lease.expires_at_ms;
      this.armExpiryTimer(existing, Math.max(50, lease.expires_at_ms - now));
      return this.toDto(existing);
    }

    const controllerDeviceId = lease.controller_device_id || input.fallbackControllerDeviceId;
    const rawClientSockets = new Set<NetSocket>();
    const activeBridges = new Set<ActivePipeBridge>();

    let entryRef: LocalMappedLeaseEntry | null = null;

    const server = net.createServer((localSocket) => {
      if (
        !entryRef ||
        entryRef.closed ||
        Date.now() >= entryRef.expiresAtMs ||
        !this.options.isDeviceControlReady(deviceId)
      ) {
        localSocket.destroy();
        return;
      }

      const activeEntry = entryRef;
      localSocket.setNoDelay(true);
      rawClientSockets.add(localSocket);
      localSocket.once('close', () => {
        rawClientSockets.delete(localSocket);
      });

      const earlyChunks: Buffer[] = [];
      let earlyBytes = 0;
      let localFailed = false;

      const onEarlyData = (chunk: Buffer | Uint8Array) => {
        if (chunk.byteLength === 0 || localFailed) return;
        if (earlyBytes + chunk.byteLength > MAX_PRE_STREAM_BUFFER_BYTES) {
          localFailed = true;
          localSocket.destroy();
          return;
        }
        const copy = Buffer.isBuffer(chunk)
          ? Buffer.from(chunk)
          : Buffer.from(chunk.buffer, chunk.byteOffset, chunk.byteLength);
        earlyChunks.push(copy);
        earlyBytes += copy.byteLength;
      };
      const onEarlyErrorOrClose = () => {
        localFailed = true;
      };

      localSocket.on('data', onEarlyData);
      localSocket.once('error', onEarlyErrorOrClose);
      localSocket.once('close', onEarlyErrorOrClose);

      void (async () => {
        let dialed: DialedDevServerStream | null = null;
        try {
          dialed = await this.options.dialDevServerStream({
            deviceId: activeEntry.deviceId,
            leaseId: activeEntry.leaseId,
            ticket: activeEntry.ticket,
            controllerDeviceId: activeEntry.controllerDeviceId,
          });

          if (localFailed || activeEntry.closed || localSocket.destroyed) {
            dialed.tlsStream.destroy();
            localSocket.destroy();
            return;
          }

          localSocket.off('data', onEarlyData);
          localSocket.off('error', onEarlyErrorOrClose);
          localSocket.off('close', onEarlyErrorOrClose);

          const initialToTls = earlyChunks.length > 0 ? Buffer.concat(earlyChunks, earlyBytes) : undefined;
          earlyChunks.length = 0;

          const bridge = bridgeFramedTlsWithTcpSocket(dialed.tlsStream, localSocket, {
            initialToTls,
            onClosed: () => {
              activeBridges.delete(bridge);
            },
          });
          activeBridges.add(bridge);
        } catch {
          localSocket.off('data', onEarlyData);
          localSocket.off('error', onEarlyErrorOrClose);
          localSocket.off('close', onEarlyErrorOrClose);
          if (dialed) {
            dialed.tlsStream.destroy();
          }
          if (!localSocket.destroyed) {
            try {
              localSocket.end(
                'HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 34\r\n\r\nDevServer preview tunnel rejected.',
              );
              setTimeout(() => localSocket.destroy(), 25).unref?.();
            } catch {
              localSocket.destroy();
            }
          }
        }
      })();
    });

    const localPort = await new Promise<number>((resolve, reject) => {
      const onError = (err: Error) => {
        server.off('listening', onListening);
        reject(err);
      };
      const onListening = () => {
        server.off('error', onError);
        const addr = server.address();
        if (!addr || typeof addr !== 'object') {
          server.close();
          reject(new Error('Failed to resolve bound ephemeral port on 127.0.0.1.'));
          return;
        }
        resolve(addr.port);
      };
      server.once('error', onError);
      server.once('listening', onListening);
      // Bind strictly to 127.0.0.1 loopback (NEVER 0.0.0.0) so LAN neighbours cannot piggyback.
      server.listen(0, '127.0.0.1');
    });

    const entry: LocalMappedLeaseEntry = {
      leaseId: lease.lease_id,
      deviceId,
      controllerDeviceId,
      ticket: lease.ticket,
      targetHost: (lease.target_host || '127.0.0.1').toLowerCase(),
      targetPort: lease.target_port,
      localPort,
      ttlSeconds: lease.ttl_seconds,
      expiresAtMs: lease.expires_at_ms,
      closed: false,
      server,
      expiryTimer: null,
      rawClientSockets,
      activeBridges,
    };
    entryRef = entry;

    this.mappingsByLeaseId.set(entry.leaseId, entry);
    let deviceSet = this.leaseIdsByDevice.get(deviceId);
    if (!deviceSet) {
      deviceSet = new Set();
      this.leaseIdsByDevice.set(deviceId, deviceSet);
    }
    deviceSet.add(entry.leaseId);

    const remainingMs = Math.max(50, lease.expires_at_ms - Date.now());
    this.armExpiryTimer(entry, remainingMs);

    return this.toDto(entry);
  }

  async closeMapping(leaseId: string, _reason = 'Closed'): Promise<boolean> {
    const entry = this.mappingsByLeaseId.get(leaseId);
    if (!entry) return false;
    await this.shutdownEntry(entry);
    return true;
  }

  async closeDeviceMappings(deviceId: string, _reason = 'Device disconnected'): Promise<number> {
    const ids = this.leaseIdsByDevice.get(deviceId);
    if (!ids || ids.size === 0) return 0;
    const snapshot = [...ids];
    this.leaseIdsByDevice.delete(deviceId);
    const entries: LocalMappedLeaseEntry[] = [];
    for (const id of snapshot) {
      const entry = this.mappingsByLeaseId.get(id);
      if (entry) {
        this.initiateImmediateShutdown(entry);
        entries.push(entry);
      }
    }
    await Promise.all(entries.map((e) => this.awaitServerClosed(e)));
    return entries.length;
  }

  async closeAll(_reason = 'Stopping'): Promise<void> {
    const entries = [...this.mappingsByLeaseId.values()];
    this.mappingsByLeaseId.clear();
    this.leaseIdsByDevice.clear();
    for (const entry of entries) {
      this.initiateImmediateShutdown(entry);
    }
    await Promise.all(entries.map((e) => this.awaitServerClosed(e)));
  }

  private armExpiryTimer(entry: LocalMappedLeaseEntry, ttlMs: number): void {
    if (entry.expiryTimer) {
      clearTimeout(entry.expiryTimer);
    }
    entry.expiryTimer = setTimeout(() => {
      entry.expiryTimer = null;
      void this.shutdownEntry(entry);
    }, ttlMs);
    entry.expiryTimer.unref?.();
  }

  private initiateImmediateShutdown(entry: LocalMappedLeaseEntry): void {
    if (entry.closed) return;
    entry.closed = true;

    if (entry.expiryTimer) {
      clearTimeout(entry.expiryTimer);
      entry.expiryTimer = null;
    }

    this.mappingsByLeaseId.delete(entry.leaseId);
    const devSet = this.leaseIdsByDevice.get(entry.deviceId);
    if (devSet) {
      devSet.delete(entry.leaseId);
      if (devSet.size === 0) {
        this.leaseIdsByDevice.delete(entry.deviceId);
      }
    }

    try {
      entry.server.close();
    } catch {
      // ignore
    }

    for (const bridge of [...entry.activeBridges]) {
      bridge.destroy();
    }
    entry.activeBridges.clear();

    for (const sock of [...entry.rawClientSockets]) {
      try {
        sock.destroy();
      } catch {
        // ignore
      }
    }
    entry.rawClientSockets.clear();
  }

  private awaitServerClosed(entry: LocalMappedLeaseEntry): Promise<void> {
    if (!entry.server.listening) {
      return Promise.resolve();
    }
    return new Promise<void>((resolve) => {
      const fallback = setTimeout(() => resolve(), 50);
      fallback.unref?.();
      entry.server.once('close', () => {
        clearTimeout(fallback);
        resolve();
      });
    });
  }

  private async shutdownEntry(entry: LocalMappedLeaseEntry): Promise<void> {
    this.initiateImmediateShutdown(entry);
    await this.awaitServerClosed(entry);
  }

  private toDto(entry: LocalMappedLeaseEntry): MappedDevServerLeaseDto {
    return {
      lease_id: entry.leaseId,
      device_id: entry.deviceId,
      target_port: entry.targetPort,
      local_port: entry.localPort,
      preview_url: `http://127.0.0.1:${entry.localPort}`,
      ttl_seconds: entry.ttlSeconds,
      expires_at_ms: entry.expiresAtMs,
    };
  }
}

/**
 * Open a Controller -> Worker mTLS socket using `ALPN = ['tinadec-peer-devserver/1']`,
 * pin the remote server's DER SHA-256 certificate fingerprint, send `devserver.open`,
 * and await `devserver.open-ack`.
 */
export async function openClientDevServerAlpnStream(options: {
  host: string;
  port: number;
  certPem: string;
  privateKeyPem: string;
  pinnedServerFingerprint: string;
  leaseId: string;
  ticket: string;
  controllerDeviceId: string;
  connectTimeoutMs?: number;
  handshakeTimeoutMs?: number;
  extractFingerprintFn?: (socket: Pick<TLSSocket, 'getPeerCertificate'>) => string | null;
}): Promise<DialedDevServerStream> {
  const connectTimeoutMs = options.connectTimeoutMs ?? 5_000;
  const handshakeTimeoutMs = options.handshakeTimeoutMs ?? 8_000;
  const extractFp = options.extractFingerprintFn ?? extractPeerCertFingerprint;

  const tlsSocket = await new Promise<TLSSocket>((resolve, reject) => {
    let settled = false;
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      try {
        sock.destroy();
      } catch {
        // ignore
      }
      reject(
        new PeerProtocolError(
          'timeout',
          `DevServer mTLS connect to ${options.host}:${options.port} timed out after ${connectTimeoutMs}ms.`,
        ),
      );
    }, connectTimeoutMs);
    timer.unref?.();

    const sock = tls.connect(
      {
        host: options.host,
        port: options.port,
        cert: options.certPem,
        key: options.privateKeyPem,
        rejectUnauthorized: false,
        ALPNProtocols: [PEER_ALPN_DEVSERVER],
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
      reject(
        new PeerProtocolError(
          err.code === 'ETIMEDOUT' ? 'timeout' : 'unreachable',
          `Cannot reach remote DevServer tunnel ${options.host}:${options.port}: ${err.message}`,
        ),
      );
    });
  });

  const tlsStream = new DevServerFramedStream(tlsSocket);

  try {
    if (tlsSocket.alpnProtocol !== PEER_ALPN_DEVSERVER) {
      throw new PeerProtocolError(
        'handshake_failed',
        `Remote peer did not negotiate ALPN '${PEER_ALPN_DEVSERVER}' (got '${String(tlsSocket.alpnProtocol)}').`,
      );
    }

    const actualServerFp = extractFp(tlsSocket);
    try {
      verifyPinnedFingerprint(
        actualServerFp,
        options.pinnedServerFingerprint,
        'Remote DevServer TLS server certificate',
      );
    } catch (err) {
      if (err instanceof PeerProtocolError) {
        try {
          tlsStream.sendHandshake({
            type: 'devserver.open-reject',
            code: err.code,
            message: err.message,
          });
          await tlsStream.readHandshake(250).catch(() => undefined);
        } catch {
          // ignore
        }
      }
      throw err;
    }

    tlsStream.sendHandshake({
      type: 'devserver.open',
      lease_id: options.leaseId,
      ticket: options.ticket,
      controller_device_id: options.controllerDeviceId,
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
