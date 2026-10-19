import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createFactReviewClient, canAcceptFactProposal, factReviewError } from './factReview.js';
import * as review from './factReview.js';

test('generation sends an explicit empty body or generation CAS and preserves cancellation', async () => {
  const calls = [], signal = new AbortController().signal;
  const client = createFactReviewClient(async (...args) => { calls.push(args); });
  await client.generate('entry /1', { signal });
  await client.regenerate('entry /1', 'generation-a', { signal });
  assert.equal(calls[0][0], '/api/entries/entry%20%2F1/fact-generation');
  assert.deepEqual(JSON.parse(calls[0][1].body), {});
  assert.deepEqual(JSON.parse(calls[1][1].body), { regenerate: true, expectedGenerationId: 'generation-a' });
  for (const [, options] of calls) { assert.equal(options.signal, signal); assert.equal(options.method, 'POST'); }
});

test('only the current Generated proposal supplies candidates, never history or effective facts', () => {
  const current = { proposalId: 'g2', state: 'Generated', additions: [] };
  const state = { generationId: 'g2', proposals: [{ proposalId: 'reconcile', state: 'Pending', additions: ['not candidates'] }, { proposalId: 'g1', state: 'Superseded', additions: ['old'] }, current], facts: ['effective'], candidates: ['wrong root'] };
  assert.equal(review.currentFactGeneration(state), current);
  assert.equal(review.currentFactGeneration({ ...state, generationId: 'missing' }), null);
  assert.equal(review.currentFactGeneration(null), null);
  assert.equal(review.currentFactGeneration({ generationId: 'reconcile', proposals: state.proposals }), null);
});

test('lifecycle actions fail closed and depend on server permissions, not proposal counts', () => {
  const fingerprint = 'A'.repeat(64);
  const pending = { source: { entryId: 'e', timelineId: 't', title: 'Source', content: 'Source', status: 'Draft', fingerprint }, refreshRequired: false,
    lifecycleState: 'Pending', generationId: 'g', canEdit: true, canGenerate: true, canRegenerate: true,
    proposals: [{ proposalId: 'g', state: 'Generated', sourceFingerprint: fingerprint, additions: [] }, { proposalId: 'r', state: 'Pending', sourceFingerprint: fingerprint, ready: true }] };
  const options = { canWrite: true };
  const allowed = review.factReviewActions(pending, options);
  assert.equal(allowed.edit, true); assert.equal(allowed.reconcile, true); assert.equal(allowed.regenerate, true);
  assert.equal(review.canAcceptCurrentFactProposal(pending, pending.proposals[1]), true);
  assert.equal(review.canAcceptCurrentFactProposal(pending, pending.proposals[0]), false);
  assert.equal(review.canAcceptCurrentFactProposal(pending, { proposalId: 'old', state: 'Pending', ready: true }), false);
  for (const state of [null, { ...pending, lifecycleState: 'Reconciled', canEdit: false, canGenerate: false, canRegenerate: false }]) {
    assert.deepEqual(review.factReviewActions(state, options), { edit: false, generate: false, regenerate: false, reconcile: false });
    assert.equal(review.canAcceptCurrentFactProposal(state, pending.proposals[1]), false);
  }
  for (const flags of [{ canWrite: false }, { canWrite: true, dirty: true }, { canWrite: true, busy: true }, { canWrite: true, conflict: true }]) {
    const actions = review.factReviewActions(pending, flags);
    assert.equal(actions.generate, false); assert.equal(actions.regenerate, false); assert.equal(actions.reconcile, false);
  }
  for (const lifecycleState of ['NotGenerated', 'Loading', 'Retry', 'Failed']) assert.equal(review.factReviewActions({ ...pending, lifecycleState }, options).reconcile, false);
  assert.equal(review.factReviewActions({ ...pending, lifecycleState: 'Empty' }, options).reconcile, true);
  assert.equal(review.factReviewActions({ ...pending, canEdit: false }, options).edit, false);
});

test('source identity mismatches and mixed-read refresh gates block reconciliation and acceptance', () => {
  const fingerprint = 'A'.repeat(64);
  const source = { entryId: 'e', timelineId: 't', title: 'Current source', content: 'Current evidence.', status: 'Draft', fingerprint };
  const generated = { proposalId: 'g', state: 'Generated', sourceFingerprint: fingerprint, additions: [] };
  const ready = { proposalId: 'r', state: 'Pending', ready: true, sourceFingerprint: fingerprint };
  const state = { source, refreshRequired: false, lifecycleState: 'Pending', generationId: 'g', canEdit: true, canRegenerate: true, proposals: [ready, generated] };
  assert.equal(review.factReviewActions(state, { canWrite: true }).reconcile, true);
  assert.equal(review.canAcceptCurrentFactProposal(state, ready), true);
  for (const mixed of [
    { ...state, refreshRequired: true },
    { ...state, source: null },
    { ...state, proposals: [ready, { ...generated, sourceFingerprint: 'B'.repeat(64) }] },
    { ...state, source: { ...source, fingerprint: 'C'.repeat(64) } }
  ]) {
    assert.equal(review.factReviewActions(mixed, { canWrite: true }).reconcile, false);
    assert.equal(review.canAcceptCurrentFactProposal(mixed, ready), false);
  }
  assert.equal(review.canAcceptCurrentFactProposal({ ...state, proposals: [{ ...ready, sourceFingerprint: 'B'.repeat(64) }, generated] }, { ...ready, sourceFingerprint: 'B'.repeat(64) }), false);
});

test('a pinned current generation preserves ready acceptance and answers in a full bounded history', () => {
  const fingerprint = 'A'.repeat(64);
  const generated = { proposalId: 'g', state: 'Generated', sourceFingerprint: fingerprint, additions: [] };
  const ready = { proposalId: 'r', state: 'Pending', ready: true, sourceFingerprint: fingerprint, questions: [{ question: 'Confirmed?', answer: 'Yes' }] };
  const state = { lifecycleState: 'Empty', generationId: 'g', canEdit: true, truncated: true, refreshRequired: false,
    source: { entryId: 'e', timelineId: 't', title: 'Source', content: 'Source', status: 'Draft', fingerprint },
    proposals: [ready, ...Array.from({ length: 48 }, (_, i) => ({ proposalId: `old-${i}`, state: 'Superseded' })), generated] };
  assert.equal(state.proposals.length, 50);
  assert.equal(review.factReviewActions(state, { canWrite: true }).reconcile, true);
  assert.equal(review.canAcceptCurrentFactProposal(state, ready), true);
  assert.equal(review.currentFactReconciliation(state), ready);
  const unanswered = { ...ready, ready: false, questions: [{ question: 'Confirmed?', answer: null }] };
  assert.equal(review.factReviewActions({ ...state, proposals: [unanswered, ...state.proposals.slice(1)] }, { canWrite: true }).reconcile, true, 'clarification submission remains enabled with pinned evidence');
  assert.equal(review.canAcceptCurrentFactProposal({ ...state, proposals: [unanswered, ...state.proposals.slice(1)] }, unanswered), false);
});

test('one save action serializes double submits and generates once after persistence', async () => {
  const calls = []; let release;
  const saving = new Promise(resolve => { release = resolve; });
  const action = review.createEntrySaveAction({ generate: async (id, options) => { calls.push(['generate', id, options.signal]); return { lifecycleState: 'Pending' }; } });
  const signal = new AbortController().signal;
  const save = async options => { calls.push(['save', options.signal]); await saving; return { id: 'saved' }; };
  const first = action.run(save, { signal });
  await action.run(save, { signal });
  assert.equal(calls.length, 1);
  release();
  const result = await first;
  assert.equal(result.entry.id, 'saved'); assert.equal(result.facts.lifecycleState, 'Pending');
  assert.deepEqual(calls, [['save', signal], ['generate', 'saved', signal]]);
});

test('generation failure never turns a successful source save into a failed save', async () => {
  const action = review.createEntrySaveAction({ generate: async () => { throw { status: 502 }; } });
  const result = await action.run(async () => ({ id: 'saved' }));
  assert.equal(result.entry.id, 'saved'); assert.equal(result.facts, null);
  assert.match(result.generationError, /saved.*provider unavailable/i);
  await assert.rejects(action.run(async () => { throw new Error('save failed'); }), /save failed/);
});

test('save failures never generate and a later explicit retry remains possible', async () => {
  let saves = 0, generations = 0;
  const action = review.createEntrySaveAction({ generate: async () => { generations++; return { lifecycleState: 'Empty' }; } });
  const save = async () => { if (++saves === 1) throw new Error('persistence failed'); return { id: 'saved-on-retry' }; };
  await assert.rejects(action.run(save), /persistence failed/);
  assert.equal(generations, 0);
  const result = await action.run(save);
  assert.equal(result.entry.id, 'saved-on-retry'); assert.equal(generations, 1);
});

test('saved receipts retain HTTP-success failure/retry/empty lifecycle without automatic retry', async () => {
  for (const lifecycleState of ['Failed', 'Retry', 'Empty']) {
    let generations = 0;
    const action = review.createEntrySaveAction({ generate: async () => { generations++; return { lifecycleState }; } });
    const result = await action.run(async () => ({ id: 'saved' }));
    assert.equal(result.entry.id, 'saved'); assert.equal(result.facts.lifecycleState, lifecycleState);
    assert.equal(result.generationError, ''); assert.equal(generations, 1);
  }
});

test('abandoned save screens do not launch generation after a delayed save', async () => {
  const controller = new AbortController(); let generated = 0;
  const action = review.createEntrySaveAction({ generate: async () => { generated++; } });
  await action.run(async () => { controller.abort(); return { id: 'persisted' }; }, { signal: controller.signal });
  assert.equal(generated, 0);
});

test('request fence rejects stale reads after edits, newer requests, and unmount', () => {
  const fence = review.createFactRequestFence();
  const old = fence.begin(); const current = fence.begin();
  assert.equal(fence.isCurrent(old), false); assert.equal(fence.isCurrent(current), true);
  fence.cancel(); assert.equal(fence.isCurrent(current), false); assert.equal(current.signal.aborted, true);
  const next = fence.begin(); assert.equal(fence.isCurrent(next), true);
});

test('fact review uses stored proposal IDs and indexed answers, never PUT', async () => {
  const calls = [];
  const client = createFactReviewClient(async (url, options = {}) => { calls.push([url, options]); return {}; });
  await client.read('entry');
  await client.plan('entry', 'previous', [{ questionIndex: 2, answer: 'Alice' }]);
  await client.accept('entry', 'server-id');
  assert.equal(calls[0][0], '/api/entries/entry/facts');
  assert.equal(calls[1][0], '/api/entries/entry/fact-proposals');
  assert.deepEqual(JSON.parse(calls[1][1].body), { previousProposalId: 'previous', answers: [{ questionIndex: 2, answer: 'Alice' }] });
  assert.equal(calls[2][0], '/api/entries/entry/fact-proposals/server-id/accept');
  assert.equal(calls[2][1].body, undefined);
  assert.ok(calls.every(([, o]) => o.method !== 'PUT'));
});

test('only ready pending persisted proposals can be explicitly accepted', () => {
  assert.equal(canAcceptFactProposal({ proposalId: 'id', ready: true, state: 'Pending' }), true);
  for (const proposal of [{ ready: true, state: 'Pending' }, { proposalId: 'id', ready: false, state: 'Pending' }, { proposalId: 'id', ready: true, state: 'Accepted' }]) assert.equal(canAcceptFactProposal(proposal), false);
  assert.match(factReviewError({ status: 409 }), /regenerate/i);
  assert.match(factReviewError({ status: 502, message: 'secret provider body' }), /provider unavailable/i);
  assert.doesNotMatch(factReviewError({ status: 502, message: 'secret provider body' }), /secret/);
});

test('failed acceptance propagates without activating a pending proposal', async () => {
  const pending = { proposalId: 'server-id', state: 'Pending', ready: true };
  const client = createFactReviewClient(async () => { throw Object.assign(new Error('untrusted error'), { status: 409 }); });
  await assert.rejects(client.accept('entry', pending.proposalId), { status: 409 });
  assert.equal(pending.state, 'Pending');
});

test('stored invalidation identifies the actual cause even if that cause is now invalid', () => {
  const fact = { id: 'old', valid: false, invalidatedByFactId: 'cause' };
  const facts = [fact, { id: 'cause', valid: false, claim: 'Atlas car C-7 is red.' }, { id: 'newest', valid: true, claim: 'Not the cause' }];
  assert.equal(review.factInvalidationText(fact, facts), 'Invalidated by fact cause: Atlas car C-7 is red.');
  assert.equal(review.factInvalidationText({ valid: true }, facts), null);
  assert.equal(review.factInvalidationText({ valid: false, invalidatedByFactId: null }, facts), 'Replacement fact was not recorded (legacy history).');
  assert.equal(review.factInvalidationText({ valid: false }, facts), 'Replacement fact was not recorded (legacy history).');
  assert.equal(review.factInvalidationText(fact, []), 'Invalidated by fact cause. Replacement claim is not loaded in this bounded view.');
});

test('proposal preview labels intended addition, not a persisted invalidating fact', () => {
  const invalidation = { invalidatingAdditionId: 'addition-b', invalidatingAdditionIndex: 0, invalidatingClaim: 'Atlas car C-7 is red.' };
  assert.equal(review.proposalInvalidationText(invalidation), 'Intended replacement addition 1 (addition-b): Atlas car C-7 is red.');
  assert.doesNotMatch(review.proposalInvalidationText(invalidation), /Invalidated by fact/);
  assert.equal(review.proposalInvalidationText({ invalidatingAdditionId: 'stable-id' }), 'Intended replacement addition stable-id. Replacement claim is not loaded.');
  assert.equal(review.proposalInvalidationText({}), 'Replacement addition was not recorded (legacy proposal).');
});

test('component renders cause projections as plain text without changing acceptance rules', () => {
  const component = readFileSync(new URL('./FactReview.jsx', import.meta.url), 'utf8');
  assert.match(component, /factInvalidationText\(fact, state\.facts\)/);
  assert.match(component, /proposalInvalidationText\(fact\)/);
  assert.doesNotMatch(component, /dangerouslySetInnerHTML/);
  assert.match(component, /!canWrite \|\| dirty \|\| conflict \|\| !canAcceptFactProposal/);
});

test('main entry screen uses fact review, not legacy review-to-PUT application', () => {
  const app = readFileSync(new URL('./App.jsx', import.meta.url), 'utf8');
  const component = readFileSync(new URL('./FactReview.jsx', import.meta.url), 'utf8');
  assert.match(app, /import FactReview from/);
  assert.doesNotMatch(app, /import EntryReview|applyReviewDraft|<EntryReview/);
  assert.doesNotMatch(component, /method: ['\"]PUT|onSave|applyReviewDraft/);
  assert.match(component, /Original saved source/);
  assert.match(component, /Accept fact proposal/);
});
