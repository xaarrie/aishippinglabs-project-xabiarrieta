import React from 'react';
import { clarificationEvidence, sourceEntryHref } from './timelineSearch.js';

export default function TimelineSearchResults({ result, projectId, timelineId, Link }) {
  const facts = (items, title) => <section aria-label={title}><h5>{title}</h5>
    {title === 'Additional retrieved context' && <p className="muted">Additional retrieved facts, not cited in the answer. Separate source-entry context is listed in its own sections.</p>}
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
      <p className="muted">Fact ID: {fact.id} · {fact.extractionJobId != null
        ? <>Extraction job: {fact.extractionJobId}</> : <>Creation review: {fact.creationReviewId}</>}</p>
    </article>) : <p className="muted">None returned.</p>}
  </section>;
  const contexts = (items, title) => items.length > 0 && <section aria-label={title}><h5>{title}</h5>
    {items.map(context => <article className="snapshot-record" key={context.evidenceId}>
      <p><strong>Source-entry context — not accepted fact proof</strong></p>
      <p className="muted">Separate entry text, not an accepted fact or a clarification answer.</p>
      {context.truncated && <p role="status">Truncated source-entry text: only part of the entry is shown.</p>}
      <blockquote className="entry-content">{context.text}</blockquote>
      <p><Link href={sourceEntryHref(projectId, timelineId, context.entryId)}>Source entry {context.entryNumber}</Link></p>
      <p className="muted">Anchor fact IDs: {context.anchorFactIds.join(', ')}</p>
      <p className="muted">Evidence ID: {context.evidenceId} · Source fingerprint: {context.sourceFingerprint}</p>
    </article>)}
  </section>;
  const research = result.research;
  return <div className="search-answer">
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
        <div><dt>Search attempts</dt><dd>{research.searchQueries} / {research.limits.maxSearchQueries}</dd></div>
        <div><dt>Budget exhausted</dt><dd>{research.budgetExhausted ? 'Yes' : 'No'}</dd></div>
        <div><dt>Evidence truncated</dt><dd>{research.evidenceTruncated ? 'Yes' : 'No'}</dd></div>
      </dl>
      <p className="muted">Tool/search attempts include rejected calls and may exceed allowances. Complete means the bounded operation finished, not that all information was found.</p>
      {research.status === 'Partial' && <p>Partial research: available evidence may be incomplete.</p>}
      {research.status === 'Empty' && <p>No eligible facts are available for research.</p>}
      {research.status === 'InsufficientEvidence' && <p>Insufficient evidence: no grounded model answer is available.</p>}
      {research.stopReason === 'ProviderCallLimit' && <p>Model-call limit reached before a final answer. Only available evidence is shown.</p>}
    </section>}
    {result.emptyMessage ? <p role="status">{result.emptyMessage}</p> : <>
      <h5>Model narrative</h5>
      <p className="muted">Generated synthesis is separate from the retrieved evidence below. Fact citations identify supporting facts; source-entry context citations identify separate context, not accepted fact proof. Citations do not validate every sentence.</p>
      <p className="entry-content">{result.content || 'No cited model answer was returned. Review the retrieved context below.'}</p>
      {facts(result.citedFacts, 'Facts cited in answer')}
      {facts(result.additionalFacts, 'Additional retrieved context')}
      {contexts(result.citedSourceContexts, 'Source-entry context cited in answer')}
      {contexts(result.additionalSourceContexts, 'Additional source-entry context')}
    </>}
  </div>;
}
