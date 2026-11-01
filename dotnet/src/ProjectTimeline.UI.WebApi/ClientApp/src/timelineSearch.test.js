import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { normalizeTimelineSearch, searchTimelineFacts, createSearchRequestFence, clarificationEvidence, searchWarnings, sourceEntryHref } from './timelineSearch.js';

const fact = { id: 'f1', entryId: 'e1', entryNumber: 2, claim: '<b>Accepted claim</b>', entityKey: 'Acme', entityType: 'Company', sourceQuote: 'Answer quote', sourceKind: 'Clarification', sourceFingerprint: 'hash', clarificationEvidence: JSON.stringify([{ Question: 'Who?', Answer: 'Answer quote' }]), creationReviewId: 'r1' };
const coverage = { entries: 5, entriesWithoutCompletedExtraction: 2, pendingReviews: 1, acceptedValidFacts: 3, truncated: true, warnings: ['Server warning'] };
const citation = { factId: 'f1', entryId: 'e1', entryNumber: 2, creationReviewId: 'r1' };
const response = () => ({ content: 'Narrative', facts: [fact, { ...fact, id: 'f2' }], citations: [citation], coverage });
const directFact = { ...fact, id: 'direct', sourceKind: 'Entry', clarificationEvidence: '', creationReviewId: null, extractionJobId: 'job1' };
const directCitation = { factId: directFact.id, entryId: directFact.entryId, entryNumber: directFact.entryNumber, creationReviewId: null, extractionJobId: 'job1' };
const directResponse = () => ({ ...response(), facts: [directFact], citations: [directCitation] });

// Small fixture overlays merge research metadata; arrays replace rather than concatenate.
const fixtures = JSON.parse(readFileSync(new URL('../tests/timelineFactResearch.fixtures.json', import.meta.url), 'utf8'));
const researchResponse = (name = 'mixed') => {
  const base = structuredClone(fixtures.mixed);
  const patch = structuredClone(fixtures.cases[name] ?? {});
  return { ...base, ...patch, research: { ...base.research, ...patch.research } };
};
function renderResult(result) {
  const require = createRequire(import.meta.url);
  const React = require('react');
  const { renderToStaticMarkup } = require('react-dom/server');
  const { transformSync } = require('esbuild');
  const code = transformSync(readFileSync(new URL('./TimelineSearchResults.jsx', import.meta.url), 'utf8'), { loader: 'jsx', format: 'cjs' }).code;
  const module = { exports: {} };
  new Function('require', 'module', 'exports', code)(require, module, module.exports);
  return renderToStaticMarkup(React.createElement(module.exports.default, { result, projectId: 'p', timelineId: 't', Link: 'a' }));
}

test('structured evidence is mandatory, including valid counters and fact provenance', () => {
  for (const value of [{ content: 'legacy' }, { ...response(), coverage: null }, { ...response(), coverage: { ...coverage, entries: -1 } }, { ...response(), facts: [{ ...fact, claim: null }] }]) {
    assert.throws(() => normalizeTimelineSearch(value), /structured fact evidence/i);
  }
});
test('direct extraction and legacy review evidence both retain grounded answers', () => {
  for (const value of [directResponse(), response(), {
    ...response(), facts: [directFact, fact], citations: [directCitation, citation]
  }, {
    ...response(), facts: [{ ...fact, extractionJobId: null }], citations: [citation]
  }, {
    ...directResponse(), facts: [{ ...directFact, creationReviewId: undefined }]
  }]) {
    const result = normalizeTimelineSearch(value);
    assert.equal(result.content, 'Narrative');
    assert.deepEqual(result.citations, value.citations);
    assert.equal(result.citedFacts.length, value.citations.length);
  }
});
test('facts require exactly one meaningful provenance origin and direct extraction uses entry evidence', () => {
  for (const origin of [
    {}, { creationReviewId: null, extractionJobId: null },
    { creationReviewId: 'r1', extractionJobId: 'job1' },
    ...['', '  ', 12, {}, []].flatMap(id => [
      { creationReviewId: id }, { creationReviewId: null, extractionJobId: id },
      { creationReviewId: 'r1', extractionJobId: id }, { creationReviewId: id, extractionJobId: 'job1' }
    ])
  ]) {
    const value = { ...directFact, creationReviewId: undefined, extractionJobId: undefined, ...origin };
    assert.throws(() => normalizeTimelineSearch({ ...directResponse(), facts: [value] }), /structured fact evidence/i);
  }
  for (const change of [{ sourceKind: 'Clarification' }, { clarificationEvidence: 'review answer' }]) {
    assert.throws(() => normalizeTimelineSearch({ ...directResponse(), facts: [{ ...directFact, ...change }] }), /structured fact evidence/i);
  }
});
test('direct citations must match extraction origin as well as fact identity and source', () => {
  for (const change of [
    { extractionJobId: undefined }, { extractionJobId: null }, { extractionJobId: '' },
    { extractionJobId: '  ' }, { extractionJobId: 12 }, { extractionJobId: {} }, { extractionJobId: [] },
    { extractionJobId: 'wrong' }, { creationReviewId: 'r1' }, { creationReviewId: '' },
    { factId: 'invented' }, { entryId: 'wrong' }, { entryNumber: directFact.entryNumber + 1 }
  ]) {
    const result = normalizeTimelineSearch({ ...directResponse(), citations: [{ ...directCitation, ...change }] });
    assert.equal(result.content, '');
    assert.equal(result.citations.length, 0);
    assert.deepEqual(result.additionalFacts, [directFact]);
    assert.ok(result.warnings.some(x => x.includes('unverified')));
  }
  const duplicate = normalizeTimelineSearch({ ...directResponse(), citations: [directCitation, directCitation] });
  assert.equal(duplicate.citations.length, 1);
  assert.equal(duplicate.content, 'Narrative');
  assert.throws(() => normalizeTimelineSearch({ ...directResponse(), facts: [directFact, directFact] }), /structured fact evidence/i);
});
test('review-only citations cannot introduce extraction provenance', () => {
  for (const extractionJobId of ['job1', '', '  ', 12, {}, []]) {
    const result = normalizeTimelineSearch({ ...response(), citations: [{ ...citation, extractionJobId }] });
    assert.equal(result.content, '');
    assert.equal(result.citations.length, 0);
  }
});
test('citations must belong to returned facts and agree on source/review; duplicates collapse', () => {
  const result = normalizeTimelineSearch({ ...response(), citations: [citation, citation, { ...citation, factId: 'invented' }, { ...citation, entryId: 'wrong' }, { ...citation, creationReviewId: 'wrong' }] });
  assert.equal(result.content, '');
  assert.deepEqual(result.citedFacts, []);
  assert.deepEqual(result.additionalFacts.map(x => x.id), ['f1', 'f2']);
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
test('result shows summary, only cited claims and all metrics, not retrieved bodies or provenance', () => {
  const value = researchResponse();
  value.content = '<script>summary</script>';
  Object.assign(value.facts[0], { claim: '<b>Cited claim</b>', sourceKind: 'Clarification',
    sourceQuote: 'HIDDEN_REVIEW_QUOTE', clarificationEvidence: 'HIDDEN_REVIEW_TRANSCRIPT' });
  const extra = { ...directFact, entryId: 'uncited-entry', claim: 'HIDDEN_UNCITED_CLAIM' };
  value.facts.push(extra);
  value.sourceContexts.push({ ...value.sourceContexts[0],
    evidenceId: '55555555-5555-4555-8555-555555555555', entryId: extra.entryId,
    entryNumber: extra.entryNumber, anchorFactIds: [extra.id], sourceFingerprint: extra.sourceFingerprint,
    text: 'HIDDEN_UNCITED_ENTRY_BODY' });
  value.coverage = { ...coverage };
  value.research.warnings = ['Research warning'];
  const result = normalizeTimelineSearch(value);
  const html = renderResult(result);
  assert.deepEqual(result.facts, value.facts);
  assert.deepEqual(result.sourceContexts, value.sourceContexts);
  assert.deepEqual(result.citedFacts, [value.facts[0]]);
  assert.deepEqual(result.additionalFacts, [extra]);
  assert.equal(result.citedSourceContexts.length, 1);
  assert.equal(result.additionalSourceContexts.length, 1);
  for (const visible of ['Agent summary', '&lt;script&gt;summary&lt;/script&gt;', 'Facts cited in answer',
    '&lt;b&gt;Cited claim&lt;/b&gt;', 'Sample team', 'Team', 'Clarification (review answer, not original entry text)',
    'Search coverage', 'Entries', 'Entries without completed extraction', 'Pending reviews', 'Accepted valid facts',
    'Retrieved facts', 'Truncated', 'Research outcome', 'Status: Complete', 'Stop reason: None',
    'Model calls', '4 / 6', 'Tool attempts', '3 / 8', 'Search attempts', '1 / 4',
    'Budget exhausted', 'Evidence truncated', 'Server warning', 'Research warning']) assert.ok(html.includes(visible), visible);
  for (const hidden of ['HIDDEN_', 'Additional retrieved context', 'Source-entry context cited in answer',
    'Additional source-entry context', 'Source quote', 'Clarification evidence', 'Source entry',
    'Fact ID:', 'Creation review:', 'Extraction job:', 'Evidence ID:', 'Source fingerprint:',
    ...value.facts.flatMap(x => [x.id, x.entryId, x.sourceFingerprint, x.creationReviewId ?? x.extractionJobId]),
    ...value.sourceContexts.flatMap(x => [x.evidenceId, x.text])]) assert.ok(!html.includes(hidden), hidden);
  assert.ok(!html.includes('<script>'));
  assert.ok(!html.includes('Generated synthesis is separate'));
});
test('direct claims stay escaped while unsafe quotes and job identifiers remain normalized but hidden', () => {
  const value = { ...directResponse(), content: '<script>direct</script>',
    facts: [{ ...directFact, extractionJobId: '<job1>', sourceQuote: '<img src=x onerror=bad>' }],
    citations: [{ ...directCitation, extractionJobId: '<job1>' }] };
  const result = normalizeTimelineSearch(value);
  assert.deepEqual(result.facts, value.facts);
  assert.deepEqual(result.citations, value.citations);
  const html = renderResult(result);
  for (const visible of ['Entry (original source text)', '&lt;script&gt;direct&lt;/script&gt;',
    '&lt;b&gt;Accepted claim&lt;/b&gt;']) assert.ok(html.includes(visible), visible);
  for (const hidden of ['<script>', '<img', '&lt;img', '&lt;job1&gt;', 'Creation review:',
    'Extraction job:', 'Clarification evidence', 'review answer, not original entry text']) assert.ok(!html.includes(hidden), hidden);
  const empty = renderResult(normalizeTimelineSearch({ ...response(), facts: [], citations: [] }));
  assert.ok(empty.includes('No accepted fact matches'));
  assert.ok(!empty.includes('Agent summary'));
});
test('new DTO supports mixed and context-only citations without promoting anchors into cited facts', () => {
  for (const name of ['mixed', 'contextOnly']) {
    const value = researchResponse(name);
    const result = normalizeTimelineSearch(value);
    assert.equal(result.content, value.content);
    assert.deepEqual(result.sourceContexts, value.sourceContexts);
    assert.deepEqual(result.contextCitations, value.contextCitations);
    assert.deepEqual(result.citedSourceContexts, value.sourceContexts);
    assert.deepEqual(result.additionalSourceContexts, []);
    assert.equal(result.citedFacts.length, name === 'mixed' ? 1 : 0);
    assert.equal(result.additionalFacts.length, name === 'mixed' ? 0 : 1);
    const html = renderResult(result);
    assert.ok(html.includes(value.content));
    assert.ok(html.includes('Agent summary'));
    assert.equal(html.includes('Facts cited in answer'), name === 'mixed');
    assert.equal(html.includes(value.facts[0].claim), name === 'mixed');
    for (const hidden of ['Source-entry context cited in answer', 'Additional source-entry context',
      'Additional retrieved context', 'Anchor fact IDs:', value.facts[0].id, value.sourceContexts[0].text,
      sourceEntryHref('p', 't', value.facts[0].entryId)]) assert.ok(!html.includes(hidden), hidden);
  }
});
test('retrieved but uncited context cannot ground a narrative', () => {
  const value = researchResponse('contextOnly'); value.contextCitations = [];
  const result = normalizeTimelineSearch(value);
  assert.equal(result.content, '');
  assert.deepEqual(result.citedSourceContexts, []);
  assert.deepEqual(result.additionalSourceContexts, value.sourceContexts);
  const html = renderResult(result);
  assert.ok(!html.includes(value.content));
  assert.deepEqual(result.facts, value.facts);
  for (const hidden of ['Agent summary', 'Facts cited in answer', 'Additional source-entry context',
    value.sourceContexts[0].text, value.facts[0].claim]) assert.ok(!html.includes(hidden), hidden);
  assert.ok(result.warnings.some(x => x.includes('No verified answer citations')));
});
test('context evidence rejects malformed, duplicate, orphaned and contradictory anchors', () => {
  const invalid = [
    null, {}, { evidenceId: 'entry-id-is-not-evidence' }, { entryId: 'unknown' }, { entryNumber: 8 },
    { entryNumber: 0 }, { sourceFingerprint: 'stale' }, { evidenceKind: 'Entry' },
    { text: null }, { text: '' }, { truncated: 'false' }, { anchorFactIds: null },
    { anchorFactIds: [] }, { anchorFactIds: ['unknown'] }, { anchorFactIds: [null] }
  ];
  for (const change of invalid) {
    const value = researchResponse();
    value.sourceContexts = [change === null ? null : Object.keys(change).length ? { ...value.sourceContexts[0], ...change } : {}];
    assert.throws(() => normalizeTimelineSearch(value), /structured fact evidence/i, JSON.stringify(change));
  }
  for (const field of ['sourceContexts', 'contextCitations']) {
    for (const bad of [null, {}, 'context']) {
      assert.throws(() => normalizeTimelineSearch({ ...researchResponse(), [field]: bad }), /structured fact evidence/i);
    }
  }
  for (const evidenceId of [researchResponse().sourceContexts[0].evidenceId, '55555555-5555-4555-8555-555555555555']) {
    const duplicate = researchResponse(); duplicate.sourceContexts.push({ ...duplicate.sourceContexts[0], evidenceId });
    assert.throws(() => normalizeTimelineSearch(duplicate), /structured fact evidence/i);
  }
  for (const key of Object.keys(researchResponse().sourceContexts[0])) {
    const missing = researchResponse(); delete missing.sourceContexts[0][key];
    assert.throws(() => normalizeTimelineSearch(missing), /structured fact evidence/i, key);
  }
  const anchors = researchResponse(); anchors.sourceContexts[0].anchorFactIds.push(anchors.facts[0].id);
  assert.throws(() => normalizeTimelineSearch(anchors), /structured fact evidence/i);
  for (const change of [{ entryId: 'foreign' }, { entryNumber: 99 }, { sourceFingerprint: 'changed' }]) {
    const value = researchResponse(); value.facts[0] = { ...value.facts[0], ...change };
    assert.throws(() => normalizeTimelineSearch(value), /structured fact evidence/i);
  }
  const orphan = researchResponse(); orphan.facts = []; orphan.citations = [];
  assert.throws(() => normalizeTimelineSearch(orphan), /structured fact evidence/i);
});
test('context citations require exact membership and provenance; tampering suppresses even mixed narrative', () => {
  const changes = [null, {}, { evidenceId: '55555555-5555-4555-8555-555555555555' },
    { entryId: 'foreign' }, { entryNumber: 99 }, { sourceFingerprint: 'wrong' },
    { evidenceKind: 'FactProof' }, { anchorFactIds: [] }, { anchorFactIds: ['unknown'] },
    { anchorFactIds: null }, { anchorFactIds: ['11111111-1111-4111-8111-111111111111', 'unknown'] }];
  for (const name of ['mixed', 'contextOnly']) for (const change of changes) {
    const value = researchResponse(name);
    value.contextCitations = [change === null ? null : change && Object.keys(change).length
      ? { ...value.contextCitations[0], ...change } : {}];
    const result = normalizeTimelineSearch(value);
    assert.equal(result.content, '', `${name}: ${JSON.stringify(change)}`);
    assert.equal(result.contextCitations.length, 0);
    assert.ok(result.warnings.some(x => x.includes('unverified')));
    assert.ok(!renderResult(result).includes(value.content));
  }
  for (const duplicateKind of ['citation', 'anchor']) {
    const value = researchResponse('contextOnly');
    if (duplicateKind === 'citation') value.contextCitations.push(structuredClone(value.contextCitations[0]));
    else value.contextCitations[0].anchorFactIds.push(value.facts[0].id);
    assert.equal(normalizeTimelineSearch(value).content, '');
  }
  for (const key of Object.keys(researchResponse().contextCitations[0])) {
    const missing = researchResponse('contextOnly'); delete missing.contextCitations[0][key];
    assert.equal(normalizeTimelineSearch(missing).content, '', key);
  }
  const unseen = normalizeTimelineSearch(researchResponse('malformed'));
  assert.equal(unseen.content, '');
  const rawEntry = researchResponse('contextOnly'); rawEntry.contextCitations[0].evidenceId = rawEntry.facts[0].entryId;
  assert.equal(normalizeTimelineSearch(rawEntry).content, '');
});
test('a character-truncated whitespace prefix remains a valid server context', () => {
  const value = researchResponse('contextOnly');
  // The server slices title + newline + body by characters, not trimmed text.
  value.sourceContexts[0].text = '\n'; value.sourceContexts[0].truncated = true;
  Object.assign(value.research, { status: 'Partial', stopReason: 'EvidenceTruncated', evidenceTruncated: true });
  const result = normalizeTimelineSearch(value);
  assert.equal(result.content, value.content);
  assert.deepEqual(result.sourceContexts, value.sourceContexts);
  const html = renderResult(result);
  assert.ok(html.includes('Evidence truncated</dt><dd>Yes'));
  assert.ok(!html.includes('Truncated source-entry text'));
});
test('multiple anchors are set-matched and each must match source entry and fingerprint', () => {
  const value = researchResponse('contextOnly');
  const second = { ...value.facts[0], id: '66666666-6666-4666-8666-666666666666' };
  value.facts.push(second); value.sourceContexts[0].anchorFactIds.push(second.id);
  value.contextCitations[0].anchorFactIds.unshift(second.id);
  assert.equal(normalizeTimelineSearch(value).content, value.content);
  value.facts[1].sourceFingerprint = 'other';
  assert.throws(() => normalizeTimelineSearch(value), /structured fact evidence/i);
});
test('research metadata exposes partial evidence and attempt counts without speculative cross-counter caps', () => {
  const value = researchResponse('partial');
  const result = normalizeTimelineSearch(value);
  assert.equal(result.content, value.content);
  assert.deepEqual(result.research, value.research);
  assert.ok(result.warnings.includes(value.research.warnings[0]));
  const html = renderResult(result);
  for (const label of ['Partial', 'ToolOrEvidenceLimit', 'Model calls', '4 / 6', 'Tool attempts', '9 / 8', 'Search attempts', '5 / 4', 'Budget exhausted', 'Evidence truncated']) assert.ok(html.includes(label), label);
  const truncated = researchResponse();
  Object.assign(truncated.research, { status: 'Partial', stopReason: 'EvidenceTruncated', evidenceTruncated: true });
  truncated.sourceContexts[0].truncated = true;
  const rendered = renderResult(normalizeTimelineSearch(truncated));
  assert.ok(rendered.includes(truncated.content));
  assert.ok(!rendered.includes('Truncated source-entry text'));
  assert.ok(!rendered.includes(truncated.sourceContexts[0].text));
  assert.ok(rendered.includes('EvidenceTruncated'));
});
test('empty, insufficient and provider-limit outcomes retain evidence without displaying uncited facts or bodies', () => {
  for (const [status, stopReason] of [['Empty', 'NoEligibleFacts'], ['InsufficientEvidence', 'NoEvidence'], ['Partial', 'ProviderCallLimit']]) {
    const value = researchResponse(); Object.assign(value.research, { status, stopReason });
    const result = normalizeTimelineSearch(value);
    assert.equal(result.content, '');
    assert.equal(result.sourceContexts.length, 1);
    assert.equal(result.citedFacts.length, 0);
    assert.equal(result.citedSourceContexts.length, 0);
    const html = renderResult(result);
    assert.ok(html.includes(status)); assert.ok(html.includes(stopReason));
    assert.ok(!html.includes(value.content));
    assert.deepEqual(result.facts, value.facts);
    assert.deepEqual(result.sourceContexts, value.sourceContexts);
    assert.ok(!html.includes(value.facts[0].claim));
    assert.ok(!html.includes(value.sourceContexts[0].text));
  }
  const empty = researchResponse(); empty.facts = []; empty.citations = []; empty.sourceContexts = []; empty.contextCitations = [];
  Object.assign(empty.research, { status: 'InsufficientEvidence', stopReason: 'NoEvidence' });
  const html = renderResult(normalizeTimelineSearch(empty));
  assert.ok(html.includes('InsufficientEvidence')); assert.ok(!html.includes('Agent summary'));
});
for (const scenario of [
  { name: 'final narrative', narrative: true, success: true, message: /finished with a cited agent summary/i },
  { name: 'partial with narrative', research: { status: 'Partial', stopReason: 'ToolOrEvidenceLimit', budgetExhausted: true }, narrative: true, message: /tool or evidence limit/i },
  { name: 'partial without narrative', research: { status: 'Partial', stopReason: 'ToolOrEvidenceLimit', budgetExhausted: true }, uncited: true, message: /tool or evidence limit/i },
  { name: 'partial with no stop reason', research: { status: 'Partial' }, uncited: true, message: /partial research/i },
  { name: 'provider limit', research: { status: 'Partial', stopReason: 'ProviderCallLimit', budgetExhausted: true }, message: /model-call limit/i },
  { name: 'truncated narrative', research: { status: 'Partial', stopReason: 'EvidenceTruncated', evidenceTruncated: true }, narrative: true, message: /evidence was truncated/i },
  { name: 'truncated without narrative', research: { status: 'Partial', stopReason: 'EvidenceTruncated', evidenceTruncated: true }, uncited: true, message: /evidence was truncated/i },
  { name: 'tool limit with no evidence', research: { status: 'Partial', stopReason: 'ToolOrEvidenceLimit', budgetExhausted: true }, empty: true, message: /tool or evidence limit/i },
  { name: 'provider limit with no evidence', research: { status: 'Partial', stopReason: 'ProviderCallLimit', budgetExhausted: true }, empty: true, message: /model-call limit/i },
  { name: 'stop reason prevents green completion even without exhaustion flag', research: { stopReason: 'ToolOrEvidenceLimit' }, narrative: true, message: /tool or evidence limit/i },
  { name: 'complete without citations', uncited: true, message: /no verified cited agent summary/i },
  { name: 'insufficient evidence', research: { status: 'InsufficientEvidence', stopReason: 'NoEvidence' }, message: /insufficient evidence/i },
  { name: 'no eligible facts', research: { status: 'Empty', stopReason: 'NoEligibleFacts' }, empty: true, message: /no eligible accepted facts/i },
  { name: 'no matching evidence', research: { status: 'InsufficientEvidence', stopReason: 'NoEvidence' }, empty: true, message: /insufficient evidence/i },
  { name: 'rejected fact citation with valid context', badFact: true, message: /narrative was withheld because citation references could not be verified/i },
  { name: 'rejected context citation with valid fact', badContext: true, message: /narrative was withheld because citation references could not be verified/i },
  { name: 'blank cited content', blank: true, message: /no verified cited agent summary/i },
  { name: 'uncited legacy content', legacy: true, uncited: true, message: /no verified cited agent summary/i }
]) test(`outcome presentation: ${scenario.name}`, () => {
  const value = researchResponse();
  Object.assign(value.research, scenario.research);
  if (scenario.legacy) delete value.research;
  if (scenario.empty) { value.facts = []; value.sourceContexts = []; }
  if (scenario.empty || scenario.uncited) { value.citations = []; value.contextCitations = []; }
  if (scenario.badFact) value.citations[0].factId = 'invented';
  if (scenario.badContext) value.contextCitations[0].sourceFingerprint = 'stale';
  value.content = scenario.blank ? ' \n ' : 'UNTRUSTED_NARRATIVE_SENTINEL';
  const result = normalizeTimelineSearch(value);
  const html = renderResult(result);
  assert.match(html, scenario.message);
  assert.equal(html.includes('Agent summary'), !!scenario.narrative);
  assert.ok(!html.includes('Generated synthesis is separate'));
  assert.equal(html.includes('UNTRUSTED_NARRATIVE_SENTINEL'), !!scenario.narrative);
  assert.equal(html.includes('request-success'), !!scenario.success);
  assert.ok(!html.includes('No cited model answer was returned'));
  if (!scenario.empty) {
    assert.equal(html.includes(value.facts[0].claim), !!scenario.narrative);
    assert.ok(!html.includes(value.sourceContexts[0].text));
    assert.deepEqual(result.facts, value.facts);
    assert.deepEqual(result.sourceContexts, value.sourceContexts);
  }
});

test('unlimited expression metadata accepts null, preserves attempts and renders no false finite ceiling', () => {
  const value = researchResponse();
  value.research.searchQueries = 25;
  value.research.limits.maxSearchQueries = null;
  const result = normalizeTimelineSearch(value);
  assert.equal(result.research.limits.maxSearchQueries, null);
  assert.equal(result.research.searchQueries, 25);
  const html = renderResult(result);
  assert.ok(html.includes('25 / No count ceiling'));
  assert.ok(!html.includes('25 / 20'));
  delete value.research.limits.maxSearchQueries;
  assert.throws(() => normalizeTimelineSearch(value), /structured fact evidence/i);
});

test('research shape validates known statuses, nonnegative counters, limits and boolean metadata', () => {
  for (const change of [
    { status: 'Done' }, { stopReason: 'Deadline' }, { modelCalls: -1 }, { toolCalls: 1.5 },
    { searchQueries: '1' }, { evidenceTruncated: null }, { budgetExhausted: 'false' },
    { warnings: [1] }, { warnings: null }, { limits: null }
  ]) {
    const value = researchResponse(); Object.assign(value.research, change);
    assert.throws(() => normalizeTimelineSearch(value), /structured fact evidence/i, JSON.stringify(change));
  }
  const limits = researchResponse().research.limits;
  for (const key of Object.keys(limits)) for (const invalid of [-1, '6', ...(key === 'maxSearchQueries' ? [] : [null]), ...(key === 'deadlineSeconds' ? [] : [1.5])]) {
    const value = researchResponse(); value.research.limits[key] = invalid;
    assert.throws(() => normalizeTimelineSearch(value), /structured fact evidence/i, key);
  }
  const value = researchResponse(); value.research.limits.deadlineSeconds = 0.5;
  assert.equal(normalizeTimelineSearch(value).research.limits.deadlineSeconds, 0.5);
});
test('legacy optional fields and research null normalize safely without sharing mutable state', () => {
  for (const value of [response(), { ...response(), research: null }]) {
    const result = normalizeTimelineSearch(value);
    assert.equal(result.research, null);
    for (const field of ['sourceContexts', 'contextCitations', 'citedSourceContexts', 'additionalSourceContexts']) assert.deepEqual(result[field], []);
    assert.equal(result.content, 'Narrative');
  }
  const nullable = researchResponse('contextOnly'); nullable.research = null;
  assert.equal(normalizeTimelineSearch(nullable).content, nullable.content);
  const noContextCitations = researchResponse(); delete noContextCitations.contextCitations;
  assert.deepEqual(normalizeTimelineSearch(noContextCitations).contextCitations, []);
  const noContexts = researchResponse(); delete noContexts.sourceContexts;
  assert.equal(normalizeTimelineSearch(noContexts).content, '');
  assert.deepEqual(normalizeTimelineSearch(noContexts).sourceContexts, []);
  const value = researchResponse(); const result = normalizeTimelineSearch(value);
  result.sourceContexts[0].anchorFactIds.push('mutated');
  result.contextCitations[0].anchorFactIds.push('mutated');
  result.coverage.warnings.push('mutated'); result.research.warnings.push('mutated');
  result.research.limits.maxToolCalls = 0; result.facts[0].claim = 'mutated';
  assert.deepEqual(value, researchResponse());
});
test('cited clarification keeps source-kind meaning without dumping its answer transcript or entry text', () => {
  const value = researchResponse();
  Object.assign(value.facts[0], { sourceKind: 'Clarification', sourceQuote: 'Review answer only', clarificationEvidence: JSON.stringify([{ Question: 'Owner?', Answer: 'Review answer only' }]) });
  const result = normalizeTimelineSearch(value);
  assert.deepEqual(result.facts, value.facts);
  assert.deepEqual(result.sourceContexts, value.sourceContexts);
  const html = renderResult(result);
  assert.ok(html.includes('review answer, not original entry text'));
  for (const hidden of ['Question: Owner?', 'Review answer only', 'Source-entry context — not accepted fact proof',
    value.sourceContexts[0].text]) assert.ok(!html.includes(hidden), hidden);
});
test('fact checks still reject invalid origins and unknown fact citations when context exists', () => {
  const value = researchResponse(); value.facts[0].extractionJobId = 'another-origin';
  assert.throws(() => normalizeTimelineSearch(value), /structured fact evidence/i);
  const unknownFact = researchResponse(); unknownFact.contextCitations = [];
  unknownFact.citations[0].factId = unknownFact.sourceContexts[0].evidenceId;
  const result = normalizeTimelineSearch(unknownFact);
  assert.equal(result.content, ''); assert.equal(result.citations.length, 0);
  assert.equal(result.additionalFacts.length, 1);
});
test('unsafe hidden evidence remains validated and retained; visible summary and warnings escape React text', () => {
  const value = researchResponse('contextOnly');
  value.content = '<script>narrative</script>';
  value.sourceContexts[0].text = '<script>context</script><img src=x onerror=bad>';
  value.research.warnings = ['<script>warning</script>'];
  value.sourceContexts[0].sourceFingerprint = '<fingerprint>';
  value.contextCitations[0].sourceFingerprint = '<fingerprint>';
  value.facts[0].sourceFingerprint = '<fingerprint>';
  const result = normalizeTimelineSearch(value);
  assert.deepEqual(result.sourceContexts, value.sourceContexts);
  assert.deepEqual(result.contextCitations, value.contextCitations);
  assert.equal(result.facts[0].sourceFingerprint, '<fingerprint>');
  const html = renderResult(result);
  for (const escaped of ['&lt;script&gt;narrative', '&lt;script&gt;warning']) assert.ok(html.includes(escaped), escaped);
  for (const hidden of ['<script>', '<img', '&lt;script&gt;context', '&lt;img', '&lt;fingerprint&gt;']) assert.ok(!html.includes(hidden), hidden);
  const tampered = structuredClone(value); tampered.contextCitations[0].sourceFingerprint = 'changed';
  assert.equal(normalizeTimelineSearch(tampered).content, '');
  const malformed = structuredClone(value); malformed.sourceContexts[0].anchorFactIds = ['unknown'];
  assert.throws(() => normalizeTimelineSearch(malformed), /structured fact evidence/i);
});

test('UI limits results to cited facts and plain summary while preserving search lifecycle and adjacent management', () => {
  const app = readFileSync(new URL('./App.jsx', import.meta.url), 'utf8');
  const search = app.slice(app.indexOf('function TimelineSearch('), app.indexOf('function TimelineEntries('));
  assert.ok(search.includes('searchTimelineFacts('));
  assert.ok(search.includes('useWorkspaceAction()'));
  assert.ok(search.includes('<WorkspaceFeedback'));
  assert.ok(search.includes('fence.isCurrent(controller)'));
  assert.ok(search.includes('maxLength={300}'));
  assert.ok(!search.includes('Fact search complete.'));
  assert.match(search, /}, ""\);/); // Result owns outcome-specific feedback, not an unconditional success.
  const ui = readFileSync(new URL('./TimelineSearchResults.jsx', import.meta.url), 'utf8');
  for (const label of ['Agent summary', 'Facts cited in answer', 'Accepted valid facts', 'Research outcome']) assert.ok(ui.includes(label), label);
  for (const hidden of ['Additional retrieved context', 'Clarification evidence', 'Source quote', 'contexts(',
    'sourceEntryHref(', 'dangerouslySetInnerHTML', 'ReactMarkdown']) assert.ok(!ui.includes(hidden), hidden);
  assert.ok(search.includes('return () => fence.cancel()'));
  assert.ok(search.includes('answer?.timelineId === timeline.id && answer?.projectId === projectId'));
  assert.ok(search.includes('Searching facts...'));
  const entries = app.slice(app.indexOf('function TimelineEntries('), app.indexOf('function Workspace('));
  for (const retained of ['title="Timeline entries"', 'Read full entry', 'Review facts / source access',
    '<TimelineSearch key={timeline.id}', '<SnapshotMaintenance key={timeline.id}', 'entry.preview']) assert.ok(entries.includes(retained), retained);
  const snapshots = app.slice(app.indexOf('function SnapshotMaintenance('), app.indexOf('function TimelineSearch('));
  for (const retained of ['Create timeline snapshot', 'Select entries', 'Create snapshot', 'snapshot.summaryContent']) assert.ok(snapshots.includes(retained), retained);
});
