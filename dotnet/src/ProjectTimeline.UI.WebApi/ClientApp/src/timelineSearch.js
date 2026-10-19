const evidenceError = () => new Error('Search response is missing valid structured fact evidence. Please retry.');
const text = value => typeof value === 'string' && value.trim().length > 0;
const count = value => Number.isSafeInteger(value) && value >= 0;

export function clarificationEvidence(value) {
  if (!text(value)) return [];
  try {
    const parsed = JSON.parse(value);
    if (Array.isArray(parsed)) return parsed.map(item => {
      const question = item?.Question ?? item?.question;
      const answer = item?.Answer ?? item?.answer;
      return text(question) && text(answer) ? `Question: ${question} — Answer: ${answer}` : null;
    }).filter(Boolean);
  } catch { /* Keep unknown evidence as plain text, never HTML. */ }
  return [value];
}

export function searchWarnings(coverage) {
  return [...new Set([
    ...coverage.warnings,
    'Bounded lexical/entity matching is not exhaustive; missing matches do not prove information or contradictions are absent.',
    ...(coverage.entriesWithoutCompletedExtraction > 0 ? ['Incomplete extraction: some entries have no completed extraction.'] : []),
    ...(coverage.pendingReviews > 0 ? ['There are pending reviews; their proposed facts are not included.'] : []),
    ...(coverage.truncated ? ['Truncated results: only part of the matching fact context was returned.'] : [])
  ])];
}

export function normalizeTimelineSearch(value) {
  const coverage = value?.coverage;
  if (!value || !Array.isArray(value.facts) || !Array.isArray(value.citations) || !coverage ||
      !['entries', 'entriesWithoutCompletedExtraction', 'pendingReviews', 'acceptedValidFacts'].every(key => count(coverage[key])) ||
      typeof coverage.truncated !== 'boolean' || !Array.isArray(coverage.warnings) || !coverage.warnings.every(x => typeof x === 'string') ||
      typeof value.content !== 'string') throw evidenceError();
  const ids = new Set();
  for (const fact of value.facts) {
    if (!fact || !['id', 'entryId', 'claim', 'entityKey', 'entityType', 'sourceQuote', 'sourceFingerprint', 'creationReviewId'].every(key => text(fact[key])) ||
        !Number.isSafeInteger(fact.entryNumber) || fact.entryNumber < 1 || !['Entry', 'Clarification'].includes(fact.sourceKind) ||
        typeof fact.clarificationEvidence !== 'string' || ids.has(fact.id)) throw evidenceError();
    ids.add(fact.id);
  }
  const facts = value.facts.map(fact => ({ ...fact }));
  const citedIds = new Set(); const citations = []; let rejected = false;
  for (const citation of value.citations) {
    const fact = facts.find(x => x.id === citation?.factId);
    if (!fact || citation.entryId !== fact.entryId || citation.entryNumber !== fact.entryNumber || citation.creationReviewId !== fact.creationReviewId) { rejected = true; continue; }
    if (!citedIds.has(fact.id)) { citedIds.add(fact.id); citations.push({ ...citation }); }
  }
  const warnings = searchWarnings(coverage);
  if (rejected) warnings.push('Some unverified citation references were omitted.');
  if (facts.length && !citations.length) warnings.push('No verified answer citations were returned; retrieved facts are context only.');
  return {
    content: citations.length ? value.content : '', facts, citations, coverage: { ...coverage }, warnings,
    citedFacts: facts.filter(x => citedIds.has(x.id)), additionalFacts: facts.filter(x => !citedIds.has(x.id)),
    emptyMessage: facts.length ? '' : coverage.acceptedValidFacts === 0
      ? 'No accepted valid facts are available. No original entry content was searched.'
      : 'No accepted fact matches were returned. No original entry content was searched.'
  };
}

export async function searchTimelineFacts(api, timelineId, query, signal) {
  if (typeof query !== 'string' || query.length > 300 || !query.trim()) throw new Error('Enter a question of 1–300 characters.');
  signal?.throwIfAborted();
  const result = await api(`/api/timelines/${encodeURIComponent(timelineId)}/entry-search?text=${encodeURIComponent(query.trim())}`, { signal });
  signal?.throwIfAborted();
  return normalizeTimelineSearch(result);
}

export function sourceEntryHref(projectId, timelineId, entryId) {
  return `/projects/${encodeURIComponent(projectId)}/timelines/${encodeURIComponent(timelineId)}/entries/${encodeURIComponent(entryId)}?projectId=${encodeURIComponent(projectId)}`;
}

// Identity fencing also covers transports which ignore AbortSignal.
export function createSearchRequestFence() {
  let current = null;
  return {
    begin() { current?.abort(); current = new AbortController(); return current; },
    cancel() { current?.abort(); current = null; },
    isCurrent(controller) { return current === controller && !controller.signal.aborted; }
  };
}
