let token = localStorage.getItem("projectTimelineToken");

export function clearToken() { token = null; localStorage.removeItem("projectTimelineToken"); }
export function setToken(value) { token = value; localStorage.setItem("projectTimelineToken", value); }

export async function download(path, fileName) {
  const response = await fetch(path, { headers: token ? { Authorization: `Bearer ${token}` } : {} });
  if (!response.ok) throw new Error(`Download failed (${response.status})`);
  const link = document.createElement("a"); link.href = URL.createObjectURL(await response.blob()); link.download = fileName; link.click(); URL.revokeObjectURL(link.href);
}

export async function api(path, options = {}) {
  const headers = { ...(options.body instanceof FormData ? {} : { "content-type": "application/json" }), ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(options.headers || {}) };
  const response = await fetch(path, { ...options, headers });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    // Never copy arbitrary error/provider text into UI messages or retain raw
    // response objects. Known entry-review codes have server-owned diagnostics.
    const code = body?.code;
    const knownFailure = response.status === 502 && ['entry_review_failed', 'entry_review_audit_failed'].includes(code);
    const changed = response.status === 409 && code === 'entry_review_source_changed';
    const error = new Error(`Request failed (${response.status})`);
    error.status = response.status;
    if (knownFailure || changed) {
      const problem = { code };
      const stages = ['initialization', 'agent_invocation', 'result_validation', 'audit_initialization', 'audit_invocation', 'audit_result_validation', 'unknown'];
      const categories = ['initialization_failed', 'invalid_result', 'provider_authentication', 'provider_rate_limited', 'provider_timeout', 'provider_unavailable', 'provider_http_error', 'provider_transport', 'agent_canceled', 'unknown'];
      if (knownFailure) {
        if (stages.includes(body.stage)) problem.stage = body.stage;
        if (categories.includes(body.category)) problem.category = body.category;
        if (typeof body.traceId === 'string' && /^[A-Za-z0-9][A-Za-z0-9:._-]{0,127}$/.test(body.traceId)) problem.traceId = body.traceId;
      }
      error.problem = problem;
      // Reconstruct safe details rather than trusting arbitrary title/detail/error
      // strings, even when a response claims a recognized failure code.
      error.message = changed ? 'The source changed. Review the current source before auditing again.'
        : `${code === 'entry_review_failed' ? 'Entry review' : 'Entry audit'} could not be completed.${problem.stage ? ` Stage: ${problem.stage}.` : ''}${problem.category ? ` Category: ${problem.category}.` : ''}${problem.traceId ? ` Diagnostic reference: ${problem.traceId}.` : ''} Contact an administrator if this continues.`;
    }
    throw error;
  }
  return response.status === 204 ? null : response.json();
}
