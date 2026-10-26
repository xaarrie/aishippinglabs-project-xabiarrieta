const evidenceError = () => new Error('Search response is missing valid structured fact evidence. Please retry.');
const text = value => typeof value === 'string' && value.trim().length > 0;
const count = value => Number.isSafeInteger(value) && value >= 0;
// Effective facts have one origin: an accepted review or a historical direct extraction.
const validProvenance = value => (text(value.creationReviewId) && value.extractionJobId == null) ||
  (value.creationReviewId == null && text(value.extractionJobId));

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

const contextKind = 'SeparateSourceEntryContextNotFactProof';
const guid = value => typeof value === 'string' && /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(value);
const uniqueAnchors = value => Array.isArray(value) && value.length > 0 && value.every(text) && new Set(value).size === value.length;
const cloneContext = value => ({ ...value, anchorFactIds: [...value.anchorFactIds] });
const integerLimits = ['maxModelCalls', 'maxToolCalls', 'maxSearchQueries', 'maxDistinctFacts',
  'maxHitsPerSearch', 'maxSourceContexts', 'maxCharsPerContext', 'maxTotalSourceContextChars',
  'maxTotalSerializedToolEvidenceChars', 'maxOutputTokens'];

function normalizeResearch(value) {
  if (value == null) return null; // Legacy responses have no research metadata.
  if (!['Complete', 'Empty', 'InsufficientEvidence', 'Partial'].includes(value.status) ||
      ![null, 'NoEligibleFacts', 'NoEvidence', 'ProviderCallLimit', 'ToolOrEvidenceLimit', 'EvidenceTruncated'].includes(value.stopReason) ||
      !['modelCalls', 'toolCalls', 'searchQueries'].every(key => count(value[key])) ||
      typeof value.evidenceTruncated !== 'boolean' || typeof value.budgetExhausted !== 'boolean' ||
      !Array.isArray(value.warnings) || !value.warnings.every(x => typeof x === 'string') ||
      !value.limits || !integerLimits.every(key => count(value.limits[key]) && value.limits[key] > 0) ||
      !Number.isFinite(value.limits.deadlineSeconds) || value.limits.deadlineSeconds <= 0) throw evidenceError();
  // Rejected SDK/tool attempts count too. Counts are not capped against limits here.
  return { ...value, warnings: [...value.warnings], limits: { ...value.limits } };
}

export function normalizeTimelineSearch(value) {
  const coverage = value?.coverage;
  if (!value || !Array.isArray(value.facts) || !Array.isArray(value.citations) || !coverage ||
      !['entries', 'entriesWithoutCompletedExtraction', 'pendingReviews', 'acceptedValidFacts'].every(key => count(coverage[key])) ||
      typeof coverage.truncated !== 'boolean' || !Array.isArray(coverage.warnings) || !coverage.warnings.every(x => typeof x === 'string') ||
      typeof value.content !== 'string') throw evidenceError();
  const ids = new Set();
  for (const fact of value.facts) {
    if (!fact || !['id', 'entryId', 'claim', 'entityKey', 'entityType', 'sourceQuote', 'sourceFingerprint'].every(key => text(fact[key])) ||
        !validProvenance(fact) || !Number.isSafeInteger(fact.entryNumber) || fact.entryNumber < 1 || !['Entry', 'Clarification'].includes(fact.sourceKind) ||
        typeof fact.clarificationEvidence !== 'string' ||
        (fact.extractionJobId != null && (fact.sourceKind !== 'Entry' || fact.clarificationEvidence !== '')) || ids.has(fact.id)) throw evidenceError();
    ids.add(fact.id);
  }
  const facts = value.facts.map(fact => ({ ...fact }));
  const research = normalizeResearch(value.research);
  const rawContexts = value.sourceContexts === undefined ? [] : value.sourceContexts;
  const rawContextCitations = value.contextCitations === undefined ? [] : value.contextCitations;
  if (!Array.isArray(rawContexts) || !Array.isArray(rawContextCitations)) throw evidenceError();
  const contextIds = new Set(); const contextEntries = new Set();
  for (const context of rawContexts) {
    if (!context || !guid(context.evidenceId) || !text(context.entryId) ||
        !Number.isSafeInteger(context.entryNumber) || context.entryNumber < 1 ||
        !text(context.sourceFingerprint) || typeof context.text !== 'string' || context.text.length === 0 ||
        typeof context.truncated !== 'boolean' ||
        context.evidenceKind !== contextKind || !uniqueAnchors(context.anchorFactIds) ||
        contextIds.has(context.evidenceId) || contextEntries.has(context.entryId) ||
        !context.anchorFactIds.every(id => facts.some(fact => fact.id === id &&
          fact.entryId === context.entryId && fact.entryNumber === context.entryNumber &&
          fact.sourceFingerprint === context.sourceFingerprint))) throw evidenceError();
    contextIds.add(context.evidenceId); contextEntries.add(context.entryId);
  }
  const sourceContexts = rawContexts.map(cloneContext);
  const citedContextIds = new Set(); const contextCitations = []; let rejectedContext = false;
  for (const citation of rawContextCitations) {
    const context = sourceContexts.find(x => x.evidenceId === citation?.evidenceId);
    if (!context || citedContextIds.has(citation.evidenceId) || citation.entryId !== context.entryId ||
        citation.entryNumber !== context.entryNumber || citation.sourceFingerprint !== context.sourceFingerprint ||
        citation.evidenceKind !== contextKind || !uniqueAnchors(citation.anchorFactIds) ||
        citation.anchorFactIds.length !== context.anchorFactIds.length ||
        !citation.anchorFactIds.every(id => context.anchorFactIds.includes(id))) { rejectedContext = true; continue; }
    citedContextIds.add(context.evidenceId); contextCitations.push(cloneContext(citation));
  }
  const citedIds = new Set(); const citations = []; let rejected = false;
  for (const citation of value.citations) {
    const fact = facts.find(x => x.id === citation?.factId);
    if (!fact || !validProvenance(citation) || citation.entryId !== fact.entryId || citation.entryNumber !== fact.entryNumber ||
        (citation.creationReviewId ?? null) !== (fact.creationReviewId ?? null) ||
        (citation.extractionJobId ?? null) !== (fact.extractionJobId ?? null)) { rejected = true; continue; }
    if (!citedIds.has(fact.id)) { citedIds.add(fact.id); citations.push({ ...citation }); }
  }
  const warnings = [...new Set([...searchWarnings(coverage), ...(research?.warnings ?? [])])];
  if (rejected || rejectedContext) warnings.push('Some unverified citation references were omitted.');
  if (rejectedContext) warnings.push('The model narrative was withheld because source-entry context citations could not be verified.');
  const noFinalAnswer = research && (['Empty', 'InsufficientEvidence'].includes(research.status) || research.stopReason === 'ProviderCallLimit');
  // A bad context reference invalidates the mixed narrative too, not just its context card.
  const showAnswer = !noFinalAnswer && !rejectedContext && (citations.length > 0 || contextCitations.length > 0);
  if (facts.length && !citations.length && !contextCitations.length) warnings.push('No verified answer citations were returned; retrieved facts are context only.');
  const answerFactIds = showAnswer ? citedIds : new Set();
  const answerContextIds = showAnswer ? citedContextIds : new Set();
  return {
    content: showAnswer ? value.content : '', facts, citations, sourceContexts, contextCitations, research,
    coverage: { ...coverage, warnings: [...coverage.warnings] }, warnings,
    citedFacts: facts.filter(x => answerFactIds.has(x.id)), additionalFacts: facts.filter(x => !answerFactIds.has(x.id)),
    citedSourceContexts: sourceContexts.filter(x => answerContextIds.has(x.evidenceId)),
    additionalSourceContexts: sourceContexts.filter(x => !answerContextIds.has(x.evidenceId)),
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
