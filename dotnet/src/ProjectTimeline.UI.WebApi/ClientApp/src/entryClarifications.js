import { savedFactSource } from './factReview.js';

const guid = value => typeof value === 'string' && /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value) && !/^0{8}(-0{4}){3}-0{12}$/.test(value);
const text = (value, max) => typeof value === 'string' && value.trim().length > 0 && value.length <= max;
const date = value => typeof value === 'string' && Number.isFinite(Date.parse(value));

// Validate the server's binding and readiness, never reconstruct evidence from hashes.
// Null means an unusable response, not an empty successful clarification list.
export function normalizeClarifications(value, entryId) {
  if (!value || !guid(value.entryId) || value.entryId !== entryId
    || !(value.generationId === null || guid(value.generationId))
    || !text(value.lifecycleState, 100)
    || !['truncated', 'canAnswer', 'canResume', 'refreshRequired'].every(key => typeof value[key] === 'boolean')
    || !Array.isArray(value.current) || value.current.length > 20
    || !Array.isArray(value.history) || value.history.length > 100) return null;
  const ids = new Set();
  for (const [rows, historical] of [[value.current, false], [value.history, true]]) {
    for (const row of rows) {
      if (!row || !guid(row.id) || ids.has(row.id) || !guid(row.generationId)
        || !text(row.questionKey, 100) || !text(row.question, 2000) || !date(row.createdAtUtc)
        || typeof row.canAnswer !== 'boolean') return null;
      ids.add(row.id);
      if (row.answer === null) {
        if (row.answeredById !== null || row.answeredAtUtc !== null) return null;
      } else if (!text(row.answer, 4000) || !guid(row.answeredById) || !date(row.answeredAtUtc) || row.canAnswer) return null;
      if (historical) {
        if (row.binding !== 'History' || row.canAnswer) return null;
      } else {
        if (!value.generationId) return null;
        if (row.binding === 'CurrentQuestion') {
          if (row.generationId !== value.generationId) return null;
        } else if (row.binding !== 'CarriedAnswer' || row.answer === null || row.canAnswer) return null;
        if (row.canAnswer && (!value.canAnswer || value.lifecycleState !== 'AwaitingClarification')) return null;
      }
    }
  }
  if ((value.canAnswer || value.canResume) && (!value.generationId || value.lifecycleState !== 'AwaitingClarification')) return null;
  if (value.canResume && (!value.canAnswer || value.current.some(row => row.answer === null))) return null;
  return value;
}

export function createClarificationClient(api) {
  const root = id => `/api/entries/${encodeURIComponent(id)}/fact-clarifications`;
  return {
    read: (id, options = {}) => api(root(id), options),
    answer: (id, questionId, expectedGenerationId, answer, options = {}) => api(`${root(id)}/${encodeURIComponent(questionId)}/answer`, {
      ...options, method: 'POST', body: JSON.stringify({ expectedGenerationId, answer })
    }),
    resume: (id, expectedGenerationId, options = {}) => api(`${root(id)}/resume`, {
      ...options, method: 'POST', body: JSON.stringify({ expectedGenerationId })
    })
  };
}

export function clarificationActions(view, canWrite = false) {
  const state = view.state;
  const allowed = canWrite === true && !!state?.generationId && !state.refreshRequired
    && !view.loading && !view.busy && !view.conflict && state.lifecycleState === 'AwaitingClarification';
  return {
    answer: row => !!(allowed && state.canAnswer === true && row?.binding === 'CurrentQuestion' && row.canAnswer === true && row.answer === null),
    resume: !!(allowed && state.canResume === true)
  };
}

function clarificationError(error) {
  if (error?.status === 409) return 'Clarification or source proof changed. Drafts retained. Refresh and inspect before explicitly saving again.';
  if ([401, 403].includes(error?.status)) return 'Clarification access unavailable. Verify sign-in and write permission. Drafts retained.';
  return 'Clarification request failed or returned invalid evidence. Drafts retained. Refresh to confirm server state; no write will be replayed.';
}

// One entry-scoped session owns drafts and fences even abort-ignorant transports.
// Refresh and effects are GET-only; save and paid resume are separate user events.
export function createClarificationSession(client, { onChange = () => {}, onResume = () => {} } = {}) {
  let view = { entryId: null, state: null, drafts: {}, loading: false, busy: false, conflict: false, error: '', message: '' };
  let canWrite = false, request = null;
  const update = patch => { view = { ...view, ...patch }; onChange(view); };
  const cancel = () => { request?.abort(); request = null; };
  const begin = () => { cancel(); request = new AbortController(); return request; };
  const current = token => request === token && !token.signal.aborted;
  const apply = response => {
    const state = normalizeClarifications(response, view.entryId);
    if (!state) throw new Error('Invalid clarification response');
    update({ state });
  };
  const refresh = async (explicit = false) => {
    if (!view.entryId || view.busy) return;
    const token = begin(), entryId = view.entryId;
    update({ loading: true });
    try {
      const response = await client.read(entryId, { signal: token.signal });
      if (!current(token)) return;
      apply(response);
      if (explicit) update({ error: '', conflict: false, message: '' });
    } catch (error) {
      if (current(token)) update({ state: null, error: clarificationError(error), conflict: true });
    } finally { if (current(token)) update({ loading: false }); }
  };
  const write = async (kind, questionId) => {
    const actions = clarificationActions(view, canWrite);
    const row = view.state?.current.find(item => item.id === questionId);
    const answer = view.drafts[questionId];
    if (kind === 'save' ? !actions.answer(row) || !text(answer, 4000) : !actions.resume) return;
    const token = begin(), entryId = view.entryId, generationId = view.state.generationId;
    update({ busy: true, error: '', message: '' });
    try {
      if (kind === 'save') {
        const response = await client.answer(entryId, questionId, generationId, answer, { signal: token.signal });
        if (!current(token)) return;
        const confirmed = normalizeClarifications(response, entryId);
        if (!confirmed || confirmed.refreshRequired || confirmed.generationId !== generationId
          || confirmed.current.find(item => item.id === questionId)?.answer !== answer) throw new Error('Unconfirmed answer save');
        update({ state: confirmed });
        const drafts = { ...view.drafts }; delete drafts[questionId];
        update({ drafts, message: 'Answer saved. No model run was requested.' });
      } else {
        const facts = await client.resume(entryId, generationId, { signal: token.signal });
        if (!current(token)) return;
        if (!savedFactSource(facts, entryId) || !guid(facts.generationId) || facts.generationId === generationId
          || !text(facts.lifecycleState, 100)
          || !['canEdit', 'canGenerate', 'canRegenerate', 'refreshRequired'].every(key => typeof facts[key] === 'boolean')) throw new Error('Invalid resume response');
        // Resume returns FactStateDto, not clarification state. Both views GET again.
        onResume();
        update({ state: null, message: 'Extraction requested. Review refreshed fact state; nothing was accepted.' });
      }
    } catch (error) {
      if (!current(token)) return;
      update({ error: clarificationError(error), conflict: true });
      // A failed paid request might have completed: refresh facts too, never rerun it.
      if (kind === 'resume') onResume();
    } finally {
      if (current(token)) {
        update({ busy: false });
        if (kind === 'resume' || view.conflict) await refresh();
      }
    }
  };
  return {
    snapshot: () => view,
    select(entryId) {
      cancel();
      update({ entryId, state: null, drafts: {}, loading: false, busy: false, conflict: false, error: '', message: '' });
    },
    permissions(value) { canWrite = value === true; },
    draft(id, answer) { update({ drafts: { ...view.drafts, [id]: answer } }); },
    refresh, save: id => write('save', id), resume: () => write('resume'),
    dispose: cancel
  };
}
