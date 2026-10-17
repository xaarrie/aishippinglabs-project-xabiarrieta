import test from 'node:test';
import assert from 'node:assert/strict';
import { formatEntryDraft, reviewRequest, canReviewEntry, applyReviewDraft, hasEntryWriteAccess } from './entryReview.js';
import * as review from './entryReview.js';

test('draft retains every structured task and decision detail', () => {
  const draft = formatEntryDraft({ title: 'Plan', context: 'Background', tasks: [{ id: 'T1', title: 'Ship', description: 'Deploy safely', owner: 'Ada', status: 'InProgress', targetDate: '2026-12-01', dependencies: ['T0'], acceptanceCriteria: ['Works offline'], pendingQuestionIds: ['Q1'] }], decisions: [{ description: 'Use cache', rationale: 'Latency', pendingQuestionIds: ['Q2'] }], assumptions: ['Capacity available'], risks: ['Outage'] });
  assert.equal(draft.title, 'Plan');
  for (const detail of ['Background', 'T1', 'Ship', 'Deploy safely', 'Ada', 'InProgress', '2026-12-01', 'T0', 'Works offline', 'Q1', 'Use cache', 'Latency', 'Q2', 'Capacity available', 'Outage']) assert.ok(draft.content.includes(detail), detail);
});
test('empty sections and null optional fields remain readable', () => {
  const draft = formatEntryDraft({ title: 'Minimal', context: '', tasks: [{ title: 'Task', id: null, owner: null }], decisions: [], assumptions: [], risks: [] });
  assert.ok(draft.content.includes('Task'));
  assert.ok(!draft.content.includes('undefined'));
  assert.ok(!draft.content.includes('null'));
});
test('formatter preserves additional nested data and multiline descriptions', () => {
  const draft = formatEntryDraft({ title: 'Plan', context: 'Line one\nLine two', tasks: [{ title: 'Task', extra: { important: 'Do not lose' }, acceptanceCriteria: ['First', 'Second'] }] });
  for (const detail of ['Line one\nLine two', 'Do not lose', 'First', 'Second', 'Acceptance Criteria']) assert.ok(draft.content.includes(detail), detail);
});
test('rich objectives, task specifics and uncategorized facts reach the editable proposal unchanged', () => {
  // Synthetic facts test formatter transport, not LLM semantic completeness.
  const entry = {
    title: 'Pump trial — Bilbao',
    context: 'Marta Ruiz and Iñaki Soto inspected bay C on 2026-10-17.\nKeep the handwritten calibration sheet.',
    objectives: [{ title: 'Validate low-pressure operation', description: 'Demonstrate 12 L/min at 2 bar before 2026-11-20.', details: ['Only after Marta Ruiz signs the safety checklist.', 'Perhaps rehearse on 2026-11-12, if Marta approves.'], acceptanceCriteria: ['Three consecutive 30 min runs without leaks.'], pendingQuestionIds: ['Q1'] }],
    tasks: [{ id: 'T7', title: 'Instrument the pump', description: 'Iñaki Soto installs two pressure sensors.', owner: 'Iñaki Soto', status: 'Planned', targetDate: '2026-11-05', dependencies: ['T3'], acceptanceCriteria: ['Error ≤ 0.2 bar across all three runs.'], pendingQuestionIds: ['Q1'], details: ['Record raw samples every 250 ms.', "The original note says 'perhaps Friday'; the date is not confirmed."], constraints: ['Do not drill the housing.', 'Stop if temperature exceeds 60 °C.'], quantities: ['2 sensors', 'Cable length: 4.5 m per sensor'] }],
    decisions: [{ description: 'Use the borrowed rig, not a new purchase.', rationale: 'The €800 budget excludes new equipment; retain cash for seals.', pendingQuestionIds: [] }],
    assumptions: ['Spare seals may arrive on Monday; unconfirmed.'],
    risks: ['If the rig is late, the 2026-11-20 demonstration slips.'],
    additionalDetails: ['Visitor badge B-17 stays with Leire.', 'The blue folder is in locker 9, not bay C.']
  };
  const originalJson = JSON.stringify(entry);
  const draft = formatEntryDraft(entry);
  const applied = applyReviewDraft({ title: 'Original', content: 'Untouched source', status: 'Draft' }, draft);
  const assertLeaves = value => {
    if (typeof value === 'string') assert.ok(applied.content.includes(value), value);
    else for (const child of Object.values(value)) assertLeaves(child);
  };
  for (const [key, value] of Object.entries(entry)) if (key !== 'title') assertLeaves(value);
  assert.equal(applied.title, entry.title);
  assert.equal(applied.status, 'Draft');
  for (const label of ['Objectives', 'Description', 'Details', 'Acceptance Criteria', 'Pending Question Ids', 'Constraints', 'Quantities', 'Additional Details']) assert.ok(applied.content.includes(label), label);
  assert.equal(JSON.stringify(entry), originalJson);
});
test('additive empty detail lists leave legacy formatted content unchanged', () => {
  const legacy = { title: 'Legacy', context: 'Original context', tasks: [{ title: 'Task', description: 'Original description' }], decisions: [], assumptions: [], risks: [] };
  const additive = { ...legacy, objectives: [], additionalDetails: [], tasks: legacy.tasks.map(task => ({ ...task, details: [], constraints: [], quantities: [] })) };
  assert.deepEqual(formatEntryDraft(additive), formatEntryDraft(legacy));
});
test('requests use saved entry initially and preserve answers across rounds', () => {
  assert.deepEqual(reviewRequest(null, { Q1: 'ignored' }), {});
  const previousResult = { status: 0, questions: [{ id: 'Q2' }] };
  const answers = { Q1: 'Earlier answer', Q2: 'New answer', Q3: '  ' };
  assert.deepEqual(reviewRequest(previousResult, answers), { previousResult, userAnswers: { Q1: 'Earlier answer', Q2: 'New answer' } });
  assert.equal(answers.Q3, '  ');
});
test('only persisted entries with write permission can be reviewed', () => {
  assert.equal(canReviewEntry(null, true), false);
  assert.equal(canReviewEntry('saved', false), false);
  assert.equal(canReviewEntry('saved', true), true);
});
test('write access matches project owner, admin and contributor/creator permissions', () => {
  const project = { ownerId: 'owner' };
  assert.equal(hasEntryWriteAccess({ id: 'owner' }, project), true);
  assert.equal(hasEntryWriteAccess({ id: 'admin', roleCode: 'Admin' }, project), true);
  for (const role of ['Creator', 'Contributor', 'Reviewer']) {
    assert.equal(hasEntryWriteAccess({ id: 'member' }, project, [{ userId: 'member', projectRoleName: role }]), role !== 'Reviewer');
  }
  assert.equal(hasEntryWriteAccess({ id: 'outsider' }, project, [{ userId: 'other', projectRoleName: 'Creator' }]), false);
  assert.equal(hasEntryWriteAccess(null, project), false);
});
const source = { entryId: 'entry-1', title: 'Original', content: 'Source\n\n', fingerprint: 'a'.repeat(64) };
const proposal = { title: ' Generated ', content: 'Proposal\r\n\n ' };
const auditResponse = { entryId: source.entryId, sourceFingerprint: source.fingerprint, proposalFingerprint: 'b'.repeat(64), coverage: 1, inconclusive: false, preservedFacts: 1, totalFacts: 1, facts: [{ fact: 'Source fact', status: 'Preserved' }], unsupportedAdditions: [] };

test('line diff marks title and line replacements without changing either text', () => {
  const diff = review.lineDiff('Old title', 'New title');
  assert.deepEqual(diff.source, [{ text: 'Old title', changed: true }]);
  assert.deepEqual(diff.proposal, [{ text: 'New title', changed: true }]);
  const lines = review.lineDiff('same\nold\nlast', 'same\nnew\nlast');
  assert.deepEqual(lines.source.map(part => part.changed), [false, true, false]);
  assert.deepEqual(lines.proposal.map(part => part.changed), [false, true, false]);
});
test('diff preserves identical, empty, blank lines, whitespace and exact newline kinds', () => {
  for (const text of ['', '\n\n', ' \r\n\t\nlast\r', '<img src=x onerror=alert(1)>\n<script>bad()</script>']) {
    const diff = review.lineDiff(text, text);
    assert.equal(diff.source.map(part => part.text).join(''), text);
    assert.equal(diff.proposal.map(part => part.text).join(''), text);
    assert.ok(diff.source.every(part => !part.changed));
  }
  for (const [before, after] of [['', '\n'], [' a\n', 'a\r\n\n'], ['\n\n', ''], ['<b>old</b>', '<b>new</b>']]) {
    const diff = review.lineDiff(before, after);
    assert.equal(diff.source.map(part => part.text).join(''), before);
    assert.equal(diff.proposal.map(part => part.text).join(''), after);
  }
});
test('large line diff uses bounded fallback and loses no content', () => {
  const before = Array.from({ length: 10000 }, (_, i) => `old ${i}\n`).join('');
  const after = before.replaceAll('old', 'new') + '\n';
  const diff = review.lineDiff(before, after);
  assert.equal(diff.fallback, true);
  assert.equal(diff.source.map(part => part.text).join(''), before);
  assert.equal(diff.proposal.map(part => part.text).join(''), after);
});
test('diff handles insertion, deletion, long lines and newline-only changes without loss', () => {
  for (const [before, after] of [['one\ntwo\n', 'one\nadded\ntwo\n'], ['one\nremoved\ntwo', 'one\ntwo'], ['x'.repeat(210000), 'x'.repeat(210001)], ['trailing\n', 'trailing']]) {
    const diff = review.lineDiff(before, after);
    assert.equal(diff.source.map(part => part.text).join(''), before);
    assert.equal(diff.proposal.map(part => part.text).join(''), after);
    assert.ok([...diff.source, ...diff.proposal].some(part => part.changed));
  }
});
test('source is independently copied from server and never invented from the editor', () => {
  const server = { ...source };
  const copied = review.reviewSource(source.entryId, server);
  server.content = 'replaced';
  assert.equal(copied.content, source.content);
  assert.ok(Object.isFrozen(copied));
  assert.equal(review.reviewSource('other', source), null);
  assert.equal(review.reviewSource(source.entryId, null), null);
});
test('audit request preserves exact displayed title/content and server source identity', () => {
  const session = review.createReviewSession();
  const snapshot = session.replace(source.entryId, source, proposal);
  assert.deepEqual(review.auditRequest(snapshot), { proposalTitle: proposal.title, proposalContent: proposal.content, expectedSourceFingerprint: source.fingerprint });
  assert.equal(review.auditRequest(session.replace(source.entryId, null, proposal)), null);
  assert.equal(review.auditRequest(session.replace(source.entryId, { ...source, fingerprint: null }, proposal)), null);
});
test('request snapshots are immutable and reverting edits cannot revive an old audit', () => {
  const session = review.createReviewSession();
  const mutable = { ...proposal };
  const snapshot = session.replace(source.entryId, source, mutable);
  mutable.content = 'changed outside the session';
  assert.equal(snapshot.proposal.content, proposal.content);
  assert.ok(Object.isFrozen(snapshot.proposal));
  session.replace(source.entryId, source, { ...proposal, content: 'edited' });
  const reverted = session.replace(source.entryId, source, proposal);
  assert.equal(session.isCurrent(snapshot), false);
  assert.equal(session.isCurrent(reverted), true);
});
test('audit binding rejects entry/source mismatch and missing provenance', () => {
  const snapshot = review.createReviewSession().replace(source.entryId, source, proposal);
  assert.equal(review.auditMatches(snapshot, auditResponse), true);
  for (const response of [null, {}, { ...auditResponse, entryId: 'other' }, { ...auditResponse, sourceFingerprint: 'changed' }, { ...auditResponse, proposalFingerprint: null }]) assert.equal(review.auditMatches(snapshot, response), false);
});
test('delayed responses are synchronously rejected after edits, rounds, discard and target/source changes', async () => {
  for (const [entryId, nextSource, nextProposal] of [
    [source.entryId, source, { ...proposal, title: 'edited' }],
    [source.entryId, source, { ...proposal, content: `${proposal.content}\n` }],
    [source.entryId, source, proposal], // New round invalidates even unchanged text.
    [source.entryId, null, null], // Discard.
    ['other', source, proposal],
    [source.entryId, { ...source, fingerprint: 'c'.repeat(64) }, proposal],
    [null, null, null] // Unmount.
  ]) {
    const session = review.createReviewSession();
    const snapshot = session.replace(source.entryId, source, proposal);
    const delayed = Promise.resolve(auditResponse).then(response => session.isCurrent(snapshot) && review.auditMatches(snapshot, response));
    session.replace(entryId, nextSource, nextProposal);
    assert.equal(session.isCurrent(snapshot), false);
    assert.equal(await delayed, false);
  }
});
test('audit coverage 0 and 1 are estimated values; null, empty and inconclusive never become perfect', () => {
  assert.equal(review.auditCoverage({ ...auditResponse, coverage: 0, preservedFacts: 0 }), 0);
  assert.equal(review.auditCoverage(auditResponse), 1);
  for (const response of [null, {}, { ...auditResponse, coverage: null }, { ...auditResponse, facts: [] }, { ...auditResponse, totalFacts: 0 }, { ...auditResponse, inconclusive: true }, { ...auditResponse, coverage: '1' }, { ...auditResponse, coverage: 2 }]) assert.equal(review.auditCoverage(response), null);
  const withAddition = { ...auditResponse, unsupportedAdditions: [{ finding: 'Invented date', proposalQuote: 'Tomorrow', evidenceVerified: true }] };
  assert.equal(review.auditCoverage(withAddition), 1);
  assert.equal(review.auditHasIssues(withAddition), true);
  for (const status of ['Omitted', 'Contradicted', 'Uncertain']) assert.equal(review.auditHasIssues({ ...auditResponse, facts: [{ status }] }), true);
});
test('invalid fact inventory or counters cannot produce a numeric coverage claim', () => {
  for (const invalid of [
    { facts: [{}] }, { facts: [{ fact: '', status: 'Preserved' }] },
    { facts: [{ fact: 'Fact', status: 'Unknown' }] }, { totalFacts: 2 },
    { preservedFacts: -1 }, { preservedFacts: 2 }, { preservedFacts: null },
    { coverage: NaN }, { coverage: Infinity }
  ]) assert.equal(review.auditCoverage({ ...auditResponse, ...invalid }), null);
});
test('API keeps HTTP status and only sanitized known review problem diagnostics', async () => {
  const originalStorage = globalThis.localStorage;
  const originalFetch = globalThis.fetch;
  globalThis.localStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
  try {
    const { api, setToken, clearToken } = await import('./api.js');
    const problem = { code: 'entry_review_audit_failed', title: 'Entry audit could not be completed.', detail: 'Untrusted provider payload: SECRET', stage: 'audit_invocation', category: 'provider_timeout', traceId: 'abc123', exception: { message: 'SECRET' }, coverage: 1 };
    globalThis.fetch = async () => new Response(JSON.stringify(problem), { status: 502 });
    await assert.rejects(api('/api/entries/entry-1/review/audit'), error => {
      assert.equal(error.status, 502);
      assert.deepEqual(error.problem, { code: problem.code, stage: problem.stage, category: problem.category, traceId: problem.traceId });
      assert.match(error.message, /provider_timeout/);
      assert.match(error.message, /abc123/);
      assert.ok(!JSON.stringify(error).includes('SECRET'));
      assert.ok(!error.message.includes('SECRET'));
      return true;
    });
    globalThis.fetch = async () => new Response(JSON.stringify({ code: 'unknown', error: 'SECRET', title: 'SECRET', detail: 'SECRET', traceId: 'SECRET' }), { status: 500 });
    await assert.rejects(api('/any'), error => error.status === 500 && error.message === 'Request failed (500)' && !error.problem);
    globalThis.fetch = async () => new Response(JSON.stringify({ code: 'entry_review_source_changed', error: 'SECRET' }), { status: 409 });
    await assert.rejects(api('/review/audit'), error => error.status === 409 && /source changed/i.test(error.message) && !error.message.includes('SECRET'));
    globalThis.fetch = async () => new Response(JSON.stringify({ ...problem, stage: 'SECRET', category: 'SECRET', traceId: 'bad\nSECRET' }), { status: 502 });
    await assert.rejects(api('/review/audit'), error => !error.message.includes('SECRET') && Object.keys(error.problem).length === 1);
    globalThis.fetch = async () => new Response('not JSON SECRET', { status: 502 });
    await assert.rejects(api('/any'), error => error.message === 'Request failed (502)');
    let observed;
    globalThis.fetch = async (path, options) => { observed = { path, options }; return new Response('{"ok":true}', { status: 200 }); };
    assert.deepEqual(await api('/success', { method: 'POST', body: '{}', headers: { 'x-test': 'yes' } }), { ok: true });
    assert.equal(observed.options.headers['x-test'], 'yes');
    assert.equal(observed.options.headers['content-type'], 'application/json');
    setToken('synthetic-test-token');
    const controller = new AbortController();
    const form = new FormData(); form.append('field', 'value');
    await api('/form', { method: 'POST', body: form, signal: controller.signal });
    assert.equal(observed.options.headers.Authorization, 'Bearer synthetic-test-token');
    assert.equal(observed.options.headers['content-type'], undefined);
    assert.equal(observed.options.body, form);
    assert.equal(observed.options.signal, controller.signal);
    clearToken();
    globalThis.fetch = async () => new Response(null, { status: 204 });
    assert.equal(await api('/empty'), null);
  } finally { globalThis.fetch = originalFetch; globalThis.localStorage = originalStorage; }
});

test('applying a draft changes only title/content and leaves snapshot untouched for discard', () => {
  const before = { title: 'Dirty title', content: 'Unsaved notes', status: 'Approved' };
  const applied = applyReviewDraft(before, { title: 'Generated', content: 'Reviewed' });
  assert.deepEqual(applied, { title: 'Generated', content: 'Reviewed', status: 'Approved' });
  assert.deepEqual(before, { title: 'Dirty title', content: 'Unsaved notes', status: 'Approved' });
});
