/**
 * Four-step application-layer handshake over mTLS (`peer.hello` -> `peer.hello-ack` /
 * `peer.hello-reject` -> `peer.hello-confirm` -> `peer.ready`) with strict DER SHA-256
 * certificate fingerprint pinning on both client and server sides.
 */

import type { TLSSocket } from 'node:tls';
import type { PeerAccessPolicy } from './peerPolicyGuard.js';
import {
  extractPeerCertFingerprint,
  intersectCapabilities,
  isPeerErrorCode,
  normalizeFingerprint,
  PEER_CAPABILITIES,
  PEER_PROTOCOL_MAJOR,
  PEER_PROTOCOL_MINOR,
  type PairingMode,
  type PeerErrorCode,
  PeerFrameConnection,
  PeerProtocolError,
  type PeerRole,
  verifyPinnedFingerprint,
} from './peerProtocol.js';

export interface ClientHandshakeOptions {
  connection: PeerFrameConnection;
  tlsSocket: Pick<TLSSocket, 'getPeerCertificate'>;
  localDeviceId: string;
  localDeviceName: string;
  pairingMode: PairingMode;
  pairingCode?: string;
  expectedRemoteFingerprint?: string | null;
  capabilities?: readonly string[];
  handshakeTimeoutMs?: number;
  /** Test-only override; production always extracts from `tlsSocket.getPeerCertificate(true).raw`. */
  extractFingerprintFn?: (socket: Pick<TLSSocket, 'getPeerCertificate'>) => string | null;
}

export interface ClientProbeResult {
  mode: 'probe';
  remoteDeviceId: string;
  remoteDeviceName: string;
  remoteFingerprint: string;
  protocolMajor: number;
  protocolMinor: number;
  capabilities: string[];
}

export interface ClientAuthenticatedHandshakeResult {
  mode: 'code' | 'grant';
  remoteDeviceId: string;
  remoteDeviceName: string;
  remoteFingerprint: string;
  protocolMajor: number;
  protocolMinor: number;
  capabilities: string[];
  policyRevision: string;
  role?: PeerRole;
}

export type ClientHandshakeResult = ClientProbeResult | ClientAuthenticatedHandshakeResult;

export interface CoreHandshakeVerifyInput {
  transport: 'lan';
  controllerDeviceId: string;
  displayName: string;
  /** Authoritative SHA-256 hex computed from `socket.getPeerCertificate(true).raw`. */
  remoteFingerprint: string;
  protocolMajor: number;
  protocolMinor: number;
  pairingMode: 'code' | 'grant';
  pairingCode?: string;
  expectedRemoteFingerprint?: string;
}

export interface CoreHandshakeVerifyDecision {
  accepted: boolean;
  code?: PeerErrorCode;
  message?: string;
  grantId?: string;
  controllerDeviceId?: string;
  displayName?: string;
  policyRevision?: string;
  policy?: PeerAccessPolicy;
}

export interface ServerHandshakeOptions {
  connection: PeerFrameConnection;
  tlsSocket: Pick<TLSSocket, 'getPeerCertificate'>;
  localDeviceId: string;
  localDeviceName: string;
  localFingerprint: string;
  isInboundEnabled: () => boolean;
  verifyHandshakeWithCore: (input: CoreHandshakeVerifyInput) => Promise<CoreHandshakeVerifyDecision>;
  capabilities?: readonly string[];
  handshakeTimeoutMs?: number;
  extractFingerprintFn?: (socket: Pick<TLSSocket, 'getPeerCertificate'>) => string | null;
}

export interface ServerProbeHandshakeResult {
  mode: 'probe';
}

export interface ServerAuthenticatedHandshakeResult {
  mode: 'code' | 'grant';
  controllerDeviceId: string;
  displayName: string;
  clientFingerprint: string;
  protocolMajor: number;
  protocolMinor: number;
  capabilities: string[];
  policyRevision: string;
  policy: PeerAccessPolicy;
}

export type ServerHandshakeResult = ServerProbeHandshakeResult | ServerAuthenticatedHandshakeResult;

function sendRejectBestEffort(
  connection: PeerFrameConnection,
  code: PeerErrorCode,
  message: string,
): void {
  try {
    connection.sendFrame({ type: 'peer.hello-reject', code, message });
  } catch {
    // Ignore write failure when tearing down rejected handshake.
  }
}

/**
 * Controller-side 4-step mTLS handshake (`peer.hello` -> `peer.hello-ack` -> `peer.hello-confirm` -> `peer.ready`).
 */
export async function performClientHandshake(
  options: ClientHandshakeOptions,
): Promise<ClientHandshakeResult> {
  const { connection, tlsSocket } = options;
  const timeoutMs = options.handshakeTimeoutMs ?? 10_000;
  const extractFp = options.extractFingerprintFn ?? extractPeerCertFingerprint;

  const actualRemoteFingerprint = extractFp(tlsSocket);
  if (!actualRemoteFingerprint) {
    connection.destroy();
    throw new PeerProtocolError(
      'encryption_unavailable',
      'Remote TLS server did not present a valid X.509 DER certificate.',
    );
  }

  if (options.expectedRemoteFingerprint) {
    try {
      verifyPinnedFingerprint(
        actualRemoteFingerprint,
        options.expectedRemoteFingerprint,
        'Remote peer server certificate',
      );
    } catch (err) {
      if (err instanceof PeerProtocolError) {
        sendRejectBestEffort(connection, err.code, err.message);
      }
      connection.destroy(err instanceof Error ? err : undefined);
      throw err;
    }
  } else if (options.pairingMode !== 'probe') {
    const err = new PeerProtocolError(
      'fingerprint_mismatch',
      'Pinned remote certificate fingerprint is required for code/grant handshakes.',
    );
    sendRejectBestEffort(connection, err.code, err.message);
    connection.destroy(err);
    throw err;
  }

  const normalizedExpected = options.expectedRemoteFingerprint
    ? normalizeFingerprint(options.expectedRemoteFingerprint) ?? undefined
    : undefined;

  connection.sendFrame({
    type: 'peer.hello',
    protocolMajor: PEER_PROTOCOL_MAJOR,
    protocolMinor: PEER_PROTOCOL_MINOR,
    deviceId: options.localDeviceId,
    deviceName: options.localDeviceName,
    pairingMode: options.pairingMode,
    ...(options.pairingCode ? { pairingCode: options.pairingCode } : {}),
    capabilities: [...(options.capabilities ?? PEER_CAPABILITIES)],
    ...(normalizedExpected ? { expectedRemoteFingerprint: normalizedExpected } : {}),
  });

  const step2 = await connection.nextFrame(timeoutMs);
  if (step2.header.type === 'peer.hello-reject') {
    const code = isPeerErrorCode(step2.header.code) ? step2.header.code : 'handshake_failed';
    const err = new PeerProtocolError(code, step2.header.message || 'Remote peer rejected handshake.');
    connection.destroy(err);
    throw err;
  }
  if (step2.header.type !== 'peer.hello-ack') {
    const err = new PeerProtocolError(
      'handshake_failed',
      `Expected peer.hello-ack, received ${step2.header.type}.`,
    );
    connection.destroy(err);
    throw err;
  }

  const ack = step2.header;
  if (ack.protocolMajor !== PEER_PROTOCOL_MAJOR) {
    const err = new PeerProtocolError(
      'protocol_major_mismatch',
      `Incompatible peer protocol major version: local=${PEER_PROTOCOL_MAJOR}, remote=${ack.protocolMajor}.`,
    );
    sendRejectBestEffort(connection, err.code, err.message);
    connection.destroy(err);
    throw err;
  }

  // Verify the self-reported ack fingerprint matches the actual TLS DER certificate on the wire.
  if (ack.certificateFingerprint) {
    try {
      verifyPinnedFingerprint(
        actualRemoteFingerprint,
        ack.certificateFingerprint,
        'Remote peer hello-ack certificate',
      );
    } catch (err) {
      if (err instanceof PeerProtocolError) {
        sendRejectBestEffort(connection, err.code, err.message);
      }
      connection.destroy(err instanceof Error ? err : undefined);
      throw err;
    }
  }

  const negotiatedCaps = intersectCapabilities(ack.capabilities ?? []);

  if (options.pairingMode === 'probe') {
    connection.destroy();
    return {
      mode: 'probe',
      remoteDeviceId: ack.deviceId,
      remoteDeviceName: ack.deviceName,
      remoteFingerprint: actualRemoteFingerprint,
      protocolMajor: ack.protocolMajor,
      protocolMinor: ack.protocolMinor,
      capabilities: negotiatedCaps,
    };
  }

  // Step 3: Controller confirms the verified TLS DER fingerprint.
  connection.sendFrame({
    type: 'peer.hello-confirm',
    deviceId: options.localDeviceId,
    confirmedRemoteFingerprint: actualRemoteFingerprint,
  });

  // Step 4: Wait for peer.ready.
  const step4 = await connection.nextFrame(timeoutMs);
  if (step4.header.type === 'peer.hello-reject') {
    const code = isPeerErrorCode(step4.header.code) ? step4.header.code : 'handshake_failed';
    const err = new PeerProtocolError(code, step4.header.message || 'Remote peer rejected confirm.');
    connection.destroy(err);
    throw err;
  }
  if (step4.header.type !== 'peer.ready') {
    const err = new PeerProtocolError(
      'handshake_failed',
      `Expected peer.ready, received ${step4.header.type}.`,
    );
    connection.destroy(err);
    throw err;
  }

  return {
    mode: options.pairingMode,
    remoteDeviceId: ack.deviceId,
    remoteDeviceName: ack.deviceName,
    remoteFingerprint: actualRemoteFingerprint,
    protocolMajor: ack.protocolMajor,
    protocolMinor: ack.protocolMinor,
    capabilities: negotiatedCaps,
    policyRevision: step4.header.policyRevision ?? ack.policyRevision ?? '1',
    role: step4.header.role,
  };
}

/**
 * Worker-side 4-step mTLS handshake (`peer.hello` -> Core verify -> `peer.hello-ack` -> `peer.hello-confirm` -> `peer.ready`).
 */
export async function performServerHandshake(
  options: ServerHandshakeOptions,
): Promise<ServerHandshakeResult> {
  const { connection, tlsSocket } = options;
  const timeoutMs = options.handshakeTimeoutMs ?? 10_000;
  const extractFp = options.extractFingerprintFn ?? extractPeerCertFingerprint;

  const step1 = await connection.nextFrame(timeoutMs);
  if (step1.header.type !== 'peer.hello') {
    const err = new PeerProtocolError(
      'handshake_failed',
      `Expected peer.hello as first frame, received ${step1.header.type}.`,
    );
    sendRejectBestEffort(connection, 'handshake_failed', err.message);
    connection.destroy(err);
    throw err;
  }

  const hello = step1.header;

  if (!options.isInboundEnabled()) {
    const err = new PeerProtocolError('inbound_disabled', 'Inbound peer access is disabled on this device.');
    sendRejectBestEffort(connection, 'inbound_disabled', err.message);
    connection.destroy(err);
    throw err;
  }

  if (hello.protocolMajor !== PEER_PROTOCOL_MAJOR) {
    const err = new PeerProtocolError(
      'protocol_major_mismatch',
      `Unsupported peer protocol major version ${hello.protocolMajor}; expected ${PEER_PROTOCOL_MAJOR}.`,
    );
    sendRejectBestEffort(connection, 'protocol_major_mismatch', err.message);
    connection.destroy(err);
    throw err;
  }

  const normalizedLocalFp = normalizeFingerprint(options.localFingerprint);
  if (!normalizedLocalFp) {
    const err = new PeerProtocolError('encryption_unavailable', 'Local TLS certificate fingerprint is unavailable.');
    sendRejectBestEffort(connection, 'encryption_unavailable', err.message);
    connection.destroy(err);
    throw err;
  }

  if (hello.expectedRemoteFingerprint) {
    try {
      verifyPinnedFingerprint(
        normalizedLocalFp,
        hello.expectedRemoteFingerprint,
        'Server local certificate vs client expectedRemoteFingerprint',
      );
    } catch (err) {
      const pErr =
        err instanceof PeerProtocolError
          ? err
          : new PeerProtocolError('fingerprint_mismatch', 'Server certificate fingerprint mismatch.');
      sendRejectBestEffort(connection, pErr.code, pErr.message);
      connection.destroy(pErr);
      throw pErr;
    }
  }

  const negotiatedCaps = intersectCapabilities(hello.capabilities ?? PEER_CAPABILITIES);

  // Probe mode only discloses public server identity + real TLS cert without consuming pairing_code or creating a grant.
  if (hello.pairingMode === 'probe') {
    connection.sendFrame({
      type: 'peer.hello-ack',
      protocolMajor: PEER_PROTOCOL_MAJOR,
      protocolMinor: PEER_PROTOCOL_MINOR,
      deviceId: options.localDeviceId,
      deviceName: options.localDeviceName,
      certificateFingerprint: normalizedLocalFp,
      capabilities: negotiatedCaps,
      probeOnly: true,
    });
    return { mode: 'probe' };
  }

  if (hello.pairingMode !== 'code' && hello.pairingMode !== 'grant') {
    const err = new PeerProtocolError('handshake_failed', `Invalid pairingMode: ${String(hello.pairingMode)}.`);
    sendRejectBestEffort(connection, 'handshake_failed', err.message);
    connection.destroy(err);
    throw err;
  }

  // Strictly compute controller's SHA-256 fingerprint from the TLS X.509 DER bytes on the socket!
  const actualClientFingerprint = extractFp(tlsSocket);
  if (!actualClientFingerprint) {
    const err = new PeerProtocolError(
      'unauthorized',
      'Client TLS X.509 certificate is required for mTLS peer authentication.',
    );
    sendRejectBestEffort(connection, 'unauthorized', err.message);
    connection.destroy(err);
    throw err;
  }

  // Note: If an untrusted client attempts to smuggle a self-reported `certificateFingerprint` field
  // inside `peer.hello`, reject immediately if it contradicts the real TLS DER fingerprint, and
  // NEVER pass anything except `actualClientFingerprint` to Core.
  const smuggledFp = (hello as Record<string, unknown>).certificateFingerprint;
  if (typeof smuggledFp === 'string' && smuggledFp.trim().length > 0) {
    try {
      verifyPinnedFingerprint(actualClientFingerprint, smuggledFp, 'Client TLS certificate');
    } catch (err) {
      const pErr =
        err instanceof PeerProtocolError
          ? err
          : new PeerProtocolError('fingerprint_mismatch', 'Client TLS fingerprint mismatch.');
      sendRejectBestEffort(connection, pErr.code, pErr.message);
      connection.destroy(pErr);
      throw pErr;
    }
  }

  const decision = await options.verifyHandshakeWithCore({
    transport: 'lan',
    controllerDeviceId: hello.deviceId,
    displayName: hello.deviceName || hello.deviceId,
    remoteFingerprint: actualClientFingerprint,
    protocolMajor: hello.protocolMajor,
    protocolMinor: hello.protocolMinor,
    pairingMode: hello.pairingMode,
    pairingCode: hello.pairingCode,
    expectedRemoteFingerprint: hello.expectedRemoteFingerprint,
  });

  if (!decision.accepted || !decision.policy) {
    const rejectCode: PeerErrorCode =
      decision.code && isPeerErrorCode(decision.code) ? decision.code : 'unauthorized';
    const rejectMsg = decision.message || 'Peer handshake rejected by Core policy authority.';
    const err = new PeerProtocolError(rejectCode, rejectMsg);
    sendRejectBestEffort(connection, rejectCode, rejectMsg);
    connection.destroy(err);
    throw err;
  }

  const policyRevision = decision.policyRevision ?? decision.policy.revision ?? '1';

  connection.sendFrame({
    type: 'peer.hello-ack',
    protocolMajor: PEER_PROTOCOL_MAJOR,
    protocolMinor: PEER_PROTOCOL_MINOR,
    deviceId: options.localDeviceId,
    deviceName: options.localDeviceName,
    certificateFingerprint: normalizedLocalFp,
    policyRevision,
    capabilities: negotiatedCaps,
  });

  const step3 = await connection.nextFrame(timeoutMs);
  if (step3.header.type === 'peer.hello-reject') {
    const code = isPeerErrorCode(step3.header.code) ? step3.header.code : 'handshake_failed';
    const err = new PeerProtocolError(code, step3.header.message || 'Client aborted handshake.');
    connection.destroy(err);
    throw err;
  }
  if (step3.header.type !== 'peer.hello-confirm') {
    const err = new PeerProtocolError(
      'handshake_failed',
      `Expected peer.hello-confirm, received ${step3.header.type}.`,
    );
    sendRejectBestEffort(connection, 'handshake_failed', err.message);
    connection.destroy(err);
    throw err;
  }

  try {
    verifyPinnedFingerprint(
      normalizedLocalFp,
      step3.header.confirmedRemoteFingerprint,
      'Client confirmedRemoteFingerprint',
    );
  } catch (err) {
    const pErr =
      err instanceof PeerProtocolError
        ? err
        : new PeerProtocolError('fingerprint_mismatch', 'Confirmed server fingerprint mismatch.');
    sendRejectBestEffort(connection, pErr.code, pErr.message);
    connection.destroy(pErr);
    throw pErr;
  }

  connection.sendFrame({
    type: 'peer.ready',
    policyRevision,
    role: decision.policy.role,
  });

  return {
    mode: hello.pairingMode,
    controllerDeviceId: decision.controllerDeviceId ?? hello.deviceId,
    displayName: decision.displayName ?? (hello.deviceName || hello.deviceId),
    clientFingerprint: actualClientFingerprint,
    protocolMajor: hello.protocolMajor,
    protocolMinor: hello.protocolMinor,
    capabilities: negotiatedCaps,
    policyRevision,
    policy: decision.policy,
  };
}
