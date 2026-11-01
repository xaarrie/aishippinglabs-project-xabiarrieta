import React from 'react';

export default function TimelineSearchResults({ result }) {
  const research = result.research;
  return <div className="search-answer">
    <p role="status" className={`request-feedback${result.content && (!research || research.status === 'Complete') &&
      !research?.stopReason && !research?.budgetExhausted && !research?.evidenceTruncated ? ' request-success' : ''}`}>{result.outcomeMessage}</p>
    <section aria-label="Search coverage"><h5>Search coverage</h5><dl className="workspace-context">
      <div><dt>Entries</dt><dd>{result.coverage.entries}</dd></div>
      <div><dt>Entries without completed extraction</dt><dd>{result.coverage.entriesWithoutCompletedExtraction}</dd></div>
      <div><dt>Pending reviews</dt><dd>{result.coverage.pendingReviews}</dd></div>
      <div><dt>Accepted valid facts</dt><dd>{result.coverage.acceptedValidFacts}</dd></div>
      <div><dt>Retrieved facts</dt><dd>{result.facts.length}</dd></div>
      <div><dt>Truncated</dt><dd>{result.coverage.truncated ? 'Yes' : 'No'}</dd></div>
    </dl><ul>{result.warnings.map((warning, index) => <li key={index}>{warning}</li>)}</ul></section>
    {research && <section aria-label="Research outcome"><h5>Research outcome</h5>
      <p role="status">Status: {research.status} · Stop reason: {research.stopReason ?? 'None'}</p>
      <dl className="workspace-context">
        <div><dt>Model calls</dt><dd>{research.modelCalls} / {research.limits.maxModelCalls}</dd></div>
        <div><dt>Tool attempts</dt><dd>{research.toolCalls} / {research.limits.maxToolCalls}</dd></div>
        <div><dt>Search attempts</dt><dd>{research.searchQueries} / {research.limits.maxSearchQueries ?? 'No count ceiling'}</dd></div>
        <div><dt>Budget exhausted</dt><dd>{research.budgetExhausted ? 'Yes' : 'No'}</dd></div>
        <div><dt>Evidence truncated</dt><dd>{research.evidenceTruncated ? 'Yes' : 'No'}</dd></div>
      </dl>
      <p className="muted">Attempts include rejected calls and may exceed limits. Complete does not mean exhaustive.</p>
    </section>}
    {result.emptyMessage && <p role="status">{result.emptyMessage}</p>}
    {result.content && <>
      <h5>Agent summary</h5>
      <p className="entry-content">{result.content}</p>
    </>}
    {result.citedFacts.length > 0 && <section aria-label="Facts cited in answer"><h5>Facts cited in answer</h5>
      {result.citedFacts.map(fact => <article className="snapshot-record" key={fact.id}>
        <p className="entry-content"><strong>{fact.claim}</strong></p>
        <p>Entity: {fact.entityKey} · {fact.entityType}</p>
        <p>Source kind: {fact.sourceKind === 'Clarification' ? 'Clarification (review answer, not original entry text)' : 'Entry (original source text)'}</p>
      </article>)}
    </section>}
  </div>;
}
