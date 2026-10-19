// Offline interaction test. Real React/Vite/Chromium; synthetic authorized API only.
// node tests/entryFactsJourney.browser.mjs <existing-playwright-index.mjs> <chrome.exe>
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
const fixtures = JSON.parse(readFileSync(new URL('./entryFactsJourney.fixtures.json', import.meta.url), 'utf8'));
import { createServer } from '../node_modules/vite/dist/node/index.js';
import react from '../node_modules/@vitejs/plugin-react/dist/index.js';

const { chromium } = await import(pathToFileURL(process.argv[2]).href);
const root = fileURLToPath(new URL('..', import.meta.url));
const server = await createServer({ root, configFile: false, envFile: false, plugins: [react()],
  cacheDir: 'C:/Windows/Temp/project-timeline-t2-vite-cache',
  server: { host: '127.0.0.1', port: 5179, strictPort: true } });
let browser;
try {
  await server.listen();
  browser = await chromium.launch({ executablePath: process.argv[3], headless: true });
  const context = await browser.newContext();
  const page = await context.newPage();
  const errors = [], calls = [], entries = new Map(), states = new Map();
  let nextEntry = 0, nextGeneration = 0, nextProposal = 0, failGeneration = false, emptyGeneration = false, readOnly = false;
  let releaseRead, delayedRead, releaseSave, delayedSave, notifySaved;
  const coverage = { currentExtractionState: 'Completed', entriesWithoutCompletedExtraction: 0 };
  const candidate = claim => ({ claim, entityKey: 'atlas', entityType: 'Project', sourceQuote: claim, sourceKind: 'Entry' });
  const sourceFor = id => {
    const entry = entries.get(id);
    const fingerprint = createHash('sha256').update(JSON.stringify({ Id: id, TimelineId: entry.timelineId,
      EntryNumber: entry.entryNumber, AuthorId: 'u', Title: entry.title, Content: entry.content })).digest('hex').toUpperCase();
    return { entryId: id, timelineId: entry.timelineId, title: entry.title, content: entry.content, status: entry.status, fingerprint };
  };
  const effective = new Map();
  entries.set('prior-entry', { id: 'prior-entry', timelineId: 't', entryNumber: 0, title: fixtures.prior.title, content: fixtures.prior.content, status: 'Draft', artifacts: [], author: { displayName: 'Offline user' } });
  const priorFact = { ...fixtures.prior.candidate, id: 'old-fact', entryId: 'prior-entry', valid: true, creationReviewId: 'prior-accepted', invalidationReviewId: null, extractionJobId: null, invalidatedByFactId: null };
  effective.set(priorFact.id, priorFact);
  const draft = (id, state, additions = [], extra = {}) => ({ proposalId: id, state, status: state, ready: false, additions, invalidations: [], questions: [], warnings: [], coverage, ...extra });
  const baseState = () => ({ source: null, refreshRequired: false, facts: [], proposals: [], candidates: [candidate('WRONG ROOT CANDIDATE')], processing: [], coverage, truncated: false, lifecycleState: 'NotGenerated', generationId: null, canEdit: true, canGenerate: true, canRegenerate: false });
  states.set('prior-entry', { ...baseState(), source: sourceFor('prior-entry'), lifecycleState: 'Reconciled', canEdit: false, canGenerate: false, facts: [priorFact],
    proposals: [draft('prior-accepted', 'Accepted', [fixtures.prior.candidate], { sourceFingerprint: sourceFor('prior-entry').fingerprint })] });
  const sourcePath = id => `/projects/p/timelines/t/entries/${id}`;
  const origin = 'http://127.0.0.1:5179';
  page.on('pageerror', e => { errors.push(e.message); console.error('Browser error:', e.message); });
  await context.addInitScript(() => localStorage.setItem('projectTimelineToken', 'offline-synthetic-token'));
  await context.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname, method = request.method();
    if (url.origin !== origin) return route.abort();
    if (!path.startsWith('/api/')) return route.continue();
    calls.push({ path, method, body: request.postData() });
    const respond = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (path === '/api/me') return respond({ id: 'u', email: 'offline@example.test', displayName: 'Offline user', roleCode: readOnly ? 'User' : 'Admin' });
    if (path === '/api/projects/p') return respond({ id: 'p', code: 'P', name: 'Offline project', ownerId: 'owner' });
    if (path === '/api/projects/p/participants') return respond([{ id: 'part', userId: 'u', projectRoleName: 'Reviewer' }]);
    if (path === '/api/projects') return respond([]);
    if (path === '/api/timelines/t') return respond({ id: 't', projectId: 'p', code: 'T', name: 'Offline timeline' });
    if (path === '/api/timelines/t/entries' && method === 'POST') {
      const id = `e${++nextEntry}`, body = request.postDataJSON();
      const entry = { ...body, id, timelineId: 't', entryNumber: nextEntry, author: { displayName: 'Offline user' }, artifacts: [] };
      entries.set(id, entry); states.set(id, { ...baseState(), source: sourceFor(id) });
      if (delayedSave) { const wait = delayedSave; delayedSave = null; notifySaved(); await wait; }
      return respond(entry);
    }
    const match = path.match(/^\/api\/entries\/([^/]+)(.*)$/);
    if (!match) { errors.push(`Unexpected API ${method} ${path}`); return respond({}, 404); }
    const [, id, tail] = match, state = states.get(id);
    if (!tail && method === 'GET') return respond(entries.get(id));
    if (!tail && method === 'PUT') {
      assert.equal(state.canEdit, true, 'source cannot be saved after acceptance');
      Object.assign(entries.get(id), request.postDataJSON());
      state.proposals.forEach(p => { p.state = 'Superseded'; });
      Object.assign(state, { source: sourceFor(id), refreshRequired: false, lifecycleState: 'NotGenerated', generationId: null, canRegenerate: false });
      return respond(entries.get(id));
    }
    if (tail === '/facts') {
      const snapshot = structuredClone(state);
      if (snapshot.proposals.length > 50) {
        const generated = snapshot.proposals.find(p => p.proposalId === snapshot.generationId && p.state === 'Generated');
        snapshot.proposals = snapshot.proposals.filter(p => p.proposalId !== generated?.proposalId).slice(0, generated ? 49 : 50).concat(generated ? [generated] : []);
        snapshot.truncated = true;
      }
      if (delayedRead) { const wait = delayedRead; delayedRead = null; await wait; }
      if (readOnly) Object.assign(snapshot, { canEdit: false, canGenerate: false, canRegenerate: false });
      return respond(snapshot);
    }
    if (tail === '/fact-generation') {
      const body = request.postDataJSON();
      if (body.regenerate) assert.deepEqual(body, { regenerate: true, expectedGenerationId: state.generationId });
      else assert.deepEqual(body, {});
      assert.equal(readOnly, false);
      state.proposals.forEach(p => { p.state = 'Superseded'; });
      state.generationId = `g${++nextGeneration}`; state.canRegenerate = true;
      if (failGeneration) {
        failGeneration = false; state.lifecycleState = 'Failed';
        state.processing = [{ id: state.generationId, kind: 'Extraction', state: 'Failed', attempts: 1, errorCode: 'Unavailable' }];
        return respond({ detail: 'untrusted provider secret must not appear' }, 502);
      }
      const saved = entries.get(id);
      const fixture = [fixtures.initial, fixtures.updated].find(x => x.content === saved.content);
      const additions = emptyGeneration ? [] : [fixture?.candidate ?? candidate(saved.content)];
      for (const addition of additions) {
        assert.equal(addition.sourceKind, 'Entry'); assert.ok(addition.entityKey && addition.entityType);
        assert.ok(saved.title.includes(addition.sourceQuote) || saved.content.includes(addition.sourceQuote));
      }
      state.source = sourceFor(id);
      state.lifecycleState = emptyGeneration ? 'Empty' : 'Pending'; emptyGeneration = false;
      state.proposals.unshift(draft(state.generationId, 'Generated', additions, { sourceFingerprint: state.source.fingerprint }));
      return respond(state);
    }
    if (tail === '/fact-proposals') {
      const body = request.postDataJSON(), generated = state.proposals.find(p => p.proposalId === state.generationId && p.state === 'Generated');
      assert.ok(generated, 'reconcile current stored generation only');
      state.proposals.filter(p => p.state === 'Pending').forEach(p => { p.state = 'Superseded'; });
      const answered = body.previousProposalId != null;
      if (answered) assert.deepEqual(body.answers, [{ questionIndex: 0, answer: 'Use the corrected source.' }]);
      else assert.deepEqual(body, { previousProposalId: null, answers: [] });
      assert.equal(generated.sourceFingerprint, state.source.fingerprint);
      const empty = generated.additions.length === 0, proposalId = `r${++nextProposal}`;
      const target = effective.get(priorFact.id);
      if (!empty) {
        assert.ok(target?.valid && target.creationReviewId === 'prior-accepted', 'invalidation target must be effective with prior accepted evidence');
        assert.notEqual(target.entryId, id);
        assert.equal(states.get(target.entryId).proposals[0].state, 'Accepted');
        assert.ok(entries.get(target.entryId).content.includes(target.sourceQuote));
      }
      const result = draft(proposalId, 'Pending', generated.additions, {
        sourceFingerprint: state.source.fingerprint, ready: answered || empty,
        questions: empty ? [] : [{ question: fixtures.clarification.question, answer: answered ? body.answers[0].answer : null }],
        invalidations: empty ? [] : [{ factId: target.id, claim: target.claim, sourceQuote: target.sourceQuote, sourceKind: target.sourceKind,
          reason: 'Explicit correction', invalidatingAdditionId: `addition-${proposalId}-0`, invalidatingAdditionIndex: 0, invalidatingClaim: generated.additions[0].claim }]
      });
      state.proposals.unshift(result); return respond(result);
    }
    if (/^\/fact-proposals\/[^/]+\/accept$/.test(tail)) {
      assert.equal(request.postData(), null, 'acceptance is bodyless');
      const proposalId = tail.split('/')[2], proposal = state.proposals.find(p => p.proposalId === proposalId);
      assert.equal(proposal.state, 'Pending'); assert.equal(proposal.ready, true);
      for (const [index, addition] of proposal.additions.entries()) {
        const fact = { ...addition, id: `addition-${proposalId}-${index}`, entryId: id, valid: true, creationReviewId: proposalId, invalidationReviewId: null, extractionJobId: null, invalidatedByFactId: null };
        effective.set(fact.id, fact); state.facts.push(fact);
      }
      for (const invalidation of proposal.invalidations) {
        const target = effective.get(invalidation.factId), replacement = effective.get(invalidation.invalidatingAdditionId);
        assert.ok(target?.valid && replacement?.valid); assert.equal(replacement.claim, invalidation.invalidatingClaim);
        Object.assign(target, { valid: false, invalidationReviewId: proposalId, invalidatedByFactId: replacement.id });
      }
      proposal.state = 'Accepted'; proposal.status = 'Accepted'; proposal.ready = false;
      Object.assign(state, { lifecycleState: 'Reconciled', canEdit: false, canGenerate: false, canRegenerate: false });
      return respond(proposal);
    }
    errors.push(`Unexpected API ${method} ${path}`); return respond({}, 404);
  });
  const visible = async text => { await page.getByText(text, { exact: true }).first().waitFor(); };
  const button = name => page.getByRole('button', { name, exact: true });
  const countGenerations = () => calls.filter(c => c.path.endsWith('/fact-generation')).length;
  const create = async (title, content) => {
    await page.goto(`${origin}/projects/p/timelines/t/entries/new`);
    await page.getByLabel('Title', { exact: true }).fill(title);
    await page.getByLabel('Content', { exact: true }).fill(content);
    // Two synchronous submissions exercise the handler lock, not just disabled UI.
    const expectedEntry = nextEntry + 1;
    await page.getByRole('form', { name: 'Create timeline entry', exact: true }).evaluate(form => { form.requestSubmit(); form.requestSubmit(); });
    await page.waitForURL(new RegExp(`/entries/e${expectedEntry}$`));
  };
  await create('Atlas source', 'Atlas capacity is 20 units.');
  await visible('Atlas capacity is 20 units.');
  await page.getByRole('region', { name: 'Current generated candidates', exact: true }).getByText('Atlas capacity is 20 units.', { exact: true }).first().waitFor();
  assert.equal(nextEntry, 1); assert.equal(countGenerations(), 1);
  assert.equal(await page.getByText('WRONG ROOT CANDIDATE', { exact: true }).count(), 0);
  assert.equal(await button('Accept fact proposal').count(), 0, 'raw Generated draft cannot be accepted');
  await page.reload(); await visible('Pending'); assert.equal(countGenerations(), 1, 'reload must not regenerate');
  await page.waitForFunction(() => [...document.querySelectorAll('a')].some(a => a.textContent === 'Edit source' && a.getAttribute('aria-disabled') === 'false'));
  await page.getByRole('link', { name: 'Edit source', exact: true }).click();
  await page.waitForURL(`${origin}${sourcePath('e1')}/edit`);
  await page.locator('#edit-entry-content').fill('Atlas capacity is 25 units.');
  assert.equal(await button('Regenerate pending facts').isDisabled(), true, 'dirty source blocks fact mutation');
  assert.equal(await button('Reconcile current generation').isDisabled(), true);
  await button('Refresh fact state').click();
  assert.equal(await page.locator('#edit-entry-content').inputValue(), 'Atlas capacity is 25 units.', 'read refresh retains dirty editor');
  await button('Save entry').click(); await page.waitForURL(`${origin}${sourcePath('e1')}`);
  await page.getByRole('region', { name: 'Current generated candidates', exact: true }).getByText('Atlas capacity is 25 units.', { exact: true }).first().waitFor();
  assert.equal(countGenerations(), 2, 'update auto-generates exactly once');
  await button('Regenerate pending facts').evaluate(element => { element.click(); element.click(); });
  await visible('Generation: g3'); assert.equal(countGenerations(), 3);
  const historyState = states.get('e1');
  historyState.proposals.unshift(...Array.from({ length: 55 }, (_, i) => draft(`historical-${i}`, 'Superseded', [], { sourceFingerprint: historyState.source.fingerprint })));
  await button('Refresh fact state').click();
  await visible('Fact/proposal/processing lists are truncated.');
  assert.equal(await button('Reconcile current generation').isDisabled(), false, 'pinned generation remains actionable beyond the history cap');
  await button('Reconcile current generation').click();
  await visible('Facts to ADD'); await visible('Facts to INVALIDATE');
  assert.equal(await button('Accept fact proposal').isDisabled(), true, 'questions are not ready');
  await page.getByLabel('Which source should be used?', { exact: true }).fill('Use the corrected source.');
  await button('Submit answers for next proposal').click();
  await page.waitForFunction(() => ![...document.querySelectorAll('button')].find(b => b.textContent === 'Accept fact proposal')?.disabled);
  await button('Accept fact proposal').click(); await visible('Reconciled');
  assert.equal(priorFact.valid, false);
  const replacement = effective.get(priorFact.invalidatedByFactId);
  assert.equal(replacement.claim, fixtures.updated.candidate.claim);
  assert.equal(replacement.entryId, 'e1'); assert.notEqual(replacement.creationReviewId, priorFact.creationReviewId);
  assert.equal(entries.get('prior-entry').content, fixtures.prior.content);
  assert.equal(await page.getByRole('link', { name: 'Edit source', exact: true }).getAttribute('aria-disabled'), 'true');
  await page.getByRole('link', { name: 'New correction entry', exact: true }).waitFor();
  // Unknown edit permission is not proof editable; then accepted state remains locked.
  delayedRead = new Promise(resolve => { releaseRead = resolve; });
  await page.goto(`${origin}${sourcePath('e1')}/edit`);
  await page.locator('#edit-entry-content').waitFor();
  assert.equal(await page.locator('#edit-entry-content').isDisabled(), true);
  releaseRead(); await visible('Reconciled');
  assert.equal(await button('Save entry').isDisabled(), true);
  console.log('PASS create/double-submit -> candidates -> reload read-only -> dirty edit/save -> regenerate CAS -> bounded-history questions/answers/preview -> bodyless accept with seeded cause -> locked source');

  failGeneration = true;
  await create('Provider failure source', 'Saved even if the provider is down.');
  await visible('Failed');
  await page.getByRole('alert').filter({ hasText: 'Entry saved. Fact provider unavailable.' }).waitFor();
  assert.equal(await page.getByText(/untrusted provider secret/).count(), 0);
  const failuresCount = countGenerations();
  await page.reload(); await visible('Failed'); assert.equal(countGenerations(), failuresCount);
  emptyGeneration = true; await button('Regenerate pending facts').click(); await visible('Empty');
  await visible('No candidates in this completed generation.');
  await page.waitForFunction(() => [...document.querySelectorAll('a')].some(a => a.textContent === 'Edit source' && a.getAttribute('aria-disabled') === 'false'));
  await page.getByRole('link', { name: 'Edit source', exact: true }).click();
  await page.locator('#edit-entry-content').waitFor();
  await button('Reconcile current generation').click();
  await page.waitForFunction(() => ![...document.querySelectorAll('button')].find(b => b.textContent === 'Accept fact proposal')?.disabled);
  await button('Accept fact proposal').click(); await visible('Reconciled');
  assert.equal(await page.locator('#edit-entry-content').isDisabled(), true, 'empty accepted evidence immediately locks the mounted editor');
  assert.equal(await button('Save entry').isDisabled(), true);
  console.log('PASS provider failure preserves navigable source/error -> reload no POST -> empty generation -> explicit empty reconciliation/accept -> lock');

  // Leave a screen after persistence but before its response: no late generation
  // or navigation may be launched by the abandoned save action.
  await page.goto(`${origin}/projects/p/timelines/t/entries/new`);
  await page.getByLabel('Title', { exact: true }).fill('Abandoned saved source');
  await page.getByLabel('Content', { exact: true }).fill('Persisted before leaving the screen.');
  delayedSave = new Promise(resolve => { releaseSave = resolve; });
  const persisted = new Promise(resolve => { notifySaved = resolve; });
  const generationCountBeforeLeaving = countGenerations();
  await button('Create entry').click(); await persisted;
  await page.goto(`${origin}${sourcePath('e1')}`); await visible('Reconciled');
  releaseSave();
  await page.goto(`${origin}${sourcePath('e3')}`); await visible('NotGenerated');
  await visible('Abandoned saved source');
  assert.equal(countGenerations(), generationCountBeforeLeaving);
  console.log('PASS abandoned delayed save remains navigable; no late generation/navigation');

  // A second real tab saves/regenerates while the first tab stays mounted.
  // The API is still offline/mocked; candidate data contracts are replayed in .NET tests.
  const otherTab = await context.newPage();
  otherTab.on('pageerror', error => errors.push(error.message));
  const remoteEdit = async (title, content) => {
    const before = countGenerations();
    await otherTab.goto(`${origin}${sourcePath('e3')}/edit`);
    await otherTab.locator('#edit-entry-title').fill(title);
    await otherTab.locator('#edit-entry-content').fill(content);
    await otherTab.getByRole('button', { name: 'Save entry', exact: true }).click();
    await otherTab.waitForURL(`${origin}${sourcePath('e3')}`);
    await otherTab.getByRole('region', { name: 'Current generated candidates', exact: true }).getByText(content, { exact: true }).first().waitFor();
    assert.equal(countGenerations(), before + 1, 'the other tab generates exactly once from its save');
  };
  await remoteEdit('Source edited in another tab', 'Atlas capacity is 30 units.');
  await button('Refresh fact state').click();
  await page.getByRole('region', { name: 'Current generated candidates', exact: true }).getByText('Atlas capacity is 30 units.', { exact: true }).first().waitFor();
  await page.getByText('Original saved source', { exact: true }).click();
  assert.equal(await page.locator('pre.entry-content').textContent(), 'Atlas capacity is 30 units.', 'new evidence must not be shown beside the old saved source');
  assert.equal(await page.getByRole('heading', { level: 1 }).textContent(), 'Source edited in another tab');
  assert.equal(await page.getByText('Persisted before leaving the screen.', { exact: true }).count(), 0);
  await page.getByRole('link', { name: 'Edit source', exact: true }).click();
  await page.locator('#edit-entry-content').fill('Unsaved local correction.');
  await remoteEdit('Source changed again elsewhere', 'Atlas capacity is 35 units.');
  await otherTab.close();
  await button('Refresh fact state').click();
  await page.getByRole('region', { name: 'Current generated candidates', exact: true }).getByText('Atlas capacity is 35 units.', { exact: true }).first().waitFor();
  assert.equal(await page.locator('pre.entry-content').textContent(), 'Atlas capacity is 35 units.');
  assert.equal(await page.locator('#edit-entry-content').inputValue(), 'Unsaved local correction.', 'remote source refresh must not clobber a dirty draft');
  assert.equal(await button('Save entry').isDisabled(), true, 'stale editor must not overwrite a newer saved source');
  await button('Reload saved source (discard draft)').click();
  assert.equal(await page.locator('#edit-entry-content').inputValue(), 'Atlas capacity is 35 units.');
  states.get('e3').refreshRequired = true;
  await button('Refresh fact state').click();
  await page.getByText('Saved source or fact evidence changed during the read. Refresh before reconciling.', { exact: true }).waitFor();
  assert.equal(await button('Reconcile current generation').isDisabled(), true);
  states.get('e3').refreshRequired = false;
  states.get('e3').proposals[0].sourceFingerprint = 'F'.repeat(64);
  await button('Refresh fact state').click();
  assert.equal(await button('Reconcile current generation').isDisabled(), true, 'fingerprint mismatch must fail closed even without a refresh flag');
  assert.equal(countGenerations(), generationCountBeforeLeaving + 2, 'only the two explicit second-tab saves generate; first-tab refresh remains GET-only');
  console.log('PASS cross-session source/candidate refresh, dirty-draft preservation and mixed-source reconciliation gate');

  // Both retry/backoff and loading are read-only states on reload.
  for (const lifecycleState of ['Retry', 'Loading']) {
    Object.assign(states.get('e2'), { lifecycleState, canEdit: true, canGenerate: true, canRegenerate: true,
      processing: [{ id: 'retry', kind: 'Extraction', state: lifecycleState, attempts: 2, errorCode: 'RateLimited', nextAttemptAtUtc: '2030-01-01T00:00:00Z' }] });
    await page.goto(`${origin}${sourcePath('e2')}`); await visible(lifecycleState);
    assert.equal(await button('Reconcile current generation').isDisabled(), true);
    assert.equal(countGenerations(), failuresCount + 3);
  }
  readOnly = true; await page.reload(); await visible('Read-only fact access.');
  assert.equal(await button('Regenerate pending facts').isDisabled(), true);
  assert.equal(await page.getByRole('link', { name: 'Edit source', exact: true }).getAttribute('aria-disabled'), 'true');
  assert.deepEqual(errors, []);
  console.log(`PASS retry/backoff/loading and read-only authorization; ${calls.length} mocked API requests; no browser errors; no live backend/model/database calls`);
} finally {
  await browser?.close(); await server.close();
}
