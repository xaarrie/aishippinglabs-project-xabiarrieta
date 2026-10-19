import React, { useEffect, useRef, useState } from 'react';
import { api } from './api.js';
import { canAcceptFactProposal, createFactReviewClient, factReviewError, factInvalidationText, proposalInvalidationText } from './factReview.js';

const client = createFactReviewClient(api);

export default function FactReview({ entry, canWrite, dirty, onActiveChange }) {
  const [state, setState] = useState(null);
  const [proposal, setProposal] = useState(null);
  const [answers, setAnswers] = useState({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [conflict, setConflict] = useState(false);
  const selected = useRef(null);
  const lock = useRef(false);
  const generation = useRef(0);
  const lifetime = useRef(null);

  const applyState = value => {
    setState(value);
    const current = value.proposals.find(x => x.proposalId === selected.current);
    const next = current || (!selected.current ? value.proposals.find(x => x.state === 'Pending') || value.proposals[0] : null);
    if (next) { selected.current = next.proposalId; setProposal(next); }
  };
  useEffect(() => {
    const controller = new AbortController(); lifetime.current = controller;
    const refresh = async () => {
      if (lock.current) return;
      const requestGeneration = ++generation.current;
      try { const value = await client.read(entry.id, { signal: controller.signal }); if (!controller.signal.aborted && !lock.current && generation.current === requestGeneration) applyState(value); }
      catch (e) { if (!controller.signal.aborted) setError(factReviewError(e)); }
    };
    refresh();
    const timer = setInterval(refresh, 8000);
    return () => { controller.abort(); clearInterval(timer); onActiveChange(false); };
  }, [entry.id]);

  const run = async (action, regenerate = false) => {
    if (lock.current || !canWrite || dirty) return;
    ++generation.current;
    lock.current = true; setBusy(true); onActiveChange(true); setError('');
    const signal = lifetime.current.signal;
    try {
      let result;
      if (action === 'accept') {
        if (!canAcceptFactProposal(proposal) || conflict) return;
        result = await client.accept(entry.id, proposal.proposalId, { signal });
      } else {
        const indexedAnswers = Object.entries(answers).filter(([, value]) => value.trim()).map(([index, answer]) => ({ questionIndex: Number(index), answer }));
        result = await client.plan(entry.id, regenerate ? null : proposal?.proposalId ?? null, regenerate ? [] : indexedAnswers, { signal });
      }
      if (signal.aborted) return;
      selected.current = result.proposalId ?? 'no-changes'; setProposal(result); setAnswers({}); setConflict(false);
      const value = await client.read(entry.id, { signal });
      if (!signal.aborted) applyState(value);
    } catch (e) {
      if (!signal.aborted) { setError(factReviewError(e)); if (e.status === 409) setConflict(true); }
    } finally {
      lock.current = false;
      if (!signal.aborted) { setBusy(false); onActiveChange(false); }
    }
  };

  return <section className="card" aria-label="Fact review" aria-busy={busy}>
    <h3>Review entry facts</h3>
    <p>Review adds or invalidates atomic facts only. Original title and content are never rewritten. Facts stay pending until you explicitly accept a ready proposal.</p>
    <details><summary>Original saved source</summary><h4>{entry.title}</h4><pre className="entry-content">{entry.content}</pre></details>
    {dirty && <p role="status">Save or discard editor changes before reviewing the saved source. Evidence-locked source cannot be rewritten.</p>}
    {!canWrite && <p role="status">Read-only fact access.</p>}
    <div className="action-bar">
      <button type="button" className="secondary" disabled={!canWrite || dirty || busy} onClick={() => run('plan', true)}>Generate new fact proposal</button>
    </div>
    {error && <p role="alert" className="request-feedback request-error">{error}</p>}
    {state && <>
      <p role="status">Extraction: {state.coverage.currentExtractionState}. Entries without completed extraction: {state.coverage.entriesWithoutCompletedExtraction}.</p>
      {state.coverage.entriesWithoutCompletedExtraction > 0 && <p role="status">Incomplete history. Explicit backfill may be needed; absence of results does not prove absence of conflicts.</p>}
      {state.truncated && <p role="status">Fact/proposal/processing lists are truncated.</p>}
      <details><summary>Processing state</summary><ul>{state.processing.map(job => <li key={job.id}>{job.kind}: {job.state} (attempts {job.attempts}) {job.errorCode}</li>)}</ul></details>
      <details><summary>Stored facts ({state.facts.length})</summary><ul>{state.facts.map(fact => <li key={fact.id}>
        {fact.valid ? 'Active' : 'Invalidated'}: {fact.claim}
        <blockquote>{fact.sourceQuote}</blockquote>
        {!fact.valid && <p>{factInvalidationText(fact, state.facts)}</p>}
      </li>)}</ul></details>
      {state.proposals.length > 0 && <label>Stored proposal<select disabled={busy} value={proposal?.proposalId || ''} onChange={event => {
        const next = state.proposals.find(x => x.proposalId === event.target.value);
        selected.current = next.proposalId; setProposal(next); setAnswers({}); setConflict(false);
      }}><option value="" disabled>Select proposal</option>{state.proposals.map(item => <option key={item.proposalId} value={item.proposalId}>{item.state}: {item.proposalId}</option>)}</select></label>}
    </>}
    {proposal && <>
      <h4>{proposal.state}: {proposal.status}</h4>
      {proposal.warnings.map((warning, i) => <p role="status" key={i}>{warning}</p>)}
      <h4>Facts to ADD</h4><ul>{proposal.additions.map((fact, i) => <li key={i}><strong>Addition {i + 1}: {fact.claim}</strong><blockquote>{fact.sourceQuote}</blockquote><small>Evidence: {fact.sourceKind}</small></li>)}</ul>
      <h4>Facts to INVALIDATE</h4><ul>{proposal.invalidations.map(fact => <li key={fact.factId}>
        <strong>{fact.claim}</strong><blockquote>{fact.sourceQuote}</blockquote>
        <p>Reason: {fact.reason}</p>
        <p>{proposalInvalidationText(fact)}</p>
      </li>)}</ul>
      {proposal.questions.map((question, i) => <label key={`${proposal.proposalId}-${i}`}>{question.question}<textarea maxLength={4000} disabled={busy || !canWrite || proposal.state !== 'Pending' || question.answer != null} value={question.answer ?? answers[i] ?? ''} onChange={event => setAnswers({ ...answers, [i]: event.target.value })} /></label>)}
      <div className="action-bar">
        {proposal.state === 'Pending' && proposal.questions.length > 0 && <button type="button" disabled={busy || !canWrite || dirty || conflict || !Object.values(answers).some(x => x.trim())} onClick={() => run('plan')}>Submit answers for next proposal</button>}
        <button type="button" className="primary" disabled={busy || !canWrite || dirty || conflict || !canAcceptFactProposal(proposal)} onClick={() => run('accept')}>Accept fact proposal</button>
      </div>
      {proposal.state === 'Accepted' && <p role="status">Facts accepted by the server. Original entry and title are unchanged.</p>}
    </>}
  </section>;
}
