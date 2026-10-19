export function createFactReviewClient(api) {
  const root = id => `/api/entries/${encodeURIComponent(id)}`;
  return {
    read: (id, options = {}) => api(`${root(id)}/facts`, options),
    plan: (id, previousProposalId = null, answers = [], options = {}) => api(`${root(id)}/fact-proposals`, {
      ...options, method: 'POST', body: JSON.stringify({ previousProposalId, answers })
    }),
    accept: (id, proposalId, options = {}) => api(`${root(id)}/fact-proposals/${encodeURIComponent(proposalId)}/accept`, { ...options, method: 'POST' })
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
