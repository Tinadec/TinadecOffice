import { createHash, randomBytes, scryptSync, timingSafeEqual } from 'node:crypto';
import type { AuthVerificationResult } from './types.js';

const SCRYPT_N = 16384;
const SCRYPT_R = 8;
const SCRYPT_P = 1;
const KEY_LENGTH = 64;
const MIN_PASSWORD_LENGTH = 8;

export function hashPassword(password: string): string {
  const salt = randomBytes(16);
  const derived = scryptSync(password, salt, KEY_LENGTH, { N: SCRYPT_N, r: SCRYPT_R, p: SCRYPT_P });
  return `scrypt$${salt.toString('base64url')}$${derived.toString('base64url')}`;
}

export function verifyPasswordHash(password: string, storedHash: string): boolean {
  if (!password || !storedHash) return false;
  const parts = storedHash.split('$');
  if (parts.length !== 3 || parts[0] !== 'scrypt') return false;
  try {
    const salt = Buffer.from(parts[1]!, 'base64url');
    const expected = Buffer.from(parts[2]!, 'base64url');
    if (salt.length < 8 || expected.length !== KEY_LENGTH) return false;
    const actual = scryptSync(password, salt, KEY_LENGTH, { N: SCRYPT_N, r: SCRYPT_R, p: SCRYPT_P });
    return actual.length === expected.length && timingSafeEqual(actual, expected);
  } catch {
    return false;
  }
}

export function verifyPlaintextConstantTime(candidate: string, expected: string): boolean {
  if (!candidate || !expected) return false;
  const a = createHash('sha256').update(candidate, 'utf8').digest();
  const b = createHash('sha256').update(expected, 'utf8').digest();
  return timingSafeEqual(a, b);
}

export function validateNewPassword(password: string): { ok: true } | { ok: false; message: string } {
  if (typeof password !== 'string' || password.length < MIN_PASSWORD_LENGTH) {
    return { ok: false, message: `Password must be at least ${MIN_PASSWORD_LENGTH} characters.` };
  }
  return { ok: true };
}

export interface RateLimitCheck {
  allowed: boolean;
  retryAfterSeconds?: number;
}

interface AttemptBucket {
  failures: number;
  firstFailureAt: number;
  lockedUntil: number;
}

export class LoginRateLimiter {
  private readonly buckets = new Map<string, AttemptBucket>();

  constructor(
    private readonly maxAttempts = 5,
    private readonly windowMs = 15 * 60 * 1000,
    private readonly lockoutMs = 15 * 60 * 1000,
    private readonly nowFn: () => number = () => Date.now(),
  ) {}

  check(key: string): RateLimitCheck {
    const now = this.nowFn();
    const bucket = this.buckets.get(key);
    if (!bucket) return { allowed: true };

    if (bucket.lockedUntil > now) {
      return {
        allowed: false,
        retryAfterSeconds: Math.max(1, Math.ceil((bucket.lockedUntil - now) / 1000)),
      };
    }

    if (now - bucket.firstFailureAt > this.windowMs) {
      this.buckets.delete(key);
    }
    return { allowed: true };
  }

  recordFailure(key: string): void {
    const now = this.nowFn();
    const existing = this.buckets.get(key);
    if (!existing || now - existing.firstFailureAt > this.windowMs) {
      const failures = 1;
      this.buckets.set(key, {
        failures,
        firstFailureAt: now,
        lockedUntil: failures >= this.maxAttempts ? now + this.lockoutMs : 0,
      });
      return;
    }

    existing.failures += 1;
    if (existing.failures >= this.maxAttempts) {
      existing.lockedUntil = now + this.lockoutMs;
    }
  }

  recordSuccess(key: string): void {
    this.buckets.delete(key);
  }

  clear(): void {
    this.buckets.clear();
  }
}

export function authenticateLocalPassword(options: {
  password: string;
  storedHash?: string;
  envPassword?: string;
  clientKey: string;
  rateLimiter: LoginRateLimiter;
}): AuthVerificationResult {
  const limit = options.rateLimiter.check(options.clientKey);
  if (!limit.allowed) {
    return {
      ok: false,
      status: 429,
      code: 'too_many_login_attempts',
      message: 'Too many failed login attempts. Please wait and try again.',
      retryAfterSeconds: limit.retryAfterSeconds,
    };
  }

  const hasStored = Boolean(options.storedHash?.trim());
  const hasEnv = Boolean(options.envPassword);
  if (!hasStored && !hasEnv) {
    return {
      ok: false,
      status: 503,
      code: 'local_password_not_configured',
      message: 'No owner web password has been configured yet.',
    };
  }

  const matched =
    (hasStored && verifyPasswordHash(options.password, options.storedHash!)) ||
    (!hasStored && hasEnv && verifyPlaintextConstantTime(options.password, options.envPassword!));

  if (!matched) {
    options.rateLimiter.recordFailure(options.clientKey);
    const after = options.rateLimiter.check(options.clientKey);
    if (!after.allowed) {
      return {
        ok: false,
        status: 429,
        code: 'too_many_login_attempts',
        message: 'Too many failed login attempts. Please wait and try again.',
        retryAfterSeconds: after.retryAfterSeconds,
      };
    }
    return {
      ok: false,
      status: 401,
      code: 'invalid_credentials',
      message: 'Invalid password.',
    };
  }

  options.rateLimiter.recordSuccess(options.clientKey);
  return {
    ok: true,
    principal: {
      userId: 'owner',
      tenantId: 'default',
      roles: ['owner'],
      authMethod: 'local_password',
      displayName: 'Owner',
    },
  };
}
