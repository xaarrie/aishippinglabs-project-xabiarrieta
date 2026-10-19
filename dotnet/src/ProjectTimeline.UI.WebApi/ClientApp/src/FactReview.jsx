import React, { useEffect, useRef, useState } from 'react';
import { api } from './api.js';
import { canAcceptFactProposal, canAcceptCurrentFactProposal, createFactReviewClient, createFactRequestFence, currentFactGeneration, currentFactReconciliation, savedFactSource, factGenerationMatchesSource, factReviewActions, factReviewError, factInvalidationText, proposalInvalidationText } from './factReview.js';

const client = createFactReviewClient(api);
const lifecycleText = {
  NotGenerated: 'Not generated. Save or explicitly generate pending candidates.',
  Loading: 'Generation in progress. Candidates are not active facts.',
  Retry: 'Generation is waiting for retry/backoff. Check processing details or explicitly regenerate.',
  Failed: 'Generation failed. The saved entry is retained; explicitly regenerate to try again.',
  Empty: 'Generation completed with no candidates. Nothing has been activated. Reconcile explicitly even for empty evidence.',
  Pending: 'Pending candidates. Nothing is active until you reconcile and explicitly accept.',
  Reconciled: 'Reconciliation accepted. Source editing is locked, including empty accepted evidence. Corrections require a new entry.'
};

export default function FactReview({ entry, canWrite, dirty = false, onActiveChange = () => {}, onStateChange = () => {} }) {
  const [state, setState] = useState(null);
  const [proposal, setProposal] = useState(null);
  const [answers, setAnswers] = useState({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [conflict, setConflict] = useState(false);
  const selected = useRef(null);
  const lock = useRef(false);
  const [fence] = useState(createFactRequestFence);
  const callbacks = useRef({ onActiveChange, onStateChange });
  callbacks.current = { onActiveChange, onStateChange };

  const applyState = response => {
    const value = savedFactSource(response, entry.id) ? response : { ...response, source: null, refreshRequired: true };
    setState(value); callbacks.current.onStateChange(value);
    const next = currentFactReconciliation(value);
    if (selected.current !== next?.proposalId) setAnswers({});
    selected.current = next?.proposalId ?? null; setProposal(next);
  };
  const refresh = async (explicit = false) => {
    if (lock.current) return;
    const request = fence.begin();
    try {
      const value = await client.read(entry.id, { signal: request.signal });
      if (fence.isCurrent(request)) { applyState(value); if (explicit) { setError(''); setConflict(false); } }
    } catch (e) { if (fence.isCurrent(request)) setError(factReviewError(e)); }
  };
  useEffect(() => {
    lock.current = false; setBusy(false);
    refresh();
    // Reads only. Reloads, dirty-form changes and remounts never incur generation.
    const timer = setInterval(() => refresh(), 8000);
    return () => { fence.cancel(); clearInterval(timer); callbacks.current.onActiveChange(false); };
  }, [entry.id, dirty]);

  const actions = factReviewActions(state, { canWrite, dirty, busy, conflict });
  const run = async action => {
    if (lock.current || !canWrite || dirty) return;
    if (action === 'accept' ? !actions.reconcile || !canAcceptCurrentFactProposal(state, proposal)
      : action === 'answers' ? !actions.reconcile || proposal?.state !== 'Pending'
      : !actions[action]) return;
    const request = fence.begin(), signal = request.signal;
    lock.current = true; setBusy(true); callbacks.current.onActiveChange(true); setError('');
    try {
      if (action === 'generate' || action === 'regenerate') {
        const value = action === 'regenerate'
          ? await client.regenerate(entry.id, state.generationId, { signal })
          : await client.generate(entry.id, { signal });
        if (fence.isCurrent(request)) { applyState(value); setAnswers({}); setConflict(false); }
      } else {
        let result;
        if (action === 'accept') result = await client.accept(entry.id, proposal.proposalId, { signal });
        else {
          const indexedAnswers = Object.entries(answers).filter(([, value]) => value.trim()).map(([index, answer]) => ({ questionIndex: Number(index), answer }));
          result = await client.plan(entry.id, action === 'answers' ? proposal.proposalId : null, action === 'answers' ? indexedAnswers : [], { signal });
        }
        if (!fence.isCurrent(request)) return;
        selected.current = result.proposalId; setProposal(result); setAnswers({}); setConflict(false);
        // Acceptance must lock the editor even if the following refresh fails.
        if (action === 'accept') applyState({ ...state, lifecycleState: 'Reconciled', canEdit: false, canGenerate: false, canRegenerate: false, proposals: [result] });
        const value = await client.read(entry.id, { signal });
        if (fence.isCurrent(request)) applyState(value);
      }
    } catch (e) {
      if (fence.isCurrent(request)) { setError(factReviewError(e)); if (e.status === 409) setConflict(true); }
    } finally {
      if (fence.isCurrent(request)) { lock.current = false; setBusy(false); callbacks.current.onActiveChange(false); }
    }
  };
  const source = savedFactSource(state, entry.id);
  const generated = factGenerationMatchesSource(state) ? currentFactGeneration(state) : null;
  const previewMatchesSource = source && !state.refreshRequired && proposal?.sourceFingerprint === source.fingerprint;
  return <section className="card" aria-label="Fact review" aria-busy={busy}>
    <h3>Review entry facts</h3>
    <p>Generated candidates are proposals, not active facts. Reconcile the current generation, answer questions, review additions and invalidations, then explicitly accept. Original title and content are never rewritten by fact review.</p>
    <details><summary>Original saved source</summary>{source ? <><h4>{source.title}</h4><pre className="entry-content">{source.content}</pre><small>Source identity: {source.fingerprint}</small></> : <p>Loading authoritative saved source...</p>}</details>
    {dirty && <p role="status">Save or discard editor changes before reviewing the saved source. Your unsaved draft is retained.</p>}
    {!canWrite && <p role="status">Read-only fact access.</p>}
    {busy && <p role="status">Processing fact request. No new acceptance is implied.</p>}
    {!state && <p role="status">Loading fact lifecycle and source permissions...</p>}
    {state?.refreshRequired && <p role="alert">Saved source or fact evidence changed during the read. Refresh before reconciling.</p>}
    {state && !state.refreshRequired && state.generationId && ['Pending', 'Empty'].includes(state.lifecycleState) && !generated && <p role="alert">Current generation does not match the saved source identity. Refresh fact state before reconciling.</p>}
    {state && <p role="status"><strong>{state.lifecycleState}</strong>: {lifecycleText[state.lifecycleState] || 'Unknown state. Refresh before taking action.'}</p>}
    <div className="action-bar">
      <button type="button" className="secondary" disabled={!actions.generate} onClick={() => run('generate')}>Generate pending facts</button>
      <button type="button" className="secondary" disabled={!actions.regenerate} onClick={() => run('regenerate')}>Regenerate pending facts</button>
      <button type="button" className="secondary" disabled={!actions.reconcile} onClick={() => run('reconcile')}>Reconcile current generation</button>
      <button type="button" className="secondary" disabled={busy} onClick={() => refresh(true)}>Refresh fact state</button>
    </div>
    <p className="muted">Generation and reconciliation may incur model billing. Regeneration supersedes the current draft; it never activates facts.</p>
    {error && <p role="alert" className="request-feedback request-error">{error}</p>}
    {state && <>
      <section aria-label="Current generated candidates"><h4>{state.lifecycleState === 'Reconciled' ? 'Generated evidence (read-only history)' : 'Current generated candidates (pending, not active)'}</h4>
        {generated ? <><small>Generation: {state.generationId}</small><ul>{generated.additions.map((fact, i) => <li key={i}><strong>{fact.claim}</strong><blockquote>{fact.sourceQuote}</blockquote><small>Evidence: {fact.sourceKind}</small></li>)}</ul>{!generated.additions.length && <p>No candidates in this completed generation.</p>}</> : <p>Current generated evidence is not available in this view. Refresh to confirm processing; history and active facts are not candidates.</p>}
      </section>
      <p role="status">Extraction: {state.coverage.currentExtractionState}. Entries without completed extraction: {state.coverage.entriesWithoutCompletedExtraction}.</p>
      {state.coverage.entriesWithoutCompletedExtraction > 0 && <p role="status">Incomplete history. Explicit backfill may be needed; absence of results does not prove absence of conflicts.</p>}
      {state.truncated && <p role="status">Fact/proposal/processing lists are truncated.</p>}
      <details><summary>Processing state</summary><ul>{state.processing.map(job => <li key={job.id}>{job.kind}: {job.state} (attempts {job.attempts}) {job.errorCode}{job.nextAttemptAtUtc && <> · Next attempt: {job.nextAttemptAtUtc}</>}</li>)}</ul></details>
      <details><summary>Stored facts ({state.facts.length})</summary><ul>{state.facts.map(fact => <li key={fact.id}>
        {fact.valid ? 'Active' : 'Invalidated'}: {fact.claim}<blockquote>{fact.sourceQuote}</blockquote>
        {!fact.valid && <p>{factInvalidationText(fact, state.facts)}</p>}
      </li>)}</ul></details>
      <details><summary>Proposal history (read-only)</summary><ul>{state.proposals.map(item => <li key={item.proposalId}>{item.state}: {item.proposalId}</li>)}</ul></details>
    </>}
    {proposal && previewMatchesSource && <section aria-label="Reconciliation preview">
      <h4>{proposal.state}: {proposal.status}</h4>
      {proposal.warnings.map((warning, i) => <p role="status" key={i}>{warning}</p>)}
      <h4>Facts to ADD</h4><ul>{proposal.additions.map((fact, i) => <li key={i}><strong>Addition {i + 1}: {fact.claim}</strong><blockquote>{fact.sourceQuote}</blockquote><small>Evidence: {fact.sourceKind}</small></li>)}</ul>
      <h4>Facts to INVALIDATE</h4><ul>{proposal.invalidations.map(fact => <li key={fact.factId}>
        <strong>{fact.claim}</strong><blockquote>{fact.sourceQuote}</blockquote><p>Reason: {fact.reason}</p><p>{proposalInvalidationText(fact)}</p>
      </li>)}</ul>
      {proposal.questions.map((question, i) => <label key={`${proposal.proposalId}-${i}`}>{question.question}<textarea maxLength={4000} disabled={!actions.reconcile || proposal.state !== 'Pending' || question.answer != null} value={question.answer ?? answers[i] ?? ''} onChange={event => setAnswers({ ...answers, [i]: event.target.value })} /></label>)}
      <div className="action-bar">
        {proposal.state === 'Pending' && proposal.questions.length > 0 && <button type="button" disabled={!actions.reconcile || !Object.values(answers).some(x => x.trim())} onClick={() => run('answers')}>Submit answers for next proposal</button>}
        <button type="button" className="primary" disabled={busy || !canWrite || dirty || conflict || !canAcceptFactProposal(proposal) || !canAcceptCurrentFactProposal(state, proposal)} onClick={() => run('accept')}>Accept fact proposal</button>
      </div>
      {proposal.state === 'Accepted' && <p role="status">Facts accepted by the server. Original entry and title are unchanged. Corrections require a new entry.</p>}
    </section>}
  </section>;
}
