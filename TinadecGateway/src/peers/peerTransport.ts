/**
 * Pluggable Peer Transport Channel Abstraction (Spec §5.1, §6.2.3):
 *
 * Decouples raw transport stream establishment (`IPeerTransportChannel` / `PeerTransportStream`)
 * from upper-layer end-to-end ECDSA P-256 X.509 DER SHA-256 fingerprint pinning, 4-step
 * `peer.hello` handshaking, and multiplexed HTTP/SSE/WS/ALPN DevServer streams:
 *
 * 1. `LanMtlsTransport` (`transport = 'lan'`):
 *    Direct TCP + mTLS over LAN / Tailscale / WireGuard `IP:Port`.
 *
 * 2. `BlindRelayTransport` (`transport = 'relay'`):
 *    Connects to a stateless blind-forwarding relay (`POST /v1/devices/register`,
 *    `POST /v1/rendezvous`, `POST /v1/links/tickets`, `WS /v1/inbox`, `WS /v1/links/:ticketId`),
 *    adapts the binary WebSocket link into a `PeerTransportStream` (`Duplex`), and upgrades
 *    an end-to-end `tls.TLSSocket` directly on top of that stream!
 *
 * Zero-Trust Relay Security Invariant:
 * - The Relay ONLY sees opaque TLS 1.3 ciphertext record frames (`0x16`, `0x17`, ...).
 * - The Relay can NEVER decrypt payloads and can NEVER forge either endpoint's X.509
 *   certificate without triggering immediate `PeerProtocolError('fingerprint_mismatch')`.
 */

import net, { type Socket as NetSocket } from 'node:net';
import { Duplex } from 'node:stream';
import tls, { type Server as TlsServer, type TLSSocket } from 'node:tls';
import {
  DEFAULT_PEER_LISTEN_PORT,
  PEER_ALPN_HTTP,
  PEER_ALPN_PROTOCOLS,
  PeerProtocolError,
} from './peerProtocol.js';

export type PeerTransportKind = 'lan' | 'relay';

/**
 * Full-duplex byte stream produced by a transport channel (either a raw binary carrier
 * stream such as WebSocket-over-Relay prior to TLS upgrade, or a direct TCP/TLS socket).
 */
export interface PeerTransportStream extends Duplex {
  readonly transportKind?: PeerTransportKind;
  readonly remoteAddressLabel?: string;
}

export interface PeerTransportConnectOptions {
  address: string;
  certPem: string;
  privateKeyPem: string;
  alpnProtocols?: readonly string[];
  connectTimeoutMs?: number;
  /** Optional pairing code when resolving a `rendezvous` relay address. */
  pairingCode?: string;
  signal?: AbortSignal;
}

export interface PeerTransportConnectedTunnel {
  transport: PeerTransportKind;
  normalizedAddress: string;
  tlsSocket: TLSSocket;
  rawStream: PeerTransportStream;
}

export interface PeerTransportAcceptedTunnel {
  transport: PeerTransportKind;
  tlsSocket: TLSSocket;
  rawStream: PeerTransportStream;
}

export interface PeerTransportListenOptions {
  host?: string;
  port?: number;
  localDeviceId: string;
  certPem: string;
  privateKeyPem: string;
  alpnProtocols?: readonly string[];
  onConnection: (accepted: PeerTransportAcceptedTunnel) => void;
}

export interface PeerTransportListenerHandle {
  readonly transport: PeerTransportKind;
  readonly boundPort: number | null;
  readonly relayDeviceId?: string;
  readonly relayAddress?: string;
  publishRendezvous?(pairingCode: string, ttlSeconds?: number): Promise<{
    relay_device_id: string;
    expires_at_ms: number;
  }>;
  close(reason?: string): Promise<void>;
}

export interface IPeerTransportChannel {
  readonly kind: PeerTransportKind;
  canHandleAddress(address: string): boolean;
  connect(options: PeerTransportConnectOptions): Promise<PeerTransportConnectedTunnel>;
  listen?(options: PeerTransportListenOptions): Promise<PeerTransportListenerHandle>;
}

// ── Address Parsing & Transport Detection Helpers ────────────────────────────

export function isRelayPeerAddress(rawAddress: string): boolean {
  const trimmed = rawAddress.trim().toLowerCase();
  return (
    trimmed.startsWith('relay://') ||
    trimmed.startsWith('relay+http://') ||
    trimmed.startsWith('relay+https://') ||
    trimmed.startsWith('relay+ws://') ||
    trimmed.startsWith('relay+wss://') ||
    trimmed.startsWith('relay:')
  );
}

export function inferPeerTransportKind(
  rawAddress: string,
  explicitTransport?: PeerTransportKind | null,
): PeerTransportKind {
  if (explicitTransport === 'relay' || explicitTransport === 'lan') {
    return explicitTransport;
  }
  return isRelayPeerAddress(rawAddress) ? 'relay' : 'lan';
}

export function parseLanPeerAddress(rawAddress: string): {
  host: string;
  port: number;
  normalized: string;
} {
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

export interface ParsedRelayEndpoint {
  httpBaseUrl: string;
  wsBaseUrl: string;
  targetDeviceId: string | null;
  rendezvousCode: string | null;
}

function normalizeRelayBaseUrls(rawRelayUrl: string): { httpBaseUrl: string; wsBaseUrl: string } {
  const trimmed = rawRelayUrl.trim();
  if (!trimmed) {
    throw new PeerProtocolError('unreachable', 'Relay base URL is not configured.');
  }

  let normalized = trimmed;
  if (normalized.startsWith('relay+http://')) {
    normalized = `http://${normalized.slice('relay+http://'.length)}`;
  } else if (normalized.startsWith('relay+https://')) {
    normalized = `https://${normalized.slice('relay+https://'.length)}`;
  } else if (normalized.startsWith('relay+ws://')) {
    normalized = `ws://${normalized.slice('relay+ws://'.length)}`;
  } else if (normalized.startsWith('relay+wss://')) {
    normalized = `wss://${normalized.slice('relay+wss://'.length)}`;
  } else if (normalized.startsWith('relay://')) {
    const rest = normalized.slice('relay://'.length);
    const hostPart = rest.split('/')[0]?.toLowerCase() ?? '';
    const isLocal =
      hostPart.startsWith('127.') ||
      hostPart.startsWith('localhost') ||
      hostPart.startsWith('[::1]');
    normalized = `${isLocal ? 'http' : 'https'}://${rest}`;
  }

  let parsed: URL;
  try {
    parsed = new URL(normalized);
  } catch {
    throw new PeerProtocolError('unreachable', `Invalid relay URL '${rawRelayUrl}'.`);
  }

  const isSecure = parsed.protocol === 'https:' || parsed.protocol === 'wss:';
  const basePath = parsed.pathname.replace(/\/+$/, '');
  const httpBaseUrl = `${isSecure ? 'https:' : 'http:'}//${parsed.host}${basePath}`;
  const wsBaseUrl = `${isSecure ? 'wss:' : 'ws:'}//${parsed.host}${basePath}`;
  return { httpBaseUrl, wsBaseUrl };
}

/**
 * Parse supported Relay peer address forms:
 * - `relay://<relayHost>:<port>/<targetRelayDeviceId>`
 * - `relay+http://<relayHost>:<port>/<targetRelayDeviceId>` / `relay+https://...`
 * - `relay://<relayHost>:<port>/rendezvous/<pairingCode>` (or `?code=<pairingCode>`)
 * - `relay:<targetRelayDeviceId>` or `<targetRelayDeviceId>` (uses default `fallbackRelayUrl`)
 * - `relay:rendezvous:<pairingCode>` (uses default `fallbackRelayUrl`)
 */
export function parseRelayPeerAddress(
  rawAddress: string,
  fallbackRelayUrl?: string,
  pairingCodeHint?: string,
): ParsedRelayEndpoint {
  const trimmed = rawAddress.trim();
  if (!trimmed) {
    throw new PeerProtocolError('unreachable', 'Relay peer address is empty.');
  }

  if (
    trimmed.startsWith('relay://') ||
    trimmed.startsWith('relay+http://') ||
    trimmed.startsWith('relay+https://') ||
    trimmed.startsWith('relay+ws://') ||
    trimmed.startsWith('relay+wss://')
  ) {
    let schemeNormalized = trimmed;
    let explicitScheme: 'http' | 'https' | null = null;
    if (schemeNormalized.startsWith('relay+http://') || schemeNormalized.startsWith('relay+ws://')) {
      explicitScheme = 'http';
      schemeNormalized = `http://${schemeNormalized.replace(/^relay\+(?:http|ws):\/\//i, '')}`;
    } else if (
      schemeNormalized.startsWith('relay+https://') ||
      schemeNormalized.startsWith('relay+wss://')
    ) {
      explicitScheme = 'https';
      schemeNormalized = `https://${schemeNormalized.replace(/^relay\+(?:https|wss):\/\//i, '')}`;
    } else {
      const rest = schemeNormalized.slice('relay://'.length);
      const hostPart = rest.split('/')[0]?.toLowerCase() ?? '';
      const isLocal =
        hostPart.startsWith('127.') ||
        hostPart.startsWith('localhost') ||
        hostPart.startsWith('[::1]') ||
        rest.includes('insecure=1');
      explicitScheme = isLocal ? 'http' : 'https';
      schemeNormalized = `${explicitScheme}://${rest}`;
    }

    let u: URL;
    try {
      u = new URL(schemeNormalized);
    } catch {
      throw new PeerProtocolError('unreachable', `Malformed relay peer address '${rawAddress}'.`);
    }

    const segments = u.pathname
      .split('/')
      .map((s) => decodeURIComponent(s).trim())
      .filter(Boolean);
    const queryCode = u.searchParams.get('code')?.trim() || null;

    let targetDeviceId: string | null = null;
    let rendezvousCode: string | null = queryCode;

    if (segments.length >= 2 && segments[segments.length - 2]?.toLowerCase() === 'rendezvous') {
      rendezvousCode = segments[segments.length - 1] ?? null;
      segments.splice(segments.length - 2, 2);
    } else if (segments.length === 1 && segments[0]?.toLowerCase() === 'rendezvous') {
      rendezvousCode = queryCode ?? pairingCodeHint?.trim() ?? null;
      segments.splice(0, 1);
    } else if (segments.length >= 1) {
      targetDeviceId = segments.pop() ?? null;
    } else if (pairingCodeHint?.trim()) {
      rendezvousCode = pairingCodeHint.trim();
    }

    const prefixPath = segments.length > 0 ? `/${segments.join('/')}` : '';
    const httpBaseUrl = `${explicitScheme}://${u.host}${prefixPath}`;
    const wsBaseUrl = `${explicitScheme === 'https' ? 'wss' : 'ws'}://${u.host}${prefixPath}`;

    if (!targetDeviceId && !rendezvousCode) {
      throw new PeerProtocolError(
        'unreachable',
        `Relay address '${rawAddress}' must include a target relay_device_id or rendezvous code.`,
      );
    }

    return {
      httpBaseUrl,
      wsBaseUrl,
      targetDeviceId,
      rendezvousCode,
    };
  }

  if (!fallbackRelayUrl) {
    throw new PeerProtocolError(
      'unreachable',
      `Relay URL is not configured to resolve shorthand address '${rawAddress}'.`,
    );
  }

  const { httpBaseUrl, wsBaseUrl } = normalizeRelayBaseUrls(fallbackRelayUrl);
  const stripped = trimmed.toLowerCase().startsWith('relay:') ? trimmed.slice(6).trim() : trimmed;
  if (stripped.toLowerCase().startsWith('rendezvous:')) {
    const code = stripped.slice('rendezvous:'.length).trim() || pairingCodeHint?.trim() || null;
    if (!code) {
      throw new PeerProtocolError('unreachable', 'Rendezvous pairing code is empty.');
    }
    return { httpBaseUrl, wsBaseUrl, targetDeviceId: null, rendezvousCode: code };
  }
  if (stripped.toLowerCase() === 'rendezvous') {
    const code = pairingCodeHint?.trim() || null;
    if (!code) {
      throw new PeerProtocolError('unreachable', 'Rendezvous pairing code is required.');
    }
    return { httpBaseUrl, wsBaseUrl, targetDeviceId: null, rendezvousCode: code };
  }

  return {
    httpBaseUrl,
    wsBaseUrl,
    targetDeviceId: stripped,
    rendezvousCode: null,
  };
}

export function formatCanonicalRelayAddress(httpBaseUrl: string, targetRelayDeviceId: string): string {
  const u = new URL(httpBaseUrl);
  const basePath = u.pathname.replace(/\/+$/, '');
  const isDefaultScheme =
    (u.protocol === 'http:' &&
      (u.hostname === '127.0.0.1' || u.hostname === 'localhost' || u.hostname === '::1' || u.hostname === '[::1]')) ||
    (u.protocol === 'https:' &&
      u.hostname !== '127.0.0.1' &&
      u.hostname !== 'localhost' &&
      u.hostname !== '::1' &&
      u.hostname !== '[::1]');
  const scheme = isDefaultScheme
    ? 'relay'
    : u.protocol === 'https:'
      ? 'relay+https'
      : 'relay+http';
  return `${scheme}://${u.host}${basePath}/${encodeURIComponent(targetRelayDeviceId)}`;
}

// ── 1. LanMtlsTransport (Default TCP + mTLS Channel) ─────────────────────────

export class LanMtlsTransport implements IPeerTransportChannel {
  readonly kind: PeerTransportKind = 'lan';

  canHandleAddress(address: string): boolean {
    return !isRelayPeerAddress(address);
  }

  async connect(options: PeerTransportConnectOptions): Promise<PeerTransportConnectedTunnel> {
    const { host, port, normalized } = parseLanPeerAddress(options.address);
    const timeoutMs = options.connectTimeoutMs ?? 5_000;
    const alpn = options.alpnProtocols?.length ? [...options.alpnProtocols] : [PEER_ALPN_HTTP];

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
            `TLS connection to ${host}:${port} timed out after ${timeoutMs}ms.`,
          ),
        );
      }, timeoutMs);
      timer.unref?.();

      const sock = tls.connect(
        {
          host,
          port,
          cert: options.certPem,
          key: options.privateKeyPem,
          rejectUnauthorized: false,
          ALPNProtocols: alpn,
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
        if (err.code === 'ETIMEDOUT') {
          reject(new PeerProtocolError('timeout', `Connection to ${host}:${port} timed out.`));
          return;
        }
        reject(new PeerProtocolError('unreachable', `Cannot reach peer ${host}:${port}: ${err.message}`));
      });
    });

    const stream = tlsSocket as PeerTransportStream;
    return {
      transport: 'lan',
      normalizedAddress: normalized,
      tlsSocket,
      rawStream: stream,
    };
  }

  async listen(options: PeerTransportListenOptions): Promise<PeerTransportListenerHandle> {
    const host = options.host ?? '0.0.0.0';
    const port = options.port ?? DEFAULT_PEER_LISTEN_PORT;
    const alpn = options.alpnProtocols?.length ? [...options.alpnProtocols] : [...PEER_ALPN_PROTOCOLS];
    const rawSockets = new Set<NetSocket>();

    const { server, boundPort } = await new Promise<{ server: TlsServer; boundPort: number }>(
      (resolve, reject) => {
        const srv = tls.createServer(
          {
            cert: options.certPem,
            key: options.privateKeyPem,
            requestCert: true,
            rejectUnauthorized: false,
            ALPNProtocols: alpn,
          },
          (tlsSocket) => {
            options.onConnection({
              transport: 'lan',
              tlsSocket,
              rawStream: tlsSocket as PeerTransportStream,
            });
          },
        );

        const onStartupError = (err: Error) => {
          srv.removeListener('listening', onListening);
          try {
            srv.close();
          } catch {
            // ignore
          }
          reject(err);
        };

        const onListening = () => {
          srv.removeListener('error', onStartupError);
          const addr = srv.address();
          const actual = typeof addr === 'object' && addr ? addr.port : port;
          resolve({ server: srv, boundPort: actual });
        };

        srv.on('connection', (rawSock) => {
          rawSockets.add(rawSock);
          rawSock.once('close', () => {
            rawSockets.delete(rawSock);
          });
        });

        srv.once('error', onStartupError);
        srv.once('listening', onListening);
        srv.listen(port, host);
      },
    );

    return {
      transport: 'lan',
      boundPort,
      async close() {
        for (const rawSock of [...rawSockets]) {
          if (!rawSock.destroyed) {
            try {
              rawSock.destroy();
            } catch {
              // ignore
            }
          }
        }
        rawSockets.clear();

        await new Promise<void>((resolve) => {
          const fallback = setTimeout(resolve, 50);
          fallback.unref?.();
          try {
            server.close(() => {
              clearTimeout(fallback);
              resolve();
            });
          } catch {
            clearTimeout(fallback);
            resolve();
          }
        });
      },
    };
  }
}

// ── 2. WebSocket Duplex Stream + E2EE TLS Upgraders ──────────────────────────

/**
 * Wrap a standard binary `WebSocket` into a Node `Duplex` (`PeerTransportStream`).
 * Every `.write(chunk)` transmits a binary WebSocket frame; every incoming binary
 * WebSocket message pushes bytes into the readable side.
 */
export class WebSocketTransportStream extends Duplex implements PeerTransportStream {
  readonly transportKind: PeerTransportKind = 'relay';
  readonly remoteAddressLabel?: string;
  private readonly ws: WebSocket;
  private wsClosed = false;

  constructor(ws: WebSocket, remoteAddressLabel?: string) {
    super();
    this.ws = ws;
    this.remoteAddressLabel = remoteAddressLabel;
    ws.binaryType = 'arraybuffer';

    ws.addEventListener('message', (evt) => {
      if (this.destroyed || this.wsClosed) return;
      if (evt.data instanceof ArrayBuffer) {
        if (evt.data.byteLength > 0) {
          this.push(Buffer.from(evt.data));
        }
      } else if (ArrayBuffer.isView(evt.data)) {
        if (evt.data.byteLength > 0) {
          this.push(Buffer.from(evt.data.buffer, evt.data.byteOffset, evt.data.byteLength));
        }
      } else if (typeof evt.data === 'string') {
        // Relay binary links never send text in normal operation; ignore or reject unexpected text.
        this.destroy(
          new PeerProtocolError('handshake_failed', 'Unexpected non-binary frame on relay ciphertext link.'),
        );
      }
    });

    ws.addEventListener('close', () => {
      if (this.wsClosed) return;
      this.wsClosed = true;
      if (!this.destroyed) {
        this.push(null);
        this.destroy();
      }
    });

    ws.addEventListener('error', () => {
      if (this.wsClosed) return;
      this.wsClosed = true;
      if (!this.destroyed) {
        this.destroy(new PeerProtocolError('unreachable', 'Relay link WebSocket encountered an error.'));
      }
    });
  }

  override _read(_size: number): void {
    // Event-driven push from WebSocket 'message'.
  }

  override _write(
    chunk: Buffer | Uint8Array,
    _encoding: BufferEncoding,
    callback: (error?: Error | null) => void,
  ): void {
    if (this.wsClosed || this.ws.readyState !== WebSocket.OPEN) {
      callback(new PeerProtocolError('device_offline', 'Relay WebSocket link is not open.'));
      return;
    }
    try {
      const bytes = Buffer.isBuffer(chunk)
        ? new Uint8Array(chunk.buffer, chunk.byteOffset, chunk.byteLength)
        : chunk;
      this.ws.send(bytes);
      callback(null);
    } catch (err) {
      callback(err instanceof Error ? err : new Error(String(err)));
    }
  }

  override _final(callback: (error?: Error | null) => void): void {
    this.closeWsSafely();
    callback(null);
  }

  override _destroy(error: Error | null, callback: (error: Error | null) => void): void {
    this.closeWsSafely();
    callback(error);
  }

  private closeWsSafely(): void {
    if (this.wsClosed) return;
    this.wsClosed = true;
    try {
      if (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING) {
        this.ws.close(1000, 'Stream closed');
      }
    } catch {
      // ignore
    }
  }
}

function openRelayWebSocket(
  wsUrl: string,
  timeoutMs = 5_000,
  headers?: Record<string, string>,
): Promise<WebSocket> {
  return new Promise<WebSocket>((resolve, reject) => {
    let settled = false;
    let ws: WebSocket;
    try {
      ws = headers && Object.keys(headers).length > 0
        ? new WebSocket(wsUrl, { headers } as never)
        : new WebSocket(wsUrl);
      ws.binaryType = 'arraybuffer';
    } catch (err) {
      reject(
        new PeerProtocolError(
          'unreachable',
          `Failed to open relay WebSocket ${wsUrl}: ${err instanceof Error ? err.message : String(err)}`,
        ),
      );
      return;
    }

    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      try {
        ws.close();
      } catch {
        // ignore
      }
      reject(new PeerProtocolError('timeout', `Relay WebSocket connection timed out after ${timeoutMs}ms.`));
    }, timeoutMs);
    timer.unref?.();

    const onOpen = () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      ws.removeEventListener('error', onError);
      ws.removeEventListener('close', onEarlyClose);
      resolve(ws);
    };

    const onError = () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(new PeerProtocolError('unreachable', `Relay WebSocket connection failed (${wsUrl}).`));
    };

    const onEarlyClose = (ev: CloseEvent) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(
        new PeerProtocolError(
          'unreachable',
          `Relay WebSocket closed before opening (code=${ev.code}).`,
        ),
      );
    };

    ws.addEventListener('open', onOpen, { once: true });
    ws.addEventListener('error', onError, { once: true });
    ws.addEventListener('close', onEarlyClose, { once: true });
  });
}

/**
 * Upgrade any client-side `Duplex` byte stream (e.g. Relay WebSocket stream) into an
 * end-to-end `tls.TLSSocket` performing mutual ECDSA P-256 X.509 authentication.
 * Only encrypted TLS records traverse `rawStream`.
 */
export function upgradeStreamToClientTls(
  rawStream: Duplex,
  options: {
    certPem: string;
    privateKeyPem: string;
    alpnProtocols?: readonly string[];
    handshakeTimeoutMs?: number;
  },
): Promise<TLSSocket> {
  const timeoutMs = options.handshakeTimeoutMs ?? 8_000;
  const alpn = options.alpnProtocols?.length ? [...options.alpnProtocols] : [PEER_ALPN_HTTP];

  return new Promise<TLSSocket>((resolve, reject) => {
    let settled = false;
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      try {
        tlsSock.destroy();
      } catch {
        // ignore
      }
      try {
        rawStream.destroy();
      } catch {
        // ignore
      }
      reject(new PeerProtocolError('timeout', `Relay E2EE TLS handshake timed out after ${timeoutMs}ms.`));
    }, timeoutMs);
    timer.unref?.();

    const tlsSock = tls.connect(
      {
        socket: rawStream,
        cert: options.certPem,
        key: options.privateKeyPem,
        rejectUnauthorized: false,
        ALPNProtocols: alpn,
      },
      () => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        tlsSock.once('close', () => {
          if (!rawStream.destroyed) {
            rawStream.destroy();
          }
        });
        resolve(tlsSock);
      },
    );

    tlsSock.once('error', (err: Error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      if (!rawStream.destroyed) {
        rawStream.destroy();
      }
      reject(
        new PeerProtocolError(
          'handshake_failed',
          `Relay E2EE client TLS upgrade failed: ${err.message}`,
        ),
      );
    });

    rawStream.once('close', () => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        try {
          tlsSock.destroy();
        } catch {
          // ignore
        }
        reject(new PeerProtocolError('handshake_failed', 'Relay stream closed during E2EE TLS handshake.'));
      } else if (!tlsSock.destroyed) {
        tlsSock.destroy();
      }
    });
  });
}

function bridgeDuplexWithLoopbackTcp(duplex: Duplex, tcpSock: NetSocket): void {
  let closed = false;
  let lingerTimer: ReturnType<typeof setTimeout> | null = null;

  const hardTeardown = () => {
    if (closed) return;
    closed = true;
    if (lingerTimer) {
      clearTimeout(lingerTimer);
      lingerTimer = null;
    }
    if (!tcpSock.destroyed) {
      try {
        tcpSock.destroy();
      } catch {
        // ignore
      }
    }
    if (!duplex.destroyed) {
      try {
        duplex.destroy();
      } catch {
        // ignore
      }
    }
  };

  const gracefulTeardownDuplex = () => {
    if (closed || lingerTimer) return;
    lingerTimer = setTimeout(() => {
      lingerTimer = null;
      hardTeardown();
    }, 20);
    lingerTimer.unref?.();
  };

  duplex.on('data', (chunk: Buffer | Uint8Array) => {
    if (closed || tcpSock.destroyed) return;
    try {
      tcpSock.write(chunk);
    } catch {
      hardTeardown();
    }
  });

  tcpSock.on('data', (chunk: Buffer | Uint8Array) => {
    if (closed || duplex.destroyed) return;
    try {
      duplex.write(chunk);
    } catch {
      hardTeardown();
    }
  });

  duplex.once('end', () => {
    if (!closed && !tcpSock.destroyed) {
      try {
        tcpSock.end();
      } catch {
        hardTeardown();
      }
    }
  });

  tcpSock.once('end', () => {
    gracefulTeardownDuplex();
  });

  duplex.once('close', hardTeardown);
  tcpSock.once('close', gracefulTeardownDuplex);
  duplex.once('error', hardTeardown);
  tcpSock.once('error', gracefulTeardownDuplex);
}

// ── 3. BlindRelayTransport (Stateless Relay + E2EE mTLS Channel) ─────────────

export interface BlindRelayTransportOptions {
  /** Base URL of the blind relay (`http://host:port` or `https://host`), optional if embedded in peer addresses. */
  relayUrl?: string | (() => string | undefined);
  /** Optional explicit `relay_device_id` when registering as an inbound acceptor; defaults to `localDeviceId`. */
  relayDeviceId?: string;
  /** Optional pre-shared/persisted `relay_token` from `ISecretStore`. */
  relayToken?: string;
  fetchImpl?: typeof fetch;
  connectTimeoutMs?: number;
  handshakeTimeoutMs?: number;
  inboxReconnectDelayMs?: number;
}

interface RelayLinkRequestedMessage {
  type: 'relay.link_requested';
  ticket_id: string;
  acceptor_token?: string;
  initiator_device_id?: string | null;
  alpn?: string | null;
  expires_at_ms?: number;
}

export class BlindRelayTransport implements IPeerTransportChannel {
  readonly kind: PeerTransportKind = 'relay';
  private readonly options: BlindRelayTransportOptions;

  constructor(options: BlindRelayTransportOptions = {}) {
    this.options = options;
  }

  private resolveDefaultRelayUrl(): string | undefined {
    const raw =
      typeof this.options.relayUrl === 'function' ? this.options.relayUrl() : this.options.relayUrl;
    const envUrl = process.env.TINADEC_PEER_RELAY_URL?.trim();
    return raw?.trim() || envUrl || undefined;
  }

  canHandleAddress(address: string): boolean {
    return isRelayPeerAddress(address);
  }

  /**
   * Dial a remote peer over the stateless blind relay and upgrade the resulting binary
   * WebSocket stream into an end-to-end mTLS `TLSSocket`.
   */
  async connect(options: PeerTransportConnectOptions): Promise<PeerTransportConnectedTunnel> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    const connectTimeoutMs = options.connectTimeoutMs ?? this.options.connectTimeoutMs ?? 6_000;
    const handshakeTimeoutMs = this.options.handshakeTimeoutMs ?? 8_000;
    const alpn = options.alpnProtocols?.length ? [...options.alpnProtocols] : [PEER_ALPN_HTTP];

    const parsed = parseRelayPeerAddress(
      options.address,
      this.resolveDefaultRelayUrl(),
      options.pairingCode,
    );

    let targetDeviceId = parsed.targetDeviceId;
    if (!targetDeviceId && parsed.rendezvousCode) {
      const rvUrl = `${parsed.httpBaseUrl}/v1/rendezvous`;
      let rvRes: Response;
      try {
        rvRes = await fetchImpl(rvUrl, {
          method: 'POST',
          headers: {
            accept: 'application/json',
            'content-type': 'application/json',
          },
          body: JSON.stringify({
            action: 'resolve',
            pairing_code: parsed.rendezvousCode,
          }),
          signal: options.signal,
        });
      } catch (err) {
        throw new PeerProtocolError(
          'unreachable',
          `Cannot reach relay server ${parsed.httpBaseUrl}: ${err instanceof Error ? err.message : String(err)}`,
        );
      }

      const rvBody = (await rvRes.json().catch(() => null)) as Record<string, unknown> | null;
      if (!rvRes.ok || typeof rvBody?.relay_device_id !== 'string' || !rvBody.relay_device_id.trim()) {
        const code = rvRes.status === 404 ? 'pairing_code_expired' : 'unreachable';
        throw new PeerProtocolError(
          code,
          typeof rvBody?.detail === 'string'
            ? rvBody.detail
            : 'Relay rendezvous code could not be resolved.',
        );
      }
      targetDeviceId = rvBody.relay_device_id.trim();
    }

    if (!targetDeviceId) {
      throw new PeerProtocolError('unreachable', 'Target relay_device_id could not be determined.');
    }

    // Request a single-use bidirectional blind-link ticket from the Relay.
    const ticketEndpoint = `${parsed.httpBaseUrl}/v1/links/tickets`;
    let ticketRes: Response;
    try {
      ticketRes = await fetchImpl(ticketEndpoint, {
        method: 'POST',
        headers: {
          accept: 'application/json',
          'content-type': 'application/json',
        },
        body: JSON.stringify({
          target_device_id: targetDeviceId,
          alpn: alpn[0],
        }),
        signal: options.signal,
      });
    } catch (err) {
      throw new PeerProtocolError(
        'unreachable',
        `Cannot reach relay ticket endpoint ${ticketEndpoint}: ${err instanceof Error ? err.message : String(err)}`,
      );
    }

    const ticketBody = (await ticketRes.json().catch(() => null)) as Record<string, unknown> | null;
    if (!ticketRes.ok || typeof ticketBody?.ticket_id !== 'string') {
      throw new PeerProtocolError(
        'unreachable',
        typeof ticketBody?.detail === 'string'
          ? ticketBody.detail
          : `Relay failed to allocate link ticket for '${targetDeviceId}' (HTTP ${ticketRes.status}).`,
      );
    }

    const ticketId = ticketBody.ticket_id;
    const initiatorToken =
      typeof ticketBody.initiator_token === 'string' ? ticketBody.initiator_token : '';
    const tokenQuery = initiatorToken ? `&token=${encodeURIComponent(initiatorToken)}` : '';
    const linkWsUrl = `${parsed.wsBaseUrl}/v1/links/${encodeURIComponent(ticketId)}?role=initiator${tokenQuery}`;

    const ws = await openRelayWebSocket(linkWsUrl, connectTimeoutMs);
    const rawStream = new WebSocketTransportStream(ws, linkWsUrl);

    const tlsSocket = await upgradeStreamToClientTls(rawStream, {
      certPem: options.certPem,
      privateKeyPem: options.privateKeyPem,
      alpnProtocols: alpn,
      handshakeTimeoutMs,
    });

    return {
      transport: 'relay',
      normalizedAddress: formatCanonicalRelayAddress(parsed.httpBaseUrl, targetDeviceId),
      tlsSocket,
      rawStream,
    };
  }

  /**
   * Register this Worker on the blind relay (`POST /v1/devices/register`), subscribe to
   * `WS /v1/inbox`, and upgrade each incoming `/v1/links/:ticketId?role=acceptor` binary
   * WebSocket stream into a genuine end-to-end server `TLSSocket`.
   */
  async listen(options: PeerTransportListenOptions): Promise<PeerTransportListenerHandle> {
    const rawRelayUrl = this.resolveDefaultRelayUrl();
    if (!rawRelayUrl) {
      throw new PeerProtocolError(
        'unreachable',
        'BlindRelayTransport.listen requires a configured relayUrl.',
      );
    }

    const { httpBaseUrl, wsBaseUrl } = normalizeRelayBaseUrls(rawRelayUrl);
    const fetchImpl = this.options.fetchImpl ?? fetch;
    const connectTimeoutMs = this.options.connectTimeoutMs ?? 6_000;
    const reconnectDelayMs = this.options.inboxReconnectDelayMs ?? 500;
    const alpn = options.alpnProtocols?.length ? [...options.alpnProtocols] : [...PEER_ALPN_PROTOCOLS];

    // 1. Register device with Relay (receives relay_device_id + ephemeral relay_token).
    const requestedDeviceId = (this.options.relayDeviceId ?? options.localDeviceId).trim();
    let regRes: Response;
    try {
      regRes = await fetchImpl(`${httpBaseUrl}/v1/devices/register`, {
        method: 'POST',
        headers: {
          accept: 'application/json',
          'content-type': 'application/json',
          ...(this.options.relayToken ? { authorization: `Bearer ${this.options.relayToken}` } : {}),
        },
        body: JSON.stringify({
          relay_device_id: requestedDeviceId,
          ...(this.options.relayToken ? { relay_token: this.options.relayToken } : {}),
        }),
      });
    } catch (err) {
      throw new PeerProtocolError(
        'unreachable',
        `Failed to register with relay ${httpBaseUrl}: ${err instanceof Error ? err.message : String(err)}`,
      );
    }

    const regBody = (await regRes.json().catch(() => null)) as Record<string, unknown> | null;
    if (!regRes.ok || typeof regBody?.relay_device_id !== 'string' || typeof regBody?.relay_token !== 'string') {
      throw new PeerProtocolError(
        regRes.status === 401 || regRes.status === 403 ? 'unauthorized' : 'unreachable',
        typeof regBody?.detail === 'string'
          ? regBody.detail
          : `Relay device registration failed (HTTP ${regRes.status}).`,
      );
    }

    const relayDeviceId = regBody.relay_device_id;
    const relayToken = regBody.relay_token;

    // 2. Bind an internal strictly-loopback (`127.0.0.1:0`) TLS upgrader server so both Bun
    //    (BoringSSL/usockets) and Node.js produce a native server `TLSSocket` with full
    //    `getPeerCertificate(true).raw` & ALPN negotiation over each Relay WebSocket stream.
    const pendingStreamsByPort = new Map<number, WebSocketTransportStream>();
    const bridgeSockets = new Set<NetSocket>();

    const internalTlsServer = tls.createServer(
      {
        cert: options.certPem,
        key: options.privateKeyPem,
        requestCert: true,
        rejectUnauthorized: false,
        ALPNProtocols: alpn,
      },
      (tlsSocket) => {
        const remotePort = tlsSocket.remotePort ?? 0;
        const rawStream = pendingStreamsByPort.get(remotePort) ?? (tlsSocket as PeerTransportStream);
        pendingStreamsByPort.delete(remotePort);
        options.onConnection({
          transport: 'relay',
          tlsSocket,
          rawStream,
        });
      },
    );

    const internalTlsPort = await new Promise<number>((resolve, reject) => {
      const onErr = (err: Error) => {
        internalTlsServer.removeListener('listening', onListen);
        reject(err);
      };
      const onListen = () => {
        internalTlsServer.removeListener('error', onErr);
        const addr = internalTlsServer.address();
        resolve(typeof addr === 'object' && addr ? addr.port : 0);
      };
      internalTlsServer.once('error', onErr);
      internalTlsServer.once('listening', onListen);
      internalTlsServer.listen(0, '127.0.0.1');
    });

    let stopped = false;
    let activeInboxWs: WebSocket | null = null;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    const activeLinkStreams = new Set<WebSocketTransportStream>();

    const acceptIncomingTicket = async (msg: RelayLinkRequestedMessage): Promise<void> => {
      if (stopped || !msg.ticket_id) return;
      const tokenPart = msg.acceptor_token ? `&token=${encodeURIComponent(msg.acceptor_token)}` : '';
      const linkUrl = `${wsBaseUrl}/v1/links/${encodeURIComponent(msg.ticket_id)}?role=acceptor${tokenPart}`;

      let linkWs: WebSocket;
      try {
        linkWs = await openRelayWebSocket(linkUrl, connectTimeoutMs);
      } catch {
        return;
      }

      if (stopped) {
        try {
          linkWs.close();
        } catch {
          // ignore
        }
        return;
      }

      const rawStream = new WebSocketTransportStream(linkWs, linkUrl);
      activeLinkStreams.add(rawStream);
      rawStream.once('close', () => {
        activeLinkStreams.delete(rawStream);
      });

      const bridgeSock = net.connect({ host: '127.0.0.1', port: internalTlsPort }, () => {
        bridgeSock.setNoDelay(true);
        const localPort = bridgeSock.localPort;
        if (localPort) {
          pendingStreamsByPort.set(localPort, rawStream);
          bridgeSock.once('close', () => {
            pendingStreamsByPort.delete(localPort);
          });
        }
        bridgeDuplexWithLoopbackTcp(rawStream, bridgeSock);
      });

      bridgeSockets.add(bridgeSock);
      bridgeSock.once('close', () => {
        bridgeSockets.delete(bridgeSock);
      });
      bridgeSock.once('error', () => {
        rawStream.destroy();
      });
    };

    const connectInboxOnce = (): Promise<void> => {
      const inboxUrl = `${wsBaseUrl}/v1/inbox?device_id=${encodeURIComponent(relayDeviceId)}&token=${encodeURIComponent(relayToken)}`;
      return new Promise<void>((resolveReady, rejectReady) => {
        let readySettled = false;
        let ws: WebSocket;
        try {
          ws = new WebSocket(inboxUrl);
          ws.binaryType = 'arraybuffer';
        } catch (err) {
          rejectReady(
            new PeerProtocolError(
              'unreachable',
              `Failed to open relay inbox WebSocket: ${err instanceof Error ? err.message : String(err)}`,
            ),
          );
          return;
        }

        const connectTimer = setTimeout(() => {
          if (!readySettled) {
            readySettled = true;
            try {
              ws.close();
            } catch {
              // ignore
            }
            rejectReady(
              new PeerProtocolError(
                'timeout',
                `Relay inbox WebSocket connection timed out after ${connectTimeoutMs}ms.`,
              ),
            );
          }
        }, connectTimeoutMs);
        connectTimer.unref?.();

        ws.addEventListener('open', () => {
          if (stopped) {
            clearTimeout(connectTimer);
            try {
              ws.close();
            } catch {
              // ignore
            }
            if (!readySettled) {
              readySettled = true;
              resolveReady();
            }
            return;
          }
          activeInboxWs = ws;
        });

        ws.addEventListener('message', (evt) => {
          if (stopped) return;
          let text = '';
          if (typeof evt.data === 'string') {
            text = evt.data;
          } else if (evt.data instanceof ArrayBuffer) {
            text = Buffer.from(evt.data).toString('utf8');
          } else if (ArrayBuffer.isView(evt.data)) {
            text = Buffer.from(evt.data.buffer, evt.data.byteOffset, evt.data.byteLength).toString('utf8');
          }
          if (!text) return;
          try {
            const parsedMsg = JSON.parse(text) as {
              type?: string;
              ticket_id?: string;
              acceptor_token?: string;
              initiator_device_id?: string | null;
              alpn?: string | null;
              expires_at_ms?: number;
            };
            if (parsedMsg.type === 'relay.inbox_ready') {
              if (!readySettled) {
                readySettled = true;
                clearTimeout(connectTimer);
                resolveReady();
              }
              return;
            }
            if (parsedMsg.type === 'relay.link_requested' && typeof parsedMsg.ticket_id === 'string') {
              void acceptIncomingTicket(parsedMsg as RelayLinkRequestedMessage);
            }
          } catch {
            // Ignore malformed inbox text.
          }
        });

        ws.addEventListener('error', () => {
          if (!readySettled) {
            readySettled = true;
            clearTimeout(connectTimer);
            rejectReady(new PeerProtocolError('unreachable', `Relay inbox WebSocket failed (${inboxUrl}).`));
          }
        });

        ws.addEventListener('close', (ev) => {
          if (activeInboxWs === ws) {
            activeInboxWs = null;
          }
          if (!readySettled) {
            readySettled = true;
            clearTimeout(connectTimer);
            rejectReady(
              new PeerProtocolError(
                'unreachable',
                `Relay inbox WebSocket closed before ready (code=${ev.code}).`,
              ),
            );
            return;
          }
          if (!stopped && !reconnectTimer) {
            reconnectTimer = setTimeout(() => {
              reconnectTimer = null;
              if (!stopped) {
                void connectInboxOnce().catch(() => undefined);
              }
            }, reconnectDelayMs);
            reconnectTimer.unref?.();
          }
        });
      });
    };

    try {
      await connectInboxOnce();
    } catch (err) {
      try {
        internalTlsServer.close();
      } catch {
        // ignore
      }
      throw err;
    }

    const relayAddress = formatCanonicalRelayAddress(httpBaseUrl, relayDeviceId);

    return {
      transport: 'relay',
      boundPort: null,
      relayDeviceId,
      relayAddress,
      async publishRendezvous(pairingCode: string, ttlSeconds?: number) {
        const res = await fetchImpl(`${httpBaseUrl}/v1/rendezvous`, {
          method: 'POST',
          headers: {
            accept: 'application/json',
            'content-type': 'application/json',
            authorization: `Bearer ${relayToken}`,
          },
          body: JSON.stringify({
            action: 'publish',
            pairing_code: pairingCode,
            relay_device_id: relayDeviceId,
            relay_token: relayToken,
            ...(ttlSeconds !== undefined ? { ttl_seconds: ttlSeconds } : {}),
          }),
        });
        const data = (await res.json().catch(() => null)) as Record<string, unknown> | null;
        if (!res.ok || !data) {
          throw new PeerProtocolError(
            'unreachable',
            typeof data?.detail === 'string' ? data.detail : 'Failed to publish relay rendezvous code.',
          );
        }
        return {
          relay_device_id: String(data.relay_device_id ?? relayDeviceId),
          expires_at_ms: Number(data.expires_at_ms ?? Date.now() + 300_000),
        };
      },
      async close() {
        stopped = true;
        if (reconnectTimer) {
          clearTimeout(reconnectTimer);
          reconnectTimer = null;
        }
        if (activeInboxWs) {
          try {
            activeInboxWs.close(1000, 'Relay listener closing');
          } catch {
            // ignore
          }
          activeInboxWs = null;
        }
        for (const stream of [...activeLinkStreams]) {
          stream.destroy();
        }
        activeLinkStreams.clear();

        for (const sock of [...bridgeSockets]) {
          try {
            sock.destroy();
          } catch {
            // ignore
          }
        }
        bridgeSockets.clear();
        pendingStreamsByPort.clear();

        await new Promise<void>((resolve) => {
          const fallback = setTimeout(resolve, 50);
          fallback.unref?.();
          try {
            internalTlsServer.close(() => {
              clearTimeout(fallback);
              resolve();
            });
          } catch {
            clearTimeout(fallback);
            resolve();
          }
        });
      },
    };
  }
}
