// Acceptance helper only. Never imports into the product or writes an existing user root.
import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { lstat, readFile, realpath } from 'node:fs/promises';
import { basename, isAbsolute, join, relative, resolve, sep } from 'node:path';

const repo = resolve(import.meta.dirname, '../../..');
const ownedParent = join(repo, '.tinadec_dev', 'tmp');
const samePath = (a, b) => process.platform === 'win32' ? a.toLowerCase() === b.toLowerCase() : a === b;
const digest = value => createHash('sha256').update(value).digest('hex');
export const HISTORY_TITLE = 'Transcript isolated 10000 history';
export const HISTORY_BODY = 'TRANSCRIPT_LARGE_HISTORY_BODY: Complete persisted message body for the isolated 10000-message history acceptance.';

async function checkedRealPath(path) {
  assert.equal((await lstat(path)).isSymbolicLink(), false, 'Acceptance paths must not be links');
  assert.ok(samePath(await realpath(path), resolve(path)), 'Acceptance path resolves through a link');
  return path;
}

/** Call after real API createSession(null, HISTORY_TITLE) and postMessage(id, HISTORY_BODY).
 * Uses the real content-addressed body: 10000 complete messages intentionally share one
 * valid reference, just as identical production messages do. This is a read-history
 * fixture; it creates no runs, turns or synthetic execution/context facts.
 */
export async function seedRealHistory({ ownedRoot, sessionId }) {
  assert.ok(isAbsolute(ownedRoot), 'Owned root must be absolute');
  const root = resolve(ownedRoot);
  assert.match(basename(root), /^transcript-real-[a-f0-9]{10}$/);
  assert.ok(samePath(resolve(root, '..'), ownedParent), 'Only a direct transcript-real-* child of .tinadec_dev/tmp is allowed');
  assert.match(sessionId, /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i);
  await checkedRealPath(ownedParent);
  await checkedRealPath(root);
  const dataRoot = await checkedRealPath(join(root, 'user', 'data'));
  const databasePath = await checkedRealPath(join(dataRoot, 'tinadec.db'));
  const { DatabaseSync } = await import('node:sqlite');
  const started = performance.now();
  const db = new DatabaseSync(databasePath, { timeout: 5000 });
  let inTransaction = false;
  try {
    db.exec('PRAGMA busy_timeout=5000; BEGIN IMMEDIATE');
    inTransaction = true;
    const session = db.prepare('SELECT * FROM sessions WHERE lower(id)=?').get(sessionId.toLowerCase());
    assert.ok(session, 'The real API must create the dedicated session first');
    assert.equal(session.title, HISTORY_TITLE);
    assert.equal(session.project_id, null, 'Fixture must remain in owned user scope');
    assert.equal(session.lifecycle_status, 'active');
    assert.equal(session.history_revision, 1);
    assert.equal(db.prepare('SELECT count(*) AS count FROM runs WHERE session_id=?').get(session.id).count, 0, 'Fixture must have no real runs');
    const existing = db.prepare('SELECT * FROM messages WHERE session_id=? ORDER BY sequence').all(session.id);
    assert.equal(existing.length, 1, 'Refuse an already used or already seeded session');
    const seed = existing[0];
    assert.equal(seed.sequence, 1);
    assert.equal(seed.role, 'user');
    assert.equal(seed.run_id, null);
    assert.equal(seed.turn_id, null);
    assert.equal(seed.reverted_at, null);
    assert.equal(seed.tenant_id, session.tenant_id);
    assert.equal(seed.workspace_id, session.workspace_id);
    const expectedBody = Buffer.from(HISTORY_BODY, 'utf8');
    assert.equal(seed.content_hash, digest(expectedBody));
    assert.equal(seed.content_length, expectedBody.length);
    assert.match(seed.content_reference, /^content\/tenants\/[a-f0-9]{32}\/[a-f0-9]{32}\/message\/[a-f0-9]{64}$/);
    const bodyPath = resolve(dataRoot, seed.content_reference);
    assert.ok(relative(dataRoot, bodyPath) && !relative(dataRoot, bodyPath).startsWith('..' + sep) && !isAbsolute(relative(dataRoot, bodyPath)));
    await checkedRealPath(bodyPath);
    assert.deepEqual(await readFile(bodyPath), expectedBody, 'Seed ContentStore body integrity mismatch');
    const ids = [seed.id.toLowerCase()];
    const insert = db.prepare(`INSERT INTO messages
      (id,tenant_id,workspace_id,session_id,run_id,turn_id,client_message_id,sequence,role,content_reference,content_hash,content_length,created_at,reverted_at)
      VALUES (?,?,?,?,NULL,NULL,NULL,?,'user',?,?,?,?,NULL)`);
    for (let sequence = 2; sequence <= 10000; sequence++) {
      const id = randomUUID();
      ids.push(id);
      insert.run(id.toUpperCase(), seed.tenant_id, seed.workspace_id, session.id, sequence,
        seed.content_reference, seed.content_hash, seed.content_length, seed.created_at);
    }
    const timestamp = new Date().toISOString().replace('T', ' ').replace('Z', '+00:00');
    assert.equal(db.prepare('UPDATE sessions SET history_revision=history_revision+9999,updated_at=? WHERE id=? AND history_revision=1').run(timestamp, session.id).changes, 1);
    const counts = db.prepare('SELECT count(*) AS count,count(DISTINCT id) AS ids,min(sequence) AS first,max(sequence) AS last FROM messages WHERE session_id=?').get(session.id);
    assert.deepEqual({ ...counts }, { count: 10000, ids: 10000, first: 1, last: 10000 });
    db.exec('COMMIT');
    inTransaction = false;
    return { sessionId: sessionId.toLowerCase(), count: 10000, ids, expected_ids_sha256: digest(JSON.stringify(ids)),
      seed_ms: performance.now() - started, complete_body_bytes: expectedBody.length, content_files: 1,
      strategy: 'One validated real ContentStore body, content-addressed deduplication, one SQLite transaction', uniqueBodyDedup: true,
      execution_facts_created: false, modified_session_count: 1 };
  } finally {
    if (inTransaction) db.exec('ROLLBACK');
    db.close();
  }
}

/** Embed in real-electron.cjs after controlled pack initialization. js and wait are
 * the runner's existing executeJavaScript/predicate helpers; seed can be overridden
 * with a host-Node call when an Electron distribution lacks node:sqlite.
 * Does not start services, replace the controller or bypass real HTTP/SSE.
 */
export async function verifyRealHistory({ ownedRoot, js, wait, requests, seed = seedRealHistory, onProgress = () => {} }) {
  const encodedBody = JSON.stringify(HISTORY_BODY);
  const session = await js(`window.__transcriptReal.api.createSession(null,${JSON.stringify(HISTORY_TITLE)})`);
  await js(`window.__transcriptReal.api.postMessage(${JSON.stringify(session.id)},${encodedBody})`);
  const seeded = await seed({ ownedRoot, sessionId: session.id });
  onProgress({ step: 'large_history_seeded', sessionId: seeded.sessionId, count: seeded.count, seed_ms: seeded.seed_ms });
  const sessionId = seeded.sessionId, key = JSON.stringify('user::' + sessionId);
  const interactionCount = () => requests.filter(r => r.method === 'POST' && r.path.endsWith('/interactions')).length;
  const interactionsBefore = interactionCount(), started = performance.now(), pages = [], anchors = [];
  await js(`(()=>{const f=window.__transcriptReal;if(f.__historyOriginal)throw Error('History observer already installed');const original=f.api.listMessagePage;f.__historyOriginal=original;f.historyPageReads=[];f.api.listMessagePage=async(...args)=>{if(args[0]!==${JSON.stringify(sessionId)})return original(...args);const observation={params:args[1],startedAt:performance.now()};f.historyPageReads.push(observation);try{const page=await original(...args);Object.assign(observation,{count:page.items.length,first:page.items[0]?.sequence,last:page.items.at(-1)?.sequence,has_more:page.has_more,revert_epoch:page.revert_epoch,elapsedMs:performance.now()-observation.startedAt});return page;}catch(error){observation.error={name:error?.name,code:error?.code,status:error?.status};throw error;}};})()`);
  try {
    await js('window.__transcriptReal.c.refreshProjectsAndSessions()');
    await js(`window.__transcriptReal.c.setSelectedSession(${key})`);
    await wait(`window.__transcriptReal.c.currentSession.value?.id===${JSON.stringify(sessionId)} && !window.__transcriptReal.c.messagesLoading.value && window.__transcriptReal.c.messages.value.length===100`, 120000);
    const frame = 'await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(()=>requestAnimationFrame(r))))';
    const batchState = `(()=>{const c=window.__transcriptReal.c;return{count:c.messages.value.length,more:c.hasMoreMessages.value,error:c.messagesLoadError.value,items:c.messages.value.slice(0,100).map(m=>({id:m.id,sequence:m.sequence,body:m.content===${encodedBody}}))};})()`;
    function acceptBatch(state, count) {
      assert.equal(state.count, count);
      assert.equal(state.error, null);
      assert.equal(state.more, count < 10000);
      const firstSequence = 10001 - count;
      assert.deepEqual(state.items.map(m => m.id), seeded.ids.slice(firstSequence - 1, firstSequence + 99));
      assert.deepEqual(state.items.map(m => m.sequence), Array.from({ length: 100 }, (_, i) => firstSequence + i));
      assert.ok(state.items.every(m => m.body), 'Incomplete or corrupt persisted message body');
      pages.push({ loaded_count: count, first_sequence: firstSequence, last_sequence: firstSequence + 99 });
    }
    acceptBatch(await js(batchState), 100);
    for (let pageIndex = 1; pageIndex < 100; pageIndex++) {
      // Check a true browser prepend on first and final page (the latter removes the
      // older-history button). A real reader gesture disables streaming follow.
      const checkAnchor = pageIndex === 1 || pageIndex === 99;
      const anchorId = checkAnchor ? seeded.ids[10000 - pageIndex * 100 + 49] : null;
      let before;
      if (checkAnchor) {
        before = await js(`(async()=>{const viewport=document.querySelector('.message-scroller-viewport'),row=document.querySelector('[data-message-id="${anchorId}"]');if(!viewport||!row)throw Error('Actual transcript anchor missing');viewport.dispatchEvent(new WheelEvent('wheel',{deltaY:-1,bubbles:true}));row.scrollIntoView({block:'center',behavior:'instant'});${frame};return{top:row.getBoundingClientRect().top-viewport.getBoundingClientRect().top,scrollTop:viewport.scrollTop};})()`);
      }
      await js('window.__transcriptReal.c.loadOlderMessages()');
      await wait('!window.__transcriptReal.c.loadingOlderMessages.value', 120000);
      await js(`(async()=>{${frame};})()`);
      acceptBatch(await js(batchState), (pageIndex + 1) * 100);
      if ((pageIndex + 1) % 10 === 0) onProgress({ step: 'large_history_page_read', pages: pageIndex + 1, messages: (pageIndex + 1) * 100 });
      if (checkAnchor) {
        const after = await js(`(()=>{const viewport=document.querySelector('.message-scroller-viewport'),row=document.querySelector('[data-message-id="${anchorId}"]');return{top:row.getBoundingClientRect().top-viewport.getBoundingClientRect().top,scrollTop:viewport.scrollTop};})()`);
        const shift = Math.abs(after.top - before.top);
        anchors.push({ page_index: pageIndex, message_id: anchorId, before, after, shift_px: shift });
        assert.ok(shift <= 2, `Actual history prepend moved reader anchor by ${shift}px`);
      }
    }
    const final = await js('window.__transcriptReal.c.messages.value.map(m=>({id:m.id,sequence:m.sequence}))');
    assert.deepEqual(final.map(m => m.id), seeded.ids);
    assert.deepEqual(final.map(m => m.sequence), Array.from({ length: 10000 }, (_, i) => i + 1));
    assert.equal(new Set(final.map(m => m.id)).size, 10000);
    const observations = await js('window.__transcriptReal.historyPageReads');
    assert.ok(observations.length >= 100);
    assert.ok(observations.every(o => o.count <= 100 && o.params.limit === 100 && !o.error));
    assert.equal(observations.filter(o => o.params.before_sequence !== undefined).length, 99);
    assert.equal(interactionCount(), interactionsBefore, 'History reading must not create model interactions');
    assert.deepEqual(await js('window.__transcriptReal.errors'), []);
    assert.equal(await js('document.querySelector("#renderer-error-fallback")!==null'), false);
    const { ids, ...seedReceipt } = seeded;
    return { accepted: true, actual_homecontroller: true, real_core_gateway: true,
      complete_bodies: 10000, exact_ids_and_sequences: true, no_duplicates_or_omissions: true,
      seed_adapter: { direct_owned_sqlite_rows: true, uniqueBodyDedup: true, model_execution: false, per_message_context_snapshots: false },
      first_controller_message_count: 100, maximum_response_bodies: Math.max(...observations.map(o => o.count)),
      physical_core_body_read_limit_evidence: 'Separate MessagePage_TenThousandRowsReadsOnlyLatestHundredBodies Core test; this runner observes payloads',
      older_page_requests: 99, interactions_created: 0, seed: seedReceipt, pages, observations, anchors,
      elapsed_ms: performance.now() - started, checked_at: new Date().toISOString() };
  } finally {
    await js('(()=>{const f=window.__transcriptReal;f.api.listMessagePage=f.__historyOriginal;delete f.__historyOriginal;})()');
  }
}
