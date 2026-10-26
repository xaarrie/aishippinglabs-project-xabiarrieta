import React, { useEffect, useRef, useState } from 'react';
import { api } from './api.js';
import { clarificationActions, createClarificationClient, createClarificationSession } from './entryClarifications.js';

const client = createClarificationClient(api);

export default function EntryClarifications({ entryId, canWrite, blocked = false, refreshKey, onResume = () => {}, onActiveChange = () => {} }) {
  const [view, setView] = useState(null);
  const callbacks = useRef({ onResume, onActiveChange });
  callbacks.current = { onResume, onActiveChange };
  const [session] = useState(() => createClarificationSession(client, { onChange: setView, onResume: () => callbacks.current.onResume() }));
  session.permissions(canWrite && !blocked);
  useEffect(() => {
    if (session.snapshot().entryId !== entryId) session.select(entryId);
    session.refresh();
    return () => session.dispose();
  }, [entryId, session]);
  const previousRefresh = useRef(refreshKey);
  useEffect(() => {
    if (previousRefresh.current !== refreshKey) {
      previousRefresh.current = refreshKey;
      session.refresh();
    }
    // Never cancel a human write when the independent fact view changes.
  }, [refreshKey, session]);
  const current = view?.entryId === entryId ? view : session.snapshot();
  useEffect(() => {
    callbacks.current.onActiveChange(current.busy);
    return () => callbacks.current.onActiveChange(false);
  }, [current.busy]);
  const state = current.state;
  const actions = clarificationActions(current, canWrite && !blocked);
  const renderRow = row => <li key={row.id} data-clarification-id={row.id}>
    <h4>{row.question}</h4>
    <small>{row.binding} · Origin generation: {row.generationId} · Asked: {row.createdAtUtc}</small>
    {row.answer !== null ? <><p>Saved human answer (immutable)</p><blockquote className="entry-content">{row.answer}</blockquote><small>Answered by {row.answeredById} · {row.answeredAtUtc}</small></>
      : <p>No saved answer.</p>}
    {canWrite && row.binding === 'CurrentQuestion' && row.answer === null && <>
      <label htmlFor={`clarification-answer-${row.id}`}>Your answer: {row.question}</label>
      <textarea id={`clarification-answer-${row.id}`} maxLength={4000} disabled={!actions.answer(row)} value={current.drafts[row.id] ?? ''} onChange={event => session.draft(row.id, event.target.value)} />
      <button type="button" className="secondary" aria-label={`Save answer: ${row.question}`} disabled={!actions.answer(row) || !current.drafts[row.id]?.trim() || current.drafts[row.id].length > 4000} onClick={() => session.save(row.id)}>Save answer</button>
    </>}
  </li>;
  const retained = Object.entries(current.drafts).filter(([id]) => !state?.current.some(row => row.id === id && row.answer === null && row.binding === 'CurrentQuestion'));
  return <section className="card" aria-label="Entry extraction clarifications" aria-busy={current.busy || current.loading}>
    <h3>Entry extraction clarifications</h3>
    <p>Questions about missing source information are separate from reconciliation questions. Answer in your own words. Saving an answer does not run a model or change the original saved source.</p>
    {!canWrite && <p role="status">Read-only clarification access.</p>}
    {current.loading && <p role="status">Loading clarification evidence and server permissions...</p>}
    {current.busy && <p role="status">Processing explicit clarification request...</p>}
    {current.error && <p role="alert">{current.error}</p>}
    {current.message && <p role="status">{current.message}</p>}
    {current.conflict && <p role="status">Writes paused. Refresh clarification state, inspect the retained draft and server answers, then choose whether to save again.</p>}
    {state?.refreshRequired && <p role="alert">Source proof requires refresh. Answers and extraction are disabled.</p>}
    {state && <>
      <p role="status">Extraction: {state.lifecycleState} · Current generation: {state.generationId ?? 'None'}</p>
      <section aria-label="Current extraction questions and answers"><h4>Current questions and carried answers</h4>
        {state.current.length ? <ul>{state.current.map(renderRow)}</ul> : <p>No current extraction clarifications.</p>}
      </section>
      <section aria-label="Extraction clarification history"><h4>Clarification history (read-only)</h4>
        {state.history.length ? <ul>{state.history.map(renderRow)}</ul> : <p>No historical clarifications in this view.</p>}
        {state.truncated && <p role="status">History is truncated to the newest 100 rows. Older answers may not be shown.</p>}
      </section>
    </>}
    {retained.length > 0 && <section aria-label="Retained unsaved clarification drafts"><h4>Retained unsaved drafts (not evidence)</h4><p>These drafts were not confirmed saved for the current questions. Inspect before using them again.</p><ul>{retained.map(([id, answer]) => <li key={id}>{id}<blockquote className="entry-content">{answer}</blockquote></li>)}</ul></section>}
    <div className="action-bar">
      <button type="button" className="secondary" disabled={current.busy || current.loading} onClick={() => session.refresh(true)}>Refresh clarification state</button>
      {canWrite && <button type="button" className="primary" disabled={!actions.resume} onClick={() => session.resume()}>Extract using answers (model run)</button>}
    </div>
    {canWrite && <p className="muted">Extract using answers explicitly runs the model and may incur billing. It requires server-confirmed readiness after all required answers are saved. Partial answers never start extraction. New candidates still require reconciliation and explicit acceptance.</p>}
  </section>;
}
