import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';

// api.js reads browser storage at import time; this fixture never contains a credential.
globalThis.localStorage = { getItem: () => null, setItem() {}, removeItem() {} };
const { api } = await import('./api.js');
const path = '/api/timelines/t/entry-search?text=alice';
const privateText = 'PRIVATE_PROVIDER_SENTINEL';
function renderSearchError(error) {
  const require = createRequire(import.meta.url);
  const React = require('react');
  const { renderToStaticMarkup } = require('react-dom/server');
  const { transformSync } = require('esbuild');
  const app = readFileSync(new URL('./App.jsx', import.meta.url), 'utf8');
  const feedback = app.slice(app.indexOf('function WorkspaceFeedback('), app.indexOf('function Participants('));
  const code = transformSync(`${feedback}\nexport default WorkspaceFeedback;`, { loader: 'jsx', format: 'cjs' }).code;
  const module = { exports: {} };
  new Function('React', 'module', 'exports', code)(React, module, module.exports);
  return renderToStaticMarkup(React.createElement(module.exports.default, { action: { error: error.message } }));
}
async function requestError(t, status, body, malformed = false) {
  t.mock.method(globalThis, 'fetch', async () => ({
    ok: false, status,
    json: async () => { if (malformed) throw new SyntaxError(privateText); return body; }
  }));
  let caught;
  await assert.rejects(api(path), error => { caught = error; return true; });
  assert.equal(caught.status, status);
  assert.ok(!JSON.stringify(caught).includes(privateText));
  assert.ok(!caught.message.includes(privateText));
  assert.ok(!renderSearchError(caught).includes(privateText));
  assert.equal(caught.cause, undefined);
  assert.equal(caught.response, undefined);
  return caught;
}

for (const [status, code, message] of [
  [400, 'InvalidQuestion', /question.*1–300/i],
  [409, 'EvidenceChanged', /evidence changed.*refresh.*search again/i],
  [409, 'StorageUnavailable', /storage.*unavailable.*administrator/i],
  [504, 'ResearchDeadlineExceeded', /timed out.*narrower question/i],
  [499, 'RequestCanceled', /canceled.*search again/i],
  [502, 'InvalidResearchAnswer', /verified.*answer.*unavailable.*retry/i]
]) test(`research HTTP ${status}/${code} reconstructs actionable safe text`, async t => {
  const error = await requestError(t, status, {
    code, error: privateText, detail: privateText, title: privateText,
    message: privateText, stage: privateText, category: privateText, traceId: privateText,
    stack: privateText, providerResponse: { body: privateText }
  });
  assert.match(error.message, message);
  assert.ok(error.message.includes(`HTTP ${status}`));
  assert.ok(error.message.includes(code));
  const html = renderSearchError(error);
  assert.match(html, message);
  assert.ok(html.includes(`HTTP ${status}`));
  assert.ok(html.includes(code));
  assert.deepEqual(error.problem, { code });
});

test('unknown or mismatched research errors retain generic fallback without raw bodies', async t => {
  for (const [status, body, malformed] of [
    [502, { code: 'UnknownResearchFailure', error: privateText }],
    [502, { code: 'EvidenceChanged', detail: privateText }],
    [409, { code: 'InvalidResearchAnswer' }],
    [504, { code: privateText }],
    [502, { code: { message: privateText } }],
    [502, { code: `<script>${privateText}</script>` }],
    [403, null], [404, {}], [500, privateText], [500, []], [502, null], [502, {}, true]
  ]) {
    const error = await requestError(t, status, body, malformed);
    assert.equal(error.message, `Request failed (${status})`);
    assert.equal(error.problem, undefined);
    const html = renderSearchError(error);
    assert.ok(html.includes(`Request failed (${status})`));
    if (typeof body?.code === 'string') assert.ok(!html.includes(body.code));
    assert.ok(!html.includes('<script>'));
  }
});

test('existing entry review diagnostics remain allowlisted and arbitrary fields are dropped', async t => {
  const error = await requestError(t, 502, {
    code: 'entry_review_failed', stage: 'agent_invocation', category: 'provider_timeout',
    traceId: 'safe-reference-1', error: privateText
  });
  assert.deepEqual(error.problem, {
    code: 'entry_review_failed', stage: 'agent_invocation', category: 'provider_timeout', traceId: 'safe-reference-1'
  });
  assert.match(error.message, /Entry review could not be completed/);
  const unknown = await requestError(t, 502, {
    code: 'entry_review_audit_failed', stage: privateText, category: privateText, traceId: '<private>', error: privateText
  });
  assert.deepEqual(unknown.problem, { code: 'entry_review_audit_failed' });
  assert.match(unknown.message, /Entry audit could not be completed/);
  const changed = await requestError(t, 409, { code: 'entry_review_source_changed', error: privateText });
  assert.match(changed.message, /source changed/i);
});

test('successful JSON and empty responses are unchanged', async t => {
  t.mock.method(globalThis, 'fetch', async () => ({ ok: true, status: 200, json: async () => ({ facts: [] }) }));
  assert.deepEqual(await api(path), { facts: [] });
  t.mock.method(globalThis, 'fetch', async () => ({ ok: true, status: 204 }));
  assert.equal(await api(path), null);
});
