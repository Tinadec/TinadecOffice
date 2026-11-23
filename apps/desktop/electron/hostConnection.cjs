/** The private verification callback stays in main. Only safe readiness snapshots are published. */
function createHostConnection({ verify, managed = true, onStatusChange = () => {},
  retryDelaysMs = [1_000, 2_000, 5_000, 10_000], watchIntervalMs = 15_000,
  verificationTimeoutMs = 10_000, now = () => new Date().toISOString(),
  setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
  if (typeof verify !== 'function') throw new TypeError('Host verification is required.');
  let status = { state: 'checking', managed };
  let timer;
  let pending;
  let controller;
  let stopped = false;
  let generation = 0;
  let retryIndex = 0;
  const snapshot = () => ({ ...status, ...(status.error ? { error: { ...status.error } } : {}) });
  function publish(next) {
    status = next;
    try { onStatusChange(snapshot()); } catch { /* A disposed UI cannot affect authorization. */ }
  }
  function clearScheduled() { if (timer !== undefined) clearTimer(timer); timer = undefined; }
  function schedule(delay) {
    clearScheduled();
    if (stopped) return;
    timer = setTimer(() => { timer = undefined; void check(); }, delay);
    timer?.unref?.();
  }
  function classify(error) {
    // A child that died during bootstrap never bound a port: retrying cannot fix a broken document,
    // and "temporarily unavailable" hides the one thing a user can act on.
    const startupFailure = error?.startupFailure;
    if (startupFailure?.code) {
      return { state: 'unavailable', error: {
        code: 'host_startup_configuration_invalid',
        message: 'A configuration file cannot be read, so the local services did not start. Repair or restore it, then retry.',
        document: typeof startupFailure.document === 'string' ? startupFailure.document : null,
        reason: typeof startupFailure.code === 'string' ? startupFailure.code : null,
      } };
    }
    const rejected = ['host_identity_rejected', 'host_endpoint_invalid', 'host_credential_invalid', 'host_nonce_invalid'].includes(error?.code)
      || /unexpected service|invalid trusted host identity proof/i.test(error?.message ?? '');
    return rejected
      ? { state: 'rejected', error: { code: 'host_identity_rejected', message: 'The local services could not prove their trusted host identity. Start Core, Gateway and Desktop together, then retry.' } }
      : { state: 'unavailable', error: { code: 'host_identity_unavailable', message: 'The local services are temporarily unavailable. Retry the host connection.' } };
  }
  function check() {
    if (stopped) return Promise.resolve(snapshot());
    if (pending) return pending;
    clearScheduled();
    const epoch = generation;
    const wasReady = status.state === 'ready';
    if (!wasReady) publish({ state: 'checking', managed });
    controller = new AbortController();
    const attemptController = controller;
    let timeout;
    const aborted = new Promise((_, reject) => {
      attemptController.signal.addEventListener('abort', () => reject(attemptController.signal.reason), { once: true });
      timeout = setTimer(() => attemptController.abort(new Error('Host verification timed out.')), verificationTimeoutMs);
      timeout?.unref?.();
    });
    const work = Promise.race([Promise.resolve().then(() => verify({ signal: attemptController.signal })), aborted]);
    pending = work.then(result => {
      if (result !== true) throw Object.assign(new Error('The host supplied an invalid trusted host identity proof.'), { code: 'host_identity_rejected' });
      if (stopped || epoch !== generation) return snapshot();
      retryIndex = 0;
      publish({ state: 'ready', managed, checked_at: now() });
      if (managed) schedule(watchIntervalMs);
      return snapshot();
    }).catch(error => {
      if (stopped || epoch !== generation) return snapshot();
      publish({ ...classify(error), managed, checked_at: now() });
      if (status.state === 'unavailable' && retryIndex < retryDelaysMs.length) schedule(retryDelaysMs[retryIndex++]);
      return snapshot();
    }).finally(() => {
      clearTimer(timeout);
      if (controller === attemptController) controller = undefined;
      pending = undefined;
    });
    return pending;
  }
  function retry() {
    if (pending) return pending;
    retryIndex = 0;
    return check();
  }
  function stop() {
    if (stopped) return;
    stopped = true;
    generation++;
    clearScheduled();
    controller?.abort(new Error('Host connection stopped.'));
    // Exit is final: late verification cannot reauthorize a closing host.
    status = { state: 'unavailable', managed, error: { code: 'host_connection_stopped', message: 'The host is shutting down.' } };
  }
  return { start: retry, retry, snapshot, stop, isReady: () => !stopped && status.state === 'ready' };
}
module.exports = { createHostConnection };
