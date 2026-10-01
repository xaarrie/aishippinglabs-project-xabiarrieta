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
    throw new Error(body.error || `Request failed (${response.status})`);
  }
  return response.status === 204 ? null : response.json();
}
