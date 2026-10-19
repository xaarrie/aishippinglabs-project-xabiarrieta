import React from 'react';
import { clarificationEvidence, sourceEntryHref } from './timelineSearch.js';

export default function TimelineSearchResults({ result, projectId, timelineId, Link }) {
  const facts = (items, title) => <section aria-label={title}><h5>{title}</h5>
    {items.length ? items.map(fact => <article className="snapshot-record" key={fact.id}>
      <p className="entry-content"><strong>{fact.claim}</strong></p>
      <p>Entity: {fact.entityKey} · {fact.entityType}</p>
      <p>Source kind: {fact.sourceKind === 'Clarification' ? 'Clarification (review answer, not original entry text)' : 'Entry (original source text)'}</p>
      <h6>Source quote</h6><blockquote className="entry-content">{fact.sourceQuote}</blockquote>
      {fact.sourceKind === 'Clarification' && <div><h6>Clarification evidence</h6>
        {clarificationEvidence(fact.clarificationEvidence).length
          ? clarificationEvidence(fact.clarificationEvidence).map((item, index) => <p className="entry-content" key={index}>{item}</p>)
          : <p>Clarification details were not supplied.</p>}
      </div>}
      <p><Link href={sourceEntryHref(projectId, timelineId, fact.entryId)}>Source entry {fact.entryNumber}</Link></p>
      <p className="muted">Fact ID: {fact.id} · Creation review: {fact.creationReviewId}</p>
    </article>) : <p className="muted">None returned.</p>}
  </section>;
  return <div className="search-answer">
    <section aria-label="Search coverage"><h5>Search coverage</h5><dl className="workspace-context">
      <div><dt>Entries</dt><dd>{result.coverage.entries}</dd></div>
      <div><dt>Entries without completed extraction</dt><dd>{result.coverage.entriesWithoutCompletedExtraction}</dd></div>
      <div><dt>Pending reviews</dt><dd>{result.coverage.pendingReviews}</dd></div>
      <div><dt>Accepted valid facts</dt><dd>{result.coverage.acceptedValidFacts}</dd></div>
      <div><dt>Retrieved facts</dt><dd>{result.facts.length}</dd></div>
      <div><dt>Truncated</dt><dd>{result.coverage.truncated ? 'Yes' : 'No'}</dd></div>
    </dl><ul>{result.warnings.map((warning, index) => <li key={index}>{warning}</li>)}</ul></section>
    {result.emptyMessage ? <p role="status">{result.emptyMessage}</p> : <>
      <h5>Model narrative</h5>
      <p className="muted">Generated synthesis is separate from the retrieved evidence below. Verified citations identify supporting facts; they do not validate every sentence.</p>
      <p className="entry-content">{result.content || 'No cited model answer was returned. Review the retrieved context below.'}</p>
      {facts(result.citedFacts, 'Facts cited in answer')}
      {facts(result.additionalFacts, 'Additional retrieved context')}
    </>}
  </div>;
}
