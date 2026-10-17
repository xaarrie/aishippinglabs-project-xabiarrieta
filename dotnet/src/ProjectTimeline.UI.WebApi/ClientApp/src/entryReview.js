// Tokens retain their line endings, including blank lines and a final newline.
export function lineDiff(original, proposed) {
  if (original === proposed) {
    const parts = original ? [{ text: original, changed: false }] : [];
    return { source: parts, proposal: parts, fallback: false };
  }
  const lines = text => text.match(/[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$/g) || [];
  const a = lines(original), b = lines(proposed);
  // Bound both cell work/storage and long-string comparisons. The fallback is
  // deliberately coarse, not a summary: every original/proposal byte survives.
  if ((a.length + 1) * (b.length + 1) > 250000 || original.length + proposed.length > 200000) {
    return { source: original ? [{ text: original, changed: true }] : [], proposal: proposed ? [{ text: proposed, changed: true }] : [], fallback: true };
  }
  const width = b.length + 1;
  const table = new Uint32Array((a.length + 1) * width);
  for (let i = a.length - 1; i >= 0; i--) for (let j = b.length - 1; j >= 0; j--) {
    table[i * width + j] = a[i] === b[j] ? 1 + table[(i + 1) * width + j + 1] : Math.max(table[(i + 1) * width + j], table[i * width + j + 1]);
  }
  const source = [], proposal = [];
  let i = 0, j = 0;
  while (i < a.length || j < b.length) {
    if (i < a.length && j < b.length && a[i] === b[j]) {
      source.push({ text: a[i++], changed: false }); proposal.push({ text: b[j++], changed: false });
    } else if (i < a.length && (j === b.length || table[(i + 1) * width + j] >= table[i * width + j + 1])) {
      source.push({ text: a[i++], changed: true });
    } else proposal.push({ text: b[j++], changed: true });
  }
  return { source, proposal, fallback: false };
}
export function reviewSource(entryId, source) {
  return source?.entryId === entryId && typeof source.title === 'string' && typeof source.content === 'string'
    ? Object.freeze({ entryId, title: source.title, content: source.content, fingerprint: source.fingerprint }) : null;
}
// Replace synchronously in event handlers, BEFORE scheduling React state. Object
// identity rejects old responses even after an edit is reverted to the same text.
export function createReviewSession() {
  let current;
  return {
    replace(entryId, source, proposal) {
      current = Object.freeze({ entryId, source: reviewSource(entryId, source), proposal: proposal ? Object.freeze({ title: proposal.title, content: proposal.content }) : null });
      return current;
    },
    snapshot: () => current,
    isCurrent: snapshot => current === snapshot
  };
}
export function auditRequest(snapshot) {
  return snapshot?.proposal && /^[a-f0-9]{64}$/.test(snapshot.source?.fingerprint || '')
    ? { proposalTitle: snapshot.proposal.title, proposalContent: snapshot.proposal.content, expectedSourceFingerprint: snapshot.source.fingerprint } : null;
}
export function auditMatches(snapshot, response) {
  return Boolean(auditRequest(snapshot) && response && response.entryId === snapshot.entryId && response.sourceFingerprint === snapshot.source.fingerprint && /^[a-f0-9]{64}$/.test(response.proposalFingerprint || ''));
}
export function auditCoverage(audit) {
  const statuses = ['Preserved', 'Omitted', 'Contradicted', 'Uncertain'];
  return audit?.inconclusive === false && Array.isArray(audit.facts) && audit.facts.length > 0
    && audit.facts.every(fact => typeof fact?.fact === 'string' && fact.fact.trim() && statuses.includes(fact.status))
    && Number.isInteger(audit.totalFacts) && audit.totalFacts === audit.facts.length
    && Number.isInteger(audit.preservedFacts) && audit.preservedFacts >= 0 && audit.preservedFacts <= audit.totalFacts
    && typeof audit.coverage === 'number' && Number.isFinite(audit.coverage) && audit.coverage >= 0 && audit.coverage <= 1 ? audit.coverage : null;
}
export function auditHasIssues(audit) {
  return Boolean(audit?.unsupportedAdditions?.length || audit?.facts?.some(fact => fact.status !== 'Preserved' || fact.evidenceWarning));
}

export const canReviewEntry = (id, canWrite) => Boolean(id && canWrite);
export const applyReviewDraft = (editor, draft) => ({ ...editor, title: draft.title, content: draft.content });
export function reviewRequest(previousResult, answers) {
  return previousResult ? { previousResult, userAnswers: Object.fromEntries(Object.entries(answers).filter(([, answer]) => answer.trim())) } : {};
}
// Render nested fields rather than flattening to titles: owners, dependencies,
// acceptance criteria and unresolved question references are part of the draft.
export const readableLabel = key => key.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, letter => letter.toUpperCase());
export function readableDetails(value, depth = 0) {
  if (value == null) return '';
  if (Array.isArray(value)) return value.map(item => {
    const text = readableDetails(item, depth + 1);
    return text ? `${'  '.repeat(depth)}- ${text}` : '';
  }).filter(Boolean).join('\n');
  if (typeof value === 'object') return Object.entries(value).map(([key, item]) => {
    const text = readableDetails(item, depth + 1);
    return text ? `${readableLabel(key)}: ${typeof item === 'object' ? '\n' : ''}${text}` : '';
  }).filter(Boolean).join('\n');
  return String(value);
}
export function formatEntryDraft(entry) {
  return { title: entry.title || '', content: Object.entries(entry).filter(([key]) => key !== 'title').map(([key, value]) => {
    const text = readableDetails(value);
    return text ? `## ${readableLabel(key)}\n${text}` : '';
  }).filter(Boolean).join('\n\n') };
}
export function hasEntryWriteAccess(user, project, participants = []) {
  return Boolean(user && project && ((user.roleCode || user.roleName) === 'Admin' || project.ownerId === user.id || participants.some(p => p.userId === user.id && ['Creator', 'Contributor'].includes(p.projectRoleName))));
}
