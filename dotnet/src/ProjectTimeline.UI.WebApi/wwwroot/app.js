const root = document.getElementById('root');
let token = localStorage.getItem('projectTimelineToken');
const api = async (path, options = {}) => fetch(path, { ...options, headers: { ...(options.body instanceof FormData ? {} : { 'content-type': 'application/json' }), ...(token ? { Authorization: `Bearer ${token}` } : {}), ...(options.headers || {}) } });
const el = (tag, attrs = {}, children = []) => { const node = document.createElement(tag); Object.entries(attrs).forEach(([key, value]) => { if (key === 'text') node.textContent = value; else if (key === 'value') node.value = value; else if (key === 'onclick') node.onclick = value; else node.setAttribute(key, value); }); children.forEach(child => node.append(child)); return node; };
const message = text => el('p', { class: 'message', text });
const jsonForm = (fields, submitText, onSubmit, values = {}) => { const form = el('form'); fields.forEach(field => { const input = el(field.type === 'textarea' ? 'textarea' : 'input', { id: field.id, placeholder: field.label, ...(field.type === 'textarea' ? {} : { type: field.type || 'text' }), ...(field.required ? { required: 'required' } : {}) }); input.value = values[field.id] || ''; form.append(input); }); form.append(el('button', { type: 'submit', text: submitText })); form.onsubmit = async event => { event.preventDefault(); await onSubmit(Object.fromEntries(fields.map(field => [field.id, form.querySelector(`#${field.id}`).value]))); }; return form; };
const action = (text, handler) => el('button', { type: 'button', text, onclick: handler });
const errorIfNeeded = async response => { if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.error || `Request failed (${response.status})`); } return response; };

function login() {
  root.replaceChildren(el('section', { class: 'card' }, [el('h1', { text: 'Sign in' }), jsonForm([{ id: 'email', label: 'Email', type: 'email', required: true }, { id: 'password', label: 'Password', type: 'password', required: true }], 'Sign in', async value => { try { const response = await errorIfNeeded(await api('/api/auth/login', { method: 'POST', body: JSON.stringify(value) })); token = (await response.json()).token; localStorage.setItem('projectTimelineToken', token); await home(); } catch (error) { root.append(message(error.message)); } }), jsonForm([{ id: 'email', label: 'Email', type: 'email', required: true }], 'Request password recovery', async value => { await api('/api/auth/recovery', { method: 'POST', body: JSON.stringify(value) }); root.append(message('If the account exists, recovery instructions will be available through the configured local recovery channel.')); })]));
}

async function home() {
  const response = await api('/api/projects');
  if (response.status === 401) { token = null; localStorage.removeItem('projectTimelineToken'); return login(); }
  const projects = response.ok ? await response.json() : [];
  const profileResponse = await api('/api/me');
  const me = profileResponse.ok ? await profileResponse.json() : {};
  const page = el('section', { class: 'card' }, [el('h1', { text: 'Project Timeline' }), action('Sign out', () => { token = null; localStorage.removeItem('projectTimelineToken'); login(); }), el('h2', { text: 'Profile' })]);
  page.append(jsonForm([{ id: 'displayName', label: 'Display name', required: true }, { id: 'password', label: 'New password', type: 'password' }], 'Save profile', async value => { await errorIfNeeded(await api('/api/me', { method: 'PUT', body: JSON.stringify(value) })); page.append(message('Profile saved.')); }, me));
  if ((me.roles || []).includes('Administrator')) page.append(await adminUsers());
  page.append(el('h2', { text: 'Projects' }), jsonForm([{ id: 'code', label: 'Project code', required: true }, { id: 'name', label: 'Project name', required: true }, { id: 'description', label: 'Description' }], 'Create project', async value => { await errorIfNeeded(await api('/api/projects', { method: 'POST', body: JSON.stringify(value) })); await home(); }));
  for (const project of projects) page.append(await projectCard(project));
  root.replaceChildren(page);
}

async function adminUsers() {
  const section = el('section', {}, [el('h2', { text: 'Manage admin users' })]);
  const list = el('div');
  const refresh = async () => { list.replaceChildren(); const response = await api('/api/admin/users'); if (!response.ok) return; for (const user of await response.json()) { const row = el('article', { class: 'card' }, [el('strong', { text: `${user.email} (${user.roles.join(', ') || 'User'})` })]); row.append(jsonForm([{ id: 'displayName', label: 'Display name', required: true }, { id: 'password', label: 'Reset password', type: 'password' }, { id: 'roles', label: 'Roles (comma separated)' }], 'Update user', async value => { await errorIfNeeded(await api(`/api/admin/users/${user.id}`, { method: 'PUT', body: JSON.stringify({ displayName: value.displayName, password: value.password || null, isActive: true, roles: value.roles.split(',').map(x => x.trim()).filter(Boolean) }) })); await refresh(); }, { displayName: user.displayName, roles: user.roles.join(', ') })); row.append(action('Deactivate user', async () => { await errorIfNeeded(await api(`/api/admin/users/${user.id}`, { method: 'DELETE' })); await refresh(); })); list.append(row); } };
  section.append(jsonForm([{ id: 'email', label: 'Email', type: 'email', required: true }, { id: 'displayName', label: 'Display name', required: true }, { id: 'password', label: 'Temporary password', type: 'password', required: true }, { id: 'roles', label: 'Roles (comma separated)' }], 'Create user', async value => { await errorIfNeeded(await api('/api/admin/users', { method: 'POST', body: JSON.stringify({ ...value, roles: value.roles.split(',').map(x => x.trim()).filter(Boolean), isActive: true }) })); await refresh(); })); section.append(list); await refresh(); return section;
}

async function projectCard(project) {
  const card = el('article', { class: 'card' }, [el('h3', { text: `${project.code} — ${project.name}` }), el('p', { text: project.description || 'No description' })]);
  const participants = el('section', {}, [el('h4', { text: 'Manage participants' })]);
  const participantList = el('div');
  const refreshParticipants = async () => { participantList.replaceChildren(); const response = await api(`/api/projects/${project.id}/participants`); if (response.ok) for (const participant of await response.json()) { const row = el('div', { text: `${participant.email} — ${participant.projectRoleName} ` }); row.append(action('Change role', async () => { const role = prompt('Project role (Creator, Reviewer, Contributor)', participant.projectRoleName); if (role) { await api(`/api/projects/${project.id}/participants/${participant.id}`, { method: 'PUT', body: JSON.stringify({ projectRoleName: role }) }); await refreshParticipants(); } }), action('Remove', async () => { await api(`/api/projects/${project.id}/participants/${participant.id}`, { method: 'DELETE' }); await refreshParticipants(); })); participantList.append(row); } };
  participants.append(jsonForm([{ id: 'email', label: 'User email', type: 'email', required: true }, { id: 'projectRoleName', label: 'Role (Creator, Reviewer, Contributor)', required: true }], 'Add participant', async value => { await errorIfNeeded(await api(`/api/projects/${project.id}/participants`, { method: 'POST', body: JSON.stringify(value) })); await refreshParticipants(); })); participants.append(participantList); await refreshParticipants(); card.append(participants);
  const timelines = el('section', {}, [el('h4', { text: 'Timeline workspace' })]);
  timelines.append(jsonForm([{ id: 'code', label: 'Timeline code', required: true }, { id: 'name', label: 'Timeline name', required: true }], 'Create timeline', async value => { await errorIfNeeded(await api(`/api/projects/${project.id}/timelines`, { method: 'POST', body: JSON.stringify(value) })); await loadTimelines(); }));
  const timelineList = el('div'); const loadTimelines = async () => { timelineList.replaceChildren(); const response = await api(`/api/projects/${project.id}/timelines`); if (response.ok) for (const timeline of await response.json()) timelineList.append(await timelineWorkspace(timeline)); }; await loadTimelines(); timelines.append(timelineList); card.append(timelines); return card;
}

async function timelineWorkspace(timeline) {
  const section = el('article', { class: 'card' }, [el('h5', { text: `${timeline.code} — ${timeline.name}` })]);
  const entries = el('div'); const loadEntries = async () => { entries.replaceChildren(); const response = await api(`/api/timelines/${timeline.id}/entries`); if (response.ok) for (const entry of await response.json()) entries.append(await entryEditor(entry)); };
  section.append(jsonForm([{ id: 'title', label: 'Entry title', required: true }, { id: 'content', label: 'Entry content', type: 'textarea', required: true }], 'Add entry', async value => { await errorIfNeeded(await api(`/api/timelines/${timeline.id}/entries`, { method: 'POST', body: JSON.stringify(value) })); await loadEntries(); })); section.append(entries); await loadEntries(); return section;
}

async function entryEditor(entry) {
  const card = el('article', { class: 'card' }, [el('h6', { text: `Entry ${entry.entryNumber}` })]); const fields = [{ id: 'title', label: 'Title', required: true }, { id: 'content', label: 'Content', type: 'textarea', required: true }, { id: 'status', label: 'Status', required: true }]; card.append(jsonForm(fields, 'Save entry', async value => { await errorIfNeeded(await api(`/api/entries/${entry.id}`, { method: 'PUT', body: JSON.stringify(value) })); card.append(message('Entry saved.')); }, entry));
  const file = el('input', { type: 'file', id: `artifact-${entry.id}`, 'aria-label': 'Upload artifact' }); const artifactList = el('ul'); const loadArtifacts = async () => { artifactList.replaceChildren(); const response = await api(`/api/entries/${entry.id}/artifacts`); if (response.ok) for (const artifact of await response.json()) { const link = el('a', { href: `/api/artifacts/${artifact.id}`, text: `${artifact.fileName} (${artifact.length} bytes)` }); link.onclick = async event => { event.preventDefault(); const download = await api(`/api/artifacts/${artifact.id}`); const blob = await download.blob(); const url = URL.createObjectURL(blob); const anchor = el('a', { href: url, download: artifact.fileName }); anchor.click(); URL.revokeObjectURL(url); }; artifactList.append(el('li', {}, [link])); } };
  file.onchange = async () => { if (!file.files.length) return; const body = new FormData(); body.append('file', file.files[0]); await errorIfNeeded(await api(`/api/entries/${entry.id}/artifacts`, { method: 'POST', body })); file.value = ''; await loadArtifacts(); }; card.append(el('label', { for: file.id, text: 'Artifact upload' }), file, artifactList); await loadArtifacts(); return card;
}

token ? home() : login();
