import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mapCoreErrorToExternal } from './errorMapper.js';

/**
 * `ALLOWED_CODES` is the external contract: a code Core authored that is not listed here comes
 * back out as `conflict`, so a caller that branches on the reason cannot. Core now stamps a code
 * on the rejections it writes itself (missing required parameter, unmatched route, oversized
 * body), which widened that family, so this table has to follow it.
 */
test('core codes outside the whitelist are rewritten as conflict', () => {
  const mapped = mapCoreErrorToExternal(400, { code: 'brand_new_code', detail: 'x' }, '/api/v1/x');
  assert.equal(mapped.code, 'conflict');
});

test('framework-authored core rejections keep their own code through the gateway', () => {
  const cases: Array<[number, string]> = [
    [400, 'invalid_request'],
    // The paged audit reads (/api/v1/model-invocations) reject a malformed filter with these two,
    // and "you passed the wrong thing" must not arrive as a retryable `conflict`.
    [400, 'invalid_query'],
    [400, 'invalid_cursor'],
    [401, 'unauthorized'],
    [403, 'forbidden'],
    [404, 'not_found'],
    [405, 'method_not_allowed'],
    [413, 'payload_too_large'],
    [415, 'unsupported_media_type'],
    [422, 'invalid_request'],
    [429, 'rate_limited'],
    [500, 'internal_error'],
    [406, 'request_failed'],
  ];
  for (const [status, code] of cases) {
    const mapped = mapCoreErrorToExternal(
      status,
      { code, detail: 'Core named the rule.', type: `https://tinadec.dev/errors/${code}`, trace_id: 'trace-1' },
      '/api/v1/x',
    );
    assert.equal(mapped.code, code, `status ${status}`);
    assert.equal(mapped.title, code, `status ${status}`);
    assert.equal(mapped.status, status);
    assert.equal(mapped.detail, 'Core named the rule.');
    assert.equal(mapped.trace_id, 'trace-1');
  }
});

/**
 * The failure shape this guard exists for: an RFC 9110 body carries only a human title, and the
 * best the gateway can report is `conflict` — a 400 the caller is told to retry.
 */
test('a core problem with no code degrades to conflict and says so through the title', () => {
  const mapped = mapCoreErrorToExternal(400, { title: 'Bad Request', status: 400 }, '/api/v1/x');
  assert.equal(mapped.code, 'conflict');
  assert.equal(mapped.title, 'conflict');
});
