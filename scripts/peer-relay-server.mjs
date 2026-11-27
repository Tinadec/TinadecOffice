#!/usr/bin/env node
/**
 * Stateless Blind-Forwarding Peer Relay Reference Server (Spec §6.2.3)
 *
 * Pure standard ESM (`node:http`, `node:crypto`, `node:url`) compatible with both
 * Bun (native `Bun.serve` WebSocket engine) and Node.js (`node:http` RFC 6455 engine)
 * with zero external npm dependencies.
 *
 * Security invariants:
 * - Never touches certificate private keys, pairing secrets, or decrypted application payloads.
 * - `/v1/links/:ticketId` blindly forwards binary TLS record frames (`0x16` Handshake,
 *   `0x17` Application Data, etc.) between `initiator` and `acceptor`, tearing down both
 *   sides and destroying the single-use ticket immediately when either endpoint closes.
 */

import { createHash, randomBytes, timingSafeEqual } from 'node:crypto';
import http from 'node:http';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const DEFAULT_PEER_RELAY_PORT = 48740;
const WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

const DEFAULT_DEVICE_TTL_MS = 24 * 60 * 60 * 1000;
const DEFAULT_RENDEZVOUS_TTL_MS = 5 * 60 * 1000;
const MAX_RENDEZVOUS_TTL_MS = 10 * 60 * 1000;
const DEFAULT_TICKET_TTL_MS = 30 * 1000;
const MAX_HTTP_BODY_BYTES = 64 * 1024;
const MAX_WS_FRAME_BYTES = 36 * 1024 * 1024;
const MAX_EARLY_QUEUE_BYTES = 256 * 1024;
const DEFAULT_WIRE_TAP_LIMIT = 2048;

const SAFE_ID_RE = /^[A-Za-z0-9._:-]{2,128}$/;

function sha256Hex(value) {
  return createHash('sha256').update(String(value), 'utf8').digest('hex');
}

function constantTimeEqualHex(leftHex, rightHex) {
  if (typeof leftHex !== 'string' || typeof rightHex !== 'string') return false;
  if (leftHex.length !== rightHex.length || leftHex.length === 0 || leftHex.length % 2 !== 0) {
    return false;
  }
  try {
    const a = Buffer.from(leftHex, 'hex');
    const b = Buffer.from(rightHex, 'hex');
    return a.length === b.length && timingSafeEqual(a, b);
  } catch {
    return false;
  }
}

function extractBearerTokenFromParts(authHeader, urlObj, bodyObj) {
  if (typeof authHeader === 'string' && authHeader.toLowerCase().startsWith('bearer ')) {
    const trimmed = authHeader.slice(7).trim();
    if (trimmed) return trimmed;
  }
  const queryToken = urlObj?.searchParams?.get('token')?.trim();
  if (queryToken) return queryToken;
  if (bodyObj && typeof bodyObj.relay_token === 'string' && bodyObj.relay_token.trim()) {
    return bodyObj.relay_token.trim();
  }
  if (bodyObj && typeof bodyObj.token === 'string' && bodyObj.token.trim()) {
    return bodyObj.token.trim();
  }
  return '';
}

// ── Node.js RFC 6455 WebSocket Frame Encoder / Connection ────────────────────

export function encodeWsFrame(opcode, payloadBuf = Buffer.alloc(0)) {
  const payload = Buffer.isBuffer(payloadBuf) ? payloadBuf : Buffer.from(payloadBuf);
  const len = payload.byteLength;
  let header;
  if (len < 126) {
    header = Buffer.allocUnsafe(2);
    header[0] = 0x80 | (opcode & 0x0f);
    header[1] = len;
  } else if (len < 65536) {
    header = Buffer.allocUnsafe(4);
    header[0] = 0x80 | (opcode & 0x0f);
    header[1] = 126;
    header.writeUInt16BE(len, 2);
  } else {
    header = Buffer.allocUnsafe(10);
    header[0] = 0x80 | (opcode & 0x0f);
    header[1] = 127;
    header.writeBigUInt64BE(BigInt(len), 2);
  }
  return len > 0 ? Buffer.concat([header, payload]) : header;
}

class NodeRelayWsConnection {
  constructor(socket, maxFrameBytes = MAX_WS_FRAME_BYTES) {
    this.socket = socket;
    this.maxFrameBytes = maxFrameBytes;
    this.buffer = Buffer.alloc(0);
    this.fragOpcode = null;
    this.fragChunks = [];
    this.fragBytes = 0;
    this.closed = false;
    this.onMessage = null;
    this.closeListeners = new Set();

    socket.setNoDelay?.(true);

    socket.on('data', (chunk) => {
      if (this.closed || !chunk || chunk.length === 0) return;
      const incoming = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      this.pushBytes(incoming);
    });
    socket.on('end', () => this.closeInternal(1000, ''));
    socket.on('close', () => this.closeInternal(1006, ''));
    socket.on('error', () => this.closeInternal(1006, ''));
  }

  addCloseListener(fn) {
    if (this.closed) {
      queueMicrotask(() => fn(1000, ''));
      return;
    }
    this.closeListeners.add(fn);
  }

  pushBytes(chunk) {
    this.buffer = this.buffer.length === 0 ? chunk : Buffer.concat([this.buffer, chunk]);

    while (!this.closed && this.buffer.length >= 2) {
      const b0 = this.buffer[0];
      const b1 = this.buffer[1];
      const fin = (b0 & 0x80) !== 0;
      const opcode = b0 & 0x0f;
      const masked = (b1 & 0x80) !== 0;
      let payloadLen = b1 & 0x7f;
      let offset = 2;

      if (payloadLen === 126) {
        if (this.buffer.length < 4) return;
        payloadLen = this.buffer.readUInt16BE(2);
        offset = 4;
      } else if (payloadLen === 127) {
        if (this.buffer.length < 10) return;
        const bigLen = this.buffer.readBigUInt64BE(2);
        if (bigLen > BigInt(this.maxFrameBytes)) {
          this.close(1009, 'Frame exceeds maximum size');
          return;
        }
        payloadLen = Number(bigLen);
        offset = 10;
      }

      if (payloadLen > this.maxFrameBytes) {
        this.close(1009, 'Frame exceeds maximum size');
        return;
      }

      const maskBytes = masked ? 4 : 0;
      if (this.buffer.length < offset + maskBytes + payloadLen) {
        return;
      }

      const payload = Buffer.from(
        this.buffer.subarray(offset + maskBytes, offset + maskBytes + payloadLen),
      );
      if (masked) {
        const mask = this.buffer.subarray(offset, offset + 4);
        for (let i = 0; i < payload.length; i++) {
          payload[i] ^= mask[i & 3];
        }
      }

      this.buffer =
        this.buffer.length === offset + maskBytes + payloadLen
          ? Buffer.alloc(0)
          : this.buffer.subarray(offset + maskBytes + payloadLen);

      if (opcode === 0x08) {
        let closeCode = 1000;
        let closeReason = '';
        if (payload.length >= 2) {
          closeCode = payload.readUInt16BE(0);
          closeReason = payload.subarray(2).toString('utf8');
        }
        try {
          if (!this.socket.destroyed) {
            const replyPayload = Buffer.allocUnsafe(2);
            replyPayload.writeUInt16BE(closeCode, 0);
            this.socket.write(encodeWsFrame(0x08, replyPayload));
            this.socket.end();
          }
        } catch {
          // ignore
        }
        this.closeInternal(closeCode, closeReason);
        return;
      }

      if (opcode === 0x09) {
        if (!this.closed && !this.socket.destroyed) {
          try {
            this.socket.write(encodeWsFrame(0x0a, payload));
          } catch {
            // ignore
          }
        }
        continue;
      }

      if (opcode === 0x0a) continue;

      if (opcode === 0x00) {
        if (this.fragOpcode === null) {
          this.close(1002, 'Unexpected continuation frame');
          return;
        }
        if (this.fragBytes + payload.length > this.maxFrameBytes) {
          this.close(1009, 'Fragmented message exceeds maximum size');
          return;
        }
        this.fragChunks.push(payload);
        this.fragBytes += payload.length;
        if (fin) {
          const full = Buffer.concat(this.fragChunks, this.fragBytes);
          const doneOpcode = this.fragOpcode;
          this.fragOpcode = null;
          this.fragChunks = [];
          this.fragBytes = 0;
          this.onMessage?.(doneOpcode, full);
        }
        continue;
      }

      if (opcode === 0x01 || opcode === 0x02) {
        if (!fin) {
          this.fragOpcode = opcode;
          this.fragChunks = [payload];
          this.fragBytes = payload.length;
          continue;
        }
        this.onMessage?.(opcode, payload);
        continue;
      }

      this.close(1002, 'Unsupported WebSocket opcode');
      return;
    }
  }

  sendText(text) {
    if (this.closed || this.socket.destroyed) return false;
    try {
      return this.socket.write(encodeWsFrame(0x01, Buffer.from(String(text), 'utf8')));
    } catch {
      this.closeInternal(1006, '');
      return false;
    }
  }

  sendJson(obj) {
    return this.sendText(JSON.stringify(obj));
  }

  sendBinary(buf) {
    if (this.closed || this.socket.destroyed) return false;
    try {
      return this.socket.write(encodeWsFrame(0x02, buf));
    } catch {
      this.closeInternal(1006, '');
      return false;
    }
  }

  close(code = 1000, reason = '') {
    if (this.closed) return;
    const reasonBuf = Buffer.from(String(reason).slice(0, 120), 'utf8');
    const closePayload = Buffer.allocUnsafe(2 + reasonBuf.length);
    closePayload.writeUInt16BE(code, 0);
    reasonBuf.copy(closePayload, 2);
    try {
      if (!this.socket.destroyed) {
        this.socket.write(encodeWsFrame(0x08, closePayload));
        this.socket.end();
      }
    } catch {
      // ignore
    }
    this.closeInternal(code, reason);
    const t = setTimeout(() => {
      try {
        this.socket.destroy();
      } catch {
        // ignore
      }
    }, 25);
    t.unref?.();
  }

  destroy() {
    this.closeInternal(1006, '');
    try {
      this.socket.destroy();
    } catch {
      // ignore
    }
  }

  closeInternal(code, reason) {
    if (this.closed) return;
    this.closed = true;
    this.buffer = Buffer.alloc(0);
    this.fragChunks = [];
    this.fragBytes = 0;
    this.onMessage = null;
    const listeners = [...this.closeListeners];
    this.closeListeners.clear();
    for (const fn of listeners) {
      try {
        fn(code, reason);
      } catch {
        // ignore
      }
    }
  }
}

class BunRelayWsConnection {
  constructor(rawBunWs) {
    this.rawWs = rawBunWs;
    this.closed = false;
    this.onMessage = null;
    this.closeListeners = new Set();
  }

  addCloseListener(fn) {
    if (this.closed) {
      queueMicrotask(() => fn(1000, ''));
      return;
    }
    this.closeListeners.add(fn);
  }

  sendText(text) {
    if (this.closed) return false;
    try {
      this.rawWs.sendText ? this.rawWs.sendText(String(text)) : this.rawWs.send(String(text));
      return true;
    } catch {
      this.closeInternal(1006, '');
      return false;
    }
  }

  sendJson(obj) {
    return this.sendText(JSON.stringify(obj));
  }

  sendBinary(buf) {
    if (this.closed) return false;
    try {
      const bytes = Buffer.isBuffer(buf)
        ? new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength)
        : buf;
      this.rawWs.sendBinary ? this.rawWs.sendBinary(bytes) : this.rawWs.send(bytes);
      return true;
    } catch {
      this.closeInternal(1006, '');
      return false;
    }
  }

  close(code = 1000, reason = '') {
    if (this.closed) return;
    this.closeInternal(code, reason);
    try {
      this.rawWs.close(code, String(reason).slice(0, 120));
    } catch {
      // ignore
    }
  }

  destroy() {
    this.close(1001, 'Relay closing');
  }

  closeInternal(code, reason) {
    if (this.closed) return;
    this.closed = true;
    this.onMessage = null;
    const listeners = [...this.closeListeners];
    this.closeListeners.clear();
    for (const fn of listeners) {
      try {
        fn(code, reason);
      } catch {
        // ignore
      }
    }
  }
}

function sendNodeJson(res, status, payload) {
  const body = Buffer.from(JSON.stringify(payload), 'utf8');
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': String(body.byteLength),
    'cache-control': 'no-store',
  });
  res.end(body);
}

function readNodeJsonBody(req, maxBytes = MAX_HTTP_BODY_BYTES) {
  return new Promise((resolveBody, rejectBody) => {
    const chunks = [];
    let total = 0;
    let settled = false;

    req.on('data', (chunk) => {
      if (settled) return;
      const buf = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      total += buf.byteLength;
      if (total > maxBytes) {
        settled = true;
        rejectBody(Object.assign(new Error('Request JSON body exceeds limit.'), { status: 413 }));
        req.destroy();
        return;
      }
      chunks.push(buf);
    });

    req.on('end', () => {
      if (settled) return;
      settled = true;
      if (total === 0) {
        resolveBody({});
        return;
      }
      try {
        const parsed = JSON.parse(Buffer.concat(chunks, total).toString('utf8'));
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
          rejectBody(Object.assign(new Error('Request body must be a JSON object.'), { status: 400 }));
          return;
        }
        resolveBody(parsed);
      } catch {
        rejectBody(Object.assign(new Error('Malformed JSON body.'), { status: 400 }));
      }
    });

    req.on('error', (err) => {
      if (settled) return;
      settled = true;
      rejectBody(err);
    });
  });
}

function rejectNodeUpgrade(socket, status, code, detail) {
  try {
    const body = JSON.stringify({ code, detail });
    socket.write(
      `HTTP/1.1 ${status} Rejected\r\n` +
        'Connection: close\r\n' +
        'Content-Type: application/json; charset=utf-8\r\n' +
        `Content-Length: ${Buffer.byteLength(body)}\r\n\r\n` +
        body,
    );
    socket.destroy();
  } catch {
    try {
      socket.destroy();
    } catch {
      // ignore
    }
  }
}

function acceptNodeUpgrade(req, socket, head, maxFrameBytes) {
  const key = req.headers['sec-websocket-key'];
  if (typeof key !== 'string' || key.trim().length === 0) {
    rejectNodeUpgrade(socket, 400, 'invalid_websocket_upgrade', 'Missing Sec-WebSocket-Key header.');
    return null;
  }
  const acceptHash = createHash('sha1')
    .update(key.trim() + WS_GUID, 'ascii')
    .digest('base64');

  socket.write(
    'HTTP/1.1 101 Switching Protocols\r\n' +
      'Upgrade: websocket\r\n' +
      'Connection: Upgrade\r\n' +
      `Sec-WebSocket-Accept: ${acceptHash}\r\n\r\n`,
  );

  const wsConn = new NodeRelayWsConnection(socket, maxFrameBytes);
  if (head && head.length > 0) {
    queueMicrotask(() => {
      if (!wsConn.closed) wsConn.pushBytes(Buffer.from(head));
    });
  }
  return wsConn;
}

// ── Stateless Blind Relay Server Factory ─────────────────────────────────────

/**
 * Start a lightweight in-memory blind-forwarding relay server.
 */
export async function startPeerRelayServer(options = {}) {
  const bindHost = options.host ?? '127.0.0.1';
  const requestedPort = options.port !== undefined ? Number(options.port) : 0;
  const recordWireTap = options.recordWireTap ?? true;
  const wireTapLimit = options.wireTapLimit ?? DEFAULT_WIRE_TAP_LIMIT;
  const ticketTtlMs = options.ticketTtlMs ?? DEFAULT_TICKET_TTL_MS;
  const defaultRendezvousTtlMs = options.rendezvousTtlMs ?? DEFAULT_RENDEZVOUS_TTL_MS;
  const maxFrameBytes = options.maxFrameBytes ?? MAX_WS_FRAME_BYTES;

  const devices = new Map();
  const inboxes = new Map();
  const rendezvousMap = new Map();
  const tickets = new Map();
  const allWsConnections = new Set();

  let boundPort = requestedPort;
  let activeBridges = 0;
  let totalRelayedBytes = 0;
  let totalRelayedFrames = 0;
  const capturedFrames = [];

  const effectiveHostForUrl = () =>
    bindHost === '0.0.0.0' || bindHost === '::'
      ? '127.0.0.1'
      : bindHost.includes(':')
        ? `[${bindHost}]`
        : bindHost;

  function pruneExpired(now = Date.now()) {
    for (const [id, dev] of devices) {
      if (dev.expiresAtMs <= now && !inboxes.has(id)) {
        devices.delete(id);
      }
    }
    for (const [key, rv] of rendezvousMap) {
      if (rv.expiresAtMs <= now) {
        rendezvousMap.delete(key);
      }
    }
    for (const [tid, tkt] of tickets) {
      if (!tkt.bridged && tkt.expiresAtMs <= now) {
        destroyTicket(tid, 1001, 'Link ticket expired');
      }
    }
  }

  function destroyTicket(ticketId, closeCode = 1000, reason = 'Link closed') {
    const tkt = tickets.get(ticketId);
    if (!tkt) return;
    tickets.delete(ticketId);
    if (tkt.expiryTimer) {
      clearTimeout(tkt.expiryTimer);
      tkt.expiryTimer = null;
    }
    if (tkt.bridged && !tkt.bridgeCountDecremented) {
      tkt.bridgeCountDecremented = true;
      activeBridges = Math.max(0, activeBridges - 1);
    }
    tkt.earlyInitiatorFrames = [];
    tkt.earlyAcceptorFrames = [];
    if (tkt.initiatorWs && !tkt.initiatorWs.closed) {
      tkt.initiatorWs.close(closeCode, reason);
    }
    if (tkt.acceptorWs && !tkt.acceptorWs.closed) {
      tkt.acceptorWs.close(closeCode, reason);
    }
  }

  function recordFrame(ticketId, direction, opcode, payload) {
    totalRelayedFrames += 1;
    totalRelayedBytes += payload.byteLength;
    if (!recordWireTap) return;
    if (capturedFrames.length >= wireTapLimit) {
      capturedFrames.shift();
    }
    capturedFrames.push({
      ticketId,
      direction,
      opcode,
      payload: Buffer.from(payload),
      timestampMs: Date.now(),
    });
  }

  function getStats() {
    pruneExpired();
    return {
      registeredDevices: devices.size,
      activeInboxes: inboxes.size,
      activeRendezvous: rendezvousMap.size,
      activeTickets: tickets.size,
      activeBridges,
      totalRelayedBytes,
      totalRelayedFrames,
    };
  }

  async function handleHttpEndpoint(method, urlObj, authHeader, readBody) {
    const now = Date.now();
    pruneExpired(now);
    const pathname = urlObj.pathname.replace(/\/+$/, '') || '/';

    if (method === 'GET' && (pathname === '/health' || pathname === '/v1/health')) {
      return {
        status: 200,
        body: {
          status: 'ok',
          service: 'tinadec-peer-blind-relay',
          mode: 'blind_ciphertext_only',
          port: boundPort,
          stats: getStats(),
        },
      };
    }

    if (method === 'POST' && pathname === '/v1/devices/register') {
      const body = await readBody();
      const rawId = String(body.relay_device_id ?? body.device_id ?? '').trim();
      const relayDeviceId = rawId || `rdev_${randomBytes(12).toString('hex')}`;
      if (!SAFE_ID_RE.test(relayDeviceId)) {
        return {
          status: 400,
          body: { code: 'invalid_request', detail: 'relay_device_id must be 2..128 safe ASCII characters.' },
        };
      }

      const suppliedToken = extractBearerTokenFromParts(authHeader, urlObj, body);
      const existing = devices.get(relayDeviceId);
      let plainToken = '';

      if (existing && existing.expiresAtMs > now) {
        if (!suppliedToken || !constantTimeEqualHex(existing.tokenHash, sha256Hex(suppliedToken))) {
          return {
            status: 403,
            body: {
              code: 'unauthorized',
              detail: 'relay_device_id is already registered with a different active token.',
            },
          };
        }
        plainToken = suppliedToken;
      } else {
        plainToken =
          suppliedToken && suppliedToken.length >= 16
            ? suppliedToken
            : randomBytes(24).toString('hex');
      }

      const ttlSeconds = Number(body.ttl_seconds);
      const ttlMs =
        Number.isFinite(ttlSeconds) && ttlSeconds > 0
          ? Math.min(DEFAULT_DEVICE_TTL_MS, Math.max(5_000, Math.round(ttlSeconds * 1000)))
          : DEFAULT_DEVICE_TTL_MS;
      const expiresAtMs = now + ttlMs;

      devices.set(relayDeviceId, {
        relayDeviceId,
        tokenHash: sha256Hex(plainToken),
        expiresAtMs,
      });

      const wsBase = `ws://${effectiveHostForUrl()}:${boundPort}`;
      return {
        status: 200,
        body: {
          relay_device_id: relayDeviceId,
          relay_token: plainToken,
          expires_at_ms: expiresAtMs,
          inbox_ws_url: `${wsBase}/v1/inbox?device_id=${encodeURIComponent(relayDeviceId)}`,
        },
      };
    }

    if ((method === 'POST' || method === 'GET') && pathname === '/v1/rendezvous') {
      const body = method === 'POST' ? await readBody() : {};
      const rawAction = String(
        body.action ??
          urlObj.searchParams.get('action') ??
          (method === 'GET' || (!body.relay_device_id && !body.device_id) ? 'resolve' : 'publish'),
      )
        .trim()
        .toLowerCase();

      const rawCode = String(
        body.pairing_code ?? body.code ?? urlObj.searchParams.get('code') ?? '',
      ).trim();
      if (!rawCode || rawCode.length > 128) {
        return {
          status: 400,
          body: { code: 'invalid_request', detail: 'pairing_code is required for rendezvous.' },
        };
      }

      const codeKey = sha256Hex(`tinadec-rv-v1:${rawCode}`);

      if (rawAction === 'publish') {
        const relayDeviceId = String(body.relay_device_id ?? body.device_id ?? '').trim();
        const dev = devices.get(relayDeviceId);
        const token = extractBearerTokenFromParts(authHeader, urlObj, body);
        if (!dev || dev.expiresAtMs <= now || !token || !constantTimeEqualHex(dev.tokenHash, sha256Hex(token))) {
          return {
            status: 401,
            body: {
              code: 'unauthorized',
              detail: 'Valid registered relay_device_id and relay_token are required to publish rendezvous.',
            },
          };
        }

        const ttlSec = Number(body.ttl_seconds);
        const ttlMs =
          Number.isFinite(ttlSec) && ttlSec > 0
            ? Math.min(MAX_RENDEZVOUS_TTL_MS, Math.max(5_000, Math.round(ttlSec * 1000)))
            : defaultRendezvousTtlMs;
        const expiresAtMs = now + ttlMs;
        rendezvousMap.set(codeKey, {
          codeKey,
          relayDeviceId,
          expiresAtMs,
        });
        return {
          status: 200,
          body: {
            published: true,
            relay_device_id: relayDeviceId,
            expires_at_ms: expiresAtMs,
          },
        };
      }

      if (rawAction === 'revoke') {
        const existing = rendezvousMap.get(codeKey);
        if (existing) {
          const dev = devices.get(existing.relayDeviceId);
          const token = extractBearerTokenFromParts(authHeader, urlObj, body);
          if (!dev || !token || !constantTimeEqualHex(dev.tokenHash, sha256Hex(token))) {
            return {
              status: 401,
              body: { code: 'unauthorized', detail: 'Invalid relay_token for rendezvous revoke.' },
            };
          }
          rendezvousMap.delete(codeKey);
        }
        return { status: 200, body: { revoked: true } };
      }

      if (rawAction === 'resolve') {
        const entry = rendezvousMap.get(codeKey);
        if (!entry || entry.expiresAtMs <= now) {
          if (entry) rendezvousMap.delete(codeKey);
          return {
            status: 404,
            body: {
              found: false,
              code: 'pairing_code_expired',
              detail: 'No active relay rendezvous entry found for this code.',
            },
          };
        }
        return {
          status: 200,
          body: {
            found: true,
            relay_device_id: entry.relayDeviceId,
            expires_at_ms: entry.expiresAtMs,
          },
        };
      }

      return {
        status: 400,
        body: { code: 'invalid_request', detail: `Unsupported rendezvous action '${rawAction}'.` },
      };
    }

    if (method === 'POST' && pathname === '/v1/links/tickets') {
      const body = await readBody();
      const targetDeviceId = String(body.target_device_id ?? body.relay_device_id ?? '').trim();
      if (!targetDeviceId || !SAFE_ID_RE.test(targetDeviceId)) {
        return {
          status: 400,
          body: { code: 'invalid_request', detail: 'target_device_id is required.' },
        };
      }

      const inboxWs = inboxes.get(targetDeviceId);
      if (!inboxWs || inboxWs.closed) {
        inboxes.delete(targetDeviceId);
        return {
          status: 503,
          body: {
            code: 'unreachable',
            detail: `Target relay device '${targetDeviceId}' has no active inbox connection.`,
          },
        };
      }

      const ticketId = `tkt_${randomBytes(16).toString('hex')}`;
      const initiatorToken = randomBytes(16).toString('hex');
      const acceptorToken = randomBytes(16).toString('hex');
      const expiresAtMs = now + ticketTtlMs;
      const alpn = typeof body.alpn === 'string' ? body.alpn.slice(0, 64) : undefined;
      const initiatorDeviceId =
        typeof body.initiator_device_id === 'string' ? body.initiator_device_id.slice(0, 128) : undefined;

      const ticket = {
        ticketId,
        targetDeviceId,
        initiatorTokenHash: sha256Hex(initiatorToken),
        acceptorTokenHash: sha256Hex(acceptorToken),
        expiresAtMs,
        initiatorClaimed: false,
        acceptorClaimed: false,
        initiatorWs: null,
        acceptorWs: null,
        bridged: false,
        bridgeCountDecremented: false,
        earlyInitiatorFrames: [],
        earlyInitiatorBytes: 0,
        earlyAcceptorFrames: [],
        earlyAcceptorBytes: 0,
        expiryTimer: null,
      };

      ticket.expiryTimer = setTimeout(() => {
        if (!ticket.bridged) {
          destroyTicket(ticketId, 1001, 'Link ticket timed out waiting for both endpoints.');
        }
      }, ticketTtlMs);
      ticket.expiryTimer.unref?.();

      tickets.set(ticketId, ticket);

      const notified = inboxWs.sendJson({
        type: 'relay.link_requested',
        ticket_id: ticketId,
        acceptor_token: acceptorToken,
        initiator_device_id: initiatorDeviceId ?? null,
        alpn: alpn ?? null,
        expires_at_ms: expiresAtMs,
        link_ws_path: `/v1/links/${encodeURIComponent(ticketId)}?role=acceptor`,
      });

      if (!notified) {
        destroyTicket(ticketId, 1011, 'Failed to notify target inbox.');
        return {
          status: 503,
          body: {
            code: 'unreachable',
            detail: `Target relay device '${targetDeviceId}' inbox disconnected.`,
          },
        };
      }

      const wsBase = `ws://${effectiveHostForUrl()}:${boundPort}`;
      return {
        status: 201,
        body: {
          ticket_id: ticketId,
          initiator_token: initiatorToken,
          target_device_id: targetDeviceId,
          expires_at_ms: expiresAtMs,
          initiator_ws_url: `${wsBase}/v1/links/${encodeURIComponent(ticketId)}?role=initiator`,
        },
      };
    }

    return {
      status: 404,
      body: { code: 'not_found', detail: `Relay route ${method} ${pathname} not found.` },
    };
  }

  function authorizeWsUpgrade(urlObj, authHeader) {
    const now = Date.now();
    pruneExpired(now);
    const pathname = urlObj.pathname.replace(/\/+$/, '') || '/';

    if (pathname === '/v1/inbox') {
      const deviceId = (urlObj.searchParams.get('device_id') ?? '').trim();
      const token = extractBearerTokenFromParts(authHeader, urlObj, null);
      const dev = devices.get(deviceId);
      if (!deviceId || !dev || dev.expiresAtMs <= now || !token || !constantTimeEqualHex(dev.tokenHash, sha256Hex(token))) {
        return {
          ok: false,
          status: 401,
          code: 'unauthorized',
          detail: 'Valid registered device_id and token are required for /v1/inbox.',
        };
      }
      return {
        ok: true,
        sessionData: { kind: 'inbox', deviceId, connectedAtMs: now },
      };
    }

    const linkMatch = /^\/v1\/links\/([^/]+)$/.exec(pathname);
    if (linkMatch) {
      const ticketId = decodeURIComponent(linkMatch[1]);
      const role = (urlObj.searchParams.get('role') ?? '').trim().toLowerCase();
      if (role !== 'initiator' && role !== 'acceptor') {
        return {
          ok: false,
          status: 400,
          code: 'invalid_request',
          detail: 'role query parameter must be initiator or acceptor.',
        };
      }

      const ticket = tickets.get(ticketId);
      if (!ticket || ticket.expiresAtMs <= now) {
        if (ticket) destroyTicket(ticketId, 1001, 'Ticket expired');
        return {
          ok: false,
          status: 404,
          code: 'ticket_not_found',
          detail: 'Link ticket does not exist or has expired.',
        };
      }

      const suppliedToken = extractBearerTokenFromParts(authHeader, urlObj, null);
      const expectedHash = role === 'initiator' ? ticket.initiatorTokenHash : ticket.acceptorTokenHash;
      if (suppliedToken && !constantTimeEqualHex(expectedHash, sha256Hex(suppliedToken))) {
        return {
          ok: false,
          status: 403,
          code: 'unauthorized',
          detail: 'Invalid link role token.',
        };
      }

      if (role === 'initiator') {
        if (ticket.initiatorClaimed) {
          return {
            ok: false,
            status: 409,
            code: 'ticket_already_claimed',
            detail: 'Initiator slot already claimed for this single-use ticket.',
          };
        }
        ticket.initiatorClaimed = true;
      } else {
        if (ticket.acceptorClaimed) {
          return {
            ok: false,
            status: 409,
            code: 'ticket_already_claimed',
            detail: 'Acceptor slot already claimed for this single-use ticket.',
          };
        }
        ticket.acceptorClaimed = true;
      }

      return {
        ok: true,
        sessionData: { kind: 'link', ticketId, role },
      };
    }

    return {
      ok: false,
      status: 404,
      code: 'not_found',
      detail: `Unknown WebSocket endpoint '${pathname}'.`,
    };
  }

  function attachWsConnection(wsConn, sessionData) {
    allWsConnections.add(wsConn);

    if (sessionData.kind === 'inbox') {
      const { deviceId, connectedAtMs } = sessionData;
      const prev = inboxes.get(deviceId);
      if (prev && prev !== wsConn) {
        prev.close(1000, 'Superseded by newer inbox connection');
      }
      inboxes.set(deviceId, wsConn);

      wsConn.addCloseListener(() => {
        allWsConnections.delete(wsConn);
        if (inboxes.get(deviceId) === wsConn) {
          inboxes.delete(deviceId);
        }
      });

      wsConn.sendJson({
        type: 'relay.inbox_ready',
        device_id: deviceId,
        timestamp_ms: connectedAtMs,
      });
      return;
    }

    const { ticketId, role } = sessionData;
    const ticket = tickets.get(ticketId);
    if (!ticket) {
      wsConn.close(1001, 'Ticket no longer active');
      allWsConnections.delete(wsConn);
      return;
    }

    if (role === 'initiator') {
      ticket.initiatorWs = wsConn;
    } else {
      ticket.acceptorWs = wsConn;
    }

    wsConn.addCloseListener(() => {
      allWsConnections.delete(wsConn);
      destroyTicket(ticketId, 1000, 'Peer endpoint closed relay link');
    });

    wsConn.onMessage = (opcode, payload) => {
      if (opcode !== 0x02) {
        destroyTicket(ticketId, 1003, 'Relay link channel only accepts binary ciphertext frames.');
        return;
      }
      if (role === 'initiator') {
        if (ticket.earlyInitiatorBytes + payload.length > MAX_EARLY_QUEUE_BYTES) {
          destroyTicket(ticketId, 1009, 'Pre-bridge buffer exceeded.');
          return;
        }
        ticket.earlyInitiatorFrames.push(payload);
        ticket.earlyInitiatorBytes += payload.length;
      } else {
        if (ticket.earlyAcceptorBytes + payload.length > MAX_EARLY_QUEUE_BYTES) {
          destroyTicket(ticketId, 1009, 'Pre-bridge buffer exceeded.');
          return;
        }
        ticket.earlyAcceptorFrames.push(payload);
        ticket.earlyAcceptorBytes += payload.length;
      }
    };

    if (ticket.initiatorWs && ticket.acceptorWs && !ticket.bridged) {
      ticket.bridged = true;
      activeBridges += 1;
      if (ticket.expiryTimer) {
        clearTimeout(ticket.expiryTimer);
        ticket.expiryTimer = null;
      }

      const initWs = ticket.initiatorWs;
      const accWs = ticket.acceptorWs;

      if (typeof options.customLinkInterceptor === 'function') {
        options.customLinkInterceptor(ticketId, initWs, accWs, {
          earlyInitiatorFrames: ticket.earlyInitiatorFrames.splice(0),
          earlyAcceptorFrames: ticket.earlyAcceptorFrames.splice(0),
          recordFrame: (dir, op, buf) => recordFrame(ticketId, dir, op, buf),
          destroy: () => destroyTicket(ticketId, 1000, 'Interceptor closed'),
        });
        return;
      }

      initWs.onMessage = (opcode, payload) => {
        if (opcode !== 0x02) {
          destroyTicket(ticketId, 1003, 'Relay link channel only accepts binary ciphertext frames.');
          return;
        }
        recordFrame(ticketId, 'initiator->acceptor', opcode, payload);
        accWs.sendBinary(payload);
      };

      accWs.onMessage = (opcode, payload) => {
        if (opcode !== 0x02) {
          destroyTicket(ticketId, 1003, 'Relay link channel only accepts binary ciphertext frames.');
          return;
        }
        recordFrame(ticketId, 'acceptor->initiator', opcode, payload);
        initWs.sendBinary(payload);
      };

      for (const frame of ticket.earlyInitiatorFrames.splice(0)) {
        recordFrame(ticketId, 'initiator->acceptor', 0x02, frame);
        accWs.sendBinary(frame);
      }
      ticket.earlyInitiatorBytes = 0;

      for (const frame of ticket.earlyAcceptorFrames.splice(0)) {
        recordFrame(ticketId, 'acceptor->initiator', 0x02, frame);
        initWs.sendBinary(frame);
      }
      ticket.earlyAcceptorBytes = 0;
    }
  }

  // ── Runtime Engine Selection: Native Bun.serve vs Node.js node:http ──────────
  const hasBunServe =
    typeof globalThis.Bun !== 'undefined' && typeof globalThis.Bun.serve === 'function';

  if (hasBunServe) {
    const bunWsMap = new WeakMap();
    const bunServer = globalThis.Bun.serve({
      hostname: bindHost,
      port: requestedPort,
      maxRequestBodySize: MAX_HTTP_BODY_BYTES * 2,
      async fetch(req, srv) {
        const urlObj = new URL(req.url);
        const upgradeHeader = req.headers.get('upgrade')?.toLowerCase();
        const authHeader = req.headers.get('authorization') ?? '';

        if (upgradeHeader === 'websocket') {
          const auth = authorizeWsUpgrade(urlObj, authHeader);
          if (!auth.ok) {
            return Response.json(
              { code: auth.code, detail: auth.detail },
              { status: auth.status, headers: { 'cache-control': 'no-store' } },
            );
          }
          const upgraded = srv.upgrade(req, { data: auth.sessionData });
          if (upgraded) return undefined;
          return Response.json(
            { code: 'upgrade_failed', detail: 'WebSocket upgrade failed.' },
            { status: 400 },
          );
        }

        try {
          const result = await handleHttpEndpoint(
            req.method.toUpperCase(),
            urlObj,
            authHeader,
            async () => {
              const text = await req.text();
              if (!text.trim()) return {};
              if (Buffer.byteLength(text, 'utf8') > MAX_HTTP_BODY_BYTES) {
                throw Object.assign(new Error('Request JSON body exceeds limit.'), { status: 413 });
              }
              let parsed;
              try {
                parsed = JSON.parse(text);
              } catch {
                throw Object.assign(new Error('Malformed JSON body.'), { status: 400 });
              }
              if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
                throw Object.assign(new Error('Request body must be a JSON object.'), { status: 400 });
              }
              return parsed;
            },
          );
          return Response.json(result.body, {
            status: result.status,
            headers: { 'cache-control': 'no-store' },
          });
        } catch (err) {
          const status = typeof err?.status === 'number' ? err.status : 500;
          return Response.json(
            {
              code: status === 400 ? 'invalid_request' : 'relay_error',
              detail: err instanceof Error ? err.message : 'Internal relay error.',
            },
            { status, headers: { 'cache-control': 'no-store' } },
          );
        }
      },
      websocket: {
        maxPayloadLength: maxFrameBytes,
        open(rawWs) {
          const wrapped = new BunRelayWsConnection(rawWs);
          bunWsMap.set(rawWs, wrapped);
          attachWsConnection(wrapped, rawWs.data);
        },
        message(rawWs, message) {
          const wrapped = bunWsMap.get(rawWs);
          if (!wrapped || wrapped.closed) return;
          if (typeof message === 'string') {
            wrapped.onMessage?.(0x01, Buffer.from(message, 'utf8'));
            return;
          }
          const buf = Buffer.isBuffer(message)
            ? Buffer.from(message)
            : Buffer.from(message.buffer, message.byteOffset, message.byteLength);
          wrapped.onMessage?.(0x02, buf);
        },
        close(rawWs, code, reason) {
          const wrapped = bunWsMap.get(rawWs);
          if (wrapped) {
            wrapped.closeInternal(code, reason);
          }
        },
      },
    });

    boundPort = bunServer.port;
    const urlHost = effectiveHostForUrl();

    return {
      server: bunServer,
      host: bindHost,
      port: boundPort,
      httpUrl: `http://${urlHost}:${boundPort}`,
      wsUrl: `ws://${urlHost}:${boundPort}`,
      getStats,
      getCapturedFrames() {
        return [...capturedFrames];
      },
      clearCapturedFrames() {
        capturedFrames.length = 0;
      },
      async close() {
        for (const tid of [...tickets.keys()]) {
          destroyTicket(tid, 1001, 'Relay server shutting down');
        }
        for (const ws of [...allWsConnections]) {
          ws.destroy();
        }
        allWsConnections.clear();
        inboxes.clear();
        devices.clear();
        rendezvousMap.clear();
        try {
          bunServer.stop(true);
        } catch {
          // ignore
        }
      },
    };
  }

  // Node.js `node:http` engine
  const rawTcpSockets = new Set();
  const nodeServer = http.createServer(async (req, res) => {
    const urlObj = new URL(req.url ?? '/', `http://${req.headers.host || '127.0.0.1'}`);
    const authHeader = req.headers.authorization ?? '';
    try {
      const result = await handleHttpEndpoint(
        (req.method ?? 'GET').toUpperCase(),
        urlObj,
        authHeader,
        () => readNodeJsonBody(req),
      );
      sendNodeJson(res, result.status, result.body);
    } catch (err) {
      const status = typeof err?.status === 'number' ? err.status : 500;
      sendNodeJson(res, status, {
        code: status === 400 ? 'invalid_request' : 'relay_error',
        detail: err instanceof Error ? err.message : 'Internal relay error.',
      });
    }
  });

  nodeServer.on('connection', (sock) => {
    rawTcpSockets.add(sock);
    sock.once('close', () => {
      rawTcpSockets.delete(sock);
    });
  });

  nodeServer.on('upgrade', (req, socket, head) => {
    const urlObj = new URL(req.url ?? '/', `http://${req.headers.host || '127.0.0.1'}`);
    const authHeader = req.headers.authorization ?? '';
    const auth = authorizeWsUpgrade(urlObj, authHeader);
    if (!auth.ok) {
      rejectNodeUpgrade(socket, auth.status, auth.code, auth.detail);
      return;
    }
    const wsConn = acceptNodeUpgrade(req, socket, head, maxFrameBytes);
    if (!wsConn) {
      if (auth.sessionData.kind === 'link') {
        destroyTicket(auth.sessionData.ticketId, 1002, 'Invalid WebSocket upgrade');
      }
      return;
    }
    attachWsConnection(wsConn, auth.sessionData);
  });

  await new Promise((resolveListen, rejectListen) => {
    const onErr = (err) => {
      nodeServer.removeListener('listening', onListening);
      rejectListen(err);
    };
    const onListening = () => {
      nodeServer.removeListener('error', onErr);
      const addr = nodeServer.address();
      boundPort = typeof addr === 'object' && addr ? addr.port : requestedPort;
      resolveListen();
    };
    nodeServer.once('error', onErr);
    nodeServer.once('listening', onListening);
    nodeServer.listen(requestedPort, bindHost);
  });

  const urlHost = effectiveHostForUrl();

  return {
    server: nodeServer,
    host: bindHost,
    port: boundPort,
    httpUrl: `http://${urlHost}:${boundPort}`,
    wsUrl: `ws://${urlHost}:${boundPort}`,
    getStats,
    getCapturedFrames() {
      return [...capturedFrames];
    },
    clearCapturedFrames() {
      capturedFrames.length = 0;
    },
    async close() {
      for (const tid of [...tickets.keys()]) {
        destroyTicket(tid, 1001, 'Relay server shutting down');
      }
      for (const ws of [...allWsConnections]) {
        ws.destroy();
      }
      allWsConnections.clear();
      inboxes.clear();
      devices.clear();
      rendezvousMap.clear();

      for (const sock of [...rawTcpSockets]) {
        try {
          sock.destroy();
        } catch {
          // ignore
        }
      }
      rawTcpSockets.clear();

      await new Promise((resolveClose) => {
        const fallback = setTimeout(resolveClose, 50);
        fallback.unref?.();
        try {
          nodeServer.close(() => {
            clearTimeout(fallback);
            resolveClose();
          });
        } catch {
          clearTimeout(fallback);
          resolveClose();
        }
      });
    },
  };
}

function parseCliArgs(argv) {
  let port;
  let host;
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === '--port' && argv[i + 1] !== undefined) {
      port = Number(argv[++i]);
    } else if (arg.startsWith('--port=')) {
      port = Number(arg.slice('--port='.length));
    } else if (arg === '--host' && argv[i + 1] !== undefined) {
      host = argv[++i];
    } else if (arg.startsWith('--host=')) {
      host = arg.slice('--host='.length);
    }
  }

  if (port === undefined) {
    const envPort = process.env.PEER_RELAY_PORT?.trim();
    port = envPort !== undefined && envPort !== '' ? Number(envPort) : DEFAULT_PEER_RELAY_PORT;
  }
  if (!Number.isInteger(port) || port < 0 || port > 65535) {
    throw new Error(`Invalid relay port: ${String(port)}`);
  }

  if (!host) {
    host = process.env.PEER_RELAY_HOST?.trim() || '127.0.0.1';
  }
  return { port, host };
}

const isMainModule =
  Boolean(import.meta.main) ||
  (Boolean(process.argv[1]) && resolve(process.argv[1]) === fileURLToPath(import.meta.url));

if (isMainModule) {
  const { port, host } = parseCliArgs(process.argv.slice(2));
  startPeerRelayServer({ port, host, recordWireTap: false })
    .then((relay) => {
      process.stdout.write(
        `${JSON.stringify({
          event: 'peer_relay_started',
          host: relay.host,
          port: relay.port,
          http_url: relay.httpUrl,
          ws_url: relay.wsUrl,
        })}\n`,
      );
      const shutdown = () => {
        relay.close().finally(() => process.exit(0));
      };
      process.once('SIGINT', shutdown);
      process.once('SIGTERM', shutdown);
    })
    .catch((err) => {
      process.stderr.write(`[peer-relay-server] fatal startup error: ${err?.message ?? err}\n`);
      process.exit(1);
    });
}
