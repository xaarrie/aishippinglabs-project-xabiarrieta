export function createFactReviewClient(api) {
  const root = id => `/api/entries/${encodeURIComponent(id)}`;
  return {
    read: (id, options = {}) => api(`${root(id)}/facts`, options),
    generate: (id, options = {}) => api(`${root(id)}/fact-generation`, { ...options, method: 'POST', body: '{}' }),
    regenerate: (id, expectedGenerationId, options = {}) => api(`${root(id)}/fact-generation`, {
      ...options, method: 'POST', body: JSON.stringify({ regenerate: true, expectedGenerationId })
    }),
    plan: (id, previousProposalId = null, answers = [], options = {}) => api(`${root(id)}/fact-proposals`, {
      ...options, method: 'POST', body: JSON.stringify({ previousProposalId, answers })
    }),
    accept: (id, proposalId, options = {}) => api(`${root(id)}/fact-proposals/${encodeURIComponent(proposalId)}/accept`, { ...options, method: 'POST' })
  };
}
// The server orders proposals newest first and supersedes old source/generation work.
// Generated drafts and historical proposals must never become reconciliation actions.
export function currentFactGeneration(state) {
  return state?.generationId ? state.proposals?.find(p => p.proposalId === state.generationId && p.state === 'Generated') ?? null : null;
}
export function currentFactReconciliation(state) {
  return state?.proposals?.find(p => p.state === (state.lifecycleState === 'Reconciled' ? 'Accepted' : 'Pending')) ?? null;
}
export function savedFactSource(state, entryId = null) {
  const source = state?.source;
  return source && (!entryId || source.entryId === entryId) && source.entryId && source.timelineId
    && ['title', 'content', 'status'].every(key => typeof source[key] === 'string')
    && /^[a-f0-9]{64}$/i.test(source.fingerprint || '') ? source : null;
}
export function entryMatchesFactSource(entry, source) {
  return !!(entry && source && entry.id === source.entryId && entry.timelineId === source.timelineId
    && ['title', 'content', 'status'].every(key => entry[key] === source[key]));
}
export function factGenerationMatchesSource(state) {
  const source = savedFactSource(state), generated = currentFactGeneration(state);
  return !!(source && !state.refreshRequired && generated && generated.sourceFingerprint === source.fingerprint);
}
export function factReviewActions(state, { canWrite = false, dirty = false, busy = false, conflict = false } = {}) {
  const edit = !!(canWrite && state?.canEdit === true && savedFactSource(state) && !state.refreshRequired && state.lifecycleState !== 'Reconciled');
  const mutate = edit && !dirty && !busy && !conflict;
  const completed = ['Pending', 'Empty'].includes(state?.lifecycleState) && factGenerationMatchesSource(state);
  return {
    edit,
    generate: !!(mutate && state.canGenerate === true && !state.generationId && state.lifecycleState === 'NotGenerated'),
    regenerate: !!(mutate && state.canRegenerate === true && state.generationId),
    reconcile: !!(mutate && completed)
  };
}
export function canAcceptCurrentFactProposal(state, proposal) {
  return !!(state?.canEdit === true && ['Pending', 'Empty'].includes(state.lifecycleState)
    && factGenerationMatchesSource(state) && currentFactReconciliation(state)?.proposalId === proposal?.proposalId
    && proposal?.sourceFingerprint === state.source.fingerprint
    && canAcceptFactProposal(currentFactReconciliation(state)) && canAcceptFactProposal(proposal));
}
// Event-only save transaction: persistence failure propagates, generation failure
// does not. A second submit cannot produce another entry or generation request.
export function createEntrySaveAction(client) {
  let locked = false;
  return { async run(save, { signal, onSaved } = {}) {
    if (locked || signal?.aborted) return;
    locked = true;
    try {
      const entry = await save({ signal });
      const receipt = { entry, facts: null, generationError: '' };
      if (signal?.aborted) return receipt;
      onSaved?.(entry);
      try { receipt.facts = await client.generate(entry.id, { signal }); }
      catch (error) { receipt.generationError = `Entry saved. ${factReviewError(error)}`; }
      return receipt;
    } finally { locked = false; }
  } };
}
export function createFactRequestFence() {
  let current;
  return {
    begin() { current?.abort(); current = new AbortController(); return current; },
    isCurrent: request => current === request && !request.signal.aborted,
    cancel() { current?.abort(); current = null; }
  };
}
export function canAcceptFactProposal(proposal) {
  return !!proposal?.proposalId && proposal.state === 'Pending' && proposal.ready === true;
}
// Only use the already-authorized bounded response; do not fetch causes or replace
// the historical cause with a newer/valid fact when a later correction exists.
export function factInvalidationText(fact, facts = []) {
  if (fact.valid) return null;
  if (!fact.invalidatedByFactId) return 'Replacement fact was not recorded (legacy history).';
  const cause = facts.find(item => item.id === fact.invalidatedByFactId);
  return cause?.claim
    ? `Invalidated by fact ${fact.invalidatedByFactId}: ${cause.claim}`
    : `Invalidated by fact ${fact.invalidatedByFactId}. Replacement claim is not loaded in this bounded view.`;
}
export function proposalInvalidationText(invalidation) {
  if (!invalidation.invalidatingAdditionId) return 'Replacement addition was not recorded (legacy proposal).';
  const index = invalidation.invalidatingAdditionIndex;
  const label = Number.isInteger(index) && index >= 0
    ? `${index + 1} (${invalidation.invalidatingAdditionId})` : invalidation.invalidatingAdditionId;
  return invalidation.invalidatingClaim
    ? `Intended replacement addition ${label}: ${invalidation.invalidatingClaim}`
    : `Intended replacement addition ${label}. Replacement claim is not loaded.`;
}
export function factReviewError(error) {
  if (error?.status === 409) return 'Fact state is stale or clarification is incomplete. Refresh and regenerate before accepting.';
  if (error?.status === 502) return 'Fact provider unavailable. Retry later; no acceptance was confirmed.';
  if (error?.status === 401 || error?.status === 403) return 'Fact review access is unavailable. Sign in and verify write permission.';
  return 'Fact request failed. Refresh to confirm the server state; no acceptance was confirmed.';
}
