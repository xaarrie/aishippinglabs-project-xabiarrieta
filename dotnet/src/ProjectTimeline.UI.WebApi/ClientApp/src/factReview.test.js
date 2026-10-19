import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createFactReviewClient, canAcceptFactProposal, factReviewError } from './factReview.js';
import * as review from './factReview.js';

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
