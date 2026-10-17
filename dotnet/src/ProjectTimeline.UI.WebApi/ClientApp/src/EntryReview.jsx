import React, { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { api } from './api.js';
import { auditCoverage, auditHasIssues, auditMatches, auditRequest, canReviewEntry, createReviewSession, formatEntryDraft, lineDiff, readableDetails, reviewRequest, reviewSource } from './entryReview.js';
import './EntryReview.css';

const types = ['Contradiction', 'Ambiguity', 'Missing data', 'Duplicate', 'Unknown reference'];
const impacts = ['Blocking', 'High', 'Medium', 'Low'];
const actions = ['Add', 'Update', 'Remove', 'Mark obsolete'];

const failureDetails = error => error.status ? error.message : 'The request could not be completed. Check your connection or contact an administrator.';

function DiffText({ parts, side, label }) {
  return <pre className="review-diff-text" tabIndex={0} aria-label={label}>{parts.map((part, index) => part.changed
    ? side === 'source' ? <del key={index}>{part.text}</del> : <ins key={index}>{part.text}</ins>
    : <React.Fragment key={index}>{part.text}</React.Fragment>)}</pre>;
}
function Comparison({ source, draft }) {
  const title = useMemo(() => lineDiff(source?.title || '', draft.title), [source?.title, draft.title]);
  const content = useMemo(() => lineDiff(source?.content || '', draft.content), [source?.content, draft.content]);
  if (!source) return <p role="status">Original comparison unavailable. Restart or update the server and run review again; your editor draft is not used as a substitute.</p>;
  return <section className="review-section" aria-label="Original and proposal comparison">
    <h3>Original and proposal — textual differences</h3>
    <p>Legend: <del>Deleted or replaced source text (struck through)</del>; <ins>Added proposal text (underlined)</ins>. Unmarked text is unchanged. This lexical comparison is not proof of semantic loss or preservation.</p>
    {(title.fallback || content.fallback) && <p>Large-input comparison: changed blocks are highlighted in full. No text is omitted.</p>}
    <div className="review-comparison">
      <section aria-label="Original saved source"><h4>Original saved source</h4><p className="review-text muted">Immutable saved source. Entry: {source.entryId}</p><h5>Title</h5><DiffText parts={title.source} side="source" label="Original saved title differences" /><h5>Content</h5><DiffText parts={content.source} side="source" label="Original saved content differences" /></section>
      <section aria-label="Current generated proposal"><h4>Current generated proposal</h4><p className="muted">Reflects the editable preview above.</p><h5>Title</h5><DiffText parts={title.proposal} side="proposal" label="Current proposal title differences" /><h5>Content</h5><DiffText parts={content.proposal} side="proposal" label="Current proposal content differences" /></section>
    </div>
  </section>;
}
function LiteralQuote({ label, quote, verified }) {
  return <div className="review-evidence"><p><strong>{label}</strong>: {verified === true ? 'Literal quote found' : 'Literal quote not verified'}</p><blockquote>{quote || '(No quote supplied)'}</blockquote></div>;
}
function SemanticAudit({ audit, onAudit, disabled }) {
  const data = audit.status === 'ready' ? audit.data : null;
  const coverage = auditCoverage(data);
  return <section className="review-section review-audit" aria-label="Semantic audit" aria-busy={audit.status === 'pending'}>
    <h3>Separate semantic audit</h3>
    <p>This independent inventory estimates detail preservation; it can miss facts. Coverage is not model confidence, a completeness guarantee, or permission to save. Literal quote matches do not verify semantic truth.</p>
    <p className="review-status" role="status">{audit.status === 'pending' ? 'Auditing the exact current proposal… No current score is available.' : audit.message || (coverage === null ? 'Audit inconclusive: estimated coverage unavailable.' : `Estimated coverage (0–1): ${coverage}. Preserved ${data.preservedFacts} / ${data.totalFacts} inventoried facts.`)}</p>
    {data && <>
      {coverage === null && <p>Inventoried facts: {data.preservedFacts ?? 'unknown'} preserved / {data.totalFacts ?? 'unknown'} total.</p>}
      {auditHasIssues(data) && <p className="review-warning">Issues reported — inspect every finding below, even when estimated coverage is 1.</p>}
      <p className="review-text">Limitation: {data.limitation || 'The inventory is model-estimated and may omit source facts.'}</p>
      <h4>Source fact inventory and evidence</h4>
      {!(data.facts || []).length && <p>No usable fact inventory was returned.</p>}
      {(data.facts || []).map((fact, index) => <article className="review-finding" key={index}>
        <h5 className="review-text">{fact.status || 'Uncertain'}: {fact.fact}</h5>
        {fact.reportedStatus && <p>Auditor-reported status: {fact.reportedStatus}. Evidence-adjusted status: {fact.status || 'Uncertain'}.</p>}
        <LiteralQuote label="Source evidence" quote={fact.sourceQuote} verified={fact.sourceQuoteVerified} />
        <LiteralQuote label="Proposal evidence" quote={fact.proposalQuote} verified={fact.proposalQuoteVerified} />
        <p className="review-text">{fact.explanation}</p>{fact.evidenceWarning && <p className="review-warning">Evidence warning: {fact.evidenceWarning}</p>}
      </article>)}
      <h4>Unsupported additions</h4>
      {!(data.unsupportedAdditions || []).length && <p>No unsupported additions reported; this is not a guarantee.</p>}
      {(data.unsupportedAdditions || []).map((addition, index) => <article className="review-finding" key={index}><h5 className="review-text">{addition.finding}</h5><LiteralQuote label="Proposal evidence" quote={addition.proposalQuote} verified={addition.evidenceVerified} /></article>)}
      <details><summary>Audit provenance (not Save approval)</summary><p className="review-text">Source fingerprint: {data.sourceFingerprint}</p><p className="review-text">Proposal fingerprint: {data.proposalFingerprint}</p></details>
    </>}
    <div className="action-bar"><button type="button" className="secondary" onClick={onAudit} disabled={disabled || audit.status === 'pending'}>{data ? 'Re-audit current proposal' : 'Audit current proposal'}</button></div>
    {(audit.status !== 'ready' || coverage === null) && <p className="review-warning">Saving remains your choice. This proposal has no current conclusive audit; do not treat saved output as audited.</p>}
  </section>;
}

// Keyed lifetime clears target-specific state and synchronously cleans up requests
// on navigation, rather than waiting for a delayed target-change effect.
export default function EntryReview(props) { return <EntryReviewSession key={props.entryId} {...props} />; }
function EntryReviewSession({ entryId, canWrite = false, dirty = false, onActiveChange, onSave }) {
  const [active, setActive] = useState(false);
  const [result, setResult] = useState(null);
  const [answers, setAnswers] = useState({});
  const [draft, setDraft] = useState(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [source, setSource] = useState(null);
  const [audit, setAudit] = useState({ status: 'unavailable', message: 'No audit yet.' });
  const prefix = useId();
  const heading = useRef(null);
  const trigger = useRef(null);
  const session = useRef(null);
  if (!session.current) session.current = createReviewSession();
  const requests = useRef({ review: null, audit: null, saving: false });
  const resultRef = useRef(null);
  const answersRef = useRef({});
  const sourceChanged = useRef(false);
  useLayoutEffect(() => () => {
    requests.current.review?.abort(); requests.current.audit?.abort();
    session.current.replace(null, null, null);
  }, []);
  useEffect(() => { if (active) heading.current?.focus(); }, [active]);

  const runAudit = async snapshot => {
    if (!session.current.isCurrent(snapshot)) return;
    const payload = auditRequest(snapshot);
    if (!payload) { setAudit({ status: 'unavailable', message: 'Audit unavailable: source fingerprint missing. Restart or update the server and run review again.' }); return; }
    if (sourceChanged.current) return;
    requests.current.audit?.abort();
    const controller = new AbortController(); requests.current.audit = controller;
    setAudit({ status: 'pending' });
    try {
      const response = await api(`/api/entries/${entryId}/review/audit`, { method: 'POST', body: JSON.stringify(payload), signal: controller.signal });
      if (!session.current.isCurrent(snapshot) || controller.signal.aborted) return;
      if (!auditMatches(snapshot, response)) { setAudit({ status: 'failed', message: 'Audit unavailable: response did not match this entry and source. Run review again.' }); return; }
      setAudit({ status: 'ready', data: response });
    } catch (e) {
      if (!session.current.isCurrent(snapshot) || controller.signal.aborted) return;
      sourceChanged.current = e.status === 409;
      setAudit({ status: sourceChanged.current ? 'source-changed' : 'failed', message: sourceChanged.current ? 'Source changed (409). Run review again before auditing. The proposal and answers are retained.' : `Audit unavailable: ${failureDetails(e)} The proposal is still editable; no automatic retry.` });
    } finally { if (requests.current.audit === controller) requests.current.audit = null; }
  };
  const run = async () => {
    if (requests.current.review || requests.current.saving || !canReviewEntry(entryId, canWrite)) return;
    requests.current.audit?.abort();
    const previous = session.current.snapshot();
    const snapshot = session.current.replace(entryId, previous?.source, previous?.proposal);
    setAudit({ status: 'stale', message: 'New review round requested. Previous audit is no longer current.' });
    setActive(true); onActiveChange?.(true); setBusy(true); setError('');
    const controller = new AbortController(); requests.current.review = controller;
    try {
      const next = await api(`/api/entries/${entryId}/review`, { method: 'POST', body: JSON.stringify(reviewRequest(resultRef.current, answersRef.current)), signal: controller.signal });
      if (!session.current.isCurrent(snapshot) || controller.signal.aborted) return;
      const nextDraft = next.entry ? formatEntryDraft(next.entry) : null;
      const nextSource = reviewSource(entryId, next.source);
      const generated = session.current.replace(entryId, nextSource, nextDraft);
      resultRef.current = next; sourceChanged.current = false;
      setResult(next); setSource(nextSource); setDraft(nextDraft);
      // Event-driven, once per successful generation; never a StrictMode effect.
      if (nextDraft) void runAudit(generated);
      else setAudit({ status: 'unavailable', message: 'No generated entry to audit.' });
    } catch (e) {
      if (session.current.isCurrent(snapshot) && !controller.signal.aborted) setError(`Review failed: ${failureDetails(e)} Your editor draft, proposal and answers are retained; retry or discard.`);
    } finally {
      if (requests.current.review === controller) { requests.current.review = null; setBusy(false); }
    }
  };
  const edit = (field, value) => {
    const previous = session.current.snapshot();
    const next = { ...previous.proposal, [field]: value };
    requests.current.audit?.abort();
    session.current.replace(entryId, previous.source, next);
    setDraft(next);
    setAudit({ status: 'stale', message: sourceChanged.current ? 'Source changed (409). Run review again before auditing.' : 'Audit stale: proposal edited. Audit the current proposal explicitly; no score applies to these edits.' });
  };
  const updateAnswer = (id, value) => {
    answersRef.current = { ...answersRef.current, [id]: value }; setAnswers(answersRef.current);
  };
  const reAudit = () => {
    if (requests.current.review || requests.current.saving || sourceChanged.current) return;
    const previous = session.current.snapshot();
    void runAudit(session.current.replace(entryId, previous.source, previous.proposal));
  };
  const discard = () => {
    requests.current.review?.abort(); requests.current.audit?.abort(); requests.current.review = null;
    session.current.replace(entryId, null, null);
    setActive(false); onActiveChange?.(false); setDraft(null); setSource(null); setError(''); setBusy(false);
    setAudit({ status: 'unavailable', message: 'Review discarded. No current audit.' });
    // Retain questions and answers for a later user-requested round.
    requestAnimationFrame(() => trigger.current?.focus());
  };
  const save = async () => {
    if (requests.current.saving || requests.current.review) return;
    const snapshot = session.current.snapshot();
    requests.current.saving = true; setBusy(true); setError('');
    try { await onSave(snapshot.proposal); }
    catch (e) { if (session.current.isCurrent(snapshot)) setError(`Save failed: ${failureDetails(e)} The preview and answers are retained for retry.`); }
    finally { requests.current.saving = false; if (session.current.isCurrent(snapshot)) setBusy(false); }
  };
  return <section className="entry-review" aria-label="Entry review" aria-busy={busy}>
    <h2 ref={heading} tabIndex={-1}>Review</h2>
    {!active ? <>
      <div className="action-bar"><button ref={trigger} type="button" className="secondary" disabled={!canReviewEntry(entryId, canWrite)} onClick={run}>Review</button></div>
      <p className="muted">{!entryId ? 'Save this new entry before reviewing it.' : !canWrite ? 'Review requires project write permission.' : 'Review analyzes saved content, not unsaved editor changes. Nothing is saved automatically.'}</p>
    </> : <>
      <p className="review-status" role="status">{dirty ? 'You have unsaved editor changes. Review uses the saved entry, not those changes. Discard restores your editor draft unchanged; Save reviewed entry replaces its title and content with this preview.' : 'Review uses the saved entry. Discard leaves the editor unchanged.'} Entry status is preserved. Digest proposals are never saved here.</p>
      {busy && <p className="review-status" role="status">Working… Please wait.</p>}
      {error && <p className="review-error" role="alert">{error}</p>}
      {result && <>
        <section className="review-section" aria-label="Review result">
          <h3>{result.status === 0 ? 'Needs clarification' : result.status === 1 ? 'Proposal generated' : 'Review result'}</h3>
          <p className="review-text">{result.changeSummary}</p>
        </section>
        <fieldset className="review-section" disabled={busy}><legend><h3>Questions and answers</h3></legend>
          {!(result.questions || []).length && <p>No open questions.</p>}
          {(result.questions || []).map((question, index) => <div className="review-question" key={`${question.id}-${index}`}>
            <h4>{question.id}: {types[question.type] ?? question.type} · {impacts[question.impact] ?? question.impact} impact</h4>
            <p id={`${prefix}-question-${index}`} className="review-text">{question.description}</p>
            {question.newEntryQuote && <div id={`${prefix}-quote-${index}`} className="review-evidence"><p><strong>Review quote</strong></p><blockquote>{question.newEntryQuote}</blockquote></div>}
            {question.priorReference && <p className="review-text">Prior reference: {question.priorReference}</p>}
            {!!question.suggestedOptions?.length && <div id={`${prefix}-options-${index}`}><p className="review-label">Suggested options</p><ul className="review-options">{question.suggestedOptions.map((option, optionIndex) => <li className="review-text" key={optionIndex}>{option}</li>)}</ul></div>}
            <div className="field">
              <label htmlFor={`${prefix}-answer-${index}`}>Answer {question.id}</label>
              <textarea id={`${prefix}-answer-${index}`} aria-describedby={[`${prefix}-question-${index}`, question.newEntryQuote && `${prefix}-quote-${index}`, !!question.suggestedOptions?.length && `${prefix}-options-${index}`].filter(Boolean).join(' ')} maxLength={4000} value={answers[question.id] || ''} onChange={event => updateAnswer(question.id, event.target.value)} />
            </div>
          </div>)}
          {Object.entries(answers).filter(([id]) => !(result.questions || []).some(q => q.id === id)).map(([id, answer], index) => <div className="field" key={id}><label htmlFor={`${prefix}-previous-${index}`}>Previous answer {id}</label><textarea id={`${prefix}-previous-${index}`} maxLength={4000} value={answer} onChange={event => updateAnswer(id, event.target.value)} /></div>)}
        </fieldset>
        <details className="review-section"><summary>Proposed digest changes (not saved)</summary>{(result.digestChanges || []).length ? result.digestChanges.map((change, index) => <pre className="review-text" key={index}>{readableDetails({ ...change, action: actions[change.action] ?? change.action })}</pre>) : <p>No digest changes proposed.</p>}</details>
        {draft && <fieldset className="review-section" disabled={busy}><legend><h3>Editable generated entry preview</h3></legend>
          <div className="field"><label htmlFor={`${prefix}-title`}>Generated title</label><input id={`${prefix}-title`} aria-describedby={`${prefix}-preview-help`} value={draft.title} onChange={event => edit('title', event.target.value)} /></div>
          <div className="field"><label htmlFor={`${prefix}-content`}>Generated content</label><textarea id={`${prefix}-content`} aria-describedby={`${prefix}-preview-help`} className="review-preview" value={draft.content} onChange={event => edit('content', event.target.value)} /></div>
          <p id={`${prefix}-preview-help`} className="muted">Save reviewed entry explicitly saves this title and content only, with the editor’s existing status. It does not apply digest changes. Submitting answers for another review regenerates this preview, replacing any preview edits.</p>
        </fieldset>}
        {draft && <><Comparison source={source} draft={draft} /><SemanticAudit audit={audit} onAudit={reAudit} disabled={busy || !auditRequest(session.current.snapshot()) || sourceChanged.current} /></>}
      </>}
      <div className="action-bar">
        <button type="button" className="secondary" disabled={busy} onClick={run}>{result ? 'Submit answers for another review' : 'Retry review'}</button>
        {draft && <button type="button" className="primary" disabled={busy || !canWrite || !draft.title.trim() || !draft.content.trim()} onClick={save}>Save reviewed entry</button>}
        <button type="button" className="secondary" disabled={requests.current.saving} onClick={discard}>Discard review</button>
      </div>
    </>}
  </section>;
}
