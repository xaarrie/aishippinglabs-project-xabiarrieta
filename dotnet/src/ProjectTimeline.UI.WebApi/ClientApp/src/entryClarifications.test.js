import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import * as helpers from './entryClarifications.js';
import { normalizeClarifications, createClarificationClient, createClarificationSession } from './entryClarifications.js';

const entryId = '11111111-1111-1111-1111-111111111111';
const generationId = '22222222-2222-2222-2222-222222222222';
const id = '33333333-3333-3333-3333-333333333333';
const other = '44444444-4444-4444-4444-444444444444';
const time = '2026-10-25T12:00:00Z';
const row = () => ({ id, generationId, questionKey: 'key', question: 'Which team?', answer: null, answeredById: null, answeredAtUtc: null, createdAtUtc: time, binding: 'CurrentQuestion', canAnswer: true });
const state = () => ({ entryId, generationId, lifecycleState: 'AwaitingClarification', current: [row()], history: [], truncated: false, canAnswer: true, canResume: false, refreshRequired: false });
const answered = () => ({ ...row(), answer: 'Actual human text', answeredById: other, answeredAtUtc: time, canAnswer: false });
const ready = () => ({ ...state(), current: [answered()], canResume: true });
const factState = () => ({ source: { entryId, timelineId: other, title: 'Saved source', content: 'Unchanged', status: 'Draft', fingerprint: 'a'.repeat(64) },
  generationId: other, lifecycleState: 'Pending', canEdit: true, canGenerate: false, canRegenerate: true, refreshRequired: false });

test('normalization rejects missing, corrupt, foreign and non-boolean readiness/provenance', () => {
  assert.deepEqual(normalizeClarifications(state(), entryId), state());
  for (const bad of [null, {}, { ...state(), entryId: other }, { ...state(), generationId: null },
    { ...state(), generationId: 'wrong' }, { ...state(), canAnswer: 'true' }, { ...state(), canResume: 1 },
    { ...state(), refreshRequired: undefined }, { ...state(), truncated: null }, { ...state(), current: null },
    { ...state(), current: [{ ...row(), generationId: other }] }, { ...state(), current: [{ ...answered(), answeredById: null }] },
    { ...state(), current: [{ ...row(), canAnswer: 'true' }] }, { ...state(), current: [{ ...row(), binding: 'History' }] },
    { ...state(), canResume: true }, { ...state(), history: [row()] }, { ...state(), current: [{ ...row(), binding: 'CarriedAnswer' }] }]) {
    assert.equal(normalizeClarifications(bad, entryId), null, JSON.stringify(bad));
  }
  assert.ok(normalizeClarifications({ ...state(), generationId: null, current: [], canAnswer: false }, entryId));
});

test('binding, not originating generation equality, distinguishes carried answers and read-only history', () => {
  const carried = { ...answered(), generationId: other, binding: 'CarriedAnswer' };
  assert.ok(normalizeClarifications({ ...ready(), current: [carried] }, entryId));
  assert.ok(normalizeClarifications({ ...state(), current: [], history: [{ ...row(), binding: 'History', canAnswer: false }], canAnswer: false }, entryId));
  assert.equal(normalizeClarifications({ ...state(), history: [{ ...row(), binding: 'History' }] }, entryId), null);
});

test('exact API paths and allowlisted bodies exclude client provenance', async () => {
  const calls = [], signal = new AbortController().signal;
  const client = createClarificationClient(async (...args) => { calls.push(args); });
  await client.read('entry /1', { signal });
  await client.answer('entry /1', 'question /1', generationId, 'My answer', { signal });
  await client.resume('entry /1', generationId, { signal });
  assert.deepEqual(calls.map(x => x[0]), ['/api/entries/entry%20%2F1/fact-clarifications', '/api/entries/entry%20%2F1/fact-clarifications/question%20%2F1/answer', '/api/entries/entry%20%2F1/fact-clarifications/resume']);
  assert.deepEqual(JSON.parse(calls[1][1].body), { expectedGenerationId: generationId, answer: 'My answer' });
  assert.deepEqual(JSON.parse(calls[2][1].body), { expectedGenerationId: generationId });
  assert.equal(calls[0][1].method, undefined);
  for (const [, options] of calls) assert.equal(options.signal, signal);
  for (const [, options] of calls.slice(1)) assert.equal(options.method, 'POST');
});

function setup(initial = state(), permission = true) {
  const calls = []; let server = initial; let failure = null; let resumed = 0;
  const client = {
    async read() { calls.push('read'); return server; },
    async answer(e, q, g, text) { calls.push(['answer', e, q, g, text]); if (failure) throw failure; return server = { ...ready(), current: [{ ...answered(), answer: text }] }; },
    async resume() { calls.push('resume'); if (failure) throw failure; server = { ...ready(), generationId: other, lifecycleState: 'Completed', current: [{ ...answered(), binding: 'CarriedAnswer' }], canAnswer: false, canResume: false }; return factState(); }
  };
  const session = createClarificationSession(client, { onResume: () => resumed++ });
  session.select(entryId); session.permissions(permission);
  return { session, calls, fail: value => failure = value, server: value => server = value, resumed: () => resumed };
}

test('partial answer save is zero resume calls; only explicit resume refreshes both views', async () => {
  const x = setup(); await x.session.refresh(); x.session.draft(id, 'Real answer');
  await x.session.save(id);
  assert.deepEqual(x.calls, ['read', ['answer', entryId, id, generationId, 'Real answer']]);
  assert.equal(x.resumed(), 0);
  await x.session.resume();
  assert.deepEqual(x.calls.slice(-2), ['resume', 'read']); assert.equal(x.resumed(), 1);
  assert.equal(x.session.snapshot().state.current[0].binding, 'CarriedAnswer');
});

test('saving one of two questions never resumes partial answers, even after refresh', async () => {
  const second = { ...row(), id: other, questionKey: 'second' };
  const partial = { ...state(), current: [{ ...answered(), answer: 'First answer' }, second] };
  let writes = 0, resumes = 0;
  const session = createClarificationSession({ read: async () => writes ? partial : { ...state(), current: [row(), second] },
    answer: async () => { writes++; return partial; }, resume: async () => resumes++ });
  session.select(entryId); session.permissions(true); await session.refresh(); session.draft(id, 'First answer');
  await session.save(id); await session.refresh(); await session.resume();
  assert.equal(writes, 1); assert.equal(resumes, 0); assert.equal(session.snapshot().state.current[1].answer, null);
});

test('read-only, answered/history rows, blocked readiness and invalid drafts never write', async () => {
  for (const [initial, permission] of [[state(), false], [ready(), true], [{ ...state(), refreshRequired: true }, true], [{ ...state(), canAnswer: false }, true]]) {
    const x = setup(initial, permission); await x.session.refresh(); x.session.draft(id, 'Attempt'); await x.session.save(id);
    assert.deepEqual(x.calls, ['read']);
  }
  for (const text of ['', ' ', 'a'.repeat(4001)]) {
    const x = setup(); await x.session.refresh(); x.session.draft(id, text); await x.session.save(id); await x.session.resume(); assert.deepEqual(x.calls, ['read']);
  }
  const x = setup(ready(), false); await x.session.refresh(); await x.session.resume(); assert.deepEqual(x.calls, ['read']);
});

test('409 and network errors retain drafts, refresh read-only and never replay writes', async () => {
  for (const error of [Object.assign(new Error(), { status: 409 }), new Error('network')]) {
    const x = setup(); await x.session.refresh(); x.session.draft(id, 'Keep me'); x.fail(error); await x.session.save(id);
    assert.equal(x.session.snapshot().drafts[id], 'Keep me'); assert.equal(x.session.snapshot().conflict, true);
    assert.ok(x.session.snapshot().error); assert.deepEqual(x.calls.slice(-1), ['read']);
    await x.session.save(id); assert.equal(x.calls.filter(Array.isArray).length, 1);
    x.fail(null); await x.session.refresh(true); await x.session.save(id); assert.equal(x.calls.filter(Array.isArray).length, 2);
  }
});

test('stale async reads and writes cannot overwrite another entry or trigger its callbacks', async () => {
  for (const operation of ['read', 'save', 'resume']) {
    let release; let resumes = 0;
    const deferred = new Promise(resolve => release = resolve);
    let block = false;
    const session = createClarificationSession({ read: () => block ? deferred : Promise.resolve(operation === 'resume' ? ready() : state()), answer: () => deferred, resume: () => deferred }, { onResume: () => resumes++ });
    session.select(entryId); session.permissions(true); await session.refresh(); session.draft(id, 'Old draft');
    block = true;
    const pending = operation === 'read' ? session.refresh() : operation === 'save' ? session.save(id) : session.resume();
    session.select(other); release(state()); await pending;
    assert.equal(session.snapshot().entryId, other); assert.equal(session.snapshot().state, null); assert.deepEqual(session.snapshot().drafts, {}); assert.equal(resumes, 0);
  }
});

test('failed resume refreshes both views once without replay; duplicate clicks serialize', async () => {
  const x = setup(ready()); await x.session.refresh(); x.session.draft(id, 'Retained inspection draft');
  x.fail(Object.assign(new Error(), { status: 409 })); await Promise.all([x.session.resume(), x.session.resume()]);
  assert.equal(x.resumed(), 1); assert.equal(x.calls.filter(x => x === 'resume').length, 1);
  assert.equal(x.session.snapshot().drafts[id], 'Retained inspection draft'); assert.equal(x.session.snapshot().conflict, true);
  assert.equal(x.calls.at(-1), 'read');
});

test('unmount rejects late responses and read failure does not erase drafts', async () => {
  let release;
  const pending = new Promise(resolve => release = resolve);
  let failRead = false;
  const session = createClarificationSession({ read: () => { if (failRead) throw new Error('offline'); return pending; } });
  session.select(entryId); session.draft(id, 'Keep');
  const request = session.refresh(); session.dispose(); release(state()); await request;
  assert.equal(session.snapshot().state, null);
  failRead = true; await session.refresh(); assert.equal(session.snapshot().drafts[id], 'Keep'); assert.ok(session.snapshot().error);
});

test('actual component escapes human text, retains history and hides writer controls for readers', () => {
  const require = createRequire(import.meta.url);
  const React = require('react');
  const { renderToStaticMarkup } = require('react-dom/server');
  const { transformSync } = require('esbuild');
  const question = '<img src=x onerror=alert(1)>';
  const loaded = { ...ready(), current: [{ ...answered(), question, answer: '<script>human</script>' }],
    history: [{ ...answered(), id: other, binding: 'History', question: 'Old question' }], truncated: true };
  const view = { entryId, state: loaded, drafts: {}, loading: false, busy: false, conflict: false, error: '', message: '' };
  const code = transformSync(readFileSync(new URL('./EntryClarifications.jsx', import.meta.url), 'utf8'), { loader: 'jsx', format: 'cjs' }).code;
  const module = { exports: {} };
  const fakeRequire = name => name === './api.js' ? { api: () => { throw new Error('No real requests'); } }
    : name === './entryClarifications.js' ? { ...helpers, createClarificationSession: () => ({ permissions() {}, snapshot: () => view }) } : require(name);
  new Function('require', 'module', 'exports', code)(fakeRequire, module, module.exports);
  const render = canWrite => renderToStaticMarkup(React.createElement(module.exports.default, { entryId, canWrite }));
  const reader = render(false);
  assert.ok(reader.includes('&lt;img')); assert.ok(reader.includes('&lt;script&gt;human')); assert.ok(!reader.includes('<script>'));
  assert.ok(reader.includes('Old question')); assert.ok(reader.includes('History is truncated'));
  assert.ok(!reader.includes('<textarea')); assert.ok(!reader.includes('>Save answer<')); assert.ok(!reader.includes('>Extract using answers (model run)<'));
  const writer = render(true); assert.ok(writer.includes('Extract using answers (model run)')); assert.ok(!writer.includes('<textarea'));
  loaded.current = [{ ...row(), question }]; loaded.canResume = false;
  const unanswered = render(true); assert.match(unanswered, /maxlength="4000"/i); assert.ok(unanswered.includes(`id="clarification-answer-${id}"`));
  assert.ok(unanswered.includes('aria-label="Save answer: &lt;img'));
});

test('entry integration remains separate and fact refresh does not remount review drafts', () => {
  const app = readFileSync(new URL('./App.jsx', import.meta.url), 'utf8');
  const detail = app.slice(app.indexOf('function EntryDetail('), app.indexOf('function EntryCreate('));
  assert.ok(detail.indexOf('<EntryClarifications') < detail.indexOf('<FactReview'));
  assert.ok(detail.includes('refreshKey={factRefreshKey}'));
  const review = readFileSync(new URL('./FactReview.jsx', import.meta.url), 'utf8');
  assert.ok(review.includes('AwaitingClarification:')); assert.ok(review.includes('if (refreshKey) refresh();'));
  const component = readFileSync(new URL('./EntryClarifications.jsx', import.meta.url), 'utf8');
  assert.ok(!component.includes('dangerouslySetInnerHTML')); assert.ok(!component.includes('setInterval'));
});

test('corrupt resume responses refresh both views but never claim successful extraction', async () => {
  for (const response of [null, {}, { ...factState(), generationId }, { ...factState(), source: { ...factState().source, entryId: other } }]) {
    let resumed = 0;
    const session = createClarificationSession({ read: async () => ready(), resume: async () => response }, { onResume: () => resumed++ });
    session.select(entryId); session.permissions(true); await session.refresh(); await session.resume();
    assert.equal(resumed, 1); assert.equal(session.snapshot().conflict, true); assert.ok(session.snapshot().error); assert.equal(session.snapshot().message, '');
  }
});

test('unconfirmed save responses retain drafts and do not imply answer persistence', async () => {
  for (const response of [state(), { ...ready(), current: [] }, ready(), { ...ready(), generationId: other, current: [{ ...answered(), binding: 'CarriedAnswer' }] }]) {
    const session = createClarificationSession({ read: async () => state(), answer: async () => response });
    session.select(entryId); session.permissions(true); await session.refresh(); session.draft(id, 'Exact saved text'); await session.save(id);
    assert.equal(session.snapshot().drafts[id], 'Exact saved text'); assert.equal(session.snapshot().conflict, true);
  }
});

test('corrupt success and failed reads fail closed without dropping unsaved drafts', async () => {
  const x = setup(); await x.session.refresh(); x.session.draft(id, 'Retained'); x.server({}); await x.session.refresh();
  assert.equal(x.session.snapshot().state, null); assert.ok(x.session.snapshot().error); assert.equal(x.session.snapshot().drafts[id], 'Retained');
  await x.session.save(id); await x.session.resume(); assert.deepEqual(x.calls, ['read', 'read']);
});
