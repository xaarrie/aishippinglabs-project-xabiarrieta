import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { normalizeTimelineSearch, searchTimelineFacts, createSearchRequestFence, clarificationEvidence, searchWarnings, sourceEntryHref } from './timelineSearch.js';

const fact = { id: 'f1', entryId: 'e1', entryNumber: 2, claim: '<b>Accepted claim</b>', entityKey: 'Acme', entityType: 'Company', sourceQuote: 'Answer quote', sourceKind: 'Clarification', sourceFingerprint: 'hash', clarificationEvidence: JSON.stringify([{ Question: 'Who?', Answer: 'Answer quote' }]), creationReviewId: 'r1' };
const coverage = { entries: 5, entriesWithoutCompletedExtraction: 2, pendingReviews: 1, acceptedValidFacts: 3, truncated: true, warnings: ['Server warning'] };
const citation = { factId: 'f1', entryId: 'e1', entryNumber: 2, creationReviewId: 'r1' };
const response = () => ({ content: 'Narrative', facts: [fact, { ...fact, id: 'f2' }], citations: [citation], coverage });

test('structured evidence is mandatory, including valid counters and fact provenance', () => {
  for (const value of [{ content: 'legacy' }, { ...response(), coverage: null }, { ...response(), coverage: { ...coverage, entries: -1 } }, { ...response(), facts: [{ ...fact, claim: null }] }]) {
    assert.throws(() => normalizeTimelineSearch(value), /structured fact evidence/i);
  }
});
test('citations must belong to returned facts and agree on source/review; duplicates collapse', () => {
  const result = normalizeTimelineSearch({ ...response(), citations: [citation, citation, { ...citation, factId: 'invented' }, { ...citation, entryId: 'wrong' }, { ...citation, creationReviewId: 'wrong' }] });
  assert.deepEqual(result.citedFacts.map(x => x.id), ['f1']);
  assert.deepEqual(result.additionalFacts.map(x => x.id), ['f2']);
  assert.equal(result.citations.length, 1);
  assert.ok(result.warnings.some(x => x.includes('unverified')));
});
test('no facts suppress model narrative; no citations cannot imply a grounded answer', () => {
  const empty = normalizeTimelineSearch({ ...response(), facts: [], citations: [] });
  assert.equal(empty.content, '');
  assert.equal(empty.emptyMessage, 'No accepted fact matches were returned. No original entry content was searched.');
  assert.equal(normalizeTimelineSearch({ ...response(), coverage: { ...coverage, acceptedValidFacts: 0 }, facts: [], citations: [] }).emptyMessage, 'No accepted valid facts are available. No original entry content was searched.');
  const uncited = normalizeTimelineSearch({ ...response(), citations: [] });
  assert.equal(uncited.content, '');
  assert.equal(uncited.additionalFacts.length, 2);
});
test('coverage always explains bounded matching and exposes server and counter warnings', () => {
  const warnings = searchWarnings(coverage);
  for (const word of ['Server warning', 'extraction', 'pending', 'Truncated', 'not exhaustive']) assert.ok(warnings.some(x => x.includes(word)), word);
});
test('clarification JSON becomes human-readable evidence; malformed values remain plain text', () => {
  assert.deepEqual(clarificationEvidence(fact.clarificationEvidence), ['Question: Who? — Answer: Answer quote']);
  assert.deepEqual(clarificationEvidence('<script>bad</script>'), ['<script>bad</script>']);
  assert.deepEqual(clarificationEvidence('[]'), []);
  assert.equal(sourceEntryHref('p/a', 't', 'e?'), '/projects/p%2Fa/timelines/t/entries/e%3F?projectId=p%2Fa');
});
test('request uses existing endpoint, encoded input and caller abort signal; invalid queries never call API', async () => {
  const calls = []; const signal = new AbortController().signal;
  const api = async (...args) => { calls.push(args); return response(); };
  await searchTimelineFacts(api, 't/1', ' who & when? ', signal);
  assert.equal(calls[0][0], '/api/timelines/t%2F1/entry-search?text=who%20%26%20when%3F');
  assert.equal(calls[0][1].signal, signal);
  for (const query of ['', '  ', 'x'.repeat(301)]) await assert.rejects(searchTimelineFacts(api, 't', query, signal), /1–300/);
  assert.equal(calls.length, 1);
  await assert.rejects(searchTimelineFacts(async () => { throw new Error('offline'); }, 't', 'who', signal), /offline/);
});
test('already cancelled searches do not invoke transport', async () => {
  const controller = new AbortController(); controller.abort();
  let called = false;
  await assert.rejects(searchTimelineFacts(async () => { called = true; return response(); }, 't', 'who', controller.signal), { name: 'AbortError' });
  assert.equal(called, false);
});
test('request fence rejects abort-ignorant late resolutions after replacement, cancellation and unmount', async () => {
  const fence = createSearchRequestFence();
  let resolve; const delayed = new Promise(done => { resolve = done; });
  const first = fence.begin();
  const result = delayed.then(() => fence.isCurrent(first));
  const second = fence.begin();
  assert.equal(first.signal.aborted, true);
  resolve(); assert.equal(await result, false);
  assert.equal(fence.isCurrent(second), true);
  fence.cancel(); assert.equal(fence.isCurrent(second), false);
  const third = fence.begin(); fence.cancel(); // component cleanup / timeline boundary
  assert.equal(fence.isCurrent(third), false);
});
test('actual result rendering escapes evidence and narrative and separates cited/context cards', () => {
  const require = createRequire(import.meta.url);
  const React = require('react');
  const { renderToStaticMarkup } = require('react-dom/server');
  const { transformSync } = require('esbuild'); // Already supplied by Vite; no added dependency.
  const code = transformSync(readFileSync(new URL('./TimelineSearchResults.jsx', import.meta.url), 'utf8'), { loader: 'jsx', format: 'cjs' }).code;
  const module = { exports: {} };
  new Function('require', 'module', 'exports', code)(require, module, module.exports);
  const render = result => renderToStaticMarkup(React.createElement(module.exports.default, { result, projectId: 'p', timelineId: 't', Link: 'a' }));
  const html = render(normalizeTimelineSearch({ ...response(), content: '<script>alert(1)</script>' }));
  assert.ok(html.includes('&lt;script&gt;alert(1)&lt;/script&gt;'));
  assert.ok(html.includes('&lt;b&gt;Accepted claim&lt;/b&gt;'));
  assert.ok(!html.includes('<script>'));
  assert.ok(html.indexOf('Facts cited in answer') < html.indexOf('Additional retrieved context'));
  assert.ok(html.includes('Question: Who? — Answer: Answer quote'));
  assert.ok(html.includes('review answer, not original entry text'));
  assert.ok(html.includes('/projects/p/timelines/t/entries/e1?projectId=p'));
  assert.ok(html.includes('Server warning'));
  const empty = render(normalizeTimelineSearch({ ...response(), facts: [], citations: [] }));
  assert.ok(empty.includes('No accepted fact matches'));
  assert.ok(!empty.includes('Model narrative'));
});
test('UI separates narrative/evidence, uses plain content and existing source routes, preserves action lifecycle', () => {
  const app = readFileSync(new URL('./App.jsx', import.meta.url), 'utf8');
  const search = app.slice(app.indexOf('function TimelineSearch('), app.indexOf('function TimelineEntries('));
  assert.ok(search.includes('searchTimelineFacts('));
  assert.ok(search.includes('useWorkspaceAction()'));
  assert.ok(search.includes('<WorkspaceFeedback'));
  assert.ok(search.includes('fence.isCurrent(controller)'));
  assert.ok(search.includes('maxLength={300}'));
  const ui = readFileSync(new URL('./TimelineSearchResults.jsx', import.meta.url), 'utf8');
  for (const label of ['Model narrative', 'Facts cited in answer', 'Additional retrieved context', 'Clarification evidence', 'Source quote', 'Accepted valid facts']) assert.ok(ui.includes(label), label);
  assert.ok(ui.includes('sourceEntryHref('));
  assert.ok(!ui.includes('dangerouslySetInnerHTML'));
  assert.ok(!ui.includes('ReactMarkdown'));
});
